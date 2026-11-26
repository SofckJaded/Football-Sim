using Godot;
using FootballSim.Presentation;
using NVector3 = System.Numerics.Vector3;

namespace FootballSim.GodotClient.Visuals;

/// <summary>
/// One footballer's articulated visual node hierarchy: the ground shadow and the optional ground decals sit
/// flat under the root, while the body chain (body → lower → torso → head) inherits the solver's pose.
///
/// <para>
/// <see cref="ApplyPose"/> is the only way the rig is ever moved. It writes the pose onto pre-cached node
/// fields — no node lookups, no material creation, no managed allocations — because it runs for every
/// player on every rendered frame (11v11 = 22 rigs at 60 Hz, twice during free training).
/// </para>
/// </summary>
public sealed partial class PlayerVisualRig : Node3D
{
    // Only the nodes ApplyPose touches are cached. Shorts, torso, head, gaze cue, ring, pointer and the
    // lower pivot are structural: they are parented once by the factory and their transforms are purely
    // inherited, so caching them would be dead state rather than a saved lookup.
    private readonly Node3D _bodyPivot;
    private readonly Node3D _torsoPivot;
    private readonly Node3D _headPivot;
    private readonly MeshInstance3D? _groundShadow;
    private readonly StandardMaterial3D? _shadowMaterial;
    private readonly float _shadowBaseAlpha;
    private readonly MeshInstance3D? _leftBoot;
    private readonly MeshInstance3D? _rightBoot;
    private readonly MeshInstance3D? _leftHand;
    private readonly MeshInstance3D? _rightHand;

    /// <summary>
    /// Takes ownership of the already built and parented parts. Internal on purpose: <see cref="PlayerRigFactory"/>
    /// is the only place that knows which optional micro-parts a <see cref="PlayerVisualStyle"/> enabled, so a
    /// rig can never be assembled half-initialised from the scene code.
    /// </summary>
    internal PlayerVisualRig(in PlayerRigParts parts)
    {
        // Decal siblings first, then the body chain, matching the documented hierarchy order. Children are
        // added here (and nowhere else) so the rig's public surface stays "move me, don't rebuild me".
        if (parts.GroundShadow is not null) AddChild(parts.GroundShadow);
        if (parts.HumanRing is not null) AddChild(parts.HumanRing);
        if (parts.DirPointer is not null) AddChild(parts.DirPointer);
        AddChild(parts.BodyPivot);

        _bodyPivot = parts.BodyPivot;
        _torsoPivot = parts.TorsoPivot;
        _headPivot = parts.HeadPivot;
        _groundShadow = parts.GroundShadow;
        _shadowMaterial = parts.ShadowMaterial;
        _leftBoot = parts.LeftBoot;
        _rightBoot = parts.RightBoot;
        _leftHand = parts.LeftHand;
        _rightHand = parts.RightHand;

        // The resting alpha belongs to the shared shadow material definition; capturing it here keeps one
        // source of truth for "how dark is the disc" while the pose supplies only a 0..1 fade multiplier.
        _shadowBaseAlpha = parts.ShadowMaterial is null ? 1f : parts.ShadowMaterial.AlbedoColor.A;
    }

    /// <summary>
    /// Applies one frame of the solver's <see cref="PlayerPose"/> onto the cached hierarchy.
    ///
    /// <para>
    /// Allocation-free by construction: every write is a primitive or struct assignment, optional parts are
    /// null-guarded because a style may disable them at build time, and the root position/yaw plus each
    /// pivot offset come straight from the pose — the rig never invents motion of its own.
    /// </para>
    /// </summary>
    public void ApplyPose(in PlayerPose pose)
    {
        Position = ToGodot(pose.RootPosition);
        Rotation = new Vector3(0f, pose.RootYaw, 0f);

        _bodyPivot.Position = ToGodot(pose.BodyOffset);
        _bodyPivot.Rotation = ToGodot(pose.BodyRotation);
        _bodyPivot.Scale = ToGodot(pose.BodyScale);

        _torsoPivot.Rotation = ToGodot(pose.TorsoRotation);
        _headPivot.Rotation = ToGodot(pose.HeadRotation);

        if (_leftBoot is not null) _leftBoot.Position = ToGodot(pose.LeftFootLocal);
        if (_rightBoot is not null) _rightBoot.Position = ToGodot(pose.RightFootLocal);
        if (_leftHand is not null) _leftHand.Position = ToGodot(pose.LeftHandLocal);
        if (_rightHand is not null) _rightHand.Position = ToGodot(pose.RightHandLocal);

        if (_groundShadow is not null)
        {
            float shadowScale = pose.ShadowScaleXZ;
            _groundShadow.Scale = new Vector3(shadowScale, 1f, shadowScale);

            if (_shadowMaterial is not null)
            {
                Color shadowColor = _shadowMaterial.AlbedoColor;
                shadowColor.A = _shadowBaseAlpha * Mathf.Clamp(pose.ShadowAlpha, 0f, 1f);
                _shadowMaterial.AlbedoColor = shadowColor;
            }
        }
    }

    /// <summary>Converts the pure-C# presentation vector into Godot's vector. The axes are identical.</summary>
    private static Vector3 ToGodot(NVector3 value) => new(value.X, value.Y, value.Z);
}
