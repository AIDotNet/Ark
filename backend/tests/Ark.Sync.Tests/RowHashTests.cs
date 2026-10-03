using Ark.Core.Metadata;
using Ark.Sync;
using Xunit;

namespace Ark.Sync.Tests;

public class RowHashTests
{
    [Fact]
    public void SameValues_DifferentColumnOrder_ProduceSameHash()
    {
        var h1 = RowHash.Compute(["id", "name"], [1L, "alice"]);
        var h2 = RowHash.Compute(["name", "id"], ["alice", 1L]);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void DifferentValues_ProduceDifferentHash()
    {
        var h1 = RowHash.Compute(["id", "name"], [1L, "alice"]);
        var h2 = RowHash.Compute(["id", "name"], [1L, "bob"]);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void StringVsNumber_ProduceDifferentHash()
    {
        var h1 = RowHash.Compute(["v"], ["1"]);
        var h2 = RowHash.Compute(["v"], [1L]);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void NullValues_AreStable()
    {
        var h1 = RowHash.Compute(["a", "b"], [null, "x"]);
        var h2 = RowHash.Compute(["b", "a"], ["x", null]);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void PkKey_EncodesCompositeKeys()
    {
        var key1 = RowHash.PkKey(["a", "b"], [1L, "x"], ["a", "b"]);
        var key2 = RowHash.PkKey(["a", "b"], [1L, "x"], ["a", "b", "c"]);
        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, RowHash.PkKey(["a", "b"], [1L, "y"], ["a", "b"]));
    }

    private static readonly CanonicalType Long = CanonicalType.Long;
}
