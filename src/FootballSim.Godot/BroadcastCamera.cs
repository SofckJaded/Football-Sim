using Godot;
using FootballSim.Core;

namespace FootballSim.GodotClient;

/// <summary>
/// The single broadcast camera used by the match and every training drill, so practice framing is
/// identical to the match: a tall side-on view that follows the ball at a fixed offset.
///
/// Audit F22: the smoothing used a fixed 0.14 lerp per rendered frame, so the camera responded
/// differently at 30, 60 and 144 FPS. It now advances by elapsed time through
/// <see cref="CameraSmoothing"/>; the half-life is derived from the old 60 Hz weight, so the 60 FPS
/// feel is unchanged while the other frame rates match it.
/// </summary>
public static class BroadcastCamera
{
    public const float DefaultZoomHeight = 34f;
    public const float DistanceBehind = 27f;
    public const float MinZoomHeight = 18f;
    public const float MaxZoomHeight = 65f;

    public static Camera3D Create(Node3D root, float zoomHeight)
    {
        var camera = new Camera3D
        {
            Position = new Vector3(0f, ClampZoomHeight(zoomHeight), DistanceBehind),
            Fov = 45f
        };
        root.AddChild(camera);
        return camera;
    }

    /// <summary>Clamps an inspector-supplied zoom height to the supported range.</summary>
    public static float ClampZoomHeight(float zoomHeight) =>
        float.IsFinite(zoomHeight)
            ? Mathf.Clamp(zoomHeight, MinZoomHeight, MaxZoomHeight)
            : DefaultZoomHeight;

    /// <summary>Follows the ball (match framing) for one frame of <paramref name="deltaSeconds"/>.</summary>
    public static void Follow(Camera3D camera, System.Numerics.Vector3 ballPosition, float zoomHeight, float deltaSeconds)
    {
        Vector3 focus = new(ballPosition.X * 0.72f, 0f, ballPosition.Z * 0.60f);
        SmoothTowards(camera, focus, zoomHeight, deltaSeconds);
    }

    /// <summary>Same framing, but locked on a supplied point (used by practice to centre the player).</summary>
    public static void FollowCentered(Camera3D camera, Vector3 focus, float zoomHeight, float deltaSeconds) =>
        SmoothTowards(camera, focus, zoomHeight, deltaSeconds);

    /// <summary>Places the camera on a focus point immediately (scene start, no glide-in).</summary>
    public static void SnapTo(Camera3D camera, Vector3 focus, float zoomHeight)
    {
        camera.Position = new Vector3(focus.X, focus.Y + ClampZoomHeight(zoomHeight), focus.Z + DistanceBehind);
        camera.LookAt(focus, Vector3.Up);
    }

    private static void SmoothTowards(Camera3D camera, Vector3 focus, float zoomHeight, float deltaSeconds)
    {
        Vector3 desired = new(focus.X, focus.Y + ClampZoomHeight(zoomHeight), focus.Z + DistanceBehind);
        System.Numerics.Vector3 current = new(camera.Position.X, camera.Position.Y, camera.Position.Z);
        System.Numerics.Vector3 target = new(desired.X, desired.Y, desired.Z);
        System.Numerics.Vector3 smoothed = CameraSmoothing.Step(
            current, target, CameraSmoothing.MatchHalfLifeSeconds, deltaSeconds);

        camera.Position = new Vector3(smoothed.X, smoothed.Y, smoothed.Z);

        // Aim from the SMOOTHED position at the focus it implies, not at the raw frame focus.
        // Rotation used to snap to the raw ball position every frame while only the position was
        // smoothed; a contested ball (whose contacts nudge it by a fraction of a metre) therefore
        // re-aimed the camera repeatedly and visibly shook the view whenever two players got close.
        Vector3 lookTarget = new(
            smoothed.X,
            smoothed.Y - ClampZoomHeight(zoomHeight),
            smoothed.Z - DistanceBehind);
        camera.LookAt(lookTarget, Vector3.Up);
    }
}
