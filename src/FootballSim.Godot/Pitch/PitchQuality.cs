using Godot;

namespace FootballSim.GodotClient;

/// <summary>
/// Pitch material quality tier (docs/football-grass.md §5.7). Low/Medium/High scale the detail
/// texture cost only; the analytic markings SDF, stripe softening and the analytic ball contact
/// shadow are identical on every tier.
/// </summary>
public enum PitchQuality
{
    /// <summary>Integrated-GPU / 4K floor: macro variation and stripes only, no detail texture.</summary>
    Low = 0,

    /// <summary>1080p, 4K/60 fallback: one detail scale at half resolution.</summary>
    Medium = 1,

    /// <summary>Desktop discrete GPU default (2560x1440 @ 144+ FPS target): two detail scales.</summary>
    High = 2
}

/// <summary>
/// Resolved pitch material parameters for one <see cref="PitchQuality"/> tier. Produced once per
/// surface build (never per frame) and pushed to the shader as plain uniforms so the ground stays a
/// single shader with uniform branching and no per-tier shader variants.
/// </summary>
public readonly struct PitchQualitySettings
{
    public PitchQuality Tier { get; init; }

    /// <summary>Detail texture edge length in texels; 0 means the tier binds a 1x1 placeholder.</summary>
    public int DetailTextureSize { get; init; }

    /// <summary>Number of <c>detail_packed</c> scales the shader samples (0, 1 or 2).</summary>
    public int DetailScales { get; init; }

    /// <summary>Macro variation texture edge length in texels (all tiers share 256).</summary>
    public int MacroTextureSize { get; init; }

    /// <summary>Wear mask edge length in texels (unmipped R8; all tiers share 256).</summary>
    public int WearTextureSize { get; init; }

    /// <summary>Strength of the mown-band sheen/normal-tilt term (0 disables it).</summary>
    public float SheenStrength { get; init; }

    /// <summary>Strength of the detail normal + albedo modulation (0 disables it).</summary>
    public float DetailStrength { get; init; }

    /// <summary>Viewport anti-aliasing this tier asks for when the client applies it.</summary>
    public Viewport.ScreenSpaceAAEnum ScreenSpaceAA { get; init; }

    /// <summary>Resolves the shader/texture parameters for a tier.</summary>
    public static PitchQualitySettings For(PitchQuality tier) => tier switch
    {
        PitchQuality.Low => new PitchQualitySettings
        {
            Tier = PitchQuality.Low,
            DetailTextureSize = 0,
            DetailScales = 0,
            MacroTextureSize = 256,
            WearTextureSize = 256,
            SheenStrength = 0f,
            DetailStrength = 0f,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled
        },
        PitchQuality.Medium => new PitchQualitySettings
        {
            Tier = PitchQuality.Medium,
            DetailTextureSize = 512,
            DetailScales = 1,
            MacroTextureSize = 256,
            WearTextureSize = 256,
            SheenStrength = 0.6f,
            DetailStrength = 0.75f,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa
        },
        _ => new PitchQualitySettings
        {
            Tier = PitchQuality.High,
            DetailTextureSize = 1024,
            DetailScales = 2,
            MacroTextureSize = 256,
            WearTextureSize = 256,
            SheenStrength = 1f,
            DetailStrength = 1f,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Smaa
        }
    };
}
