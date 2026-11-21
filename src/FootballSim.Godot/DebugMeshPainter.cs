using System;
using Godot;

namespace FootballSim.GodotClient;

/// <summary>
/// Immediate-mode ground-plane painting for the on-pitch AI debug view.
///
/// The whole overlay is one <see cref="ImmediateMesh"/> under one <see cref="MeshInstance3D"/>: thick
/// lines, discs, rings, arrows and wedge fills are emitted as triangles with per-vertex colour. That
/// keeps the debug scene at a single draw call and avoids a node pool that would have to be resized as
/// contracts come and go. It is only rebuilt while debug mode is enabled.
///
/// Geometry is in world space (the parent node is the match root, whose children are all at the origin).
/// Y is nudged per layer by the caller so the ground heat map never z-fights with the rings drawn on top
/// of it.
/// </summary>
public sealed class DebugMeshPainter
{
    /// <summary>Smallest separation below which a line is treated as a dot and skipped.</summary>
    private const float MinSegmentLength = 1e-3f;

    private readonly ImmediateMesh _mesh = new();
    private readonly MeshInstance3D _instance;
    private readonly ShaderMaterial _material;
    private bool _surfaceOpen;

    public DebugMeshPainter(Node3D parent, string name = "AiDebugOverlay")
    {
        _material = new ShaderMaterial
        {
            // Vertex colours are the whole material: unshaded, alpha blended, both windings visible so a
            // quad is never lost to the camera looking at its back face.
            Shader = new Shader
            {
                Code = "shader_type spatial;\n" +
                       "render_mode unshaded, cull_disabled, blend_mix;\n" +
                       "void fragment() { ALBEDO = COLOR.rgb; ALPHA = COLOR.a; }\n"
            }
        };

        _instance = new MeshInstance3D
        {
            Name = name,
            Mesh = _mesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false
        };

        parent.AddChild(_instance);
    }

    public bool Visible
    {
        get => _instance.Visible;
        set => _instance.Visible = value;
    }

    /// <summary>Starts a frame: drops the previous mesh and lazily opens a triangle surface on first use.</summary>
    public void Begin()
    {
        if (_surfaceOpen)
        {
            _mesh.SurfaceEnd();
            _surfaceOpen = false;
        }

        _mesh.ClearSurfaces();
    }

    /// <summary>Closes the frame. An empty frame leaves the mesh surface list empty rather than zero-vertex.</summary>
    public void End()
    {
        if (!_surfaceOpen) return;
        _mesh.SurfaceEnd();
        _surfaceOpen = false;
    }

    /// <summary>Draws a single flat triangle.</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        EnsureSurface();
        _mesh.SurfaceSetColor(color);
        _mesh.SurfaceAddVertex(a);
        _mesh.SurfaceAddVertex(b);
        _mesh.SurfaceAddVertex(c);
    }

    /// <summary>Draws a quad as two triangles, with a single colour.</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        EnsureSurface();
        _mesh.SurfaceSetColor(color);
        _mesh.SurfaceAddVertex(a);
        _mesh.SurfaceAddVertex(b);
        _mesh.SurfaceAddVertex(c);
        _mesh.SurfaceAddVertex(a);
        _mesh.SurfaceAddVertex(c);
        _mesh.SurfaceAddVertex(d);
    }

    /// <summary>Draws a thick line segment in the XZ plane.</summary>
    public void Line(Vector3 from, Vector3 to, float width, Color color)
    {
        Vector3 delta = to - from;
        float length = delta.Length();
        if (length < MinSegmentLength || width <= 0f) return;

        Vector3 along = delta * (1f / length);
        Vector3 side = along.Cross(Vector3.Up);
        if (side.LengthSquared() < 1e-6f)
            side = along.Cross(Vector3.Right);
        side = side.Normalized() * (width * 0.5f);

        Quad(from - side, from + side, to + side, to - side, color);
    }

    /// <summary>Draws a dashed thick line; used for "belief/intention" links rather than executed structure.</summary>
    public void DashedLine(Vector3 from, Vector3 to, float width, Color color,
        float dashLength = 0.65f, float gapLength = 0.45f)
    {
        Vector3 delta = to - from;
        float length = delta.Length();
        if (length < MinSegmentLength || width <= 0f) return;

        Vector3 along = delta * (1f / length);
        float travelled = 0f;
        while (travelled < length)
        {
            float dash = MathF.Min(dashLength, length - travelled);
            Line(from + along * travelled, from + along * (travelled + dash), width, color);
            travelled += dashLength + gapLength;
        }
    }

    /// <summary>Filled circle on the ground plane.</summary>
    public void Disc(Vector3 centre, float radius, Color color, int segments = 16)
    {
        if (radius <= 0f) return;

        for (int i = 0; i < segments; i++)
        {
            float a0 = i / (float)segments * MathF.PI * 2f;
            float a1 = (i + 1) / (float)segments * MathF.PI * 2f;
            Triangle(
                centre,
                centre + new Vector3(MathF.Cos(a0), 0f, MathF.Sin(a0)) * radius,
                centre + new Vector3(MathF.Cos(a1), 0f, MathF.Sin(a1)) * radius,
                color);
        }
    }

    /// <summary>Thick unfilled ring on the ground plane.</summary>
    public void Ring(Vector3 centre, float radius, float width, Color color, int segments = 28)
    {
        if (radius <= 0f || width <= 0f) return;

        float inner = MathF.Max(0f, radius - width * 0.5f);
        float outer = radius + width * 0.5f;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i / (float)segments * MathF.PI * 2f;
            float a1 = (i + 1) / (float)segments * MathF.PI * 2f;
            Vector3 dir0 = new(MathF.Cos(a0), 0f, MathF.Sin(a0));
            Vector3 dir1 = new(MathF.Cos(a1), 0f, MathF.Sin(a1));
            Quad(centre + dir0 * inner, centre + dir0 * outer,
                 centre + dir1 * outer, centre + dir1 * inner, color);
        }
    }

    /// <summary>Filled sector from <paramref name="centre"/> along <paramref name="directionXZ"/>.</summary>
    public void Wedge(Vector3 centre, Vector2 directionXZ, float radius, float halfAngleRadians,
        Color color, int segments = 24)
    {
        if (radius <= 0f) return;

        float centreAngle = MathF.Atan2(directionXZ.Y, directionXZ.X);
        Vector3 previous = centre;
        for (int i = 0; i <= segments; i++)
        {
            float angle = centreAngle - halfAngleRadians +
                          (i / (float)segments) * (halfAngleRadians * 2f);
            Vector3 point = centre + new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * radius;
            if (i > 0) Triangle(centre, previous, point, color);
            previous = point;
        }
    }

    /// <summary>Line with a two-stroke arrow head at <paramref name="to"/>.</summary>
    public void Arrow(Vector3 from, Vector3 to, float width, Color color,
        float headLength = 1.1f, float headWidth = 0.55f)
    {
        Line(from, to, width, color);

        Vector3 delta = to - from;
        float length = delta.Length();
        if (length < MinSegmentLength) return;

        Vector3 along = delta * (1f / length);
        Vector3 side = along.Cross(Vector3.Up).Normalized();
        float head = MathF.Min(headLength, length);
        Line(to, to - along * head + side * headWidth, width, color);
        Line(to, to - along * head - side * headWidth, width, color);
    }

    /// <summary>Short vertical flag used to mark a point that is not under a player model (a restart spot, a target).</summary>
    public void Flag(Vector3 ground, float height, float width, Color color)
    {
        Line(ground, ground + Vector3.Up * height, width, color);
        Line(ground + Vector3.Up * height, ground + Vector3.Up * (height * 0.6f) + Vector3.Right * (width * 5f), width, color);
    }

    private void EnsureSurface()
    {
        if (_surfaceOpen) return;
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        _surfaceOpen = true;
    }
}
