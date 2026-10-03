using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

/// <summary>
/// 生成执行计划快照（ExecutionPlan）：结构动作（含破坏性标记与 SQL 全文）、数据计划、扩展依赖、转换警告、
/// 改名候选、视图 DDL。执行阶段直接消费快照，不再重新规划（消除预览/执行 TOCTOU）。
/// </summary>
public sealed class SyncPlanner
{
    public async Task<ExecutionPlan> BuildPlanAsync(
        IDbProvider source, IDbProvider target, SyncPlanRequest req, CancellationToken ct = default)
    {
        if (req.SourceConnectionId == req.TargetConnectionId &&
            string.Equals(req.SourceDatabase, req.TargetDatabase, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(req.TargetSchema))
            throw ArkException.Validation("源库与目标库相同，无需同步（如需同库跨 schema 同步请指定目标 Schema）");

        var plans = new List<TableExecution>();
        // 表间外键依赖（用于建表拓扑排序：被引用的表先建）
        var tableDeps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sel in req.Tables)
        {
            Identifier.EnsureValid(sel.Table, "表名");
            if (!string.IsNullOrEmpty(sel.Schema)) Identifier.EnsureValid(sel.Schema, "schema 名");

            var srcRef = new TableRef(req.SourceDatabase, sel.Schema, sel.Table);
            var tgtSchema = ResolveTargetSchema(target, source.Dialect, sel.Schema, req.TargetSchema);
            var tgtRef = new TableRef(req.TargetDatabase, tgtSchema, sel.Table);

            var srcMeta = await source.GetTableAsync(req.SourceDatabase, sel.Schema, sel.Table, ct);
            var (converted, issues) = TableConverter.Convert(srcMeta, source.Dialect, target.Dialect);
            var issueList = issues.ToList();

            // 列映射 = 目标端重命名：直接改写 converted 模型（新建表按映射建列、Diff/复制按映射对齐）
            if (sel.ColumnMap is { Count: > 0 })
            {
                var renames = sel.ColumnMap
                    .Where(kv => !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
                var convertedNames = converted.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (from, to) in renames)
                {
                    if (!convertedNames.Contains(from))
                        issueList.Add(new ConversionIssue("error", $"列映射的源列 {from} 不存在，该映射已忽略"));
                    else if (convertedNames.Contains(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                        issueList.Add(new ConversionIssue("error", $"列映射冲突：目标列名 {to} 已存在"));
                }
                converted = converted with
                {
                    Columns = converted.Columns.Select(c =>
                        renames.TryGetValue(c.Name, out var to) ? c with { Name = to } : c).ToList(),
                    PrimaryKeyColumns = converted.PrimaryKeyColumns
                        .Select(pk => renames.TryGetValue(pk, out var to) ? to : pk).ToList(),
                    Indexes = converted.Indexes.Select(ix => ix with
                    {
                        Columns = ix.Columns.Select(c => renames.TryGetValue(c, out var to) ? to : c).ToList(),
                    }).ToList(),
                    ForeignKeys = converted.ForeignKeys.Select(fk => fk with
                    {
                        Columns = fk.Columns.Select(c => renames.TryGetValue(c, out var to) ? to : c).ToList(),
                    }).ToList(),
                };
            }

            // WHERE 片段试编译（语法错误提前暴露）
            if (!string.IsNullOrWhiteSpace(sel.Where))
                await ValidateWhereAsync(source, srcRef, sel.Where!, issueList, ct);

            var actions = new List<DdlAction>();
            var postCopy = new List<DdlAction>();
            var renameHints = new List<RenameCandidate>();
            CanonicalTable? existing = null;
            try
            {
                existing = await target.GetTableAsync(req.TargetDatabase, tgtSchema, sel.Table, ct);
            }
            catch (ArkException ex) when (ex.Code == ArkErrorCodes.TableNotFound)
            {
            }

            if (srcMeta.IsView)
            {
                if (source.Dialect == target.Dialect)
                {
                    await AppendViewActions(source, target, req, sel, tgtSchema, existing, actions, issueList, ct);
                }
                else
                {
                    issueList.Add(new ConversionIssue("warning",
                        $"视图 {sel.Table}: 跨方言（{source.Dialect} → {target.Dialect}）无法自动翻译视图 DDL，已跳过；可在目标端人工创建"));
                }
            }
            else if (existing is null or { IsView: true })
            {
                if (existing is { IsView: true })
                {
                    foreach (var sql in target.Ddl.DropTable(tgtRef))
                        actions.Add(new DdlAction
                        {
                            Id = ActionId(sel.Table, "DropView", sql),
                            Kind = "DropView",
                            Summary = $"删除同名视图 {sel.Table}（将被表替换）",
                            Sql = sql,
                            IsDestructive = true,
                        });
                    existing = null;
                }

                var warnings = new List<string>();
                foreach (var sql in target.Ddl.CreateTable(tgtRef, converted, warnings))
                    actions.Add(new DdlAction
                    {
                        Id = ActionId(sel.Table, "CreateTable", sql),
                        Kind = "CreateTable",
                        Summary = $"创建表 {sel.Table}",
                        Sql = sql,
                        Warnings = warnings.Distinct().ToList(),
                    });
                var defer = req.DeferIndexes && req.Mode == SyncMode.StructureAndData;
                foreach (var idx in converted.Indexes)
                foreach (var sql in target.Ddl.CreateIndex(idx, tgtRef, converted))
                    (defer ? postCopy : actions).Add(new DdlAction
                    {
                        Id = ActionId(sel.Table, "CreateIndex", sql),
                        Kind = "CreateIndex",
                        Summary = $"创建索引 {idx.Name}（{string.Join(", ", idx.Columns)}）" + (defer ? "（数据复制后创建）" : ""),
                        Sql = sql,
                    });
                foreach (var fk in converted.ForeignKeys)
                foreach (var sql in target.Ddl.CreateForeignKey(fk, tgtRef))
                    (defer ? postCopy : actions).Add(new DdlAction
                    {
                        Id = ActionId(sel.Table, "AddForeignKey", sql),
                        Kind = "AddForeignKey",
                        Summary = $"添加外键 {fk.Name} → {fk.ReferencedTable}" + (defer ? "（数据复制后创建）" : ""),
                        Sql = sql,
                    });
            }
            else
            {
                var detail = StructureDiffer.DiffDetailed(existing, converted, tgtRef);
                var baseDiff = detail.Diff;
                renameHints.AddRange(detail.RenameCandidates);
                foreach (var w in detail.Warnings) issueList.Add(new ConversionIssue("warning", w));
                if (baseDiff.PrimaryKeyChanged)
                    issueList.Add(new ConversionIssue("warning", "主键发生变更，需人工处理（涉及约束重建）"));
                foreach (var r in renameHints)
                    issueList.Add(new ConversionIssue("hint",
                        $"疑似列改名 {r.DropColumn} → {r.AddColumn}（置信度 {r.Confidence}）。确认为改名请在列映射中加入映射以保留数据"));

                actions.AddRange(DiffToActions(target, baseDiff, tgtRef, sel.Table));
            }

            // 数据计划
            DataPlan? data = null;
            if (req.Mode == SyncMode.StructureAndData && !srcMeta.IsView)
            {
                var est = await source.EstimateRowsAsync(srcRef, ct);
                var warns = new List<string>();
                var hasPk = srcMeta.PrimaryKeyColumns.Count > 0;
                var method = sel.MethodOverride ?? req.DataMethod;
                var effective = method;
                if (!hasPk && method == DataSyncMethod.RowDiff)
                {
                    warns.Add("该表没有主键，行级 Diff 不可用，执行时将回退为全量复制");
                    effective = DataSyncMethod.FullCopy;
                }
                if (!hasPk && req.ConflictMode == ConflictMode.SkipExisting)
                    warns.Add("该表没有主键，无法识别“已存在”，冲突语义回退为直接插入");
                if (converted.Columns.Any(c => c.IsGenerated) && existing is null)
                    warns.Add("目标表含生成列，导入时将自动跳过生成列");
                data = new DataPlan
                {
                    Method = method,
                    EffectiveMethod = effective,
                    HasPrimaryKey = hasPk,
                    EstimatedRows = est,
                    ConflictMode = req.ConflictMode,
                    Warnings = warns,
                };
            }

            // 插件函数依赖检查：从（已翻译的）默认值表达式里提取函数名
            var functions = converted.Columns
                .Where(c => c.DefaultValueSql is { } d && IsFunctionCall(d))
                .Select(c => FnName(c.DefaultValueSql!))
                .Where(f => f is not null).Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var extensions = functions.Count > 0
                ? await target.CheckFunctionsAsync(req.TargetDatabase, functions, ct)
                : [];

            // 脱敏规则命中检查 + 唯一键冲突警告
            if (sel.MaskRules is { Count: > 0 })
            {
                foreach (var col in converted.Columns)
                {
                    var rule = Masking.Match(sel.MaskRules, col.Name);
                    if (rule is null) continue;
                    if (converted.PrimaryKeyColumns.Contains(col.Name, StringComparer.OrdinalIgnoreCase)
                        && rule.Kind is MaskRuleKind.RandomInt or MaskRuleKind.RandomLetter or MaskRuleKind.RandomDate)
                        issueList.Add(new ConversionIssue("warning",
                            $"列 {col.Name} 是主键且应用了随机脱敏（{rule.Kind}），可能导致主键冲突"));
                    issueList.Add(new ConversionIssue("hint", $"列 {col.Name} 将应用脱敏规则 {rule.Kind}"));
                }
            }

            var selectedNames = req.Tables.Select(t => t.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
            tableDeps[sel.Table] = converted.ForeignKeys
                .Select(f => f.ReferencedTable)
                .Where(r => selectedNames.Contains(r) && !string.Equals(r, sel.Table, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // PG 非 public schema：生成 DDL 中的裸表名（如 FK REFERENCES "users"）需 search_path 解析
            if (target.Dialect == ArkDialect.PostgreSQL
                && !string.IsNullOrEmpty(tgtSchema)
                && !string.Equals(tgtSchema, "public", StringComparison.OrdinalIgnoreCase))
            {
                actions.Insert(0, new DdlAction
                {
                    Id = ActionId(sel.Table, "SetSearchPath", $"SET search_path TO {tgtSchema}"),
                    Kind = "SetSearchPath",
                    Summary = $"切换 search_path 至 {tgtSchema}（DDL 用）",
                    Sql = $"SET search_path TO \"{tgtSchema}\"",
                });
                var reset = new DdlAction
                {
                    Id = ActionId(sel.Table, "ResetSearchPath", "RESET search_path"),
                    Kind = "ResetSearchPath",
                    Summary = "恢复 search_path",
                    Sql = "RESET search_path;",
                };
                if (postCopy.Count > 0) postCopy.Add(reset);
                else actions.Add(reset);
            }

            plans.Add(new TableExecution
            {
                Source = sel,
                Target = new TableSelection { Database = req.TargetDatabase, Schema = tgtSchema, Table = sel.Table },
                TargetTableExists = existing is not null,
                StructureActions = DedupIds(actions),
                PostCopyActions = DedupIds(postCopy),
                Data = data,
                ConversionIssues = issueList,
                Extensions = extensions,
                RenameCandidates = renameHints,
                SourceMeta = srcMeta,
                TargetConverted = converted,
            });
        }

        // 目标端多余的表
        if (req.DropExtraTables)
        {
            var selected = req.Tables.Select(t => t.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tgtSchema = ResolveTargetSchema(target, source.Dialect, null, req.TargetSchema);
            var targetTables = (await target.ListTablesAsync(req.TargetDatabase, tgtSchema, ct))
                .Where(t => t.Kind == "table")
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var extra in targetTables.Except(selected))
            {
                var tgtRef = new TableRef(req.TargetDatabase, tgtSchema, extra);
                plans.Add(new TableExecution
                {
                    Source = new TableSelection { Database = req.TargetDatabase, Schema = tgtSchema, Table = extra },
                    Target = new TableSelection { Database = req.TargetDatabase, Schema = tgtSchema, Table = extra },
                    TargetTableExists = true,
                    StructureActions =
                    [
                        new DdlAction
                        {
                            Id = ActionId(extra, "DropTable", extra),
                            Kind = "DropTable",
                            Summary = $"删除目标端多余表 {extra}",
                            Sql = target.Ddl.DropTable(tgtRef).First(),
                            IsDestructive = true,
                        },
                    ],
                });
            }
        }

        // 视图依赖表存在：稳定地排在所有表之后（并行分层也不早于表）
        plans = plans
            .Select((p, i) => (p, i))
            .OrderBy(x => x.p.SourceMeta?.IsView == true ? 1 : 0)
            .ThenBy(x => x.i)
            .Select(x => x.p)
            .ToList();
        plans = SortByDependencies(plans, tableDeps);

        return new ExecutionPlan
        {
            Options = req,
            Tables = plans,
        };
    }

    // ------------------------------------------------------------------

    /// <summary>视图动作：目标不存在 → CreateView；已存在 → DropView（破坏性）+ CreateView。DDL 原文不翻译。</summary>
    private static async Task AppendViewActions(
        IDbProvider source, IDbProvider target, SyncPlanRequest req, TableSelection sel,
        string? tgtSchema, CanonicalTable? existing,
        List<DdlAction> actions, List<ConversionIssue> issues, CancellationToken ct)
    {
        var def = await source.GetViewDefinitionAsync(req.SourceDatabase, sel.Schema, sel.Table, ct);
        if (string.IsNullOrWhiteSpace(def))
        {
            issues.Add(new ConversionIssue("warning", $"视图 {sel.Table} 定义无法从源库读取，已跳过"));
            return;
        }
        var createSql = BuildCreateView(target.Dialect, sel.Table, tgtSchema, def, req.TargetDatabase);
        if (existing is not null)
        {
            var tgtRef = new TableRef(req.TargetDatabase, tgtSchema, sel.Table);
            foreach (var sql in target.Ddl.DropTable(tgtRef))
                actions.Add(new DdlAction
                {
                    Id = ActionId(sel.Table, "DropView", sql),
                    Kind = "DropView",
                    Summary = $"删除已存在视图 {sel.Table}",
                    Sql = sql,
                    IsDestructive = true,
                });
        }
        if (target.Dialect == ArkDialect.PostgreSQL && !string.IsNullOrEmpty(tgtSchema))
        {
            // 视图查询体可能含未限定的表名：临时切换 search_path 执行
            actions.Add(new DdlAction
            {
                Id = ActionId(sel.Table, "SetSearchPath", $"SET search_path TO {tgtSchema}"),
                Kind = "SetSearchPath",
                Summary = $"切换 search_path 至 {tgtSchema}（视图创建用）",
                Sql = $"SET search_path TO \"{tgtSchema}\"",
            });
        }
        actions.Add(new DdlAction
        {
            Id = ActionId(sel.Table, "CreateView", createSql),
            Kind = "CreateView",
            Summary = $"创建视图 {sel.Table}",
            Sql = createSql,
            Warnings = ["视图查询体按源端原文复制，未做方言翻译，请人工确认"],
        });
        if (target.Dialect == ArkDialect.PostgreSQL && !string.IsNullOrEmpty(tgtSchema))
        {
            actions.Add(new DdlAction
            {
                Id = ActionId(sel.Table, "ResetSearchPath", "RESET search_path"),
                Kind = "ResetSearchPath",
                Summary = "恢复 search_path",
                Sql = "RESET search_path;",
            });
        }
    }

    private static string BuildCreateView(ArkDialect dialect, string table, string? schema, string def, string? database)
    {
        string name;
        if (dialect == ArkDialect.PostgreSQL && !string.IsNullOrEmpty(schema))
            name = $"\"{schema}\".\"{table}\"";
        else if (dialect == ArkDialect.MySQL)
            name = string.IsNullOrEmpty(database) ? $"`{table}`" : $"`{database}`.`{table}`";
        else
            name = $"\"{table}\"";
        var body = ExtractViewBody(def);
        return $"CREATE OR REPLACE VIEW {name} AS {body}";
    }

    /// <summary>从源端视图定义中提取查询体（PG pg_get_viewdef 直接是查询体；SQLite/MySQL 带 CREATE VIEW 头）。</summary>
    private static string ExtractViewBody(string def)
    {
        var body = def.Trim();
        if (body.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            var asIdx = IndexOfAsKeyword(body);
            if (asIdx > 0) body = body[(asIdx + 3)..].Trim().TrimEnd(';');
        }
        return body;
    }

    private static int IndexOfAsKeyword(string s)
    {
        for (var i = 0; i < s.Length - 3; i++)
        {
            if ((s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r') &&
                string.Equals(s.Substring(i + 1, 2), "AS", StringComparison.OrdinalIgnoreCase) &&
                (i + 3 >= s.Length || s[i + 3] == ' ' || s[i + 3] == '\t' || s[i + 3] == '\n' || s[i + 3] == '\r' || s[i + 3] == '('))
                return i;
        }
        return -1;
    }

    private static async Task ValidateWhereAsync(
        IDbProvider source, TableRef srcRef, string where, List<ConversionIssue> issues, CancellationToken ct)
    {
        if (where.Contains(';') || where.Contains("--") || where.Contains("/*"))
        {
            issues.Add(new ConversionIssue("error", $"WHERE 片段包含非法字符（分号/注释）: {where}"));
            return;
        }
        try
        {
            await using var conn = await source.OpenConnectionAsync(srcRef.Database, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 15;
            cmd.CommandText = $"SELECT 1 FROM {source.Ddl.TableName(srcRef)} WHERE ({where}) LIMIT 1";
            await cmd.ExecuteScalarAsync(ct);
        }
        catch (Exception ex)
        {
            issues.Add(new ConversionIssue("error", $"WHERE 片段无法编译: {ex.Message}"));
        }
    }

    /// <summary>把逐项 Diff 变成带破坏性标记的 DdlAction。</summary>
    private static List<DdlAction> DiffToActions(
        IDbProvider target, TableDiff baseDiff, TableRef tgtRef, string tableName)
    {
        var actions = new List<DdlAction>();
        var empty = baseDiff with
        {
            AddedColumns = [],
            AlteredColumns = [],
            DroppedColumns = [],
            AddedIndexes = [],
            DroppedIndexes = [],
            AddedForeignKeys = [],
            DroppedForeignKeys = [],
            PrimaryKeyChanged = false,
        };

        void Emit(TableDiff mini, string kind, string summary, bool destructive)
        {
            var warnings = new List<string>();
            var stmts = target.Ddl.AlterTable(mini, warnings);
            foreach (var sql in stmts)
                actions.Add(new DdlAction
                {
                    Id = ActionId(tableName, kind, sql),
                    Kind = kind,
                    Summary = summary,
                    Sql = sql,
                    IsDestructive = destructive,
                    Warnings = warnings.Distinct().ToList(),
                });
        }

        foreach (var col in baseDiff.AddedColumns)
            Emit(empty with { AddedColumns = [col] }, "AddColumn", $"添加列 {tableName}.{col.Name}", false);

        foreach (var change in baseDiff.AlteredColumns)
        {
            var destructive = !Equals(change.Before.Type, change.After.Type);
            Emit(empty with { AlteredColumns = [change] }, "AlterColumn",
                $"修改列 {tableName}.{change.After.Name}" + (destructive ? $"（类型 {change.Before.Type} → {change.After.Type}）" : ""),
                destructive);
        }

        foreach (var col in baseDiff.DroppedColumns)
            Emit(empty with { DroppedColumns = [col] }, "DropColumn", $"删除列 {tableName}.{col.Name}", true);

        foreach (var idx in baseDiff.DroppedIndexes)
        foreach (var sql in target.Ddl.DropIndex(idx.Name, tgtRef))
            actions.Add(new DdlAction
            {
                Id = ActionId(tableName, "DropIndex", sql),
                Kind = "DropIndex",
                Summary = $"删除索引 {idx.Name}",
                Sql = sql,
                Warnings = ["删除索引不损失数据，但影响查询性能直至重建"],
            });

        foreach (var idx in baseDiff.AddedIndexes)
        foreach (var sql in target.Ddl.CreateIndex(idx, tgtRef, baseDiff.After))
            actions.Add(new DdlAction
            {
                Id = ActionId(tableName, "CreateIndex", sql),
                Kind = "CreateIndex",
                Summary = $"创建索引 {idx.Name}（{string.Join(", ", idx.Columns)}）",
                Sql = sql,
            });

        foreach (var fk in baseDiff.DroppedForeignKeys)
        foreach (var sql in target.Ddl.DropForeignKey(fk.Name, tgtRef))
            actions.Add(new DdlAction
            {
                Id = ActionId(tableName, "DropForeignKey", sql),
                Kind = "DropForeignKey",
                Summary = $"删除外键 {fk.Name}",
                Sql = sql,
            });

        foreach (var fk in baseDiff.AddedForeignKeys)
        foreach (var sql in target.Ddl.CreateForeignKey(fk, tgtRef))
            actions.Add(new DdlAction
            {
                Id = ActionId(tableName, "AddForeignKey", sql),
                Kind = "AddForeignKey",
                Summary = $"添加外键 {fk.Name} → {fk.ReferencedTable}",
                Sql = sql,
            });

        return actions;
    }

    /// <summary>内容派生的稳定动作 ID（同内容同 ID；不同动作以序号去重）。</summary>
    private static IReadOnlyList<DdlAction> DedupIds(List<DdlAction> actions)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<DdlAction>(actions.Count);
        foreach (var a in actions)
        {
            if (seen.TryGetValue(a.Id, out var n))
            {
                seen[a.Id] = n + 1;
                result.Add(a with { Id = $"{a.Id}#{n}" });
            }
            else
            {
                seen[a.Id] = 1;
                result.Add(a);
            }
        }
        return result;
    }

    private static string ActionId(string table, string kind, string sql) =>
        $"{table}:{kind}:{sql}";

    /// <summary>按外键依赖拓扑排序（Kahn，稳定）：被引用的表排在前面；存在循环依赖时保持原序。</summary>
    private static List<TableExecution> SortByDependencies(List<TableExecution> plans, Dictionary<string, HashSet<string>> deps)
    {
        var result = new List<TableExecution>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<TableExecution>(plans);
        while (remaining.Count > 0)
        {
            var progressed = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var p = remaining[i];
                var unmet = deps.TryGetValue(p.Source.Table, out var d) &&
                            d.Any(t => !done.Contains(t));
                if (unmet) continue;
                result.Add(p);
                done.Add(p.Source.Table);
                remaining.RemoveAt(i);
                progressed = true;
                i--;
            }
            if (!progressed)
            {
                result.AddRange(remaining);
                break;
            }
        }
        return result;
    }

    private static string? ResolveTargetSchema(
        IDbProvider target, ArkDialect sourceDialect, string? sourceSchema, string? overrideSchema)
    {
        if (!string.IsNullOrWhiteSpace(overrideSchema)) return overrideSchema;
        return target.Dialect switch
        {
            // 仅当源端本身有真实 schema 层（PgSQL）时继承；SQLite 的 main / MySQL 的库名不是有效目标 schema
            ArkDialect.PostgreSQL => sourceDialect == ArkDialect.PostgreSQL
                ? (string.IsNullOrEmpty(sourceSchema) ? "public" : sourceSchema)
                : "public",
            _ => null, // MySQL/SQLite 无 schema 层
        };
    }

    private static bool IsFunctionCall(string expr)
    {
        var i = expr.IndexOf('(');
        return i > 0 && expr[..i].Trim().All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '"' or '`');
    }

    private static string? FnName(string expr)
    {
        var i = expr.IndexOf('(');
        if (i <= 0) return null;
        var name = expr[..i].Trim().Trim('"', '`');
        return Identifier.IsValid(name) ? name.ToLowerInvariant() : null;
    }
}
