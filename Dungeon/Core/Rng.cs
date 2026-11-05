namespace Dungeon.Core;

// SplitMix64: tiny, fast, and its whole state is one ulong, so it can be copied exactly.
// System.Random cannot be cloned, which skill verification and replay need.
public sealed class Rng
{
    public ulong State { get; private set; }

    public Rng(ulong seed) => State = seed;

    // Derives an independent stream per purpose so that, e.g., changing entity logic
    // does not shift the layout generated for the same seed.
    public static Rng ForStream(int seed, string stream) => new(Mix((ulong)seed ^ StableHash(stream)));

    public Rng Clone() => new(State);

    public ulong NextULong()
    {
        State += 0x9E3779B97F4A7C15UL;
        return Mix(State);
    }

    // Uniform int in [minInclusive, maxExclusive).
    public int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        var range = (ulong)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextULong() % range);
    }

    // Uniform double in [0, 1).
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public double Range(double min, double max) => min + NextDouble() * (max - min);

    public bool Chance(double probability) => NextDouble() < probability;

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    // string.GetHashCode() is randomized per process in .NET, so use FNV-1a instead.
    private static ulong StableHash(string text)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 0x100000001B3UL;
        }
        return hash;
    }
}
