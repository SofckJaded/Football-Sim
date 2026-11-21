using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Physics;

namespace FootballSim.AI;

/// <summary>
/// One continuation the shallow search scored (A8.2): the first action it belongs to (a pass to
/// <see cref="ReceiverId"/>), the continuation's own region and meeting tick, the confidence it earns, and
/// the terms that produced both numbers.
///
/// The record is the search's answer to "which continuation is worth a contract", in exactly the vocabulary
/// A8.1's <see cref="CombinationContract"/> already uses — a kind, participants, a region, a meeting tick and
/// a confidence. It adds no action: the continuation is still an ordinary pass the receiver plays, and the
/// search's result only decides <i>which</i> promise, if any, the carrier commits to.
/// </summary>
/// <param name="Kind">The combination the continuation belongs to: a one-two or a third-player move.</param>
/// <param name="ReceiverId">The first receiver: the team-mate the first pass is aimed at.</param>
/// <param name="ThirdPlayerId">The advertised runner a third-player layoff is aimed at; 0 for a one-two.</param>
/// <param name="RegionXZ">Where the continuation ball should be played, in pitch coordinates.</param>
/// <param name="RegionRadiusMeters">How wide that meeting region is.</param>
/// <param name="ArrivalTick">The live tick the continuation ball can first arrive at the region.</param>
/// <param name="Confidence01">The contract's confidence: the continuation's quality scaled by the chance the
/// first ball actually arrives. This is the number A8.1's initiation preference is multiplied by.</param>
/// <param name="Value01">The depth-two rank value: <see cref="Confidence01"/> scaled by how much the
/// continuation advances play. The search's own threshold is applied to this number.</param>
/// <param name="FirstLegCompletion01">The first pass's completion probability, from the ordinary pass model
/// on the carrier's own picture. Already charged against the carrier's defenders including their sighting
/// uncertainty; nothing here adds a second penalty to the pass that will be priced by its own A4 score.</param>
/// <param name="ContinuationQuality01">The continuation's own quality (lane, space, progression, and for a
/// third-player move the runner's advertised promise), reusing A8.1's confidence shape so the search and the
/// heuristic speak the same scale.</param>
public readonly record struct ShallowContinuation(
    CombinationKind Kind,
    int ReceiverId,
    int ThirdPlayerId,
    Vector2 RegionXZ,
    float RegionRadiusMeters,
    int ArrivalTick,
    float Confidence01,
    float Value01,
    float FirstLegCompletion01,
    float ContinuationQuality01)
{
    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        $"{Kind} first->#{ReceiverId}" + (ThirdPlayerId > 0 ? $" then->#{ThirdPlayerId}" : string.Empty) +
        $" region ({RegionXZ.X:F1},{RegionXZ.Y:F1}) r{RegionRadiusMeters:F1} meet t{ArrivalTick}" +
        $" conf {Confidence01:F2} value {Value01:F2} (p1 {FirstLegCompletion01:F2}, q {ContinuationQuality01:F2})";
}

/// <summary>
/// A8.2's shallow continuation search: from the carrier's <b>own perceived state</b>, score the best few
/// first actions and, for each, at most a few continuations — depth two, deterministic, bounded, and never a
/// read of the authoritative world.
///
/// <b>What it searches.</b> The first actions are the carrier's highest-ranked pass receivers — the same
/// receivers the ordinary decision has already found eligible and ranked (the bound is
/// <see cref="MaxFirstActions"/> of them). For each, the first leg is the feet pass the ordinary candidate
/// path will price, estimated by the ordinary pass model on this carrier's observations. The continuations
/// are the vocabulary A8.1 already owns:
/// <list type="bullet">
/// <item><b>two one-two return regions</b> — the deep region A8.1's heuristic places ahead of the initiator,
/// and a shorter bounce region; each is accepted only where the initiator could actually run to it (inside
/// the pitch, onside, in room) and the receiver could actually play it (lane, timing);</item>
/// <item><b>the best advertised layoff</b> — a third team-mate's live A5.3 run, valued by its own promise,
/// the layoff lane and how well the layoff's arrival fits the runner's advertised meeting window.</item>
/// </list>
/// That is at most three scored continuations per first action, at most nine in total — the plan's initial
/// budget ("the best three first actions and at most three continuations each, to depth two").
///
/// <b>Depth two, no rollout.</b> A continuation is scored, not simulated: the second ball is valued from the
/// lane, the room at the region, the progression it buys and, for a third-player move, the runner's promise —
/// all terms the team is allowed to hold. There is no truth-state rollout, no second world and no simulated
/// opposing reaction: an opponent enters only through the carrier's own observation of him, with the
/// uncertainty of that sighting charged against the option.
///
/// <b>Defender uncertainty.</b> The first leg's uncertainty is the pass model's (A2.2's sighting age widened
/// into arrival times). The continuation's lane is judged with
/// <see cref="CandidateGenerator.EvaluateLaneClearance(Vector2, Vector2, System.Collections.Generic.IReadOnlyList{PerceivedEntity}, TeamId, bool)"/>
/// charging each defender's positional uncertainty into the corridor: a remembered defender may be nearer
/// the second ball's line than his stale position says, so the promise is priced against the worst case.
///
/// <b>Why not a second decision pipeline.</b> The search produces at most one proposal per examined first
/// action, in A8.1's own proposal shape, and drops it in the existing proposal table. The carrier's choice is
/// unchanged: every pass is still scored by the A4 value model and the contract only adds the bounded
/// initiation preference A8.1 defined. The search changes <i>which</i> promise is offered, never how an
/// action is priced, and it invents no candidate, sight, reach or contact.
///
/// <b>Determinism.</b> Receivers are examined in the order the caller ranked them; teammates and opponents
/// are read in perception order; advertisements in board index order. No collection is iterated unordered, no
/// RNG is touched, and the buffers are reused so a decision allocates nothing.
/// </summary>
public sealed class ShallowActionSearch
{
    /// <summary>
    /// First actions examined per decision. Three is the plan's initial budget: a carrier cannot realistically
    /// commit to more promises than that, and each one costs a pass plan plus its continuations.
    /// </summary>
    public const int MaxFirstActions = 3;

    /// <summary>
    /// Continuation branches scored per first action: two one-two return regions (deep and short) and the
    /// best advertised layoff. Three is the plan's initial budget.
    /// </summary>
    public const int MaxContinuationsPerAction = 3;

    /// <summary>Hard ceiling on continuations scored in one decision (the product of the two bounds).</summary>
    public const int MaxContinuationsEvaluated = MaxFirstActions * MaxContinuationsPerAction;

    /// <summary>
    /// Lowest depth-two value that is worth a contract. Below it the move is not a combination worth promising
    /// — the ordinary pass is still played, it is simply not wrapped in a contract. Deliberately a value
    /// threshold rather than a pressure test: pressure is A8.1's heuristic trigger, and the search exists to
    /// replace "somebody is pressed" with "this continuation is worth it".
    /// </summary>
    public const float MinContinuationValue01 = 0.30f;

    /// <summary>How far ahead of the initiator the deep one-two return region is placed, in metres.</summary>
    internal const float DeepReturnForwardMeters = CandidateGenerator.OneTwoReturnForwardMeters;

    /// <summary>How far sideways toward the receiver's side the deep region is placed, in metres.</summary>
    internal const float DeepReturnLateralMeters = CandidateGenerator.OneTwoReturnLateralMeters;

    /// <summary>How far ahead of the initiator the short bounce return region is placed, in metres.</summary>
    internal const float ShortReturnForwardMeters = 5f;

    /// <summary>How far sideways toward the receiver's side the short region is placed, in metres.</summary>
    internal const float ShortReturnLateralMeters = 1.2f;

    /// <summary>Width of the deep return region, in metres (A8.1's figure: a region, not a point).</summary>
    internal const float DeepReturnRadiusMeters = CandidateGenerator.OneTwoReturnRadiusMeters;

    /// <summary>Width of the short return region, in metres. Narrower: it is a bounce, not a run into space.</summary>
    internal const float ShortReturnRadiusMeters = 2.2f;

    /// <summary>
    /// Weight of the first leg's completion probability in the contract's confidence. The search adds the one
    /// term A8.1's heuristic could not know — whether the first ball is actually likely to arrive — but it
    /// does not replace the continuation's own quality: a well-timed return that is never reached is still a
    /// promise the team should not be bound to, and a poor return on a certain pass is still poor.
    /// </summary>
    internal const float FirstLegConfidenceWeight = 0.40f;

    /// <summary>Smallest progression a return region must offer, in metres (A8.1's gate, reused).</summary>
    internal const float MinReturnProgressionMeters = CandidateGenerator.OneTwoMinProgressionMeters;

    private readonly PitchDimensions _pitch;
    private readonly PassPlanner _planner;
    private readonly PassSuccessModel _passSuccess;

    private readonly ShallowContinuation[] _proposals = new ShallowContinuation[MaxFirstActions];

    private int _proposalCount;
    private int _firstActionsEvaluated;
    private int _continuationsEvaluated;

    public ShallowActionSearch(PitchDimensions pitch, PassPlanner planner, PassSuccessModel passSuccess)
    {
        _pitch = pitch;
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _passSuccess = passSuccess ?? throw new ArgumentNullException(nameof(passSuccess));
    }

    /// <summary>Proposals the most recent search produced: one per examined first action that earned one.</summary>
    public int ProposalCount => _proposalCount;

    /// <summary>First actions examined by the most recent search (never above <see cref="MaxFirstActions"/>).</summary>
    public int FirstActionsEvaluated => _firstActionsEvaluated;

    /// <summary>Continuation branches scored by the most recent search (never above <see cref="MaxContinuationsEvaluated"/>).</summary>
    public int ContinuationsEvaluated => _continuationsEvaluated;

    /// <summary>One proposal of the most recent search. <paramref name="index"/> must be below <see cref="ProposalCount"/>.</summary>
    public ShallowContinuation ProposalAt(int index) => _proposals[index];

    /// <summary>
    /// The best proposal of the most recent search, if any. Convenience for diagnostics and tests; the
    /// decision path reads every proposal, because each belongs to a different first action.
    /// </summary>
    public bool TryGetBestProposal(out ShallowContinuation best)
    {
        best = default;
        if (_proposalCount == 0) return false;

        best = _proposals[0];
        for (int i = 1; i < _proposalCount; i++)
        {
            if (_proposals[i].Value01 <= best.Value01) continue;
            best = _proposals[i];
        }

        return true;
    }

    /// <summary>
    /// Runs one bounded search from the carrier's own observation.
    /// </summary>
    /// <param name="obs">The carrier's observation. Every teammate, opponent and advertisement read here comes
    /// from it or from the team's own communication boards — the search has no other input, which is what makes
    /// a truth-state rollout impossible rather than merely discouraged.</param>
    /// <param name="rankedReceiverIds">Eligible pass receivers in the carrier's own rank order (the caller has
    /// already applied the ordinary eligibility gates). The first <see cref="MaxFirstActions"/> are examined.</param>
    /// <param name="ballXZ">The carrier's believed ball position (the pass plans start from it).</param>
    /// <param name="offsideLineProjX">The offside line's projected X, in the carrier's attacking direction.</param>
    /// <param name="runs">The team's run advertisement board, or null when the match has no communication
    /// context (a unit test built without one). A null board means no third-player continuation can exist,
    /// because no run has been announced.</param>
    public void Search(
        FootballerObservation obs,
        ReadOnlySpan<int> rankedReceiverIds,
        Vector2 ballXZ,
        float offsideLineProjX,
        RunAdvertisementBoard? runs)
    {
        _proposalCount = 0;
        _firstActionsEvaluated = 0;
        _continuationsEvaluated = 0;

        if (obs.SelfId <= 0) return;

        Vector2 selfXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        int examined = 0;

        for (int index = 0; index < rankedReceiverIds.Length; index++)
        {
            if (examined >= MaxFirstActions) break;
            int receiverId = rankedReceiverIds[index];
            if (receiverId <= 0 || receiverId == obs.SelfId) continue;
            if (!TryGetVisibleEntity(obs, receiverId, out PerceivedEntity receiver)) continue;

            examined++;
            _firstActionsEvaluated++;

            Vector2 receiverXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
            Vector2 receiverVelocityXZ = new(receiver.EstimatedVelocity.X, receiver.EstimatedVelocity.Z);

            // The first leg is the feet ball the ordinary carrier path prices for this receiver. Planning it
            // here (rather than guessing a flight time) is what lets the search use the one pass model.
            PassPlan firstPlan = _planner.PlanGroundPass(
                ballXZ,
                receiver.PlayerId,
                receiverXZ,
                receiverVelocityXZ,
                CandidateGenerator.MaxReceivableSpeed,
                extraLeadMeters: 0f,
                extraLeadDirection: default,
                maxLeadMeters: CandidateGenerator.FeetPassMaxLeadMeters,
                minPower01: 0f);
            if (!firstPlan.Feasible) continue;

            float firstLane = CandidateGenerator.EvaluateLaneClearance(
                ballXZ, firstPlan.TargetXZ, obs.VisiblePlayers, obs.SelfTeam);
            PassSuccessTerms firstTerms = _passSuccess.Estimate(obs, receiver, firstPlan, firstLane);
            if (firstTerms.LaneBlocked) continue;

            float firstLegCompletion = Math.Clamp(firstTerms.CompletionProbability01, 0f, 1f);
            if (firstLegCompletion <= 0f) continue;

            ShallowContinuation best;
            bool found = false;

            // Branch 1 and 2: a one-two return region, deep and short. Both are the same promise shape; the
            // search compares them because "run a little further" and "bounce it straight back" are genuinely
            // different moves and the geometry decides which is on.
            if (TryOneTwoContinuation(
                    obs, receiver, firstPlan, ballXZ, selfXZ, offsideLineProjX, firstLegCompletion,
                    DeepReturnForwardMeters, DeepReturnLateralMeters, DeepReturnRadiusMeters,
                    out ShallowContinuation deep))
            {
                _continuationsEvaluated++;
                best = deep;
                found = true;
            }
            else
            {
                best = default;
            }

            if (TryOneTwoContinuation(
                    obs, receiver, firstPlan, ballXZ, selfXZ, offsideLineProjX, firstLegCompletion,
                    ShortReturnForwardMeters, ShortReturnLateralMeters, ShortReturnRadiusMeters,
                    out ShallowContinuation bounce))
            {
                _continuationsEvaluated++;
                if (!found || bounce.Value01 > best.Value01)
                {
                    best = bounce;
                    found = true;
                }
            }

            // Branch 3: the best advertised layoff. A5.3's board is the only place a run is promised, so a
            // third-player continuation can only exist where a team-mate has announced one.
            if (TryThirdPlayerContinuation(
                    obs, receiver, firstPlan, firstLegCompletion, runs, out ShallowContinuation layoff))
            {
                _continuationsEvaluated++;
                if (!found || layoff.Value01 > best.Value01)
                {
                    best = layoff;
                    found = true;
                }
            }

            if (!found || best.Value01 < MinContinuationValue01) continue;
            if (_proposalCount >= _proposals.Length) continue;
            _proposals[_proposalCount++] = best;
        }
    }

    /// <summary>
    /// Builds and scores one one-two continuation: a return region ahead of the initiator, played back by
    /// <paramref name="receiver"/> at the tick both of them can meet it.
    ///
    /// The gates are the same physical ones A8.1's heuristic uses (inside the pitch, onside, real progression,
    /// room at the region, a lane from the receiver, a meeting inside the combination window), because those
    /// are feasibility rather than policy. What differs is the lane: it is judged with the defenders' sighting
    /// uncertainty charged in, so a stale picture produces a more conservative promise.
    /// </summary>
    private bool TryOneTwoContinuation(
        FootballerObservation obs,
        in PerceivedEntity receiver,
        in PassPlan firstPlan,
        Vector2 ballXZ,
        Vector2 selfXZ,
        float offsideLineProjX,
        float firstLegCompletion,
        float forwardMeters,
        float lateralMeters,
        float radiusMeters,
        out ShallowContinuation continuation)
    {
        continuation = default;

        Vector2 receiverXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
        Vector2 region = OneTwoRegion(selfXZ, receiverXZ, obs.AttackingDirectionX, forwardMeters, lateralMeters);
        region = new Vector2(
            Math.Clamp(region.X, -_pitch.HalfLength + ReturnRegionPitchMarginX, _pitch.HalfLength - ReturnRegionPitchMarginX),
            Math.Clamp(region.Y, -_pitch.HalfWidth + ReturnRegionPitchMarginY, _pitch.HalfWidth - ReturnRegionPitchMarginY));

        float progression = (region.X - selfXZ.X) * obs.AttackingDirectionX;
        if (progression < MinReturnProgressionMeters) return false;

        // A8.1's initiator re-checks these every decision and abandons the contract when they fail, so a region
        // that fails them here would be a promise nobody can keep.
        if (region.X * obs.AttackingDirectionX > OffsideTactics.MaxAttackingProjection(offsideLineProjX)) return false;

        float space = CandidateGenerator.EvaluateSpaceFreedom(region, obs.VisiblePlayers, obs.SelfTeam);
        if (space < CandidateGenerator.CombinationReturnRunMinSpace01) return false;

        // The receiver's lane, with each defender's believed uncertainty charged into the corridor (A8.2).
        float lane = CandidateGenerator.EvaluateLaneClearance(
            receiverXZ, region, obs.VisiblePlayers, obs.SelfTeam, chargeUncertainty: true);
        if (lane < CandidateGenerator.OneTwoMinLaneClearance01) return false;

        float travelSeconds = Vector2.Distance(selfXZ, region) / CandidateGenerator.OneTwoRunnerSpeedMetersPerSecond;
        float returnFlightSeconds = Vector2.Distance(receiverXZ, region) /
                                    CandidateGenerator.OneTwoReturnPassSpeedMetersPerSecond;
        float arrivalSeconds = MathF.Max(
            travelSeconds, firstPlan.FlightTimeSeconds + CandidateGenerator.OneTwoReceiverSetupSeconds + returnFlightSeconds);
        if (!float.IsFinite(arrivalSeconds) || arrivalSeconds <= 0f ||
            arrivalSeconds > CandidateGenerator.OneTwoMaxArrivalSeconds)
        {
            return false;
        }

        int arrivalTick = obs.Tick + Math.Max(6, (int)MathF.Round(arrivalSeconds * 60f));
        if (arrivalTick - obs.Tick > CombinationCoordinator.MaxArrivalLeadTicks) return false;

        // A8.1's confidence shape, so the search and the heuristic speak one scale. The search's one addition
        // is the probability the first ball arrives at all: a perfect return behind a ball that never gets
        // there is not a promise.
        float progressionQuality = Math.Clamp(progression / 10f, 0f, 1f);
        float quality = Math.Clamp(
            CandidateGenerator.OneTwoConfidenceBase
            + CandidateGenerator.OneTwoConfidenceLaneWeight * lane
            + CandidateGenerator.OneTwoConfidenceSpaceWeight * space
            + CandidateGenerator.OneTwoConfidenceProgressionWeight * progressionQuality,
            0f, 1f);
        float confidence = Math.Clamp(
            quality * (1f - FirstLegConfidenceWeight + FirstLegConfidenceWeight * firstLegCompletion), 0f, 1f);
        if (confidence <= 0f) return false;

        continuation = new ShallowContinuation(
            CombinationKind.OneTwo,
            receiver.PlayerId,
            ThirdPlayerId: 0,
            region,
            radiusMeters,
            arrivalTick,
            confidence,
            Value01: confidence * ProgressionSupport(progression),
            firstLegCompletion,
            quality);
        return true;
    }

    /// <summary>
    /// Builds and scores the best advertised third-player layoff from this receiver: a live A5.3 run by a third
    /// team-mate, close enough to lay the first ball off to, whose advertised window the layoff can actually be
    /// timed into. The run itself is A5.3's; this method only prices playing the first pass to the receiver so
    /// that layoff becomes playable.
    /// </summary>
    private bool TryThirdPlayerContinuation(
        FootballerObservation obs,
        in PerceivedEntity receiver,
        in PassPlan firstPlan,
        float firstLegCompletion,
        RunAdvertisementBoard? runs,
        out ShallowContinuation continuation)
    {
        continuation = default;
        if (runs is null) return false;

        int visibleCount = runs.VisibleCount(obs.SelfTeam, obs.Tick);
        if (visibleCount == 0) return false;

        Vector2 receiverXZ = new(receiver.EstimatedPosition.X, receiver.EstimatedPosition.Z);
        float receiverProj = receiverXZ.X * obs.AttackingDirectionX;
        float bestValue = 0f;
        bool found = false;

        for (int index = 0; index < visibleCount; index++)
        {
            if (!runs.TryGetVisibleAt(obs.SelfTeam, index, obs.Tick, out RunAdvertisement advert)) continue;
            if (advert.PlayerId == obs.SelfId || advert.PlayerId == receiver.PlayerId) continue;
            if (advert.MeetTick <= obs.Tick + CandidateGenerator.ThirdPlayerMinLeadTicks) continue;

            Vector2 region = advert.MeetXZ;
            float layoffMeters = Vector2.Distance(receiverXZ, region);
            if (layoffMeters < CandidateGenerator.ThirdPlayerMinLayoffMeters ||
                layoffMeters > CandidateGenerator.ThirdPlayerMaxLayoffMeters)
            {
                continue;
            }

            // The layoff must go forward of the first receiver; a third man arriving behind him is a second
            // outlet, not a continuation.
            if (region.X * obs.AttackingDirectionX <= receiverProj - 2f) continue;

            float layoffFlight = layoffMeters / CandidateGenerator.ThirdPlayerLayoffSpeedMetersPerSecond;
            float arrivalSeconds = firstPlan.FlightTimeSeconds
                                   + CandidateGenerator.OneTwoReceiverSetupSeconds + layoffFlight;
            if (!float.IsFinite(arrivalSeconds) || arrivalSeconds <= 0f ||
                arrivalSeconds > CandidateGenerator.OneTwoMaxArrivalSeconds)
            {
                continue;
            }

            int arrivalTick = obs.Tick + Math.Max(CandidateGenerator.ThirdPlayerMinLeadTicks,
                (int)MathF.Round(arrivalSeconds * 60f));
            if (arrivalTick > advert.ExpiresTick) continue;
            if (arrivalTick < advert.MeetTick - CandidateGenerator.ThirdPlayerEarlyArrivalTicks) continue;
            if (arrivalTick - obs.Tick > CombinationCoordinator.MaxArrivalLeadTicks) continue;

            float lateness = Math.Clamp(
                (arrivalTick - advert.MeetTick) /
                (float)Math.Max(1, advert.ExpiresTick - advert.MeetTick), 0f, 1f);

            float lane = CandidateGenerator.EvaluateLaneClearance(
                receiverXZ, region, obs.VisiblePlayers, obs.SelfTeam, chargeUncertainty: true);
            if (lane < CandidateGenerator.ThirdPlayerMinLaneClearance01) continue;

            float promise = advert.Promise01(obs.Tick);
            if (promise <= 0f) continue;

            float quality = Math.Clamp(
                CandidateGenerator.ThirdPlayerConfidenceBase * promise
                + 0.45f * lane
                + 0.15f * CandidateGenerator.EvaluateSpaceFreedom(region, obs.VisiblePlayers, obs.SelfTeam)
                - 0.25f * lateness,
                0f, 1f);
            float confidence = Math.Clamp(
                quality * (1f - FirstLegConfidenceWeight + FirstLegConfidenceWeight * firstLegCompletion), 0f, 1f);
            if (confidence <= 0f) continue;

            float progression = (region.X - receiverXZ.X) * obs.AttackingDirectionX;
            float value = confidence * ProgressionSupport(progression);
            if (found && value <= bestValue) continue;

            bestValue = value;
            found = true;
            continuation = new ShallowContinuation(
                CombinationKind.ThirdPlayer,
                receiver.PlayerId,
                advert.PlayerId,
                region,
                Math.Clamp(advert.MeetRadiusMeters,
                    CombinationCoordinator.MinReturnRadiusMeters,
                    CombinationCoordinator.MaxReturnRadiusMeters),
                arrivalTick,
                confidence,
                value,
                firstLegCompletion,
                quality);
        }

        return found;
    }

    /// <summary>
    /// The one-two return region: ahead of the initiator along the attacking axis, offset toward the receiver's
    /// side. One formula, used by both A8.1's heuristic and this search — two placements for one promise would
    /// be two conventions.
    /// </summary>
    public static Vector2 OneTwoRegion(
        Vector2 initiatorXZ,
        Vector2 receiverXZ,
        float attackingDirectionX,
        float forwardMeters,
        float lateralMeters)
    {
        float lateralSign = MathF.Abs(receiverXZ.Y - initiatorXZ.Y) > 0.5f
            ? MathF.Sign(receiverXZ.Y - initiatorXZ.Y)
            : 0f;
        return new Vector2(
            initiatorXZ.X + attackingDirectionX * forwardMeters,
            initiatorXZ.Y + lateralSign * lateralMeters);
    }

    /// <summary>
    /// How much a continuation's progression is worth as a rank multiplier (0.35..1): a return that gains
    /// twelve metres or more is worth its full rank, a safe sideways bounce much less, and no continuation is
    /// worth nothing at all. Progression saturating rather than growing keeps a long, speculative promise from
    /// outranking an equally likely short one on distance alone.
    /// </summary>
    private static float ProgressionSupport(float progressionMeters) =>
        0.35f + 0.65f * Math.Clamp(progressionMeters / 12f, 0f, 1f);

    /// <summary>Pitch margin inside which a return region is still a region, in metres along the length.</summary>
    internal const float ReturnRegionPitchMarginX = 4f;

    /// <summary>Pitch margin inside which a return region is still a region, in metres across the width.</summary>
    internal const float ReturnRegionPitchMarginY = 2.5f;

    /// <summary>The believed entity for one player, or false when this carrier cannot see him.</summary>
    private static bool TryGetVisibleEntity(FootballerObservation obs, int playerId, out PerceivedEntity entity)
    {
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity candidate = obs.VisiblePlayers[index];
            if (candidate.PlayerId != playerId) continue;
            entity = candidate;
            return true;
        }

        entity = default;
        return false;
    }
}
