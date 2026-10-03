using System.Data.Common;
using System.Text;
using System.Text.Json;
using Ark.Core.Errors;
using Ark.Core.Io;
using Ark.Core.Metadata;
using Ark.Core.Responses;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

public sealed record ImportResult(string Table, long Inserted, bool TableCreated, IReadOnlyList<string> Warnings);

/// <summary>
/// 导入 CSV / JSON 到目标表：
/// - 目标表存在 → 按列名映射插入；
/// - 目标表不存在 → 采样推断列类型建表（无主键），再插入。
/// </summary>
public static class Importer
{
    public static async Task<ImportResult> ImportAsync(
        Stream input, string format, string tableName,
        IDbProvider target, TableRef table,
        int batchSize, CancellationToken ct = default)
    {
        Identifier.EnsureValid(tableName, "表名");
        var warnings = new List<string>();

        // 1) 解析为 (列名[], 每行的 JsonElement 值字典)
        List<string> columns;
        var rows = new List<IReadOnlyDictionary<string, JsonElement?>>();
        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(input, Encoding.UTF8);
            var raw = CsvParser.Parse(reader);
            if (raw.Count == 0) throw ArkException.Validation("CSV 文件为空");
            columns = raw[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"col{i}" : h.Trim()).ToList();
            foreach (var line in raw.Skip(1))
            {
                var dict = new Dictionary<string, JsonElement?>();
                for (var i = 0; i < columns.Count; i++)
                {
                    var v = i < line.Count ? line[i] : null;
                    dict[columns[i]] = v is null ? null : JsonSerializer.SerializeToElement(v);
                }
                rows.Add(dict);
            }
        }
        else if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = await JsonDocument.ParseAsync(input, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw ArkException.Validation("JSON 导入要求顶层数组");
            columns = [];
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    throw ArkException.Validation("JSON 数组元素必须是对象");
                var dict = new Dictionary<string, JsonElement?>();
                foreach (var p in el.EnumerateObject())
                {
                    if (!columns.Contains(p.Name)) columns.Add(p.Name);
                    dict[p.Name] = p.Value.Clone();
                }
                rows.Add(dict);
            }
        }
        else
        {
            throw ArkException.Validation($"不支持的导入格式: {format}（支持 csv/json）");
        }

        if (columns.Count == 0) throw ArkException.Validation("未识别到任何列");
        foreach (var c in columns) Identifier.EnsureValid(c, "列名");

        // 2) 目标表不存在 → 推断类型建表
        var tableCreated = false;
        CanonicalTable meta;
        try
        {
            meta = await target.GetTableAsync(table.Database, table.Schema, table.Table, ct);
        }
        catch (ArkException ex) when (ex.Code == ArkErrorCodes.TableNotFound)
        {
            var inferred = InferTable(tableName, columns, rows.Cast<IReadOnlyDictionary<string, JsonElement?>>().ToList(), warnings);
            var ddlWarnings = new List<string>();
            var stmts = target.Ddl.CreateTable(inferred, ddlWarnings);
            warnings.AddRange(ddlWarnings);
            var (executed, errors) = await target.ExecuteDdlAsync(table.Database, stmts, ct);
            if (errors.Count > 0)
                throw new ArkException(ArkErrorCodes.DdlFailed, $"建表失败: {errors[0]}");
            tableCreated = true;
            meta = inferred;
        }

        // 3) 分批插入（跳过目标表中不存在于文件的列；文件中多余列警告）
        var targetCols = meta.Columns.Where(c => !c.IsGenerated && columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var missing = columns.Where(c => !meta.Columns.Any(m => string.Equals(m.Name, c, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
            warnings.Add($"文件中的列 {string.Join(", ", missing)} 在目标表中不存在，已跳过");

        long inserted = 0;
        await using var conn = await target.OpenConnectionAsync(table.Database, ct);
        var effectiveBatch = Math.Max(1, Math.Min(batchSize <= 0 ? 500 : batchSize, 900 / Math.Max(1, targetCols.Count)));

        foreach (var chunk in Chunk(rows, effectiveBatch))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var colSql = string.Join(", ", targetCols.Select(c => target.Ddl.Quote(c.Name)));
                var groups = new List<string>();
                var pi = 0;
                foreach (var row in chunk)
                {
                    var ps = new List<string>();
                    foreach (var col in targetCols)
                    {
                        row.TryGetValue(col.Name, out var el);
                        var p = cmd.CreateParameter();
                        p.ParameterName = $"@p{pi++}";
                        p.Value = CellValue.ToParam(el is null ? null : CellValue.FromJson(el.Value, col.Type), target.Dialect);
                        cmd.Parameters.Add(p);
                        ps.Add(target.Dialect == ArkDialect.PostgreSQL && Ark.Core.Typing.TypeMapper.PgCastFor(col) is { } cast
                            ? p.ParameterName + "::" + cast
                            : p.ParameterName);
                    }
                    groups.Add("(" + string.Join(", ", ps) + ")");
                }
                cmd.CommandText =
                    $"INSERT INTO {target.Ddl.TableName(table)} ({colSql}) VALUES {string.Join(", ", groups)}";
                await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                inserted += chunk.Count;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        return new ImportResult(table.Table, inserted, tableCreated, warnings);
    }

    /// <summary>按采样值推断每列的规范类型，生成建表模型。</summary>
    private static CanonicalTable InferTable(
        string tableName, List<string> columns,
        List<IReadOnlyDictionary<string, JsonElement?>> rows, List<string> warnings)
    {
        var cols = new List<CanonicalColumn>();
        foreach (var name in columns)
        {
            var samples = rows.Select(r => r.TryGetValue(name, out var v) ? v : null)
                .Where(v => v is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
                .Take(200)
                .Select(v => v!.Value)
                .ToList();
            var id = InferTypeId(samples);
            if (id == CanonicalTypeId.Text && samples.Any(s => s.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
                warnings.Add($"列 {name}: 含对象/数组值，按文本存储");
            cols.Add(new CanonicalColumn { Name = name, Type = CanonicalType.Of(id), Nullable = true });
        }
        return new CanonicalTable
        {
            Name = tableName,
            Columns = cols,
            PrimaryKeyColumns = [],
            Indexes = [],
            ForeignKeys = [],
        };
    }

    private static CanonicalTypeId InferTypeId(List<JsonElement> samples)
    {
        if (samples.Count == 0) return CanonicalTypeId.Text;
        var kinds = samples.Select(s => s.ValueKind).ToHashSet();
        if (kinds.All(k => k is JsonValueKind.True or JsonValueKind.False)) return CanonicalTypeId.Boolean;
        if (kinds.All(k => k == JsonValueKind.Number))
        {
            var allInt = samples.All(s => s.TryGetInt64(out _));
            return allInt ? CanonicalTypeId.Int64 : CanonicalTypeId.Double;
        }
        if (kinds.All(k => k == JsonValueKind.String))
        {
            var allDate = samples.All(s => DateTime.TryParse(s.GetString(), out _));
            if (allDate) return CanonicalTypeId.DateTime;
        }
        return CanonicalTypeId.Text;
    }

    private static List<List<T>> Chunk<T>(IEnumerable<T> src, int size)
    {
        var list = new List<List<T>>();
        var cur = new List<T>();
        foreach (var item in src)
        {
            cur.Add(item);
            if (cur.Count >= size) { list.Add(cur); cur = []; }
        }
        if (cur.Count > 0) list.Add(cur);
        return list;
    }
}
