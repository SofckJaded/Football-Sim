using System;
using System.Collections.Generic;

namespace FootballSim.World.Core;

/// <summary>
/// Named random consumers in a world simulation. Each stream is derived independently from the world seed,
/// so adding or reordering draws in one stream (say scouting) never shifts the draws of another stream
/// (say fixtures). That independence is what lets separate world systems evolve without invalidating the
/// determinism of the rest.
/// </summary>
public enum WorldRandomStream
{
    /// <summary>Umbrella stream for contributions that genuinely span several systems.</summary>
    Master = 0,

    /// <summary>Club, player and manager identity and backstory generation.</summary>
    WorldGeneration = 1,

    /// <summary>Latent talent, aptitude and personality rolls.</summary>
    Talent = 2,

    /// <summary>Season fixture list generation.</summary>
    Fixtures = 3,

    /// <summary>Quick-sim resolution of AI-only fixtures.</summary>
    QuickSim = 4,

    /// <summary>Weekly player growth and decline.</summary>
    Growth = 5,

    /// <summary>Scouting visibility rolls and target discovery.</summary>
    Scouting = 6,

    /// <summary>Transfer and loan market decisions.</summary>
    Transfers = 7,

    /// <summary>Contract renewals, expiries and free-agent decisions.</summary>
    Contracts = 8,

    /// <summary>Injury and recovery rolls.</summary>
    Injuries = 9,

    /// <summary>Youth intake and retiring-player regeneration.</summary>
    Regens = 10,

    /// <summary>Manager hiring, firing and carousel decisions.</summary>
    Managers = 11,

    /// <summary>Domestic cup draws and tie resolution.</summary>
    Cup = 12,

    /// <summary>Career-level events: inbox offers, awards and trials.</summary>
    Career = 13,

    /// <summary>Diagnostics and audit sampling. Must never feed a simulated outcome.</summary>
    Diagnostics = 14
}

/// <summary>
/// Deterministic random source for the living-world simulation.
///
/// It is deliberately self-contained rather than reusing the match engine's random generator: world and
/// career determinism must not move when a match-engine RNG change lands. The generator is SplitMix64 —
/// the state advances by the golden-ratio constant and the output is the SplitMix64 finalizer of the new
/// state — and <see cref="NextUInt"/> returns the upper 32 bits, matching the match engine's output
/// convention and taking the statistically strongest half of the 64-bit mix.
///
/// Every stream is identified by <c>(rootSeed, stream, fork ordinal)</c>: derive a root stream with
/// <see cref="Derive"/>/<see cref="For"/>, then fork independent children with <see cref="Fork(ulong)"/> or
/// <see cref="Fork(WorldRandomStream, ulong)"/>. Forks derive from the parent's <see cref="Seed"/> only, so
/// creating a fork never consumes parent randomness and adding a new fork cannot shift draws that already
/// existed.
/// </summary>
public sealed class WorldRng
{
    /// <summary>Golden-ratio increment: the SplitMix64 state stride and the stream separation constant.</summary>
    private const ulong OrdinateStride = 0x9E3779B97F4A7C15UL;

    /// <summary>Stride used to separate the forks of one parent stream.</summary>
    private const ulong ForkStride = 0xC2B2AE3D27D4EB4FUL;

    private ulong _state;

    /// <summary>
    /// Creates a stream from a seed. Seed 0 is replaced by the golden-ratio constant so that zero input
    /// still produces a usable, deterministic stream.
    /// </summary>
    public WorldRng(ulong seed)
    {
        Seed = NonZero(seed);
        _state = Seed;
    }

    /// <summary>The seed this generator was constructed with, after the zero-seed substitution.</summary>
    public ulong Seed { get; }

    /// <summary>
    /// Current 64-bit stream state. Capturing it and assigning it back restores the stream exactly, which is
    /// how save/load resumes world randomness without replaying history. Assigning 0 is mapped to the
    /// golden-ratio constant, mirroring the constructor.
    /// </summary>
    public ulong State
    {
        get => _state;
        set => _state = NonZero(value);
    }

    /// <summary>
    /// Derives the root seed of <paramref name="stream"/> for a world seeded with <paramref name="rootSeed"/>.
    /// The result is stable for a given pair and never 0.
    /// </summary>
    public static ulong Derive(ulong rootSeed, WorldRandomStream stream)
    {
        ulong z = rootSeed + OrdinateStride * ((ulong)stream + 1UL);
        return NonZero(Mix(z));
    }

    /// <summary>Creates the root generator of <paramref name="stream"/> for <paramref name="rootSeed"/>.</summary>
    public static WorldRng For(ulong rootSeed, WorldRandomStream stream) =>
        new WorldRng(Derive(rootSeed, stream));

    /// <summary>
    /// Creates an independent child stream from this generator's <see cref="Seed"/> and the caller-supplied
    /// ordinal. The parent's state is neither read nor advanced, so forking is free with respect to existing
    /// draws; <c>(parent seed, ordinal)</c> fully determines the child.
    /// </summary>
    public WorldRng Fork(ulong ordinal)
    {
        ulong z = Seed + ForkStride * (ordinal + 1UL);
        return new WorldRng(NonZero(Mix(z)));
    }

    /// <summary>
    /// Creates an independent child stream that also mixes in the named <paramref name="stream"/>, so two
    /// forks of the same parent can address "quick sim ordinal 3" and "cup ordinal 3" separately. The
    /// unnamed <see cref="Fork(ulong)"/> occupies its own line of the derivation and never collides with a
    /// named stream. The parent's state is not advanced.
    /// </summary>
    public WorldRng Fork(WorldRandomStream stream, ulong ordinal)
    {
        ulong z = Seed + OrdinateStride * ((ulong)stream + 1UL) + ForkStride * (ordinal + 1UL);
        return new WorldRng(NonZero(Mix(z)));
    }

    /// <summary>Advances the stream and returns the upper 32 bits of the SplitMix64 output.</summary>
    public uint NextUInt()
    {
        _state += OrdinateStride;
        return (uint)(Mix(_state) >> 32);
    }

    /// <summary>
    /// Uniform integer in <c>[minInclusive, maxExclusive)</c> using rejection sampling, so every value is
    /// equally likely (a bare modulo would favour the low end).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxExclusive"/> is not greater than <paramref name="minInclusive"/>.
    /// </exception>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive,
                $"The exclusive upper bound must be greater than the inclusive lower bound ({minInclusive}).");

        ulong span = (ulong)((long)maxExclusive - minInclusive);
        ulong rejectionThreshold = (0x1_0000_0000UL - span) % span;

        uint sample = NextUInt();
        while (sample < rejectionThreshold)
            sample = NextUInt();

        return (int)(minInclusive + (long)(sample % span));
    }

    /// <summary>Uniform float in <c>[0, 1)</c> built from the top 24 bits of one draw (24-bit mantissa).</summary>
    public float NextFloat() => (NextUInt() & 0x00FFFFFFu) * (1.0f / 0x01000000);

    /// <summary>Uniform double in <c>[0, 1)</c> built from 53 bits over two draws.</summary>
    public double NextDouble()
    {
        ulong high = NextUInt() >> 6; // 26 high bits
        ulong low = NextUInt() >> 5;  // 27 low bits
        return (high * 134217728.0 + low) * (1.0 / 9007199254740992.0);
    }

    /// <summary>Uniform float in <c>[min, max)</c>.</summary>
    public float NextRange(float min, float max) => min + (max - min) * NextFloat();

    /// <summary>
    /// Approximately normal sample via the Box-Muller transform over two consecutive draws. The result is
    /// clamped to <c>mean ± 8 * |stdDev|</c> so a pathological draw can never produce infinity or a wildly
    /// out-of-family value; a zero <paramref name="stdDev"/> returns <paramref name="mean"/> exactly.
    /// </summary>
    public float NextGaussian(float mean = 0f, float stdDev = 1f)
    {
        // 1 - NextFloat() lies in (0, 1], which keeps Log finite; both uniforms are independent draws.
        float u1 = 1.0f - NextFloat();
        float u2 = NextFloat();
        float magnitude = MathF.Sqrt(-2.0f * MathF.Log(u1));
        float z = magnitude * MathF.Cos(2.0f * MathF.PI * u2);
        float limit = 8.0f * MathF.Abs(stdDev);
        return Math.Clamp(mean + z * stdDev, mean - limit, mean + limit);
    }

    /// <summary>
    /// True with the given probability. <paramref name="probability"/> is clamped to <c>[0, 1]</c>, so 0
    /// never returns true and 1 always does.
    /// </summary>
    public bool NextBool(float probability = 0.5f)
    {
        float clamped = Math.Clamp(probability, 0f, 1f);
        return NextFloat() < clamped;
    }

    /// <summary>Uniform index in <c>[0, count)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public int NextIndex(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be positive.");
        return NextInt(0, count);
    }

    /// <summary>
    /// Deterministic Fisher-Yates shuffle of <paramref name="list"/>. The same generator state and the same
    /// starting order always produce the same permutation.
    /// </summary>
    public void Shuffle<T>(IList<T> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = NextInt(0, i + 1);
            T swap = list[i];
            list[i] = list[j];
            list[j] = swap;
        }
    }

    /// <summary>Picks a uniformly random element of <paramref name="list"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="list"/> is empty.</exception>
    public T Pick<T>(IReadOnlyList<T> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.Count == 0)
            throw new InvalidOperationException("Cannot pick from an empty list.");
        return list[NextIndex(list.Count)];
    }

    /// <summary>SplitMix64 finalizer.</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Maps a zero mix result to the golden-ratio constant so a stream can never be zero-seeded.</summary>
    private static ulong NonZero(ulong value) => value == 0UL ? OrdinateStride : value;
}
