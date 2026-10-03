using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Ark.Core.Querying;

public sealed record QueryColumn(string Name, string DataType);

/// <summary>一个结果集（SQL 控制台可返回多个）。</summary>
public sealed record QueryResultSet(IReadOnlyList<QueryColumn> Columns, IReadOnlyList<object?[]> Rows, int AffectedRows);

public sealed record QueryResponse(IReadOnlyList<QueryResultSet> ResultSets, IReadOnlyList<string> Messages, double ElapsedMs);

/// <summary>结构化筛选（后端拼参数化 SQL，标识符先行校验）。</summary>
public sealed record FilterClause
{
    [Required] public string Column { get; init; } = "";
    /// <summary>= != > < >= <= like not_like is_null is_not_null in not_in</summary>
    [Required] public string Op { get; init; } = "=";
    public JsonElement? Value { get; init; }
    public IReadOnlyList<JsonElement>? Values { get; init; }
}

public sealed record OrderClause(string Column, bool Descending);

public sealed record RowQueryRequest
{
    [Range(1, 10_000)] public int Limit { get; init; } = 200;
    [Range(0, int.MaxValue)] public int Offset { get; init; }
    public IReadOnlyList<FilterClause> Filters { get; init; } = [];
    public IReadOnlyList<OrderClause> OrderBy { get; init; } = [];
}

public sealed record RowPage(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    long Total,
    int Limit,
    int Offset,
    IReadOnlyList<string> PrimaryKeyColumns);

// ------------------------- 行编辑变更集 -------------------------

public sealed record RowInsert(IReadOnlyDictionary<string, JsonElement> Values);
public sealed record RowUpdate(IReadOnlyDictionary<string, JsonElement> Key, IReadOnlyDictionary<string, JsonElement> Values);
public sealed record RowDelete(IReadOnlyDictionary<string, JsonElement> Key);

public sealed record RowChangeSet
{
    public IReadOnlyList<RowInsert> Inserts { get; init; } = [];
    public IReadOnlyList<RowUpdate> Updates { get; init; } = [];
    public IReadOnlyList<RowDelete> Deletes { get; init; } = [];
}

public sealed record ApplyChangesResponse(int Inserted, int Updated, int Deleted);

// ------------------------- SQL 补全 / 执行计划 -------------------------

public sealed record CompletionColumn(string Name, string DataType);

/// <summary>补全元数据中的一个表/视图（kind: table | view）。</summary>
public sealed record CompletionTable(string Name, string Kind, IReadOnlyList<CompletionColumn> Columns);

/// <summary>SQL 控制台自动补全所需的库内对象元数据（当前 schema 全量）。</summary>
public sealed record CompletionSchema(IReadOnlyList<CompletionTable> Tables);

/// <summary>EXPLAIN 结果（统一为文本树展示；PG 为 pretty JSON）。</summary>
public sealed record ExplainResponse(string PlanText);
