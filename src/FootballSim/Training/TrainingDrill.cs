using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Physics;
using FootballSim.Rules;

namespace FootballSim.Training;

/// <summary>
/// Where a drill puts the lone trainee and the ball when the drill (re)starts.
/// </summary>
public readonly record struct TrainingSpawn(
    Vector3 PlayerPosition,
    Vector3 BallPosition,
    Vector2 PlayerFacingXZ
);

/// <summary>
/// An AI participant a drill wants on the pitch (a stationary passer, a goalkeeper, ...).
/// Ids must be unique and different from <see cref="TrainingSession.HumanPlayerId"/>.
/// </summary>
public sealed record TrainingActorSpec(
    int Id,
    TeamId Team,
    int ShirtNumber,
    TacticalRole Role,
    Vector3 Position,
    Vector2 FacingXZ,
    IFootballerController Controller
);

/// <summary>
/// A self-contained practice drill. New drills only need to derive from this class and be
/// registered in <see cref="TrainingDrillRegistry"/>; the session, HUD and reset pipeline
/// stay unchanged.
/// </summary>
public abstract class TrainingDrill
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }

    /// <summary>False for announced-but-not-yet-built drills; the menu shows them disabled.</summary>
    public virtual bool IsAvailable => true;

    /// <summary>+1 when the trainee attacks the +X goal, -1 when attacking the -X goal.</summary>
    public abstract float AttackingDirectionX { get; }

    public abstract TrainingSpawn CreateSpawn(PitchDimensions pitch);

    /// <summary>AI participants (teammates or opponents) this drill needs.</summary>
    public virtual IReadOnlyList<TrainingActorSpec> CreateAiActors(
        PitchDimensions pitch,
        TrainingSpawn humanSpawn,
        MatchCommunicationContext communication)
        => Array.Empty<TrainingActorSpec>();

    /// <summary>True on the tick the drill's scoring condition is met.</summary>
    public abstract bool IsGoal(BallState ball, PitchDimensions pitch);

    /// <summary>True when the ball has left the playable area without scoring.</summary>
    public abstract bool IsBallOutOfBounds(BallState ball, PitchDimensions pitch);

    /// <summary>Per-tick drill rule (for example "the trainee received the pass → next rep").</summary>
    public virtual void OnStep(TrainingSession session, float dt) { }

    /// <summary>Called whenever the session respawns everyone.</summary>
    public virtual void OnReset() { }
}

/// <summary>Shared bounds check for training drills.</summary>
public static class TrainingPitch
{
    public const float OutMargin = 0.35f;

    public static bool IsOutOfBounds(Vector3 position, PitchDimensions pitch)
    {
        if (MathF.Abs(position.Z) > pitch.HalfWidth + OutMargin) return true;
        if (position.X < -pitch.HalfLength - OutMargin) return true;
        if (position.X > pitch.HalfLength + OutMargin) return true;

        // A ball resting inside either goal mouth counts as out of play for drills without goals.
        if (position.X > pitch.HalfLength && MathF.Abs(position.Z) < pitch.HalfGoalWidth) return true;
        if (position.X < -pitch.HalfLength && MathF.Abs(position.Z) < pitch.HalfGoalWidth) return true;
        return false;
    }
}

/// <summary>
/// Free Training — 1 player + goal on an otherwise empty pitch.
/// </summary>
public class FreeTrainingDrill : TrainingDrill
{
    public const string DrillId = "free-training";

    /// <summary>Resting ball radius — the shared physics value, never a private constant (audit F22).</summary>
    public const float BallRadius = BallGeometry.DefaultRadius;

    public override string Id => DrillId;
    public override string Title => "Free Training";
    public override string Description => "1 player + goal";
    public override float AttackingDirectionX => 1f;

    public override TrainingSpawn CreateSpawn(PitchDimensions pitch)
    {
        // Just outside the attacking penalty area, close enough that the box is one touch away.
        float boxEdgeX = pitch.HalfLength - pitch.PenaltyAreaLength;
        float z = -MathF.Min(pitch.PenaltyAreaWidth * 0.25f, 6.0f);

        return new TrainingSpawn(
            new Vector3(boxEdgeX - 4.0f, 0f, z),
            new Vector3(boxEdgeX - 2.0f, BallRadius, z),
            new Vector2(1f, 0f));
    }

    public override bool IsGoal(BallState ball, PitchDimensions pitch)
    {
        Vector3 p = ball.Position;
        return p.X > pitch.HalfLength &&
               MathF.Abs(p.Z) < pitch.HalfGoalWidth &&
               p.Y <= pitch.GoalHeight;
    }

    public override bool IsBallOutOfBounds(BallState ball, PitchDimensions pitch) =>
        TrainingPitch.IsOutOfBounds(ball.Position, pitch);
}

/// <summary>
/// Free Training with an AI goalkeeper defending the goal.
/// </summary>
public sealed class FreeTrainingGoalkeeperDrill : FreeTrainingDrill
{
    public new const string DrillId = "free-training-gk";
    public const int GoalkeeperPlayerId = 2;

    public override string Id => DrillId;
    public override string Title => "Free Training with Goalkeeper";
    public override string Description => "1 player + GK + goal";

    public override IReadOnlyList<TrainingActorSpec> CreateAiActors(
        PitchDimensions pitch,
        TrainingSpawn humanSpawn,
        MatchCommunicationContext communication)
    {
        return new[]
        {
            new TrainingActorSpec(
                GoalkeeperPlayerId,
                TeamId.Away,
                1,
                TacticalRole.Goalkeeper,
                new Vector3(pitch.HalfLength - 1.4f, 0f, 0f),
                new Vector2(-1f, 0f),
                new GoalkeeperController(pitch))
        };
    }
}

/// <summary>
/// Passing &amp; Receiving: one stationary AI teammate with the ball, one trainee. The teammate
/// either passes immediately or waits for the trainee's call-for-pass (the existing Pass control).
/// </summary>
public abstract class PassingDrillBase : TrainingDrill
{
    public const int AiPlayerId = 2;
    private const float AiX = -7f;

    private bool _passInFlight;

    protected abstract bool PassOnlyOnRequest { get; }

    /// <summary>Number of passes the AI has played this session (kept across respawns).</summary>
    public int AiPasses { get; private set; }

    public override float AttackingDirectionX => 1f;

    public override TrainingSpawn CreateSpawn(PitchDimensions pitch) => new(
        new Vector3(0f, 0f, 0f),
        new Vector3(AiX + 0.55f, FreeTrainingDrill.BallRadius, 0f),
        new Vector2(-1f, 0f));

    public override IReadOnlyList<TrainingActorSpec> CreateAiActors(
        PitchDimensions pitch,
        TrainingSpawn humanSpawn,
        MatchCommunicationContext communication)
    {
        return new[]
        {
            new TrainingActorSpec(
                AiPlayerId,
                TeamId.Home,
                8,
                TacticalRole.CentralMidfielder,
                new Vector3(AiX, 0f, 0f),
                new Vector2(1f, 0f),
                new TrainingPasserController(
                    TrainingSession.HumanPlayerId,
                    communication,
                    PassOnlyOnRequest,
                    pitch))
        };
    }

    public override bool IsGoal(BallState ball, PitchDimensions pitch) => false;

    public override bool IsBallOutOfBounds(BallState ball, PitchDimensions pitch) =>
        TrainingPitch.IsOutOfBounds(ball.Position, pitch);

    public override void OnStep(TrainingSession session, float dt)
    {
        BallState ball = session.Ball;
        float speedSquared = ball.Velocity.LengthSquared();

        // A real pass leaves the passer's foot at passing speed; a dribble touch does not.
        if (ball.LastTouchedByPlayerId == AiPlayerId && speedSquared > 25f)
        {
            if (!_passInFlight)
            {
                AiPasses++;
                _passInFlight = true;
            }
        }
        else if (ball.LastTouchedByPlayerId != AiPlayerId || speedSquared < 16f)
        {
            // The ball was touched by the trainee or has settled back at the AI's feet.
            _passInFlight = false;
        }
    }

    public override void OnReset()
    {
        _passInFlight = false;
    }
}

public sealed class PassingReceivingOneDrill : PassingDrillBase
{
    public const string DrillId = "passing-receiving-1";

    public override string Id => DrillId;
    public override string Title => "Passing & Receiving 1";
    public override string Description => "2 players — pass it back to the AI";
    protected override bool PassOnlyOnRequest => false;
}

public sealed class PassingReceivingTwoDrill : PassingDrillBase
{
    public const string DrillId = "passing-receiving-2";

    public override string Id => DrillId;
    public override string Title => "Passing & Receiving 2";
    public override string Description => "2 players — call, then pass it back";
    protected override bool PassOnlyOnRequest => true;
}

/// <summary>
/// The drills offered on the Practice page. Add future drills to this list and they appear in the
/// menu automatically.
/// </summary>
public static class TrainingDrillRegistry
{
    private static readonly List<TrainingDrill> Drills = new()
    {
        new FreeTrainingDrill(),
        new FreeTrainingGoalkeeperDrill(),
        new PassingReceivingOneDrill(),
        new PassingReceivingTwoDrill()
    };

    public static IReadOnlyList<TrainingDrill> All => Drills;

    public static TrainingDrill? Find(string id) =>
        Drills.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public static TrainingDrill Default => Drills[0];
}
