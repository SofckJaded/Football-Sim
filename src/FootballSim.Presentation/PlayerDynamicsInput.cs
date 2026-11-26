using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;

namespace FootballSim.Presentation;

/// <summary>
/// One player's read-only presentation snapshot for one rendered frame: the authoritative simulation
/// state plus the few world facts the pose solver needs. It holds <see cref="Player"/> by reference but
/// the solver and every partial of it **must treat that object as read-only** — the presentation layer
/// never writes simulation state, never draws from a simulation RNG, and never feeds an offset back into
/// reach or contact.
///
/// The manager builds this once per player per frame; it is a value type with no heap fields other than
/// the player reference, so the whole visual update allocates nothing.
/// </summary>
public readonly struct PlayerDynamicsInput
{
    /// <summary>The authoritative simulation state. Read-only for every presentation consumer.</summary>
    public required FootballerState Player { get; init; }

    /// <summary>Interpolated render position (previous tick → current tick, alpha blended).</summary>
    public required Vector3 VisualPosition { get; init; }

    /// <summary>Authoritative ball position (not interpolated: actions read the contact-time fact).</summary>
    public Vector3 BallPosition { get; init; }

    /// <summary>Authoritative ball velocity, used by the keeper stage for reaction direction.</summary>
    public Vector3 BallVelocity { get; init; }

    /// <summary>A7.1 head-scan state from the player's own observation. Neutral in training.</summary>
    public ScanStatus Scan { get; init; }

    /// <summary>Keeper lifecycle phase. Meaningless for an outfielder.</summary>
    public GoalkeeperState GoalkeeperState { get; init; }

    /// <summary>True when this snapshot is the locked human player.</summary>
    public bool IsHumanControlled { get; init; }

    /// <summary>Live kick-button charge for the human wind-up coil; 0 for AI players.</summary>
    public float ChargedPower01 { get; init; }

    /// <summary>Current match phase, for stoppage micro-reactions.</summary>
    public MatchPhase Phase { get; init; }

    /// <summary>The team that just scored, when <see cref="Phase"/> is <see cref="MatchPhase.GoalScored"/>.</summary>
    public TeamId? ScoringTeam { get; init; }

    /// <summary>The scorer, when known: used to make the scorer's celebration stronger.</summary>
    public int? ScorerPlayerId { get; init; }

    /// <summary>True when this player is holding the ball in his hands (keeper catch / claim).</summary>
    public bool HoldsBall { get; init; }

    /// <summary>True when the possession model says this player controls the ball at his feet.</summary>
    public bool ControlsBall { get; init; }

    /// <summary>
    /// Distance to the nearest active opponent in metres; <c>0</c> when unknown or no opponent exists.
    /// </summary>
    public float NearestOpponentDistance { get; init; }

    /// <summary>World XZ unit direction from this player toward that opponent.</summary>
    public Vector2 NearestOpponentDirectionXZ { get; init; }

    // --- Derived convenience reads (computed, never stored) -----------------------------------

    /// <summary>Authoritative horizontal speed.</summary>
    public float SpeedXZ => SpringMath.HorizontalLength(Player.Velocity);

    /// <summary>The last command the simulation executed for this player.</summary>
    public FootballerCommand Command => Player.LastCommand;

    /// <summary>True when the ball is within reach of a header this frame (2D distance).</summary>
    public bool BallIsAboveHeadHeight => BallPosition.Y > FootballerState.PlayerHeight - 0.15f;
}

/// <summary>
/// Presentation-only tuning. Everything here changes how the model <i>looks</i>; none of it can reach the
/// simulation. <see cref="MotionIntensity"/> is the single dial the plan asks for: 1.0 is grounded
/// athletic realism, higher values exaggerate the squash/stretch for readability from the broadcast camera.
/// </summary>
public sealed class PlayerDynamicsSettings
{
    /// <summary>Global exaggeration scalar for every dynamic offset and angle. 1.0 = tuned default.</summary>
    public float MotionIntensity { get; set; } = 1f;

    /// <summary>Which micro-parts a built rig gets.</summary>
    public PlayerVisualStyle Style { get; set; } = PlayerVisualStyle.Recommended;

    /// <summary>Master switch: false makes every player render in the neutral rest pose.</summary>
    public bool EnableDynamics { get; set; } = true;

    /// <summary>Disables the head-scan / ball-gaze stage (useful while tuning locomotion).</summary>
    public bool EnableGaze { get; set; } = true;

    /// <summary>Disables the keeper articulation stage (useful while tuning outfield motion).</summary>
    public bool EnableGoalkeeperDynamics { get; set; } = true;

    /// <summary>
    /// A position jump larger than this (metres) between rendered frames is treated as a teleport
    /// (kickoff reset, restart placement, training reset) and snaps the pose instead of interpolating.
    /// </summary>
    public float TeleportSnapDistance { get; set; } = 2.5f;
}
