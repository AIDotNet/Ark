using Ark.Core.Metadata;
using Ark.Core.Sync;
using Xunit;

namespace Ark.Sync.Tests;

public class StructureDifferFingerprintTests
{
    private static CanonicalTable T(
        IReadOnlyList<CanonicalIndex>? indexes = null,
        IReadOnlyList<CanonicalForeignKey>? fks = null,
        List<CanonicalColumn>? columns = null) =>
        new()
        {
            Name = "t",
            Columns = columns ??
            [
                new CanonicalColumn { Name = "id", Type = CanonicalType.Long, Nullable = false },
                new CanonicalColumn { Name = "name", Type = CanonicalType.Str },
            ],
            Indexes = indexes ?? [],
            ForeignKeys = fks ?? [],
        };

    [Fact]
    public void SameNameDifferentIndexDefinition_IsDetectedAsRecreate()
    {
        var before = T([new CanonicalIndex { Name = "ix", Columns = ["name"], IsUnique = false }]);
        var after = T([new CanonicalIndex { Name = "ix", Columns = ["name"], IsUnique = true }]);

        var diff = StructureDiffer.Diff(before, after, new TableRef("db", null, "t"));

        Assert.Single(diff.DroppedIndexes);
        Assert.Single(diff.AddedIndexes);
    }

    [Fact]
    public void SameNameSameIndexDefinition_IsNoop()
    {
        var before = T([new CanonicalIndex { Name = "ix", Columns = ["name"], IsUnique = true }]);
        var after = T([new CanonicalIndex { Name = "ix", Columns = ["name"], IsUnique = true }]);

        var diff = StructureDiffer.Diff(before, after, new TableRef("db", null, "t"));

        Assert.Empty(diff.DroppedIndexes);
        Assert.Empty(diff.AddedIndexes);
    }

    [Fact]
    public void PartialIndexPredicate_Change_IsDetected()
    {
        var before = T([new CanonicalIndex { Name = "ix", Columns = ["name"], WhereClause = "a" }]);
        var after = T([new CanonicalIndex { Name = "ix", Columns = ["name"], WhereClause = "b" }]);

        var diff = StructureDiffer.Diff(before, after, new TableRef("db", null, "t"));

        Assert.Single(diff.DroppedIndexes);
        Assert.Single(diff.AddedIndexes);
    }

    [Fact]
    public void ForeignKeyOnDeleteChange_IsDetected()
    {
        CanonicalForeignKey Fk(string? onDelete) => new()
        {
            Name = "fk1",
            Columns = ["a"],
            ReferencedTable = "r",
            ReferencedColumns = ["id"],
            OnDelete = onDelete,
        };
        var before = T(fks: [Fk("CASCADE")]);
        var after = T(fks: [Fk(null)]); // null 归一为 NO ACTION ≠ CASCADE

        var diff = StructureDiffer.Diff(before, after, new TableRef("db", null, "t"));

        Assert.Single(diff.DroppedForeignKeys);
        Assert.Single(diff.AddedForeignKeys);
    }

    [Fact]
    public void ForeignKeyNoAction_Null_Equivalence_IsNoop()
    {
        CanonicalForeignKey Fk(string? onDelete) => new()
        {
            Name = "fk1",
            Columns = ["a"],
            ReferencedTable = "r",
            ReferencedColumns = ["id"],
            OnDelete = onDelete,
        };
        var before = T(fks: [Fk("NO ACTION")]);
        var after = T(fks: [Fk(null)]);

        var diff = StructureDiffer.Diff(before, after, new TableRef("db", null, "t"));

        Assert.Empty(diff.DroppedForeignKeys);
        Assert.Empty(diff.AddedForeignKeys);
    }

    [Fact]
    public void AdjacentSameTypeColumnSwap_IsRenameCandidate()
    {
        var before = T(columns:
        [
            new CanonicalColumn { Name = "id", Type = CanonicalType.Long, Nullable = false },
            new CanonicalColumn { Name = "old_name", Type = CanonicalType.Str },
        ]);
        var after = T(columns:
        [
            new CanonicalColumn { Name = "id", Type = CanonicalType.Long, Nullable = false },
            new CanonicalColumn { Name = "new_name", Type = CanonicalType.Str },
        ]);

        var result = StructureDiffer.DiffDetailed(before, after, new TableRef("db", null, "t"));

        var candidate = Assert.Single(result.RenameCandidates);
        Assert.Equal("old_name", candidate.DropColumn);
        Assert.Equal("new_name", candidate.AddColumn);
        Assert.Contains(result.Diff.DroppedColumns, c => c.Name == "old_name");
        Assert.Contains(result.Diff.AddedColumns, c => c.Name == "new_name");
    }

    [Fact]
    public void DistantTypeMismatchColumnSwap_IsNotRenameCandidate()
    {
        var before = T(columns:
        [
            new CanonicalColumn { Name = "id", Type = CanonicalType.Long, Nullable = false },
            new CanonicalColumn { Name = "gone", Type = CanonicalType.Long },
        ]);
        var after = T(columns:
        [
            new CanonicalColumn { Name = "id", Type = CanonicalType.Long, Nullable = false },
            new CanonicalColumn { Name = "new_col", Type = CanonicalType.Str },
        ]);

        var result = StructureDiffer.DiffDetailed(before, after, new TableRef("db", null, "t"));

        Assert.Empty(result.RenameCandidates);
    }
}

public class KeyCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTrips_Values()
    {
        var key = KeyCodec.EncodeValues([123L, "hello", null]);
        var values = KeyCodec.Decode(key);
        Assert.Equal(3, values.Length);
        Assert.Equal(123L, values[0]);
        Assert.Equal("hello", values[1]);
        Assert.Null(values[2]);
    }

    [Fact]
    public void CompoundKeys_JoinWithSeparator()
    {
        var key = KeyCodec.EncodeValues(["a", 1L]);
        Assert.Contains('\x1F', key);
        Assert.Equal(2, KeyCodec.Decode(key).Length);
    }
}

public class CronExpressionTests
{
    [Fact]
    public void EveryMinute_NextIsAfterPlusOne()
    {
        var cron = CronExpression.Parse("* * * * *");
        var next = cron.NextOccurrence(new DateTimeOffset(2026, 1, 1, 10, 30, 15, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 31, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void DailyAt1030_RespectsHourAndMinute()
    {
        var cron = CronExpression.Parse("30 10 * * *");
        var next = cron.NextOccurrence(new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 10, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void StepField_Works()
    {
        var cron = CronExpression.Parse("*/15 * * * *");
        var next = cron.NextOccurrence(new DateTimeOffset(2026, 1, 1, 10, 31, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 45, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Weekday_IsRespected()
    {
        // 2026-01-03 是周六；cron 限定周一(1) → 下一个周一是 2026-01-05
        var cron = CronExpression.Parse("0 9 * * 1");
        var next = cron.NextOccurrence(new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void ListAndRange_AreSupported()
    {
        var cron = CronExpression.Parse("0 9,18 * 1-3 *");
        var next = cron.NextOccurrence(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 18, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void InvalidFieldCount_Throws()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse("0 9 * *"));
        Assert.Throws<FormatException>(() => CronExpression.Parse("0 9 * * * *"));
    }
}

public class MaskingTests
{
    [Fact]
    public void ExactAndWildcardPatterns_Match()
    {
        var rules = new List<MaskRule>
        {
            new("email", MaskRuleKind.Fixed, "redacted@example.com"),
            new("user_*", MaskRuleKind.Null),
        };
        Assert.NotNull(Masking.Match(rules, "email"));
        Assert.NotNull(Masking.Match(rules, "user_name"));
        Assert.Null(Masking.Match(rules, "email2"));
    }

    [Fact]
    public void Compile_AppliesByColumnIndex()
    {
        var rules = new List<MaskRule> { new("email", MaskRuleKind.Fixed, "x@y.z") };
        var compiled = Masking.Compile(rules, ["id", "email", "name"]);
        Assert.True(compiled.ContainsKey(1));
        Assert.False(compiled.ContainsKey(0));
        var rnd = new Random(1);
        Assert.Equal("x@y.z", compiled[1](rnd, "original"));
    }

    [Fact]
    public void NullAndRandomKinds_TransformValues()
    {
        var rules = new List<MaskRule>
        {
            new("a", MaskRuleKind.Null),
            new("b", MaskRuleKind.RandomLetter, "5"),
        };
        var compiled = Masking.Compile(rules, ["a", "b"]);
        var rnd = new Random(7);
        Assert.Null(compiled[0](rnd, "x"));
        var letter = compiled[1](rnd, "x");
        Assert.IsType<string>(letter);
        Assert.Equal(5, ((string)letter!).Length);
    }
}
