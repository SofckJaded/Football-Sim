using System;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Presentation;
using FootballSim.Rules;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Behaviour gates for the presentation stages (plan phases P1-P5): each test reduces one line of the plan's
/// movement/action/keeper specification to an observable property of <see cref="PlayerPose"/>.
///
/// These are presentation expectations only — nothing here can change the simulation, and the mirror
/// symmetry / distance-driven-stride tests exist specifically to prove the dynamics are physics-driven rather
/// than canned or frame-rate dependent.
/// </summary>
public sealed class PlayerDynamicsBehaviourTests
{
    private const float Tick = 1f / 60f;

    // =========================================================================================
    // P1 locomotion
    // =========================================================================================

    [Fact]
    public void SprintLeansTheBodyForward()
    {
        var rig = new Rig();
        rig.Player.Velocity = new Vector3(8f, 0f, 0f);
        rig.Player.FacingXZ = new Vector2(1f, 0f);
        rig.Run(90);

        Assert.True(rig.Solver.BodyPitch < -0.18f,
            $"a full sprint should pitch the body forward (plan 5.2.1: -16 deg), pitch was " +
            $"{SpringMath.RadToDeg(rig.Solver.BodyPitch):F1} deg");
    }

    [Fact]
    public void HardBrakingPitchesTheBodyBack()
    {
        var rig = new Rig();
        rig.Player.FacingXZ = new Vector2(1f, 0f);
        rig.Player.Velocity = new Vector3(8f, 0f, 0f);
        rig.Run(60);

        // Dig in: ~8.4 m/s^2 of deceleration, the plan's braking pose (5.2.2: +6..+14 deg lean-back).
        float peakPitch = float.MinValue;
        for (int i = 0; i < 30; i++)
        {
            rig.Player.Velocity = new Vector3(8f - 0.14f * (i + 1), 0f, 0f);
            rig.Run(1);
            peakPitch = MathF.Max(peakPitch, rig.Solver.BodyPitch);
        }

        Assert.True(peakPitch > 0.05f,
            $"hard braking should pitch the body back, peak pitch was {SpringMath.RadToDeg(peakPitch):F1} deg");
    }

    [Fact]
    public void StrideCycleIsDrivenByDistanceNotByFrameCount()
    {
        // Same speed, same total distance, different time steps: the gait phase must match, because the
        // phase integrates distance / stride length. If it were a wall-clock oscillator the feet would skate
        // and the two runs would disagree.
        var fast = new Rig();
        fast.Player.FacingXZ = new Vector2(1f, 0f);
        fast.Player.Velocity = new Vector3(4f, 0f, 0f);
        fast.Run(61, Tick * 2f); // 60 productive steps of 1/30 s

        var slow = new Rig();
        slow.Player.FacingXZ = new Vector2(1f, 0f);
        slow.Player.Velocity = new Vector3(4f, 0f, 0f);
        slow.Run(121, Tick); // 120 productive steps of 1/60 s

        float fastFoot = fast.Solver.Pose.LeftFootLocal.X;
        float slowFoot = slow.Solver.Pose.LeftFootLocal.X;
        Assert.True(MathF.Abs(fastFoot - slowFoot) < 0.05f,
            $"distance-driven stride should agree across frame rates; 1/30 s run gave {fastFoot:F3} m, " +
            $"1/60 s run gave {slowFoot:F3} m");
    }

    [Fact]
    public void ShieldBallDropsTheCentreOfGravity()
    {
        var shielding = new Rig();
        shielding.Player.LastCommand = Command(shield: true);
        shielding.Run(50);

        var upright = new Rig();
        upright.Run(50);

        Assert.True(shielding.Solver.BodyOffsetY < upright.Solver.BodyOffsetY - 0.10f,
            $"shielding should plant the player low (plan 5.3: -0.14 m); shield {shielding.Solver.BodyOffsetY:F3} m vs " +
            $"upright {upright.Solver.BodyOffsetY:F3} m");
    }

    [Fact]
    public void MotionIntensityScalesVisibleMotion()
    {
        var subtle = new Rig(intensity: 0.3f);
        subtle.Player.Velocity = new Vector3(8f, 0f, 0f);
        subtle.Player.FacingXZ = new Vector2(1f, 0f);
        subtle.Run(90);

        var punchy = new Rig(intensity: 2.0f);
        punchy.Player.Velocity = new Vector3(8f, 0f, 0f);
        punchy.Player.FacingXZ = new Vector2(1f, 0f);
        punchy.Run(90);

        // Compare the composed pose: MotionIntensity is applied exactly once, at compose time.
        float subtlePitch = subtle.Solver.Pose.BodyRotation.Z;
        float punchyPitch = punchy.Solver.Pose.BodyRotation.Z;
        Assert.True(MathF.Abs(punchyPitch) > MathF.Abs(subtlePitch) * 2f,
            $"MotionIntensity must scale the pose: 2.0 gave {punchyPitch:F3} rad, " +
            $"0.3 gave {subtlePitch:F3} rad");
    }

    // =========================================================================================
    // P2 kicks, headers, jumps, landings
    // =========================================================================================

    [Fact]
    public void JumpTakeoffStretchesAndLandingSquashFiresThenSettles()
    {
        var rig = new Rig();
        rig.Run(10);

        // Takeoff edge: the plan's "+12% stretch".
        rig.Player.IsJumping = true;
        rig.Player.Position = new Vector3(0f, 0.30f, 0f);
        rig.Player.Velocity = new Vector3(2f, 2.5f, 0f);
        rig.VisualPosition = rig.Player.Position;
        rig.Run(1);

        float peakStretch = 0f;
        for (int i = 0; i < 12; i++)
        {
            rig.Player.Position = new Vector3(0f, 0.45f, 0f);
            rig.VisualPosition = rig.Player.Position;
            rig.Run(1);
            peakStretch = MathF.Max(peakStretch, rig.Solver.Squash);
        }

        Assert.True(peakStretch > 0.05f, $"takeoff should stretch the body, peak squash was {peakStretch:F3}");

        // Landing edge: the last airborne vertical speed is the impact evidence.
        rig.Player.Velocity = new Vector3(2f, -3.6f, 0f);
        rig.Run(1);
        rig.Player.IsJumping = false;
        rig.Player.Position = Vector3.Zero;
        rig.Player.Velocity = Vector3.Zero;
        rig.VisualPosition = Vector3.Zero;
        rig.Run(1);

        float deepestSquash = 0f;
        for (int i = 0; i < 30; i++)
        {
            rig.Run(1);
            deepestSquash = MathF.Min(deepestSquash, rig.Solver.Squash);
        }

        Assert.True(deepestSquash < -0.05f,
            $"landing should squash the body (plan 5.4.4: -0.14), deepest squash was {deepestSquash:F3}");

        // Fires once and settles: nothing re-triggers while the player stays grounded (the residual is the
        // idle breathing squash, which is allowed to keep the chest moving).
        rig.Run(240);
        Assert.True(rig.Solver.Squash > -0.03f, $"landing squash must not re-fire, squash was {rig.Solver.Squash:F3}");
    }

    [Fact]
    public void HeaderContactWhipsTheWaistAndNeckForward()
    {
        var rig = new Rig();
        rig.Run(10);
        rig.Player.LastCommand = Command(kick: KickIntentType.Header, aim: new Vector3(1f, 0f, 0f));
        rig.Solver.NotifyContact(new BallContactEvent(1, BallContactKind.Kick, rig.Player.Id,
            rig.Player.Team, new Vector3(1f, 1.8f, 0f)) { Intent = KickIntentType.Header });
        rig.Run(3);

        Assert.True(rig.Solver.Pose.TorsoRotation.Z < -0.15f,
            $"header should snap the waist forward (plan 5.4.3: -28 deg), waist was " +
            $"{SpringMath.RadToDeg(rig.Solver.Pose.TorsoRotation.Z):F1} deg");
        Assert.True(rig.Solver.Pose.HeadRotation.Z < -0.20f,
            $"header should nod the head (plan 5.4.3: -35 deg), head was " +
            $"{SpringMath.RadToDeg(rig.Solver.Pose.HeadRotation.Z):F1} deg");
    }

    [Fact]
    public void ShotKickWhipsTheTorsoAndAchievesTheFollowThrough()
    {
        var rig = new Rig();
        rig.Run(10);
        rig.Player.LastCommand = Command(kick: KickIntentType.Shot, power: 0.9f,
            aim: new Vector3(1f, 0f, 0f));
        rig.Solver.NotifyContact(new BallContactEvent(1, BallContactKind.Kick, rig.Player.Id,
            rig.Player.Team, new Vector3(0.6f, 0.11f, 0f)) { Intent = KickIntentType.Shot });
        rig.Run(10);

        Assert.True(MathF.Abs(rig.Solver.Pose.TorsoRotation.Y) > 0.30f,
            $"a powered shot should rotate the waist through (plan 5.5), yaw was " +
            $"{SpringMath.RadToDeg(rig.Solver.Pose.TorsoRotation.Y):F1} deg");
        Assert.True(MathF.Abs(rig.Solver.Pose.RightFootLocal.X - RigDimensions.RightFootRest.X) > 0.08f,
            "the kicking boot should swing through the ball");
    }

    [Fact]
    public void ClearanceFollowThroughIsStrongerThanAShortPass()
    {
        float clearanceYaw = KickYawOf(KickIntentType.Clearance, 0.7f);
        float passYaw = KickYawOf(KickIntentType.GroundPass, 0.7f);

        Assert.True(MathF.Abs(clearanceYaw) > MathF.Abs(passYaw) * 1.3f,
            $"a clearance hack (45 deg) should out-twist a compact pass (18-26 deg): " +
            $"clearance {SpringMath.RadToDeg(clearanceYaw):F1} deg vs pass {SpringMath.RadToDeg(passYaw):F1} deg");
    }

    [Fact]
    public void TackleRequestDropsTheHipsAndLeansTheBodyForward()
    {
        var rig = new Rig();
        rig.Run(10);
        rig.Player.LastCommand = Command(tackle: true);
        rig.Run(15);

        Assert.True(rig.Solver.BodyPitch < -0.20f,
            $"a tackle should lean the body steeply forward (plan 5.6: -28..-38 deg), pitch was " +
            $"{SpringMath.RadToDeg(rig.Solver.BodyPitch):F1} deg");
        Assert.True(rig.Solver.BodyOffsetY < -0.08f,
            $"a tackle should drop the hips (plan 5.6: -0.22..-0.30 m), offset was {rig.Solver.BodyOffsetY:F3} m");
    }

    [Fact]
    public void LosingBalanceTriggersAKnockbackWobble()
    {
        var rig = new Rig();
        rig.NearestOpponentDistance = 0.7f;
        rig.NearestOpponentDirectionXZ = new Vector2(1f, 0f); // opponent in front -> knock backward
        rig.Run(10);

        rig.Player.Balance01 = 0.60f; // sharp dispossession signature
        rig.Run(3);

        Assert.True(MathF.Abs(rig.Solver.Pose.TorsoRotation.Z) > 0.08f ||
                    MathF.Abs(rig.Solver.Pose.TorsoRotation.X) > 0.08f,
            "a sharp balance loss should knock the torso into a wobble (plan 5.6)");
    }

    // =========================================================================================
    // P2 dribble carrier / P5 goal reaction / P4 keeper touches
    // =========================================================================================

    [Fact]
    public void BallCarrierLeansLowerOverTheBall()
    {
        var carrying = new Rig();
        carrying.ControlsBall = true;
        carrying.Player.Velocity = new Vector3(2f, 0f, 0f);
        carrying.Player.FacingXZ = new Vector2(1f, 0f);
        carrying.Run(40);

        var plain = new Rig();
        plain.Player.Velocity = new Vector3(2f, 0f, 0f);
        plain.Player.FacingXZ = new Vector2(1f, 0f);
        plain.Run(40);

        Assert.True(carrying.Solver.Pose.TorsoRotation.Z < plain.Solver.Pose.TorsoRotation.Z - 0.03f,
            $"a carrier should lean over the ball (plan 5.2.5: -5 deg), carrier waist " +
            $"{SpringMath.RadToDeg(carrying.Solver.Pose.TorsoRotation.Z):F1} deg vs " +
            $"{SpringMath.RadToDeg(plain.Solver.Pose.TorsoRotation.Z):F1} deg");
    }

    [Fact]
    public void ScoringTeamCelebratesAndTheReactionIsBounded()
    {
        var scorer = new Rig();
        scorer.Run(10);
        scorer.Phase = MatchPhase.GoalScored;
        scorer.ScoringTeam = scorer.Player.Team;
        scorer.ScorerPlayerId = scorer.Player.Id;
        scorer.Run(6);

        Assert.True(scorer.Solver.Pose.BodyOffset.Y > 0.03f,
            $"a goal celebration should lift the scorer (plan 5.9), body offset was " +
            $"{scorer.Solver.Pose.BodyOffset.Y:F3} m");

        // The reaction is a short bounce, not a permanent pose change.
        scorer.Run(240);
        Assert.InRange(scorer.Solver.Pose.BodyOffset.Y, -0.05f, 0.05f);
    }

    [Fact]
    public void KeeperCleanCatchAbsorbsTheBallIntoTheChest()
    {
        var rig = new Rig(goalkeeper: true);
        rig.Run(10);
        rig.Solver.NotifyKeeperEvent(KeeperVisualEvent.CleanCatch);
        rig.HoldsBall = true;
        rig.Run(2);

        Assert.True(rig.Solver.Squash < -0.03f,
            $"a clean catch should absorb through the body (plan 5.7.5: -0.09 squash), squash was {rig.Solver.Squash:F3}");
        Assert.True(rig.Solver.Pose.TorsoRotation.Z < -0.03f,
            "a clean catch should curl the chest inward around the ball");
    }

    [Fact]
    public void KeeperReflexParryRecoilsAwayFromTheShot()
    {
        var rig = new Rig(goalkeeper: true);
        rig.Run(10);
        // Shot travelling from the keeper's right toward his left: the body must be torqued away from it.
        rig.BallVelocity = new Vector3(0f, 0f, -25f);
        rig.Solver.NotifyKeeperEvent(KeeperVisualEvent.ReflexParry);
        rig.Run(3);

        Assert.True(MathF.Abs(rig.Solver.Pose.TorsoRotation.X) > 0.08f,
            $"a 25 m/s parry should torque the torso away from the shot (plan 5.7.4), roll was " +
            $"{SpringMath.RadToDeg(rig.Solver.Pose.TorsoRotation.X):F1} deg");
    }

    // =========================================================================================
    // P4 goalkeeper dives
    // =========================================================================================

    [Fact]
    public void KeeperDiveRollIsMirrorSymmetric()
    {
        float rightRoll = DiveRollPeak(1f);
        float leftRoll = DiveRollPeak(-1f);

        Assert.True(rightRoll > 0.6f, $"a right dive should roll the body right, peak was {rightRoll:F2} rad");
        Assert.True(leftRoll < -0.6f, $"a left dive should roll the body left, peak was {leftRoll:F2} rad");
        Assert.True(MathF.Abs(MathF.Abs(rightRoll) - MathF.Abs(leftRoll)) < MathF.Abs(rightRoll) * 0.30f,
            $"left and right dives must mirror: right {rightRoll:F2} rad vs left {leftRoll:F2} rad");
    }

    [Fact]
    public void KeeperDiveArcRisesWithTheInterceptHeight()
    {
        float lowArc = DiveArcPeak(0.10f);
        float midArc = DiveArcPeak(0.95f);
        float topArc = DiveArcPeak(1.95f);

        Assert.True(lowArc < midArc && midArc < topArc,
            $"dive height tiers must be ordered Low < Mid < TopCorner, got {lowArc:F3} / {midArc:F3} / {topArc:F3} m");
        Assert.True(topArc > midArc + 0.10f, "the top-corner tier should be a clearly bigger leap");
    }

    [Fact]
    public void KeeperReturnsUprightAfterTheDiveEnds()
    {
        var rig = new Rig(goalkeeper: true);
        rig.Run(10);
        StartDive(rig, 1f, 1.0f);
        rig.Run(40);
        Assert.True(MathF.Abs(rig.Solver.DiveRoll) > 0.4f);

        rig.Player.IsDiving = false;
        rig.Player.DribbleTouchTimer = 0f;
        rig.Run(90);

        Assert.True(MathF.Abs(rig.Solver.DiveRoll) < 0.20f,
            $"the keeper must get up after the dive, roll was {rig.Solver.DiveRoll:F2} rad");
    }

    // =========================================================================================
    // P5 head scanning
    // =========================================================================================

    [Fact]
    public void ScanHeadYawUsesTheRigSignConvention()
    {
        var rig = new Rig();
        // Positive ScanStatus.HeadYawDegrees points to the player's right (+Z); a positive rig +Y rotation
        // turns left, so the rendered yaw must be the negative of the scan yaw (plan section 2).
        rig.Scan = new ScanStatus(ScanAttentionKind.Threat, 35f, true);
        rig.Run(40);

        float expected = -SpringMath.DegToRad(35f);
        Assert.InRange(rig.Solver.Pose.HeadRotation.Y, expected - 0.05f, expected + 0.05f);
    }

    // =========================================================================================
    // Fixtures and helpers
    // =========================================================================================

    private static float KickYawOf(KickIntentType intent, float power)
    {
        var rig = new Rig();
        rig.Run(10);
        rig.Player.LastCommand = Command(kick: intent, power: power, aim: new Vector3(1f, 0f, 0f));
        rig.Solver.NotifyContact(new BallContactEvent(1, BallContactKind.Kick, rig.Player.Id,
            rig.Player.Team, new Vector3(0.6f, 0.11f, 0f)) { Intent = intent });
        rig.Run(10);
        return rig.Solver.Pose.TorsoRotation.Y;
    }

    private static float DiveRollPeak(float diveSide)
    {
        var rig = new Rig(goalkeeper: true);
        rig.Run(10);
        StartDive(rig, diveSide, 1.0f);
        float peak = 0f;
        for (int i = 0; i < 40; i++)
        {
            rig.Run(1);
            peak = MathF.Abs(rig.Solver.DiveRoll) > MathF.Abs(peak) ? rig.Solver.DiveRoll : peak;
        }

        return peak;
    }

    private static float DiveArcPeak(float interceptHeight)
    {
        var rig = new Rig(goalkeeper: true);
        rig.Run(10);
        StartDive(rig, 1f, interceptHeight);
        float peak = float.MinValue;
        for (int i = 0; i < 45; i++)
        {
            rig.Run(1);
            peak = MathF.Max(peak, rig.Solver.Pose.BodyOffset.Y);
        }

        return peak;
    }

    private static void StartDive(Rig rig, float diveSide, float interceptHeight)
    {
        rig.Player.LastCommand = Command(
            dive: true,
            diveDirection: new Vector3(0f, interceptHeight, diveSide));
        rig.Player.IsDiving = true;
        rig.Player.DribbleTouchTimer = BallContactRules.KeeperDiveSeconds;
        rig.GoalkeeperState = GoalkeeperState.Saving;
    }

    private static FootballerCommand Command(
        bool shield = false,
        bool tackle = false,
        KickIntentType kick = KickIntentType.None,
        float power = 0f,
        Vector3 aim = default,
        bool dive = false,
        Vector3 diveDirection = default)
    {
        if (aim == default) aim = new Vector3(1f, 0f, 0f);
        return new FootballerCommand(
            new Vector2(1f, 0f), new Vector2(1f, 0f), false, shield, tackle, false, dive,
            diveDirection, kick, aim, power, 0f);
    }

    private static FootballerState CreatePlayer(bool goalkeeper)
    {
        // Fixed ids: the per-player golden-ratio phase offsets are deterministic, so tests are repeatable.
        return new FootballerState
        {
            Id = goalkeeper ? 1 : 7,
            Team = TeamId.Home,
            ShirtNumber = 1,
            Role = goalkeeper ? TacticalRole.Goalkeeper : TacticalRole.Striker,
            Attributes = new PlayerAttributes(),
            Tendencies = new PlayerTendencies()
        };
    }

    /// <summary>
    /// One player plus everything the solver input needs, so a test can state only what it cares about.
    /// Fixed per-role ids keep the per-player golden-ratio phase offsets deterministic between rigs.
    /// </summary>
    private sealed class Rig
    {
        public readonly FootballerState Player;
        public readonly PlayerDynamicsSolver Solver;

        public Rig(bool goalkeeper = false, float intensity = 1f)
        {
            Player = CreatePlayer(goalkeeper);
            Solver = new PlayerDynamicsSolver(Player.Id, goalkeeper,
                new PlayerDynamicsSettings { MotionIntensity = intensity });
        }

        public Vector3 VisualPosition;
        public Vector3 BallPosition = new(2f, 0.11f, 0f);
        public Vector3 BallVelocity;
        public ScanStatus Scan = ScanStatus.None;
        public GoalkeeperState GoalkeeperState = GoalkeeperState.Positioning;
        public bool HoldsBall;
        public bool ControlsBall;
        public float NearestOpponentDistance;
        public Vector2 NearestOpponentDirectionXZ;
        public MatchPhase Phase = MatchPhase.FirstHalf;
        public TeamId? ScoringTeam;
        public int? ScorerPlayerId;

        public void Run(int ticks, float dt = Tick)
        {
            for (int i = 0; i < ticks; i++)
            {
                Player.PreviousPosition = Player.Position;
                var input = new PlayerDynamicsInput
                {
                    Player = Player,
                    VisualPosition = VisualPosition,
                    BallPosition = BallPosition,
                    BallVelocity = BallVelocity,
                    Scan = Scan,
                    GoalkeeperState = GoalkeeperState,
                    Phase = Phase,
                    ScoringTeam = ScoringTeam,
                    ScorerPlayerId = ScorerPlayerId,
                    HoldsBall = HoldsBall,
                    ControlsBall = ControlsBall,
                    NearestOpponentDistance = NearestOpponentDistance,
                    NearestOpponentDirectionXZ = NearestOpponentDirectionXZ
                };
                Solver.Step(in input, dt);
            }
        }
    }
}
