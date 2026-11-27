using Godot;

namespace FootballSim.GodotClient;

/// <summary>
/// Dev-only capture utility: instantiates one of the shipped scenes (match / Free Training) as a
/// child, waits for it to settle, then writes full-resolution PNGs of the viewport. Exists so the
/// rendered visual gates (`docs/football-grass.md` §7.3) can be captured at 2560x1440 with a fixed
/// zoom without changing any shipped scene or project setting.
///
/// Usage:
///   godot --path src/FootballSim.Godot --scene res://PitchCaptureScene.tscn --resolution 2560x1440 \
///     -- --target res://MainMatchScene.tscn --out C:\...\g1 --name match --zoom 65 --count 3
/// </summary>
public partial class PitchCaptureNode : Node3D
{
    private string _target = "res://MainMatchScene.tscn";
    private string _outDir = ".";
    private string _name = "capture";
    private float _zoom = -1f;
    private int _warmupFrames = 150;
    private int _count = 3;
    private int _spacingFrames = 30;

    public override async void _Ready()
    {
        ParseArgs();
        DirAccess.MakeDirRecursiveAbsolute(_outDir);

        PackedScene scene = GD.Load<PackedScene>(_target);
        if (scene is null)
        {
            GD.PrintErr($"CAPTURE cannot load {_target}");
            GetTree().Quit(1);
            return;
        }

        Node instance = scene.Instantiate();
        AddChild(instance);

        if (_zoom > 0f)
        {
            // Zoom is an exported presentation property on both shipped scene roots.
            instance.Set("CameraZoomHeight", _zoom);
        }

        for (int i = 0; i < _warmupFrames; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Viewport viewport = GetViewport();
        for (int capture = 0; capture < _count; capture++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Image image = viewport.GetTexture().GetImage();
            string path = $"{_outDir}/{_name}-{capture:D2}.png";
            Error err = image.SavePng(path);
            GD.Print($"CAPTURE {path} {image.GetWidth()}x{image.GetHeight()} err={err}");
            for (int i = 0; i < _spacingFrames; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        GD.Print($"CAPTURE done target={_target} zoom={_zoom}");
        GetTree().Quit();
    }

    private void ParseArgs()
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target" when i + 1 < args.Length: _target = args[++i]; break;
                case "--out" when i + 1 < args.Length: _outDir = args[++i]; break;
                case "--name" when i + 1 < args.Length: _name = args[++i]; break;
                case "--zoom" when i + 1 < args.Length:
                    _zoom = float.TryParse(args[++i], out float z) ? z : -1f;
                    break;
                case "--warmup" when i + 1 < args.Length:
                    _warmupFrames = int.TryParse(args[++i], out int w) ? w : _warmupFrames;
                    break;
                case "--count" when i + 1 < args.Length:
                    _count = int.TryParse(args[++i], out int c) ? c : _count;
                    break;
                case "--spacing" when i + 1 < args.Length:
                    _spacingFrames = int.TryParse(args[++i], out int s) ? s : _spacingFrames;
                    break;
            }
        }
    }
}
