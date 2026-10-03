namespace Ark.Core.Metadata;

public sealed record ColumnChange(CanonicalColumn Before, CanonicalColumn After);

/// <summary>两张表的结构差异（同名的目标表 vs 目标规范模型）。</summary>
public sealed record TableDiff
{
    public required TableRef Target { get; init; }
    public required CanonicalTable After { get; init; }
    public IReadOnlyList<CanonicalColumn> AddedColumns { get; init; } = [];
    public IReadOnlyList<ColumnChange> AlteredColumns { get; init; } = [];
    public IReadOnlyList<CanonicalColumn> DroppedColumns { get; init; } = [];
    public IReadOnlyList<CanonicalIndex> AddedIndexes { get; init; } = [];
    public IReadOnlyList<CanonicalIndex> DroppedIndexes { get; init; } = [];
    public IReadOnlyList<CanonicalForeignKey> AddedForeignKeys { get; init; } = [];
    public IReadOnlyList<CanonicalForeignKey> DroppedForeignKeys { get; init; } = [];
    public bool PrimaryKeyChanged { get; init; }
}
