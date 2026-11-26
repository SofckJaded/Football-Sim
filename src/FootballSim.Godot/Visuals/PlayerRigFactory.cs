using Godot;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Presentation;
using NVector3 = System.Numerics.Vector3;

namespace FootballSim.GodotClient.Visuals;

/// <summary>
/// Builds <see cref="PlayerVisualRig"/> instances and owns the resources they share.
///
/// <para>
/// WHY a factory: the match scene and free training each used to carry their own copy of the flat four-mesh
/// player builder, so a silhouette tweak had to be made twice and the neutral dimensions could drift apart.
/// This type is the one place where a rig is assembled. Materials and meshes that carry no per-player state
/// are built once in the constructor and shared by every rig; only the ground-shadow material is duplicated
/// per rig because its alpha is animated independently by the pose.
/// </para>
/// </summary>
public sealed class PlayerRigFactory
{
    // Wall-clock/GPU state is built once per factory, not once per player: 22 players share the same
    // material and mesh resources, so the renderer allocates a handful of resources per match rather than
    // a couple of hundred, and one factory instance is cheap to create in both scenes.

    // --- Shared colours, copied verbatim from the pre-existing flat player renderer --------------------
    private static readonly Color HomeJerseyColor = new(0.12f, 0.38f, 0.90f);
    private static readonly Color HomeKeeperJerseyColor = new(0.15f, 0.72f, 0.35f);
    private static readonly Color AwayJerseyColor = new(0.88f, 0.16f, 0.16f);
    private static readonly Color AwayKeeperJerseyColor = new(0.92f, 0.55f, 0.12f);
    private static readonly Color HomeShortsColor = new(0.95f, 0.95f, 0.95f);
    private static readonly Color AwayShortsColor = new(0.14f, 0.15f, 0.18f);
    private static readonly Color SkinColor = new(0.88f, 0.72f, 0.58f);
    private static readonly Color BootColor = new(0.08f, 0.08f, 0.10f);
    private static readonly Color KeeperGloveColor = new(0.98f, 0.85f, 0.15f);
    private static readonly Color HumanRingColor = new(0.0f, 0.95f, 1.0f);
    private static readonly Color HumanRingEmissionColor = new(0.0f, 0.85f, 1.0f);
    private static readonly Color GazeCueColor = new(0.10f, 0.08f, 0.07f);

    /// <summary>
    /// Resting alpha of the ground shadow. The pose's <c>ShadowAlpha</c> is a 0..1 multiplier on top of it,
    /// so a planted player shows the same 0.55 disc as the pre-existing ball shadow and only lifting off
    /// the turf fades it out.
    /// </summary>
    private const float ShadowRestAlpha = 0.55f;

    // Decoration dimensions that the body-dimension source of truth deliberately does not own: the facing
    // tick and the ring are pre-existing marker art, not anatomy.
    private const float ShadowDiscThickness = 0.01f;
    private const float HumanRingThickness = 0.03f;
    private const float DirPointerForwardOffset = 0.42f;
    private const float DirPointerLength = 0.45f;
    private const float DirPointerThickness = 0.04f;
    private const float DirPointerWidth = 0.14f;

    private readonly StandardMaterial3D _homeJersey;
    private readonly StandardMaterial3D _homeKeeperJersey;
    private readonly StandardMaterial3D _awayJersey;
    private readonly StandardMaterial3D _awayKeeperJersey;
    private readonly StandardMaterial3D _homeShorts;
    private readonly StandardMaterial3D _awayShorts;
    private readonly StandardMaterial3D _skin;
    private readonly StandardMaterial3D _boots;
    private readonly StandardMaterial3D _keeperGloves;
    private readonly StandardMaterial3D _humanRing;
    private readonly StandardMaterial3D _gazeCue;
    private readonly StandardMaterial3D _shadowBase;

    private readonly CylinderMesh _shadowMesh;
    private readonly CylinderMesh _humanRingMesh;
    private readonly BoxMesh _dirPointerMesh;
    private readonly CylinderMesh _shortsMesh;
    private readonly BoxMesh _bootMesh;
    private readonly CapsuleMesh _torsoMesh;
    private readonly SphereMesh _outfielderHandMesh;
    private readonly SphereMesh _keeperGloveMesh;
    private readonly SphereMesh _headMesh;
    private readonly BoxMesh _gazeCueMesh;

    /// <summary>
    /// Pre-builds every shared material and mesh. Called once per scene (the <c>PlayerVisualManager</c> owns
    /// one factory) and again only when a scene reloads, never per player.
    /// </summary>
    public PlayerRigFactory()
    {
        _homeJersey = CreateSolidMaterial(HomeJerseyColor);
        _homeKeeperJersey = CreateSolidMaterial(HomeKeeperJerseyColor);
        _awayJersey = CreateSolidMaterial(AwayJerseyColor);
        _awayKeeperJersey = CreateSolidMaterial(AwayKeeperJerseyColor);
        _homeShorts = CreateSolidMaterial(HomeShortsColor);
        _awayShorts = CreateSolidMaterial(AwayShortsColor);
        _skin = CreateSolidMaterial(SkinColor);
        _boots = CreateSolidMaterial(BootColor);
        _gazeCue = CreateSolidMaterial(GazeCueColor);

        // Bright gloves are the keeper's main readability cue from the broadcast height, so they glow a
        // little rather than relying on albedo alone under the pitch lighting.
        _keeperGloves = new StandardMaterial3D
        {
            AlbedoColor = KeeperGloveColor,
            EmissionEnabled = true,
            Emission = KeeperGloveColor,
            EmissionEnergyMultiplier = 0.35f
        };

        _humanRing = new StandardMaterial3D
        {
            AlbedoColor = HumanRingColor,
            EmissionEnabled = true,
            Emission = HumanRingEmissionColor,
            EmissionEnergyMultiplier = 1.5f
        };

        // One shared definition; each rig duplicates it because only the alpha is per-player animated.
        _shadowBase = new StandardMaterial3D
        {
            AlbedoColor = new Color(0f, 0f, 0f, ShadowRestAlpha),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };

        _shadowMesh = new CylinderMesh
        {
            TopRadius = RigDimensions.ShadowRadius,
            BottomRadius = RigDimensions.ShadowRadius,
            Height = ShadowDiscThickness
        };
        _humanRingMesh = new CylinderMesh
        {
            TopRadius = RigDimensions.HumanRingRadius,
            BottomRadius = RigDimensions.HumanRingRadius,
            Height = HumanRingThickness
        };
        _dirPointerMesh = new BoxMesh
        {
            Size = new Vector3(DirPointerLength, DirPointerThickness, DirPointerWidth)
        };
        _shortsMesh = new CylinderMesh
        {
            TopRadius = RigDimensions.ShortsTopRadius,
            BottomRadius = RigDimensions.ShortsBottomRadius,
            Height = RigDimensions.ShortsHeight
        };
        _bootMesh = new BoxMesh
        {
            Size = new Vector3(RigDimensions.BootLength, RigDimensions.BootHeight, RigDimensions.BootWidth)
        };
        _torsoMesh = new CapsuleMesh
        {
            Radius = RigDimensions.TorsoRadius,
            Height = RigDimensions.TorsoHeight
        };
        _outfielderHandMesh = new SphereMesh
        {
            Radius = RigDimensions.OutfielderHandRadius,
            Height = RigDimensions.OutfielderHandRadius * 2f
        };
        _keeperGloveMesh = new SphereMesh
        {
            Radius = RigDimensions.KeeperGloveRadius,
            Height = RigDimensions.KeeperGloveRadius * 2f
        };
        _headMesh = new SphereMesh
        {
            Radius = RigDimensions.HeadRadius,
            Height = RigDimensions.HeadHeight
        };
        _gazeCueMesh = new BoxMesh
        {
            Size = new Vector3(RigDimensions.GazeCueLength, RigDimensions.GazeCueHeight, RigDimensions.GazeCueWidth)
        };
    }

    /// <summary>
    /// Builds one fully parented rig for a participant. The style is baked in: a part that is toggled off is
    /// simply not created, and <see cref="PlayerVisualRig.ApplyPose"/> null-guards it for the rig's lifetime.
    /// </summary>
    /// <param name="player">The simulated footballer whose team, role and id decide the kit and rig name.</param>
    /// <param name="isHuman">True for the locked human player, who gets the ground ring when the style allows it.</param>
    /// <param name="style">Which optional micro-parts to build.</param>
    public PlayerVisualRig Create(FootballerState player, bool isHuman, PlayerVisualStyle style)
    {
        bool isKeeper = player.Role == TacticalRole.Goalkeeper;

        StandardMaterial3D jerseyMaterial = player.Team == TeamId.Home
            ? (isKeeper ? _homeKeeperJersey : _homeJersey)
            : (isKeeper ? _awayKeeperJersey : _awayJersey);
        StandardMaterial3D shortsMaterial = player.Team == TeamId.Home ? _homeShorts : _awayShorts;
        StandardMaterial3D handMaterial = isKeeper && style.ShowKeeperGloves ? _keeperGloves : _skin;
        Mesh handMesh = isKeeper && style.ShowKeeperGloves ? _keeperGloveMesh : _outfielderHandMesh;

        // --- Body pivot chain --------------------------------------------------------------------------
        Node3D bodyPivot = new() { Name = "BodyPivot" };
        Node3D lowerPivot = new() { Name = "LowerPivot" };

        // Owner decision 2026-11-26: the tall leg/shorts cylinder reads as trousers and is removed from the
        // default silhouette. It stays a style toggle so the old look is one flag away.
        if (style.ShowLegs)
        {
            MeshInstance3D shortsMesh = new()
            {
                Name = "ShortsMesh",
                Mesh = _shortsMesh,
                Position = new Vector3(0f, RigDimensions.ShortsCenterY, 0f),
                MaterialOverride = shortsMaterial
            };
            lowerPivot.AddChild(shortsMesh);
        }

        MeshInstance3D? leftBoot = null;
        MeshInstance3D? rightBoot = null;
        if (style.ShowBoots)
        {
            leftBoot = CreatePart("LeftBoot", _bootMesh, _boots, ToGodot(RigDimensions.LeftFootRest));
            rightBoot = CreatePart("RightBoot", _bootMesh, _boots, ToGodot(RigDimensions.RightFootRest));
            lowerPivot.AddChild(leftBoot);
            lowerPivot.AddChild(rightBoot);
        }

        Node3D torsoPivot = new()
        {
            Name = "TorsoPivot",
            Position = new Vector3(0f, RigDimensions.WaistHeight, 0f)
        };

        MeshInstance3D torsoMesh = new()
        {
            Name = "TorsoMesh",
            Mesh = _torsoMesh,
            Position = new Vector3(0f, RigDimensions.TorsoCenterY - RigDimensions.WaistHeight, 0f),
            MaterialOverride = jerseyMaterial
        };
        torsoPivot.AddChild(torsoMesh);

        MeshInstance3D? leftHand = null;
        MeshInstance3D? rightHand = null;
        if (style.ShowHands)
        {
            leftHand = CreatePart("LeftHand", handMesh, handMaterial, ToGodot(RigDimensions.LeftHandRest));
            rightHand = CreatePart("RightHand", handMesh, handMaterial, ToGodot(RigDimensions.RightHandRest));
            torsoPivot.AddChild(leftHand);
            torsoPivot.AddChild(rightHand);
        }

        Node3D headPivot = new()
        {
            Name = "HeadPivot",
            Position = new Vector3(0f, RigDimensions.NeckHeight, 0f)
        };
        MeshInstance3D headMesh = new()
        {
            Name = "HeadMesh",
            Mesh = _headMesh,
            Position = new Vector3(RigDimensions.HeadRest.X, RigDimensions.HeadRest.Y, RigDimensions.HeadRest.Z),
            MaterialOverride = _skin
        };
        headPivot.AddChild(headMesh);

        if (style.ShowGazeCue)
        {
            MeshInstance3D gazeCue = new()
            {
                Name = "GazeCue",
                Mesh = _gazeCueMesh,
                Position = new Vector3(
                    RigDimensions.GazeCueForwardOffset,
                    RigDimensions.GazeCueRise,
                    0f),
                MaterialOverride = _gazeCue
            };
            headPivot.AddChild(gazeCue);
        }

        torsoPivot.AddChild(headPivot);
        bodyPivot.AddChild(lowerPivot);
        bodyPivot.AddChild(torsoPivot);

        // --- Root-level decals (flat on the turf, never inherit the body chain) -------------------------
        MeshInstance3D? groundShadow = null;
        StandardMaterial3D? shadowMaterial = null;
        if (style.ShowShadow)
        {
            // Duplicated per rig because ApplyPose animates the alpha from the solver's height fade.
            shadowMaterial = (StandardMaterial3D)_shadowBase.Duplicate();
            groundShadow = new MeshInstance3D
            {
                Name = "GroundShadow",
                Mesh = _shadowMesh,
                Position = new Vector3(0f, RigDimensions.ShadowHeight, 0f),
                MaterialOverride = shadowMaterial,
                // A shadow disc must not cast a shadow of its own: it is a height read-out, not geometry.
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
        }

        MeshInstance3D? humanRing = null;
        if (isHuman && style.ShowHumanRing)
        {
            humanRing = new MeshInstance3D
            {
                Name = "HumanRing",
                Mesh = _humanRingMesh,
                Position = new Vector3(0f, RigDimensions.HumanRingHeight, 0f),
                MaterialOverride = _humanRing
            };
        }

        MeshInstance3D? dirPointer = null;
        if (style.ShowDirPointer)
        {
            dirPointer = CreatePart(
                "DirPointer",
                _dirPointerMesh,
                _homeShorts,
                new Vector3(DirPointerForwardOffset, RigDimensions.DirPointerHeight, 0f));
        }

        PlayerRigParts parts = new()
        {
            GroundShadow = groundShadow,
            ShadowMaterial = shadowMaterial,
            HumanRing = humanRing,
            DirPointer = dirPointer,
            BodyPivot = bodyPivot,
            TorsoPivot = torsoPivot,
            HeadPivot = headPivot,
            LeftBoot = leftBoot,
            RightBoot = rightBoot,
            LeftHand = leftHand,
            RightHand = rightHand
        };

        PlayerVisualRig rig = new(in parts) { Name = $"Player{player.Id}" };
        return rig;
    }

    private static MeshInstance3D CreatePart(string name, Mesh mesh, Material material, Vector3 position) => new()
    {
        Name = name,
        Mesh = mesh,
        Position = position,
        MaterialOverride = material
    };

    private static StandardMaterial3D CreateSolidMaterial(Color color) => new() { AlbedoColor = color };

    /// <summary>Converts the pure-C# presentation vector into Godot's vector. The axes are identical.</summary>
    private static Vector3 ToGodot(NVector3 value) => new(value.X, value.Y, value.Z);
}

/// <summary>
/// The handles <see cref="PlayerVisualRig"/> keeps from the hierarchy the factory built. Optional members are
/// null when the <see cref="PlayerVisualStyle"/> disabled that part, which is exactly the null-guard contract
/// <see cref="PlayerVisualRig.ApplyPose"/> follows.
///
/// <para>
/// Internal to the assembly: the factory is the only producer and the rig the only consumer, so the scene code
/// cannot construct a rig out of arbitrary nodes.
/// </para>
/// </summary>
internal readonly struct PlayerRigParts
{
    public MeshInstance3D? GroundShadow { get; init; }
    public StandardMaterial3D? ShadowMaterial { get; init; }
    public MeshInstance3D? HumanRing { get; init; }
    public MeshInstance3D? DirPointer { get; init; }
    public Node3D BodyPivot { get; init; }
    public Node3D TorsoPivot { get; init; }
    public Node3D HeadPivot { get; init; }
    public MeshInstance3D? LeftBoot { get; init; }
    public MeshInstance3D? RightBoot { get; init; }
    public MeshInstance3D? LeftHand { get; init; }
    public MeshInstance3D? RightHand { get; init; }
}
