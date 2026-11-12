using Godot;
using FootballSim.Core;

namespace FootballSim.GodotClient;

/// <summary>
/// Builds the shared 3D stadium dressing (environment, grass, markings, goals) for the match
/// and training scenes. Pass the goal signs to build only the goals a scene needs.
/// </summary>
public static class PitchVisualBuilder
{
    public static void Build(Node3D root, PitchDimensions p, params float[] goalSigns)
    {
        BuildEnvironment(root);

        var apron = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(p.Length + 16f, 0.08f, p.Width + 16f) },
            Position = new Vector3(0f, -0.05f, 0f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.11f, 0.30f, 0.14f) }
        };
        root.AddChild(apron);

        int stripeCount = 18;
        float stripeWidth = p.Length / stripeCount;
        var lightGrass = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.50f, 0.22f) };
        var darkGrass = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.44f, 0.19f) };

        for (int i = 0; i < stripeCount; i++)
        {
            float centerX = -p.HalfLength + (i + 0.5f) * stripeWidth;
            var stripe = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(stripeWidth, 0.02f, p.Width) },
                Position = new Vector3(centerX, -0.01f, 0f),
                MaterialOverride = (i % 2 == 0) ? lightGrass : darkGrass
            };
            root.AddChild(stripe);
        }

        var lineMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.96f, 0.96f, 0.96f),
            Roughness = 0.9f
        };
        const float lw = 0.14f;

        AddPitchLine(root, new Vector3(0f, 0.01f, -p.HalfWidth), new Vector3(p.Length + lw, 0.01f, lw), lineMat);
        AddPitchLine(root, new Vector3(0f, 0.01f, p.HalfWidth), new Vector3(p.Length + lw, 0.01f, lw), lineMat);
        AddPitchLine(root, new Vector3(-p.HalfLength, 0.01f, 0f), new Vector3(lw, 0.01f, p.Width + lw), lineMat);
        AddPitchLine(root, new Vector3(p.HalfLength, 0.01f, 0f), new Vector3(lw, 0.01f, p.Width + lw), lineMat);
        AddPitchLine(root, new Vector3(0f, 0.01f, 0f), new Vector3(lw, 0.01f, p.Width), lineMat);

        AddHollowCircle(root, Vector3.Zero, p.CenterCircleRadius, lw, 48, lineMat);
        AddSpotDisc(root, Vector3.Zero, 0.28f, lineMat);

        foreach (float sign in goalSigns)
        {
            float goalLineX = sign * p.HalfLength;
            float penEdgeX = goalLineX - sign * p.PenaltyAreaLength;
            float halfPenW = p.PenaltyAreaWidth * 0.5f;

            AddPitchLine(root, new Vector3(penEdgeX, 0.01f, 0f), new Vector3(lw, 0.01f, p.PenaltyAreaWidth + lw), lineMat);
            AddPitchLine(root, new Vector3(goalLineX - sign * (p.PenaltyAreaLength * 0.5f), 0.01f, -halfPenW), new Vector3(p.PenaltyAreaLength, 0.01f, lw), lineMat);
            AddPitchLine(root, new Vector3(goalLineX - sign * (p.PenaltyAreaLength * 0.5f), 0.01f, halfPenW), new Vector3(p.PenaltyAreaLength, 0.01f, lw), lineMat);

            float gaEdgeX = goalLineX - sign * p.GoalAreaLength;
            float halfGaW = p.GoalAreaWidth * 0.5f;
            AddPitchLine(root, new Vector3(gaEdgeX, 0.01f, 0f), new Vector3(lw, 0.01f, p.GoalAreaWidth + lw), lineMat);
            AddPitchLine(root, new Vector3(goalLineX - sign * (p.GoalAreaLength * 0.5f), 0.01f, -halfGaW), new Vector3(p.GoalAreaLength, 0.01f, lw), lineMat);
            AddPitchLine(root, new Vector3(goalLineX - sign * (p.GoalAreaLength * 0.5f), 0.01f, halfGaW), new Vector3(p.GoalAreaLength, 0.01f, lw), lineMat);

            AddSpotDisc(root, new Vector3(goalLineX - sign * p.PenaltySpotDistance, 0.01f, 0f), 0.24f, lineMat);
            Build3DGoal(root, p, sign);
        }
    }

    private static void BuildEnvironment(Node3D root)
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.10f, 0.12f, 0.16f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.75f, 0.78f, 0.82f),
            AmbientLightEnergy = 0.65f
        };
        root.AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52f, 28f, 0f),
            LightEnergy = 1.15f,
            ShadowEnabled = true
        };
        root.AddChild(sun);
    }

    private static void Build3DGoal(Node3D root, PitchDimensions p, float sideSign)
    {
        float gx = sideSign * p.HalfLength;
        float backX = sideSign * (p.HalfLength + p.GoalDepth);
        float halfGW = p.HalfGoalWidth;
        float gh = p.GoalHeight;

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

        foreach (float zSign in new[] { -1f, 1f })
        {
            var post = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = p.GoalPostRadius, BottomRadius = p.GoalPostRadius, Height = gh },
                Position = new Vector3(gx, gh * 0.5f, zSign * halfGW),
                MaterialOverride = postMat
            };
            root.AddChild(post);

            var backPost = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.04f, Height = gh },
                Position = new Vector3(backX, gh * 0.5f, zSign * halfGW),
                MaterialOverride = postMat
            };
            root.AddChild(backPost);

            var sideNet = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(p.GoalDepth, gh, 0.02f) },
                Position = new Vector3((gx + backX) * 0.5f, gh * 0.5f, zSign * halfGW),
                MaterialOverride = netMat
            };
            root.AddChild(sideNet);
        }

        var crossbar = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = p.GoalPostRadius, BottomRadius = p.GoalPostRadius, Height = p.GoalWidth + p.GoalPostRadius * 2f },
            Position = new Vector3(gx, gh, 0f),
            RotationDegrees = new Vector3(90f, 0f, 0f),
            MaterialOverride = postMat
        };
        root.AddChild(crossbar);

        var backNet = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.02f, gh, p.GoalWidth) },
            Position = new Vector3(backX, gh * 0.5f, 0f),
            MaterialOverride = netMat
        };
        root.AddChild(backNet);

        var topNet = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(p.GoalDepth, 0.02f, p.GoalWidth) },
            Position = new Vector3((gx + backX) * 0.5f, gh, 0f),
            MaterialOverride = netMat
        };
        root.AddChild(topNet);
    }

    private static void AddPitchLine(Node3D root, Vector3 pos, Vector3 size, Material mat)
    {
        root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            Position = pos,
            MaterialOverride = mat
        });
    }

    private static void AddSpotDisc(Node3D root, Vector3 pos, float radius, Material mat)
    {
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.015f },
            Position = pos,
            MaterialOverride = mat
        });
    }

    private static void AddHollowCircle(Node3D root, Vector3 center, float radius, float lineWidth, int segments, Material mat)
    {
        float segLen = (2f * Mathf.Pi * radius) / segments * 1.05f;
        for (int i = 0; i < segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.Pi * 2f;
            float x = center.X + Mathf.Cos(angle) * radius;
            float z = center.Z + Mathf.Sin(angle) * radius;
            var seg = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(lineWidth, 0.01f, segLen) },
                Position = new Vector3(x, 0.01f, z),
                Rotation = new Vector3(0f, -angle, 0f),
                MaterialOverride = mat
            };
            root.AddChild(seg);
        }
    }
}
