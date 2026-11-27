using System.Collections.Generic;
using Godot;
using FootballSim.Core;

namespace FootballSim.GodotClient;

/// <summary>
/// Handle to the built pitch ground surface. The match and training scenes keep this and push the
/// per-frame shader state (ball contact shadow in G2, actor slots/wear in G4) without owning the
/// material; the two ground meshes themselves are plain scene nodes
/// (docs/football-grass.md Â§5.2, Â§5.8).
/// </summary>
public sealed class PitchSurfaceHandle
{
    /// <summary>The inner turf + run-off material (the shader the scenes push uniforms into).</summary>
    public required ShaderMaterial Material { get; init; }

    /// <summary>The detailed inner turf + run-off plane.</summary>
    public required MeshInstance3D Inner { get; init; }

    /// <summary>The flat outer apron skirt ring.</summary>
    public required MeshInstance3D Apron { get; init; }

    /// <summary>Cached procedural textures for this pitch and tier.</summary>
    public required PitchTextureSet Textures { get; init; }

    /// <summary>World-space direction the sunlight travels (unit; y negative for a daylight sun).</summary>
    public Vector3 SunForward { get; init; } = new(0.3f, -0.8f, -0.5f);

    /// <summary>Shows or hides the whole ground surface (bench A/B for the grass increment).</summary>
    public void SetVisible(bool visible)
    {
        Inner.Visible = visible;
        Apron.Visible = visible;
    }

    /// <summary>Toggles the analytic markings SDF without rebuilding the material (bench A/B).</summary>
    public void SetMarkingsEnabled(bool enabled) =>
        Material.SetShaderParameter("markings_enabled", enabled ? 1 : 0);

    /// <summary>Sets the shader's uniform-branched quality tier (bench A/B; tiers normally build once).</summary>
    public void SetQualityLevel(int level) =>
        Material.SetShaderParameter("quality_level", Mathf.Clamp(level, 0, 2));

    /// <summary>
    /// Places the analytic ball contact shadow under a ball at <paramref name="ballCentre"/>, offset
    /// along the sun direction by the ball's height so the patch sits where the sun would cast it.
    /// </summary>
    public void SetBallShadow(Vector3 ballCentre, float ballRadius)
    {
        float height = Mathf.Max(0f, ballCentre.Y - ballRadius);
        float t = SunForward.Y < -0.05f ? height / -SunForward.Y : 0f;
        Vector3 ground = new(ballCentre.X + SunForward.X * t, 0f, ballCentre.Z + SunForward.Z * t);
        Material.SetShaderParameter("ball_pos_height", new Vector4(ground.X, ground.Z, 0f, height));
    }

    /// <summary>Disables the analytic ball contact shadow.</summary>
    public void ClearBallShadow() =>
        Material.SetShaderParameter("ball_pos_height", new Vector4(0f, 0f, 0f, -1f));
}

/// <summary>
/// Builds the pitch ground as two flush <c>y = 0.0</c> opaque meshes sharing one material design: an
/// inner detailed turf + run-off plane carrying <c>pitch_surface.gdshader</c>, and a cheap flat outer
/// apron skirt ring out to 400 x 300 m. Splitting the mesh (instead of an in-shader apron early-out)
/// keeps GPU 2x2 quad derivatives valid at the seam and keeps the heavy shader off outer pixels
/// (docs/football-grass.md Â§5.3).
/// </summary>
public static class PitchSurfaceBuilder
{
    private const string SurfaceShaderPath = "res://Pitch/pitch_surface.gdshader";
    private const string ApronShaderPath = "res://Pitch/pitch_apron.gdshader";

    /// <summary>Continuous mown run-off around every line before the flat apron ring takes over, metres.</summary>
    public const float RunOffWidth = 20f;

    /// <summary>Width over which the inner plane fades into the apron colour so the seam is invisible.</summary>
    public const float ApronFadeWidth = 6f;

    /// <summary>Mowing band width: an even band count centred on the origin so lines land on boundaries.</summary>
    public static float StripeWidth(PitchDimensions pitch)
    {
        float bands = Mathf.Max(2f, 2f * Mathf.Round(pitch.Length / 11f));
        return pitch.Length / bands;
    }

    /// <summary>Builds the two ground meshes under <paramref name="root"/> and returns their handle.</summary>
    public static PitchSurfaceHandle Build(Node3D root, PitchDimensions pitch, PitchQualitySettings quality,
        Vector3 sunForward)
    {
        PitchTextureSet textures = PitchTextureFactory.Get(pitch, quality);
        Shader surfaceShader = GD.Load<Shader>(SurfaceShaderPath);
        Shader apronShader = GD.Load<Shader>(ApronShaderPath);

        var material = new ShaderMaterial { Shader = surfaceShader };
        ApplySurfaceParameters(material, pitch, quality, textures);

        float innerWidth = pitch.Length + 2f * RunOffWidth;
        float innerDepth = pitch.Width + 2f * RunOffWidth;
        var inner = new MeshInstance3D
        {
            Name = "PitchTurf",
            Mesh = new PlaneMesh { Size = new Vector2(innerWidth, innerDepth), SubdivideWidth = 1, SubdivideDepth = 1 },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = Vector3.Zero
        };
        inner.AddToGroup("pitch_ground");
        root.AddChild(inner);

        var apronMaterial = new ShaderMaterial { Shader = apronShader };
        apronMaterial.SetShaderParameter("apron_color", ApronColor);
        apronMaterial.SetShaderParameter("apron_far_color", new Color(0.128f, 0.224f, 0.137f));
        apronMaterial.SetShaderParameter("macro_noise", textures.MacroNoise);
        var apron = new MeshInstance3D
        {
            Name = "PitchApron",
            Mesh = BuildApronRing(innerWidth, innerDepth),
            MaterialOverride = apronMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = Vector3.Zero
        };
        apron.AddToGroup("pitch_ground");
        root.AddChild(apron);

        return new PitchSurfaceHandle
        {
            Material = material,
            Inner = inner,
            Apron = apron,
            Textures = textures,
            SunForward = sunForward
        };
    }

    /// <summary>Flat colour the inner plane fades to at its perimeter; must match the apron shader.</summary>
    public static readonly Color ApronColor = new(0.158f, 0.302f, 0.161f);

    /// <summary>Pushes the geometry/palette/tier uniforms into the inner turf material (once per build).</summary>
    public static void ApplySurfaceParameters(ShaderMaterial material, PitchDimensions pitch,
        PitchQualitySettings quality, PitchTextureSet textures)
    {
        material.SetShaderParameter("pitch_half_size", new Vector2(pitch.HalfLength, pitch.HalfWidth));
        material.SetShaderParameter("area_metrics", new Vector4(
            pitch.PenaltyAreaLength,
            pitch.PenaltyAreaWidth * 0.5f,
            pitch.GoalAreaLength,
            pitch.GoalAreaWidth * 0.5f));
        material.SetShaderParameter("arc_metrics", new Vector4(
            pitch.CenterCircleRadius,
            pitch.PenaltySpotDistance,
            1f,
            0.22f));
        material.SetShaderParameter("stripe_width", StripeWidth(pitch));
        material.SetShaderParameter("line_width", 0.12f);
        material.SetShaderParameter("mow_lean_axis", new Vector2(0f, 1f));
        material.SetShaderParameter("run_off_width", RunOffWidth);
        material.SetShaderParameter("apron_fade_width", ApronFadeWidth);

        material.SetShaderParameter("turf_light", new Color(0.182f, 0.396f, 0.153f));
        material.SetShaderParameter("turf_dark", new Color(0.172f, 0.374f, 0.145f));
        material.SetShaderParameter("paint_color", new Color(0.878f, 0.878f, 0.845f));
        material.SetShaderParameter("soil_color", new Color(0.290f, 0.278f, 0.192f));
        material.SetShaderParameter("apron_color", ApronColor);

        material.SetShaderParameter("detail_packed", textures.DetailPacked);
        material.SetShaderParameter("macro_noise", textures.MacroNoise);
        material.SetShaderParameter("wear_map", textures.WearMap);

        material.SetShaderParameter("quality_level", (int)quality.Tier);
        material.SetShaderParameter("detail_strength", quality.DetailStrength);
        material.SetShaderParameter("sheen_strength", quality.SheenStrength);
        material.SetShaderParameter("ball_pos_height", new Vector4(0f, 0f, 0f, -1f));
    }

    /// <summary>Builds the 8-triangle skirt ring: four quads framing the inner plane with an exact cutout.</summary>
    private static ArrayMesh BuildApronRing(float innerWidth, float innerDepth)
    {
        float outerWidth = Mathf.Max(400f, innerWidth + 240f);
        float outerDepth = Mathf.Max(300f, innerDepth + 180f);
        float ihx = innerWidth * 0.5f;
        float ihz = innerDepth * 0.5f;
        float ohx = outerWidth * 0.5f;
        float ohz = outerDepth * 0.5f;

        var vertices = new Vector3[16];
        var normals = new Vector3[16];
        var indices = new int[24];
        int v = 0;
        int i = 0;

        // North (z negative) and south strips run the full outer width.
        AddQuad(vertices, normals, indices, ref v, ref i,
            new Vector3(-ohx, 0f, -ohz), new Vector3(ohx, 0f, -ohz),
            new Vector3(ohx, 0f, -ihz), new Vector3(-ohx, 0f, -ihz));
        AddQuad(vertices, normals, indices, ref v, ref i,
            new Vector3(-ohx, 0f, ihz), new Vector3(ohx, 0f, ihz),
            new Vector3(ohx, 0f, ohz), new Vector3(-ohx, 0f, ohz));
        // West and east strips fill the sides between the two.
        AddQuad(vertices, normals, indices, ref v, ref i,
            new Vector3(-ohx, 0f, -ihz), new Vector3(-ihx, 0f, -ihz),
            new Vector3(-ihx, 0f, ihz), new Vector3(-ohx, 0f, ihz));
        AddQuad(vertices, normals, indices, ref v, ref i,
            new Vector3(ihx, 0f, -ihz), new Vector3(ohx, 0f, -ihz),
            new Vector3(ohx, 0f, ihz), new Vector3(ihx, 0f, ihz));

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh { ResourceName = "PitchApronRing" };
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static void AddQuad(Vector3[] vertices, Vector3[] normals, int[] indices, ref int v, ref int i,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        vertices[v] = a;
        vertices[v + 1] = b;
        vertices[v + 2] = c;
        vertices[v + 3] = d;
        for (int n = 0; n < 4; n++)
            normals[v + n] = Vector3.Up;
        indices[i] = v;
        indices[i + 1] = v + 1;
        indices[i + 2] = v + 2;
        indices[i + 3] = v;
        indices[i + 4] = v + 2;
        indices[i + 5] = v + 3;
        v += 4;
        i += 6;
    }
}

