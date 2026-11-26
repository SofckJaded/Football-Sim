using System.Numerics;

namespace FootballSim.Presentation;

/// <summary>
/// The complete visual pose of one footballer for one rendered frame. Produced by
/// <see cref="PlayerDynamicsSolver"/> and consumed by the Godot rig adapter, which only assigns these
/// values onto pivot nodes — it contains no physics, so a future skeletal rig can reinterpret the same
/// pose (or read a richer one) without touching the solver.
///
/// All rotations are Euler angles in <b>radians</b> in the player-root frame (+X forward, +Y up,
/// +Z the player's right), applied in Y-X-Z order by the rig. All offsets are in metres.
/// </summary>
public struct PlayerPose
{
    /// <summary>
    /// World-space root position on the <b>ground plane</b> (Y is always 0). The authoritative jump height is
    /// added to <see cref="BodyOffset"/> instead, so decals parented to the root (ground shadow, human ring,
    /// facing tick) stay flat on the grass while the player is airborne.
    /// </summary>
    public Vector3 RootPosition;

    /// <summary>Smoothed facing yaw about +Y, radians. Removes one-tick facing pops.</summary>
    public float RootYaw;

    /// <summary>
    /// Extra translation of the body pivot in root-local axes: X = forward, Y = up (crouch / dive arc),
    /// Z = right. The root itself stays on the ground plane.
    /// </summary>
    public Vector3 BodyOffset;

    /// <summary>
    /// Whole-body rotation: X = roll/bank in radians (+ banks right), Y = twist (+ turns left),
    /// Z = pitch (+ leans backward, - leans forward). Dive roll/pitch are folded in by the solver.
    /// </summary>
    public Vector3 BodyRotation;

    /// <summary>Volume-preserving squash/stretch applied to the body pivot (feet stay planted at Y = 0).</summary>
    public Vector3 BodyScale;

    /// <summary>Waist pivot rotation: X = side tilt, Y = counter-twist, Z = waist bend.</summary>
    public Vector3 TorsoRotation;

    /// <summary>Neck pivot rotation: X = lateral tilt, Y = head yaw (scan), Z = nod.</summary>
    public Vector3 HeadRotation;

    /// <summary>Left boot position relative to the lower (hips) pivot, in root axes.</summary>
    public Vector3 LeftFootLocal;

    /// <summary>Right boot position relative to the lower (hips) pivot, in root axes.</summary>
    public Vector3 RightFootLocal;

    /// <summary>Left hand / glove position relative to the torso pivot.</summary>
    public Vector3 LeftHandLocal;

    /// <summary>Right hand / glove position relative to the torso pivot.</summary>
    public Vector3 RightHandLocal;

    /// <summary>Ground shadow XZ scale (shrinks and fades as the player rises).</summary>
    public float ShadowScaleXZ;

    /// <summary>Ground shadow alpha multiplier in [0, 1].</summary>
    public float ShadowAlpha;

    /// <summary>The neutral standing pose: what a just-spawned player shows before any dynamics run.</summary>
    public static PlayerPose Neutral => new()
    {
        RootPosition = Vector3.Zero,
        RootYaw = 0f,
        BodyOffset = Vector3.Zero,
        BodyRotation = Vector3.Zero,
        BodyScale = Vector3.One,
        TorsoRotation = Vector3.Zero,
        HeadRotation = Vector3.Zero,
        LeftFootLocal = RigDimensions.LeftFootRest,
        RightFootLocal = RigDimensions.RightFootRest,
        LeftHandLocal = RigDimensions.LeftHandRest,
        RightHandLocal = RigDimensions.RightHandRest,
        ShadowScaleXZ = 1f,
        ShadowAlpha = 1f
    };
}
