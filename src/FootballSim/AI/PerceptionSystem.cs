using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// Builds the controller-facing <see cref="FootballerObservation"/> from the authoritative world:
/// per-player position and velocity estimates with confidence-based noise, a reaction-delayed view of
/// the ball, and the ownership/election facts a controller is allowed to know.
///
/// This is the seam between truth and knowledge (plan §3). The engine hands it the real state; every
/// derived value it publishes is what a controller may act on. Extracted verbatim from
/// <see cref="TacticalUtilityAiController"/> in A1.1d so perception and decision can evolve separately.
///
/// A2.2 added the two halves of an actual knowledge boundary:
/// <list type="bullet">
/// <item><b>sight filtering</b> — an opponent outside the facing cone (and not right next to the
/// player) is not measured this tick;</item>
/// <item><b>persistent memory</b> — such an opponent is published from <see cref="PlayerMemory"/> as a
/// <i>prediction</i> from the last sighting, with age, decaying confidence and growing uncertainty.
/// The earlier version included every active opponent on every tick, so an unseen opponent's live
/// motion reached every scorer and the "confidence" number described nothing.</item>
/// </list>
///
/// Teammates are deliberately still observed every tick: a team's shape is coordinated by the
/// match-level job system, and modelling teammate belief properly is A7.2's delayed, expiring team
/// calls. The hard invariant the plan states — an unseen <i>opponent</i>'s motion must not reach an
/// estimate, score or assignment — is about the side a player can only see.
///
/// A7.1 adds the third piece: <b>the sight cone follows the head, not the body</b>. A
/// <see cref="ScanPlanner"/> owns each player's head yaw and turns it at a bounded rate toward an attention
/// target (a stale believed opponent, or the blind space behind him before a reception), so the cone that
/// decides what is seen this tick points where the head looks. A body standing on the sight line blocks it
/// (simple occlusion). Sightings still go into the same <see cref="PlayerMemory"/>: this is a new way to
/// <i>see</i>, not a second memory.
///
/// The scan model is opt-in for callers that supply an <see cref="AiRuntimeConfig"/>: a standalone
/// <c>new PerceptionSystem()</c> (fixtures, probes) keeps the pre-A7.1 model exactly — gaze is the body
/// facing, every profile-length check is unchanged, and no scan state exists.
/// </summary>
public sealed class PerceptionSystem
{
    /// <summary>Longest reaction delay the perception history can represent (audit F18).</summary>
    private const int MaxReactionDelayTicks = 20;

    /// <summary>A ball teleport (restart, kickoff) is not a reaction-worthy movement.</summary>
    private const float TeleportResetDistance = 15f;

    /// <summary>
    /// Widest angle off the gaze direction at which an opponent is still seen. Human peripheral vision is
    /// roughly ±100°, and players are not expected to lose a man standing beside them. A7.1 points the cone
    /// at the head's gaze instead of the body facing, so turning the head moves the whole cone.
    /// </summary>
    private const float SightConeMinFacingDot = -0.17f;

    /// <summary>
    /// Inside this radius a player knows who is there regardless of facing — body contact, voice and
    /// the ball itself. Without it, players would lose track of a defender standing on their toes.
    /// </summary>
    private const float NearFieldMeters = 2.5f;

    /// <summary>
    /// A body can only occlude a sight line it stands close to. A7.1's simple occlusion considers the few
    /// bodies nearest the observer: a body 16 m away subtends about two degrees, so it can only hide a
    /// target that is almost exactly behind it, and the observer's own close surroundings are what matter.
    /// </summary>
    private const float OcclusionRangeMeters = 16f;

    /// <summary>How far off the sight line a body still blocks it, in metres (a torso is ~0.5 m wide).</summary>
    private const float OcclusionRadiusMeters = 0.55f;

    /// <summary>Bound on occluders tested per sight line (A9.3: perception work stays linear and small).</summary>
    private const int MaxOccluders = 4;

    /// <summary>A loose ball within this distance makes the team's elected responder scan before committing.</summary>
    private const float LooseBallContestMeters = 12f;

    private readonly Dictionary<int, List<(Vector3 Position, Vector3 Velocity)>> _ballHistory = new();
    private readonly Dictionary<int, float> _historyTimestamp = new();
    private readonly Dictionary<int, PlayerMemory> _memories = new();
    private readonly ScoutingReport? _scouting;
    private readonly ScanPlanner _scanPlanner;

    /// <summary>
    /// True when the match supplied an AI configuration and scanning is on. A standalone
    /// <see cref="PerceptionSystem"/> is off by construction, so fixtures that predate A7.1 are unchanged.
    /// </summary>
    private readonly bool _scanningEnabled;

    private long _scanTicks;
    private long _observerTicks;
    private long _revealedByGaze;
    private long _occludedByBody;

    /// <param name="scouting">
    /// Pre-match scouting knowledge of the squads (A2.3). Null (the default) publishes the generic
    /// capability assumption for every other player, which is what standalone controllers and tests get:
    /// a conservative band, never the authoritative attributes.
    /// </param>
    /// <param name="ai">
    /// The match's validated AI configuration (A7.1). Null — the default for standalone controllers,
    /// fixtures and probes — keeps the pre-A7.1 sight model: the gaze is the body facing and nothing is
    /// occluded. The match always passes its validated configuration, so <see cref="AiRuntimeConfig.EnableScanning"/>
    /// decides whether its players turn their heads.
    /// </param>
    public PerceptionSystem(ScoutingReport? scouting = null, AiRuntimeConfig? ai = null)
    {
        _scouting = scouting;
        _scanningEnabled = ai?.EnableScanning == true;
        _scanPlanner = new ScanPlanner(ai ?? AiRuntimeConfig.Default);
    }

    /// <summary>
    /// A7.1 match-level scan telemetry: how often heads turned, how much of the picture only the gaze
    /// revealed, and how often a body blocked a sight line. Diagnostics only — no decision reads it.
    /// </summary>
    public ScanTelemetry ScanStats => new(
        _scanPlanner.ScanStarts,
        _scanPlanner.ThreatScanStarts,
        _scanPlanner.DangerScanStarts,
        _scanTicks,
        _observerTicks,
        _revealedByGaze,
        _occludedByBody);

    public static int ReactionDelayTicks(float reactionDelaySeconds) =>
        Math.Clamp((int)MathF.Round(reactionDelaySeconds / SimConstants.FixedDeltaTime), 0, MaxReactionDelayTicks);

    /// <summary>What one player currently remembers. Created on demand; never null for a live player.</summary>
    public PlayerMemory GetMemory(int observerId)
    {
        if (!_memories.TryGetValue(observerId, out PlayerMemory? memory))
        {
            memory = new PlayerMemory();
            _memories[observerId] = memory;
        }
        return memory;
    }

    /// <summary>One player's belief about another, or null when they have never seen them.</summary>
    public PlayerBelief? GetBelief(int observerId, int playerId) =>
        _memories.TryGetValue(observerId, out PlayerMemory? memory) ? memory.Find(playerId) : null;

    /// <summary>
    /// Forgets one player (A1.5): their own reactions and memory, and what everybody remembers about
    /// them. A removed player must not linger in anybody's picture of the pitch.
    /// </summary>
    public void Forget(int playerId)
    {
        _ballHistory.Remove(playerId);
        _historyTimestamp.Remove(playerId);
        _memories.Remove(playerId);
        _scanPlanner.Forget(playerId);
        foreach (PlayerMemory memory in _memories.Values)
            memory.Forget(playerId);
    }

    /// <summary>Forgets every player's ball-reaction history, memory and head state.</summary>
    public void Clear()
    {
        _ballHistory.Clear();
        _historyTimestamp.Clear();
        _scanPlanner.Clear();
        foreach (PlayerMemory memory in _memories.Values)
            memory.Clear();
    }

    public FootballerObservation BuildObservation(
        FootballerState self,
        IReadOnlyList<FootballerState> allPlayers,
        BallState ball,
        TeamId? confirmedPossessingTeam,
        MatchPhase matchPhase,
        float matchTimeSeconds,
        float attackingDirX,
        bool isSetPieceTaker,
        TeamTacticalProfile teamProfile,
        SeededRandom rng,
        BallPossessionSnapshot? possession = null,
        bool? isDesignatedBallResponder = null,
        TeamJobAssignment? job = null,
        int tick = 0,
        TeamContext teamContext = default,
        RestDefence teamSecurity = default,
        int? ballResponderPlayerId = null,
        DefensiveLineBand lineBand = default,
        int? supportPresserPlayerId = null,
        TurnoverSituation teamTurnover = default,
        TeamCallContext teamCalls = default)
    {
        // Standalone controllers (probes, unit tests) have no match-level ownership model; derive a
        // conservative snapshot from the ball so behaviour stays sensible without inventing control.
        BallPossessionSnapshot ballPossession = possession ?? InferPossession(ball);
        PlayerMemory memory = GetMemory(self.Id);
        bool firstLook = !memory.IsInitialised;
        Vector2 facing = SimMath.SafeNormalize(self.FacingXZ, Vector2.UnitX);

        // A7.1: the head decides where the eyes point before the sight filter runs, so this tick's sight is
        // filtered by this tick's gaze. With scanning off (a standalone PerceptionSystem) the gaze is the
        // body facing and none of this is evaluated — the pre-A7.1 model, exactly.
        ScanStatus scan = ScanStatus.None;
        Vector2 gaze = facing;
        Span<int> occluderIds = stackalloc int[MaxOccluders];
        Span<float> occluderDistances = stackalloc float[MaxOccluders];
        Span<Vector2> occluderOffsets = stackalloc Vector2[MaxOccluders];
        int occluderCount = 0;
        _observerTicks++;
        if (_scanningEnabled)
        {
            scan = ChooseScan(self, ball, ballPossession, isDesignatedBallResponder, memory, tick);
            gaze = scan.GazeFrom(self.FacingXZ);
            if (scan.IsScanning) _scanTicks++;
            occluderCount = CollectOccluders(self, allPlayers, occluderIds, occluderDistances, occluderOffsets);
        }

        var visible = new List<PerceivedEntity>(allPlayers.Count - 1);
        int knowledgeAgeTicks = 0;
        foreach (var other in allPlayers)
        {
            if (other.Id == self.Id || !other.IsActiveOnPitch) continue;

            Vector2 toOther = new(other.Position.X - self.Position.X, other.Position.Z - self.Position.Z);
            float dist = toOther.Length();
            float gazeDot = dist > 1e-3f ? Vector2.Dot(toOther / dist, gaze) : 1f;
            float facingDot = gazeDot;
            if (_scanningEnabled && dist > 1e-3f)
                facingDot = Vector2.Dot(toOther / dist, facing);

            // Teammates are coordinated, not scouted (A7.2 owns teammate belief); an opponent has to be
            // in front of the player, close enough to feel, or seen on the first look at the pitch.
            bool inSight = firstLook ||
                           other.Team == self.Team ||
                           dist <= NearFieldMeters ||
                           gazeDot >= SightConeMinFacingDot;

            // A7.1 simple occlusion: a body standing on the sight line hides a distant opponent. The near
            // field is exempt (contact and voice) and a teammate is never hidden.
            if (inSight && !firstLook && dist > NearFieldMeters && other.Team != self.Team &&
                IsOccluded(toOther * (1f / dist), dist, other.Id,
                    occluderCount, occluderIds, occluderDistances, occluderOffsets))
            {
                inSight = false;
                _occludedByBody++;
            }

            if (inSight)
            {
                float distanceFactor = Math.Clamp(1.0f - (dist / 65.0f), 0.20f, 1.0f);
                float confidence = distanceFactor;

                float posNoise = (1.0f - confidence) * 0.30f;
                Vector3 estPos = other.Position + new Vector3(
                    rng.NextGaussian(0f, posNoise),
                    0f,
                    rng.NextGaussian(0f, posNoise));

                Vector3 estVel = other.Velocity + new Vector3(
                    rng.NextGaussian(0f, posNoise * 0.45f),
                    0f,
                    rng.NextGaussian(0f, posNoise * 0.45f));

                memory.Observe(other.Id, other.Team, other.Role, estPos, estVel, confidence, tick);
                visible.Add(new PerceivedEntity(other.Id, other.Team, other.Role, estPos, estVel, confidence)
                {
                    Capability = CapabilityOf(other.Id)
                });

                // Evidence for A7.1: this opponent is in the picture only because the head turned.
                if (_scanningEnabled && !firstLook && other.Team != self.Team &&
                    gazeDot >= SightConeMinFacingDot && facingDot < SightConeMinFacingDot)
                {
                    _revealedByGaze++;
                }
                continue;
            }

            // Out of sight: publish the prediction from memory. No RNG is drawn and no live state is
            // read, so an unseen opponent's movement cannot reach this player's picture of the pitch.
            if (!memory.TryPredict(other.Id, tick, out PerceivedEntity remembered)) continue;
            if (other.Team != self.Team && remembered.ObservedAgeTicks > knowledgeAgeTicks)
                knowledgeAgeTicks = remembered.ObservedAgeTicks;

            // Capability is scouting knowledge, not a position, so it is not aged with the sighting.
            visible.Add(remembered with { Capability = CapabilityOf(other.Id) });
        }

        memory.MarkInitialised();

        // Audit F18: reaction delay is a delayed *view* of the ball, not a smaller velocity-noise
        // scale. The player decides from where the ball was ReactionDelaySeconds ago (the execution
        // layer still applies live positions when the ball is actually played). A small velocity
        // noise scaled by reaction quality is kept on top, as before.
        (Vector3 observedBallPos, Vector3 observedBallVel) =
            ObservedBallState(self, ball, matchTimeSeconds);

        float ballDist = Vector3.Distance(self.Position, observedBallPos);
        float ballNoise = Math.Clamp(ballDist / 90.0f, 0.01f, 0.18f) * self.Attributes.ReactionDelaySeconds;
        Vector3 perceivedBallPos = observedBallPos;
        Vector3 perceivedBallVel = observedBallVel + new Vector3(
            rng.NextGaussian(0f, ballNoise),
            rng.NextGaussian(0f, ballNoise),
            rng.NextGaussian(0f, ballNoise));

        return new FootballerObservation
        {
            SelfId = self.Id,
            SelfTeam = self.Team,
            SelfRole = self.Role,
            SelfPosition = self.Position,
            SelfVelocity = self.Velocity,
            SelfFacingXZ = self.FacingXZ,
            Scan = scan,
            Calls = teamCalls,
            Stamina01 = self.Stamina01,
            AvailableToKick = self.AvailableToKick,
            ControlQuality01 = self.ControlQuality01,
            SelfBalance01 = self.Balance01,
            PerceivedBallPosition = perceivedBallPos,
            PerceivedBallVelocity = perceivedBallVel,
            PossessingTeam = confirmedPossessingTeam ?? ballPossession.PossessionTeam ?? ball.LastTouchedByTeam,
            BallLastTouchedByPlayerId = ball.LastTouchedByPlayerId,
            BallLastTouchedByTeam = ball.LastTouchedByTeam,
            BallLastPlay = ball.LastPlay,
            BallControlState = ballPossession.State,
            BallControllingPlayerId = ballPossession.ControllingPlayerId,
            IntendedReceiverId = ballPossession.IntendedReceiverId,
            BallHoldRemainingSeconds = ball.HeldByPlayerId == self.Id ? ball.HeldReleaseDelaySeconds : 0f,
            IsDesignatedBallResponder = isDesignatedBallResponder,
            BallResponderPlayerId = ballResponderPlayerId,
            SupportPresserPlayerId = supportPresserPlayerId,
            TacticalJob = job?.Job ?? TacticalJob.Shape,
            TacticalJobAnchor = job?.Anchor,
            JobTargetPlayerId = job?.TargetPlayerId,
            JobReason = job?.Reason ?? string.Empty,
            IsTemporaryResponsibility = job?.IsTemporary ?? false,
            ResponsibilitySourcePlayerId = job?.SourcePlayerId,
            TacticalAnchorPosition = self.TacticalAnchor,
            AttackingDirectionX = attackingDirX,
            MatchTimeSeconds = matchTimeSeconds,
            CurrentMatchPhase = matchPhase,
            IsSetPieceTaker = isSetPieceTaker,
            Tick = tick,
            KnowledgeAgeTicks = knowledgeAgeTicks,
            TacticalContext = teamContext,
            TeamSecurity = teamSecurity,
            TeamLineBand = lineBand,
            TeamTurnover = teamTurnover,
            VisiblePlayers = visible,
            TeamProfile = teamProfile,
            SelfTendencies = self.Tendencies,
            SelfAttributes = self.Attributes
        };
    }

    /// <summary>
    /// A7.1: one player's scan decision for the tick. The inputs are the ball's state (a planned pass to
    /// this player, or a loose ball this team's elected responder is contesting) and the nearest believed
    /// opponent whose sighting has gone stale. No authoritative player position is read here: the threat
    /// comes from <see cref="PlayerMemory"/>, so a player turns to look where he remembers someone.
    /// </summary>
    private ScanStatus ChooseScan(
        FootballerState self,
        BallState ball,
        BallPossessionSnapshot possession,
        bool? isDesignatedBallResponder,
        PlayerMemory memory,
        int tick)
    {
        bool receiving = possession.State == BallControlState.InFlightPass &&
                         possession.IntendedReceiverId == self.Id;

        if (!receiving && possession.State == BallControlState.Loose && isDesignatedBallResponder == true)
        {
            Vector2 toBall = new(ball.Position.X - self.Position.X, ball.Position.Z - self.Position.Z);
            receiving = toBall.Length() <= LooseBallContestMeters;
        }

        bool isCarrier = possession.ControllingPlayerId == self.Id &&
                         possession.State is BallControlState.Controlled or BallControlState.KeeperHeld;

        PerceivedEntity threat = default;
        bool staleThreat = false;
        if (!isCarrier)
        {
            staleThreat = memory.TryFindStaleOpponent(
                self.Team, tick, self.Position, _scanPlanner.RefreshAgeTicks,
                _scanPlanner.AttentionRangeMeters, out threat);
        }

        Vector2 threatDirection = default;
        float threatDistance = float.MaxValue;
        if (staleThreat)
        {
            threatDirection = new Vector2(
                threat.EstimatedPosition.X - self.Position.X,
                threat.EstimatedPosition.Z - self.Position.Z);
            threatDistance = threatDirection.Length();
        }

        return _scanPlanner.Think(new ScanSituation(
            self.Id, tick, self.FacingXZ, receiving, isCarrier,
            staleThreat, threatDirection, threatDistance));
    }

    /// <summary>
    /// A7.1: the bodies near the observer that could stand on a sight line, nearest first, bounded to
    /// <see cref="MaxOccluders"/>. Choosing them once per observer keeps occlusion linear — a sight line is
    /// tested against at most four bodies rather than against the whole squad.
    /// </summary>
    private static int CollectOccluders(
        FootballerState self,
        IReadOnlyList<FootballerState> allPlayers,
        Span<int> ids,
        Span<float> distances,
        Span<Vector2> offsets)
    {
        int count = 0;
        foreach (FootballerState other in allPlayers)
        {
            if (other.Id == self.Id || !other.IsActiveOnPitch) continue;

            Vector2 offset = new(other.Position.X - self.Position.X, other.Position.Z - self.Position.Z);
            float distance = offset.Length();
            if (distance > OcclusionRangeMeters || distance <= 1e-3f) continue;

            int slot;
            if (count < MaxOccluders)
            {
                slot = count;
                count++;
            }
            else
            {
                if (distance >= distances[MaxOccluders - 1]) continue;
                slot = MaxOccluders - 1;
            }

            while (slot > 0 && distances[slot - 1] > distance)
            {
                distances[slot] = distances[slot - 1];
                ids[slot] = ids[slot - 1];
                offsets[slot] = offsets[slot - 1];
                slot--;
            }

            distances[slot] = distance;
            ids[slot] = other.Id;
            offsets[slot] = offset;
        }

        return count;
    }

    /// <summary>
    /// A7.1 simple occlusion: a body blocks the sight line when it is between the observer and the target
    /// and within <see cref="OcclusionRadiusMeters"/> of that line. The caller exempts the observer's near
    /// field (contact and voice) and teammates, so this is only asked about distant opponents.
    /// </summary>
    private static bool IsOccluded(
        in Vector2 direction,
        float targetDistance,
        int targetId,
        int occluderCount,
        ReadOnlySpan<int> ids,
        ReadOnlySpan<float> distances,
        ReadOnlySpan<Vector2> offsets)
    {
        for (int i = 0; i < occluderCount; i++)
        {
            if (ids[i] == targetId) continue;
            if (distances[i] >= targetDistance) continue; // the body is past the target from here

            Vector2 offset = offsets[i];
            float along = Vector2.Dot(offset, direction);
            if (along <= 0f || along >= targetDistance) continue;

            float lateral = MathF.Abs(offset.X * direction.Y - offset.Y * direction.X);
            if (lateral < OcclusionRadiusMeters) return true;
        }

        return false;
    }

    /// <summary>
    /// Audit F18: the ball state this player can perceive right now. The perception system keeps a
    /// short per-player history keyed by match time, returns the sample from
    /// <see cref="PlayerAttributes.ReactionDelaySeconds"/> ago and tracks it forward with the
    /// perceived velocity, so the delay shows up as a late reaction to a change (a kick, a bounce, a
    /// reversal) rather than as a permanent lag. Until enough history exists (match start) the live
    /// state is used, and a discontinuous ball jump (restart teleport) clears the history.
    /// </summary>
    private (Vector3 Position, Vector3 Velocity) ObservedBallState(
        FootballerState self,
        BallState ball,
        float matchTimeSeconds)
    {
        if (!_ballHistory.TryGetValue(self.Id, out List<(Vector3 Position, Vector3 Velocity)>? history))
        {
            history = new List<(Vector3, Vector3)>(MaxReactionDelayTicks + 1);
            _ballHistory[self.Id] = history;
        }

        bool newTick = !_historyTimestamp.TryGetValue(self.Id, out float lastTime) ||
                       MathF.Abs(lastTime - matchTimeSeconds) > 1e-6f;

        if (newTick)
        {
            if (history.Count > 0)
            {
                Vector3 lastSeen = history[^1].Position;
                if (Vector3.Distance(lastSeen, ball.Position) > TeleportResetDistance)
                    history.Clear();
            }

            history.Add((ball.Position, ball.Velocity));
            if (history.Count > MaxReactionDelayTicks + 1)
                history.RemoveAt(0);
            _historyTimestamp[self.Id] = matchTimeSeconds;
        }

        int delayTicks = ReactionDelayTicks(self.Attributes.ReactionDelaySeconds);
        int index = history.Count - 1 - delayTicks;
        if (index < 0) return (ball.Position, ball.Velocity);

        (Vector3 samplePosition, Vector3 sampleVelocity) = history[index];

        // Track through the delay with the perceived velocity: a player follows steady motion
        // accurately and only reacts late to a *change* (a kick, a bounce, a sudden reversal). Without
        // this the AI would permanently chase a ball that is one reaction-time behind reality —
        // measured as hundreds of out-of-reach kicks per match.
        float delaySeconds = delayTicks * SimConstants.FixedDeltaTime;
        Vector3 predictedPosition = samplePosition + sampleVelocity * delaySeconds;
        return (predictedPosition, sampleVelocity);
    }

    /// <summary>Scouted capability band for a player, or the conservative default when unscouted.</summary>
    private CapabilityEstimate CapabilityOf(int playerId) =>
        _scouting?.Get(playerId) ?? CapabilityEstimate.Generic;

    private static BallPossessionSnapshot InferPossession(BallState ball)
    {
        if (ball.HeldByPlayerId is int heldById)
        {
            // Standalone controllers have no ownership tracker; a held ball is authoritative on
            // its own (audit F03).
            return new BallPossessionSnapshot(BallControlState.KeeperHeld, ball.LastTouchedByTeam, heldById, null);
        }

        if (ball.LastTouchedByPlayerId is int playerId &&
            ball.LastTouchedByTeam is TeamId team &&
            new Vector2(ball.Velocity.X, ball.Velocity.Z).Length() <= 2.5f)
        {
            return new BallPossessionSnapshot(BallControlState.Controlled, team, playerId, null);
        }

        return BallPossessionSnapshot.Loose;
    }
}