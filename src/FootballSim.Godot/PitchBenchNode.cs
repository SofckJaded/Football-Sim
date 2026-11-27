using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Godot;
using FootballSim.Core;

namespace FootballSim.GodotClient;

/// <summary>
/// Dev-only pitch measurement and capture harness (docs/football-grass.md §6 G0.1, §7.2). It builds the
/// shared pitch dressing with no simulation at all, pins the broadcast camera on reproducible framings and
/// reports per-frame CPU/GPU cost, draw calls and memory so the grass work can be compared run to run.
///
/// Modes (user arguments after <c>--</c>):
/// <list type="bullet">
/// <item><c>--bench</c>: fixed 20 s sweep over the pinned presets (5 s each), 60 warm-up frames per preset,
/// CPU frame time from <c>Time.GetTicksUsec</c> and GPU time from the viewport render-time measurement,
/// repeated once with the ground surface hidden to measure the grass increment. Prints a stable
/// <c>BENCH ...</c> summary block to stdout and quits.</item>
/// <item><c>--capture --out &lt;dir&gt; [--prefix &lt;name&gt;]</c>: saves the four review PNGs, plus a
/// <c>-ballshadow.png</c> frame when <c>--ball-height</c> is supplied.</item>
/// <item><c>--pan-capture --out &lt;dir&gt;</c>: 20 s continuous pan through the bench camera keyframes,
/// eight equally spaced PNGs for the shimmer A/B.</item>
/// <item>no user arguments: interactive HUD with the preset/AA/ground/markings/tier/shadow controls.</item>
/// </list>
///
/// GPU render-time measurement has a cost and is enabled only in <c>--bench</c> mode, never in an
/// interactive or capture run. The harness never runs the simulation and is not referenced by any
/// gameplay scene.
/// </summary>
public partial class PitchBenchNode : Node3D
{
    private const string GroundGroupName = "pitch_ground";
    private const string SunGroupName = "pitch_sun";
    private const string TurfMeshName = "PitchTurf";
    private const int MaxPresets = 4;
    private const int BenchWarmupFrames = 60;
    private const double BenchSecondsPerPreset = 5.0;
    private const double HudRefreshSeconds = 0.25;
    private const int CaptureSettleFrames = 20;
    private const int LiveSampleCapacity = 240;
    private const int PanShotCount = 8;
    private const double PanDurationSeconds = 20.0;
    private const float PanKeyframeSeconds = (float)(PanDurationSeconds / MaxPresets);
    private const string DefaultCapturePrefix = "bench";
    private const string DefaultCaptureDirectory = "user://pitch_bench";

    private enum HarnessMode
    {
        Interactive,
        Bench,
        Capture,
        PanCapture
    }

    private enum AaMode
    {
        Off,
        Fxaa,
        Smaa,
        Taa
    }

    private enum BenchPhase
    {
        Idle,
        Warmup,
        Sampling
    }

    /// <summary>A pinned broadcast framing: same 45° FOV / 27 m-behind camera, fixed focus and height.</summary>
    private readonly record struct CameraPreset(string Name, Vector3 Focus, float Zoom);

    private static readonly CameraPreset[] DefaultPresets =
    {
        new("18m", Vector3.Zero, 18f),
        new("34m", Vector3.Zero, 34f),
        new("65m", Vector3.Zero, 65f),
        new("65m-corner", new Vector3(52.5f, 0f, 34f), 65f)
    };

    private readonly List<string> _unknownArguments = new();

    // Parsed CLI state.
    private bool _helpRequested;
    private bool _benchRequested;
    private bool _captureRequested;
    private bool _panCaptureRequested;
    private bool _hideGround;
    private float? _zoomOverride;
    private AaMode? _aaOverride;
    private int? _shadowSplitsOverride;
    private int? _tierOverride;
    private float? _ballHeight;
    private string _captureOutDir = string.Empty;
    private string _capturePrefix = DefaultCapturePrefix;

    // Scene handles and runtime state.
    private Camera3D _camera = null!;
    private Rid _viewportRid;
    private HarnessMode _mode = HarnessMode.Interactive;
    private CameraPreset[] _activePresets = DefaultPresets;
    private int _currentPresetIndex;
    private AaMode _aaMode = AaMode.Off;
    private int _tier = 2;
    private bool _groundVisible = true;
    private bool _markingsEnabled = true;
    private bool _gpuMeasureEnabled;

    // Defensive scene lookups (the ground rebuild landed in parallel; every A/B path degrades to a no-op).
    private bool _sunResolved;
    private DirectionalLight3D? _sun;
    private bool _sunMissingLogged;
    private bool _turfResolved;
    private ShaderMaterial? _turfMaterial;
    private bool _turfMissingLogged;
    private bool _groundGroupEmptyLogged;
    private bool _tierNoOpLogged;
    private bool _markingsNoOpLogged;
    private bool _ballHeightNoOpLogged;

    // Interactive HUD state.
    private readonly StringBuilder _hudText = new(512);
    private readonly double[] _liveCpuMs = new double[LiveSampleCapacity];
    private Label? _hud;
    private double _hudRefreshSeconds;
    private double _smoothedCpuMs;
    private int _liveSampleIndex;
    private int _liveSampleCount;

    // Bench state. Two passes x four presets: pass 0 = ground visible, pass 1 = ground hidden.
    private BenchPhase _benchPhase;
    private int _benchPass;
    private int _benchPresetIndex;
    private int _benchWarmupFrames;
    private ulong _benchSampleStartUsec;
    private ulong _lastFrameUsec;
    private readonly List<double>[,] _cpuSamples = new List<double>[2, MaxPresets];
    private readonly List<double>[,] _gpuSamples = new List<double>[2, MaxPresets];
    private readonly int[] _passDrawCalls = new int[2];
    private readonly int[] _passObjects = new int[2];
    private readonly long[] _passPrimitives = new long[2];
    private readonly double[] _passVideoMemBytes = new double[2];
    private readonly double[] _passTextureMemBytes = new double[2];

    public override void _Ready()
    {
        ParseArguments(OS.GetCmdlineUserArgs());

        if (_unknownArguments.Count > 0)
            GD.PrintErr("PitchBench warning: ignoring unknown argument(s): " + string.Join(", ", _unknownArguments));

        if (_helpRequested)
        {
            PrintUsage();
            GetTree().Quit(0);
            return;
        }

        // Shared studio dressing only: this scene never owns a simulation.
        PitchVisualBuilder.Build(this, new PitchDimensions(), -1f, 1f);

        _viewportRid = GetViewport().GetViewportRid();
        _activePresets = BuildActivePresets();
        _currentPresetIndex = 0;
        CameraPreset initial = _activePresets[0];
        _camera = BroadcastCamera.Create(this, initial.Zoom);
        BroadcastCamera.SnapTo(_camera, initial.Focus, initial.Zoom);
        _camera.MakeCurrent();

        ApplyStartupState();

        _mode = ResolveMode();
        int requestedModes = (_benchRequested ? 1 : 0) + (_captureRequested ? 1 : 0) + (_panCaptureRequested ? 1 : 0);
        if (requestedModes > 1)
            GD.Print("BENCH note=multiple_modes picked=" + ModeName(_mode) + " priority=pan-capture,capture,bench");

        switch (_mode)
        {
            case HarnessMode.Bench:
                StartBench();
                break;
            case HarnessMode.Capture:
                RunCaptureMode();
                break;
            case HarnessMode.PanCapture:
                RunPanCaptureMode();
                break;
            default:
                BuildHud();
                UpdateHud();
                break;
        }
    }

    public override void _Process(double delta)
    {
        ulong nowUsec = Time.GetTicksUsec();
        double cpuMs = ConsumeFrameCpuMs(nowUsec);

        switch (_mode)
        {
            case HarnessMode.Bench:
                TickBench(nowUsec, cpuMs);
                break;
            case HarnessMode.Interactive:
                TickInteractive(delta, cpuMs);
                break;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_mode != HarnessMode.Interactive) return;
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo) return;

        bool handled = true;
        switch (keyEvent.Keycode)
        {
            case Key.Key1:
            case Key.Key2:
            case Key.Key3:
            case Key.Key4:
                SelectPreset((int)keyEvent.Keycode - (int)Key.Key1);
                break;
            case Key.A:
                ApplyAa(NextAaMode(_aaMode));
                break;
            case Key.G:
                SetGroundVisible(!_groundVisible);
                GD.Print("BENCH set ground=" + (_groundVisible ? "visible" : "hidden"));
                break;
            case Key.M:
                ApplyMarkings(!_markingsEnabled);
                GD.Print("BENCH set markings=" + (_markingsEnabled ? "on" : "off"));
                break;
            case Key.T:
                ApplyTier((_tier + 1) % 3);
                break;
            case Key.S:
                ApplyShadowSplits(ShadowSplitCount() == 2 ? 4 : 2);
                break;
            case Key.F1:
                PrintLiveSummary();
                break;
            case Key.Escape:
                GetTree().Quit(0);
                break;
            default:
                handled = false;
                break;
        }

        if (handled) GetViewport().SetInputAsHandled();
    }

    // ------------------------------------------------------------------ CLI

    private void ParseArguments(string[] arguments)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            switch (argument)
            {
                case "--help":
                case "-h":
                    _helpRequested = true;
                    break;
                case "--bench":
                    _benchRequested = true;
                    break;
                case "--capture":
                    _captureRequested = true;
                    break;
                case "--pan-capture":
                    _panCaptureRequested = true;
                    break;
                case "--out":
                    if (TryTakeValue(arguments, ref i, out string outDirectory))
                        _captureOutDir = outDirectory;
                    else
                        _unknownArguments.Add("--out (missing value)");
                    break;
                case "--prefix":
                    if (TryTakeValue(arguments, ref i, out string prefix) && prefix.Length > 0)
                        _capturePrefix = SanitizeFileName(prefix);
                    else
                        _unknownArguments.Add("--prefix (missing value)");
                    break;
                case "--zoom":
                    if (TryTakeValue(arguments, ref i, out string zoomText) &&
                        float.TryParse(zoomText, NumberStyles.Float, CultureInfo.InvariantCulture, out float zoomValue))
                        _zoomOverride = zoomValue;
                    else
                        _unknownArguments.Add("--zoom (expected 18|34|65)");
                    break;
                case "--aa":
                    if (TryTakeValue(arguments, ref i, out string aaText) && TryParseAaMode(aaText, out AaMode aaMode))
                        _aaOverride = aaMode;
                    else
                        _unknownArguments.Add("--aa (expected off|fxaa|smaa|taa)");
                    break;
                case "--shadows":
                    if (TryTakeValue(arguments, ref i, out string shadowText) && (shadowText == "2" || shadowText == "4"))
                        _shadowSplitsOverride = shadowText == "2" ? 2 : 4;
                    else
                        _unknownArguments.Add("--shadows (expected 2|4)");
                    break;
                case "--tier":
                    if (TryTakeValue(arguments, ref i, out string tierText) &&
                        int.TryParse(tierText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tierValue) &&
                        tierValue is >= 0 and <= 2)
                        _tierOverride = tierValue;
                    else
                        _unknownArguments.Add("--tier (expected 0|1|2)");
                    break;
                case "--ball-height":
                    if (TryTakeValue(arguments, ref i, out string ballText) &&
                        float.TryParse(ballText, NumberStyles.Float, CultureInfo.InvariantCulture, out float ballHeight))
                        _ballHeight = ballHeight;
                    else
                        _unknownArguments.Add("--ball-height (missing/invalid metres)");
                    break;
                case "--hide-ground":
                    _hideGround = true;
                    break;
                default:
                    _unknownArguments.Add(argument);
                    break;
            }
        }
    }

    private static bool TryTakeValue(string[] arguments, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= arguments.Length) return false;
        value = arguments[++index];
        return true;
    }

    private static bool TryParseAaMode(string text, out AaMode mode)
    {
        switch (text.ToLowerInvariant())
        {
            case "off":
                mode = AaMode.Off;
                return true;
            case "fxaa":
                mode = AaMode.Fxaa;
                return true;
            case "smaa":
                mode = AaMode.Smaa;
                return true;
            case "taa":
                mode = AaMode.Taa;
                return true;
            default:
                mode = AaMode.Off;
                return false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(name.Length);
        foreach (char character in name)
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        return builder.ToString();
    }

    private HarnessMode ResolveMode()
    {
        if (_panCaptureRequested) return HarnessMode.PanCapture;
        if (_captureRequested) return HarnessMode.Capture;
        if (_benchRequested) return HarnessMode.Bench;
        return HarnessMode.Interactive;
    }

    private static string ModeName(HarnessMode mode) => mode switch
    {
        HarnessMode.Bench => "bench",
        HarnessMode.Capture => "capture",
        HarnessMode.PanCapture => "pan-capture",
        _ => "interactive"
    };

    private CameraPreset[] BuildActivePresets()
    {
        if (!_zoomOverride.HasValue) return DefaultPresets;

        float zoom = BroadcastCamera.ClampZoomHeight(_zoomOverride.Value);
        string name = ZoomText(zoom) + "m";
        return new[] { new CameraPreset(name, Vector3.Zero, zoom) };
    }

    private void PrintUsage()
    {
        GD.Print("PitchBench - dev-only pitch measurement/capture harness (docs/football-grass.md G0.1).");
        GD.Print(string.Empty);
        GD.Print("Usage (user arguments go after --):");
        GD.Print("  --bench                    20 s sweep per pass (4 presets x 5 s, 60 warm-up frames each),");
        GD.Print("                             CPU+GPU frame stats, repeated with the ground hidden (A/B).");
        GD.Print("                             Prints the BENCH summary block and quits.");
        GD.Print("  --capture --out <dir>      Save <prefix>-18m/34m/65m/65m-corner.png, then quit.");
        GD.Print("  --pan-capture --out <dir>  20 s continuous pan through the bench keyframes; saves 8");
        GD.Print("                             <prefix>-pan-00.png .. -pan-07.png for the shimmer A/B.");
        GD.Print("  --out <dir>                Capture output directory (default: user://pitch_bench).");
        GD.Print("  --prefix <name>            Capture file prefix (default: bench).");
        GD.Print("  --zoom <18|34|65>          Single-preset override (pitch centre); also narrows --capture.");
        GD.Print("                             Presets: 18m, 34m, 65m (origin), 65m-corner (52.5, 0, 34).");
        GD.Print("  --aa off|fxaa|smaa|taa     Root-viewport anti-aliasing (default: project setting).");
        GD.Print("  --shadows 2|4              Directional shadow splits, max distance pinned to 150 m.");
        GD.Print("  --tier 0|1|2               Pitch shader quality_level (default 2 = High).");
        GD.Print("  --ball-height <metres>     Analytic ball contact-shadow height on the inner turf");
        GD.Print("                             (0 = on the ground, < 0 disables). With --capture it also");
        GD.Print("                             writes <prefix>-ballshadow.png from the 18 m framing.");
        GD.Print("  --hide-ground              Start with the ground surface hidden.");
        GD.Print("  --help                     Print this help and quit.");
        GD.Print(string.Empty);
        GD.Print("No user arguments = interactive HUD. Keys: 1-4 presets, A cycle AA, G ground,");
        GD.Print("M markings SDF, T quality tier, S shadow splits, F1 print summary, Esc quit.");
        GD.Print("Unknown arguments print a warning and are ignored; defaults are used.");
    }

    // ------------------------------------------------------------------ startup state

    private void ApplyStartupState()
    {
        if (_shadowSplitsOverride.HasValue)
            ApplyShadowSplits(_shadowSplitsOverride.Value);

        if (_aaOverride.HasValue)
            ApplyAa(_aaOverride.Value);
        else
            _aaMode = ReadAaMode(GetViewport());

        ApplyTier(_tierOverride ?? 2);
        ApplyMarkings(true);

        if (_ballHeight.HasValue)
            ApplyBallHeight(_ballHeight.Value);

        SetGroundVisible(!_hideGround);
    }

    private void ApplyAa(AaMode mode)
    {
        Viewport viewport = GetViewport();
        viewport.UseTaa = mode == AaMode.Taa;
        viewport.ScreenSpaceAA = mode switch
        {
            AaMode.Fxaa => Viewport.ScreenSpaceAAEnum.Fxaa,
            AaMode.Smaa => Viewport.ScreenSpaceAAEnum.Smaa,
            _ => Viewport.ScreenSpaceAAEnum.Disabled
        };
        _aaMode = mode;
        GD.Print("BENCH set aa=" + AaName(mode));
    }

    private static AaMode ReadAaMode(Viewport viewport)
    {
        if (viewport.UseTaa) return AaMode.Taa;
        return viewport.ScreenSpaceAA switch
        {
            Viewport.ScreenSpaceAAEnum.Fxaa => AaMode.Fxaa,
            Viewport.ScreenSpaceAAEnum.Smaa => AaMode.Smaa,
            _ => AaMode.Off
        };
    }

    private static AaMode NextAaMode(AaMode mode) => mode switch
    {
        AaMode.Off => AaMode.Fxaa,
        AaMode.Fxaa => AaMode.Smaa,
        AaMode.Smaa => AaMode.Taa,
        _ => AaMode.Off
    };

    private static string AaName(AaMode mode) => mode switch
    {
        AaMode.Fxaa => "fxaa",
        AaMode.Smaa => "smaa",
        AaMode.Taa => "taa",
        _ => "off"
    };

    private void ApplyShadowSplits(int splits)
    {
        DirectionalLight3D? sun = ResolveSun();
        if (sun == null) return;

        sun.DirectionalShadowMode = splits == 2
            ? DirectionalLight3D.ShadowMode.Parallel2Splits
            : DirectionalLight3D.ShadowMode.Parallel4Splits;
        sun.DirectionalShadowMaxDistance = 150f;
        GD.Print("BENCH set shadows=" + splits.ToString(CultureInfo.InvariantCulture) + "-split max_distance_m=150");
    }

    private int ShadowSplitCount()
    {
        DirectionalLight3D? sun = ResolveSun();
        if (sun == null) return 0;
        return sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits ? 4 : 2;
    }

    private string ShadowSplitName()
    {
        DirectionalLight3D? sun = ResolveSun();
        if (sun == null) return "n/a";
        return sun.DirectionalShadowMode switch
        {
            DirectionalLight3D.ShadowMode.Orthogonal => "orthogonal",
            DirectionalLight3D.ShadowMode.Parallel2Splits => "2-split",
            DirectionalLight3D.ShadowMode.Parallel4Splits => "4-split",
            _ => "n/a"
        };
    }

    private void ApplyTier(int tier)
    {
        int clamped = Mathf.Clamp(tier, 0, 2);
        _tier = clamped;
        ShaderMaterial? turf = ResolveTurfMaterial();
        if (turf == null || !HasShaderUniform(turf, "quality_level"))
        {
            LogOnce(ref _tierNoOpLogged,
                "PitchBench: inner turf ShaderMaterial has no 'quality_level' uniform; --tier/T is a no-op");
            return;
        }

        turf.SetShaderParameter("quality_level", clamped);
        GD.Print("BENCH set tier=" + clamped.ToString(CultureInfo.InvariantCulture));
    }

    private void ApplyMarkings(bool enabled)
    {
        _markingsEnabled = enabled;
        ShaderMaterial? turf = ResolveTurfMaterial();
        if (turf == null || !HasShaderUniform(turf, "markings_enabled"))
        {
            LogOnce(ref _markingsNoOpLogged,
                "PitchBench: inner turf ShaderMaterial has no 'markings_enabled' uniform; --markings/M is a no-op");
            return;
        }

        turf.SetShaderParameter("markings_enabled", enabled ? 1 : 0);
    }

    private void ApplyBallHeight(float height)
    {
        ShaderMaterial? turf = ResolveTurfMaterial();
        if (turf == null || !HasShaderUniform(turf, "ball_pos_height"))
        {
            LogOnce(ref _ballHeightNoOpLogged,
                "PitchBench: inner turf ShaderMaterial has no 'ball_pos_height' uniform; --ball-height is a no-op");
            return;
        }

        // Vector4(x, z, unused, height above ground); a negative height disables the analytic shadow.
        turf.SetShaderParameter("ball_pos_height", new Vector4(0f, 0f, 0f, height));
        GD.Print("BENCH set ball_height_m=" + F(height) + " (negative disables)");
    }

    private void SetGroundVisible(bool visible)
    {
        _groundVisible = visible;
        Godot.Collections.Array<Node> groundNodes = GetTree().GetNodesInGroup(GroundGroupName);
        if (groundNodes.Count == 0)
        {
            LogOnce(ref _groundGroupEmptyLogged,
                "PitchBench: scene group '" + GroundGroupName +
                "' is empty; ground visibility A/B and --hide-ground are no-ops");
            return;
        }

        foreach (Node node in groundNodes)
        {
            if (node is Node3D node3D)
                node3D.Visible = visible;
            else if (node is CanvasItem canvasItem)
                canvasItem.Visible = visible;
        }
    }

    private DirectionalLight3D? ResolveSun()
    {
        if (_sunResolved) return _sun;
        _sunResolved = true;

        foreach (Node node in GetTree().GetNodesInGroup(SunGroupName))
        {
            if (node is DirectionalLight3D groupedLight)
            {
                _sun = groupedLight;
                return _sun;
            }
        }

        foreach (Node child in GetChildren())
        {
            if (child is DirectionalLight3D childLight)
            {
                _sun = childLight;
                return _sun;
            }
        }

        LogOnce(ref _sunMissingLogged,
            "PitchBench: no DirectionalLight3D in group '" + SunGroupName +
            "' or under the bench root; shadow toggles are no-ops");
        return null;
    }

    private ShaderMaterial? ResolveTurfMaterial()
    {
        if (_turfResolved) return _turfMaterial;
        _turfResolved = true;

        // Preferred: the named inner-turf mesh in the ground group.
        foreach (Node node in GetTree().GetNodesInGroup(GroundGroupName))
        {
            if (node is MeshInstance3D mesh &&
                mesh.Name.ToString() == TurfMeshName &&
                mesh.MaterialOverride is ShaderMaterial namedMaterial)
            {
                _turfMaterial = namedMaterial;
                return _turfMaterial;
            }
        }

        // Fallback: any ground mesh whose material declares the turf uniforms (the apron material is a
        // ShaderMaterial too, but it declares none of them).
        foreach (Node node in GetTree().GetNodesInGroup(GroundGroupName))
        {
            if (node is MeshInstance3D { MaterialOverride: ShaderMaterial material } && HasTurfUniforms(material))
            {
                _turfMaterial = material;
                return _turfMaterial;
            }
        }

        // Last resort for a future builder without the group: scan the bench subtree.
        foreach (Node node in FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is MeshInstance3D { MaterialOverride: ShaderMaterial material } && HasTurfUniforms(material))
            {
                _turfMaterial = material;
                return _turfMaterial;
            }
        }

        LogOnce(ref _turfMissingLogged,
            "PitchBench: no inner turf ShaderMaterial found (group '" + GroundGroupName +
            "' empty or unshaded); tier/markings/ball-shadow toggles are no-ops");
        return null;
    }

    private static bool HasTurfUniforms(ShaderMaterial material) =>
        HasShaderUniform(material, "markings_enabled") ||
        HasShaderUniform(material, "quality_level") ||
        HasShaderUniform(material, "ball_pos_height");

    private static bool HasShaderUniform(ShaderMaterial material, string uniformName)
    {
        Shader? shader = material.Shader;
        if (shader == null) return false;

        foreach (Godot.Collections.Dictionary entry in shader.GetShaderUniformList())
        {
            if (entry.TryGetValue("name", out Variant name) && name.AsString() == uniformName)
                return true;
        }

        return false;
    }

    private static void LogOnce(ref bool flag, string message)
    {
        if (flag) return;
        flag = true;
        GD.PushWarning(message);
    }

    // ------------------------------------------------------------------ bench

    private void StartBench()
    {
        for (int pass = 0; pass < 2; pass++)
        {
            for (int preset = 0; preset < MaxPresets; preset++)
            {
                _cpuSamples[pass, preset] = new List<double>(16384);
                _gpuSamples[pass, preset] = new List<double>(16384);
            }
        }

        _benchPass = 0;
        _benchPresetIndex = 0;
        _benchPhase = BenchPhase.Idle;
        _benchWarmupFrames = 0;
        _lastFrameUsec = 0;

        RenderingServer.Singleton.ViewportSetMeasureRenderTime(_viewportRid, true);
        _gpuMeasureEnabled = true;

        GD.Print("BENCH running mode=bench presets=" + _activePresets.Length.ToString(CultureInfo.InvariantCulture) +
                 " seconds_per_preset=" + F(BenchSecondsPerPreset) +
                 " warmup_frames=" + BenchWarmupFrames.ToString(CultureInfo.InvariantCulture) +
                 " passes=visible,hidden gpu_measure=on");

        SetGroundVisible(true);
        BeginBenchPreset();
    }

    private void BeginBenchPreset()
    {
        CameraPreset preset = _activePresets[_benchPresetIndex];
        BroadcastCamera.SnapTo(_camera, preset.Focus, preset.Zoom);
        _currentPresetIndex = _benchPresetIndex;
        _benchWarmupFrames = 0;
        _benchPhase = BenchPhase.Warmup;
        _lastFrameUsec = Time.GetTicksUsec();

        GD.Print("BENCH progress pass=" + (_benchPass == 0 ? "visible" : "hidden") +
                 " preset=" + preset.Name + " phase=warmup");
    }

    private void TickBench(ulong nowUsec, double cpuMs)
    {
        switch (_benchPhase)
        {
            case BenchPhase.Warmup:
                _benchWarmupFrames++;
                if (_benchWarmupFrames < BenchWarmupFrames) return;
                _benchPhase = BenchPhase.Sampling;
                _benchSampleStartUsec = nowUsec;
                return;

            case BenchPhase.Sampling:
                break;

            default:
                return;
        }

        _cpuSamples[_benchPass, _benchPresetIndex].Add(cpuMs);
        double gpuMs = RenderingServer.Singleton.ViewportGetMeasuredRenderTimeGpu(_viewportRid);
        if (gpuMs > 0.0)
            _gpuSamples[_benchPass, _benchPresetIndex].Add(gpuMs);

        ulong elapsedUsec = nowUsec >= _benchSampleStartUsec ? nowUsec - _benchSampleStartUsec : 0UL;
        if (elapsedUsec * 1e-6 < BenchSecondsPerPreset) return;

        RecordPassMonitors();
        _benchPresetIndex++;
        if (_benchPresetIndex < _activePresets.Length)
        {
            BeginBenchPreset();
            return;
        }

        _benchPresetIndex = 0;
        _benchPass++;
        if (_benchPass < 2)
        {
            SetGroundVisible(false);
            BeginBenchPreset();
            return;
        }

        FinishBench();
    }

    private void RecordPassMonitors()
    {
        int pass = Mathf.Clamp(_benchPass, 0, 1);
        _passDrawCalls[pass] = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        _passObjects[pass] = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        _passPrimitives[pass] = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        _passVideoMemBytes[pass] = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed);
        _passTextureMemBytes[pass] = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed);
    }

    private void FinishBench()
    {
        _benchPhase = BenchPhase.Idle;
        RenderingServer.Singleton.ViewportSetMeasureRenderTime(_viewportRid, false);
        _gpuMeasureEnabled = false;
        SetGroundVisible(true);

        PrintBenchSummary();
        GetTree().Quit(0);
    }

    private void PrintBenchSummary()
    {
        PrintSummaryHeader();

        for (int pass = 0; pass < 2; pass++)
        {
            string passName = pass == 0 ? "visible" : "hidden";
            for (int i = 0; i < _activePresets.Length; i++)
            {
                CameraPreset preset = _activePresets[i];
                Distribution cpu = Summarize(_cpuSamples[pass, i]);
                Distribution gpu = Summarize(_gpuSamples[pass, i]);
                GD.Print("BENCH preset=" + preset.Name + " pass=" + passName +
                         " zoom=" + ZoomText(preset.Zoom) +
                         " frames=" + cpu.Count.ToString(CultureInfo.InvariantCulture) +
                         FormatStats(cpu, gpu));
            }
        }

        Distribution visibleCpu = OverallDistribution(0, false);
        Distribution visibleGpu = OverallDistribution(0, true);
        Distribution hiddenCpu = OverallDistribution(1, false);
        Distribution hiddenGpu = OverallDistribution(1, true);

        GD.Print("BENCH overall_visible p50_cpu_ms=" + F(visibleCpu.P50) +
                 " p95_cpu_ms=" + F(visibleCpu.P95) +
                 " p50_gpu_ms=" + F(visibleGpu.P50) +
                 " p95_gpu_ms=" + F(visibleGpu.P95) +
                 " p99_cpu_ms=" + F(visibleCpu.P99) +
                 " low1pct_cpu_ms=" + F(visibleCpu.Low1Percent) +
                 " p99_gpu_ms=" + F(visibleGpu.P99) +
                 " low1pct_gpu_ms=" + F(visibleGpu.Low1Percent));
        GD.Print("BENCH overall_hidden p50_cpu_ms=" + F(hiddenCpu.P50) +
                 " p95_cpu_ms=" + F(hiddenCpu.P95) +
                 " p50_gpu_ms=" + F(hiddenGpu.P50) +
                 " p95_gpu_ms=" + F(hiddenGpu.P95) +
                 " p99_cpu_ms=" + F(hiddenCpu.P99) +
                 " low1pct_cpu_ms=" + F(hiddenCpu.Low1Percent) +
                 " p99_gpu_ms=" + F(hiddenGpu.P99) +
                 " low1pct_gpu_ms=" + F(hiddenGpu.Low1Percent));
        GD.Print("BENCH grass_increment p50_gpu_ms=" + F(visibleGpu.P50 - hiddenGpu.P50) +
                 " p95_gpu_ms=" + F(visibleGpu.P95 - hiddenGpu.P95));

        GD.Print("BENCH draw_calls=" + _passDrawCalls[0].ToString(CultureInfo.InvariantCulture) +
                 " objects=" + _passObjects[0].ToString(CultureInfo.InvariantCulture) +
                 " primitives=" + _passPrimitives[0].ToString(CultureInfo.InvariantCulture) +
                 " video_mem_mb=" + F(BytesToMegabytes(_passVideoMemBytes[0])) +
                 " texture_mem_mb=" + F(BytesToMegabytes(_passTextureMemBytes[0])) +
                 " static_mem_mb=" + F(BytesToMegabytes(OS.GetStaticMemoryUsage())));
        PrintSettingsLine();
        GD.Print("BENCH done");
    }

    private List<double> CombineSamples(int pass, bool useGpu)
    {
        List<double> combined = new(4096);
        for (int i = 0; i < _activePresets.Length; i++)
            combined.AddRange(useGpu ? _gpuSamples[pass, i] : _cpuSamples[pass, i]);
        return combined;
    }

    private Distribution OverallDistribution(int pass, bool useGpu) => Summarize(CombineSamples(pass, useGpu));

    // ------------------------------------------------------------------ capture

    private void RunCaptureMode()
    {
        _mode = HarnessMode.Capture;
        string outDirectory = NormalizeOutputDirectory(_captureOutDir);
        Error directoryError = DirAccess.MakeDirRecursiveAbsolute(outDirectory);
        if (directoryError != Error.Ok)
        {
            GD.PrintErr("PitchBench: cannot create capture directory '" + outDirectory +
                        "' (err=" + directoryError + ")");
            GetTree().Quit(1);
            return;
        }

        GD.Print("BENCH running mode=capture out=" + outDirectory +
                 " prefix=" + _capturePrefix +
                 " presets=" + _activePresets.Length.ToString(CultureInfo.InvariantCulture) +
                 " aa=" + AaName(_aaMode) +
                 " tier=" + _tier.ToString(CultureInfo.InvariantCulture) +
                 " shadows=" + ShadowSplitName() +
                 " ball_height_m=" + (_ballHeight.HasValue ? F(_ballHeight.Value) : "n/a"));

        CaptureStills(outDirectory);
    }

    private async void CaptureStills(string outDirectory)
    {
        int failures = 0;
        for (int i = 0; i < _activePresets.Length; i++)
        {
            CameraPreset preset = _activePresets[i];
            BroadcastCamera.SnapTo(_camera, preset.Focus, preset.Zoom);
            _currentPresetIndex = i;
            if (!await SaveViewportPngAsync(outDirectory, _capturePrefix + "-" + preset.Name + ".png"))
                failures++;
        }

        if (_ballHeight.HasValue)
        {
            // The analytic ball shadow is largest and most legible from the closest pinned framing.
            BroadcastCamera.SnapTo(_camera, Vector3.Zero, 18f);
            if (!await SaveViewportPngAsync(outDirectory, _capturePrefix + "-ballshadow.png"))
                failures++;
        }

        GD.Print("BENCH done");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private void RunPanCaptureMode()
    {
        _mode = HarnessMode.PanCapture;
        string outDirectory = NormalizeOutputDirectory(_captureOutDir);
        Error directoryError = DirAccess.MakeDirRecursiveAbsolute(outDirectory);
        if (directoryError != Error.Ok)
        {
            GD.PrintErr("PitchBench: cannot create capture directory '" + outDirectory +
                        "' (err=" + directoryError + ")");
            GetTree().Quit(1);
            return;
        }

        GD.Print("BENCH running mode=pan-capture out=" + outDirectory +
                 " prefix=" + _capturePrefix +
                 " shots=" + PanShotCount.ToString(CultureInfo.InvariantCulture) +
                 " duration_s=" + F(PanDurationSeconds) +
                 " aa=" + AaName(_aaMode) +
                 " tier=" + _tier.ToString(CultureInfo.InvariantCulture) +
                 " shadows=" + ShadowSplitName());

        RunPanCaptureAsync(outDirectory);
    }

    /// <summary>
    /// Shimmer A/B: pan continuously through the same four pinned keyframes the bench measures
    /// (18 m -> 34 m -> 65 m -> 65 m corner, 5 s per segment) and save eight equally spaced stills.
    /// The camera moves between keyframes with a smoothstep over the segment so consecutive shots differ
    /// while exposure/AA/tier stay fixed for the whole run.
    /// </summary>
    private async void RunPanCaptureAsync(string outDirectory)
    {
        for (int i = 0; i < CaptureSettleFrames; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        int failures = 0;
        double shotIntervalSeconds = PanDurationSeconds / PanShotCount;
        ulong startUsec = Time.GetTicksUsec();
        int shot = 0;

        while (shot < PanShotCount)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            ulong elapsedUsec = Time.GetTicksUsec() - startUsec;
            double elapsedSeconds = elapsedUsec * 1e-6;
            ApplyPanCamera(elapsedSeconds);

            if (elapsedSeconds < shot * shotIntervalSeconds) continue;

            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

            string fileName = _capturePrefix + "-pan-" + shot.ToString("00", CultureInfo.InvariantCulture) + ".png";
            string path = Path.Combine(outDirectory, fileName);
            Image image = GetViewport().GetTexture().GetImage();
            Error saveError = image.SavePng(path);
            if (saveError != Error.Ok) failures++;

            GD.Print("BENCH capture pan=" + shot.ToString("00", CultureInfo.InvariantCulture) +
                     " t_s=" + Num(elapsedSeconds, "0.00") +
                     " zoom=" + Num(_camera.Position.Y, "0.0") +
                     " file=" + path +
                     " size=" + image.GetWidth().ToString(CultureInfo.InvariantCulture) + "x" +
                     image.GetHeight().ToString(CultureInfo.InvariantCulture) +
                     " err=" + saveError);
            shot++;
        }

        GD.Print("BENCH done");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private void ApplyPanCamera(double elapsedSeconds)
    {
        double clampedSeconds = Math.Clamp(elapsedSeconds, 0.0, PanDurationSeconds);
        int segment = Math.Min((int)(clampedSeconds / PanKeyframeSeconds), MaxPresets - 2);
        float local = (float)((clampedSeconds - segment * PanKeyframeSeconds) / PanKeyframeSeconds);
        float blend = SmoothStep01(Mathf.Clamp(local, 0f, 1f));

        CameraPreset from = DefaultPresets[segment];
        CameraPreset to = DefaultPresets[segment + 1];
        Vector3 focus = from.Focus.Lerp(to.Focus, blend);
        float zoom = Mathf.Lerp(from.Zoom, to.Zoom, blend);
        BroadcastCamera.SnapTo(_camera, focus, zoom);
    }

    private static float SmoothStep01(float value) => value * value * (3f - 2f * value);

    private async Task<bool> SaveViewportPngAsync(string directory, string fileName)
    {
        for (int i = 0; i < CaptureSettleFrames; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        Image image = GetViewport().GetTexture().GetImage();
        string path = Path.Combine(directory, fileName);
        Error saveError = image.SavePng(path);
        GD.Print("BENCH capture file=" + path +
                 " size=" + image.GetWidth().ToString(CultureInfo.InvariantCulture) + "x" +
                 image.GetHeight().ToString(CultureInfo.InvariantCulture) +
                 " settle_frames=" + CaptureSettleFrames.ToString(CultureInfo.InvariantCulture) +
                 " err=" + saveError);
        return saveError == Error.Ok;
    }

    private static string NormalizeOutputDirectory(string directory)
    {
        string value = string.IsNullOrWhiteSpace(directory) ? DefaultCaptureDirectory : directory.Trim();
        if (value.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
        {
            value = ProjectSettings.GlobalizePath(value);
        }
        else if (!Path.IsPathRooted(value))
        {
            value = Path.GetFullPath(value);
        }

        return value.TrimEnd('/', '\\');
    }

    // ------------------------------------------------------------------ interactive HUD

    private void BuildHud()
    {
        var layer = new CanvasLayer { Name = "BenchHud" };
        AddChild(layer);

        _hud = new Label
        {
            Name = "BenchHudLabel",
            Position = new Vector2(12f, 8f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _hud.AddThemeFontSizeOverride("font_size", 16);
        _hud.AddThemeColorOverride("font_color", Colors.White);
        _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hud.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(_hud);
    }

    private void TickInteractive(double deltaSeconds, double cpuMs)
    {
        UpdateCpuEstimates(cpuMs);

        _hudRefreshSeconds += deltaSeconds;
        if (_hudRefreshSeconds < HudRefreshSeconds) return;
        _hudRefreshSeconds = 0.0;
        UpdateHud();
    }

    private void UpdateCpuEstimates(double cpuMs)
    {
        if (cpuMs <= 0.0) return;

        _smoothedCpuMs = _liveSampleCount == 0 ? cpuMs : _smoothedCpuMs * 0.9 + cpuMs * 0.1;
        _liveCpuMs[_liveSampleIndex] = cpuMs;
        _liveSampleIndex = (_liveSampleIndex + 1) % LiveSampleCapacity;
        if (_liveSampleCount < LiveSampleCapacity) _liveSampleCount++;
    }

    private void SelectPreset(int index)
    {
        if (index < 0 || index >= _activePresets.Length) return;

        _currentPresetIndex = index;
        CameraPreset preset = _activePresets[index];
        BroadcastCamera.SnapTo(_camera, preset.Focus, preset.Zoom);
        _hudRefreshSeconds = HudRefreshSeconds;
        GD.Print("BENCH set preset=" + preset.Name + " zoom=" + ZoomText(preset.Zoom));
    }

    private void UpdateHud()
    {
        if (_hud == null) return;

        CameraPreset preset = CurrentPreset;
        _hudText.Clear();
        _hudText.Append("PitchBench  preset ").Append(preset.Name)
            .Append("  zoom ").Append(ZoomText(preset.Zoom)).Append(" m\n");
        _hudText.Append("fps ").Append(Engine.GetFramesPerSecond().ToString(CultureInfo.InvariantCulture))
            .Append("   cpu ").Append(F(_smoothedCpuMs)).Append(" ms")
            .Append("   gpu ").Append(_gpuMeasureEnabled ? F(ReadGpuMs()) : "n/a").Append(" ms\n");
        _hudText.Append("draws ").Append(ReadDrawCallCount().ToString(CultureInfo.InvariantCulture))
            .Append("   objects ").Append(ReadObjectCount().ToString(CultureInfo.InvariantCulture))
            .Append("   prims ")
            .Append(((long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame))
                .ToString(CultureInfo.InvariantCulture))
            .Append("   static ").Append(F(BytesToMegabytes(OS.GetStaticMemoryUsage()))).Append(" MB")
            .Append("   vram ").Append(F(ReadVideoMemoryMegabytes())).Append(" MB\n");
        _hudText.Append("aa ").Append(AaName(_aaMode))
            .Append("   tier ").Append(_tier.ToString(CultureInfo.InvariantCulture))
            .Append("   ground ").Append(_groundVisible ? "visible" : "hidden")
            .Append("   markings ").Append(_markingsEnabled ? "on" : "off")
            .Append("   shadows ").Append(ShadowSplitName());
        if (_ballHeight.HasValue)
            _hudText.Append("   ball_shadow ").Append(F(_ballHeight.Value)).Append(" m");
        _hudText.Append('\n');
        _hudText.Append("keys 1-4 presets  A aa  G ground  M markings  T tier  S shadows  F1 summary  Esc quit");

        _hud.Text = _hudText.ToString();
    }

    private void PrintLiveSummary()
    {
        CameraPreset preset = CurrentPreset;
        Distribution cpu = SummarizeLiveCpu();

        PrintSummaryHeader();
        GD.Print("BENCH live mode=interactive preset=" + preset.Name +
                 " zoom=" + ZoomText(preset.Zoom) +
                 " frames=" + cpu.Count.ToString(CultureInfo.InvariantCulture) +
                 FormatStats(cpu, Distribution.Empty));
        GD.Print("BENCH draw_calls=" + ReadDrawCallCount().ToString(CultureInfo.InvariantCulture) +
                 " objects=" + ReadObjectCount().ToString(CultureInfo.InvariantCulture) +
                 " primitives=" + ((long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame))
                     .ToString(CultureInfo.InvariantCulture) +
                 " video_mem_mb=" + F(ReadVideoMemoryMegabytes()) +
                 " texture_mem_mb=" +
                 F(BytesToMegabytes(Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed))) +
                 " static_mem_mb=" + F(BytesToMegabytes(OS.GetStaticMemoryUsage())));
        PrintSettingsLine();
        GD.Print("BENCH done");
    }

    private void PrintSettingsLine()
    {
        string ballShadowText = _ballHeight.HasValue
            ? " ball_height_m=" + F(_ballHeight.Value)
            : string.Empty;
        GD.Print("BENCH aa=" + AaName(_aaMode) +
                 " tier=" + _tier.ToString(CultureInfo.InvariantCulture) +
                 " shadows=" + ShadowSplitName() +
                 " ground=" + (_groundVisible ? "visible" : "hidden") +
                 " markings=" + (_markingsEnabled ? "on" : "off") +
                 ballShadowText);
    }

    private CameraPreset CurrentPreset =>
        _activePresets[Mathf.Clamp(_currentPresetIndex, 0, _activePresets.Length - 1)];

    private double ReadGpuMs() => RenderingServer.Singleton.ViewportGetMeasuredRenderTimeGpu(_viewportRid);

    private static int ReadDrawCallCount() =>
        (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);

    private static int ReadObjectCount() =>
        (int)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);

    private static double ReadVideoMemoryMegabytes() =>
        BytesToMegabytes(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed));

    private Distribution SummarizeLiveCpu()
    {
        if (_liveSampleCount == 0) return Distribution.Empty;

        double[] samples = new double[_liveSampleCount];
        for (int i = 0; i < _liveSampleCount; i++)
        {
            int index = (_liveSampleIndex - _liveSampleCount + i + LiveSampleCapacity * 2) % LiveSampleCapacity;
            samples[i] = _liveCpuMs[index];
        }

        Array.Sort(samples);
        return FromSorted(samples);
    }

    // ------------------------------------------------------------------ summary helpers

    private static void PrintSummaryHeader() =>
        GD.Print("BENCH version=1 resolution=" + DetectRequestedResolution() + " window=" + WindowSizeText());

    private static string DetectRequestedResolution()
    {
        string[] engineArguments = OS.GetCmdlineArgs();
        for (int i = 0; i + 1 < engineArguments.Length; i++)
        {
            if (engineArguments[i] == "--resolution") return engineArguments[i + 1];
        }

        return WindowSizeText();
    }

    private static string WindowSizeText()
    {
        Vector2I size = DisplayServer.WindowGetSize();
        return size.X.ToString(CultureInfo.InvariantCulture) + "x" + size.Y.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatStats(Distribution cpu, Distribution gpu) =>
        " p50_cpu_ms=" + F(cpu.P50) +
        " p95_cpu_ms=" + F(cpu.P95) +
        " p99_cpu_ms=" + F(cpu.P99) +
        " low1pct_cpu_ms=" + F(cpu.Low1Percent) +
        " p50_gpu_ms=" + F(gpu.P50) +
        " p95_gpu_ms=" + F(gpu.P95) +
        " p99_gpu_ms=" + F(gpu.P99) +
        " low1pct_gpu_ms=" + F(gpu.Low1Percent);

    private static double ConsumeFrameCpuMilliseconds(ulong nowUsec, ref ulong lastFrameUsec)
    {
        double cpuMs = lastFrameUsec == 0 || nowUsec <= lastFrameUsec
            ? 0.0
            : (nowUsec - lastFrameUsec) / 1000.0;
        lastFrameUsec = nowUsec;
        return cpuMs;
    }

    private double ConsumeFrameCpuMs(ulong nowUsec) => ConsumeFrameCpuMilliseconds(nowUsec, ref _lastFrameUsec);

    private static Distribution Summarize(List<double> samples)
    {
        if (samples.Count == 0) return Distribution.Empty;

        double[] sorted = samples.ToArray();
        Array.Sort(sorted);
        return FromSorted(sorted);
    }

    private static Distribution FromSorted(double[] sorted)
    {
        if (sorted.Length == 0) return Distribution.Empty;

        return new Distribution(
            sorted.Length,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            MeanOfSlowest(sorted, 0.01));
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        int index = (int)Math.Round(fraction * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static double MeanOfSlowest(double[] sorted, double fraction)
    {
        int count = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction));
        double sum = 0.0;
        for (int i = sorted.Length - count; i < sorted.Length; i++)
            sum += sorted[i];
        return sum / count;
    }

    private static double BytesToMegabytes(double bytes) => bytes / (1024.0 * 1024.0);

    private static string ZoomText(float zoom) => zoom.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string F(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>One frame-time distribution in milliseconds; empty distributions hold NaN percentiles.</summary>
    private readonly struct Distribution
    {
        public static readonly Distribution Empty = new(0, double.NaN, double.NaN, double.NaN, double.NaN);

        public Distribution(int count, double p50, double p95, double p99, double low1Percent)
        {
            Count = count;
            P50 = p50;
            P95 = p95;
            P99 = p99;
            Low1Percent = low1Percent;
        }

        public int Count { get; }

        public double P50 { get; }

        public double P95 { get; }

        public double P99 { get; }

        public double Low1Percent { get; }
    }
}
