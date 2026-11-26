using System.Numerics;

namespace FootballSim.Presentation;

/// <summary>
/// Neutral (rest pose) dimensions of the primitive footballer rig, in metres. One source of truth for
/// both the Godot mesh factory and the pose solver, so a mesh can never drift away from the numbers the
/// dynamics are tuned against.
///
/// The coordinate convention is the player-root frame verified against the pre-existing renderer
/// (<c>yaw = Atan2(-FacingXZ.Y, FacingXZ.X)</c>):
/// <list type="bullet">
/// <item><description><c>+X</c> = forward (facing direction)</description></item>
/// <item><description><c>+Y</c> = up</description></item>
/// <item><description><c>+Z</c> = the player's right side</description></item>
/// </list>
/// Right-handed rotations therefore mean: <c>+RotZ</c> leans BACKWARD, <c>+RotX</c> banks RIGHT,
/// <c>+RotY</c> twists LEFT.
/// </summary>
public static class RigDimensions
{
    // --- Pivots -------------------------------------------------------------------------------
    /// <summary>Waist height: the <c>TorsoPivot</c> hinge above the player root.</summary>
    public const float WaistHeight = 0.72f;

    /// <summary>Neck height relative to the torso pivot (world Y at rest = 1.68 m).</summary>
    public const float NeckHeight = 0.96f;

    /// <summary>Rest height of the ground shadow disc.</summary>
    public const float ShadowHeight = 0.015f;

    /// <summary>Rest height of the flat facing tick.</summary>
    public const float DirPointerHeight = 0.04f;

    /// <summary>Rest height of the locked-human ring.</summary>
    public const float HumanRingHeight = 0.02f;

    // --- Core body meshes ---------------------------------------------------------------------
    public const float ShortsCenterY = 0.36f;
    public const float ShortsTopRadius = 0.28f;
    public const float ShortsBottomRadius = 0.22f;
    public const float ShortsHeight = 0.72f;

    public const float TorsoCenterY = 1.05f;
    public const float TorsoRadius = 0.32f;
    public const float TorsoHeight = 0.92f;

    public const float HeadCenterY = 1.68f;
    public const float HeadRadius = 0.20f;
    public const float HeadHeight = 0.40f;

    // --- Optional micro-parts -----------------------------------------------------------------
    /// <summary>Boot centre height above the turf.</summary>
    public const float BootCenterY = 0.075f;

    /// <summary>Sideways offset of each boot from the centre line.</summary>
    public const float BootSideOffset = 0.15f;

    /// <summary>Forward offset of each boot (feet point forward).</summary>
    public const float BootForwardOffset = 0.06f;

    public const float BootLength = 0.24f;
    public const float BootWidth = 0.13f;
    public const float BootHeight = 0.11f;

    /// <summary>Rest sideways offset of a hand / glove from the torso centre line.</summary>
    public const float HandSideOffset = 0.38f;

    /// <summary>Rest height of a hand / glove above the torso pivot.</summary>
    public const float HandCenterY = 1.02f - WaistHeight;

    /// <summary>Rest forward offset of a hand / glove.</summary>
    public const float HandForwardOffset = 0.05f;

    public const float OutfielderHandRadius = 0.075f;
    public const float KeeperGloveRadius = 0.11f;

    /// <summary>Forward/down offset of the gaze cue (nose / brow wedge) on the head sphere.</summary>
    public const float GazeCueForwardOffset = 0.17f;
    public const float GazeCueRise = 0.05f;
    public const float GazeCueLength = 0.09f;
    public const float GazeCueWidth = 0.17f;
    public const float GazeCueHeight = 0.07f;

    /// <summary>Radius of the flat ground shadow disc at rest.</summary>
    public const float ShadowRadius = 0.42f;

    /// <summary>Outer radius of the locked-human ring.</summary>
    public const float HumanRingRadius = 0.68f;

    // --- Derived rest positions used by the solver --------------------------------------------
    /// <summary>Rest local position of the left boot relative to the lower pivot.</summary>
    public static readonly Vector3 LeftFootRest = new(BootForwardOffset, BootCenterY, -BootSideOffset);

    /// <summary>Rest local position of the right boot relative to the lower pivot.</summary>
    public static readonly Vector3 RightFootRest = new(BootForwardOffset, BootCenterY, BootSideOffset);

    /// <summary>Rest local position of the left hand relative to the torso pivot.</summary>
    public static readonly Vector3 LeftHandRest = new(HandForwardOffset, HandCenterY, -HandSideOffset);

    /// <summary>Rest local position of the right hand relative to the torso pivot.</summary>
    public static readonly Vector3 RightHandRest = new(HandForwardOffset, HandCenterY, HandSideOffset);

    /// <summary>Rest local position of the head mesh relative to the head pivot (centred).</summary>
    public static readonly Vector3 HeadRest = Vector3.Zero;
}
