using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using FootballSim.Scenarios;
using FootballSim.Tactics;

namespace FootballSim;

public sealed class MatchAnalyticsSummary
{
    public int TotalTicks { get; set; }
    public int HomePossessionTicks { get; set; }
    public int AwayPossessionTicks { get; set; }
    public int HomePassesAttempted { get; set; }
    public int AwayPassesAttempted { get; set; }

    /// <summary>Passes a team-mate actually controlled next (audit F17: separate from attempts).</summary>
    public int HomePassesCompleted { get; set; }
    public int AwayPassesCompleted { get; set; }

    public int HomeShots { get; set; }
    public int AwayShots { get; set; }
    public int HomeGoals { get; set; }
    public int AwayGoals { get; set; }

    /// <summary>
    /// Ticks on which the team had live control of the ball (ownership model), as opposed to
    /// <see cref="HomePossessionTicks"/>, which also counts a deliberate play that is still
    /// travelling/loose. Neither is last touch (audit F17).
    /// </summary>
    public int HomeControlledPossessionTicks { get; set; }
    public int AwayControlledPossessionTicks { get; set; }

    public float HomeAverageBallTerritoryX { get; set; }
    public float AwayAverageBallTerritoryX { get; set; }
    public float HomeAverageTeamWidthMeters { get; set; }
    public float AwayAverageTeamWidthMeters { get; set; }

    public float HomePossessionShare => (HomePossessionTicks + AwayPossessionTicks) > 0
        ? (float)HomePossessionTicks / (HomePossessionTicks + AwayPossessionTicks)
        : 0.5f;

    public float HomeControlledPossessionShare => (HomeControlledPossessionTicks + AwayControlledPossessionTicks) > 0
        ? (float)HomeControlledPossessionTicks / (HomeControlledPossessionTicks + AwayControlledPossessionTicks)
        : 0.5f;

    public float HomePassCompletionShare => HomePassesAttempted > 0
        ? (float)HomePassesCompleted / HomePassesAttempted
        : 0f;

    public float AwayPassCompletionShare => AwayPassesAttempted > 0
        ? (float)AwayPassesCompleted / AwayPassesAttempted
        : 0f;
}

public sealed class MatchSimulationEngine
{
    private readonly PreMatchConfig _config;
    private readonly PitchDimensions _pitch;
    private readonly SeededRandom _executionRng;
    private readonly SeededRandom _perceptionRng;
    private readonly BallPhysicsSolver _ballSolver;
    private readonly FootballerExecutionSystem _executionSystem;
    private readonly PerceptionSystem _perceptionSystem;
    private readonly TeamTacticalCoordinator _tacticalCoordinator;
    private readonly MatchRulesEngine _rulesEngine;
    private readonly BallPossessionTracker _possessionTracker = new();
    private readonly MatchStatisticsTracker _statistics;
    private readonly BallResponderElection _ballResponderElection;
    private readonly TeamJobCoordinator _teamJobs;
    private readonly List<FootballerState> _players = new();
    private readonly Dictionary<int, IFootballerController> _controllers = new();
    private readonly Dictionary<int, FootballerObservation> _latestObservations = new();
    private readonly MatchCommunicationContext _communication = new();
    private readonly List<BallContactEvent> _contactLog = new();
    private readonly ContactGeometrySnapshot _contactGeometry = new();

    private double _homeTerritoryAccum;
    private double _awayTerritoryAccum;
    private double _homeWidthAccum;
    private double _awayWidthAccum;
    private int _sampleCount;

    public MatchSimulationEngine(PreMatchConfig? config = null, PitchDimensions? pitch = null)
        : this(config ?? new PreMatchConfig(), pitch ?? new PitchDimensions(), scenario: null)
    {
    }

    /// <summary>
    /// Starts the match from an explicit world instead of a generated squad. See
    /// <see cref="MatchScenario"/>: the fixture supplies positions, velocities, attributes, ball state
    /// and phase, so a tactical assertion has a fixed, reproducible subject.
    /// </summary>
    public MatchSimulationEngine(MatchScenario scenario)
        : this(scenario?.BuildConfig() ?? throw new ArgumentNullException(nameof(scenario)),
               scenario.Pitch, scenario)
    {
        Scenario = scenario;
    }

    private MatchSimulationEngine(PreMatchConfig config, PitchDimensions pitch, MatchScenario? scenario)
    {
        _config = config;
        _pitch = pitch;

        if (scenario is null)
        {
            // Audit F22: reject a degenerate configuration before it is used to build squads. A negative
            // half length, a human id that is not in the match or a goal wider than the pitch used to be
            // accepted and only surfaced later as an out-of-range index or a divided-by-zero clock.
            SimulationConfigValidation.Validate(_config, _pitch);
        }
        else
        {
            // An explicit world is validated as the pieces it supplies. The even-team-count rule only
            // describes a generated squad, and a fixture such as 3v2 is deliberately unbalanced.
            scenario.Validate();
            SimulationConfigValidation.ValidatePitch(_pitch);
            SimulationConfigValidation.ValidateTacticalProfile(_config.HomeProfile, "Home profile");
            SimulationConfigValidation.ValidateTacticalProfile(_config.AwayProfile, "Away profile");
        }

        // A1.3: one stream per consumer. Perception no longer advances the stream that decides whether
        // a pass is controlled, so a perception change cannot silently rewrite every later physics
        // exchange — and a command-only replay sees the same execution stream whether or not the AI ran.
        _executionRng = RandomStreams.Create(_config.Seed, RandomStream.Execution);
        _perceptionRng = RandomStreams.Create(_config.Seed, RandomStream.Perception);
        _ballSolver = new BallPhysicsSolver(null, _pitch);
        _executionSystem = new FootballerExecutionSystem(_pitch);
        _perceptionSystem = new PerceptionSystem();
        _tacticalCoordinator = new TeamTacticalCoordinator(_pitch);
        _ballResponderElection = new BallResponderElection(_pitch);
        _teamJobs = new TeamJobCoordinator(_pitch);
        _rulesEngine = new MatchRulesEngine(_pitch, _ballSolver.Parameters.Radius);
        Analytics = new MatchAnalyticsSummary();
        _statistics = new MatchStatisticsTracker(Analytics);

        if (scenario is null)
        {
            Ball = new BallState
            {
                Position = new Vector3(0f, BallGeometry.RestingCentreHeight(_ballSolver.Parameters.Radius), 0f),
                PreviousPosition = new Vector3(0f, BallGeometry.RestingCentreHeight(_ballSolver.Parameters.Radius), 0f)
            };

            InitializeSquads();
            _rulesEngine.StartMatch(_players, Ball);
        }
        else
        {
            Ball = scenario.CreateBallState(_ballSolver.Parameters.Radius);
            InitializeScenarioSquads(scenario);
            _rulesEngine.StartScenario(
                scenario.MatchClockSeconds, scenario.Period, scenario.HomeScore, scenario.AwayScore,
                $"Scenario '{scenario.Name}'");

            BallPossessionSnapshot seeded = scenario.CreatePossessionSnapshot();
            _possessionTracker.Seed(seeded);
            _tacticalCoordinator.SeedPossession(seeded.PossessionTeam);

            if (scenario.BallSetup == ScenarioBallSetup.KeeperHeld)
            {
                // A keeper holding the ball is the same state a catch produces, so the physics step is
                // skipped and the release delay applies exactly as it would after one.
                Ball.HeldByPlayerId = seeded.ControllingPlayerId;
                Ball.HeldReleaseDelaySeconds = 0f;
            }
        }
    }

    public int CurrentTick { get; private set; }
    public PitchDimensions Pitch => _pitch;

    /// <summary>
    /// The authoritative ball radius used by physics, contacts and the boundary rules. Presentation
    /// draws the ball from this value instead of its own constant (audit F22).
    /// </summary>
    public float BallRadius => _ballSolver.Parameters.Radius;

    public PreMatchConfig Config => _config;
    public BallState Ball { get; }
    public IReadOnlyList<FootballerState> Players => _players;
    public MatchRulesEngine Rules => _rulesEngine;
    public TeamTacticalCoordinator Tactics => _tacticalCoordinator;

    /// <summary>Authoritative ball-ownership snapshot for the current tick (audit F10).</summary>
    public BallPossessionSnapshot Possession => _possessionTracker.Current;

    /// <summary>
    /// The ball contacts players requested on the most recent live tick, with the ones execution
    /// refused and why (audit F17). Diagnostics/tests read this instead of inferring from commands.
    /// </summary>
    public IReadOnlyList<BallContactAttempt> LastContactAttempts => _executionSystem.ContactAttempts;

    /// <summary>
    /// Executed contacts, oldest first, bounded to <see cref="MaxContactLogEntries"/>. This exists
    /// because the execution layer's per-tick event list is consumed and cleared by the rules engine
    /// and statistics on the same tick, so nothing outside the engine could observe what actually
    /// happened without replaying the tick.
    ///
    /// It is diagnostics only: no decision reads it. Keeping it bounded means a long match cannot grow
    /// the log without limit, and the oldest entries are dropped first.
    /// </summary>
    public IReadOnlyList<BallContactEvent> ContactLog => _contactLog;

    public const int MaxContactLogEntries = 4096;

    /// <summary>
    /// Bounded log of controller decisions. Off by default; enable it from a test or a diagnostic tool
    /// before stepping. Recording allocates nothing beyond the entry itself and never consumes
    /// simulation randomness.
    /// </summary>
    public DecisionTraceBuffer DecisionTrace { get; } = new();

    /// <summary>Statistics tracker fed by the execution contacts (audit F17).</summary>
    public MatchStatisticsTracker Statistics => _statistics;

    /// <summary>Player currently elected as the ball responder for a team, if any.</summary>
    public int? GetBallResponder(TeamId team) => _ballResponderElection.GetResponder(team);

    /// <summary>Team-level off-ball job assignments for the current tick (audit F04).</summary>
    public TeamJobCoordinator TeamJobs => _teamJobs;
    public MatchAnalyticsSummary Analytics { get; }
    public HumanInputController? LockedHumanController { get; private set; }
    public bool IsSpectatorFastForwardActive => LockedHumanController?.IsSpectatorMode ?? false;

    /// <summary>
    /// The explicit world this engine was started from, or null for a normal match built from
    /// <see cref="PreMatchConfig"/>. Diagnostics use it to say which fixture a run belongs to.
    /// </summary>
    public MatchScenario? Scenario { get; }

    private void InitializeSquads()
    {
        int idCounter = 1;
        foreach (TeamId team in new[] { TeamId.Home, TeamId.Away })
        {
            for (int shirt = 1; shirt <= _config.PlayersPerTeam; shirt++)
            {
                int playerId = idCounter++;
                var (role, _, _) = TeamTacticalCoordinator.GetFormationRoleAndSlot(shirt, _config.PlayersPerTeam);

                var player = new FootballerState
                {
                    Id = playerId,
                    Team = team,
                    ShirtNumber = shirt,
                    Role = role,
                    Attributes = new PlayerAttributes
                    {
                        // Deterministic footedness per shirt so the same seed/replay is identical.
                        PreferredFoot = shirt is 3 or 7 or 11 ? Footedness.Left : Footedness.Right
                    },
                    Tendencies = PlayerTendencyProfile.ForRole(role)
                };

                // Audit F22: the squad is public state, so its attributes are validated once here
                // rather than letting a NaN speed reach the AI one frame later.
                SimulationConfigValidation.ValidatePlayerAttributes(player.Attributes, $"Player #{playerId}");
                SimulationConfigValidation.ValidateTendencies(player.Tendencies, $"Player #{playerId}");
                _players.Add(player);

                if (_config.DesignatedHumanPlayerId == playerId)
                {
                    var humanCtrl = new HumanInputController(playerId, _communication);
                    LockedHumanController = humanCtrl;
                    _controllers[playerId] = humanCtrl;
                }
                else if (role == TacticalRole.Goalkeeper)
                {
                    _controllers[playerId] = new GoalkeeperController(_pitch);
                }
                else
                {
                    _controllers[playerId] = new TacticalUtilityAiController(_pitch, _communication);
                }
            }
        }
    }

    /// <summary>
    /// Replaces a footballer's controller.
    ///
    /// Audit F21: the pre-match human lock is an invariant of the match, not of the optional initial
    /// config, and the engine's authoritative <see cref="LockedHumanController"/> reference must always
    /// describe the controller that is actually installed:
    /// <list type="bullet">
    /// <item>the id must belong to this match (no accidental new players in the dictionary);</item>
    /// <item>a <see cref="HumanInputController"/> may only drive the player it is locked to;</item>
    /// <item>after kickoff a human controller may only be installed for the player that was locked at
    /// construction — a match created without a human can never gain one mid-match;</item>
    /// <item>after kickoff the locked human player cannot be handed to a non-human controller, so live
    /// input, the responder election and spectator fast-forward keep pointing at a real object.</item>
    /// </list>
    /// Pre-match (before the first tick) any controller may be installed, including a labelled
    /// <see cref="ControllerSourceType.HumanPreMatchLocked"/> replay used by determinism tests.
    /// </summary>
    /// <summary>
    /// Builds the squads from the scenario's explicit player specs.
    ///
    /// Unlike <see cref="InitializeSquads"/> nothing is inferred from a formation table: role, shirt,
    /// attributes and tendency are whatever the fixture stated, and a spec may install its own
    /// controller (a scripted opponent). The human lock behaves exactly as in a normal match.
    /// </summary>
    private void InitializeScenarioSquads(MatchScenario scenario)
    {
        IReadOnlyList<ScenarioPlayer> specs = scenario.ResolveShirtNumbers();
        int? humanId = scenario.HumanPlayerId;
        int? ballOwnerId = scenario.BallSetup == ScenarioBallSetup.Loose
            ? null
            : scenario.BallOwnerPlayerId;

        foreach (ScenarioPlayer spec in specs)
        {
            var player = new FootballerState
            {
                Id = spec.Id,
                Team = spec.Team,
                ShirtNumber = spec.ShirtNumber,
                Role = spec.Role,
                Attributes = spec.Attributes,
                Tendencies = spec.Tendencies ?? PlayerTendencyProfile.ForRole(spec.Role),
                Position = spec.Position,
                PreviousPosition = spec.Position,
                Velocity = spec.Velocity,
                FacingXZ = spec.FacingXZ,
                Stamina01 = spec.Stamina01,
                TacticalAnchor = spec.Position,
                IsSubstitutedOrInjured = !spec.IsActiveOnPitch,

                // A fixture that hands this player the ball at kick-off is starting with the ball
                // under control, not with a first touch still to be won.
                ControlQuality01 = spec.Id == ballOwnerId ? 1.0f : 0f
            };

            _players.Add(player);

            IFootballerController controller;
            if (spec.Controller is not null)
            {
                controller = spec.Controller;
            }
            else if (humanId == spec.Id)
            {
                var human = new HumanInputController(spec.Id, _communication);
                LockedHumanController = human;
                controller = human;
            }
            else if (spec.Role == TacticalRole.Goalkeeper)
            {
                controller = new GoalkeeperController(_pitch);
            }
            else
            {
                controller = new TacticalUtilityAiController(_pitch, _communication);
            }

            _controllers[spec.Id] = controller;
        }
    }

    public void OverrideController(int playerId, IFootballerController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        bool knownPlayer = false;
        foreach (var player in _players)
        {
            if (player.Id != playerId) continue;
            knownPlayer = true;
            break;
        }
        if (!knownPlayer)
            throw new ArgumentOutOfRangeException(nameof(playerId), playerId,
                $"No footballer with id {playerId} is part of this match.");

        if (controller is HumanInputController human && human.LockedPlayerId != playerId)
        {
            throw new InvalidOperationException(
                $"Pre-match lock violation: a HumanInputController locked to Player #{human.LockedPlayerId} " +
                $"cannot control Player #{playerId}.");
        }

        bool isLockedHumanPlayer = _config.DesignatedHumanPlayerId == playerId ||
                                   LockedHumanController?.LockedPlayerId == playerId;

        if (CurrentTick > 0)
        {
            if (controller.SourceType == ControllerSourceType.HumanPreMatchLocked && !isLockedHumanPlayer)
            {
                throw new InvalidOperationException(
                    "Cannot switch human control to another footballer during an active match.");
            }

            if (isLockedHumanPlayer && controller is not HumanInputController)
            {
                throw new InvalidOperationException(
                    $"Cannot hand the locked human player (#{playerId}) to a non-human controller during an " +
                    "active match; the human lock is chosen before kickoff.");
            }
        }

        _controllers[playerId] = controller;

        // Keep the authoritative human reference consistent with the installed controller.
        if (controller is HumanInputController installedHuman)
            LockedHumanController = installedHuman;
        else if (isLockedHumanPlayer)
            LockedHumanController = null;
    }

    public IFootballerController GetController(int playerId) => _controllers[playerId];
    public FootballerObservation? GetLastObservation(int playerId) =>
        _latestObservations.TryGetValue(playerId, out var obs) ? obs : null;

    /// <summary>
    /// Returns the execution layer's accepted/refused contact outcomes to the controllers that
    /// buffer input, so a refused human kick is retried instead of silently lost (audit F17).
    /// </summary>
    private void ApplyContactFeedback()
    {
        IReadOnlyList<BallContactAttempt> attempts = _executionSystem.ContactAttempts;
        for (int i = 0; i < attempts.Count; i++)
        {
            BallContactAttempt attempt = attempts[i];
            if (attempt.Executed || !attempt.WantedContact) continue;
            if (_controllers.TryGetValue(attempt.PlayerId, out IFootballerController? controller) &&
                controller is HumanInputController human)
            {
                human.AcknowledgeContactAttempt(attempt);
            }
        }
    }

    public void StepTick()
    {
        float dt = SimConstants.FixedDeltaTime;

        // Audit F06: full time ends authoritative gameplay. The Godot node keeps calling StepTick,
        // so returning here must leave the match bit-for-bit unchanged.
        if (_rulesEngine.Phase == MatchPhase.FullTime) return;

        CurrentTick++;

        if (LockedHumanController != null && !LockedHumanController.IsSpectatorMode)
        {
            var humanPlayer = _players.First(p => p.Id == LockedHumanController.LockedPlayerId);
            if (!humanPlayer.IsActiveOnPitch)
                LockedHumanController.NotifyPlayerUnavailableForMatch();
        }

        float homeDirX = _rulesEngine.GetHomeAttackDirectionX();

        // Audit F06: a whistle/setup delay is not live play. Decisions, contacts and ball physics
        // are frozen; only the rules clock advances, so the restart can resume.
        bool livePlay = !_rulesEngine.IsPlaySuspended;

        if (livePlay)
        {
            // Audit F10: possession comes from the ownership model (control or a deliberate play),
            // not from a bare last touch, and is confirmed every tick so a change is immediate.
            _tacticalCoordinator.ObservePossession(_possessionTracker.Current, dt);

            // One centrally elected ball responder per team, using real time-to-reach. The human is
            // never elected as an AI responder, so an idle human cannot switch the AI off the ball.
            int? humanPlayerId = LockedHumanController?.LockedPlayerId;
            _ballResponderElection.Update(_players, Ball, _possessionTracker.Current, humanPlayerId);
            BallPossessionSnapshot possession = _possessionTracker.Current;

            if (CurrentTick % 4 == 1)
            {
                _tacticalCoordinator.UpdateTeamAnchors(
                    _players, Ball, _config.HomeProfile, _config.AwayProfile, homeDirX, dt * 4f,
                    _config.PlayersPerTeam);
            }

            // Audit F04: persistent team-level jobs relative to the actual carrier/loose ball.
            _teamJobs.Update(
                _players, Ball, possession, _tacticalCoordinator.ConfirmedPossessingTeam,
                _ballResponderElection.GetResponder(TeamId.Home),
                _ballResponderElection.GetResponder(TeamId.Away),
                homeDirX, CurrentTick, _config.HomeProfile, _config.AwayProfile);

            foreach (var p in _players)
            {
                if (!p.IsActiveOnPitch) continue;
                var profile = p.Team == TeamId.Home ? _config.HomeProfile : _config.AwayProfile;
                float attackDirX = p.Team == TeamId.Home ? homeDirX : -homeDirX;
                bool isTaker = _rulesEngine.DesignatedSetPieceTakerId == p.Id;

                var obs = _perceptionSystem.BuildObservation(
                    p, _players, Ball, _tacticalCoordinator.ConfirmedPossessingTeam,
                    _rulesEngine.Phase, _rulesEngine.MatchClockSeconds,
                    attackDirX, isTaker, profile, _perceptionRng,
                    possession, _ballResponderElection.IsResponder(p.Team, p.Id), _teamJobs.Get(p.Id));
                _latestObservations[p.Id] = obs;

                var cmd = _controllers[p.Id].Decide(obs, dt);
                p.LastCommand = cmd;

                if (DecisionTrace.IsEnabled)
                    RecordDecisionTrace(p, obs);
            }

            _executionSystem.UpdateLocomotionAndContacts(
                _players, Ball, _executionRng, dt,
                (tacklerId, victimId, pos, sev) => _rulesEngine.EvaluateTackleFoul(tacklerId, victimId, pos, sev, _players, Ball),
                CurrentTick,
                new ReceptionAssistContext(possession.State, possession.IntendedReceiverId, humanPlayerId));

            // Audit F14: the first touch after a restart IS the restart's play. Stamp it with the
            // restart kind so the rules engine can apply the Law 11 exemptions (no offside from a
            // throw-in/goal kick/corner) and the no-direct-goal rule for indirect restarts.
            _rulesEngine.StampRestartOrigin(_executionSystem.ContactEvents);

            // A1.4: freeze the world as it was when these contacts were made. The rules engine runs
            // after the physics step below, so a laws decision read from the live ball would be
            // decided by where the ball is heading rather than where it was played.
            _contactGeometry.Capture(CurrentTick, _players, Ball.Position);

            // Audit F17: statistics come from the contacts execution actually made, never from the
            // commands above. A kick that lost the tick's arbitration is not a pass attempt.
            _statistics.ApplyContacts(_executionSystem.ContactEvents, CurrentTick);
            AppendContactLog(_executionSystem.ContactEvents);
            ApplyContactFeedback();

            // Consume this tick's plays so the next tick's decisions see the new ownership.
            _possessionTracker.Update(_players, Ball, _executionSystem.ContactEvents, dt);

            _ballSolver.Step(Ball, dt);
        }
        else
        {
            // A restart/pause is not play: ownership cannot survive it.
            _possessionTracker.Reset();
            _statistics.ResetPassPhase();
        }

        // Audit F08: hand this tick's plays to the rules engine, then drain them so a restart
        // triggered by an offside offence cannot be re-evaluated twice.
        _rulesEngine.StepRules(
            _players, Ball, _config.HalfDurationSeconds, dt,
            livePlay ? _executionSystem.ContactEvents : null,
            livePlay ? _contactGeometry : null);

        if (livePlay)
            _executionSystem.ContactEvents.Clear();

        RecordTickAnalytics(homeDirX, livePlay);
    }

    /// <summary>
    /// Records what the installed controller chose. A utility AI exposes its scored candidates, so the
    /// trace can name the runner-up and the margin; any other controller (keeper, human, scripted) is
    /// recorded by its debug summary with no candidate set, which is the truth about it.
    /// </summary>
    private void RecordDecisionTrace(FootballerState player, in FootballerObservation observation)
    {
        if (_controllers.TryGetValue(player.Id, out IFootballerController? controller) &&
            controller is TacticalUtilityAiController ai)
        {
            string selected = "Idle";
            float selectedScore = float.NegativeInfinity;
            string? runnerUp = null;
            float runnerUpScore = float.NegativeInfinity;

            IReadOnlyList<UtilityCandidate> candidates = ai.LastCandidates;
            for (int i = 0; i < candidates.Count; i++)
            {
                UtilityCandidate candidate = candidates[i];
                if (candidate.UtilityScore > selectedScore)
                {
                    if (selectedScore > float.NegativeInfinity)
                    {
                        runnerUp = selected;
                        runnerUpScore = selectedScore;
                    }
                    selected = candidate.ActionName;
                    selectedScore = candidate.UtilityScore;
                }
                else if (candidate.UtilityScore > runnerUpScore)
                {
                    runnerUp = candidate.ActionName;
                    runnerUpScore = candidate.UtilityScore;
                }
            }

            if (candidates.Count == 0)
            {
                selected = controller.GetDebugSummary();
                selectedScore = 0f;
            }

            DecisionTrace.Record(
                CurrentTick, observation, selected, selectedScore,
                runnerUp, runnerUpScore, candidates.Count,
                targetPlayerId: player.LastCommand.IntendedReceiverId);
            return;
        }

        DecisionTrace.Record(
            CurrentTick, observation, controller?.GetDebugSummary() ?? "Uncontrolled", 0f,
            null, 0f, 0, targetPlayerId: player.LastCommand.IntendedReceiverId);
    }

    private void AppendContactLog(IReadOnlyList<BallContactEvent> events)
    {
        if (events.Count == 0) return;

        int overflow = _contactLog.Count + events.Count - MaxContactLogEntries;
        if (overflow > 0)
        {
            if (overflow >= _contactLog.Count) _contactLog.Clear();
            else _contactLog.RemoveRange(0, overflow);
        }

        for (int i = 0; i < events.Count; i++)
            _contactLog.Add(events[i]);
    }

    private void RecordTickAnalytics(float homeDirX, bool livePlay)
    {
        Analytics.TotalTicks = CurrentTick;
        Analytics.HomeGoals = _rulesEngine.HomeScore;
        Analytics.AwayGoals = _rulesEngine.AwayScore;

        if (!livePlay) return;

        // Audit F17: possession must come from the ownership model (control or a deliberate play),
        // not from Ball.LastTouchedByTeam — a save/parry changes the last touch without handing the
        // ball to the keeper's team, and a stopped phase is not possession for anybody.
        BallPossessionSnapshot possession = _possessionTracker.Current;

        if (possession.PossessionTeam == TeamId.Home)
        {
            Analytics.HomePossessionTicks++;
            _homeTerritoryAccum += Ball.Position.X * homeDirX;
        }
        else if (possession.PossessionTeam == TeamId.Away)
        {
            Analytics.AwayPossessionTicks++;
            _awayTerritoryAccum += Ball.Position.X * (-homeDirX);
        }

        if (possession.IsControlledBy(TeamId.Home)) Analytics.HomeControlledPossessionTicks++;
        else if (possession.IsControlledBy(TeamId.Away)) Analytics.AwayControlledPossessionTicks++;

        if (CurrentTick % 30 == 0)
        {
            _sampleCount++;
            _homeWidthAccum += ComputeTeamSpanZ(TeamId.Home);
            _awayWidthAccum += ComputeTeamSpanZ(TeamId.Away);

            Analytics.HomeAverageBallTerritoryX = Analytics.HomePossessionTicks > 0
                ? (float)(_homeTerritoryAccum / Analytics.HomePossessionTicks)
                : 0f;
            Analytics.AwayAverageBallTerritoryX = Analytics.AwayPossessionTicks > 0
                ? (float)(_awayTerritoryAccum / Analytics.AwayPossessionTicks)
                : 0f;
            Analytics.HomeAverageTeamWidthMeters = (float)(_homeWidthAccum / _sampleCount);
            Analytics.AwayAverageTeamWidthMeters = (float)(_awayWidthAccum / _sampleCount);
        }
    }

    private float ComputeTeamSpanZ(TeamId team)
    {
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;
        foreach (var p in _players)
        {
            if (!p.IsActiveOnPitch || p.Role == TacticalRole.Goalkeeper || p.Team != team) continue;
            if (p.Position.Z < minZ) minZ = p.Position.Z;
            if (p.Position.Z > maxZ) maxZ = p.Position.Z;
        }
        return maxZ > minZ ? maxZ - minZ : 0f;
    }
}
