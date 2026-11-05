using Dungeon.Core;

namespace Dungeon.Tests;

public class RngTests
{
    private const ulong PinnedLayoutSeed1 = 17487069102411261042UL;

    [Fact]
    public void Same_seed_gives_same_sequence()
    {
        var a = new Rng(42);
        var b = new Rng(42);
        for (var i = 0; i < 1000; i++)
            Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact]
    public void Clone_continues_identically_and_independently()
    {
        var a = new Rng(7);
        a.NextULong();
        var b = a.Clone();
        var fromA = Enumerable.Range(0, 100).Select(_ => a.Next(0, 1000)).ToList();
        var fromB = Enumerable.Range(0, 100).Select(_ => b.Next(0, 1000)).ToList();
        Assert.Equal(fromA, fromB);
    }

    [Fact]
    public void Streams_differ_for_same_seed()
    {
        Assert.NotEqual(Rng.ForStream(1, "layout").NextULong(), Rng.ForStream(1, "entity").NextULong());
    }

    [Fact]
    public void Stream_derivation_is_stable_across_runs()
    {
        // Pinned value: if this changes, every recorded seed and LLM cache entry is invalidated.
        Assert.Equal(PinnedLayoutSeed1, Rng.ForStream(1, "layout").NextULong());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 5)]
    [InlineData(3, 10)]
    public void Next_stays_in_range(int min, int max)
    {
        var rng = new Rng(99);
        for (var i = 0; i < 10_000; i++)
        {
            var v = rng.Next(min, max);
            Assert.InRange(v, min, max - 1);
        }
    }

    [Fact]
    public void NextDouble_is_in_unit_interval_and_roughly_uniform()
    {
        var rng = new Rng(123);
        var values = Enumerable.Range(0, 100_000).Select(_ => rng.NextDouble()).ToList();
        Assert.All(values, v => Assert.InRange(v, 0.0, 0.9999999999));
        Assert.InRange(values.Average(), 0.49, 0.51);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Chance_edges_are_exact(double p)
    {
        var rng = new Rng(5);
        Assert.All(Enumerable.Range(0, 1000), _ => Assert.Equal(p >= 1.0, rng.Chance(p)));
    }
}
