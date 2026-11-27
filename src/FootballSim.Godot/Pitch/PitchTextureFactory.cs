using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using FootballSim.Core;

namespace FootballSim.GodotClient;

/// <summary>
/// One set of procedural pitch textures, shared by both ground meshes and cached per pitch/tier so a
/// scene transition never regenerates them (docs/football-grass.md Â§5.6).
/// </summary>
public sealed class PitchTextureSet : IDisposable
{
    /// <summary>
    /// Packed detail texture (RGBA8, mipmapped, tiling). R,G = turf normal XY encoded as
    /// <c>0.5 + 0.5 * n</c>; B = micro albedo luminance (0.5 = neutral); A = micro ambient
    /// occlusion (1 = open, ~0.55 = fully occluded).
    /// </summary>
    public required ImageTexture DetailPacked { get; init; }

    /// <summary>Macro variation (R8, mipmapped, tiling). Sampled at two world scales for blotch and clump variation.</summary>
    public required ImageTexture MacroNoise { get; init; }

    /// <summary>Static wear mask (R8, no mips, maps the pitch rectangle exactly). 0 = pristine, 1 = worn.</summary>
    public required ImageTexture WearMap { get; init; }

    /// <summary>Detail texture edge in texels; 0 when the tier has no detail texture bound.</summary>
    public int DetailSize { get; init; }

    public void Dispose()
    {
        DetailPacked.Dispose();
        MacroNoise.Dispose();
        WearMap.Dispose();
    }
}

/// <summary>
/// Per-part generation cost of the most recent <see cref="PitchTextureFactory.Get"/> build. Internal
/// only: it exists so the dev texture dump scene can report where the G2.1 time budget goes without
/// widening the public texture contract consumed by <c>PitchSurfaceBuilder</c>.
/// </summary>
internal readonly struct PitchTextureBuildTimings
{
    /// <summary>Wall time for the packed detail image (generation + CPU mipmap build) in milliseconds.</summary>
    public double DetailMs { get; init; }

    /// <summary>Wall time for the macro noise image in milliseconds.</summary>
    public double MacroMs { get; init; }

    /// <summary>Wall time for the wear mask image in milliseconds.</summary>
    public double WearMs { get; init; }

    /// <summary>Wall time for the three <see cref="ImageTexture"/> uploads in milliseconds.</summary>
    public double UploadMs { get; init; }

    /// <summary>Mipmap levels actually present in the generated detail image (1 means no mips).</summary>
    public int DetailMipmaps { get; init; }

    /// <summary>Mipmap levels actually present in the generated macro noise image (1 means no mips).</summary>
    public int MacroMipmaps { get; init; }

    /// <summary>Mipmap levels present in the wear image; the contract requires 1 (unmipped).</summary>
    public int WearMipmaps { get; init; }

    /// <summary>True when the macro texture fell back to RGBA8 because R8 mipmap generation failed.</summary>
    public bool MacroUsesRgba8 { get; init; }

    // Detail sub-phases (dev diagnostics): the detail image dominates the G2.1 startup budget, so the
    // dump scene reports where inside it the time goes.
    public double DetailBaseMs { get; init; }
    public double DetailBladeMs { get; init; }
    public double DetailCombineMs { get; init; }
    public double DetailEncodeMs { get; init; }
    public double DetailMipmapMs { get; init; }

    /// <summary>Sum of all parts in milliseconds.</summary>
    public double TotalMs => DetailMs + MacroMs + WearMs + UploadMs;
}

/// <summary>
/// Builds the pitch's procedural textures once per (pitch, tier), with deterministic seeds so the
/// surface is identical on every run and never touches simulation randomness
/// (docs/football-grass.md Â§5.6, G2.1).
///
/// Every layer is generated from a periodic value-noise lattice, so the detail and macro textures
/// wrap exactly on both axes; the wear mask is non-tiling and maps the pitch rectangle 1:1. Only
/// integer hashes over fixed constants are used â€” no <see cref="System.Random"/>, no clock, no
/// simulation RNG.
///
/// The per-pixel loops live in small job classes whose row body is marked
/// <see cref="MethodImplOptions.AggressiveOptimization"/>. Godot .NET runs the game assembly with
/// debug (unoptimized) JIT settings, and these loops are called exactly once at startup, so the
/// default tier-0 code would never be recompiled before the work was over; forcing optimized code
/// keeps the one-time generation inside the 30 ms budget without changing a single output value.
/// </summary>
public static class PitchTextureFactory
{
    private const uint DetailSeed = 0x6A09E667u;
    private const uint MacroSeed = 0xBB67AE85u;
    private const uint WearSeed = 0x3C6EF372u;

    /// <summary>Detail normal tilt is clamped to this tangent-space magnitude (â‰ˆ35Â°) â€” grass tuft scale, not rock.</summary>
    private const float MaxDetailNormalTilt = 0.6f;

    private static readonly Dictionary<(string Key, int Detail, int Macro, int Wear), PitchTextureSet> Cache = new();

    private static bool _r8MipFallbackLogged;

    // Detail sub-phase timings, copied into LastBuildTimings for the dev texture dump scene.
    private static double _detailBaseMs;
    private static double _detailBladeMs;
    private static double _detailCombineMs;
    private static double _detailEncodeMs;
    private static double _detailMipmapMs;

    /// <summary>Per-part cost of the last uncached build (dev diagnostics only).</summary>
    internal static PitchTextureBuildTimings LastBuildTimings { get; private set; }

    /// <summary>Returns the cached texture set for a pitch and quality tier.</summary>
    public static PitchTextureSet Get(PitchDimensions pitch, PitchQualitySettings quality)
    {
        string key = $"{pitch.Length:F2}x{pitch.Width:F2}";
        var cacheKey = (key, quality.DetailTextureSize, quality.MacroTextureSize, quality.WearTextureSize);
        if (Cache.TryGetValue(cacheKey, out PitchTextureSet? existing))
            return existing;

        PitchTextureSet set = Build(pitch, quality);
        Cache[cacheKey] = set;
        return set;
    }

    private static PitchTextureSet Build(PitchDimensions pitch, PitchQualitySettings quality)
    {
        var clock = Stopwatch.StartNew();

        Image detail = BuildDetailImage(quality.DetailTextureSize);
        double detailMs = clock.Elapsed.TotalMilliseconds;

        Image macro = BuildMacroImage(quality.MacroTextureSize);
        double macroMs = clock.Elapsed.TotalMilliseconds - detailMs;

        Image wear = BuildWearImage(pitch, quality.WearTextureSize);
        double wearMs = clock.Elapsed.TotalMilliseconds - detailMs - macroMs;

        // CreateFromImage uploads the CPU image (already carrying its CPU-generated mip chain) to the
        // rendering server once; the textures are then cached for the lifetime of the process.
        ImageTexture detailTexture = ImageTexture.CreateFromImage(detail);
        ImageTexture macroTexture = ImageTexture.CreateFromImage(macro);
        ImageTexture wearTexture = ImageTexture.CreateFromImage(wear);
        double uploadMs = clock.Elapsed.TotalMilliseconds - detailMs - macroMs - wearMs;

        LastBuildTimings = new PitchTextureBuildTimings
        {
            DetailMs = detailMs,
            MacroMs = macroMs,
            WearMs = wearMs,
            UploadMs = uploadMs,
            DetailMipmaps = detail.GetMipmapCount(),
            MacroMipmaps = macro.GetMipmapCount(),
            WearMipmaps = wear.GetMipmapCount(),
            MacroUsesRgba8 = macro.GetFormat() == Image.Format.Rgba8,
            DetailBaseMs = _detailBaseMs,
            DetailBladeMs = _detailBladeMs,
            DetailCombineMs = _detailCombineMs,
            DetailEncodeMs = _detailEncodeMs,
            DetailMipmapMs = _detailMipmapMs
        };

        return new PitchTextureSet
        {
            DetailPacked = detailTexture,
            MacroNoise = macroTexture,
            WearMap = wearTexture,
            DetailSize = quality.DetailTextureSize
        };
    }

    // -------------------------------------------------------------------------------------------------
    // Detail texture: packed turf normal + micro albedo + micro AO
    // -------------------------------------------------------------------------------------------------
    //
    // Height field = 62% low-frequency tuft/clump field + 38% high-frequency blade field. The blade
    // field is domain-warped by a third low-frequency field before it is added, which is what stops the
    // grain from reading as a uniform brushed pattern. The final normal is a wrapped central difference
    // of that height field, so the Sobel-style boundary handling is automatic (index wrap == tiling).
    // Albedo follows blade tips (lighter) and crevices (darker); AO darkens the crevices between tufts.

    private static Image BuildDetailImage(int size)
    {
        // Tiers ask for 1024 (High), 512 (Medium) or 1 (Low). Anything else that is not a power-of-two
        // edge of at least 64 texels cannot carry the periodic lattice, so it falls back to neutral.
        int n = size;
        if (n < 64 || (n & (n - 1)) != 0)
        {
            _detailBaseMs = 0.0;
            _detailBladeMs = 0.0;
            _detailCombineMs = 0.0;
            _detailEncodeMs = 0.0;
            _detailMipmapMs = 0.0;
            return CreateSolidRgba(1, 1, 128, 128, 128, 255);
        }

        int baseRes = n / 4;
        var clock = Stopwatch.StartNew();

        // --- low-frequency tuft/clump field and the warp field that bends the blade layer ---
        var clump = new float[baseRes * baseRes];
        var warp = new float[baseRes * baseRes];
        int p = Math.Max(1, baseRes / 64);
        FillPeriodicNoiseStack([clump, warp], baseRes,
        [
            PreparePeriodicLayer(baseRes, p, p, DetailSeed + 0x01u, 0.50f, false, 0),
            PreparePeriodicLayer(baseRes, Math.Max(1, baseRes / 32), Math.Max(1, baseRes / 32), DetailSeed + 0x03u, 0.27f, true, 0),
            PreparePeriodicLayer(baseRes, Math.Max(1, baseRes / 16), Math.Max(1, baseRes / 16), DetailSeed + 0x05u, 0.14f, true, 0),
            PreparePeriodicLayer(baseRes, Math.Max(1, baseRes / 8), Math.Max(1, baseRes / 8), DetailSeed + 0x07u, 0.09f, true, 0),
            PreparePeriodicLayer(baseRes, Math.Max(1, baseRes / 32), Math.Max(1, baseRes / 32), DetailSeed + 0x09u, 0.60f, false, 1),
            PreparePeriodicLayer(baseRes, Math.Max(1, baseRes / 16), Math.Max(1, baseRes / 16), DetailSeed + 0x0Bu, 0.40f, true, 1)
        ]);

        _detailBaseMs = clock.Elapsed.TotalMilliseconds;

        // --- high-frequency blade field (unit mean, so it composes with the clump field) ---
        // Two isotropic octaves carry the tuft grain; two anisotropic octaves carry the blade streaks
        // (narrow in U, long in V) which read as grass laid along the mow direction.
        // Large one-time buffers are allocated uninitialized: every byte is written before it is read,
        // and skipping the eager zero fill removes ~18 MB of memset from the startup path.
        var blade = GC.AllocateUninitializedArray<float>(n * n);
        FillPeriodicNoiseStack([blade], n,
        [
            PreparePeriodicLayer(n, Math.Max(1, n / 16), Math.Max(1, n / 16), DetailSeed + 0x11u, 0.30f, false, 0),
            PreparePeriodicLayer(n, Math.Max(1, n / 32), Math.Max(1, n / 32), DetailSeed + 0x13u, 0.24f, true, 0),
            PreparePeriodicLayer(n, Math.Max(1, n / 64), Math.Max(1, n / 64), DetailSeed + 0x15u, 0.13f, true, 0),
            PreparePeriodicLayer(n, Math.Max(1, n / 8), Math.Max(1, n / 64), DetailSeed + 0x17u, 0.21f, true, 0),
            PreparePeriodicLayer(n, Math.Max(1, n / 8), Math.Max(1, n / 32), DetailSeed + 0x19u, 0.12f, true, 0)
        ]);

        _detailBladeMs = clock.Elapsed.TotalMilliseconds - _detailBaseMs;

        var height = GC.AllocateUninitializedArray<float>(n * n);
        var tip = GC.AllocateUninitializedArray<float>(n * n);
        var bare = GC.AllocateUninitializedArray<byte>(n * n);
        CombineDetail(n, baseRes, clump, warp, blade, height, tip, bare);

        _detailCombineMs = clock.Elapsed.TotalMilliseconds - _detailBaseMs - _detailBladeMs;

        // Encode straight into the head of the mip chain buffer so the 4 MB level-0 data is never copied
        // twice; the rest of the chain is then filled by one parallel dispatch.
        var chain = GC.AllocateUninitializedArray<byte>(MipChainBytes(n, 4));
        EncodeDetail(n, height, tip, bare, chain);

        _detailEncodeMs = clock.Elapsed.TotalMilliseconds - _detailBaseMs - _detailBladeMs - _detailCombineMs;

        GenerateMipChain(n, chain);
        Image image = Image.CreateFromData(n, n, true, Image.Format.Rgba8, chain);

        _detailMipmapMs = clock.Elapsed.TotalMilliseconds - _detailBaseMs - _detailBladeMs - _detailCombineMs - _detailEncodeMs;
        return image;
    }

    private static void CombineDetail(
        int n,
        int baseRes,
        float[] clump,
        float[] warp,
        float[] blade,
        float[] height,
        float[] tip,
        byte[] bareMask)
    {
        int baseMask = baseRes - 1;
        var column0 = new int[n];
        var column1 = new int[n];
        var weightX = new float[n];
        var warpColumn0 = new int[n];
        var warpColumn1 = new int[n];
        for (int x = 0; x < n; x++)
        {
            int cell = x >> 2;
            float t = (x - (cell << 2)) * 0.25f;
            column0[x] = cell;
            column1[x] = (cell + 1) & baseMask;
            weightX[x] = t * t * (3f - 2f * t);
            warpColumn0[x] = (cell + 37) & baseMask;
            warpColumn1[x] = (cell + 38) & baseMask;
        }

        var row0 = new int[n];
        var row1 = new int[n];
        var weightY = new float[n];
        var warpRow0 = new int[n];
        var warpRow1 = new int[n];
        for (int y = 0; y < n; y++)
        {
            int cell = y >> 2;
            float t = (y - (cell << 2)) * 0.25f;
            row0[y] = cell;
            row1[y] = (cell + 1) & baseMask;
            weightY[y] = t * t * (3f - 2f * t);
            warpRow0[y] = (cell + 11) & baseMask;
            warpRow1[y] = (cell + 12) & baseMask;
        }

        var job = new DetailCombineJob
        {
            Size = n,
            BaseResolution = baseRes,
            Clump = clump,
            Warp = warp,
            Blade = blade,
            Height = height,
            Tip = tip,
            Bare = bareMask,
            Column0 = column0,
            Column1 = column1,
            WeightX = weightX,
            WarpColumn0 = warpColumn0,
            WarpColumn1 = warpColumn1,
            Row0 = row0,
            Row1 = row1,
            WeightY = weightY,
            WarpRow0 = warpRow0,
            WarpRow1 = warpRow1
        };
        Parallel.For(0, n, job.Run);
    }

    private static void EncodeDetail(int n, float[] height, float[] tip, byte[] bareMask, byte[] rgba)
    {
        var job = new DetailEncodeJob
        {
            Size = n,
            Height = height,
            Tip = tip,
            Bare = bareMask,
            Rgba = rgba
        };
        Parallel.For(0, n, job.Run);
    }

    // -------------------------------------------------------------------------------------------------
    // Macro variation: R8 tiling value field, mean 0.5 / std 0.15
    // -------------------------------------------------------------------------------------------------

    private static Image BuildMacroImage(int size)
    {
        int n = size;
        if (n < 8 || (n & (n - 1)) != 0)
            return CreateSolidR8(1, 1, 128);

        // Broad blotches (1/4 of the texture) plus finer clumps (1/8 and 1/16). The shader samples this
        // texture at two world scales, so it needs real content at both ends of that range.
        var field = new float[n * n];
        FillPeriodicNoiseStack([field], n,
        [
            PreparePeriodicLayer(n, Math.Max(1, n / 4), Math.Max(1, n / 4), MacroSeed + 0x01u, 0.62f, false, 0),
            PreparePeriodicLayer(n, Math.Max(1, n / 8), Math.Max(1, n / 8), MacroSeed + 0x03u, 0.26f, true, 0),
            PreparePeriodicLayer(n, Math.Max(1, n / 16), Math.Max(1, n / 16), MacroSeed + 0x05u, 0.12f, true, 0)
        ]);

        // Normalise to the contract's mean 0.5 / std 0.15. Real turf varies by a few percent, so the
        // field must stay low contrast; the shader also multiplies in the detail and stripe terms.
        double sum = 0.0;
        double sumSquares = 0.0;
        for (int i = 0; i < field.Length; i++)
        {
            sum += field[i];
            sumSquares += field[i] * (double)field[i];
        }

        double mean = sum / field.Length;
        double variance = sumSquares / field.Length - mean * mean;
        float standardDeviation = MathF.Sqrt(MathF.Max((float)variance, 1e-6f));
        float scale = 0.15f / standardDeviation;

        var bytes = new byte[n * n];
        var job = new MacroNormalizeJob
        {
            Size = n,
            Field = field,
            Mean = (float)mean,
            Scale = scale,
            Bytes = bytes
        };
        Parallel.For(0, n, job.Run);

        Image image = Image.CreateFromData(n, n, false, Image.Format.R8, bytes);
        image.GenerateMipmaps();

        // Godot can seed mipmaps for R8 on this build; the guard keeps the shader contract honest on a
        // backend that cannot, at the cost of a 4x wider texture (sampled .r is identical).
        if (image.GetMipmapCount() <= 1)
            image = RebuildAsRgba8WithMips(n, bytes);

        return image;
    }

    private static Image RebuildAsRgba8WithMips(int n, byte[] red)
    {
        if (!_r8MipFallbackLogged)
        {
            GD.PushWarning("PitchTextureFactory: R8 mipmap generation unsupported; using RGBA8 for macro_noise (shader reads .r).");
            _r8MipFallbackLogged = true;
        }

        var rgba = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            byte value = red[i];
            int j = i * 4;
            rgba[j] = value;
            rgba[j + 1] = value;
            rgba[j + 2] = value;
            rgba[j + 3] = 255;
        }

        Image image = Image.CreateFromData(n, n, false, Image.Format.Rgba8, rgba);
        image.GenerateMipmaps();
        return image;
    }

    // -------------------------------------------------------------------------------------------------
    // Wear mask: static baseline traffic zones, R8, pitch rectangle, no mips
    // -------------------------------------------------------------------------------------------------
    //
    // Row 0 maps to world Z = -HalfWidth and column 0 to world X = -HalfLength, i.e. U follows pitch
    // length (X) and V follows pitch width (Z) across the full 105 x 68 m rectangle. Zones are softened
    // with broad noise warping and a mid-scale mask so the edges never look painted on.
    //
    // The zones describe where the turf is actually trampled â€” goalmouths, penalty areas, touchline
    // strips, the centre kick-off area â€” never the painted markings themselves. A wear band exactly on a
    // painted line is invisible in the render (the paint covers it) and reads as a mistake in the mask,
    // so the centre-circle wear is a blotchy disc inside the ring and the penalty-arc ring is gone.

    private static Image BuildWearImage(PitchDimensions pitch, int size)
    {
        int n = size <= 1 ? 1 : size;
        var bytes = new byte[n * n];
        if (n == 1)
            return Image.CreateFromData(1, 1, false, Image.Format.R8, bytes);

        // Zone irregularity needs noise at three scales: broad blotches (~26/13/6.5 m), mid clumps
        // (~3.3/1.6 m) and fine scuff (~0.8/0.4 m). The seven octaves and the zone evaluation run in one
        // row dispatch: each row derives its three noise samples, then resolves the zone maths straight
        // away, so the mask build is a single dispatch over this small 256x256 texture.
        var job = new WearBuildJob
        {
            Size = n,
            PitchDimensions = pitch,
            Bytes = bytes,
            Layers =
            [
                PreparePeriodicLayer(n, 4, 4, WearSeed + 0x01u, 0.50f, false, 0),
                PreparePeriodicLayer(n, 8, 8, WearSeed + 0x03u, 0.30f, true, 0),
                PreparePeriodicLayer(n, 16, 16, WearSeed + 0x05u, 0.20f, true, 0),
                PreparePeriodicLayer(n, 32, 32, WearSeed + 0x11u, 0.65f, false, 1),
                PreparePeriodicLayer(n, 64, 64, WearSeed + 0x13u, 0.35f, true, 1),
                PreparePeriodicLayer(n, 128, 128, WearSeed + 0x23u, 0.60f, false, 2),
                PreparePeriodicLayer(n, 256, 256, WearSeed + 0x25u, 0.40f, true, 2)
            ]
        };
        Parallel.For(0, n, job.Run);

        // Unmipped by contract: the shader samples it bilinearly only and G4 updates it in place.
        return Image.CreateFromData(n, n, false, Image.Format.R8, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float EvaluateWear(
        float worldX,
        float worldZ,
        PitchDimensions pitch,
        float coarse,
        float mid,
        float fine)
    {
        float ax = MathF.Abs(worldX);
        float az = MathF.Abs(worldZ);
        float halfLength = pitch.HalfLength;
        float halfWidth = pitch.HalfWidth;

        // Irregular, non-painted edges: warp the zone query point with broad turf noise and modulate
        // each zone with a mid-scale mask. The three noise fields are generated once per mask build.
        float qx = ax + (coarse - 0.5f) * 2.2f;
        float qz = az + (mid - 0.5f) * 1.6f;
        float edge = 0.72f + 0.56f * mid + 0.18f * fine;

        // --- goalmouths and six-yard boxes: the heaviest baseline traffic (players, keeper, goal kicks) ---
        float goalArea = BoxMask(
            qx - (halfLength - pitch.GoalAreaLength * 0.5f),
            qz,
            pitch.GoalAreaLength * 0.5f,
            pitch.GoalAreaWidth * 0.5f,
            0.9f);
        float goalMouth = BoxMask(qx - (halfLength - 1.15f), qz, 1.15f, 4.3f, 0.6f);

        // --- penalty areas: mild general traffic, plus a wide irregular scuff halo around each spot ---
        float penaltyArea = BoxMask(
            qx - (halfLength - pitch.PenaltyAreaLength * 0.5f),
            qz,
            pitch.PenaltyAreaLength * 0.5f,
            pitch.PenaltyAreaWidth * 0.5f,
            1.4f);
        float penaltySpotX = halfLength - pitch.PenaltySpotDistance;
        float penaltySpot = DiscMask(qx - penaltySpotX, qz, 2.3f, 1.5f);

        // --- kick-off traffic: a blotchy disc INSIDE the centre circle, strongest at the spot and on
        // the kick-off lane along the halfway line; it fades out well before the painted circle. ---
        float centreDistance = MathF.Sqrt(worldX * worldX + worldZ * worldZ);
        float circleInside = 1f - SmoothStep(
            pitch.CenterCircleRadius * 0.70f,
            pitch.CenterCircleRadius * 0.97f,
            centreDistance);
        float blotch = 0.50f + 0.70f * (coarse * 0.55f + mid * 0.45f);
        float centreDisc = circleInside * blotch;
        float blobDistance = MathF.Sqrt(
            worldX * worldX / 13.0f + worldZ * worldZ / 7.3f);
        float centreBlob = 1f - SmoothStep(0.55f, 1.25f, blobDistance + (mid - 0.5f) * 0.45f);
        float kickOffLane = BoxMask(worldX, worldZ, 3.0f, 8.6f, 2.6f) * (0.55f + 0.60f * mid);

        // --- touchline strips: warm-up, throw-ins, coaching-box traffic ---
        float touchlineDistance = halfWidth - az;
        float touchline = touchlineDistance <= 0f
            ? 0f
            : MathF.Exp(-(touchlineDistance * touchlineDistance) / 2.5f);

        float wear = 0.085f + 0.060f * coarse + 0.030f * fine;
        wear += 0.40f * goalArea * edge;
        wear += 0.22f * goalMouth * (0.80f + 0.40f * mid);
        wear += 0.10f * penaltyArea * (0.75f + 0.50f * mid);
        wear += 0.30f * penaltySpot * (0.70f + 0.60f * fine);
        wear += 0.20f * centreDisc * (0.70f + 0.60f * fine);
        wear += 0.20f * centreBlob;
        wear += 0.14f * kickOffLane;
        wear += 0.30f * touchline * (0.60f + 0.80f * mid);
        // Soft shoulder keeps the goalmouth hotspots in the contract's ~0.55-0.8 peak band instead of
        // letting overlapping zones stack to a flat, clamped white patch.
        if (wear > 0.78f)
            wear = 0.78f + (wear - 0.78f) * 0.35f;
        return Math.Clamp(wear, 0f, 1f);
    }

    // -------------------------------------------------------------------------------------------------
    // Noise primitives
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// One octave of periodic value noise: the lattice wraps at the texture border, which is what makes
    /// every generated field tile exactly. Prepared once and then evaluated by
    /// <see cref="PeriodicNoiseStackJob"/> for a whole row at a time.
    /// </summary>
    private sealed class PeriodicNoiseLayer
    {
        public int PeriodX;
        public int PeriodY;
        public int CellsX;
        public int TargetIndex;
        public float Amplitude;
        public bool Add;
        public float[] Lattice = null!;
        public int[] Column0 = null!;
        public int[] Column1 = null!;
        public float[] WeightX = null!;
        public int[] Row0 = null!;
        public int[] Row1 = null!;
        public float[] WeightY = null!;
    }

    private static PeriodicNoiseLayer PreparePeriodicLayer(
        int resolution,
        int periodX,
        int periodY,
        uint seed,
        float amplitude,
        bool add,
        int targetIndex)
    {
        periodX = Math.Clamp(periodX, 1, resolution);
        periodY = Math.Clamp(periodY, 1, resolution);
        int cellsX = resolution / periodX;
        int cellsY = resolution / periodY;

        var lattice = new float[periodX * periodY];
        for (int j = 0; j < periodY; j++)
        {
            for (int i = 0; i < periodX; i++)
                lattice[j * periodX + i] = HashToUnit(seed ^ ((uint)i * 0x9E3779B9u) ^ ((uint)j * 0x85EBCA6Bu));
        }

        var column0 = new int[resolution];
        var column1 = new int[resolution];
        var weightX = new float[resolution];
        for (int x = 0; x < resolution; x++)
        {
            int cell = x / cellsX;
            float t = (x - cell * cellsX) / (float)cellsX;
            column0[x] = cell;
            column1[x] = (cell + 1) % periodX;
            weightX[x] = t * t * (3f - 2f * t);
        }

        var row0 = new int[resolution];
        var row1 = new int[resolution];
        var weightY = new float[resolution];
        for (int y = 0; y < resolution; y++)
        {
            int cell = y / cellsY;
            float t = (y - cell * cellsY) / (float)cellsY;
            row0[y] = cell;
            row1[y] = (cell + 1) % periodY;
            weightY[y] = t * t * (3f - 2f * t);
        }

        return new PeriodicNoiseLayer
        {
            PeriodX = periodX,
            PeriodY = periodY,
            CellsX = cellsX,
            TargetIndex = targetIndex,
            Amplitude = amplitude,
            Add = add,
            Lattice = lattice,
            Column0 = column0,
            Column1 = column1,
            WeightX = weightX,
            Row0 = row0,
            Row1 = row1,
            WeightY = weightY
        };
    }

    /// <summary>
    /// Evaluates several periodic noise octaves into one or more fields with a single parallel
    /// dispatch (a layer writes the field named by its <see cref="PeriodicNoiseLayer.TargetIndex"/>).
    /// Layers are accumulated in the listed order with the same arithmetic as one-at-a-time generation,
    /// so the result is bit-identical to per-layer passes while saving per-pass dispatch overhead.
    /// </summary>
    private static void FillPeriodicNoiseStack(float[][] targets, int resolution, PeriodicNoiseLayer[] layers)
    {
        var job = new PeriodicNoiseStackJob
        {
            Targets = targets,
            Resolution = resolution,
            Layers = layers
        };
        Parallel.For(0, resolution, job.Run);
    }

    /// <summary>Wrapped bilinear sample for the high-frequency blade field (linear weights are enough there).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleLinearWrapped(float[] field, int resolution, float x, float y)
    {
        int mask = resolution - 1;
        int xi = (int)MathF.Floor(x);
        int yi = (int)MathF.Floor(y);
        float tx = x - xi;
        float ty = y - yi;
        int x0 = xi & mask;
        int x1 = (xi + 1) & mask;
        int y0 = (yi & mask) * resolution;
        int y1 = ((yi + 1) & mask) * resolution;
        float top = field[y0 + x0] + (field[y0 + x1] - field[y0 + x0]) * tx;
        float bottom = field[y1 + x0] + (field[y1 + x1] - field[y1 + x0]) * tx;
        return top + (bottom - top) * ty;
    }

    /// <summary>Integer avalanche hash (xxHash-style finalizer): fixed constants, no state, no clock.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HashToUnit(uint value) => (Hash(value) & 0x00FFFFFFu) * (1f / 16777216f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Soft-edged axis-aligned box: 1 inside, 0 outside, feathered by Â±<paramref name="feather"/> metres.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float BoxMask(float offsetX, float offsetZ, float halfX, float halfZ, float feather)
    {
        float outsideX = MathF.Abs(offsetX) - halfX;
        float outsideZ = MathF.Abs(offsetZ) - halfZ;
        float outside = MathF.Max(outsideX, outsideZ);
        return 1f - SmoothStep(-feather, feather, outside);
    }

    /// <summary>Soft-edged disc: 1 inside the radius, 0 beyond it plus the feather.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DiscMask(float offsetX, float offsetZ, float radius, float feather)
    {
        float distance = MathF.Sqrt(offsetX * offsetX + offsetZ * offsetZ);
        return 1f - SmoothStep(radius - feather, radius + feather, distance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float unitValue) =>
        (byte)Math.Clamp((int)(unitValue * 255f + 0.5f), 0, 255);

    /// <summary>Total bytes of a complete RGBA8 mip chain, i.e. what <see cref="GenerateMipChain"/> fills.</summary>
    private static int MipChainBytes(int n, int channels)
    {
        int totalBytes = 0;
        for (int w = n, h = n; ; )
        {
            totalBytes += Math.Max(1, w) * Math.Max(1, h) * channels;
            if (w == 1 && h == 1)
                break;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }

        return totalBytes;
    }

    /// <summary>
    /// Fills all mip levels after level 0 with the same 2x2 box-filter average in one parallel dispatch
    /// across every destination row of every level. The engine's CPU mip builder is single-threaded and
    /// this was 3-4 ms of the G2.1 startup budget at 1024x1024.
    /// </summary>
    private static void GenerateMipChain(int n, byte[] chain)
    {
        int sourceOffset = 0;
        int sourceWidth = n;
        int sourceHeight = n;
        int destOffset = n * n * 4;
        while (sourceWidth > 1 || sourceHeight > 1)
        {
            int destWidth = Math.Max(1, sourceWidth >> 1);
            int destHeight = Math.Max(1, sourceHeight >> 1);
            var level = new MipmapLevelJob
            {
                Chain = chain,
                SourceOffset = sourceOffset,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                DestOffset = destOffset,
                DestWidth = destWidth
            };
            // One dispatch per level and a barrier in between: a coarser level must never read a source
            // level whose rows another worker is still writing (the earlier single flat dispatch did).
            Parallel.For(0, destHeight, level.Run);

            sourceOffset = destOffset;
            sourceWidth = destWidth;
            sourceHeight = destHeight;
            destOffset += destWidth * destHeight * 4;
        }
    }

    private static Image CreateSolidRgba(int width, int height, byte r, byte g, byte b, byte a)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = r;
            bytes[i + 1] = g;
            bytes[i + 2] = b;
            bytes[i + 3] = a;
        }

        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, bytes);
    }

    private static Image CreateSolidR8(int width, int height, byte value)
    {
        var bytes = new byte[width * height];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = value;

        return Image.CreateFromData(width, height, false, Image.Format.R8, bytes);
    }

    // -------------------------------------------------------------------------------------------------
    // Row jobs. Each body is one image row and is the only hot code in this file; forcing optimized
    // compilation keeps the one-time generation inside budget under Godot's debug JIT settings.
    // -------------------------------------------------------------------------------------------------

    private sealed class PeriodicNoiseStackJob
    {
        public float[][] Targets = null!;
        public int Resolution;
        public PeriodicNoiseLayer[] Layers = null!;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Run(int y)
        {
            int resolution = Resolution;
            int row = y * resolution;
            float[][] targets = Targets;
            for (int layerIndex = 0; layerIndex < Layers.Length; layerIndex++)
            {
                PeriodicNoiseLayer layer = Layers[layerIndex];
                int periodX = layer.PeriodX;
                int latticeRow0 = layer.Row0[y] * periodX;
                int latticeRow1 = layer.Row1[y] * periodX;
                float weightY = layer.WeightY[y];
                float amplitude = layer.Amplitude;
                float[] lattice = layer.Lattice;
                int[] column0 = layer.Column0;
                int[] column1 = layer.Column1;
                float[] weightX = layer.WeightX;
                float[] target = targets[layer.TargetIndex];
                // Lattice columns are only 4+ texels apart for every layer that takes part in the detail
                // and macro fields, so the interpolation cell (and therefore both lattice column
                // indices) is constant across each 4-texel block. Hoisting them out of the inner loop
                // removes two third of the per-texel index loads in the hottest fill pass.
                int block = layer.CellsX >= 4 && (layer.CellsX & 3) == 0 ? 4 : 1;
                if (layer.Add)
                {
                    for (int x = 0; x < resolution; x += block)
                    {
                        int x0 = column0[x];
                        int x1 = column1[x];
                        for (int k = 0; k < block; k++)
                        {
                            int xx = x + k;
                            float wx = weightX[xx];
                            float top = lattice[latticeRow0 + x0] + (lattice[latticeRow0 + x1] - lattice[latticeRow0 + x0]) * wx;
                            float bottom = lattice[latticeRow1 + x0] + (lattice[latticeRow1 + x1] - lattice[latticeRow1 + x0]) * wx;
                            target[row + xx] += (top + (bottom - top) * weightY) * amplitude;
                        }
                    }
                }
                else
                {
                    for (int x = 0; x < resolution; x += block)
                    {
                        int x0 = column0[x];
                        int x1 = column1[x];
                        for (int k = 0; k < block; k++)
                        {
                            int xx = x + k;
                            float wx = weightX[xx];
                            float top = lattice[latticeRow0 + x0] + (lattice[latticeRow0 + x1] - lattice[latticeRow0 + x0]) * wx;
                            float bottom = lattice[latticeRow1 + x0] + (lattice[latticeRow1 + x1] - lattice[latticeRow1 + x0]) * wx;
                            target[row + xx] = (top + (bottom - top) * weightY) * amplitude;
                        }
                    }
                }
            }
        }
    }

    private sealed class DetailCombineJob
    {
        public int Size;
        public int BaseResolution;
        public float[] Clump = null!;
        public float[] Warp = null!;
        public float[] Blade = null!;
        public float[] Height = null!;
        public float[] Tip = null!;
        public byte[] Bare = null!;
        public int[] Column0 = null!;
        public int[] Column1 = null!;
        public float[] WeightX = null!;
        public int[] WarpColumn0 = null!;
        public int[] WarpColumn1 = null!;
        public int[] Row0 = null!;
        public int[] Row1 = null!;
        public float[] WeightY = null!;
        public int[] WarpRow0 = null!;
        public int[] WarpRow1 = null!;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Run(int y)
        {
            int n = Size;
            int baseRes = BaseResolution;
            int row = y * n;
            // Bends the blade grain by up to ~1.2% of the texture edge. Large enough to break the
            // straight anisotropic streak look, small enough that the tuft clumps keep their shape.
            float warpPixels = n * 0.012f;
            int clumpRow0 = Row0[y] * baseRes;
            int clumpRow1 = Row1[y] * baseRes;
            int warpRow0 = WarpRow0[y] * baseRes;
            int warpRow1 = WarpRow1[y] * baseRes;
            float wy = WeightY[y];
            float[] clump = Clump;
            float[] warp = Warp;
            float[] bladeField = Blade;
            float[] height = Height;
            float[] tip = Tip;
            byte[] bare = Bare;
            int[] column0 = Column0;
            int[] column1 = Column1;
            float[] weightX = WeightX;
            int[] warpColumn0 = WarpColumn0;
            int[] warpColumn1 = WarpColumn1;

            // Blocks of four output texels share one base-resolution interpolation cell (the base grid is
            // exactly 4 texels apart), so the twelve lattice loads below are hoisted out of the per-texel
            // work: the inner loop only does the lerp math and the warped blade sample.
            for (int x = 0; x < n; x += 4)
            {
                int c0 = column0[x];
                int c1 = column1[x];
                int wc0 = warpColumn0[x];
                int wc1 = warpColumn1[x];

                float clumpA = clump[clumpRow0 + c0];
                float clumpB = clump[clumpRow0 + c1];
                float clumpC = clump[clumpRow1 + c0];
                float clumpD = clump[clumpRow1 + c1];
                float warpXA = warp[clumpRow0 + c0];
                float warpXB = warp[clumpRow0 + c1];
                float warpXC = warp[clumpRow1 + c0];
                float warpXD = warp[clumpRow1 + c1];
                float warpYA = warp[warpRow0 + wc0];
                float warpYB = warp[warpRow0 + wc1];
                float warpYC = warp[warpRow1 + wc0];
                float warpYD = warp[warpRow1 + wc1];

                for (int k = 0; k < 4; k++)
                {
                    int xx = x + k;
                    float wx = weightX[xx];

                    // Clump and warp fields only exist at base resolution; the 4x upsample weights are
                    // precomputed per column/row so the per-texel cost is a few lerps and four reads.
                    float clumpTop = clumpA + (clumpB - clumpA) * wx;
                    float clumpBottom = clumpC + (clumpD - clumpC) * wx;
                    float clumpValue = clumpTop + (clumpBottom - clumpTop) * wy;

                    float warpTopX = warpXA + (warpXB - warpXA) * wx;
                    float warpBottomX = warpXC + (warpXD - warpXC) * wx;
                    float warpX = warpTopX + (warpBottomX - warpTopX) * wy - 0.5f;

                    float warpTopY = warpYA + (warpYB - warpYA) * wx;
                    float warpBottomY = warpYC + (warpYD - warpYC) * wx;
                    float warpY = warpTopY + (warpBottomY - warpTopY) * wy - 0.5f;

                    float bladeValue = SampleLinearWrapped(bladeField, n, xx + warpX * warpPixels, y + warpY * warpPixels);

                    // Small thin/bare patches: the clump field's crests flatten a little and lose blade cover.
                    float bareValue = SmoothStep(0.60f, 0.78f, clumpValue);
                    bladeValue *= 1f - 0.32f * bareValue;

                    int index = row + xx;
                    height[index] = clumpValue * 0.62f + bladeValue * 0.38f;
                    tip[index] = bladeValue;
                    bare[index] = ToByte(bareValue);
                }
            }
        }
    }

    private sealed class DetailEncodeJob
    {
        public int Size;
        public float[] Height = null!;
        public float[] Tip = null!;
        public byte[] Bare = null!;
        public byte[] Rgba = null!;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Run(int y)
        {
            int n = Size;
            int mask = n - 1;
            int row = y * n;
            int rowUp = ((y - 1) & mask) * n;
            int rowDown = ((y + 1) & mask) * n;
            // Central-difference slope is measured in texels, and the layer feature sizes are a fixed
            // fraction of the texture, so the multiplier has to scale with the edge length to keep the
            // tangent tilt (and therefore the look) identical on High and Medium.
            float normalStrength = n * 0.0085f;
            float[] height = Height;
            float[] tip = Tip;
            byte[] bareMask = Bare;
            byte[] rgba = Rgba;

            for (int x = 0; x < n; x++)
            {
                int left = (x - 1) & mask;
                int right = (x + 1) & mask;

                float heightHere = height[row + x];
                float slopeX = height[row + left] - height[row + right];
                float slopeZ = height[rowUp + x] - height[rowDown + x];

                float normalX = slopeX * normalStrength;
                // A small constant bias along +V gives the grain a mow-lean instead of a perfectly
                // symmetric fuzz; the shader's per-band tilt supplies the larger, alternating lean.
                float normalZ = slopeZ * normalStrength + 0.05f;

                float lengthSquared = normalX * normalX + normalZ * normalZ;
                if (lengthSquared > MaxDetailNormalTilt * MaxDetailNormalTilt)
                {
                    float scale = MaxDetailNormalTilt / MathF.Sqrt(lengthSquared);
                    normalX *= scale;
                    normalZ *= scale;
                }

                float tipValue = tip[row + x];
                // Recover the (unwarped) clump level from the composed height so albedo keeps the
                // clump-scale brightness variation as well as the blade-tip variation.
                float clumpValue = (heightHere - 0.38f * tipValue) * (1f / 0.62f);
                float bareValue = bareMask[row + x] * (1f / 255f);
                float dither = HashToUnit((uint)(row + x) * 0x9E3779B9u ^ DetailSeed) - 0.5f;

                float luminance = 0.5f
                    + 0.20f * (tipValue * 2f - 1f)
                    + 0.06f * (clumpValue * 2f - 1f)
                    + 0.05f * dither
                    - 0.10f * bareValue;
                luminance = Math.Clamp(luminance, 0.30f, 0.72f);

                float occlusion = 0.54f + 0.46f * SmoothStep(0.14f, 0.86f, tipValue) + 0.03f * dither;
                occlusion = Math.Clamp(occlusion, 0.5f, 1f);

                int i = (row + x) * 4;
                // Every packed channel is already clamped to its contract range, so direct float-to-byte
                // truncation is byte-identical to the clamping ToByte helper (verified against the PNG
                // hashes) and skips four per-channel clamps in the hottest encode loop.
                rgba[i] = (byte)((0.5f + 0.5f * normalX) * 255f + 0.5f);
                rgba[i + 1] = (byte)((0.5f + 0.5f * normalZ) * 255f + 0.5f);
                rgba[i + 2] = (byte)(luminance * 255f + 0.5f);
                rgba[i + 3] = (byte)(occlusion * 255f + 0.5f);
            }
        }
    }

    /// <summary>One 2x2 box-filter mip level per destination row; rows of all levels share one dispatch.</summary>
    private sealed class MipmapLevelJob
    {
        public byte[] Chain = null!;
        public int SourceOffset;
        public int SourceWidth;
        public int SourceHeight;
        public int DestOffset;
        public int DestWidth;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Run(int y)
        {
            byte[] chain = Chain;
            int sourceWidth = SourceWidth;
            int sourceRow0 = SourceOffset + y * 2 * sourceWidth * 4;
            int sourceRow1 = SourceOffset + Math.Min(y * 2 + 1, SourceHeight - 1) * sourceWidth * 4;
            int destRow = DestOffset + y * DestWidth * 4;
            for (int x = 0; x < DestWidth; x++)
            {
                int column0 = x * 2 * 4;
                int column1 = Math.Min(x * 2 + 1, sourceWidth - 1) * 4;
                int i00 = sourceRow0 + column0;
                int i01 = sourceRow0 + column1;
                int i10 = sourceRow1 + column0;
                int i11 = sourceRow1 + column1;
                int o = destRow + x * 4;
                chain[o] = (byte)((chain[i00] + chain[i01] + chain[i10] + chain[i11] + 2) >> 2);
                chain[o + 1] = (byte)((chain[i00 + 1] + chain[i01 + 1] + chain[i10 + 1] + chain[i11 + 1] + 2) >> 2);
                chain[o + 2] = (byte)((chain[i00 + 2] + chain[i01 + 2] + chain[i10 + 2] + chain[i11 + 2] + 2) >> 2);
                chain[o + 3] = (byte)((chain[i00 + 3] + chain[i01 + 3] + chain[i10 + 3] + chain[i11 + 3] + 2) >> 2);
            }
        }
    }

    private sealed class MacroNormalizeJob
    {
        public int Size;
        public float[] Field = null!;
        public float Mean;
        public float Scale;
        public byte[] Bytes = null!;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Run(int y)
        {
            int row = y * Size;
            float mean = Mean;
            float scale = Scale;
            float[] field = Field;
            byte[] bytes = Bytes;
            for (int x = 0; x < Size; x++)
            {
                float value = 0.5f + (field[row + x] - mean) * scale;
                bytes[row + x] = ToByte(Math.Clamp(value, 0f, 1f));
            }
        }
    }

    /// <summary>
    /// Builds one wear-mask row: derive the three scale-noise samples for the row, then resolve the zone
    /// maths immediately. Keeping both halves in one row job avoids three noise-field allocations and
    /// three extra parallel dispatches for a 64 KB mask.
    /// </summary>
    private sealed class WearBuildJob
    {
        public int Size;
        public PitchDimensions PitchDimensions = null!;
        public byte[] Bytes = null!;
        public PeriodicNoiseLayer[] Layers = null!;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Run(int y)
        {
            int n = Size;
            PitchDimensions pitch = PitchDimensions;
            byte[] bytes = Bytes;
            float[] coarse = new float[n];
            float[] mid = new float[n];
            float[] fine = new float[n];
            float[][] rows = [coarse, mid, fine];
            PeriodicNoiseLayer[] layers = Layers;

            for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                PeriodicNoiseLayer layer = layers[layerIndex];
                int periodX = layer.PeriodX;
                int latticeRow0 = layer.Row0[y] * periodX;
                int latticeRow1 = layer.Row1[y] * periodX;
                float weightY = layer.WeightY[y];
                float amplitude = layer.Amplitude;
                float[] lattice = layer.Lattice;
                int[] column0 = layer.Column0;
                int[] column1 = layer.Column1;
                float[] weightX = layer.WeightX;
                float[] row = rows[layer.TargetIndex];
                if (layer.Add)
                {
                    for (int x = 0; x < n; x++)
                    {
                        int x0 = column0[x];
                        int x1 = column1[x];
                        float wx = weightX[x];
                        float top = lattice[latticeRow0 + x0] + (lattice[latticeRow0 + x1] - lattice[latticeRow0 + x0]) * wx;
                        float bottom = lattice[latticeRow1 + x0] + (lattice[latticeRow1 + x1] - lattice[latticeRow1 + x0]) * wx;
                        row[x] += (top + (bottom - top) * weightY) * amplitude;
                    }
                }
                else
                {
                    for (int x = 0; x < n; x++)
                    {
                        int x0 = column0[x];
                        int x1 = column1[x];
                        float wx = weightX[x];
                        float top = lattice[latticeRow0 + x0] + (lattice[latticeRow0 + x1] - lattice[latticeRow0 + x0]) * wx;
                        float bottom = lattice[latticeRow1 + x0] + (lattice[latticeRow1 + x1] - lattice[latticeRow1 + x0]) * wx;
                        row[x] = (top + (bottom - top) * weightY) * amplitude;
                    }
                }
            }

            int rowStart = y * n;
            float worldZ = (y + 0.5f) / n * pitch.Width - pitch.HalfWidth;
            for (int x = 0; x < n; x++)
            {
                float worldX = (x + 0.5f) / n * pitch.Length - pitch.HalfLength;
                bytes[rowStart + x] = ToByte(EvaluateWear(worldX, worldZ, pitch, coarse[x], mid[x], fine[x]));
            }
        }
    }
}

