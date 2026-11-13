using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// A5.1/A5.2: generates the bounded set of off-ball paths one player may run from his assigned duty, and
/// scores them.
///
/// <b>Why paths and not an anchor.</b> The team allocator (<see cref="TeamJobCoordinator"/>) decides
/// <i>who</i> does what — one player per duty, one allocator, unchanged. This object decides <i>where that
/// duty goes and when</i>: a depth runner against a high line has an in-behind path, a pin path and a box
/// arrival, and which one is worth running is a question about space, timing and the pass that could find
/// him. Before A5 the answer was always "walk to the anchor", so a run was a shape and a shape never
/// arrived anywhere.
///
/// <b>Bounded, twice.</b> The vocabulary per duty is a fixed short list (never more than four kinds), and
/// the set is truncated to <see cref="AiRuntimeConfig.MaxRunCandidates"/> in the order the kinds are
/// listed — support and depth before speculation. Decoys are declared but not generated; see
/// <see cref="RunKind.Decoy"/>.
///
/// <b>Adoption.</b> The path the player chooses is frozen for its window (<see cref="RunIntent"/>): the
/// meeting region stops moving, so a run is a run and not a target that chases the ball. The frozen path
/// is re-scored every tick against the live picture and competes with the fresh ones, which is what lets a
/// collapsing space end it early — but it does not re-time itself, so committing to a run cannot be
/// silently extended by a favourable tick.
///
/// <b>Announcement.</b> An adopted run is published to the team's <see cref="RunAdvertisementBoard"/>
/// (A5.3): where the runner means to meet the ball, when, and how sure he is, after the board's
/// communication delay. That is what lets the carrier play to the region instead of guessing a lead, and
/// what lets a claim extend this run's window to the ball's own arrival rather than expiring underneath
/// the pass.
///
/// This object owns per-player state (the adopted intent) and is reset with the controller (A1.5): a run
/// chosen before a restart is a run into a passage of play that ended.
/// </summary>
public sealed class RunCandidateGenerator
{
    /// <summary>
    /// Most paths one duty can offer before configuration truncation. Four kinds is the widest duty
    /// (a depth runner: in behind, pin, late box arrival, blind-side arrival).
    /// </summary>
    public const int MaxKindsPerDuty = 4;

    /// <summary>Fixed capacity of the emitted-path buffer; covers every kind plus the adopted one.</summary>
    public const int PathCapacity = 8;

    /// <summary>Gap kept between an attacking meeting point and the offside line, in metres (A5.4).</summary>
    public const float OnsideMarginMeters = 1.0f;

    /// <summary>
    /// Slack on the onside test for an <i>adopted</i> run, in metres. The defensive line moves by a few
    /// tenths most ticks; without this the meeting region would be re-clamped and the intent re-adopted
    /// every second, and a run that restarts is not a run. Standing marginally beyond the line is not an
    /// offence — the ball cannot be played to him there, which the pass model already refuses — so the
    /// intent survives until the line has moved a real distance.
    /// </summary>
    public const float OnsideToleranceMeters = 1.5f;

    /// <summary>A path must gain at least this much ground to be a run rather than a shuffle.</summary>
    public const float MinRunGainMeters = 2.5f;

    /// <summary>How far into the space behind the line an in-behind run reaches, in metres.</summary>
    public const float InBehindRunMeters = 12f;

    /// <summary>Radius of the region a depth run means to meet the ball in, in metres.</summary>
    public const float DepthMeetRadiusMeters = 5f;

    /// <summary>Radius of a box arrival's meeting region, in metres.</summary>
    public const float BoxMeetRadiusMeters = 4.5f;

    /// <summary>Distance from the carrier at which a checking run offers itself, in metres.</summary>
    public const float CheckShortDistanceMeters = 14f;

    /// <summary>
    /// Inside this distance the player is short of the ball already and there is nothing to check: a
    /// "checking run" from twelve metres out is a shuffle, not a run. The floor sits above the meeting
    /// distance so the path always means moving toward the ball.
    /// </summary>
    public const float CheckShortMinDistanceMeters = CheckShortDistanceMeters + 3f;

    /// <summary>How far beyond the carrier an overlap aims, in metres.</summary>
    public const float OverlapAheadMeters = 7f;

    /// <summary>How far inside the carrier an underlap aims, in metres.</summary>
    public const float UnderlapAheadMeters = 5f;

    /// <summary>How far inside the touchline an underlap arrives, in metres.</summary>
    public const float UnderlapInsideMeters = 12f;

    /// <summary>How far goal-side of the ball a recycle support falls back, in metres.</summary>
    public const float RecycleDropMeters = 8f;

    /// <summary>Nearest-defender radius inside which a pin path exists, in metres.</summary>
    public const float PinRadiusMeters = 18f;

    /// <summary>How far behind the line a pin holds its man, in metres.</summary>
    public const float PinBehindLineMeters = 0.4f;

    /// <summary>Attack-relative projection from which a box arrival is worth offering, in metres.</summary>
    public const float FinalThirdProjX = 20f;

    /// <summary>How far from the goal line a box arrival meets the ball, in metres.</summary>
    public const float BoxMeetFromGoalMeters = 11f;

    /// <summary>Lateral lane of a box arrival, in metres.</summary>
    public const float BoxLaneMeters = 6f;

    /// <summary>How far goal-side of the box a timed arrival holds, in metres.</summary>
    public const float BoxHoldFromGoalMeters = 25f;

    /// <summary>Ticks a late box arrival holds before attacking the region (0.4 s).</summary>
    public const int LateArrivalHoldTicks = 24;

    /// <summary>Grace after the planned arrival during which the region is still a place to meet a ball.</summary>
    public const int ArrivalGraceTicks = 60;

    /// <summary>Below this the team is not in a state to run into space at all.</summary>
    private const float MinLaneMeters = 4f;

    private readonly PitchDimensions _pitch;
    private readonly RunAdvertisementBoard? _board;
    private readonly RunPath[] _paths = new RunPath[PathCapacity];
    private readonly RunScoreTerms[] _terms = new RunScoreTerms[PathCapacity];
    private int _count;
    private RunIntent? _intent;
    private int _publishedPlayerId;
    private TeamId _publishedTeam;

    /// <param name="board">
    /// The team's advertisement board (A5.3). Null when the caller has no match communication (a probe or a
    /// unit test built without one): runs then stay local to the runner, exactly as they did before A5.3.
    /// </param>
    public RunCandidateGenerator(PitchDimensions pitch, RunAdvertisementBoard? board = null)
    {
        _pitch = pitch;
        _board = board;
    }

    /// <summary>Paths emitted by the last <see cref="Generate"/> call.</summary>
    public int Count => _count;

    /// <summary>One emitted path. <paramref name="index"/> must be below <see cref="Count"/>.</summary>
    public RunPath PathAt(int index) => _paths[index];

    /// <summary>One emitted path's score terms. <paramref name="index"/> must be below <see cref="Count"/>.</summary>
    public RunScoreTerms TermsAt(int index) => _terms[index];

    /// <summary>The run this player has adopted, if any. Published to the team board (A5.3).</summary>
    public RunIntent? Intent => _intent;

    /// <summary>
    /// Builds this decision's path set. Returns the number of paths emitted (0 when the player is not part
    /// of a possession that could use one).
    /// </summary>
    /// <param name="offsideLineProjX">The offside line from what the player can see (attack-relative projection).</param>
    /// <param name="tick">Absolute engine tick.</param>
    /// <param name="maxPaths">Bound on emitted paths (<see cref="AiRuntimeConfig.MaxRunCandidates"/>).</param>
    public int Generate(in FootballerObservation obs, float offsideLineProjX, int tick, int maxPaths)
    {
        _count = 0;

        // A run is an attacking act: it only exists while this team has the ball.
        if (obs.PossessingTeam != obs.SelfTeam) return 0;
        if (obs.SelfRole == TacticalRole.Goalkeeper) return 0;

        Vector2? anchorOrNull = obs.TacticalJobAnchor is Vector3 anchor
            ? new Vector2(anchor.X, anchor.Z)
            : null;
        if (anchorOrNull is not Vector2 dutyAnchor) return 0;

        Span<RunKind> kinds = stackalloc RunKind[MaxKindsPerDuty];
        int kindCount = KindsFor(obs.TacticalJob, kinds);
        if (kindCount == 0) return 0;

        FootballerObservation obsRef = obs;
        Vector2 selfXZ = new(obsRef.SelfPosition.X, obsRef.SelfPosition.Z);
        Vector2 ballXZ = new(obsRef.PerceivedBallPosition.X, obsRef.PerceivedBallPosition.Z);
        Vector2 carrierXZ = CarrierXZ(obsRef, ballXZ);
        float attackDirX = obsRef.AttackingDirectionX;

        // The adopted run survives only while it is still a legal, live intention under this duty.
        if (_intent is RunIntent claimed) _intent = ApplyClaim(claimed, obsRef, tick);

        if (_intent is RunIntent live && !IsIntentValid(live, obsRef, offsideLineProjX, tick))
            _intent = null;

        // A5.3: once a team-mate has played the ball into the region, the frozen path is the only run on
        // offer. Re-selecting another kind is how a runner abandons a near-post run two ticks before the
        // ball lands in it; the other duties still compete (shape, press, the pass interception).
        if (_intent is RunIntent receiving && IsClaimLive(obsRef, tick))
        {
            Emit(receiving.Path, RunScoring.Score(receiving.Path, obsRef, carrierXZ, obsRef.TeamSecurity));
            return _count;
        }

        int budget = maxPaths < 1 ? 1 : maxPaths;
        for (int i = 0; i < kindCount && _count < budget; i++)
        {
            RunKind kind = kinds[i];

            if (_intent is RunIntent adopted && adopted.Kind == kind)
            {
                // Re-offer the frozen geometry: the score is live, the meeting region is not.
                Emit(adopted.Path, RunScoring.Score(adopted.Path, obsRef, carrierXZ, obsRef.TeamSecurity));
                continue;
            }

            if (!TryBuild(kind, obsRef, selfXZ, ballXZ, carrierXZ, dutyAnchor, attackDirX, offsideLineProjX,
                    tick, out RunPath path))
            {
                continue;
            }

            RunScoreTerms terms = RunScoring.Score(path, obsRef, carrierXZ, obsRef.TeamSecurity);
            path = path with { Confidence01 = ConfidenceFrom(terms) };
            Emit(path, terms);
        }

        return _count;
    }

    /// <summary>
    /// Records what the controller actually chose. A chosen run path becomes the adopted intent with its
    /// geometry frozen; anything else ends the previous run, because a player cannot both hold a run and do
    /// something else.
    ///
    /// The adopted run is published to the team board (A5.3) on the same call: the runner tells his team
    /// where and when he will be, and the board applies the communication delay. A run that ends is
    /// withdrawn, so a carrier never plays to a region the runner has already left.
    /// </summary>
    public void NoteSelected(in FootballerObservation obs, in CandidateKey key)
    {
        if (key.Kind != CandidateKind.Run)
        {
            EndIntent();
            return;
        }

        for (int i = 0; i < _count; i++)
        {
            if (!RunTags.KeyOf(_paths[i].Kind).Equals(key)) continue;

            RunPath path = _paths[i];
            if (_intent is RunIntent existing &&
                existing.Kind == path.Kind &&
                existing.Duty == obs.TacticalJob &&
                Vector2.DistanceSquared(existing.Path.MeetXZ, path.MeetXZ) < 1e-4f)
            {
                // Same run, re-chosen: keep the adoption tick so the window does not slide every tick. It is
                // still published, because a claim may have lengthened the window since it was last heard.
                Publish(obs, existing);
                return;
            }

            var adopted = new RunIntent(path, obs.TacticalJob, obs.Tick);
            _intent = adopted;
            Publish(obs, adopted);
            return;
        }

        EndIntent();
    }

    /// <summary>Drops the path set and the adopted run, and withdraws its advertisement (A1.5/A5.3).</summary>
    public void Reset()
    {
        _count = 0;
        EndIntent();
    }

    private void Publish(in FootballerObservation obs, in RunIntent intent)
    {
        if (_board is null) return;

        _board.Publish(obs.SelfTeam, obs.SelfId, intent, obs.Tick);
        _publishedPlayerId = obs.SelfId;
        _publishedTeam = obs.SelfTeam;
    }

    private void EndIntent()
    {
        // Only a run that was actually announced needs withdrawing; a player whose run was never heard (or
        // who is not the player this generator was publishing for) has nothing to retract.
        if (_publishedPlayerId > 0) _board?.Withdraw(_publishedTeam, _publishedPlayerId);
        _intent = null;
        _publishedPlayerId = 0;
    }

    /// <summary>
    /// The kinds a duty may run, in the order they are offered. Support and depth come before
    /// speculation, exactly as A5.1 asks.
    /// </summary>
    public static int KindsFor(TacticalJob duty, Span<RunKind> buffer)
    {
        switch (duty)
        {
            case TacticalJob.DepthRun:
                buffer[0] = RunKind.InBehind;
                buffer[1] = RunKind.Pin;
                buffer[2] = RunKind.LateBoxArrival;
                buffer[3] = RunKind.BlindSideArrival;
                return 4;

            case TacticalJob.Overlap:
                buffer[0] = RunKind.Overlap;
                buffer[1] = RunKind.Underlap;
                buffer[2] = RunKind.CheckShort;
                return 3;

            case TacticalJob.FarSideWidth:
                buffer[0] = RunKind.HoldWidth;
                buffer[1] = RunKind.InBehind;
                buffer[2] = RunKind.CheckShort;
                return 3;

            case TacticalJob.ThirdPlayerOption:
                buffer[0] = RunKind.CheckShort;
                buffer[1] = RunKind.Underlap;
                buffer[2] = RunKind.InBehind;
                return 3;

            case TacticalJob.NearOutlet:
                buffer[0] = RunKind.CheckShort;
                buffer[1] = RunKind.RecycleSupport;
                return 2;

            default:
                return 0;
        }
    }

    /// <summary>
    /// Geometry, timing and the arrival window for one kind. Returns false when the situation simply does
    /// not contain that path (no space behind the line, a defender nobody is pinning, too far from the
    /// box), which is how the set stays honest instead of offering every kind at every moment.
    /// </summary>
    private bool TryBuild(
        RunKind kind,
        in FootballerObservation obs,
        Vector2 selfXZ,
        Vector2 ballXZ,
        Vector2 carrierXZ,
        Vector2 dutyAnchor,
        float attackDirX,
        float offsideLineProjX,
        int tick,
        out RunPath path)
    {
        path = default;

        float halfLength = _pitch.HalfLength;
        float halfWidth = _pitch.HalfWidth;
        float selfProj = selfXZ.X * attackDirX;
        float carrierProj = carrierXZ.X * attackDirX;
        float maxAttackingProj = offsideLineProjX - OnsideMarginMeters;

        float laneZ = MathF.Abs(dutyAnchor.Y) > 1f ? dutyAnchor.Y : selfXZ.Y;
        float width01 = obs.TeamProfile.AttackingWidth01;
        float wideZ = (halfWidth - 3f) * (0.55f + 0.55f * width01);

        Vector2 meet;
        float meetRadius;
        int holdUntilTick = tick;
        Vector2 holdXZ = selfXZ;

        switch (kind)
        {
            case RunKind.HoldWidth:
            {
                float side = MathF.Abs(laneZ) > 1f ? MathF.Sign(laneZ) : MathF.Sign(selfXZ.Y >= 0f ? 1f : -1f);
                float proj = MathF.Min(selfProj + 2f, maxAttackingProj);
                meet = new Vector2(attackDirX * proj, side * wideZ);
                meetRadius = 6f;
                break;
            }

            case RunKind.CheckShort:
            {
                float distanceToBall = Vector2.Distance(selfXZ, ballXZ);
                if (distanceToBall < CheckShortMinDistanceMeters) return false;

                Vector2 awayFromBall = SimMath.SafeNormalize(selfXZ - ballXZ, new Vector2(-attackDirX, 0f));
                meet = ballXZ + awayFromBall * CheckShortDistanceMeters;
                meetRadius = 5f;
                break;
            }

            case RunKind.InBehind:
            {
                if (offsideLineProjX <= 10f) return false;
                if (maxAttackingProj <= selfProj + MinRunGainMeters) return false;

                float runProj = MathF.Min(maxAttackingProj, selfProj + InBehindRunMeters);
                meet = new Vector2(attackDirX * runProj, laneZ);
                meetRadius = DepthMeetRadiusMeters;
                break;
            }

            case RunKind.Overlap:
            {
                if (MathF.Abs(carrierXZ.Y) < 8f) return false;   // not a wide situation: nothing to overlap
                if (selfProj > carrierProj + 1f) return false;   // already beyond him: the overlap is done

                float side = MathF.Sign(carrierXZ.Y >= 0f ? 1f : -1f);
                float runProj = MathF.Min(maxAttackingProj, carrierProj + OverlapAheadMeters);
                meet = new Vector2(attackDirX * runProj, side * wideZ);
                meetRadius = 5f;
                break;
            }

            case RunKind.Underlap:
            {
                if (MathF.Abs(carrierXZ.Y) < 8f) return false;

                float side = MathF.Sign(carrierXZ.Y >= 0f ? 1f : -1f);
                float insideZ = MathF.Max(UnderlapInsideMeters, MathF.Abs(carrierXZ.Y) - UnderlapInsideMeters);
                insideZ = MathF.Min(insideZ, halfWidth - 6f) * side;
                float runProj = MathF.Min(maxAttackingProj, carrierProj + UnderlapAheadMeters);
                meet = new Vector2(attackDirX * runProj, insideZ);
                meetRadius = 5f;
                break;
            }

            case RunKind.RecycleSupport:
            {
                float z = Math.Clamp(laneZ, -halfWidth + 4f, halfWidth - 4f);
                meet = new Vector2(ballXZ.X - attackDirX * RecycleDropMeters, z);
                meetRadius = 5f;
                break;
            }

            case RunKind.Pin:
            {
                if (offsideLineProjX <= 10f) return false;
                if (maxAttackingProj - PinBehindLineMeters <= selfProj + MinRunGainMeters) return false;
                if (!TryNearestOpponent(obs, selfXZ, PinRadiusMeters, out Vector2 pinned)) return false;

                meet = new Vector2(
                    attackDirX * (maxAttackingProj - PinBehindLineMeters),
                    Math.Clamp(pinned.Y, -halfWidth + 5f, halfWidth - 5f));
                meetRadius = 4f;
                break;
            }

            case RunKind.LateBoxArrival:
            case RunKind.BlindSideArrival:
            {
                if (kind == RunKind.LateBoxArrival)
                {
                    if (MathF.Max(carrierProj, selfProj) < FinalThirdProjX) return false;
                }
                else if (MathF.Max(carrierProj, selfProj) < FinalThirdProjX - 6f)
                {
                    return false;
                }

                float boxProj = halfLength - BoxMeetFromGoalMeters;
                float runProj = MathF.Min(boxProj, maxAttackingProj);
                if (runProj <= selfProj + MinRunGainMeters) return false;

                float side = kind == RunKind.LateBoxArrival
                    ? (MathF.Abs(laneZ) > 1f ? MathF.Sign(laneZ) : MathF.Sign(selfXZ.Y >= 0f ? 1f : -1f))
                    : BlindSideSign(obs, attackDirX);

                meet = new Vector2(attackDirX * runProj, side * BoxLaneMeters);
                meetRadius = BoxMeetRadiusMeters;

                // A late arrival is timed, not chased: hold at the edge of the box until the window opens.
                float holdProj = MathF.Min(maxAttackingProj, halfLength - BoxHoldFromGoalMeters);
                if (holdProj > selfProj + MinRunGainMeters)
                {
                    holdXZ = new Vector2(attackDirX * holdProj, side * BoxLaneMeters);
                    holdUntilTick = tick + LateArrivalHoldTicks;
                }

                break;
            }

            default:
                return false;   // Decoy is declared but priced by A5.6, not offered here.
        }

        meet = ClampToPitch(meet);
        if (!IsFinite(meet)) return false;

        ReachEstimate reach = TimeToReachModel.ToPoint(
            ReachKinematics.FromObservation(obs), meet, RunScoring.ArrivalHorizonSeconds);
        if (!reach.Reachable) return false;

        int travelTicks = (int)MathF.Ceiling(reach.Seconds / SimConstants.FixedDeltaTime);
        int holdTicks = holdUntilTick > tick ? holdUntilTick - tick : 0;
        int meetTick = tick + holdTicks + travelTicks;

        path = new RunPath(
            kind,
            selfXZ,
            meet,
            meetRadius,
            tick,
            meetTick,
            meetTick + ArrivalGraceTicks,
            holdUntilTick,
            holdXZ,
            0f);
        return true;
    }

    /// <summary>
    /// Which side of the marker a blind-side arrival comes in on: the far post relative to the man in the
    /// box. Falls back to the player's own side when nobody is marking the space.
    /// </summary>
    private float BlindSideSign(in FootballerObservation obs, float attackDirX)
    {
        Vector2 boxCentre = new(attackDirX * (_pitch.HalfLength - BoxMeetFromGoalMeters - 4f), 0f);
        float best = float.MaxValue;
        float side = 0f;

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == obs.SelfTeam) continue;
            if (entity.Role == TacticalRole.Goalkeeper) continue;

            Vector2 position = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            if (!SimMath.IsFinite(position)) continue;

            float distance = Vector2.Distance(position, boxCentre);
            if (distance >= best) continue;
            best = distance;
            side = position.Y;
        }

        if (best > 18f || MathF.Abs(side) < 1f)
            return MathF.Sign(obs.SelfPosition.Z >= 0f ? 1f : -1f);

        return side >= 0f ? -1f : 1f;
    }

    private static bool TryNearestOpponent(
        in FootballerObservation obs,
        Vector2 pointXZ,
        float radiusMeters,
        out Vector2 positionXZ)
    {
        float best = radiusMeters;
        positionXZ = default;
        bool found = false;

        IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
        for (int i = 0; i < visible.Count; i++)
        {
            PerceivedEntity entity = visible[i];
            if (entity.Team == obs.SelfTeam) continue;

            Vector2 position = new(entity.EstimatedPosition.X, entity.EstimatedPosition.Z);
            if (!SimMath.IsFinite(position)) continue;

            float distance = Vector2.Distance(position, pointXZ);
            if (distance >= best) continue;
            best = distance;
            positionXZ = position;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// The carrier's position as this player knows it: the controlling player when there is one, the ball
    /// otherwise. Not a truth read — it comes out of the observation's own entities.
    /// </summary>
    private static Vector2 CarrierXZ(in FootballerObservation obs, Vector2 ballXZ)
    {
        if (obs.BallControllingPlayerId is int carrierId)
        {
            IReadOnlyList<PerceivedEntity> visible = obs.VisiblePlayers;
            for (int i = 0; i < visible.Count; i++)
            {
                if (visible[i].PlayerId != carrierId) continue;
                Vector2 position = new(visible[i].EstimatedPosition.X, visible[i].EstimatedPosition.Z);
                if (SimMath.IsFinite(position)) return position;
            }
        }

        return ballXZ;
    }

    /// <summary>
    /// The whole intent is still a live, legal intention: inside its window, under the same duty, with the
    /// ball still ours, and (for a forward run) still legally receivable with a margin.
    /// </summary>
    private static bool IsIntentValid(
        in RunIntent intent,
        in FootballerObservation obs,
        float offsideLineProjX,
        int tick)
    {
        if (obs.PossessingTeam != obs.SelfTeam) return false;
        if (intent.Duty != obs.TacticalJob) return false;
        if (tick > intent.Path.ExpiresTick) return false;

        if (!IsForwardRun(intent.Kind)) return true;

        float meetProj = intent.Path.MeetXZ.X * obs.AttackingDirectionX;
        return meetProj <= offsideLineProjX - OnsideMarginMeters + OnsideToleranceMeters;
    }

    /// <summary>
    /// A5.3: a team-mate has played the ball into this region. The window is extended to the ball's arrival
    /// (plus the claim's grace) and any timed hold is released, so the run is received rather than timed: a
    /// late box arrival must not stay at the edge of the box while the pass is landing behind him.
    /// </summary>
    private RunIntent ApplyClaim(in RunIntent intent, in FootballerObservation obs, int tick)
    {
        if (!IsClaimLive(obs, tick, out RunClaim claim)) return intent;

        int expires = Math.Max(intent.Path.ExpiresTick, claim.ArriveTick + RunAdvertisementBoard.ClaimGraceTicks);
        int holdUntil = Math.Min(intent.Path.HoldUntilTick, tick);
        if (expires == intent.Path.ExpiresTick && holdUntil == intent.Path.HoldUntilTick) return intent;

        return intent with
        {
            Path = intent.Path with { ExpiresTick = expires, HoldUntilTick = holdUntil }
        };
    }

    private bool IsClaimLive(in FootballerObservation obs, int tick) => IsClaimLive(obs, tick, out _);

    private bool IsClaimLive(in FootballerObservation obs, int tick, out RunClaim claim)
    {
        claim = default;
        return _board is not null && _board.TryGetClaim(obs.SelfTeam, obs.SelfId, tick, out claim);
    }

    /// <summary>Kinds whose meeting region must stay legally receivable before the ball is released.</summary>
    public static bool IsForwardRun(RunKind kind) => kind is RunKind.InBehind or RunKind.Overlap
        or RunKind.Underlap or RunKind.LateBoxArrival or RunKind.BlindSideArrival or RunKind.Pin;

    /// <summary>How sure of the run the arrival race and the pass into it make the runner (A5.3 advertises it).</summary>
    private static float ConfidenceFrom(in RunScoreTerms terms)
    {
        float confidence = 0.20f + 0.55f * terms.ArrivalAdvantage01 + 0.25f * terms.PassAvailability01;
        return confidence < 0f ? 0f : confidence > 1f ? 1f : confidence;
    }

    private void Emit(in RunPath path, in RunScoreTerms terms)
    {
        if (_count >= PathCapacity) return;
        _paths[_count] = path;
        _terms[_count] = terms;
        _count++;
    }

    private Vector2 ClampToPitch(Vector2 point) => new(
        Math.Clamp(point.X, -_pitch.HalfLength + 3f, _pitch.HalfLength - 3f),
        Math.Clamp(point.Y, -_pitch.HalfWidth + 2.5f, _pitch.HalfWidth - 2.5f));

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
