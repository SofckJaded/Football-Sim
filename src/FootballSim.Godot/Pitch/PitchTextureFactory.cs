using System;
using Godot;
using FootballSim.Core;

namespace FootballSim.GodotClient;

/// <summary>
/// One set of procedural pitch textures, shared by both ground meshes and cached per pitch/tier so a
/// scene transition never regenerates them (docs/football-grass.md §5.6).
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
/// Builds the pitch's procedural textures once per (pitch, tier), with deterministic seeds so the
/// surface is identical on every run and never touches simulation randomness
/// (docs/football-grass.md §5.6, G2.1).
/// </summary>
public static class PitchTextureFactory
{
    private static readonly System.Collections.Generic.Dictionary<(string Key, int Detail, int Macro, int Wear), PitchTextureSet> Cache = new();

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
        Image detail = BuildPlaceholderDetail(quality.DetailTextureSize);
        Image macro = BuildPlaceholderMacro(quality.MacroTextureSize);
        Image wear = BuildPlaceholderWear(quality.WearTextureSize);
        return new PitchTextureSet
        {
            DetailPacked = ImageTexture.CreateFromImage(detail),
            MacroNoise = ImageTexture.CreateFromImage(macro),
            WearMap = ImageTexture.CreateFromImage(wear),
            DetailSize = quality.DetailTextureSize
        };
    }

    // NOTE: placeholder implementation; replaced by the real procedural generator (G2.1).

    private static Image BuildPlaceholderDetail(int size)
    {
        int edge = Mathf.Max(1, size);
        var bytes = new byte[edge * edge * 4];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = 128;
            bytes[i + 1] = 128;
            bytes[i + 2] = 128;
            bytes[i + 3] = 255;
        }
        Image image = Image.CreateFromData(edge, edge, false, Image.Format.Rgba8, bytes);
        if (edge > 1)
            image.GenerateMipmaps();
        return image;
    }

    private static Image BuildPlaceholderMacro(int size)
    {
        int edge = Mathf.Max(1, size);
        var bytes = new byte[edge * edge];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = 128;
        Image image = Image.CreateFromData(edge, edge, false, Image.Format.R8, bytes);
        if (edge > 1)
            image.GenerateMipmaps();
        return image;
    }

    private static Image BuildPlaceholderWear(int size)
    {
        int edge = Mathf.Max(1, size);
        var bytes = new byte[edge * edge];
        return Image.CreateFromData(edge, edge, false, Image.Format.R8, bytes);
    }
}
