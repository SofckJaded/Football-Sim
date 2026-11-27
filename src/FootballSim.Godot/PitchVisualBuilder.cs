using Godot;
using FootballSim.Core;

namespace FootballSim.GodotClient;

/// <summary>
/// Builds the shared 3D stadium dressing (environment, pitch surface, goals) for the match and
/// training scenes. Pass the goal signs to build only the goals a scene needs, and hand the returned
/// <see cref="PitchSurfaceHandle"/> to the scene so it can push the per-frame ball contact shadow.
///
/// The ground itself is built by <see cref="PitchSurfaceBuilder"/> as two flush <c>y = 0.0</c> meshes
///; the old ~105 separate stripe/marking/apron boxes are gone (docs/football-grass.md §5.8, G1).
/// </summary>
public static class PitchVisualBuilder
{
    /// <summary>Sun pitch in degrees; lower than the old -52 for stripe separation and grounded shadows.</summary>
    public const float SunElevationDegrees = -36f;

    /// <summary>Sun yaw in degrees.</summary>
    public const float SunYawDegrees = 28f;

    /// <summary>
    /// Builds the whole pitch dressing under <paramref name="root"/> at the High quality tier and
    /// returns the handle to the ground surface so callers can drive the per-frame shader state.
    /// </summary>
    public static PitchSurfaceHandle Build(Node3D root, PitchDimensions p, params float[] goalSigns) =>
        Build(root, p, PitchQualitySettings.For(PitchQuality.High), goalSigns);

    /// <summary>
    /// Builds the whole pitch dressing under <paramref name="root"/> at an explicit quality tier
    /// (the Settings page's "Pitch detail" row resolves it before the scene builds the pitch).
    /// </summary>
    public static PitchSurfaceHandle Build(Node3D root, PitchDimensions p, PitchQualitySettings quality,
        params float[] goalSigns)
    {
        DirectionalLight3D sun = BuildEnvironment(root);
        Vector3 sunForward = sun.GlobalTransform.Basis.Z.Normalized() * -1f;

        PitchSurfaceHandle surface = PitchSurfaceBuilder.Build(root, p, quality, sunForward);

        if (goalSigns.Length > 0)
            BuildGoals(root, p, goalSigns);
        return surface;
    }

    private static DirectionalLight3D BuildEnvironment(Node3D root)
    {
        // Lightweight procedural sky used only for ambient + reflected IBL (the visible background
        // stays the dark solid colour until the stadium-dressing milestone). Mown bands need real
        // sky grazing reflection to read as turf instead of paint.
        var skyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.28f, 0.42f, 0.62f),
            SkyHorizonColor = new Color(0.62f, 0.68f, 0.74f),
            SkyCurve = 0.12f,
            SkyEnergyMultiplier = 0.85f,
            GroundBottomColor = new Color(0.14f, 0.15f, 0.14f),
            GroundHorizonColor = new Color(0.36f, 0.39f, 0.36f),
            GroundCurve = 0.08f,
            GroundEnergyMultiplier = 0.45f,
            SunAngleMax = 16f,
            SunCurve = 0.08f
        };
        var sky = new Sky
        {
            SkyMaterial = skyMaterial,
            RadianceSize = Sky.RadianceSizeEnum.Size128
        };

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.10f, 0.12f, 0.16f),
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 0.62f,
            AmbientLightEnergy = 0.85f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky
        };
        root.AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(SunElevationDegrees, SunYawDegrees, 0f),
            LightEnergy = 1.30f,
            ShadowEnabled = true,
            // 65 m zoom puts the far touchline 95-125 m from the camera: the old 100 m default cut
            // far-side player shadows off. 150 m with two splits fixes that and halves the shadow
            // pass cost versus the default four splits.
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 150f,
            DirectionalShadowSplit1 = 0.22f,
            DirectionalShadowBlendSplits = true,
            ShadowBias = 0.05f,
            ShadowNormalBias = 1.2f
        };
        sun.AddToGroup("pitch_sun");
        root.AddChild(sun);
        return sun;
    }

    /// <summary>
    /// Builds both goals as two <see cref="MultiMeshInstance3D"/> nodes (all opaque posts and
    /// crossbars; all net panels) so the whole dressing stays well under the 8 draw-call gate
    /// instead of the old 18 separate meshes.
    /// </summary>
    private static void BuildGoals(Node3D root, PitchDimensions p, float[] goalSigns)
    {
        var postMat = new StandardMaterial3D
        {
            AlbedoColor = Colors.White,
            Metallic = 0.3f,
            Roughness = 0.3f
        };
        var netMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.92f, 0.94f, 0.98f, 0.35f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        var unitCylinder = new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 1f, RadialSegments = 10 };
        var unitBox = new BoxMesh { Size = Vector3.One };

        int framesPerGoal = 5;
        int netPanelsPerGoal = 4;
        var posts = new MultiMesh
        {
            Mesh = unitCylinder,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = framesPerGoal * goalSigns.Length
        };
        var nets = new MultiMesh
        {
            Mesh = unitBox,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = netPanelsPerGoal * goalSigns.Length
        };

        int postIndex = 0;
        int netIndex = 0;
        foreach (float sideSign in goalSigns)
        {
            float gx = sideSign * p.HalfLength;
            float backX = sideSign * (p.HalfLength + p.GoalDepth);
            float halfGW = p.HalfGoalWidth;
            float gh = p.GoalHeight;
            float midX = (gx + backX) * 0.5f;

            Basis postScale = ScaleBasis(p.GoalPostRadius, gh, p.GoalPostRadius);
            Basis backPostScale = ScaleBasis(0.04f, gh, 0.04f);
            Basis crossbarScale = ScaleBasis(p.GoalPostRadius, p.GoalWidth + p.GoalPostRadius * 2f, p.GoalPostRadius);

            foreach (float zSign in new[] { -1f, 1f })
            {
                posts.SetInstanceTransform(postIndex++, new Transform3D(postScale, new Vector3(gx, gh * 0.5f, zSign * halfGW)));
                posts.SetInstanceTransform(postIndex++, new Transform3D(backPostScale, new Vector3(backX, gh * 0.5f, zSign * halfGW)));
            }
            // Crossbar: the unit cylinder runs along local +Y, so rotate it onto +Z.
            Basis crossbarBasis = new Basis(Vector3.Right, Mathf.Pi * 0.5f) * crossbarScale;
            posts.SetInstanceTransform(postIndex++, new Transform3D(crossbarBasis, new Vector3(gx, gh, 0f)));

            foreach (float zSign in new[] { -1f, 1f })
            {
                nets.SetInstanceTransform(netIndex++, new Transform3D(
                    ScaleBasis(p.GoalDepth, gh, 0.02f), new Vector3(midX, gh * 0.5f, zSign * halfGW)));
            }
            nets.SetInstanceTransform(netIndex++, new Transform3D(
                ScaleBasis(0.02f, gh, p.GoalWidth), new Vector3(backX, gh * 0.5f, 0f)));
            nets.SetInstanceTransform(netIndex++, new Transform3D(
                ScaleBasis(p.GoalDepth, 0.02f, p.GoalWidth), new Vector3(midX, gh, 0f)));
        }

        root.AddChild(new MultiMeshInstance3D { Name = "GoalFrames", Multimesh = posts, MaterialOverride = postMat });
        root.AddChild(new MultiMeshInstance3D { Name = "GoalNets", Multimesh = nets, MaterialOverride = netMat });
    }

    private static Basis ScaleBasis(float x, float y, float z) =>
        Basis.Identity.Scaled(new Vector3(x, y, z));
}
