using System;
using System.Globalization;
using Godot;
using FootballSim.Core;

namespace FootballSim.GodotClient;

/// <summary>
/// Dev-only G2.1 texture dump: builds the procedural pitch textures for the High and Medium tiers,
/// prints generation timings / sizes / content statistics, saves inspection PNGs and quits.
///
/// Run with:
/// <c>godot --path src\FootballSim.Godot --scene res://PitchTextureDumpScene.tscn -- --out &lt;dir&gt;</c>
/// The scene is not referenced by any game scene; it only exists so the G2.1 evidence (timings, VRAM
/// sizes and the look of the tiling detail / macro / wear textures) can be re-captured on demand.
/// </summary>
public partial class PitchTextureDumpNode : Node3D
{
    private const string DefaultOutputDirectory = "C:/Games/geminiball/artifacts/presentation/grass/g2/tex";

    public override void _Ready()
    {
        string outputDirectory = ResolveOutputDirectory();
        Error directoryError = DirAccess.MakeDirRecursiveAbsolute(outputDirectory);
        LogInvariant($"PITCHTEX output_dir={outputDirectory} mkdir={directoryError}");

        var pitch = new PitchDimensions();
        PitchQualitySettings high = PitchQualitySettings.For(PitchQuality.High);
        PitchQualitySettings medium = PitchQualitySettings.For(PitchQuality.Medium);

        // Cold High build: this is the number the G2.1 budget (<= 30 ms) is about.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        PitchTextureSet highSet = PitchTextureFactory.Get(pitch, high);
        double highColdMs = clock.Elapsed.TotalMilliseconds;
        PitchTextureBuildTimings highTimings = PitchTextureFactory.LastBuildTimings;

        clock.Restart();
        PitchTextureSet highCached = PitchTextureFactory.Get(pitch, high);
        double highCachedMs = clock.Elapsed.TotalMilliseconds;

        LogInvariant($"PITCHTEX high cold={highColdMs:F2}ms detail={highTimings.DetailMs:F2} macro={highTimings.MacroMs:F2} wear={highTimings.WearMs:F2} upload={highTimings.UploadMs:F2} total={highTimings.TotalMs:F2} cached={highCachedMs:F3}ms same_object={ReferenceEquals(highSet, highCached)}");
        LogInvariant($"PITCHTEX high detail_phases base={highTimings.DetailBaseMs:F2} blade={highTimings.DetailBladeMs:F2} combine={highTimings.DetailCombineMs:F2} encode={highTimings.DetailEncodeMs:F2} mipmaps={highTimings.DetailMipmapMs:F2}");
        LogInvariant($"PITCHTEX high mips detail={highTimings.DetailMipmaps} macro={highTimings.MacroMipmaps} wear={highTimings.WearMipmaps} macro_rgba8_fallback={highTimings.MacroUsesRgba8}");

        // Second cold build (different pitch string, so the cache misses) measures the same work with
        // the JIT and thread pool already warm; the difference is one-time first-call overhead.
        var rebuildPitch = new PitchDimensions { Length = 105.01f };
        clock.Restart();
        PitchTextureFactory.Get(rebuildPitch, high);
        double highRebuildMs = clock.Elapsed.TotalMilliseconds;
        PitchTextureBuildTimings rebuildTimings = PitchTextureFactory.LastBuildTimings;
        LogInvariant($"PITCHTEX high rebuild={highRebuildMs:F2}ms detail={rebuildTimings.DetailMs:F2} macro={rebuildTimings.MacroMs:F2} wear={rebuildTimings.WearMs:F2} upload={rebuildTimings.UploadMs:F2} total={rebuildTimings.TotalMs:F2} phases base={rebuildTimings.DetailBaseMs:F2} blade={rebuildTimings.DetailBladeMs:F2} combine={rebuildTimings.DetailCombineMs:F2} encode={rebuildTimings.DetailEncodeMs:F2} mipmaps={rebuildTimings.DetailMipmapMs:F2}");

        DumpDetail(highSet, outputDirectory);
        DumpMacro(highSet, outputDirectory);
        DumpWear(highSet, outputDirectory);

        // Medium cold build: the half-resolution tier must fit the same budget.
        clock.Restart();
        PitchTextureSet mediumSet = PitchTextureFactory.Get(pitch, medium);
        double mediumColdMs = clock.Elapsed.TotalMilliseconds;
        PitchTextureBuildTimings mediumTimings = PitchTextureFactory.LastBuildTimings;
        LogInvariant($"PITCHTEX medium cold={mediumColdMs:F2}ms detail={mediumTimings.DetailMs:F2} macro={mediumTimings.MacroMs:F2} wear={mediumTimings.WearMs:F2} upload={mediumTimings.UploadMs:F2} total={mediumTimings.TotalMs:F2} mips detail={mediumTimings.DetailMipmaps} macro={mediumTimings.MacroMipmaps} wear={mediumTimings.WearMipmaps} macro_rgba8_fallback={mediumTimings.MacroUsesRgba8}");

        Image mediumDetail = mediumSet.DetailPacked.GetImage();
        LogInvariant($"PITCHTEX medium detail_image={mediumDetail.GetWidth()}x{mediumDetail.GetHeight()} format={mediumDetail.GetFormat()} mips={mediumDetail.GetMipmapCount()} bytes={mediumDetail.GetData().Length}");

        // Low tier binds the 1x1 neutral texel; confirm it is exactly the neutral contract value.
        PitchTextureSet lowSet = PitchTextureFactory.Get(pitch, PitchQualitySettings.For(PitchQuality.Low));
        Image lowDetail = lowSet.DetailPacked.GetImage();
        byte[] lowData = lowDetail.GetData();
        string lowRgba = lowData.Length >= 4 ? $"{lowData[0]},{lowData[1]},{lowData[2]},{lowData[3]}" : "n/a";
        LogInvariant($"PITCHTEX low detail_image={lowDetail.GetWidth()}x{lowDetail.GetHeight()} format={lowDetail.GetFormat()} bytes={lowData.Length} rgba={lowRgba}");

        GD.Print("PITCHTEX done");
        GetTree().Quit();
    }

    private static string ResolveOutputDirectory()
    {
        string outputDirectory = DefaultOutputDirectory;
        string[] userArguments = OS.GetCmdlineUserArgs();
        for (int i = 0; i < userArguments.Length; i++)
        {
            if (string.Equals(userArguments[i], "--out", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < userArguments.Length)
                outputDirectory = userArguments[i + 1];
        }

        return outputDirectory;
    }

    // -------------------------------------------------------------------------------------------------
    // Detail texture: packed normal + micro albedo + micro AO
    // -------------------------------------------------------------------------------------------------

    private static void DumpDetail(PitchTextureSet set, string outputDirectory)
    {
        Image image = set.DetailPacked.GetImage();
        int width = image.GetWidth();
        int height = image.GetHeight();
        byte[] data = image.GetData();
        int expectedBytes = ExpectedMipChainBytes(width, height, 4);

        LogInvariant($"PITCHTEX detail_image={width}x{height} format={image.GetFormat()} mips={image.GetMipmapCount()} bytes={data.Length} expected_with_mips={expectedBytes}");

        int mip0Bytes = Math.Min(data.Length, width * height * 4);
        double albedoSum = 0.0;
        double occlusionSum = 0.0;
        int albedoMin = 255;
        int albedoMax = 0;
        int occlusionMin = 255;
        float maxTilt = 0f;
        int normalTiltClamped = 0;
        for (int i = 0; i + 3 < mip0Bytes; i += 4)
        {
            float normalX = data[i] / 127.5f - 1f;
            float normalZ = data[i + 1] / 127.5f - 1f;
            float tilt = MathF.Sqrt(normalX * normalX + normalZ * normalZ);
            if (tilt > maxTilt) maxTilt = tilt;
            if (tilt > 0.59f) normalTiltClamped++;

            int albedo = data[i + 2];
            int occlusion = data[i + 3];
            albedoSum += albedo;
            occlusionSum += occlusion;
            if (albedo < albedoMin) albedoMin = albedo;
            if (albedo > albedoMax) albedoMax = albedo;
            if (occlusion < occlusionMin) occlusionMin = occlusion;
        }

        double pixels = mip0Bytes / 4.0;
        LogInvariant($"PITCHTEX detail_stats albedo_min={albedoMin}({albedoMin / 255.0:F3}) albedo_mean={albedoSum / pixels / 255.0:F3} albedo_max={albedoMax}({albedoMax / 255.0:F3}) ao_min={occlusionMin}({occlusionMin / 255.0:F3}) ao_mean={occlusionSum / pixels / 255.0:F3} normal_max_tilt={maxTilt:F3} pixels_at_clamp={normalTiltClamped / pixels * 100.0:F2}%");

        (double horizontal, double vertical) = SeamRatios(data, width, height, 4);
        LogInvariant($"PITCHTEX detail_seam wrap_horiz={horizontal:F3} wrap_vert={vertical:F3} (1.000 == no seam)");

        SavePreview(ColouriseDetail(image, 1), outputDirectory, "detail-albedo.png");
        SavePreview(ColouriseNormal(image), outputDirectory, "detail-normal.png");
        SavePreview(ColouriseDetail(image, 2), outputDirectory, "detail-tiled2x2.png");
    }

    /// <summary>Grass-coloured preview: micro albedo drives the palette, the packed normal drives shading.</summary>
    private static Image ColouriseDetail(Image detail, int tiles)
    {
        int width = detail.GetWidth();
        int height = detail.GetHeight();
        byte[] source = detail.GetData();
        int sourceBytes = Math.Min(source.Length, width * height * 4);
        int outputWidth = width * tiles;
        int outputHeight = height * tiles;
        var output = new byte[outputWidth * outputHeight * 4];

        // Reference palette from docs/football-grass.md Appendix A (#2D5E2E .. #3B7132).
        const float DarkR = 0.176f, DarkG = 0.369f, DarkB = 0.180f;
        const float LightR = 0.231f, LightG = 0.443f, LightB = 0.196f;
        // Directional light for the preview: low sun from the upper-left of the texture.
        const float LightX = -0.42f, LightUp = 0.79f, LightZ = -0.44f;
        const float PreviewGain = 2.05f;

        for (int y = 0; y < outputHeight; y++)
        {
            int sourceY = y % height;
            for (int x = 0; x < outputWidth; x++)
            {
                int sourceX = x % width;
                int sourceIndex = (sourceY * width + sourceX) * 4;
                if (sourceIndex + 3 >= sourceBytes)
                    continue;

                float normalX = source[sourceIndex] / 127.5f - 1f;
                float normalZ = source[sourceIndex + 1] / 127.5f - 1f;
                float normalUp = MathF.Sqrt(MathF.Max(0f, 1f - normalX * normalX - normalZ * normalZ));
                float albedo = source[sourceIndex + 2] / 255f;
                float occlusion = source[sourceIndex + 3] / 255f;

                float palette = Math.Clamp((albedo - 0.30f) * (1f / 0.42f), 0f, 1f);
                float red = DarkR + (LightR - DarkR) * palette;
                float green = DarkG + (LightG - DarkG) * palette;
                float blue = DarkB + (LightB - DarkB) * palette;

                float sun = MathF.Max(0f, normalX * LightX + normalUp * LightUp + normalZ * LightZ);
                float shade = (0.60f + 0.78f * sun) * (0.62f + 0.38f * occlusion) * PreviewGain;

                int outputIndex = (y * outputWidth + x) * 4;
                output[outputIndex] = ToByte(red * shade);
                output[outputIndex + 1] = ToByte(green * shade);
                output[outputIndex + 2] = ToByte(blue * shade);
                output[outputIndex + 3] = 255;
            }
        }

        return Image.CreateFromData(outputWidth, outputHeight, false, Image.Format.Rgba8, output);
    }

    /// <summary>Standard tangent-space normal preview: encoded X in R, encoded Y in G, reconstructed Z in B.</summary>
    private static Image ColouriseNormal(Image detail)
    {
        int width = detail.GetWidth();
        int height = detail.GetHeight();
        byte[] source = detail.GetData();
        int sourceBytes = Math.Min(source.Length, width * height * 4);
        var output = new byte[width * height * 4];
        for (int i = 0; i + 3 < sourceBytes; i += 4)
        {
            float normalX = source[i] / 127.5f - 1f;
            float normalZ = source[i + 1] / 127.5f - 1f;
            float normalUp = MathF.Sqrt(MathF.Max(0f, 1f - normalX * normalX - normalZ * normalZ));
            output[i] = source[i];
            output[i + 1] = source[i + 1];
            output[i + 2] = ToByte(0.5f + 0.5f * normalUp);
            output[i + 3] = 255;
        }

        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, output);
    }

    // -------------------------------------------------------------------------------------------------
    // Macro noise and wear mask
    // -------------------------------------------------------------------------------------------------

    private static void DumpMacro(PitchTextureSet set, string outputDirectory)
    {
        Image image = set.MacroNoise.GetImage();
        int width = image.GetWidth();
        int height = image.GetHeight();
        byte[] data = image.GetData();
        int mip0Bytes = Math.Min(data.Length, width * height);

        double sum = 0.0;
        double sumSquares = 0.0;
        int minimum = 255;
        int maximum = 0;
        for (int i = 0; i < mip0Bytes; i++)
        {
            int value = data[i];
            sum += value;
            sumSquares += value * (double)value;
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
        }

        double mean = sum / mip0Bytes;
        double standardDeviation = Math.Sqrt(Math.Max(sumSquares / mip0Bytes - mean * mean, 0.0));
        LogInvariant($"PITCHTEX macro_image={width}x{height} format={image.GetFormat()} mips={image.GetMipmapCount()} bytes={data.Length} expected_with_mips={ExpectedMipChainBytes(width, height, 1)}");
        LogInvariant($"PITCHTEX macro_stats mean={mean / 255.0:F3} std={standardDeviation / 255.0:F3} min={minimum / 255.0:F3} max={maximum / 255.0:F3}");

        (double horizontal, double vertical) = SeamRatios(data, width, height, 1);
        LogInvariant($"PITCHTEX macro_seam wrap_horiz={horizontal:F3} wrap_vert={vertical:F3} (1.000 == no seam)");

        SavePreview(Greyscale(image), outputDirectory, "macro.png");
    }

    private static void DumpWear(PitchTextureSet set, string outputDirectory)
    {
        Image image = set.WearMap.GetImage();
        int width = image.GetWidth();
        int height = image.GetHeight();
        byte[] data = image.GetData();
        int mip0Bytes = Math.Min(data.Length, width * height);
        double sum = 0.0;
        int maximum = 0;
        var histogram = new int[256];
        for (int i = 0; i < mip0Bytes; i++)
        {
            int value = data[i];
            sum += value;
            if (value > maximum) maximum = value;
            histogram[value]++;
        }

        LogInvariant($"PITCHTEX wear_image={width}x{height} format={image.GetFormat()} mips={image.GetMipmapCount()} bytes={data.Length}");
        LogInvariant($"PITCHTEX wear_stats mean={sum / mip0Bytes / 255.0:F3} max={maximum / 255.0:F3} p50={Percentile(histogram, mip0Bytes, 0.50):F3} p90={Percentile(histogram, mip0Bytes, 0.90):F3} p99={Percentile(histogram, mip0Bytes, 0.99):F3}");

        // The shader's perceptibility budget: worn = mix(grass, soil*0.62, wear*0.42) * (1 - wear*0.20).
        // Report how much of the pitch is above the levels where that reads as a visible scuff, and the
        // area each threshold covers in square metres (one texel = pitch area / texel count).
        double texelSquareMetres = 105.0 * 68.0 / mip0Bytes;
        int above30 = 0;
        int above50 = 0;
        for (int i = 0; i < mip0Bytes; i++)
        {
            if (data[i] > 77) above30++;
            if (data[i] > 128) above50++;
        }

        LogInvariant($"PITCHTEX wear_area above_0.30={above30 * texelSquareMetres:F1}m2 ({above30 / (double)mip0Bytes * 100.0:F2}%) above_0.50={above50 * texelSquareMetres:F1}m2 ({above50 / (double)mip0Bytes * 100.0:F2}%)");

        SavePreview(Greyscale(image), outputDirectory, "wear.png");
    }

    /// <summary>Percentile (0..1) of a byte histogram of <paramref name="count"/> samples.</summary>
    private static double Percentile(int[] histogram, int count, double percentile)
    {
        int target = (int)Math.Round(percentile * (count - 1));
        int running = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            running += histogram[value];
            if (running > target)
                return value / 255.0;
        }

        return 1.0;
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    private static Image Greyscale(Image source)
    {
        int width = source.GetWidth();
        int height = source.GetHeight();
        byte[] data = source.GetData();
        int pixelCount = Math.Min(width * height, data.Length);
        int channels = source.GetFormat() == Image.Format.Rgba8 ? 4 : 1;
        var output = new byte[width * height * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            byte value = data[i * channels];
            int j = i * 4;
            output[j] = value;
            output[j + 1] = value;
            output[j + 2] = value;
            output[j + 3] = 255;
        }

        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, output);
    }

    /// <summary>
    /// Mean absolute difference across the wrap edge divided by the same metric for an interior seam.
    /// A seamless tile gives a ratio near 1; a real seam gives a much larger edge difference.
    /// </summary>
    private static (double Horizontal, double Vertical) SeamRatios(byte[] data, int width, int height, int channels)
    {
        double edgeHorizontal = 0.0;
        double interiorHorizontal = 0.0;
        double edgeVertical = 0.0;
        double interiorVertical = 0.0;
        int samplesHorizontal = 0;
        int samplesVertical = 0;

        for (int y = 0; y < height; y++)
        {
            int left = (y * width) * channels;
            int right = (y * width + width - 1) * channels;
            int middle = (y * width + width / 2) * channels;
            int middleLeft = (y * width + width / 2 - 1) * channels;
            for (int c = 0; c < channels; c++)
            {
                edgeHorizontal += Math.Abs(data[left + c] - data[right + c]);
                interiorHorizontal += Math.Abs(data[middle + c] - data[middleLeft + c]);
                samplesHorizontal++;
            }
        }

        for (int x = 0; x < width; x++)
        {
            int top = x * channels;
            int bottom = ((height - 1) * width + x) * channels;
            int middle = ((height / 2) * width + x) * channels;
            int middleTop = ((height / 2 - 1) * width + x) * channels;
            for (int c = 0; c < channels; c++)
            {
                edgeVertical += Math.Abs(data[top + c] - data[bottom + c]);
                interiorVertical += Math.Abs(data[middle + c] - data[middleTop + c]);
                samplesVertical++;
            }
        }

        double horizontal = interiorHorizontal <= 0.0 ? 0.0 : edgeHorizontal / interiorHorizontal;
        double vertical = interiorVertical <= 0.0 ? 0.0 : edgeVertical / interiorVertical;
        return (horizontal, vertical);
    }

    private static int ExpectedMipChainBytes(int width, int height, int channels)
    {
        int total = 0;
        while (true)
        {
            total += Math.Max(1, width) * Math.Max(1, height) * channels;
            if (width == 1 && height == 1)
                break;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return total;
    }

    private static void SavePreview(Image image, string outputDirectory, string fileName)
    {
        string path = outputDirectory + "/" + fileName;
        Error error = image.SavePng(path);
        double megabytes = image.GetData().Length / (1024.0 * 1024.0);
        LogInvariant($"PITCHTEX saved {fileName} {image.GetWidth()}x{image.GetHeight()} raw={megabytes:F2}MiB err={error}");
    }

    private static void LogInvariant(FormattableString message) =>
        GD.Print(message.ToString(CultureInfo.InvariantCulture));

    private static byte ToByte(float unitValue) =>
        (byte)Math.Clamp((int)(unitValue * 255f + 0.5f), 0, 255);
}
