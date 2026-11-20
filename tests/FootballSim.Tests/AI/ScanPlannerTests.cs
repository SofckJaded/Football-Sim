using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.1 — the scan planner: head/gaze independent of body facing, a bounded head turn, a bounded hold, a
/// return phase, and the attention rule that decides what is worth looking at.
///
/// These are model tests: every number (turn rate, hold, cooldown, refresh age) comes from
/// <see cref="AiRuntimeConfig"/>, so a test that changes one of them and sees a different head motion is
/// also evidence that the setting is consumed rather than declared.
/// </summary>
public sealed class ScanPlannerTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    [Fact]
    public void Gaze_IsTheBodyFacingRotatedByTheHeadYaw()
    {
        var forward = new Vector2(1f, 0f);
        Assert.Equal(forward, new ScanStatus(ScanAttentionKind.None, 0f, false).GazeFrom(forward));

        // The sign convention: +90° turns the gaze from +X toward +Z.
        Vector2 left = new ScanStatus(ScanAttentionKind.DangerSpace, 90f, false).GazeFrom(forward);
        Assert.True(Vector2.Distance(left, new Vector2(0f, 1f)) < 1e-4f, $"gaze was {left}");

        // An unnormalised facing cannot produce an unnormalised gaze.
        Vector2 normalised = new ScanStatus(ScanAttentionKind.None, 0f, false).GazeFrom(new Vector2(3f, 0f));
        Assert.Equal(forward, normalised);
    }

    [Fact]
    public void AReceivingPlayer_TurnsHisHeadAtTheConfiguredRate_AndReachesTheNeckLimit()
    {
        AiRuntimeConfig ai = AiRuntimeConfig.Default;
        var planner = new ScanPlanner(ai);
        float maxStep = ai.ScanTurnRateDegreesPerSecond * Dt;

        ScanSituation situation = Situation(2, receiving: true);
        ScanStatus previous = planner.Think(situation with { Tick = 0 });
        float largestStep = 0f;
        float furthestYaw = MathF.Abs(previous.HeadYawDegrees);
        bool scanned = false;
        for (int tick = 1; tick <= 40; tick++)
        {
            ScanStatus status = planner.Think(situation with { Tick = tick });
            float step = MathF.Abs(status.HeadYawDegrees - previous.HeadYawDegrees);
            if (step > largestStep) largestStep = step;
            furthestYaw = MathF.Max(furthestYaw, MathF.Abs(status.HeadYawDegrees));
            scanned |= status.IsScanning;
            Assert.True(step <= maxStep + 1e-3f,
                $"the head moved {step:F2}° in one tick, above the configured {maxStep:F2}°");
            previous = status;
        }

        // The danger space is directly behind, so the turn is the full neck limit on one shoulder.
        Assert.True(MathF.Abs(furthestYaw - ai.ScanMaxHeadTurnDegrees) < 0.5f,
            $"the furthest head yaw was {furthestYaw:F1}°, not the {ai.ScanMaxHeadTurnDegrees:F0}° limit");
        Assert.True(scanned, "the head turned but never completed a visual fix");
        Assert.True(largestStep > 0f);
    }

    [Fact]
    public void ASlowerHead_IsMeasurablySlower_SoTheSettingIsConsumed()
    {
        // 120°/s against the 600°/s default: ten ticks of turn is 20° rather than 100°.
        var slow = new ScanPlanner(new AiRuntimeConfig { ScanTurnRateDegreesPerSecond = 120f });
        var normal = new ScanPlanner(new AiRuntimeConfig());

        ScanSituation situation = Situation(2, receiving: true);
        for (int tick = 0; tick < 10; tick++)
        {
            slow.Think(situation with { Tick = tick });
            normal.Think(situation with { Tick = tick });
        }

        Assert.True(MathF.Abs(slow.Think(situation with { Tick = 10 }).HeadYawDegrees) < 30f,
            "a 120°/s head reached the target too quickly to be the configured rate");
        Assert.True(MathF.Abs(normal.Think(situation with { Tick = 10 }).HeadYawDegrees) > 90f);
    }

    [Fact]
    public void TheNeckLimit_ClampsEvenAThreatDirectlyBehind()
    {
        AiRuntimeConfig ai = AiRuntimeConfig.Default;
        var planner = new ScanPlanner(ai);
        Vector2 behind = new(-1f, 0f);

        float furthestYaw = 0f;
        bool threatScan = false;
        for (int tick = 0; tick <= 30; tick++)
        {
            ScanStatus status = planner.Think(Situation(3, receiving: false, staleThreat: true,
                threatDirection: behind, threatDistance: 8f) with { Tick = tick });
            furthestYaw = MathF.Max(furthestYaw, MathF.Abs(status.HeadYawDegrees));
            threatScan |= status.IsScanning && status.Attention == ScanAttentionKind.Threat;
            Assert.True(MathF.Abs(status.HeadYawDegrees) <= ai.ScanMaxHeadTurnDegrees + 1e-3f,
                $"head yaw {status.HeadYawDegrees:F1}° passed the {ai.ScanMaxHeadTurnDegrees:F0}° neck limit");
        }

        Assert.True(threatScan, "the head never completed a fix on the believed threat");
        Assert.True(furthestYaw > ai.ScanMaxHeadTurnDegrees - 0.5f);
    }

    [Fact]
    public void AScan_HoldsForItsWindow_ThenReleasesAndReturnsToTheBodyFacing()
    {
        AiRuntimeConfig ai = AiRuntimeConfig.Default;
        var planner = new ScanPlanner(ai);
        ScanSituation situation = Situation(2, receiving: true);

        int scanStarts = 0;
        int scanningTicks = 0;
        bool wasScanning = false;
        int holdTicks = 0;
        for (int tick = 0; tick <= 80; tick++)
        {
            ScanStatus status = planner.Think(situation with { Tick = tick });
            if (status.IsScanning && !wasScanning)
            {
                scanStarts++;
                scanningTicks = 0;
            }
            if (status.IsScanning) scanningTicks++;
            wasScanning = status.IsScanning;

            if (scanStarts == 1 && !status.IsScanning)
            {
                holdTicks = scanningTicks;
                break;
            }
        }

        int expectedHold = (int)MathF.Round(ai.ScanHoldSeconds / Dt);
        Assert.Equal(1, scanStarts);
        Assert.InRange(holdTicks, expectedHold, expectedHold);
        Assert.Equal(1, planner.ScanStarts);

        // The reception ends; the head returns to the body facing and stays there.
        ScanStatus released = default;
        for (int tick = 81; tick <= 130; tick++)
        {
            released = planner.Think(situation with { Tick = tick, Receiving = false });
        }

        Assert.False(released.IsScanning);
        Assert.Equal(ScanAttentionKind.None, released.Attention);
        Assert.True(MathF.Abs(released.HeadYawDegrees) < 1e-2f, $"the head did not return: {released.HeadYawDegrees:F1}°");
    }

    [Fact]
    public void AnIdlePlayer_KeepsHisEyesOnHisBodyFacing()
    {
        var planner = new ScanPlanner(AiRuntimeConfig.Default);
        ScanSituation idle = Situation(2, receiving: false);

        for (int tick = 0; tick < 60; tick++)
        {
            ScanStatus status = planner.Think(idle with { Tick = tick });
            Assert.Equal(ScanStatus.None, status);
        }

        Assert.Equal(0, planner.ScanStarts);
    }

    [Fact]
    public void AStaleBelievedThreat_IsScanned_EvenWhenTheBallIsNotComing()
    {
        var planner = new ScanPlanner(AiRuntimeConfig.Default);
        Vector2 right = new(0f, 1f); // +Z is +90° from the +X body facing

        ScanStatus fix = default;
        bool scanned = false;
        for (int tick = 0; tick <= 25; tick++)
        {
            ScanStatus status = planner.Think(Situation(2, receiving: false, staleThreat: true,
                threatDirection: right, threatDistance: 10f) with { Tick = tick });
            if (status.IsScanning && !scanned)
            {
                fix = status;
                scanned = true;
            }
        }

        Assert.True(scanned, "the head never fixed on the stale threat");
        Assert.Equal(ScanAttentionKind.Threat, fix.Attention);
        Assert.True(MathF.Abs(fix.HeadYawDegrees - 90f) < 1e-2f, $"the head aimed at {fix.HeadYawDegrees:F1}°");
        Assert.Equal(1, planner.ThreatScanStarts);
        Assert.Equal(0, planner.DangerScanStarts);
    }

    [Fact]
    public void TheBelievedThreat_WinsOverTheDangerSpace_WhenBothAreAvailable()
    {
        Vector2 right = new(0f, 1f);

        float threatYaw = FurthestYaw(new ScanPlanner(AiRuntimeConfig.Default),
            Situation(2, receiving: true, staleThreat: true, threatDirection: right, threatDistance: 10f));
        float dangerYaw = FurthestYaw(new ScanPlanner(AiRuntimeConfig.Default),
            Situation(2, receiving: true));

        Assert.True(MathF.Abs(threatYaw - 90f) < 0.5f, $"the threat scan aimed at {threatYaw:F1}°");
        Assert.True(MathF.Abs(dangerYaw - 100f) < 0.5f, $"the danger scan aimed at {dangerYaw:F1}°");
        Assert.True(threatYaw < dangerYaw, "the danger scan did not turn further than the threat scan");
    }

    /// <summary>Largest absolute head yaw a repeated situation reaches in 30 ticks.</summary>
    private static float FurthestYaw(ScanPlanner planner, ScanSituation situation)
    {
        float furthest = 0f;
        for (int tick = 0; tick < 30; tick++)
        {
            ScanStatus status = planner.Think(situation with { Tick = tick });
            furthest = MathF.Max(furthest, MathF.Abs(status.HeadYawDegrees));
        }
        return furthest;
    }

    [Fact]
    public void ConsecutiveDangerScans_AlternateShoulders()
    {
        var planner = new ScanPlanner(AiRuntimeConfig.Default);
        ScanSituation situation = Situation(2, receiving: true);

        var episodes = new List<float>();
        bool wasScanning = false;
        for (int tick = 0; tick <= 260; tick++)
        {
            ScanStatus status = planner.Think(situation with { Tick = tick });
            if (status.IsScanning && !wasScanning) episodes.Add(status.HeadYawDegrees);
            wasScanning = status.IsScanning;
        }

        Assert.True(episodes.Count >= 2, $"only {episodes.Count} danger scans in 4.3 s");
        Assert.True(episodes[0] > 50f, $"first scan was {episodes[0]:F1}°");
        Assert.True(episodes[1] < -50f, $"second scan was {episodes[1]:F1}° instead of the other shoulder");
    }

    [Fact]
    public void ACarrier_DoesNotScan()
    {
        var planner = new ScanPlanner(AiRuntimeConfig.Default);
        ScanSituation carrier = Situation(2, receiving: false, staleThreat: true, threatDirection: new Vector2(-1f, 0f),
            threatDistance: 6f);

        for (int tick = 0; tick <= 40; tick++)
        {
            ScanStatus status = planner.Think(carrier with { Tick = tick, IsCarrier = true });
            Assert.Equal(ScanStatus.None, status);
        }

        Assert.Equal(0, planner.ScanStarts);
    }

    [Fact]
    public void ThePlanner_IsDeterministic()
    {
        var left = new ScanPlanner(AiRuntimeConfig.Default);
        var right = new ScanPlanner(AiRuntimeConfig.Default);
        ScanSituation situation = Situation(7, receiving: true, staleThreat: true, threatDirection: new Vector2(0.3f, 0.7f),
            threatDistance: 12f);

        for (int tick = 0; tick < 400; tick++)
        {
            ScanStatus a = left.Think(situation with { Tick = tick });
            ScanStatus b = right.Think(situation with { Tick = tick });
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Forget_DropsTheHeadState_ButKeepsTheMatchTelemetry()
    {
        var planner = new ScanPlanner(AiRuntimeConfig.Default);
        ScanSituation situation = Situation(2, receiving: true);

        for (int tick = 0; tick <= 30; tick++) planner.Think(situation with { Tick = tick });
        int starts = planner.ScanStarts;
        Assert.True(starts > 0);

        planner.Forget(2);

        ScanStatus fresh = planner.Think(situation with { Tick = 31 });
        Assert.False(fresh.IsScanning);
        Assert.True(MathF.Abs(fresh.HeadYawDegrees - 10f) < 1e-2f,
            $"a forgotten player restarted at {fresh.HeadYawDegrees:F1}° instead of one bounded step");
        Assert.Equal(starts, planner.ScanStarts);
    }

    /// <summary>A situation with the body facing +X and no threat unless one is named.</summary>
    private static ScanSituation Situation(
        int playerId,
        bool receiving,
        bool staleThreat = false,
        Vector2 threatDirection = default,
        float threatDistance = 0f) => new(
        playerId,
        Tick: 0,
        BodyFacingXZ: new Vector2(1f, 0f),
        Receiving: receiving,
        IsCarrier: false,
        StaleThreatKnown: staleThreat,
        ThreatDirectionXZ: threatDirection,
        ThreatDistanceMeters: threatDistance);
}
