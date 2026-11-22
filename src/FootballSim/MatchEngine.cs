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

    /// <summary>
    /// Mean lateral span, in metres, of the team's outfield players while that team actually has the ball
    /// (ownership model). Numerator: sum of the z-span of every active outfield player at each 30-tick
    /// sample taken in that team's possession. Denominator: those samples only. Source: authoritative
    /// positions in <see cref="MatchSimulationEngine"/>, live play only.
    ///
    /// This is separate from <see cref="HomeAverageTeamWidthMeters"/>, which samples regardless of who has
    /// the ball and therefore mostly reports the run of play: on the tactical gate it correlated with
    /// possession share rather than with the width profile. The profile's width is a statement about the
    /// *attacking* shape, so the comparison between profiles is made on this measure (A4 evidence).
    /// </summary>
    public float HomeAttackingWidthMeters { get; set; }

    /// <summary>Possession-scoped lateral span for the away team; see <see cref="HomeAttackingWidthMeters"/>.</summary>
    public float AwayAttackingWidthMeters { get; set; }

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
    // A9.2: the communication context owns the run-advertisement board, whose channel is one of the
    // ablation switches, so it is built from the validated configuration rather than field-initialised.
    private readonly MatchCommunicationContext _communication;
    private readonly List<BallContactEvent> _contactLog = new();
    private readonly ContactGeometrySnapshot _contactGeometry = new();
    private readonly HashSet<int> _inactivePlayerIds = new();
    private TeamBelief _homeBelief = new(TeamId.Home);
    private TeamBelief _awayBelief = new(TeamId.Away);
    private MatchPhase _lastPhase = MatchPhase.PreKickoff;

    private double _homeTerritoryAccum;
    private double _awayTerritoryAccum;
    private double _homeWidthAccum;
    private double _awayWidthAccum;
    private int _sampleCount;

    // A4 evidence: the same span, but only sampled while that team has the ball (see
    // MatchAnalyticsSummary.HomeAttackingWidthMeters).
    private double _homeAttackingWidthAccum;
    private double _awayAttackingWidthAccum;
    private int _homeAttackingWidthSamples;
    private int _awayAttackingWidthSamples;

    /// <summary>A9.3: team-belief map rebuilds, for the perf probe. Diagnostics only.</summary>
    private int _beliefRebuildCount;

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
            AiRuntimeConfigValidation.Validate(_config.Ai, "AI runtime configuration");
        }

        // A1.3: one stream per consumer. Perception no longer advances the stream that decides whether
        // a pass is controlled, so a perception change cannot silently rewrite every later physics
        // exchange — and a command-only replay sees the same execution stream whether or not the AI ran.
        _executionRng = RandomStreams.Create(_config.Seed, RandomStream.Execution);
        _perceptionRng = RandomStreams.Create(_config.Seed, RandomStream.Perception);
        _ballSolver = new BallPhysicsSolver(null, _pitch);
        _executionSystem = new FootballerExecutionSystem(_pitch, _config.Ai.EnableCognitiveError);
        // A3.1: the coordinator resolves each team's context from the profiles (the transition windows
        // depend on directness and pressing intensity), so it needs them from the start.
        _tacticalCoordinator = new TeamTacticalCoordinator(_pitch, _config.HomeProfile, _config.AwayProfile);
        _ballResponderElection = new BallResponderElection(_pitch);
        _communication = new MatchCommunicationContext(_config.Ai);
        _teamJobs = new TeamJobCoordinator(_pitch, _config.Ai);
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
            _tacticalCoordinator.AssignFormations(
                _players, _config.PlayersPerTeam, fromPositions: false,
                _rulesEngine.GetHomeAttackDirectionX());
        }
        else
        {
            Ball = scenario.CreateBallState(_ballSolver.Parameters.Radius);
            InitializeScenarioSquads(scenario);
            _rulesEngine.StartScenario(
                scenario.MatchClockSeconds, scenario.Period, scenario.HomeScore, scenario.AwayScore,
                $"Scenario '{scenario.Name}'");

            // A3.3: a fragment owns the formation it describes. Assigning it from the fixture's layout
            // is also what lets an unbalanced side (3v2, 4v3) hold a shape of its own size instead of
            // inheriting shirts 1..N of the eleven-a-side table.
            _tacticalCoordinator.AssignFormations(
                _players, _config.PlayersPerTeam, fromPositions: true,
                _rulesEngine.GetHomeAttackDirectionX());

            BallPossessionSnapshot seeded = scenario.CreatePossessionSnapshot();
            _possessionTracker.Seed(seeded);
            _tacticalCoordinator.SeedPossession(seeded.PossessionTeam);

            if (scenario.BallSetup == ScenarioBallSetup.KeeperHeld)
            {
                // A keeper holding the ball is the same state a catch produces, so the physics step is
                // skipped and the release delay applies exactly as it would after one. The delay is the
                // catch's own constant (L2): seeding zero let a fixture's keeper distribute on tick 1.
                Ball.HeldByPlayerId = seeded.ControllingPlayerId;
                Ball.HeldReleaseDelaySeconds = BallContactRules.KeeperHoldSeconds;
            }
        }

        // A2.3: scouting knowledge is built once, from the squad as it is at kickoff, and never
        // refreshed. A scorer therefore receives an estimate with a confidence band instead of a live
        // read of an opponent's attributes. A7.1: the same validated configuration owns the scan model,
        // so a fixture's `Ai` block decides whether its players turn their heads.
        _perceptionSystem = new PerceptionSystem(ScoutingReport.FromSquad(_players), _config.Ai);
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

    /// <summary>
    /// The match's shared communication state: the human's call for the pass and the team run advertisement
    /// board (A5.3). Exposed read-only for diagnostics, the inspector and tests; decisions reach it through
    /// the controllers, so nothing can write it from outside.
    /// </summary>
    public MatchCommunicationContext Communication => _communication;

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

    /// <summary>Most recent discontinuity the AI was told about (A1.5), or null before the first one.</summary>
    public AiResetReason? LastAiResetReason { get; private set; }

    /// <summary>Tick at which <see cref="LastAiResetReason"/> was raised.</summary>
    public int LastAiResetTick { get; private set; }

    /// <summary>Statistics tracker fed by the execution contacts (audit F17).</summary>
    public MatchStatisticsTracker Statistics => _statistics;

    /// <summary>Player currently elected as the ball responder for a team, if any.</summary>
    public int? GetBallResponder(TeamId team) => _ballResponderElection.GetResponder(team);

    /// <summary>
    /// A7.1 scanning evidence: completed head turns, how much of the picture only the gaze revealed, and
    /// how often a body blocked a sight line. Diagnostics only — nothing in a decision path reads it.
    /// </summary>
    public ScanTelemetry ScanStats => _perceptionSystem.ScanStats;

    /// <summary>Team-level off-ball job assignments for the current tick (audit F04).</summary>
    public TeamJobCoordinator TeamJobs => _teamJobs;

    /// <summary>
    /// What <paramref name="team"/> currently believes about its opponents (A2.6), rebuilt each live tick
    /// from that team's own observations. Diagnostics and the inspector overlays read it; the tactical
    /// consumers receive it directly.
    /// </summary>
    public TeamBelief TeamBeliefOf(TeamId team) => team == TeamId.Home ? _homeBelief : _awayBelief;

    /// <summary>
    /// A truth view of the opponents for validation overlays (A2.6). Labelled by construction: nothing
    /// in a decision path may read this, and the type is distinct from what tactics receive.
    /// </summary>
    public TeamBelief TruthViewOf(TeamId team) => TeamBelief.FromTruth(team, _players);
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

                // A7.6: the default squad fields an archetype per shirt (all seven once in 11v11), which is
                // the behaviour change; with `EnableArchetypes` off every player is the pre-A7.6 player and
                // the squad is the uniform one the A/B compares against.
                PlayerArchetype archetype = _config.Ai.EnableArchetypes
                    ? PlayerArchetypeProfile.ForShirt(role, shirt, _config.PlayersPerTeam)
                    : PlayerArchetype.None;

                var player = new FootballerState
                {
                    Id = playerId,
                    Team = team,
                    ShirtNumber = shirt,
                    Role = role,
                    Archetype = archetype,
                    Attributes = new PlayerAttributes
                    {
                        // Deterministic footedness per shirt so the same seed/replay is identical.
                        PreferredFoot = shirt is 3 or 7 or 11 ? Footedness.Left : Footedness.Right
                    },
                    Tendencies = PlayerTendencyProfile.For(role, archetype)
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
                    _controllers[playerId] = new GoalkeeperController(_pitch, _config.Ai);
                }
                else
                {
                    _controllers[playerId] = new TacticalUtilityAiController(_pitch, _communication, _config.Ai);
                }
            }
        }
    }

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
            // A7.6: a fixture may state an archetype explicitly; otherwise the match fields the role's default
            // one when archetypes are enabled. The archetype's deltas are folded into the tendencies the same
            // way, whether the fixture stated them or not (explicit tendencies are the fixture's statement of
            // the player, and the archetype is a preference on top of it).
            PlayerArchetype archetype = spec.Archetype ??
                (_config.Ai.EnableArchetypes ? PlayerArchetypeProfile.DefaultFor(spec.Role) : PlayerArchetype.None);

            var player = new FootballerState
            {
                Id = spec.Id,
                Team = spec.Team,
                ShirtNumber = spec.ShirtNumber,
                Role = spec.Role,
                Archetype = archetype,
                Attributes = spec.Attributes,
                Tendencies = PlayerArchetypeProfile.Apply(
                    spec.Tendencies ?? PlayerTendencyProfile.ForRole(spec.Role), archetype),
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
                controller = new GoalkeeperController(_pitch, _config.Ai);
            }
            else
            {
                controller = new TacticalUtilityAiController(_pitch, _communication, _config.Ai);
            }

            _controllers[spec.Id] = controller;
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

        // A replacement is a discontinuity for the new controller: whatever it inherits from its own
        // construction must not be mixed with the previous controller's passage of play (A1.5).
        if (controller is IAiStateResettable resettable)
        {
            resettable.ResetForPhase(AiResetReason.ControllerReplaced);
            LastAiResetReason = AiResetReason.ControllerReplaced;
            LastAiResetTick = CurrentTick;
        }

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
    /// A9.3: cumulative AI work for the whole match, merged from the controllers that generated it plus
    /// the team-level refreshes this engine drives. A probe calls this once at the end: the per-tick
    /// decision path never aggregates anything, so the counters cannot become a hot path.
    /// </summary>
    public AiWorkStats GetAiWorkStats()
    {
        var total = new AiWorkStats();
        foreach (IFootballerController controller in _controllers.Values)
        {
            switch (controller)
            {
                case TacticalUtilityAiController utility:
                    total.Add(utility.CandidateWork);
                    total.Add(utility.ReceptionWork);
                    break;
                case GoalkeeperController keeper:
                    total.Add(keeper.Work);
                    break;
            }
        }

        total.RecordBeliefRebuilds(_beliefRebuildCount);
        return total;
    }

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
            // L4: no LINQ in the per-tick path; the id is known and the loop is bounded by the squad.
            FootballerState? humanPlayer = null;
            for (int index = 0; index < _players.Count; index++)
            {
                if (_players[index].Id != LockedHumanController.LockedPlayerId) continue;
                humanPlayer = _players[index];
                break;
            }
            if (humanPlayer is not null && !humanPlayer.IsActiveOnPitch)
                LockedHumanController.NotifyPlayerUnavailableForMatch();
        }

        float homeDirX = _rulesEngine.GetHomeAttackDirectionX();

        // A3.1: the rules engine owns the restart; the tactical context mirrors what it has armed. Read
        // before any decision so what a player sees this tick is the restart in force this tick — an
        // armed restart is an exclusive claim on the ball, even though ownership is reset by the stoppage.
        _tacticalCoordinator.ObserveRestart(
            _rulesEngine.ActiveRestart, _rulesEngine.RestartTeam, Ball, homeDirX);

        // Audit F06: a whistle/setup delay is not live play. Decisions, contacts and ball physics
        // are frozen; only the rules clock advances, so the restart can resume.
        bool livePlay = !_rulesEngine.IsPlaySuspended;

        if (livePlay)
        {
            // Audit F10: possession comes from the ownership model (control or a deliberate play),
            // not from a bare last touch, and is confirmed every tick so a change is immediate.
            // A3.1/A3.2: the same read splits the claim from the ball's stability, and a turnover
            // re-resolves the phase and both zones on this tick instead of waiting out a dwell.
            _tacticalCoordinator.ObservePossession(_possessionTracker.Current, Ball, homeDirX, dt);

            // One centrally elected ball responder per team, using real time-to-reach. The human is
            // never elected as an AI responder, so an idle human cannot switch the AI off the ball.
            int? humanPlayerId = LockedHumanController?.LockedPlayerId;
            _ballResponderElection.Update(_players, Ball, _possessionTracker.Current, humanPlayerId);
            BallPossessionSnapshot possession = _possessionTracker.Current;

            if (CurrentTick % 4 == 1)
            {
                _tacticalCoordinator.UpdateTeamAnchors(_players, Ball, homeDirX);
            }

            // Audit F04: persistent team-level jobs relative to the actual carrier/loose ball.
            // A2.6: the job coordinator reads the teams' *beliefs*, not the authoritative positions, so
            // an opponent nobody has seen cannot pull a defender out of shape. The belief views are
            // rebuilt on the cadence the jobs are actually recomputed, not every tick.
            TeamId? possessingTeam = _tacticalCoordinator.ConfirmedPossessingTeam;
            if (_teamJobs.WillRecompute(possessingTeam, CurrentTick))
                UpdateTeamBeliefs();

            // A7.2: the team-wide defensive instruction currently in force reaches the one allocator as a
            // bounded preference on the A6.3 line band — never as an immediate move of a player. A step/drop
            // call is read before the jobs update so this tick's band already includes it; the board's own
            // delay and expiry are what keep the instruction from being a per-tick reflex.
            float homeLineCallShift = LineCallShiftFor(TeamId.Home);
            float awayLineCallShift = LineCallShiftFor(TeamId.Away);

            _teamJobs.Update(
                _players, Ball, possession, possessingTeam,
                _ballResponderElection.GetResponder(TeamId.Home),
                _ballResponderElection.GetResponder(TeamId.Away),
                homeDirX, CurrentTick, _config.HomeProfile, _config.AwayProfile,
                _homeBelief, _awayBelief,
                _tacticalCoordinator.HomeFormation, _tacticalCoordinator.AwayFormation,
                _tacticalCoordinator.HomeContext, _tacticalCoordinator.AwayContext,
                homeLineCallShift, awayLineCallShift);

            // A6.1: one second man per team, elected once from the team's shared inputs. Resolving it
            // independently in each defender's noisy observation let two equidistant centre-backs both
            // decide they were the nearest eligible player and step in together.
            int? homeSupportPresser = ElectSupportPresser(TeamId.Home, possession, humanPlayerId);
            int? awaySupportPresser = ElectSupportPresser(TeamId.Away, possession, humanPlayerId);

            // A8.4: one short trap contract per team, elected once from the same shared inputs (own exact
            // positions, the team belief, the presser, the second man and the allocator's assignments). The trap
            // is kept or ended here, so every player's observation this tick sees the same contract.
            _communication.Traps.Update(
                TeamId.Home, _players, _homeBelief, possession,
                _ballResponderElection.GetResponder(TeamId.Home), homeSupportPresser, _teamJobs,
                humanPlayerId, homeDirX, _config.HomeProfile.Compactness01, _pitch, CurrentTick,
                _config.Ai.EnableTrapContracts);
            _communication.Traps.Update(
                TeamId.Away, _players, _awayBelief, possession,
                _ballResponderElection.GetResponder(TeamId.Away), awaySupportPresser, _teamJobs,
                humanPlayerId, -homeDirX, _config.AwayProfile.Compactness01, _pitch, CurrentTick,
                _config.Ai.EnableTrapContracts);

            // A8.5: one restart routine, elected once while the rules engine has a restart armed. Read after the
            // rules engine has armed (or consumed) this tick's restart and before any observation is built, so
            // every participant sees the same routine and the designated taker can play it on this tick. The
            // election uses the restarting team's own exact positions, its shared belief of the opponents and the
            // rules engine's own restart state — the rules engine keeps the placement, the taker and the phase.
            TeamId restartTeam = _rulesEngine.RestartTeam;
            _communication.Restarts.Update(
                restartTeam, _rulesEngine.ActiveRestart,
                new Vector2(Ball.Position.X, Ball.Position.Z),
                _rulesEngine.DesignatedSetPieceTakerId, _players,
                restartTeam == TeamId.Home ? _homeBelief : _awayBelief,
                restartTeam == TeamId.Home ? _config.HomeProfile : _config.AwayProfile,
                humanPlayerId, restartTeam == TeamId.Home ? homeDirX : -homeDirX, _pitch,
                Ball.LastTouchedByPlayerId, Ball.LastTouchedByTeam, CurrentTick,
                _config.Ai.EnableRestartRoutines);

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
                    possession, _ballResponderElection.IsResponder(p.Team, p.Id), _teamJobs.Get(p.Id), CurrentTick,
                    _tacticalCoordinator.ContextFor(p.Team), _teamJobs.SecurityOf(p.Team),
                    _ballResponderElection.GetResponder(p.Team), _teamJobs.LineBandOf(p.Team),
                    p.Team == TeamId.Home ? homeSupportPresser : awaySupportPresser,
                    _teamJobs.TurnoverOf(p.Team),
                    // A7.2: what this player's own team has already said, through the communication delay.
                    _communication.Calls.BuildContext(p.Team, p.Id, CurrentTick),
                    // A8.1: the bounded combination this player is part of, if any. Read here (once, from
                    // the match's own board) so every decision the player makes this tick sees the same
                    // contract and no scorer reads the board directly.
                    _communication.Combinations.TryGetContext(p.Team, p.Id, CurrentTick,
                        out CombinationContext combination)
                        ? combination
                        : CombinationContext.None,
                    // A8.4: the short trap this player is part of, if any. Read once here from the match's own
                    // board, like the combination above, so every decision sees the same contract and no scorer
                    // reads the board directly.
                    _communication.Traps.ContextFor(p.Team, p.Id, CurrentTick),
                    // A8.5: the restart routine this player is part of, if any. Read once here from the match's own
                    // board for the same reason: the taker's delivery and the keeper's goal-kick distribution both
                    // read the routine from the observation, never from the board.
                    _communication.Restarts.ContextFor(p.Team, p.Id, CurrentTick));
                _latestObservations[p.Id] = obs;

                // A7.2: the player's own shout, decided from the observation he just built. Only an AI
                // makes calls; the locked human's instruction is the HUD's call-for-pass and is unchanged.
                if (_controllers[p.Id].SourceType == ControllerSourceType.AI)
                    TeamCallPlanner.Publish(obs, _communication.Calls, _config.Ai);

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

            // A8.1: a contract's continuation is the kick it actually became. Applied from the same
            // contact events the rules engine and the statistics consume, so a combination is attributed
            // to a play — never to two players happening to stand near each other.
            _communication.Combinations.ApplyContacts(_executionSystem.ContactEvents, CurrentTick);
            AppendContactLog(_executionSystem.ContactEvents);
            ApplyContactFeedback();

            // Consume this tick's plays so the next tick's decisions see the new ownership.
            _possessionTracker.Update(_players, Ball, _executionSystem.ContactEvents, dt);

            // A8.1: a contract belongs to the possession it was made in (the exit gate's "no contract
            // outlives its possession"). Read after the ownership model has consumed this tick, so a team
            // that has just lost the ball ends its contracts before its next decisions.
            _communication.Combinations.ObservePossession(_possessionTracker.Current.PossessionTeam, CurrentTick);

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

        ApplyResetHooks();

        if (livePlay)
            _executionSystem.ContactEvents.Clear();

        RecordTickAnalytics(homeDirX, livePlay);
    }

    /// <summary>
    /// Rebuilds each team's picture of its opponents from the observations the players built last tick
    /// (A2.6).
    ///
    /// One tick of lag is deliberate: a team acts on what it saw, not on what is true now. The very
    /// first tick has no observations at all, so the views are seeded once from the world — the same
    /// "first look" rule perception uses, so a squad does not start blind at kickoff.
    /// </summary>
    private void UpdateTeamBeliefs()
    {
        _beliefRebuildCount++;

        if (!_homeBelief.HasBeenBuilt)
        {
            _homeBelief = TeamBelief.FromTruth(TeamId.Home, _players);
            _awayBelief = TeamBelief.FromTruth(TeamId.Away, _players);
            return;
        }

        _homeBelief.RebuildFromObservations(_players, _latestObservations);
        _awayBelief.RebuildFromObservations(_players, _latestObservations);
    }

    /// <summary>
    /// Elects the team's second presser from its shared belief and its own exact player positions, or
    /// returns null when the team is not defending against a controlled carrier. The value is published in
    /// every player's observation so all of them resolve the same man (A6.1: exactly one second man).
    /// </summary>
    private int? ElectSupportPresser(
        TeamId team,
        BallPossessionSnapshot possession,
        int? humanPlayerId)
    {
        if (possession.State != BallControlState.Controlled ||
            possession.PossessionTeam == team ||
            possession.ControllingPlayerId is not int carrierId)
        {
            return null;
        }

        TeamBelief belief = team == TeamId.Home ? _homeBelief : _awayBelief;
        int elected = PressPolicy.ElectSupportPresser(
            team, _players, belief,
            _ballResponderElection.GetResponder(team), carrierId, humanPlayerId);

        // Zero is a real answer ("nobody may step in"): publish it so the observation does not fall back
        // to each defender's noisy independent resolution.
        return elected;
    }

    /// <summary>
    /// A7.2: the line-band shift this team's live step/drop call asks for, or zero. Drop outranks step
    /// inside the board; the value is re-clamped by <see cref="DefensiveLineModel"/> so it can bias the
    /// band but never defeat the ball, run or keeper caps.
    /// </summary>
    private float LineCallShiftFor(TeamId team) =>
        _communication.Calls.TryGetLineDirective(team, CurrentTick, out TeamCall directive)
            ? TeamCallPlanner.LineShiftMeters(directive.Kind)
            : 0f;

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
            // The controller owns the selection (it is the only place that knows the commitment
            // bonus), so the trace reports its decision rather than re-deriving one from the raw table.
            UtilityCandidate selected = ai.LastSelected;
            UtilityCandidate? runnerUp = ai.LastRunnerUp;

            DecisionTrace.Record(
                CurrentTick, observation, selected.ActionName, selected.UtilityScore,
                runnerUp?.ActionName, runnerUp?.UtilityScore ?? 0f, ai.LastCandidateCount,
                target: selected.TargetXZ,
                targetPlayerId: player.LastCommand.IntendedReceiverId,
                candidateKey: selected.Key.IsNone ? null : selected.Key.Describe(),
                terms: selected.Terms,
                // A4.6: the refusal reasons the action set recorded. Copied only while tracing, so the
                // decision path itself never allocates for diagnostics.
                rejections: CopyRejections(ai));
            return;
        }

        DecisionTrace.Record(
            CurrentTick, observation, controller?.GetDebugSummary() ?? "Uncontrolled", 0f,
            null, 0f, 0, targetPlayerId: player.LastCommand.IntendedReceiverId);
    }

    /// <summary>
    /// Copies the action set's refusal reasons for one trace entry (A4.6), or null when there are none. Only
    /// called while tracing is on, so the allocation belongs to diagnostics rather than to a decision.
    /// </summary>
    private static IReadOnlyList<CandidateRejection>? CopyRejections(TacticalUtilityAiController ai)
    {
        int count = ai.RejectionCount;
        if (count == 0) return null;

        var copy = new CandidateRejection[count];
        for (int index = 0; index < count; index++) copy[index] = ai.GetRejection(index);
        return copy;
    }

    /// <summary>
    /// Drops AI state that belongs to a passage of play whenever the authoritative world says the passage
    /// ended (A1.5): a restart, the interval, full time, a player leaving the pitch.
    ///
    /// This is deliberately driven from the engine's phase and availability, not from the AI's own idea
    /// of what happened. A controller that decided for itself that a restart occurred would be another
    /// place for the two views to diverge.
    /// </summary>
    private void ApplyResetHooks()
    {
        MatchPhase phase = _rulesEngine.Phase;
        if (phase != _lastPhase)
        {
            AiResetReason? reason = phase switch
            {
                MatchPhase.HalfTime => AiResetReason.HalfTime,
                MatchPhase.FullTime => AiResetReason.FullTime,
                MatchPhase.ThrowIn or MatchPhase.GoalKick or MatchPhase.CornerKick or MatchPhase.FreeKick
                    or MatchPhase.PenaltyKick or MatchPhase.GoalScored => AiResetReason.MatchRestart,
                _ => null
            };

            _lastPhase = phase;
            if (reason is AiResetReason resetReason)
            {
                ResetAiState(resetReason);
                LastAiResetReason = resetReason;
                LastAiResetTick = CurrentTick;
            }
        }

        foreach (FootballerState player in _players)
        {
            bool active = player.IsActiveOnPitch;
            bool wasActive = !_inactivePlayerIds.Contains(player.Id);
            if (active || !wasActive) continue;

            _inactivePlayerIds.Add(player.Id);
            if (_controllers.TryGetValue(player.Id, out IFootballerController? controller) &&
                controller is IAiStateResettable resettable)
            {
                resettable.ResetForPhase(AiResetReason.PlayerRemoved);
            }
            // A3.3: a player who left the pitch cannot own a place in the formation. The players who
            // remain keep their own slots, which is what keeps the shape plausible with ten men.
            _tacticalCoordinator.FormationFor(player.Team).Remove(player.Id);
            _perceptionSystem.Forget(player.Id);
            // A8.1: a contract he was part of ends with him — an inactive player must not keep owning a
            // contract or a return region.
            _communication.Combinations.ReleasePlayer(player.Id, CurrentTick);
            // A8.4: the same rule for a trap role: an inactive player must not keep owning the press, the
            // screen or the cover inside a live contract.
            _communication.Traps.ReleasePlayer(player.Id, CurrentTick);
            // A8.5: and for a restart routine: an inactive player must not keep owning the taker, support or
            // target role inside a routine.
            _communication.Restarts.ReleasePlayer(player.Id, CurrentTick);
            LastAiResetReason = AiResetReason.PlayerRemoved;
            LastAiResetTick = CurrentTick;
        }
    }

    /// <summary>Notifies every controller and perception that a discontinuity occurred.</summary>
    private void ResetAiState(AiResetReason reason)
    {
        foreach (FootballerState player in _players)
        {
            if (!_controllers.TryGetValue(player.Id, out IFootballerController? controller)) continue;
            if (controller is IAiStateResettable resettable)
                resettable.ResetForPhase(reason);
        }

        // Ball-reaction history describes how the ball has been moving *for this player*. It is dropped
        // only when the world is genuinely reset — the interval and full time, where direction reverses and
        // every position is re-placed. A restart is ordinary play: the ball is placed within a few metres of
        // where it left, and `ObservedBallState` already ignores a teleport, so wiping the history for a
        // throw-in would make the first look after every stoppage blind for no reason.
        if (reason is AiResetReason.HalfTime or AiResetReason.FullTime)
            _perceptionSystem.Clear();

        // A7.2: a shout belongs to the passage it was made in, and every call lifetime is under two
        // seconds anyway — but a restart also moves the ball and re-places the teams, so a call about the
        // old picture must not survive it. The board is empty by construction at half time; clearing on
        // every reset is the honest statement rather than an assumption about timing.
        _communication.Calls.ClearAll();

        // A8.1: a contract is a promise about a passage of play; a restart or an interval ends it. Clearing
        // on every reset is the honest statement rather than an assumption about how long the promise would
        // have lasted — the same rule A7.2's calls follow (and for the same reason: the ball is re-placed).
        _communication.Combinations.ClearAll(CurrentTick);

        // A8.4: a trap is a commitment about one picture of one passage; the restart re-places the ball and the
        // teams, so no trap survives it. Clearing also clears the recovery windows: a new passage is a new
        // chance to trap.
        _communication.Traps.ClearAll(CurrentTick);

        // A8.5: a restart routine belongs to the restart it was elected for, so a reset ends it. When the reset is
        // a different restart already armed under it, the honest ending is "replaced"; otherwise the world was
        // simply reset.
        _communication.Restarts.ClearAll(_rulesEngine.ActiveRestart);
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
            float homeSpan = ComputeTeamSpanZ(TeamId.Home);
            float awaySpan = ComputeTeamSpanZ(TeamId.Away);
            _homeWidthAccum += homeSpan;
            _awayWidthAccum += awaySpan;

            // A4: the shape the *profile* is about is the one a team holds while it has the ball. The
            // possession-scoped sample is kept beside the unconditional one rather than replacing it, so
            // the older number stays comparable within this version.
            if (possession.PossessionTeam == TeamId.Home)
            {
                _homeAttackingWidthAccum += homeSpan;
                _homeAttackingWidthSamples++;
            }
            else if (possession.PossessionTeam == TeamId.Away)
            {
                _awayAttackingWidthAccum += awaySpan;
                _awayAttackingWidthSamples++;
            }

            Analytics.HomeAverageBallTerritoryX = Analytics.HomePossessionTicks > 0
                ? (float)(_homeTerritoryAccum / Analytics.HomePossessionTicks)
                : 0f;
            Analytics.AwayAverageBallTerritoryX = Analytics.AwayPossessionTicks > 0
                ? (float)(_awayTerritoryAccum / Analytics.AwayPossessionTicks)
                : 0f;
            Analytics.HomeAverageTeamWidthMeters = (float)(_homeWidthAccum / _sampleCount);
            Analytics.AwayAverageTeamWidthMeters = (float)(_awayWidthAccum / _sampleCount);
            Analytics.HomeAttackingWidthMeters = _homeAttackingWidthSamples > 0
                ? (float)(_homeAttackingWidthAccum / _homeAttackingWidthSamples)
                : 0f;
            Analytics.AwayAttackingWidthMeters = _awayAttackingWidthSamples > 0
                ? (float)(_awayAttackingWidthAccum / _awayAttackingWidthSamples)
                : 0f;
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
