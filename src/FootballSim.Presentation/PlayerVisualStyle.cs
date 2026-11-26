namespace FootballSim.Presentation;

/// <summary>
/// Which optional micro-parts a rig is built with. The recommended (default) style is the plan's
/// Option A: keep the existing torso/shorts/head silhouette, then add the pieces that make motion
/// readable from the broadcast camera — a ground shadow, floating boots, floating hands (bright
/// keeper gloves) and a gaze cue on the head so scanning is visible.
///
/// Every toggle is independent so the silhouette can be reduced to the strict 4-piece model in one
/// line if the art direction changes. The flags are baked into the built rig; changing them rebuilds.
/// </summary>
public readonly struct PlayerVisualStyle
{
    /// <summary>Flat disc under the player: the only way to read jump/dive height from 34 m.</summary>
    public bool ShowShadow { get; init; }

    /// <summary>Small boot meshes that sell stride cadence, kicks and backpedaling.</summary>
    public bool ShowBoots { get; init; }

    /// <summary>Floating hands / gloves: balance flares for outfielders, bright reach for keepers.</summary>
    public bool ShowHands { get; init; }

    /// <summary>Bright keeper gloves instead of skin-toned hands.</summary>
    public bool ShowKeeperGloves { get; init; }

    /// <summary>A small nose/brow wedge on +X so a head turn is visible on the spherical head.</summary>
    public bool ShowGazeCue { get; init; }

    /// <summary>The flat white facing tick at the player's feet.</summary>
    public bool ShowDirPointer { get; init; }

    /// <summary>The cyan ring under the locked human player.</summary>
    public bool ShowHumanRing { get; init; }

    /// <summary>The plan's recommended style (Option A): everything on.</summary>
    public static PlayerVisualStyle Recommended => new()
    {
        ShowShadow = true,
        ShowBoots = true,
        ShowHands = true,
        ShowKeeperGloves = true,
        ShowGazeCue = true,
        ShowDirPointer = true,
        ShowHumanRing = true
    };

    /// <summary>The strict 4-piece silhouette plus only the ground shadow.</summary>
    public static PlayerVisualStyle StrictFourPiece => new()
    {
        ShowShadow = true,
        ShowBoots = false,
        ShowHands = false,
        ShowKeeperGloves = false,
        ShowGazeCue = false,
        ShowDirPointer = true,
        ShowHumanRing = true
    };
}
