using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// One shout a player has decided to make, before the board attaches its timestamps (A7.2). The pure half
/// of the planner is expressed as a draft so a test can ask "what would this player shout, given exactly
/// this observation" without constructing a board or a clock.
/// </summary>
public readonly record struct TeamCallDraft(
    TeamCallKind Kind,
    int TargetPlayerId,
    int NamedPlayerId,
    float Strength01)
{
    /// <summary>Compact text form for a failure message.</summary>
    public string Describe() =>
        $"{Kind} →#{TargetPlayerId}" + (NamedPlayerId > 0 ? $" names #{NamedPlayerId}" : string.Empty) +
        $" strength {Strength01:F2}";
}

/// <summary>
/// A7.2's call producer: <b>one player decides what, if anything, to shout at his own team, from his own
/// observation only.</b>
///
/// Five rules govern every call here, and they are the milestone's contract:
/// <list type="number">
/// <item><b>One observation.</b> Every input is a field of the caller's <see cref="FootballerObservation"/>:
/// his teammates (fully observed), his own sightings of opponents (which may be aged predictions), and
/// his own team instructions. The planner never receives the authoritative player list. A call therefore
/// cannot reveal an opponent the caller could not see — a test pins that with two worlds whose only
/// difference is a hidden opponent's position, producing identical calls.</item>
/// <item><b>No world state in the payload.</b> A <see cref="TeamCall"/> has no position, velocity or intent
/// field to carry, so even a caller who sees something perfectly can only say which instruction he is
/// giving. The receiver's perception is unchanged; only his preference (or his belief that somebody is
/// behind the press) moves.</item>
/// <item><b>Delayed and expiring.</b> The board applies the communication delay and the lifetime; a call
/// is heard after the delay and forgotten at its expiry. There is no "always on" shout.</item>
/// <item><b>Bounded.</b> A player makes at most one shout per tick (the priority ladder), a repeated shout
/// is suppressed while the first is live, and the board is a fixed table.</item>
/// <item><b>Team-scoped.</b> The board is per team; nothing an opponent says is readable.</item>
/// </list>
///
/// The call set is the plan's: man-on, turn, switch and run are carrier/receiver preferences; cover is the
/// second defender telling the presser he is behind him; step and drop are the one line caller asking his
/// own line up or back. Every number is a stated, documented constant because the whole call layer is
/// hand-set calibration (A9.2's ablations are where it gets tested).
/// </summary>
public static class TeamCallPlanner
{
    // ---------------------------------------------------------------------------------------------
    // Shared gates. A shout is only evidence if the caller recently saw the thing he is shouting about.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Oldest sighting a call may be based on, in ticks (0.2 s).</summary>
    public const int FreshSightingTicks = 12;

    /// <summary>Weakest sighting confidence a call may be based on: a barely-seen dot is not a "man on".</summary>
    public const float MinSightingConfidence01 = 0.5f;

    /// <summary>
    /// Furthest a caller may be from the ball carrier and still shout about him, in metres. An instruction
    /// from across the pitch is noise: the caller cannot see the detail he is claiming.
    /// </summary>
    public const float CallSightRangeMeters = 30f;

    // ---------------------------------------------------------------------------------------------
    // Man-on: "there is an opponent on you".
    // ---------------------------------------------------------------------------------------------

    /// <summary>How close an opponent must be to the carrier for the caller to shout "man on", in metres.</summary>
    public const float ManOnRadiusMeters = 4.0f;

    /// <summary>How long a man-on shout stays current, in ticks (0.45 s).</summary>
    public const int ManOnLifetimeTicks = 27;

    // ---------------------------------------------------------------------------------------------
    // Turn: "you have time to turn".
    // ---------------------------------------------------------------------------------------------

    /// <summary>No believed opponent inside this radius of the carrier is what makes "turn" true, in metres.</summary>
    public const float TurnFreeRadiusMeters = 5.5f;

    /// <summary>How long a turn shout stays current, in ticks (0.50 s).</summary>
    public const int TurnLifetimeTicks = 30;

    // ---------------------------------------------------------------------------------------------
    // Switch: a far-side unmarked team-mate asks for the ball.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Lateral distance from the ball at which a team-mate is far-side enough to ask for a switch, in metres.</summary>
    public const float SwitchMinLateralMeters = 15f;

    /// <summary>Space around the caller that makes him a real switch option, in metres.</summary>
    public const float SwitchSpaceMeters = 5.0f;

    /// <summary>Furthest a switch target may be from the carrier and still be a playable request, in metres.</summary>
    public const float SwitchMaxRangeMeters = 34f;

    /// <summary>How close an opponent must be to the carrier before "switch it" is worth shouting, in metres.</summary>
    public const float SwitchCarrierPressureMeters = 5.0f;

    /// <summary>How long a switch request stays current, in ticks (0.60 s).</summary>
    public const int SwitchLifetimeTicks = 36;

    // ---------------------------------------------------------------------------------------------
    // Run: the carrier asks a team-mate to go.
    // ---------------------------------------------------------------------------------------------

    /// <summary>How far ahead of the carrier a team-mate must already stand to be asked to run, in metres.</summary>
    public const float RunCallMinAheadMeters = 5f;

    /// <summary>Space around the runner that makes the instruction worth making, in metres.</summary>
    public const float RunCallSpaceMeters = 5f;

    /// <summary>Furthest a runner may be from the carrier and still be asked to run, in metres.</summary>
    public const float RunCallRangeMeters = 30f;

    /// <summary>How long a run instruction stays current, in ticks (1.0 s).</summary>
    public const int RunLifetimeTicks = 60;

    // ---------------------------------------------------------------------------------------------
    // Cover: the elected second defender tells the presser he is behind him.
    // ---------------------------------------------------------------------------------------------

    /// <summary>How far from the carrier the second man may be to claim cover, in metres.</summary>
    public const float CoverCallRangeMeters = 20f;

    /// <summary>How long a cover call stays current, in ticks (0.50 s).</summary>
    public const int CoverLifetimeTicks = 30;

    // ---------------------------------------------------------------------------------------------
    // Step / drop: the line's one instruction.
    // ---------------------------------------------------------------------------------------------

    /// <summary>How near the carrier the line caller must be to judge whether he is advancing, in metres.</summary>
    public const float StepSightMeters = 25f;

    /// <summary>
    /// An opponent moving toward our goal faster than this stops "step": the line must not walk into a
    /// carrier who is driving at it, in metres per second (positive along our attacking direction).
    /// </summary>
    public const float StepRetreatSpeedMeters = -0.5f;

    /// <summary>How near an opponent running in behind must be for "drop", in metres.</summary>
    public const float DropSightMeters = 35f;

    /// <summary>Speed at which an opponent counts as running in behind, in metres per second.</summary>
    public const float DropRunSpeedMeters = 1.5f;

    /// <summary>How far goal-side of the caller an opponent must be to be "in behind", in metres.</summary>
    public const float DropAheadMeters = 2f;

    /// <summary>Oldest sighting of a runner that still justifies "drop", in ticks (0.5 s).</summary>
    public const int DropSightingTicks = 30;

    /// <summary>How long a step or drop instruction stays current, in ticks (0.60 s).</summary>
    public const int LineDirectiveLifetimeTicks = 36;

    /// <summary>
    /// How far a step call may move the defensive line up, in metres. Bounded here and again inside
    /// <see cref="DefensiveLineModel"/>: a call is a preference, and every existing cap (the ball cap, a
    /// credible runner, the keeper's coverage) still binds over it.
    /// </summary>
    public const float StepLineShiftMeters = 1.5f;

    /// <summary>How far a drop call may move the defensive line back, in metres.</summary>
    public const float DropLineShiftMeters = -2.0f;

    // ---------------------------------------------------------------------------------------------
    // The producer.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How long a call of <paramref name="kind"/> stays current after it is heard.
    /// </summary>
    public static int LifetimeTicks(TeamCallKind kind) => kind switch
    {
        TeamCallKind.ManOn => ManOnLifetimeTicks,
        TeamCallKind.Turn => TurnLifetimeTicks,
        TeamCallKind.Switch => SwitchLifetimeTicks,
        TeamCallKind.Run => RunLifetimeTicks,
        TeamCallKind.Cover => CoverLifetimeTicks,
        TeamCallKind.Step or TeamCallKind.Drop => LineDirectiveLifetimeTicks,
        _ => 0
    };

    /// <summary>
    /// The line band shift a team-wide call asks for, in metres (positive = step up). Unknown or
    /// non-defensive kinds are zero.
    /// </summary>
    public static float LineShiftMeters(TeamCallKind kind) => kind switch
    {
        TeamCallKind.Step => StepLineShiftMeters,
        TeamCallKind.Drop => DropLineShiftMeters,
        _ => 0f
    };

    /// <summary>
    /// Lets one player speak, if anything in his picture is worth saying. Called by the engine once per
    /// active AI player per decision tick; publishes at most one call.
    /// </summary>
    public static void Publish(in FootballerObservation obs, TeamCallBoard? board, AiRuntimeConfig? ai)
    {
        if (board is null || ai is null || !ai.EnableTeamCalls) return;

        // A call's delay and expiry are ticks. A controller built without a match (tick 0) has no clock
        // to delay against, so it does not shout at all rather than making calls that cannot expire.
        if (obs.Tick <= 0) return;

        if (!TryBuild(obs, out TeamCallDraft draft)) return;

        board.Publish(
            obs.SelfTeam, draft.Kind, obs.SelfId, draft.TargetPlayerId, draft.NamedPlayerId,
            draft.Strength01, obs.Tick, ai.TeamCallCommunicationDelayTicks, LifetimeTicks(draft.Kind));
    }

    /// <summary>
    /// The priority ladder. A player says one thing per tick, and the most urgent thing he can see wins:
    /// man-on outranks turn (pressure before space), switch outranks turn (a release is better than a
    /// turn under pressure), and drop outranks step (never step up into a ball played in behind).
    /// </summary>
    public static bool TryBuild(in FootballerObservation obs, out TeamCallDraft draft)
    {
        if (TryBuildManOn(obs, out draft)) return true;
        if (TryBuildManOnReceiver(obs, out draft)) return true;
        if (TryBuildTurn(obs, out draft)) return true;
        if (TryBuildSwitch(obs, out draft)) return true;
        if (TryBuildRun(obs, out draft)) return true;
        if (TryBuildCover(obs, out draft)) return true;
        if (TryBuildDrop(obs, out draft)) return true;
        if (TryBuildStep(obs, out draft)) return true;

        draft = default;
        return false;
    }

    /// <summary>
    /// "Man on" for a pass in flight: the caller sees an opponent closing on the team-mate who is the
    /// intended receiver, so he warns that team-mate. A7.3's reception planner is the consumer — the
    /// receiver cannot see the man arriving behind him while he faces the ball, and the plan's shield /
    /// escape touches need the belief the shout provides.
    ///
    /// The shout is the same kind as the carrier's man-on because it is the same instruction: "somebody
    /// is on you". It names the receiver as its target and carries no position, so it still cannot
    /// become a sighting. A caller must see both the receiver and the fresh opponent from his own
    /// picture (<see cref="CallSightRangeMeters"/> bounds how far away that judgement may be made).
    /// </summary>
    public static bool TryBuildManOnReceiver(in FootballerObservation obs, out TeamCallDraft draft)
    {
        draft = default;
        if (obs.BallControlState != BallControlState.InFlightPass) return false;
        if (obs.PossessingTeam != obs.SelfTeam) return false;
        if (obs.IntendedReceiverId is not int receiverId || receiverId == obs.SelfId) return false;
        if (!TryFindEntity(obs, receiverId, out PerceivedEntity receiver)) return false;
        if (receiver.Team != obs.SelfTeam) return false;
        if (DistanceXZ(obs.SelfPosition, receiver.EstimatedPosition) > CallSightRangeMeters) return false;

        float nearest = float.MaxValue;
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity opponent = obs.VisiblePlayers[i];
            if (opponent.Team == obs.SelfTeam || !IsFresh(opponent)) continue;

            float distance = DistanceXZ(opponent.EstimatedPosition, receiver.EstimatedPosition);
            if (distance > ManOnRadiusMeters || distance >= nearest) continue;
            nearest = distance;
        }

        if (nearest == float.MaxValue) return false;

        // The closer the man, the more urgent the warning — same scale as the carrier's man-on.
        float urgency = 1f - nearest / ManOnRadiusMeters;
        draft = new TeamCallDraft(TeamCallKind.ManOn, receiverId, 0, Clamp01(0.55f + 0.45f * urgency));
        return true;
    }

    /// <summary>
    /// "Man on": the caller sees an opponent within <see cref="ManOnRadiusMeters"/> of a team-mate who has
    /// the ball. The call says only that somebody is on him; it carries no position, so the carrier still
    /// has to decide what to do about it with his own picture.
    /// </summary>
    public static bool TryBuildManOn(in FootballerObservation obs, out TeamCallDraft draft)
    {
        draft = default;
        if (!TryGetOwnTeamCarrier(obs, out PerceivedEntity carrier)) return false;
        if (DistanceXZ(obs.SelfPosition, carrier.EstimatedPosition) > CallSightRangeMeters) return false;

        float nearest = float.MaxValue;
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity opponent = obs.VisiblePlayers[i];
            if (opponent.Team == obs.SelfTeam || !IsFresh(opponent)) continue;

            float distance = DistanceXZ(opponent.EstimatedPosition, carrier.EstimatedPosition);
            if (distance > ManOnRadiusMeters || distance >= nearest) continue;
            nearest = distance;
        }

        if (nearest == float.MaxValue) return false;

        // The closer the man, the more urgent the shout.
        float urgency = 1f - nearest / ManOnRadiusMeters;
        draft = new TeamCallDraft(TeamCallKind.ManOn, carrier.PlayerId, 0, Clamp01(0.55f + 0.45f * urgency));
        return true;
    }

    /// <summary>
    /// "Turn": the caller sees the carrier with no believed opponent inside
    /// <see cref="TurnFreeRadiusMeters"/>, so there is time to turn. A stale belief still counts as an
    /// opponent — the caller does not get to claim space on the strength of a memory he distrusts.
    /// </summary>
    public static bool TryBuildTurn(in FootballerObservation obs, out TeamCallDraft draft)
    {
        draft = default;
        if (!TryGetOwnTeamCarrier(obs, out PerceivedEntity carrier)) return false;
        if (DistanceXZ(obs.SelfPosition, carrier.EstimatedPosition) > CallSightRangeMeters) return false;

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity opponent = obs.VisiblePlayers[i];
            if (opponent.Team == obs.SelfTeam) continue;
            if (DistanceXZ(opponent.EstimatedPosition, carrier.EstimatedPosition) <= TurnFreeRadiusMeters)
                return false;
        }

        draft = new TeamCallDraft(TeamCallKind.Turn, carrier.PlayerId, 0, 0.60f);
        return true;
    }

    /// <summary>
    /// "Switch": a far-side, unmarked team-mate asks the carrier to find him. The caller must be a real
    /// option (nobody on him) and the carrier must be pressured — otherwise "switch it" is just noise.
    /// The named receiver is the caller himself; the fact that he is a team-mate is not hidden world state.
    /// </summary>
    public static bool TryBuildSwitch(in FootballerObservation obs, out TeamCallDraft draft)
    {
        draft = default;
        if (obs.SelfRole == TacticalRole.Goalkeeper) return false;
        if (!TryGetOwnTeamCarrier(obs, out PerceivedEntity carrier)) return false;

        float carrierDistance = DistanceXZ(obs.SelfPosition, carrier.EstimatedPosition);
        if (carrierDistance < SwitchMinLateralMeters || carrierDistance > SwitchMaxRangeMeters) return false;

        // Far-side: the caller is a long way laterally from the ball, so the pass to him is a switch.
        if (MathF.Abs(obs.SelfPosition.Z - obs.PerceivedBallPosition.Z) < SwitchMinLateralMeters) return false;

        // A real option: nobody is on the caller.
        float openness = NearestOpponentDistance(obs, obs.SelfPosition);
        if (openness <= SwitchSpaceMeters) return false;

        // Worth shouting only while the carrier is under enough pressure to need the release.
        float carrierPressure = NearestOpponentDistance(obs, carrier.EstimatedPosition);
        if (carrierPressure > SwitchCarrierPressureMeters) return false;

        float strength = Clamp01((openness - SwitchSpaceMeters) / 10f + 0.4f);
        draft = new TeamCallDraft(TeamCallKind.Switch, carrier.PlayerId, obs.SelfId, strength);
        return true;
    }

    /// <summary>
    /// "Run": the carrier asks the most advanced team-mate with space ahead of him to go. The call is a
    /// request, not a claim (A5.3's run board stays the only place a run is promised): it raises the value
    /// of the runner's own run candidates if he has any.
    /// </summary>
    public static bool TryBuildRun(in FootballerObservation obs, out TeamCallDraft draft)
    {
        draft = default;
        if (obs.BallControllingPlayerId != obs.SelfId) return false;
        if (obs.PossessingTeam != obs.SelfTeam) return false;
        if (obs.BallControlState is not (BallControlState.Controlled or BallControlState.KeeperHeld)) return false;

        int bestId = 0;
        float bestAhead = float.MinValue;
        float bestSpace = 0f;

        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[i];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;
            if (mate.Role == TacticalRole.Goalkeeper) continue;

            float distance = DistanceXZ(obs.SelfPosition, mate.EstimatedPosition);
            if (distance > RunCallRangeMeters) continue;

            float ahead = (mate.EstimatedPosition.X - obs.SelfPosition.X) * obs.AttackingDirectionX;
            if (ahead < RunCallMinAheadMeters) continue;

            float space = NearestOpponentDistance(obs, mate.EstimatedPosition);
            if (space < RunCallSpaceMeters) continue;

            if (ahead <= bestAhead) continue;
            bestId = mate.PlayerId;
            bestAhead = ahead;
            bestSpace = space;
        }

        if (bestId == 0) return false;

        // A team-mate 20 m ahead in 15 m of space is a stronger instruction than one 6 m ahead in 5 m.
        float strength = Clamp01(0.40f + 0.020f * bestAhead + 0.010f * MathF.Min(bestSpace, 20f));
        draft = new TeamCallDraft(TeamCallKind.Run, bestId, 0, strength);
        return true;
    }

    /// <summary>
    /// "Cover": the match-elected second defender tells the presser he is behind him and goal-side. The
    /// shout is about the caller's own position, which the presser cannot be certain of in the moment;
    /// the consumer is <see cref="PressPolicy"/>, which already asks the same question geometrically.
    /// </summary>
    public static bool TryBuildCover(in FootballerObservation obs, out TeamCallDraft draft)
    {
        draft = default;
        if (obs.BallControlState is not BallControlState.Controlled) return false;
        if (obs.PossessingTeam == obs.SelfTeam) return false;
        if (obs.SupportPresserPlayerId != obs.SelfId) return false;

        int presserId = obs.BallResponderPlayerId ?? 0;
        if (presserId <= 0 || presserId == obs.SelfId) return false;
        if (obs.BallControllingPlayerId is not int carrierId) return false;
        if (!TryFindEntity(obs, carrierId, out PerceivedEntity carrier)) return false;
        if (carrier.Team == obs.SelfTeam) return false;
        if (!IsFresh(carrier)) return false;

        float distance = DistanceXZ(obs.SelfPosition, carrier.EstimatedPosition);
        if (distance > CoverCallRangeMeters) return false;

        // Goal-side of the carrier: "behind the press" is the one claim the shout makes.
        float goalSideDot = (obs.SelfPosition.X - carrier.EstimatedPosition.X) * obs.AttackingDirectionX;
        if (goalSideDot >= 0f) return false;

        float strength = Clamp01(1f - distance / (CoverCallRangeMeters + 1f));
        draft = new TeamCallDraft(TeamCallKind.Cover, presserId, 0, strength);
        return true;
    }

    /// <summary>
    /// "Drop": the line caller sees an opponent goal-side of him running at our goal and asks the line
    /// back. Only the one designated line caller (the line player nearest the ball in his own picture) may
    /// shout, so the line hears one voice instead of four contradictory ones.
    /// </summary>
    public static bool TryBuildDrop(in FootballerObservation obs, out TeamCallDraft draft)
    {
        draft = default;
        if (obs.PossessingTeam == obs.SelfTeam) return false;
        if (!IsDesignatedLineCaller(obs)) return false;

        float nearest = float.MaxValue;
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity opponent = obs.VisiblePlayers[i];
            if (opponent.Team == obs.SelfTeam) continue;
            if (opponent.ObservedAgeTicks > DropSightingTicks) continue;
            if (opponent.Confidence01 < MinSightingConfidence01) continue;

            float distance = DistanceXZ(obs.SelfPosition, opponent.EstimatedPosition);
            if (distance > DropSightMeters) continue;

            // Behind the line and running at our goal: both are needed before the line is asked to drop.
            float behindMeters = (obs.SelfPosition.X - opponent.EstimatedPosition.X) * obs.AttackingDirectionX;
            if (behindMeters < DropAheadMeters) continue;
            float atGoalSpeed = -opponent.EstimatedVelocity.X * obs.AttackingDirectionX;
            if (atGoalSpeed < DropRunSpeedMeters) continue;

            if (distance >= nearest) continue;
            nearest = distance;
        }

        if (nearest == float.MaxValue) return false;

        float strength = Clamp01(0.70f + 0.30f * (1f - nearest / DropSightMeters));
        draft = new TeamCallDraft(TeamCallKind.Drop, 0, 0, strength);
        return true;
    }

    /// <summary>
    /// "Step": the line caller sees the opponent carrier no longer advancing and asks the line up. This is
    /// the one call whose fact the A6.3 band does not already carry — the band sees where the ball is, not
    /// which way the man on it is moving.
    /// </summary>
    public static bool TryBuildStep(in FootballerObservation obs, out TeamCallDraft draft)
    {
        draft = default;
        if (obs.PossessingTeam == obs.SelfTeam) return false;
        if (!IsDesignatedLineCaller(obs)) return false;
        if (obs.BallControlState is not BallControlState.Controlled) return false;
        if (obs.BallControllingPlayerId is not int carrierId) return false;
        if (!TryFindEntity(obs, carrierId, out PerceivedEntity carrier)) return false;
        if (carrier.Team == obs.SelfTeam || !IsFresh(carrier)) return false;
        if (DistanceXZ(obs.SelfPosition, carrier.EstimatedPosition) > StepSightMeters) return false;

        // Our attacking direction points at the opponent's goal, so "not advancing at us" is a non-negative
        // projection. A carrier driving at us (negative) keeps the line where it is.
        float advance = carrier.EstimatedVelocity.X * obs.AttackingDirectionX;
        if (advance < StepRetreatSpeedMeters) return false;

        draft = new TeamCallDraft(TeamCallKind.Step, 0, 0, 0.70f);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>True when this player is the line player nearest the ball in his own picture.</summary>
    private static bool IsDesignatedLineCaller(in FootballerObservation obs)
    {
        if (!DefensiveLineModel.HoldsLineSlot(obs.SelfRole)) return false;

        float mine = DistanceXZ(obs.SelfPosition, obs.PerceivedBallPosition);
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity mate = obs.VisiblePlayers[i];
            if (mate.Team != obs.SelfTeam || mate.PlayerId == obs.SelfId) continue;
            if (!DefensiveLineModel.HoldsLineSlot(mate.Role)) continue;

            float theirs = DistanceXZ(mate.EstimatedPosition, obs.PerceivedBallPosition);
            if (theirs < mine - 1e-3f) return false;
            if (MathF.Abs(theirs - mine) <= 1e-3f && mate.PlayerId < obs.SelfId) return false;
        }

        return true;
    }

    /// <summary>The team-mate who has the ball, or false when this player's team does not have control.</summary>
    private static bool TryGetOwnTeamCarrier(in FootballerObservation obs, out PerceivedEntity carrier)
    {
        carrier = default;
        if (obs.PossessingTeam != obs.SelfTeam) return false;
        if (obs.BallControlState is not (BallControlState.Controlled or BallControlState.KeeperHeld)) return false;
        if (obs.BallControllingPlayerId is not int carrierId || carrierId == obs.SelfId) return false;
        return TryFindEntity(obs, carrierId, out carrier) && carrier.Team == obs.SelfTeam;
    }

    private static bool TryFindEntity(in FootballerObservation obs, int playerId, out PerceivedEntity entity)
    {
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            if (obs.VisiblePlayers[i].PlayerId != playerId) continue;
            entity = obs.VisiblePlayers[i];
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>A sighting fresh and confident enough to base an instruction on.</summary>
    private static bool IsFresh(in PerceivedEntity entity) =>
        entity.ObservedAgeTicks <= FreshSightingTicks && entity.Confidence01 >= MinSightingConfidence01;

    private static float NearestOpponentDistance(in FootballerObservation obs, Vector3 point)
    {
        float nearest = float.MaxValue;
        for (int i = 0; i < obs.VisiblePlayers.Count; i++)
        {
            PerceivedEntity opponent = obs.VisiblePlayers[i];
            if (opponent.Team == obs.SelfTeam) continue;
            float distance = DistanceXZ(opponent.EstimatedPosition, point);
            if (distance < nearest) nearest = distance;
        }

        return nearest;
    }

    private static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
}
