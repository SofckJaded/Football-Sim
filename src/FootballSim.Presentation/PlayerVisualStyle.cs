namespace FootballSim.Presentation;

/// <summary>
/// Which optional micro-parts a rig is built with. The default style is the plan's Option A shape with two
/// owner decisions applied (2026-11-26): the leg/shorts cylinder is kept exactly as the original renderer drew
/// it, and the animated boots are OFF — the floating feet swinging with the stride/kick read as out of place
/// against the rigid leg cylinder. Every piece stays an independent toggle: <see cref="WithBoots"/> restores
/// the animated feet, <see cref="NoLegs"/> drops the leg cylinder instead.
///
/// The flags are baked into the built rig; changing them rebuilds the rig on the next scene (re)start.
/// </summary>
public readonly struct PlayerVisualStyle
{
    /// <summary>Flat disc under the player: the only way to read jump/dive height from 34 m.</summary>
    public bool ShowShadow { get; init; }

    /// <summary>The leg/shorts cylinder below the waist: the original model's rigid lower body.</summary>
    public bool ShowLegs { get; init; }

    /// <summary>
    /// Small boot meshes. OFF by default (owner feedback 2026-11-26): rigid leg cylinder + independently swinging
    /// feet looks wrong, so the default rests on the original silhouette. On, they sell stride and kick arcs.
    /// </summary>
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

    /// <summary>Style used by the match and free training unless a scene deliberately selects another one.</summary>
    public static PlayerVisualStyle Recommended => new()
    {
        ShowShadow = true,
        ShowLegs = true,
        ShowBoots = false,
        ShowHands = true,
        ShowKeeperGloves = true,
        ShowGazeCue = true,
        ShowDirPointer = true,
        ShowHumanRing = true
    };

    /// <summary>Keeps the animated floating boots for stride/kick readability (the earlier experiment).</summary>
    public static PlayerVisualStyle WithBoots => new()
    {
        ShowShadow = true,
        ShowLegs = true,
        ShowBoots = true,
        ShowHands = true,
        ShowKeeperGloves = true,
        ShowGazeCue = true,
        ShowDirPointer = true,
        ShowHumanRing = true
    };

    /// <summary>Drops the rigid leg cylinder, leaving a floating torso/head with animated boots.</summary>
    public static PlayerVisualStyle NoLegs => new()
    {
        ShowShadow = true,
        ShowLegs = false,
        ShowBoots = true,
        ShowHands = true,
        ShowKeeperGloves = true,
        ShowGazeCue = true,
        ShowDirPointer = true,
        ShowHumanRing = true
    };

    /// <summary>The strict original 4-piece silhouette (legs/shorts, torso, head, facing tick) plus shadow.</summary>
    public static PlayerVisualStyle StrictFourPiece => new()
    {
        ShowShadow = true,
        ShowLegs = true,
        ShowBoots = false,
        ShowHands = false,
        ShowKeeperGloves = false,
        ShowGazeCue = false,
        ShowDirPointer = true,
        ShowHumanRing = true
    };
}
