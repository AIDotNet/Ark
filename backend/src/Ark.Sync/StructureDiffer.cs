using Ark.Core.Metadata;
using Ark.Core.Sync;

namespace Ark.Sync;

/// <summary>比较目标端现状与转换后的规范模型，产出结构差异。</summary>
public static class StructureDiffer
{
    public sealed record DetailedResult(
        TableDiff Diff,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<RenameCandidate> RenameCandidates);

    public static TableDiff Diff(CanonicalTable before, CanonicalTable after, TableRef target) =>
        DiffDetailed(before, after, target).Diff;

    /// <summary>
    /// 详细 Diff：
    /// - 索引/外键先按名对齐，再比对"定义指纹"（列集/唯一性/方法/谓词、引用与动作）——同名异定义 → Drop+Create；
    /// - 列级改名启发式：删列+加列 位置相邻且类型一致 → RenameCandidate（hint，由用户确认）。
    /// </summary>
    public static DetailedResult DiffDetailed(CanonicalTable before, CanonicalTable after, TableRef target)
    {
        var warnings = new List<string>();
        var renames = new List<RenameCandidate>();

        var addedColumns = new List<CanonicalColumn>();
        var alteredColumns = new List<ColumnChange>();
        var droppedColumns = new List<CanonicalColumn>();

        var beforeByName = before.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var afterByName = after.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var a in afterByName)
        {
            if (!beforeByName.TryGetValue(a.Key, out var b)) { addedColumns.Add(a.Value); continue; }
            if (ColumnEquals(b, a.Value)) continue;
            alteredColumns.Add(new ColumnChange(b, a.Value));
        }
        foreach (var b in beforeByName)
            if (!afterByName.ContainsKey(b.Key)) droppedColumns.Add(b.Value);

        DetectRenames(before, after, droppedColumns, addedColumns, renames, warnings);

        // 索引：名字对齐 + 定义指纹
        var beforeIdx = before.Indexes.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var afterIdx = after.Indexes.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var addedIndexes = new List<CanonicalIndex>();
        var droppedIndexes = new List<CanonicalIndex>();
        foreach (var kv in afterIdx)
        {
            if (!beforeIdx.TryGetValue(kv.Key, out var beforeIdxDef))
            {
                addedIndexes.Add(kv.Value);
                continue;
            }
            if (IndexFingerprint(beforeIdxDef) == IndexFingerprint(kv.Value)) continue;
            // 同名异定义 → 重建
            droppedIndexes.Add(beforeIdxDef);
            addedIndexes.Add(kv.Value);
            warnings.Add($"索引 {kv.Key} 定义变化（{Describe(beforeIdxDef)} → {Describe(kv.Value)}），将重建");
        }
        foreach (var kv in beforeIdx)
            if (!afterIdx.ContainsKey(kv.Key)) droppedIndexes.Add(kv.Value);

        // 外键：名字对齐 + 定义指纹
        var beforeFk = before.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var afterFk = after.ForeignKeys.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var addedFks = new List<CanonicalForeignKey>();
        var droppedFks = new List<CanonicalForeignKey>();
        foreach (var kv in afterFk)
        {
            if (!beforeFk.TryGetValue(kv.Key, out var beforeFkDef))
            {
                addedFks.Add(kv.Value);
                continue;
            }
            if (FkFingerprint(beforeFkDef) == FkFingerprint(kv.Value)) continue;
            droppedFks.Add(beforeFkDef);
            addedFks.Add(kv.Value);
            warnings.Add($"外键 {kv.Key} 定义变化（{DescribeFk(beforeFkDef)} → {DescribeFk(kv.Value)}），将重建");
        }
        foreach (var kv in beforeFk)
            if (!afterFk.ContainsKey(kv.Key)) droppedFks.Add(kv.Value);

        var pkChanged = !before.PrimaryKeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(after.PrimaryKeyColumns);

        var diff = new TableDiff
        {
            Target = target,
            After = after,
            AddedColumns = addedColumns,
            AlteredColumns = alteredColumns,
            DroppedColumns = droppedColumns,
            AddedIndexes = addedIndexes,
            DroppedIndexes = droppedIndexes,
            AddedForeignKeys = addedFks,
            DroppedForeignKeys = droppedFks,
            PrimaryKeyChanged = pkChanged,
        };
        return new DetailedResult(diff, warnings, renames);
    }

    // ------------------------------------------------------------------

    /// <summary>索引定义指纹：列序 + 唯一性 + 方法 + 部分索引谓词。</summary>
    public static string IndexFingerprint(CanonicalIndex i)
    {
        var cols = string.Join(",", i.Columns.Select(c => c.Trim('"', '`', ' ').ToLowerInvariant()));
        return $"idx|{cols}|{(i.IsUnique ? 1 : 0)}|{i.Method?.ToLowerInvariant() ?? ""}|{(i.WhereClause ?? "").ToLowerInvariant()}";
    }

    /// <summary>外键定义指纹：列序 + 引用表/列 + ON DELETE/UPDATE（空动作归一为 NO ACTION）。</summary>
    public static string FkFingerprint(CanonicalForeignKey f)
    {
        static string Norm(string? act) =>
            string.IsNullOrWhiteSpace(act) || act.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase)
                ? "no-action"
                : act.ToLowerInvariant();
        return $"fk|{string.Join(",", f.Columns.Select(c => c.ToLowerInvariant()))}" +
               $"|{(f.ReferencedTable ?? "").Trim('"', '`').ToLowerInvariant()}" +
               $"|{string.Join(",", f.ReferencedColumns.Select(c => c.ToLowerInvariant()))}" +
               $"|{Norm(f.OnDelete)}|{Norm(f.OnUpdate)}";
    }

    private static void DetectRenames(
        CanonicalTable before, CanonicalTable after,
        IReadOnlyList<CanonicalColumn> dropped, IReadOnlyList<CanonicalColumn> added,
        List<RenameCandidate> renames, List<string> warnings)
    {
        if (dropped.Count == 0 || added.Count == 0) return;
        if (dropped.Count > 3 || added.Count > 3) return; // 大规模删加列（表重建）不做启发式

        var candidates = new List<(CanonicalColumn Drop, CanonicalColumn Add, int Dist)>();
        foreach (var d in dropped)
        {
            var dropPos = before.Columns.ToList().FindIndex(c => c.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var a in added)
            {
                if (d.Type.Id != a.Type.Id) continue;
                if (d.Nullable != a.Nullable) continue;
                var addPos = after.Columns.ToList().FindIndex(c => c.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
                if (Math.Abs(dropPos - addPos) > 1) continue;
                candidates.Add((d, a, Math.Abs(dropPos - addPos)));
            }
        }
        // 一对一唯一配对才提示（避免误报）
        var usedD = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (d, a, dist) in candidates.OrderBy(c => c.Dist))
        {
            if (usedD.Contains(d.Name) || usedA.Contains(a.Name)) continue;
            usedD.Add(d.Name);
            usedA.Add(a.Name);
            renames.Add(new RenameCandidate(d.Name, a.Name, dist == 0 ? "high" : "medium"));
            warnings.Add($"检测到疑似列改名 {d.Name} → {a.Name}（位置相邻、类型一致）。" +
                         "若确认为改名，请在表选项的列映射中加入该映射以保留数据；否则按删列+加列执行（数据丢失）");
        }
    }

    private static string Describe(CanonicalIndex i) =>
        $"{(i.IsUnique ? "UNIQUE " : "")}({string.Join(", ", i.Columns)}){i.Method}{(i.WhereClause is null ? "" : $" WHERE {i.WhereClause}")}";

    private static string DescribeFk(CanonicalForeignKey f) =>
        $"({string.Join(", ", f.Columns)}) → {f.ReferencedTable}({string.Join(", ", f.ReferencedColumns)})" +
        $" ON DELETE {f.OnDelete ?? "NO ACTION"} ON UPDATE {f.OnUpdate ?? "NO ACTION"}";

    public static bool ColumnEquals(CanonicalColumn a, CanonicalColumn b) =>
        Equals(a.Type, b.Type)
        && a.Nullable == b.Nullable
        && string.Equals(a.DefaultValueSql, b.DefaultValueSql, StringComparison.OrdinalIgnoreCase)
        && a.DefaultKind == b.DefaultKind
        && a.IsAutoIncrement == b.IsAutoIncrement
        && a.IsGenerated == b.IsGenerated;
}
