using System;
using System.Collections.Generic;
using Xunit;
using FootballSim.World.Core;

namespace FootballSim.World.Tests;

/// <summary>W0 core primitives: deterministic, stream-separated world randomness.</summary>
public sealed class WorldRngTests
{
    private const ulong GoldenConstant = 0x9E3779B97F4A7C15UL;

    [Fact]
    public void SameSeedAndStream_ProduceIdenticalSequences()
    {
        WorldRng first = WorldRng.For(20261127UL, WorldRandomStream.Talent);
        WorldRng second = WorldRng.For(20261127UL, WorldRandomStream.Talent);

        AssertSameSequence(TakeUInts(first, 100), TakeUInts(second, 100));
    }

    [Fact]
    public void DifferentStreams_ProduceDifferentSequences()
    {
        Assert.NotEqual(
            WorldRng.Derive(7UL, WorldRandomStream.Talent),
            WorldRng.Derive(7UL, WorldRandomStream.Growth));

        Assert.True(SequencesDiffer(
            TakeUInts(WorldRng.For(7UL, WorldRandomStream.Talent), 16),
            TakeUInts(WorldRng.For(7UL, WorldRandomStream.Growth), 16)));
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        Assert.True(SequencesDiffer(
            TakeUInts(WorldRng.For(1UL, WorldRandomStream.Talent), 16),
            TakeUInts(WorldRng.For(2UL, WorldRandomStream.Talent), 16)));
    }

    [Fact]
    public void Derive_IsStable_AndNeverZero()
    {
        Assert.Equal(
            WorldRng.Derive(20261127UL, WorldRandomStream.Talent),
            WorldRng.Derive(20261127UL, WorldRandomStream.Talent));

        for (int ordinal = 0; ordinal <= (int)WorldRandomStream.Diagnostics; ordinal++)
        {
            WorldRandomStream stream = (WorldRandomStream)ordinal;
            Assert.NotEqual(0UL, WorldRng.Derive(0UL, stream));
            Assert.NotEqual(0UL, WorldRng.Derive(20261127UL, stream));
            Assert.NotEqual(0UL, WorldRng.Derive(ulong.MaxValue, stream));
        }
    }

    [Fact]
    public void For_EqualsConstructorOverDerive()
    {
        WorldRng factory = WorldRng.For(99UL, WorldRandomStream.Fixtures);
        WorldRng direct = new WorldRng(WorldRng.Derive(99UL, WorldRandomStream.Fixtures));

        Assert.Equal(direct.Seed, factory.Seed);
        Assert.Equal(direct.State, factory.State);
        AssertSameSequence(TakeUInts(direct, 32), TakeUInts(factory, 32));
    }

    [Fact]
    public void Fork_IsRepeatable_AndDiffersFromParent()
    {
        WorldRng parent = new WorldRng(4242UL);
        WorldRng first = parent.Fork(0UL);
        WorldRng second = parent.Fork(0UL);

        Assert.Equal(first.Seed, second.Seed);
        AssertSameSequence(TakeUInts(first, 16), TakeUInts(second, 16));
        Assert.NotEqual(parent.Seed, first.Seed);
    }

    [Fact]
    public void Fork_DifferentOrdinals_ProduceDifferentStreams()
    {
        WorldRng parent = new WorldRng(77UL);

        ulong zeroth = parent.Fork(0UL).Seed;
        ulong first = parent.Fork(1UL).Seed;
        ulong second = parent.Fork(2UL).Seed;

        Assert.NotEqual(zeroth, first);
        Assert.NotEqual(zeroth, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Fork_DoesNotAdvanceParentRandomness()
    {
        WorldRng untouched = new WorldRng(2026UL);
        WorldRng forked = new WorldRng(2026UL);

        WorldRng child = forked.Fork(3UL);
        _ = child.NextUInt();
        _ = forked.Fork(WorldRandomStream.QuickSim, 3UL);

        AssertSameSequence(TakeUInts(untouched, 32), TakeUInts(forked, 32));
    }

    [Fact]
    public void ForkWithStream_IsRepeatable_AndDistinctFromThePlainFork()
    {
        WorldRng parent = new WorldRng(555UL);
        WorldRng plain = parent.Fork(2UL);
        WorldRng named = parent.Fork(WorldRandomStream.QuickSim, 2UL);
        WorldRng namedAgain = parent.Fork(WorldRandomStream.QuickSim, 2UL);

        Assert.NotEqual(plain.Seed, named.Seed);
        Assert.Equal(named.Seed, namedAgain.Seed);
        AssertSameSequence(TakeUInts(named, 16), TakeUInts(namedAgain, 16));

        Assert.NotEqual(
            parent.Fork(WorldRandomStream.QuickSim, 2UL).Seed,
            parent.Fork(WorldRandomStream.Cup, 2UL).Seed);
        Assert.NotEqual(
            parent.Fork(WorldRandomStream.QuickSim, 2UL).Seed,
            parent.Fork(WorldRandomStream.QuickSim, 3UL).Seed);
    }

    [Fact]
    public void NextUInt_CoversTheFullRangeReasonably()
    {
        WorldRng rng = new WorldRng(0xDEADBEEFUL);
        HashSet<byte> highBytes = new HashSet<byte>();
        uint minimum = uint.MaxValue;
        uint maximum = uint.MinValue;

        for (int i = 0; i < 20000; i++)
        {
            uint value = rng.NextUInt();
            highBytes.Add((byte)(value >> 24));
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
        }

        Assert.True(highBytes.Count == 256, $"expected all 256 high bytes, got {highBytes.Count}");
        Assert.True(minimum < 10_000_000u, $"minimum draw was {minimum}");
        Assert.True(maximum > uint.MaxValue - 10_000_000u, $"maximum draw was {maximum}");
    }

    [Fact]
    public void NextInt_StaysInRange_AndReachesBothEndpoints()
    {
        WorldRng rng = new WorldRng(17UL);
        bool sawMinimum = false;
        bool sawMaximum = false;

        for (int i = 0; i < 5000; i++)
        {
            int value = rng.NextInt(-5, 5);
            Assert.InRange(value, -5, 4);
            if (value == -5) sawMinimum = true;
            if (value == 4) sawMaximum = true;
        }

        Assert.True(sawMinimum, "never drew the inclusive lower endpoint");
        Assert.True(sawMaximum, "never drew exclusion - 1 upper endpoint");

        Assert.Equal(0, new WorldRng(1UL).NextInt(0, 1));

        for (int i = 0; i < 100; i++)
            Assert.InRange(rng.NextInt(int.MinValue, int.MaxValue), int.MinValue, int.MaxValue - 1);
    }

    [Fact]
    public void NextInt_ThrowsForEmptyOrInvertedRange()
    {
        WorldRng rng = new WorldRng(1UL);

        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(6, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(int.MaxValue, int.MinValue));
    }

    [Fact]
    public void NextFloat_AndNextDouble_StayInTheUnitInterval()
    {
        WorldRng rng = new WorldRng(31415UL);

        for (int i = 0; i < 10000; i++)
        {
            float single = rng.NextFloat();
            Assert.True(single >= 0f && single < 1f, $"NextFloat left [0,1): {single}");

            double wider = rng.NextDouble();
            Assert.True(wider >= 0.0 && wider < 1.0, $"NextDouble left [0,1): {wider}");
        }
    }

    [Fact]
    public void NextRange_StaysWithinTheRequestedBounds()
    {
        WorldRng rng = new WorldRng(1618UL);

        for (int i = 0; i < 2000; i++)
        {
            float value = rng.NextRange(2f, 5f);
            Assert.True(value >= 2f && value < 5f, $"NextRange left [2,5): {value}");
        }
    }

    [Fact]
    public void NextGaussian_HasTheRequestedMean_AndStaysFinite()
    {
        const float Mean = 2.5f;
        const float StdDev = 0.8f;
        const int SampleCount = 20000;

        WorldRng rng = new WorldRng(2718UL);
        double sum = 0.0;

        for (int i = 0; i < SampleCount; i++)
        {
            float sample = rng.NextGaussian(Mean, StdDev);
            Assert.True(float.IsFinite(sample), $"non-finite gaussian sample: {sample}");
            Assert.InRange(sample, Mean - 8f * StdDev, Mean + 8f * StdDev);
            sum += sample;
        }

        double average = sum / SampleCount;
        Assert.InRange(average, Mean - 0.05, Mean + 0.05);

        Assert.Equal(3.5f, new WorldRng(4UL).NextGaussian(3.5f, 0f));
    }

    [Fact]
    public void NextBool_ClampsProbabilities()
    {
        WorldRng rng = new WorldRng(3UL);

        for (int i = 0; i < 1000; i++)
        {
            Assert.False(rng.NextBool(0f));
            Assert.True(rng.NextBool(1f));
            Assert.False(rng.NextBool(-1f));
            Assert.True(rng.NextBool(2f));
        }

        int trueCount = 0;
        for (int i = 0; i < 10000; i++)
        {
            if (rng.NextBool(0.5f)) trueCount++;
        }

        Assert.InRange(trueCount, 4500, 5500);
    }

    [Fact]
    public void Shuffle_IsAPermutation_AndRepeatableForOneSeed()
    {
        int[] first = ShuffledOrder(42UL);
        int[] second = ShuffledOrder(42UL);
        int[] otherSeed = ShuffledOrder(43UL);

        AssertSameSequence(first, second);
        Assert.True(SequencesDiffer(first, otherSeed), "different seeds produced the same shuffle order");

        int[] sorted = new int[first.Length];
        Array.Copy(first, sorted, first.Length);
        Array.Sort(sorted);
        for (int i = 0; i < sorted.Length; i++)
            Assert.Equal(i, sorted[i]);
    }

    [Fact]
    public void Pick_ReturnsMembers_AndThrowsForAnEmptyList()
    {
        string[] items = { "a", "b", "c" };
        WorldRng rng = new WorldRng(8UL);
        HashSet<string> seen = new HashSet<string>();

        for (int i = 0; i < 300; i++)
        {
            string picked = rng.Pick(items);
            Assert.Contains(picked, items);
            seen.Add(picked);
        }

        Assert.True(seen.Count == 3, $"expected every item to be picked, saw {seen.Count}");
        Assert.Throws<InvalidOperationException>(() => rng.Pick(Array.Empty<string>()));
    }

    [Fact]
    public void NextIndex_StaysInRange_AndThrowsForNonPositiveCounts()
    {
        WorldRng rng = new WorldRng(9UL);

        for (int i = 0; i < 500; i++)
            Assert.InRange(rng.NextIndex(7), 0, 6);

        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextIndex(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextIndex(-3));
    }

    [Fact]
    public void StateCaptureAndRestore_ReproducesTheStream()
    {
        WorldRng rng = new WorldRng(0x1234UL);
        for (int i = 0; i < 10; i++)
            rng.NextUInt();

        ulong saved = rng.State;
        uint[] expected = TakeUInts(rng, 25);

        rng.State = saved;
        Assert.Equal(saved, rng.State);
        AssertSameSequence(expected, TakeUInts(rng, 25));
    }

    [Fact]
    public void ZeroSeed_IsReplacedByTheGoldenConstant()
    {
        WorldRng zeroSeeded = new WorldRng(0UL);
        WorldRng goldenSeeded = new WorldRng(GoldenConstant);

        Assert.Equal(GoldenConstant, zeroSeeded.Seed);
        Assert.Equal(GoldenConstant, zeroSeeded.State);
        AssertSameSequence(TakeUInts(goldenSeeded, 8), TakeUInts(zeroSeeded, 8));

        zeroSeeded.State = 0UL;
        Assert.Equal(GoldenConstant, zeroSeeded.State);
    }

    [Fact]
    public void PinnedGoldenSequence_ForTalentStream_DoesNotChange()
    {
        // W0 determinism pin. These are the first eight draws of WorldRng.For(20261127, Talent) and the
        // stream's derived seed. If either changes, every generated world changed with it.
        string[] expected =
        {
            "0xABB36E24",
            "0x4A910F11",
            "0x3DC66383",
            "0xAD7FF9FE",
            "0x098FC726",
            "0xAD19B133",
            "0x99A4AC53",
            "0x2584C060"
        };

        Assert.Equal(0x0B2025610462570AUL, WorldRng.Derive(20261127UL, WorldRandomStream.Talent));

        WorldRng rng = WorldRng.For(20261127UL, WorldRandomStream.Talent);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], $"0x{rng.NextUInt():X8}");
    }

    private static uint[] TakeUInts(WorldRng rng, int count)
    {
        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
            values[i] = rng.NextUInt();
        return values;
    }

    private static int[] ShuffledOrder(ulong seed)
    {
        List<int> items = new List<int>();
        for (int i = 0; i < 20; i++)
            items.Add(i);

        WorldRng rng = new WorldRng(seed);
        rng.Shuffle(items);
        return items.ToArray();
    }

    private static void AssertSameSequence<T>(T[] expected, T[] actual)
    {
        Assert.True(expected.Length == actual.Length,
            $"expected {expected.Length} values, got {actual.Length}");
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    private static bool SequencesDiffer<T>(T[] first, T[] second)
    {
        if (first.Length != second.Length) return true;
        for (int i = 0; i < first.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(first[i], second[i])) return true;
        }
        return false;
    }
}
