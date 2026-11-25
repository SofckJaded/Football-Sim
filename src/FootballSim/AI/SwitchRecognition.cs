using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// A8.3: one switch / overload-to-isolate opportunity, as the carrier's own picture describes it.
///
/// The record exists so the opportunity is <b>explainable</b> rather than a single opaque bonus: every term is
/// 0..1 and higher-is-better, and <see cref="Preference"/> is the bounded nudge the carrier's pass score
/// receives when the terms say the far side is worth hitting. When the opportunity is not recognized the
/// preference is zero but the terms the carrier could measure are still filled in, so a test (or the
/// inspector) can see which fact was missing instead of being told only "no".
/// </summary>
/// <param name="ReceiverId">The far-side team-mate the switch would be played to.</param>
/// <param name="LateralMeters">How far across the pitch the receiving point is from the ball, in metres.</param>
/// <param name="CarrierPressure01">How closed down the carrier is (1 = an opponent on him, 0 = free).</param>
/// <param name="FarSideOpenness01">The room around the receiver, on the pass pre-rank's own space scale.</param>
/// <param name="Matchup01">How good the receiver's duel is: an isolated one-v-one scores high, being doubled up
/// scores low, a completely free man is good but not an isolation.</param>
/// <param name="Feasibility01">The pass model's own completion probability for this ball — the one model, read,
/// never re-estimated.</param>
/// <param name="TurnoverSafety01">How much rest defence the side has if the ball is lost here (0 = the minimum
/// the recognizer will accept, 1 = fully secure).</param>
/// <param name="Confidence01">The weighted combination of the five terms, 0..1.</param>
/// <param name="Preference">The action-value preference the pass earns: <c>MaxPreference × Confidence01</c>.</param>
public readonly record struct SwitchOpportunity(
    int ReceiverId,
    float LateralMeters,
    float CarrierPressure01,
    float FarSideOpenness01,
    float Matchup01,
    float Feasibility01,
    float TurnoverSafety01,
    float Confidence01,
    float Preference)
{
    /// <summary>True when the carrier's picture supports a switch, i.e. the pass earns a preference.</summary>
    public bool IsRecognized => Preference > 0f;

    /// <summary>Compact text form for a trace line, a probe or a test failure message.</summary>
    public string Describe() =>
        $"switch->#{ReceiverId} lateral {LateralMeters:F1} m conf {Confidence01:F2} pref {Preference:F2}" +
        $" (pressure {CarrierPressure01:F2}, far-side {FarSideOpenness01:F2}, matchup {Matchup01:F2}," +
        $" feasible {Feasibility01:F2}, turnover-safety {TurnoverSafety01:F2})";
}

/// <summary>
/// A8.3's switch / overload-to-isolate recognition: from the carrier's <b>own observation</b>, decide whether a
/// pass to a far-side team-mate is worth a bounded preference.
///
/// <b>Recognition, not a capability.</b> This module produces one number, a preference on a pass candidate that
/// already exists. It adds no candidate, no sight, no reach, no transmission range and no contact rule: the
/// pass is still planned by <see cref="Physics.PassPlanner"/> and priced by <see cref="PassSuccessModel"/>, and
/// execution still decides whether the ball is played. It is the A8.2 lesson applied to A8.3 — a new
/// "recognition" is a <i>choice among existing actions</i> with one bounded preference, never a new pipeline.
///
/// <b>The five inputs the plan names.</b>
/// <list type="bullet">
/// <item><b>Local pressure</b> — the carrier must actually be closed down. The gate is A7.2's own switch-call
/// threshold (<see cref="TeamCallPlanner.SwitchCarrierPressureMeters"/>): a carrier with nobody within five
/// metres can carry or pass as he likes, so there is no overload to escape. The term scales with how tight the
/// nearest opponent is inside <see cref="PressureRadiusMeters"/>.</item>
/// <item><b>Far-side opportunity</b> — the receiver must be far across the pitch (A7.2's
/// <see cref="TeamCallPlanner.SwitchMinLateralMeters"/> is the same far-side line this milestone and the call
/// already share) and stand in real room (<see cref="TeamCallPlanner.SwitchSpaceMeters"/>). The term uses
/// <see cref="CandidateGenerator.EvaluateSpaceFreedom"/>, the one space expression the pass pre-rank and A8.1
/// use, so the recognizer cannot disagree with them about how open a point is.</item>
/// <item><b>Receiver matchup</b> — is he isolated? A one-v-one with no cover is the point of
/// overload-to-isolate; a doubled-up receiver is not, and a completely free man is open rather than isolated.
/// The duel is also weighted by the scouted pace band (<see cref="PerceivedEntity.Capability"/>) of the
/// receiver against his marker, never by their live attributes.</item>
/// <item><b>Actual pass feasibility</b> — the pass-success model's own completion probability for this ball.
/// A crossfield ball that cannot be completed is not an opportunity, however free the far side is: below
/// <see cref="MinCompletionProbability01"/> (above the offer floor) the recognition is refused.</item>
/// <item><b>Turnover cost</b> — a switch is the ball played across the pitch, and losing it there is the
/// transition the side's rest defence exists for. The recognition therefore requires a live rest-defence
/// picture (<see cref="FootballerObservation.TeamSecurity"/>) that is at least as good as the one the
/// allocator demands before committing another player forward
/// (<see cref="TransitionSecurityModel.MinSecurityForForwardRun"/>), and scales with how good it is.
/// "No picture" is treated as <i>cannot say</i>, so an unknown rest defence never buys a switch.</item>
/// </list>
///
/// <b>No forced switch after N passes.</b> There is deliberately no pass counter, passage clock or match-time
/// input anywhere in this type: the recognition is a pure function of the current picture, so a side can never
/// reach a state where "it is time to switch". The same picture on the first decision of a passage and on the
/// twentieth produces the same result, which is pinned by tests.
///
/// <b>Determinism and cost.</b> Only the carrier's observation is read; players are examined in perception
/// order, no dictionary or unordered collection is iterated, no RNG is touched, and nothing is allocated.
/// </summary>
public static class SwitchRecognition
{
    /// <summary>
    /// Largest preference a recognized switch can earn, on the action-value scale. Deliberately inside the
    /// range of A4.4's stated preferences (the forward-progress preference tops out at
    /// <see cref="ActionValueScale.ProgressBonus"/>): a switch is worth leaning toward, never worth taking
    /// over a clearly better action, and the pass model's completion and loss terms still decide.
    /// </summary>
    public const float MaxPreference = 0.35f;

    /// <summary>
    /// Lowest completion the pass model must give the switch before it is recognized at all. This sits above
    /// <see cref="CandidateGenerator.MinViableCompletion01"/>, the floor a pass needs merely to be offered: an
    /// opportunity has to be actually playable, not merely legal.
    /// </summary>
    public const float MinCompletionProbability01 = 0.35f;

    /// <summary>Distance inside which a believed opponent counts as pressure on the carrier, in metres.</summary>
    public const float PressureRadiusMeters = 7f;

    /// <summary>
    /// Nearest-defender distance at which a far-side receiver counts as free rather than isolated, in metres.
    /// Inside it he is in a duel (which the matchup term prices); beyond it there is no marker to isolate from.
    /// </summary>
    public const float FreeReceiverMeters = 9f;

    /// <summary>
    /// Distance from the receiver inside which a second believed defender makes the matchup a double-up
    /// rather than a one-v-one, in metres.
    /// </summary>
    public const float CoverDistanceMeters = 6f;

    /// <summary>Pace difference that saturates the matchup's scouted pace edge, in metres per second.</summary>
    public const float PaceEdgeSpanMetersPerSecond = 4f;

    /// <summary>Weight of the carrier's local pressure in the confidence.</summary>
    public const float WeightCarrierPressure = 0.25f;

    /// <summary>Weight of the far side's room in the confidence.</summary>
    public const float WeightFarSideOpenness = 0.20f;

    /// <summary>Weight of the receiver matchup in the confidence.</summary>
    public const float WeightMatchup = 0.15f;

    /// <summary>Weight of the pass model's own completion probability in the confidence.</summary>
    public const float WeightFeasibility = 0.30f;

    /// <summary>Weight of the rest-defence picture in the confidence (a gate, and then a weight).</summary>
    public const float WeightTurnoverSafety = 0.10f;

    /// <summary>
    /// Rest defence a switch needs: the same aggregate the allocator demands before committing another player
    /// forward (<see cref="TransitionSecurityModel.MinSecurityForForwardRun"/>). A switch is that kind of
    /// commitment — one more player's worth of the pitch is risked on the ball arriving — so it uses the same
    /// threshold rather than a second definition of "secure enough".
    /// </summary>
    public const float MinSecurityForSwitch01 = TransitionSecurityModel.MinSecurityForForwardRun;

    /// <summary>
    /// Room a far-side receiver needs, on <see cref="CandidateGenerator.EvaluateSpaceFreedom"/>'s scale. It is
    /// A7.2's <see cref="TeamCallPlanner.SwitchSpaceMeters"/> expressed on the one space metric, so the call's
    /// "he is unmarked" and the carrier's "the far side is free" are the same statement.
    /// </summary>
    public const float MinFarSideOpenness01 =
        TeamCallPlanner.SwitchSpaceMeters / CandidateGenerator.SpaceFreedomSaturationMeters;

    /// <summary>
    /// Evaluates one pass from the carrier to <paramref name="receiver"/> as a possible switch.
    /// </summary>
    /// <param name="obs">The carrier's observation. It is the only input; nothing here reads the match.</param>
    /// <param name="receiver">The believed team-mate the ball would be played to.</param>
    /// <param name="arrivalPointXZ">Where the ball is actually aimed (the planned receiving point).</param>
    /// <param name="completionProbability01">The pass model's completion estimate for this ball — read from the
    /// one model, never re-estimated here.</param>
    public static SwitchOpportunity Evaluate(
        in FootballerObservation obs,
        in PerceivedEntity receiver,
        Vector2 arrivalPointXZ,
        float completionProbability01,
        float pitchHalfLength = 52.5f)
    {
        Vector2 ballXZ = new(obs.PerceivedBallPosition.X, obs.PerceivedBallPosition.Z);
        Vector2 carrierXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);

        float lateralMeters = MathF.Abs(arrivalPointXZ.Y - ballXZ.Y);
        float carrierPressure = CarrierPressure01(obs, carrierXZ, out float carrierNearestOpponent);
        float openness = CandidateGenerator.EvaluateSpaceFreedom(
            arrivalPointXZ, obs.VisiblePlayers, obs.SelfTeam);
        float matchup = Matchup01(obs, receiver, arrivalPointXZ);
        float feasibility = float.IsFinite(completionProbability01)
            ? Math.Clamp(completionProbability01, 0f, 1f)
            : 0f;
        float security = obs.TeamSecurity.Security01;
        float turnoverSafety = security > MinSecurityForSwitch01
            ? Math.Clamp((security - MinSecurityForSwitch01) / (1f - MinSecurityForSwitch01), 0f, 1f)
            : 0f;

        var opportunity = new SwitchOpportunity(
            receiver.PlayerId,
            lateralMeters,
            carrierPressure,
            openness,
            matchup,
            feasibility,
            turnoverSafety,
            Confidence01: 0f,
            Preference: 0f);

        if (!IsEligible(obs, lateralMeters, openness, carrierNearestOpponent, feasibility, ballXZ, pitchHalfLength))
            return opportunity;

        float confidence = Math.Clamp(
            WeightCarrierPressure * carrierPressure
            + WeightFarSideOpenness * openness
            + WeightMatchup * matchup
            + WeightFeasibility * feasibility
            + WeightTurnoverSafety * turnoverSafety,
            0f, 1f);
        return opportunity with
        {
            Confidence01 = confidence,
            Preference = MaxPreference * confidence
        };
    }

    /// <summary>
    /// The cheap half of the recognition, used by the pass-target pre-rank: everything
    /// <see cref="Evaluate"/> needs except the planned ball's completion probability, which does not exist until
    /// a trajectory has been planned. Keeping the gates in one place is what stops "the receiver the pre-rank
    /// rescued" and "the receiver the price recognized" from being two different receivers.
    /// </summary>
    public static bool IsSwitchGeometry(
        in FootballerObservation obs,
        Vector2 receiverXZ,
        Vector2 ballXZ,
        float pitchHalfLength = 52.5f)
    {
        float lateralMeters = MathF.Abs(receiverXZ.Y - ballXZ.Y);
        float openness = CandidateGenerator.EvaluateSpaceFreedom(receiverXZ, obs.VisiblePlayers, obs.SelfTeam);
        Vector2 carrierXZ = new(obs.SelfPosition.X, obs.SelfPosition.Z);
        float carrierNearestOpponent = NearestOpponentDistance(obs, carrierXZ);
        return IsEligible(
            obs, lateralMeters, openness, carrierNearestOpponent, completionProbability01: 1f,
            ballXZ, pitchHalfLength);
    }

    /// <summary>
    /// The shared gates. Each one is a fact the plan names, and each one refuses the recognition outright
    /// rather than lowering it: a covered far side, an unpressed carrier, a ball the model says cannot be
    /// completed and a side with no rest defence are not weaker opportunities, they are not opportunities.
    /// </summary>
    private static bool IsEligible(
        in FootballerObservation obs,
        float lateralMeters,
        float openness01,
        float carrierNearestOpponentMeters,
        float completionProbability01,
        Vector2 ballXZ,
        float pitchHalfLength)
    {
        // A keeper's distribution is the keeper's own decision (A6.5b); the recognizer does not re-price it.
        if (obs.SelfRole == TacticalRole.Goalkeeper) return false;

        // ai65: a central carrier clean through 1v1 on goal must not be pulled out to the touchline by a
        // wide-switch bonus triggered by trailing chasers.
        if (IsCentralOneOnOneTowardGoal(obs, ballXZ, pitchHalfLength)) return false;

        // Turnover cost: without a rest-defence picture the carrier cannot say what losing the ball costs,
        // and an unknown must not read as "safe". Below the allocator's own commitment threshold it is short.
        if (!(obs.TeamSecurity.Security01 > 0f)) return false;
        if (obs.TeamSecurity.Security01 < MinSecurityForSwitch01) return false;

        // Far side: the same line A7.2's switch call uses.
        if (lateralMeters < TeamCallPlanner.SwitchMinLateralMeters) return false;

        // Deep enough room to receive and turn: A7.2's switch-space threshold on the one space scale.
        if (openness01 < MinFarSideOpenness01) return false;

        // Local pressure: a switch is the release from an overloaded near side; without it there is nothing
        // to release. The threshold is the call's own "the carrier needs a way out" distance.
        if (carrierNearestOpponentMeters > TeamCallPlanner.SwitchCarrierPressureMeters) return false;

        // The ball has to be one the pass model can actually complete.
        if (completionProbability01 < MinCompletionProbability01) return false;

        return true;
    }

    private static bool IsCentralOneOnOneTowardGoal(
        in FootballerObservation obs,
        Vector2 ballXZ,
        float pitchHalfLength)
    {
        float ballProj = ballXZ.X * obs.AttackingDirectionX;
        if (ballProj < 12f || MathF.Abs(ballXZ.Y) > 14f) return false;

        Vector2 oppGoalXZ = new(obs.AttackingDirectionX * pitchHalfLength, 0f);
        if (CandidateGenerator.EvaluateLaneClearance(
                ballXZ, oppGoalXZ, obs.VisiblePlayers, obs.SelfTeam, excludeGoalkeeper: true) < 0.75f)
        {
            return false;
        }

        float corridorHalfWidth = MathF.Max(8.0f, MathF.Abs(ballXZ.Y) + 3.0f);
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam || entity.Role == TacticalRole.Goalkeeper) continue;
            float oppProj = entity.EstimatedPosition.X * obs.AttackingDirectionX;
            if (oppProj > ballProj + 1.0f && MathF.Abs(entity.EstimatedPosition.Z) <= corridorHalfWidth)
                return false;
        }

        return true;
    }

    /// <summary>
    /// How pressed the carrier is, 0..1, and the nearest believed opponent's distance in metres. The radius is
    /// wider than the gate on purpose: the gate is "there is real pressure", the term is "how much".
    /// </summary>
    private static float CarrierPressure01(in FootballerObservation obs, Vector2 carrierXZ, out float nearestMeters)
    {
        nearestMeters = NearestOpponentDistance(obs, carrierXZ);
        float pressure = 1f - nearestMeters / PressureRadiusMeters;
        return Math.Clamp(pressure, 0f, 1f);
    }

    /// <summary>
    /// The receiver's duel quality: an isolated one-v-one scores highest, a doubled-up man lowest, a completely
    /// free man in between. The scouted pace band tilts the duel: a quick receiver against a slower marker is
    /// the isolation worth creating.
    /// </summary>
    private static float Matchup01(
        in FootballerObservation obs,
        in PerceivedEntity receiver,
        Vector2 receiverXZ)
    {
        float nearestMarkerMeters = float.MaxValue;
        float secondNearest = float.MaxValue;
        PerceivedEntity marker = default;

        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam) continue;
            float distance = Vector2.Distance(
                receiverXZ, new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z));
            if (distance < nearestMarkerMeters)
            {
                secondNearest = nearestMarkerMeters;
                nearestMarkerMeters = distance;
                marker = entity;
            }
            else if (distance < secondNearest)
            {
                secondNearest = distance;
            }
        }

        // Nobody at all: the openness term already says the receiver is open, and there is no duel to win.
        if (nearestMarkerMeters > FreeReceiverMeters) return FreeReceiverWithoutDuel01;

        bool doubled = secondNearest < CoverDistanceMeters;
        float isolation = doubled ? DoubledUpMatchup01 : IsolatedMatchup01;
        float paceEdge = 0.5f;
        float receiverSpeed = receiver.Capability.MaxSprintSpeed;
        float markerSpeed = marker.Capability.MaxSprintSpeed;
        if (marker.PlayerId > 0 && float.IsFinite(receiverSpeed) && float.IsFinite(markerSpeed))
        {
            paceEdge = Math.Clamp(
                0.5f + (receiverSpeed - markerSpeed) / PaceEdgeSpanMetersPerSecond, 0f, 1f);
        }

        // Isolation is the opportunity; the pace edge is a 30 % modifier on it, so a slow receiver against a
        // quick marker is still a one-v-one but a worse one.
        return Math.Clamp(isolation * (0.70f + 0.30f * paceEdge), 0f, 1f);
    }

    /// <summary>Matchup of a receiver with no believed marker: open, but not an isolation to exploit.</summary>
    public const float FreeReceiverWithoutDuel01 = 0.75f;

    /// <summary>Matchup of a receiver with one marker and no cover: the overload-to-isolate target.</summary>
    public const float IsolatedMatchup01 = 1.0f;

    /// <summary>Matchup of a receiver with two defenders: the side has already been doubled up.</summary>
    public const float DoubledUpMatchup01 = 0.25f;

    /// <summary>Nearest believed opponent to a point, or a large number when none is in the picture.</summary>
    private static float NearestOpponentDistance(in FootballerObservation obs, Vector2 pointXZ)
    {
        float nearest = float.MaxValue;
        for (int index = 0; index < obs.VisiblePlayers.Count; index++)
        {
            PerceivedEntity entity = obs.VisiblePlayers[index];
            if (entity.Team == obs.SelfTeam) continue;
            float distance = Vector2.Distance(
                pointXZ, new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z));
            if (distance < nearest) nearest = distance;
        }

        return nearest;
    }
}
