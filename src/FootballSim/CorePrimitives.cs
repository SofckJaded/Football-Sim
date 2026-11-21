using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Tactics;

namespace FootballSim.Core;

public static class SimConstants
{
    public const float FixedDeltaTime = 1.0f / 60.0f;
    public const int PhysicsSubsteps = 2;
    public const float Gravity = 9.81f;
}

/// <summary>
/// Small numeric helpers shared by controllers, locomotion and physics.
/// Audit F09: bare <see cref="Vector2.Normalize(Vector2)"/> / <see cref="Vector3.Normalize(Vector3)"/>
/// emit NaN for a zero-length vector, and nothing in the pipeline rejected non-finite components.
/// </summary>
public static class SimMath
{
    private const float NormalizeEpsilonSquared = 1e-12f;

    public static bool IsFinite(in Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    public static bool IsFinite(in Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Normalizes <paramref name="v"/>, returning <paramref name="fallback"/> for
    /// zero-length, near-zero or non-finite input instead of producing NaN.</summary>
    public static Vector2 SafeNormalize(in Vector2 v, in Vector2 fallback)
    {
        float lenSq = v.LengthSquared();
        if (!float.IsFinite(lenSq) || lenSq <= NormalizeEpsilonSquared) return fallback;
        Vector2 result = v * (1f / MathF.Sqrt(lenSq));
        return IsFinite(result) ? result : fallback;
    }

    /// <summary>3D counterpart of <see cref="SafeNormalize(in Vector2, in Vector2)"/>.</summary>
    public static Vector3 SafeNormalize(in Vector3 v, in Vector3 fallback)
    {
        float lenSq = v.LengthSquared();
        if (!float.IsFinite(lenSq) || lenSq <= NormalizeEpsilonSquared) return fallback;
        Vector3 result = v * (1f / MathF.Sqrt(lenSq));
        return IsFinite(result) ? result : fallback;
    }
}

public sealed class PitchDimensions
{
    public float Length { get; init; } = 105.0f;
    public float Width { get; init; } = 68.0f;
    public float GoalWidth { get; init; } = 7.32f;
    public float GoalHeight { get; init; } = 2.44f;
    public float GoalDepth { get; init; } = 2.0f;
    public float GoalPostRadius { get; init; } = 0.06f;
    public float PenaltyAreaLength { get; init; } = 16.5f;
    public float PenaltyAreaWidth { get; init; } = 40.32f;
    public float GoalAreaLength { get; init; } = 5.5f;
    public float GoalAreaWidth { get; init; } = 18.32f;
    public float PenaltySpotDistance { get; init; } = 11.0f;
    public float CenterCircleRadius { get; init; } = 9.15f;

    public float HalfLength => Length * 0.5f;
    public float HalfWidth => Width * 0.5f;
    public float HalfGoalWidth => GoalWidth * 0.5f;
}

public sealed class SeededRandom
{
    private ulong _state;

    public SeededRandom(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public ulong State => _state;

    public uint NextUInt()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return (uint)((x * 0x2545F4914F6CDD1DUL) >> 32);
    }

    public float NextFloat() => (NextUInt() & 0x00FFFFFF) / (float)0x01000000;

    public float NextRange(float min, float max) => min + (max - min) * NextFloat();

    public float NextGaussian(float mean = 0f, float stdDev = 1f)
    {
        float u1 = MathF.Max(1e-6f, NextFloat());
        float u2 = NextFloat();
        float mag = MathF.Sqrt(-2.0f * MathF.Log(u1));
        float z0 = mag * MathF.Cos(2.0f * MathF.PI * u2);
        return mean + z0 * stdDev;
    }
}

/// <summary>
/// Independent random consumers in one match. One shared <see cref="SeededRandom"/> used to mean that
/// adding a perception query (or a diagnostic probe that observed the ball) shifted every subsequent
/// physics sample: two features could never be developed without invalidating each other's replays.
/// </summary>
public enum RandomStream
{
    /// <summary>
    /// Physical execution: contact arbitration, first touch, tackles, keeper saves. This is the stream a
    /// command-only replay must reproduce, so nothing else may draw from it.
    /// </summary>
    Execution = 0,

    /// <summary>Perception: observation noise, later scanning uncertainty and attention jitter.</summary>
    Perception = 1,

    /// <summary>
    /// Decision uncertainty: reservation or intent-conversion noise. Reserved now so that the derived
    /// seeds of the existing streams never have to move when it starts being used.
    /// </summary>
    Decision = 2
}

/// <summary>
/// Derives a match-local random stream from the match seed.
///
/// The mixer is SplitMix64's finalizer over <c>seed + golden-ratio * (ordinate + 1)</c>. It only has to
/// satisfy two properties, and both are tested: the same (seed, ordinate) always yields the same value,
/// and different ordinates yield different streams. It is deliberately not cryptographic — a football
/// match's replay identity is versioned, not secret.
/// </summary>
public static class RandomStreams
{
    /// <summary>Golden-ratio increment used to separate stream ordinates before mixing.</summary>
    private const ulong OrdinateStride = 0x9E3779B97F4A7C15UL;

    /// <summary>Creates the stream for <paramref name="stream"/> under <paramref name="matchSeed"/>.</summary>
    public static SeededRandom Create(ulong matchSeed, RandomStream stream) =>
        new(Derive(matchSeed, stream));

    /// <summary>
    /// The seed <paramref name="stream"/> would start from. Exposed because diagnostics record it, and
    /// because a replay tool has to be able to rebuild one stream without rebuilding the match.
    /// </summary>
    public static ulong Derive(ulong matchSeed, RandomStream stream)
    {
        ulong z = matchSeed + OrdinateStride * ((ulong)stream + 1UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z == 0UL ? OrdinateStride : z;
    }
}

public enum TeamId
{
    Home = 0,
    Away = 1
}

/// <summary>
/// Behavioural version of the simulation. Two builds with different values are not required to produce
/// identical trajectories, so every recorded artifact states it.
///
/// History:
/// <list type="bullet">
/// <item><c>AiBehaviour 1</c> — everything up to and including A0: one shared random stream.</item>
/// <item><c>AiBehaviour 2</c> — A1.3: perception and execution draw from separate derived streams, so
/// trajectories differ from version 1 by construction.</item>
/// <item><c>AiBehaviour 3</c> — A1.1c: commitment is matched on a stable candidate key
/// (<see cref="CandidateKey"/>) instead of the action label. The reference 11v11 match is byte-identical
/// to version 2, so the version bump records a semantic change rather than an observed behaviour change.</item>
/// <item><c>AiBehaviour 4</c> — A1.5/A1.2: commitment, tackle cooldown and the held intent are dropped at
/// every discontinuity (restart, half time, full time, removal, training reset, controller replacement),
/// and a controller-owned intent executor exists behind an off-by-default scheduler. Trajectories differ
/// from version 3 because a commitment no longer survives a restart.</item>
/// <item><c>AiBehaviour 5</c> — A2.1: one reach model (<see cref="AI.TimeToReachModel"/>) replaces the
/// four ad-hoc time-to-reach estimates in the responder election, the ball interception target, the
/// carrier pursuit point and the goalkeeper sweep. It uses current velocity, turn cost, acceleration,
/// fatigue, recovery balance and contact reach, and the responder election now scores candidates
/// against their own meeting with the ball instead of one shared intercept point.</item>
/// <item><c>AiBehaviour 6</c> — A2.2: perception has sight filtering and a persistent, ageing memory.
/// An opponent outside the facing cone is no longer measured every tick; the player keeps a predicted
/// position from the last sighting, with growing uncertainty and decaying confidence. Teammates are
/// still observed every tick (teammate belief is A7.2's team calls).</item>
/// <item><c>AiBehaviour 7</c> — A2.3/A2.4: scorers estimate an opponent's body from a scouted
/// <see cref="CapabilityEstimate"/> band (never the live attributes), and the carrier chooses a carry
/// direction with a local pressure window (arrival-based, directional) blended into its freedom term.</item>
/// <item><c>AiBehaviour 8</c> — A2.5/A2.6: a cell's influence is split into mine / theirs / unclaimed
/// shares with the absolute arrival times kept alongside, and the persistent team-job coordinator reads
/// a team belief (union of the team's observations) instead of the authoritative squad, so an unseen
/// opponent can no longer change a team's structure.</item>
/// <item><c>AiBehaviour 9</c> — A3.1/A3.2: each team's single tactical state is split into possession
/// stability, claim/phase, zone, transition window and armed restart (<see cref="AI.TeamContext"/>), and
/// the single 3.5 s state lock is replaced by a policy per fact — a turnover resolves the phase, the
/// transition and both zones on the tick it happens, while the zone alone keeps a deadband and a
/// structural dwell. An armed restart claims the ball for the restarting team, and a zone is resolved
/// immediately under emergency defence. Trajectories differ from version 8 because the lock no longer
/// delays a counter-attack, the goalkeeper's sweep offset follows the transition window directly and a
/// restart now hands the claim to the team that will take it.</item>
/// <item><c>AiBehaviour 10</c> — A3.3: formation ownership is assigned once, per player id
/// (<see cref="Tactics.TeamFormation"/>), from the pre-match shirt template for a generated squad or
/// from the fixture's own layout for a scenario fragment, and the anchor writer no longer reads a shirt
/// number. A fixture therefore holds a shape of its own size instead of inheriting the eleven-a-side
/// table, and the keeper holds no outfield slot. A defender pulled out of his line by the press now
/// leaves a region a teammate takes as a temporary responsibility (`TacticalJob.ProtectGap`) until
/// handback is safe; a long stationary structural mismatch moves one slot by a bounded step. Trajectories
/// differ from version 9 in both formats, and most visibly in the scenario fixtures.</item>
/// <item><c>AiBehaviour 11</c> — A3.4: a job target is a region (<see cref="Tactics.JobRegion"/>), and
/// it is scored against the ball, the teammates already placed in the assignment pass, the opponents the
/// team believes in and the space around it. Duties are rewarded for width, depth, staggering and
/// complementary channels — softly and relatively, with no lane index — and the chosen region is then
/// refined by a bounded two-ring probe. Kept incumbent regions now count as placed, which is what stops
/// two duties from being handed targets a few centimetres apart. Trajectories differ from version 10.</item>
/// <item><c>AiBehaviour 13</c> — A4.1/A4.2: the carrier's options are an explicit, bounded action set
/// (shot, pass to feet, pass into a run region, raised alternative, recycle/keeper support, carry,
/// take-on, shield, clearance), each with its own <see cref="CandidateKey"/>, bounded by
/// <see cref="AI.AiRuntimeConfig.MaxPassTargets"/> and <see cref="AI.AiRuntimeConfig.MaxCarrierCandidates"/>.
/// A pass is now scored once by <see cref="AI.PassSuccessModel"/> — <c>p·gain − (1−p)·loss</c>, with the
/// loss term owning transition risk and rising in the passer's own third — and the lead given to a moving
/// receiver is bounded, because the unbounded solve was measuring 43 m punts to a team-mate 12 m away.
/// Job-region refinement may no longer slide a duty onto a team-mate's target. Trajectories differ from
/// version 12.</item>
/// <item><c>AiBehaviour 14</c> — A4.3/A4.4: a pass's gain is the value of the state after the reception
/// (<see cref="AI.ReceptionValueModel"/>: progression, turnability, next options, shooting chance, pressure
/// and the profile's structure), every action's constants live on one documented scale
/// (<see cref="AI.ActionValueScale"/>, 1.0 = a clean possession retained in midfield), the blanket
/// final-third backward-pass ban is gone — a recycle is now priced rather than forbidden — and the
/// guaranteed shot utility is a documented curve on that scale. Passes into your own goal mouth are no
/// longer offered and keeper support needs an off-his-line keeper, both measured: removing the ban
/// produced own goals from back-passes. Trajectories differ from version 13.</item>
/// <item><c>AiBehaviour 15</c> — A4.5/A4.6: the flat commitment bonus is joined by the plan's robust switch
/// rule — <c>best − current &gt; absolute + relative × max(|current|, ε)</c> (
/// <see cref="AI.AiRuntimeConfig.SwitchAbsoluteMargin"/>, <see cref="AI.AiRuntimeConfig.SwitchRelativeMargin"/>)
/// — so a player holds what he is doing unless the alternative is clearly better, and a negative current
/// score does not lock him in. A chosen candidate now publishes the four terms its score was built from
/// (<see cref="AI.ValueTerms"/>), refused candidates carry a reason (<see cref="AI.CandidateRejection"/>),
/// and both reach the decision trace and the inspector. Trajectories differ from version 14 because the
/// switch rule changes what players do.</item>
/// <item><c>AiBehaviour 16</c> — A4.4 completes: the shot is scored on the scale by a stated conversion
/// curve (<c>conversion × goal value − (1 − conversion) × miss cost</c>,
/// <see cref="AI.ActionValueScale.ShotConversion"/>) instead of a guaranteed close-range bonus, which is
/// allowed now that the reception value no longer outbids the shot from inside the box and a test pins
/// "a clear chance beats a covered one, and a covered one loses to a good pass". The planner's power search
/// also steps up one notch at a time for a moving receiver instead of jumping to maximum power.
/// Trajectories differ from version 15.</item>
/// <item><c>AiBehaviour 17</c> — the A4 pass audit: the reception's "room to turn" is measured against
/// opponents <b>goal-side</b> of the reception (<see cref="AI.ReceptionValueModel"/>), not behind it —
/// the comparison was inverted, so a marked forward looked free — and a raised pass must clear the same
/// completion floor as a ground one instead of being offered at any price. Trajectories differ from
/// version 16.</item>
/// <item><c>AiBehaviour 18</c> — A4.9: a restart taker plans his delivery with the carrier's own planner
/// (<see cref="AI.CandidateGenerator.TryPlanSetPieceDelivery"/>) — led to where the receiver will be, on
/// the ground whenever the ground route reaches him, and with the raised power matched to the distance
/// instead of a linear ramp. He also prefers a receiver whose lane is open. Measured on a thirty-minute
/// 11v11 segment, throw-in completion went from 29 % to 83 %, and the balls that flew 10–20 m past their
/// receiver disappeared entirely. Trajectories differ from version 17.</item>
/// <item><c>AiBehaviour 19</c> — A4.10: a pass that is aimed ahead of where the receiver is now is
/// charged as a bet on a run nothing has committed to
/// (<see cref="AI.PassSuccessModel.LeadRisk01"/>, <see cref="AI.PassSuccessModel.LeadRiskScaleMeters"/>),
/// so a ball to a player who is already there beats the same ball led twelve metres into space — measured
/// at p 0.97 against 0.81 where the lead used to be free and both scored 0.92. A ball into the space
/// ahead is also no longer offered to a receiver who is not running
/// (<see cref="AI.CandidateGenerator"/> `MinRunIntoSpaceSpeed`), because the through lead then put the
/// ball five metres past a man who was never going anywhere. Trajectories differ from version 18.</item>
/// <item><c>AiBehaviour 20</c> — A4.11: the lead is a <b>bound on where the ball may be played</b>, not a
/// receiver frozen at that bound. The planner used to clamp the runner's modelled position to the lead and
/// then call the meeting feasible, which reported a sprinter as catchable while aiming at a point he had
/// already left; it now solves the real meeting and refuses the pass when the lead would exceed
/// <see cref="Physics.PassPlanner.DefaultMaxLeadMeters"/> (the assisted human pass, the keeper's
/// distribution and a recycle were silently using a 60 m default and punting). The AI's feet pass is
/// bounded at <c>FeetPassMaxLeadMeters</c> = 6 m instead of 12 — a twelve-metre "pass to feet" completed
/// 79 % and looked like a ball to nobody, six metres completes 84 % — and the room a runner needs ahead
/// of him for a ball into space is 3.6 m instead of 5, because refusing the feet ball had removed the
/// final-third option entirely. Trajectories differ from version 19.</item>
/// <item><c>AiBehaviour 21</c> — A4.12: the assisted human pass ranks the receivers in the aim cone by the
/// ball that would actually be played and takes the best one that is <b>playable</b>, preferring a lane at
/// or above <c>MinAssistedLaneClearance01</c>. Before this it picked one receiver from the cone and, if his
/// pass was not plannable, fell back to the raw stick aim at the charge power — which a player experiences
/// as "none of my passes land". The AI reference match is unchanged (it has no locked human), so the
/// recorded gate/match/perf numbers are the `ai20` ones.</item>
/// <item><c>AiBehaviour 22</c> — A5.1/A5.2: an off-ball receiving duty no longer walks to one anchor. The
/// duty's meaning is now a bounded set of <b>run paths</b> (<see cref="AI.RunCandidateGenerator"/>): hold
/// width, check short, in behind, overlap, underlap, recycle support, late and blind-side box arrival,
/// and pin — each with a meeting region, a start window, an optional hold point and an expiry, scored on
/// seven terms (<see cref="AI.RunScoring"/>): arrival advantage, pass availability, receiving
/// orientation, line breaks, congestion at arrival, role usefulness and transition exposure. The path a
/// player chooses is <b>adopted and frozen</b> for its window (<see cref="AI.RunIntent"/>), so a run
/// arrives somewhere instead of chasing the ball, and it ends on expiry, a different choice, a duty
/// change, or a meeting region the defensive line has walked past. Decoys are declared but not generated
/// (their value is the defender's response, A5.6). The <c>high-line-run</c> fixture now puts the away
/// line fourteen metres inside its own half with a real gap behind it, and carries a midfield, because
/// without one the allocator makes the striker the third-player option and he never gets to run in
/// behind. Trajectories differ from version 21.</item>
/// <item><c>AiBehaviour 23</c> — A5.3: an adopted run is <b>announced to the team</b>
/// (<see cref="AI.RunAdvertisementBoard"/>, on the match's existing communication context) with the
/// meeting region, the planned arrival tick and the runner's confidence, visible to team-mates only after
/// a 0.3 s communication delay. The carrier plays into that region: the plan is aimed at it and the power
/// is chosen so the ball arrives when the runner does
/// (<see cref="Physics.PassPlanner.PlanGroundPassToRegion"/>), instead of guessing a lead from his current
/// velocity. The promise is priced by confidence and timing, and the lead charge
/// (<see cref="AI.PassSuccessModel.LeadRisk01"/>) applies only to a ball nobody promised — one risk owner,
/// not two. A carrier that plays the pass raises a claim, which keeps the runner's run alive until the
/// ball should have arrived and releases any timed hold, so a late box arrival is received rather than
/// timed. Trajectories differ from version 22.</item>
/// <item><c>AiBehaviour 24</c> — A5.3 completes: an adopted run's <b>confidence is re-measured every tick</b>
/// and the renewal carries it (the meeting region, its window and the adoption tick stay frozen, so the
/// commitment is intact while the promise stops claiming certainty in a space that has closed), and the
/// pass-target pre-rank adds a documented bonus for a receiver who has announced a run
/// (<c>CandidateGenerator.AnnouncedRunRankBonus</c>) so
/// <see cref="AI.AiRuntimeConfig.MaxPassTargets"/> cannot prune the one option the advertisement exists
/// for. Trajectories differ from version 23.</item>
/// <item><c>AiBehaviour 25</c> — A5.4: the offside line is a <b>tactical budget before release and history
/// after it</b>. One margin (<see cref="AI.OffsideTactics.MarginMeters"/>, 1 m) now governs the pass-target
/// gate, the run region and the job target; the pass gate used 0.35 m, so the run generator and the carrier
/// disagreed about the same receiver and a "pass to feet" could target a man the carrier then refused.
/// Once a team-mate has played a pass with this player as its intended receiver
/// (<see cref="AI.OffsideTactics.IsReleased"/>), the line stops mattering: his adopted run is not cancelled
/// by a line that stepped up after the pass, only the frozen run is offered while the ball is coming, and
/// his job and shape targets are no longer clamped back behind the line. Trajectories differ from
/// version 24.</item>
/// <item><c>AiBehaviour 26</c> — A5.5 (delivery and arrival half): airborne deliveries are predicted by the
/// <b>real trajectory</b>. <see cref="Physics.KickLaunchModel"/> is now the single source for the execution
/// layer's pass-shaped launches, and <see cref="Physics.RaisedPassModel"/> calibrates the lofted, cross and
/// driven flights from those launches through the real solver — landing point, flight time, first bounce —
/// so the raised alternative is priced on where the ball actually comes down instead of a drag-free range
/// shortened by a flat 15 %. A <b>cross</b> is offered when the carrier is wide in the attacking half and a
/// team-mate has announced a box arrival, and it is aimed at that future meet point. Box arrivals are also
/// priced for being <b>complementary</b>: a new run term lowers an arrival that duplicates a team-mate's
/// announced region and time and rewards the player's own finishing/aerial capability. Trajectories differ
/// from version 25.</item>
/// <item><c>AiBehaviour 28</c> — A5.6: the <b>decoy</b> is generated and priced on the defender's answer.
/// <see cref="AI.DefenderResponseModel"/> is a bounded follow-versus-hold estimate built only from the
/// runner's own observation — how close the defender is to the space the run attacks, how dangerous that
/// space is, and whether he is his side's deepest defender (the last line holds more than it steps out) —
/// and the room following frees at the most tightly marked team-mate. A decoy is offered from the
/// <c>FarSideWidth</c> and <c>ThirdPlayerOption</c> duties only, aimed into the marker's own ground, and it
/// is scored on that response rather than on the receiving terms (it keeps the exposure and the distance
/// from the play as its costs). The <b>sinusoidal anchor pushes are gone</b>: the role push in possession
/// no longer oscillates with an elapsed clock, and <c>UpdateTeamAnchors</c> no longer takes a clock at all,
/// so an anchor is a pure function of the picture. Trajectories differ from version 27.</item>
/// <item><c>AiBehaviour 29</c> — A6.1/A6.2: defending is coordinated instead of two men independently
/// running at the carrier. <see cref="AI.PressPolicy"/> turns the old second-defender challenge into
/// <b>pressure-cover-balance</b>: exactly one team-mate may be the second man, he steps in only when somebody
/// is goal-side of the press (or he is himself goal-side, so stepping forward abandons nothing), and with
/// neither he delays or holds instead. The defensive candidate vocabulary is now press, delay, cover, screen,
/// track, intercept, hold and drop; the cover stands where its <b>cover shadow</b> blocks the opponent's most
/// dangerous forward pass rather than at a fixed depth. Pressure is <b>angled</b>: the approach point is
/// scanned around the carrier (inside a touchline carrier, goal-side of a central one, opposite side for the
/// second man), and its depth is the balance (a lone presser jockeys once the press can be bypassed). The
/// urgency itself is read from observable <b>evidence</b> — a back pass, a slow pass, a poor touch, a carrier
/// running backwards, the touchline — moderated by cover and bypass risk. <see cref="FootballerObservation"/>
/// gained the team's <c>BallResponderPlayerId</c> so a second defender knows who is already committing; that
/// is a team fact, not an opponent read. Trajectories differ from version 28.</item>
/// <item><c>AiBehaviour 30</c> — A6.3: the defensive line stands in <b>one shared depth band</b> instead of
/// wherever each defender's own formation anchor happened to put him. <see cref="AI.DefensiveLineModel"/>
/// derives it from four facts the team can observe — the ball's field position, how hard the carrier is being
/// pressed (through the one reach model), the deepest <b>credible run in behind</b> (a believed opponent
/// between the line and the ball, running at this goal, aged and confidence-weighted), and the keeper's own
/// <b>sweep reach</b> over a three-second budget — with a hard ball cap so the line is never well upfield of
/// the ball. <see cref="Tactics.TeamJobCoordinator"/> places every <c>DefensiveLine</c> target on the band,
/// keeping each defender's own lateral lane and allowing only two bounded local exceptions (ball-side steps
/// up, far-side tucks) that may never leave the band's range. Marking responsibility is a <b>style</b> choice
/// (zone/man share of the profile: direct counterattack marks one, possession two, gegenpress three) and it
/// moves by an <b>explicit handover</b> with a recorded reason when the threat crosses into another
/// defender's zone; one threat can never be marked twice. Trajectories differ from version 29.</item>
/// <item><c>AiBehaviour 31</c> — five integration fixes. Line defenders now follow the allocator's band
/// target as normal movement instead of their formation anchor; a moving target is met through an iterative
/// lead (the reach query no longer declares a reachable lateral runner unreachable); a marking handover is
/// still possible when the marking budget is full; the second man is a match-level election published in
/// <see cref="FootballerObservation.SupportPresserPlayerId"/> instead of each defender's noisy independent
/// answer; and a human's call for the pass is filtered to the caller's own team. Trajectories differ from
/// version 30.</item>
/// <item><c>AiBehaviour 32</c> — A6.4: a team <b>answers its own turnover</b> instead of the profile's
/// counter-press window being a window nothing consumed. <see cref="AI.TurnoverResponseModel"/> compares four
/// belief-side inputs — local arrival numbers (ours versus theirs through the one reach model), central
/// security (the A3.6 <see cref="AI.RestDefence"/>, read for the side that just lost the ball), the new
/// carrier's <b>constraint</b> (the share of the exits he can use that one of ours would reach first) and his
/// <b>escape routes</b> (<see cref="AI.RestDefence.OutletExposure01"/>) — and chooses <b>counter-press</b>,
/// <b>delay/recover</b> or <b>retreat</b> for the first six seconds after the loss. The profile's window is
/// still a maximum: the counter-press answer needs it open, and a chosen answer cannot outlive it. The answer
/// is held for half a second and published in <see cref="FootballerObservation.TeamTurnover"/>, where the
/// existing owners of movement consume it: the defensive line's band moves up or down (inside the A6.3 caps),
/// the marking mix gains or loses a man, the presser's standoff becomes contact / jockey / shepherd, and the
/// second man is licensed only when the team chose to press. Trajectories differ from version 31.</item>
/// <item><c>AiBehaviour 33</c> — A6.5: the keeper decides against the ball's <b>real trajectory</b> instead of
/// two straight-line guesses. <see cref="AI.BallTrajectoryEstimate"/> rolls the authorised ball state through the
/// <b>same</b> <see cref="Physics.BallPhysicsSolver"/> the match uses — a sampled copy of the real path, not a
/// second set of equations — and <see cref="AI.GoalkeeperDecisionModel"/> asks it both of the keeper's questions:
/// the <b>sweep</b> is a race between the keeper and the nearest believed opponent to the same point of that
/// path (first, or within a stated margin, commits; clearly beaten declines with a reason), and the <b>dive</b>
/// exists only for a ball that is genuinely going in and only when the interception point is reachable inside the
/// flight time, so an out-of-reach shot is not dived for. A shot that is on target is a shot, not a loose ball:
/// the keeper does not fall through to a sweep when the dive is declined. Trajectories differ from version
/// 32.</item>
/// <item><c>AiBehaviour 34</c> — A6.6: the keeper's <b>handling is gated on the law</b> (Law 12) through one
/// model, <see cref="Rules.GoalkeeperLawModel"/>: hands only inside his own penalty area, never on a ball a
/// team-mate deliberately kicked to him, and never on a ball nobody has touched since his own release. The
/// verdict is the <i>execution's</i> contact reach, height, body-orientation rule and claim priority, so a
/// keeper outside his area plays as an outfielder and a back-pass is played with the feet rather than caught
/// by accident; the keeper's own decision reads the same verdict, so he no longer dives for a shot whose only
/// interception point is outside his area. The ball carries <see cref="Physics.BallState.LastPlay"/> (kind,
/// kick intent, player, team) — a deliberate kick is not a deflection, and the old last-touch-team test could
/// not tell them apart. Trajectories differ from version 33.</item>
/// <item><c>AiBehaviour 35</c> — A6.5b: the keeper claims a delivery at the point it is going to (the law is
/// asked about that point), prices his distribution through the pass chain and stands off his line as a
/// possession outlet; the 1v1 block and the sweep race share the trajectory. Trajectories differ from
/// version 34.</item>
/// <item><c>AiBehaviour 36</c> — audit bugfix batch 40: a throw-in can no longer score directly (the
/// second-touch law is armed before the offside exemption returns, and an intercepted restart is stamped for
/// the intercepting team as open play); the keeper's dive request no longer has a second 2.6 m reach cap
/// below the reach it prices; a re-issued run claim cannot push its deadline out or resurrect itself after
/// the grace window; the possession flight clock resets when the intended receiver changes; a marker elected
/// presser and a handover move their duty counts instead of duplicating them; the defensive-line own-goal
/// floor can no longer put the line upfield of a ball on its own byline; and the penalty spot is validated
/// inside the penalty area. Trajectories differ from version 35.</item>
/// <item><c>AiBehaviour 37</c> — audit bugfix batch 41: the pass lane is the <b>segment</b> between the ball
/// and the receiver, so a chaser behind the passer or a defender past the receiver no longer hard-rejects an
/// open forward ball (A6.B8); the keeper's distribution keeps the same offside margin every other pass keeps,
/// except from a goal kick (A6.B9, latent: the distributions the value scale chooses are short and cannot be
/// offside); and an underlap arrives <b>inside the carrier</b> by up to 12 m, floored 3 m from the centre
/// line, instead of at a fixed |z| = 12 m lane that could be outside him (A6.B10). A6.B11 (a height-aware
/// dive) was implemented, measured as a regression and reverted. Trajectories differ from version 36.</item>
/// <item><c>AiBehaviour 38</c> — audit bugfix batch 42 (M1/M2/M3/L8). The pass-target pre-rank is dropped
/// with every decision, so the early-possession recycle options are no longer filtered by the previous
/// passage's buffer (M1); a lofted ball aimed at an announced run region claims it, like a cross, driven
/// ball or cutback (M2); a coverage request no longer overwrites the elected ball responder's <c>Presser</c>
/// duty, so the side keeps pressing (M3); and a raised restart delivery reports its own lane verdict, so the
/// taker's "open route first" tier ranks it correctly (L8). Trajectories differ from version 37.</item>
/// <item><c>AiBehaviour 39</c> — audit bugfix batch 43 (M4–M9 + L2/L4/L25/L39/L41). A punched clearance is
/// recorded as a release, so the keeper may not gather his own punch while a parry stays gatherable (M4);
/// the goal-kick exemption of the keeper's distribution gate is read from the armed restart, not the phase,
/// where it was dead code (M5, latent); an unseen carrier reads "cannot say" (0.5) for passer pressure, so
/// an unknown opponent no longer moves the line 1.2 m (M6); the band averages the same roles the line
/// assignment places on it, so a defensive midfielder no longer pulls the back four upfield (M7); the
/// scenario API rejects a locked human with a spec controller, a spec human without the lock, an off-pitch
/// ball, an owned loose ball and an unstated last play (M8/M9/L39); a zero tick budget is legal (L41); and a
/// flight that never enters the keeper's hands band falls through to the sweep (L25). Trajectories differ
/// from version 38.</item>
/// <item><c>AiBehaviour 40</c> — A7.1: a player's <b>gaze is not his body facing</b>. Every player owns a
/// head that turns at a bounded rate toward an attention target, holds the fix for a bounded window and
/// returns through a cooldown (<see cref="AI.ScanPlanner"/>), and the sight cone A2.2 filters with points
/// where the <i>head</i> looks. A player about to receive a pass (or contest a loose ball) glances at the one
/// believed opponent whose sighting has gone stale, or into the blind space behind him on alternating
/// shoulders; a body standing on the sight line also blocks it (simple occlusion). Sightings still go into
/// the A2.2 <see cref="AI.PlayerMemory"/>, so an unscanned rear threat stays an aged estimate while a
/// scanned one is fresh. Trajectories differ from version 39.</item>
/// <item><c>AiBehaviour 41</c> — A7.2: <b>delayed, expiring team calls</b>. A player may shout one
/// instruction at his own team (man-on, turn, switch, run, cover, step, drop) from his own observation; the
/// per-team <see cref="AI.TeamCallBoard"/> applies a communication delay and an expiry, and the receiver's
/// decision changes only through a preference or through the belief that somebody covers behind the press.
/// A man-on call makes the carrier shield or bounce rather than turn into pressure; a turn call supports
/// turning; a switch names the open far-side receiver; a run request raises the value of the runner's own
/// run candidates; a cover call changes the presser's depth; step and drop are the one line caller's
/// bounded bias on the A6.3 line band. No call carries an opponent position, so the knowledge boundary is
/// unchanged. Trajectories differ from version 40.</item>
/// <item><c>AiBehaviour 42</c> — A7.3: <b>reception planning before arrival</b>. The intended receiver of an
/// in-flight pass plans the first touch (<see cref="AI.ReceptionPlanner"/>): kill, open body, stride touch,
/// shield or escape, evaluated as feasible (orientation, touch) pairs against the arrival time, the next
/// action's reception value and the uncertainty of his own belief. The plan is a movement/orientation
/// intent — the command vocabulary is unchanged — and A7.4 publishes it on the command boundary. A man-on
/// call can now be addressed to the receiver by a team-mate who sees the presser closing from behind him,
/// and the plan's shield/escape touches consume that belief. The on-ball candidate gate is height-aware, so
/// a receiver under a dropping pass enters the receiving path instead of asking for kicks execution refuses.
/// Trajectories differ from version 41.</item>
/// <item><c>AiBehaviour 43</c> — A7.4: the reception plan crosses the <b>command boundary</b>. A
/// <see cref="FootballerCommand"/> carries an optional <see cref="ReceptionIntent"/> (absent by default, so
/// the locked human, training drills, replay and every fixture keep the pre-A7.4 cushion exactly), and the
/// execution layer's first touch uses it: kill, open body, stride, shield and escape now differ in how much
/// of the pass's pace the touch keeps and where it sends the ball, realised in proportion to the receiver's
/// own first-touch quality (<see cref="Locomotion.ReceptionTouchModel"/>). Reach, contact height, body
/// orientation and the contact arbitration are untouched, so an intent can never manufacture a contact.
/// Trajectories differ from version 42.</item>
/// <item><c>AiBehaviour 44</c> — A7.5: the <b>four error sources are separated</b>
/// (<see cref="AI.PlayerCognitiveState"/>). Stamina and composure are the shared causes, converted in one
/// place into a picture-of-the-game error — positional and ball noise scaled up by fatigue, the ball view's
/// delay lengthened by fatigue, and a deterministic per-candidate misjudgement of a choice's value — and into
/// an execution error, which is now applied to the first touch: pressure and fatigue make a legal touch keep
/// more of the pass's pace, so the A7 exit gate's "pressure reduces control quality" clause is true and has
/// one owner (<see cref="Locomotion.BallContactRules.PressureAtContact"/>). Everything is exactly neutral at
/// full stamina with reference composure, so a fresh fixture decides and touches as it did in version 43;
/// <see cref="AI.AiRuntimeConfig.EnableCognitiveError"/> off is the version-43 build. Trajectories differ
/// from version 43 as soon as a player is tired or pressed.</item>
/// <item><c>AiBehaviour 45</c> — A7.5 completes: <b>an early guess can be wrong</b>.
/// <see cref="ReceptionIntent"/> carries the meeting point the receiver's own flight estimate expected
/// (<see cref="ReceptionIntent.MeetingPointXZ"/>), and the execution layer compares it with where the ball
/// actually is at the contact: the further off the plan, the less of the rehearsed touch is realised
/// (<see cref="Locomotion.ReceptionTouchModel.AnticipationToleranceMeters"/>). A plan that met the ball
/// where it expected to is unchanged, and a command with no plan is unchanged. Trajectories differ from
/// version 44 once a flight is mis-predicted.</item>
/// <item><c>AiBehaviour 46</c> — A7.6: <b>archetypes are preferences on the one pipeline</b>
/// (<see cref="AI.PlayerArchetypeProfile"/>). A player may carry a <see cref="PlayerArchetype"/> —
/// playmaker, ball winner, inverted fullback, touchline winger, inside forward, target forward, sweeper
/// keeper — which biases the same candidates, weights and attention every player already has: the role's
/// tendencies gain bounded affinity deltas, off-ball path scores gain a run-kind preference, the scan
/// attention refreshes sooner or later, and the keeper's support depth and sweep commit window move. No
/// archetype generates a candidate, reads a sighting or moves a limit another player could not have, and
/// <see cref="AI.AiRuntimeConfig.EnableArchetypes"/> off is the version-45 build exactly. Trajectories
/// differ from version 45 because the default squad now fields archetypes.</item>
/// <item><c>AiBehaviour 47</c> — A8.1: <b>bounded one-two and third-player contracts</b>
/// (<see cref="AI.CombinationCoordinator"/>). When the carrier's own observation shows a give-and-go or a
/// team-mate's advertised run worth combining with, his pass can carry a contract: participants, a return
/// region, an expected arrival, a confidence and an expiry, heard by the other participants through the one
/// communication channel after the usual delay. A one-two's initiator runs into the frozen return region, the
/// receiver's continuation pass is timed to it, and a third-player contract values the layoff to the runner.
/// Every participant may abandon a lane that stopped being valid; a contract ends with the possession it was
/// made in. The preferences are bounded and no candidate, reach, sight or contact rule is added, and
/// <see cref="AI.AiRuntimeConfig.EnableCombinations"/> off is the version-46 build exactly. Trajectories
/// differ from version 46 once a combination is played.</item>
/// <item><c>AiBehaviour 48</c> — A8.2: the carrier <b>searches a few continuations</b>
/// (<see cref="AI.ShallowActionSearch"/>) instead of deciding from the pressure heuristic alone. From its
/// own perceived state it examines the best three first actions and at most three continuations each — two
/// one-two return regions and the best advertised layoff — with the defenders' sighting uncertainty charged
/// into the second ball's lane, and no truth-state rollout. The search chooses <i>which</i> continuation is
/// worth a contract; the pass is still priced by the one A4 scorer and the contract still only carries the
/// bounded A8.1 preferences. Receivers outside the bounded search keep the A8.1 heuristic trigger, and
/// <see cref="AI.AiRuntimeConfig.EnableShallowSearch"/> off with combinations on is the version-47 build
/// exactly. Trajectories differ from version 47 wherever the search chooses a move the heuristic did not.</item>
/// <item><c>AiBehaviour 49</c> — A8.3: the carrier <b>recognizes a switch / overload-to-isolate opportunity</b>
/// (<see cref="AI.SwitchRecognition"/>) from his own picture instead of switching only when a team-mate shouts
/// (A7.2) or when the ordinary pass value happens to favour it. Five observable inputs gate and weight one
/// bounded preference on a pass that already exists: the local pressure on the carrier (A7.2's own "needs a way
/// out" distance), the far side's lateral distance and room (A7.2's switch line and space, on the one space
/// scale), the receiver's matchup (an isolated one-v-one weighted by the scouted pace band, a doubled-up man
/// scoring low, a free man in between), the pass model's own completion probability for the ball, and the
/// rest-defence picture (the allocator's own forward-commitment threshold, so a short rest defence cannot buy a
/// switch). There is deliberately no pass counter, passage clock or match-time input: the recognition is a pure
/// function of the current picture, so nothing forces a switch after N passes. A far-side target that the
/// recognition supports also gets the ordinary bounded pre-rank rescue (like an announced run, a switch call or
/// A8.1's continuation target) so it is priced rather than pruned. No candidate, sight, reach, transmission
/// range or contact rule is added, and <see cref="AI.AiRuntimeConfig.EnableSwitchRecognition"/> off is the
/// version-48 build exactly. Trajectories differ from version 48 where a recognized switch outbids the pass it
/// would otherwise have played.</item>
/// <item><c>AiBehaviour 50</c> — A8.4: the defensive side gets <b>short trap contracts</b>
/// (<see cref="AI.TrapCoordinator"/>, <see cref="AI.TrapPolicy"/>). When the team's shared picture shows an
/// opponent carrier pinned to a touchline, the elected presser already close to him, the allocator's cover
/// behind that press and a believed forward option a defender can screen, the team commits to a short trap: the
/// presser takes A6.2's angled pressure point, the named screener takes the one lane out with a bounded
/// preference on the same Screen act, and the cover stays behind the press. The contract names only team facts
/// and beliefs (a believed carrier position, angles relative to it, a confidence), expires after 1.5 s, and
/// ends exactly once — won, escaped, loose, expired, lost presser/cover, participant removed or reset. A failed
/// (escaped) trap puts the side into a short recovery window, and when a trap ends every participant is back on
/// his ordinary duty: no trap outlives its possession and no inactive player keeps a role. No candidate, reach,
/// sight, contact rule or second allocator is added — the trap names points inside A6.1/A6.2's vocabulary — and
/// <see cref="AI.AiRuntimeConfig.EnableTrapContracts"/> off is the version-49 build exactly. Trajectories
/// differ from version 49 wherever a trap is raised.</item>
/// <item><c>AiBehaviour 51</c> — A8.5: <b>one small library of legal restart routines</b>
/// (<see cref="AI.RestartPolicy"/>, <see cref="AI.RestartCoordinator"/>). While the rules engine has a restart
/// armed — kick-off, throw-in, corner, free kick, indirect free kick or goal kick — the restarting team elects
/// one routine from a two-option lawful menu: play the <i>short</i> option to a support player's feet, or
/// <i>deliver</i> to a target further up the pitch, with the support and the target named as roles. The choice is
/// a bounded weight (space around the receiver, how short the short ball is, how advanced the target is) plus the
/// team profile's own directness — or A6.5b's short-distribution preference for a goal kick. The one legality
/// test the library adds is Law 11: for the restarts that are not offside-exempt (kick-off, free kick, indirect
/// free kick) a receiver must be onside by the same believed line every pass keeps, which the old taker heuristic
/// never asked. The rules engine still places the ball, designates the taker and owns the restart state, and the
/// taker still plans the delivery with A4.9's passer; the routine only chooses which lawful option he plays and
/// who receives it, and falls back to his pre-routine heuristic when the named receiver cannot be delivered to.
/// The routine ends exactly once — taken, stolen, replaced, expired, taker/participant lost or reset — so it
/// never survives into unrelated possession, and when it ends every participant is back on his ordinary duty. No
/// candidate, placement, reach, sight, contact rule or second allocator is added, and
/// <see cref="AI.AiRuntimeConfig.EnableRestartRoutines"/> off is the version-50 build exactly. Trajectories
/// differ from version 50 wherever a restart routine is applied.</item>
/// <item><c>AiBehaviour 52</c> — A9.1: <b>team styles</b>. The profile gains one explicit style axis,
/// <see cref="TeamTacticalProfile.ManOrientation01"/> (the third term of A6.3's zone/man mix and the marker's
/// goal-side standoff), and three presets join the vocabulary — <see cref="TeamTacticalProfile.Direct"/>,
/// <see cref="TeamTacticalProfile.LowBlock"/> and <see cref="TeamTacticalProfile.ManOriented"/> — alongside the
/// three that already existed. The existing presets keep every one of their numbers, but one field that only
/// gated the counter-attack transition now reaches the carrier: <see cref="TeamTacticalProfile.Directness01"/>
/// contributes the same forward-pass preference a player's own direct-pass tendency does, symmetric around the
/// neutral 0.50, so a possession side values a forward ball less and a direct side more than before. The new
/// marking axis is a bias around its neutral 0.50 too, so a profile that sets no style is unchanged. No new
/// candidate, reach, contact, allocator or second tactical model is added — a style is a set of stated
/// preferences on the one pipeline. Trajectories differ from version 51 wherever a team's directness is not
/// 0.50.</item>
/// </list>
/// </summary>
public static class SimulationVersion
{
    /// <summary>Incremented when an AI/physics change makes trajectories differ from the previous version.</summary>
    public const int AiBehaviour = 52;

    /// <summary>Version of the <see cref="RandomStreams"/> derivation.</summary>
    public const int RandomStreamsDerivation = 1;

    /// <summary>Compact tag for artifacts, e.g. <c>ai2-rs1</c>.</summary>
    public static string Tag => $"ai{AiBehaviour}-rs{RandomStreamsDerivation}";
}
public enum TacticalRole
{
    Goalkeeper,
    CenterBack,
    FullBack,
    DefensiveMidfielder,
    CentralMidfielder,
    Winger,
    Striker
}

/// <summary>
/// A7.6: what kind of player this is, as a set of <b>preferences</b> on the one decision pipeline — never a
/// capability, never a second pipeline.
///
/// The role (<see cref="TacticalRole"/>) says where a player starts and which duties he may be given; the
/// archetype says what he leans toward. A playmaker and a ball winner can occupy the same central-midfield
/// slot and generate exactly the same candidates, because an archetype only moves weights (the role's
/// tendency affinities, off-ball path preference, scan attention) and never a candidate set, a sight range,
/// a reach or a contact rule. <see cref="None"/> is the neutral archetype: it applies no delta at all, so a
/// fixture that never mentions this field behaves exactly as it did before A7.6.
/// </summary>
public enum PlayerArchetype
{
    /// <summary>No archetype: the role's own tendencies and no preference delta (the pre-A7.6 player).</summary>
    None = 0,

    /// <summary>Wants the ball in the middle: progressive, risk-tolerant passing, extra scanning.</summary>
    Playmaker,

    /// <summary>Wins it back: presses higher and longer, recycles simply, stays disciplined.</summary>
    BallWinner,

    /// <summary>Steps inside from fullback: underlaps more than he overlaps, comfortable on the ball.</summary>
    InvertedFullBack,

    /// <summary>Holds the touchline: wide runs, crosses, take-ons; less shooting.</summary>
    TouchlineWinger,

    /// <summary>Attacks the half-space and the box from wide: shoots and makes inside runs, crosses less.</summary>
    InsideForward,

    /// <summary>Holds the last line and the box: pins, late arrivals, hold-up play; little dribbling.</summary>
    TargetForward,

    /// <summary>Plays high behind the line: a deeper support position and a bolder sweep race.</summary>
    SweeperKeeper
}

public enum KickIntentType
{
    None = 0,
    GroundPass,         // Pass 1: To teammate's feet (Ground)
    LoftedPass,         // Pass 1 + High Modifier: To teammate's feet (Air/Lobbed)
    ThroughPass,        // Pass 2: Into space ahead of runner (Ground)
    LobbedThroughPass,  // Pass 2 + High Modifier: Into space ahead of runner (Air/Lobbed)
    DrivenPass,         // Fast pinged pass
    Cross,              // Pass 3: Whipped aerial cross into the box
    Shot,               // Standard shot
    CurledShot,         // Shot + High/Finesse modifier: inside-foot curled shot
    ChipShot,           // Shot + Chip button (RB / V): lofted shot over the keeper
    TrivelaShot,        // Shot + Sprint held (R2 / Shift): outside-foot curled shot
    Header,
    Clearance
}

/// <summary>
/// Classification of <see cref="KickIntentType"/> used by the ownership model, the execution layer
/// and match statistics. Audit F17: pass counting used to be a hand-written list that omitted
/// <see cref="KickIntentType.ThroughPass"/> and <see cref="KickIntentType.LobbedThroughPass"/>
/// entirely, so every through ball was invisible to analytics.
/// </summary>
public static class KickIntents
{
    public static bool IsPass(KickIntentType intent) => intent is
        KickIntentType.GroundPass or
        KickIntentType.LoftedPass or
        KickIntentType.ThroughPass or
        KickIntentType.LobbedThroughPass or
        KickIntentType.DrivenPass or
        KickIntentType.Cross;

    public static bool IsShot(KickIntentType intent) => intent is
        KickIntentType.Shot or
        KickIntentType.CurledShot or
        KickIntentType.ChipShot or
        KickIntentType.TrivelaShot;
}

public enum ControllerSourceType
{
    AI,
    Scripted,
    RecordedReplay,
    HumanPreMatchLocked
}

/// <summary>How a player last played the ball. Used by the rules engine to judge offside at the
/// moment the ball is played rather than when the toucher identity happens to change (audit F08).</summary>
public enum BallContactKind
{
    /// <summary>Pass, shot, cross, clearance or keeper distribution.</summary>
    Kick,
    /// <summary>Standing tackle that dispossessed an opponent.</summary>
    Tackle,
    /// <summary>First-touch control or a dribble touch.</summary>
    Collection,
    /// <summary>Goalkeeper catch or parry.</summary>
    Save
}

/// <summary>
/// Which restart put the ball back into play (audit F14). Law 11 treats restarts differently:
/// a player can receive the ball directly from a throw-in, goal kick or corner kick without being
/// offside, while a goal cannot be scored directly from an indirect free kick or a throw-in.
/// The old model carried nothing but the phase, so every restart was treated as open play.
/// </summary>
public enum RestartKind
{
    /// <summary>Open play: no restart is awaiting its first touch.</summary>
    None = 0,
    Kickoff,
    ThrowIn,
    GoalKick,
    CornerKick,
    /// <summary>A direct free kick (a foul): it can score directly and offside applies normally.</summary>
    FreeKick,
    /// <summary>An indirect free kick (an offside offence): a goal needs a second touch first.</summary>
    IndirectFreeKick,
    PenaltyKick
}

/// <summary>Law 11 semantics of each <see cref="RestartKind"/>, kept in one place (audit F14).</summary>
public static class RestartRules
{
    /// <summary>
    /// There is no offside offence when a player receives the ball directly from a throw-in, a goal
    /// kick or a corner kick (IFAB Law 11). The restarting team's receivers must therefore not be put
    /// into the offside snapshot at the restart's first touch.
    /// </summary>
    public static bool ExemptsOffside(RestartKind kind) => kind is
        RestartKind.ThrowIn or RestartKind.GoalKick or RestartKind.CornerKick;

    /// <summary>
    /// These restarts cannot score directly: the ball must be touched by another player first. That
    /// covers an indirect free kick and a throw-in. A ball that crosses the goal line from such a
    /// restart without a second touch is a goal kick (opponents' goal) or a corner kick (own goal),
    /// never a goal.
    /// </summary>
    public static bool RequiresSecondTouchBeforeGoal(RestartKind kind) => kind is
        RestartKind.IndirectFreeKick or RestartKind.ThrowIn;
}

/// <summary>
/// A play/touch of the ball, emitted by the execution layer and consumed by the rules engine in the
/// same tick. Player positions at the moment of the play are the positions the rules engine sees when
/// it consumes the event, so no separate snapshot array is carried.
/// </summary>
public readonly record struct BallContactEvent(
    int Tick,
    BallContactKind Kind,
    int PlayerId,
    TeamId Team,
    Vector3 BallPosition
)
{
    /// <summary>The kick that produced this contact (pass, shot, clearance...).</summary>
    public KickIntentType Intent { get; init; } = KickIntentType.None;

    /// <summary>
    /// The player the ball was actually played to, when the play was a planned pass (audit F01/F10).
    /// Reception assistance and the responder election use it instead of guessing from proximity.
    /// </summary>
    public int? IntendedReceiverId { get; init; }

    /// <summary>
    /// True for a <see cref="BallContactKind.Save"/> that only deflected the ball (a parry) rather
    /// than catching it. A deflection must not hand team possession to the keeper's team (F10).
    /// </summary>
    public bool IsParry { get; init; }

    /// <summary>
    /// The restart that put the ball back into play, when this touch *is* that restart's first play
    /// (audit F14). It lets the rules engine apply the Law 11 restart exemptions (no offside from a
    /// throw-in, goal kick or corner) and the no-direct-goal rule for indirect restarts, which a bare
    /// contact kind cannot express. <see cref="RestartKind.None"/> for ordinary open play.
    /// </summary>
    public RestartKind RestartOrigin { get; init; } = RestartKind.None;

    /// <summary>
    /// A deliberate play: a kick, a tackle or a controlled touch. Under Law 11 an opponent's
    /// deliberate play removes a previously offside attacker's restriction, whereas a save - a
    /// reaction, not a deliberate play - does not (audit F14).
    /// </summary>
    public bool IsDeliberatePlay => Kind is BallContactKind.Kick or BallContactKind.Tackle or BallContactKind.Collection;

    /// <summary>
    /// A keeper catch/parry. It is a "deliberate save", which by law does **not** reset offside.
    /// </summary>
    public bool IsSaveOrDeflection => Kind == BallContactKind.Save;
}

/// <summary>
/// How the ball was last put into play (A6.6): the contact kind, the kick that produced it and who
/// made it. It exists because one law cannot be decided from "the ball's last toucher" alone — Law 12
/// distinguishes a ball <b>deliberately kicked</b> to the goalkeeper from one that merely deflected off
/// a team-mate, and the goalkeeper's own release from anybody else's play.
///
/// The fact is public play information, exactly like <see cref="Physics.BallState.LastTouchedByTeam"/>: every
/// player saw the pass. It is therefore safe to publish in an observation, where it must not be confused
/// with the ownership model (which is about who <i>has</i> the ball).
/// </summary>
/// <param name="Kind">The contact that played it.</param>
/// <param name="Intent">The kick, when <paramref name="Kind"/> is <see cref="BallContactKind.Kick"/>.</param>
/// <param name="PlayerId">Who played it; null when the play is unknown (a restart setup, a seeded ball).</param>
/// <param name="Team">That player's team.</param>
public readonly record struct BallLastPlay(
    BallContactKind Kind,
    KickIntentType Intent,
    int? PlayerId,
    TeamId? Team)
{
    /// <summary>
    /// No play is recorded: a restart has reset the ball, or a fixture seeded one. Every law that reads
    /// this must treat "unknown" as unconstrained rather than guessing a play.
    /// </summary>
    public static BallLastPlay None => new(BallContactKind.Kick, KickIntentType.None, null, null);

    /// <summary>True when a real contact is recorded here.</summary>
    public bool IsKnown => PlayerId.HasValue;

    /// <summary>
    /// True when the play was a deliberate kick, i.e. the thing Law 12's back-pass clause names. A header
    /// is a deliberate play but not a kick, and a control touch or a tackle is neither, so neither is covered
    /// by that clause. A throw-in is executed as a kick contact in this engine, so it is covered here too.
    /// </summary>
    public bool IsDeliberateKick =>
        Kind == BallContactKind.Kick && Intent != KickIntentType.Header;

    /// <summary>
    /// True when the ball was punched clear rather than caught or parried (A6.5b/M4). A punch is a
    /// <see cref="BallContactKind.Save"/> with <c>IsParry</c>, so <see cref="Kind"/> alone cannot tell it
    /// apart from a dive parry — and the two need different laws: the keeper may gather his own parry, but a
    /// punch is a release into play with nobody having touched it since.
    /// </summary>
    public bool PunchedClearance { get; init; }

    /// <summary>
    /// True when this play was the keeper's own release — a kick, a throw, a clearance or a punched
    /// clearance — so nobody has touched the ball since he put it into play (M4). Deliberately <b>not</b>
    /// <see cref="IsDeliberateKick"/>: a punch must bar his hands without becoming a Law 11 deliberate play,
    /// and a parry must bar nothing at all.
    /// </summary>
    public bool IsOwnRelease =>
        (Kind == BallContactKind.Kick && Intent != KickIntentType.Header) || PunchedClearance;
}

/// <summary>
/// Why the execution layer refused to play the ball for a player who asked to (audit F17).
/// </summary>
public enum ContactRejectionReason
{
    /// <summary>The contact was executed (or nothing was requested).</summary>
    None = 0,
    /// <summary>The ball was outside the physical reach for this player's current action.</summary>
    OutOfReach,
    /// <summary>The ball was above the reachable height (no jump/header, or above it).</summary>
    BallTooHigh,
    /// <summary>The player's body was facing away from the ball and could not play it.</summary>
    FacingAway,
    /// <summary>Kick/tackle recovery: this player was not allowed to play the ball yet.</summary>
    InRecovery,
    /// <summary>In reach, but another player won the single-contact arbitration for this tick.</summary>
    LostArbitration,
    /// <summary>The ball is held by a goalkeeper and only the holder may release it.</summary>
    BallHeld,
    /// <summary>A foul triggered a restart, so the pending contact was cancelled.</summary>
    RestartTriggered
}

/// <summary>
/// One player's requested ball contact this tick and whether execution actually played the ball.
/// Statistics and input buffers consume this instead of assuming a proposed command happened
/// (audit F17). Contacts the execution layer made without a request (a plain first touch) are not
/// recorded here, only players who asked to kick or tackle.
/// </summary>
public readonly record struct BallContactAttempt(
    int Tick,
    int PlayerId,
    TeamId Team,
    KickIntentType Intent,
    bool WantedTackle,
    bool Executed,
    ContactRejectionReason Reason
)
{
    public bool WantedKick => Intent != KickIntentType.None;
    public bool WantedContact => WantedKick || WantedTackle;
}

/// <summary>
/// A7.3/A7.4: the touch a receiver has decided to take before the ball arrives.
///
/// It lives beside <see cref="FootballerCommand"/> rather than in the planner because it is the one
/// piece of the reception plan that crosses the command boundary: the command carries it as a
/// <see cref="ReceptionIntent"/>, and execution decides what a <i>legal</i> contact does differently
/// because of it. The kind never decides <i>whether</i> a contact happens.
/// </summary>
public enum ReceptionTouchKind
{
    /// <summary>No plan (the neutral default of a fixture or a non-receiver).</summary>
    None = 0,

    /// <summary>Kill it: meet the ball slowly and stop it, body already open toward the next action.</summary>
    Kill,

    /// <summary>Open the body: meet the ball on the half-turn so the first touch can go up the pitch.</summary>
    OpenBody,

    /// <summary>Stride touch: keep the run going and touch the ball into the space ahead.</summary>
    StrideTouch,

    /// <summary>Shield it: get the body between the ball and the believed presser.</summary>
    Shield,

    /// <summary>Escape pressure: touch the ball away from the believed presser into the larger space.</summary>
    EscapePressure
}

/// <summary>
/// A7.4: the reception intent a command carries — "if this body gets a <i>legal</i> touch on that ball,
/// make it this touch".
///
/// <b>The intent never buys a contact.</b> Reach, contact height, body orientation, recovery and the
/// contact arbitration are decided exactly as before (<c>BallContactRules</c> plus the execution
/// layer's own arbitration); a plan to meet a ball the body cannot reach does nothing at all. What the
/// intent may change is what a contact that <i>did</i> happen does to the ball: how much of the
/// incoming pace is kept and which way the touch sends it, realised in proportion to the receiver's own
/// first-touch quality, so a bad touch still cannot be made good by intending it
/// (<see cref="Locomotion.ReceptionTouchModel"/>).
///
/// The neutral value is <see cref="None"/>, so the locked human, training drills, replay and every
/// fixture keep the pre-A7.4 cushion bit for bit.
/// </summary>
public readonly record struct ReceptionIntent(ReceptionTouchKind TouchKind)
{
    /// <summary>The neutral intent: no planned touch, the exact pre-A7.4 first-touch cushion.</summary>
    public static ReceptionIntent None => default;

    /// <summary>
    /// A7.5: the point the receiver's flight estimate said the ball would be when he met it — his own
    /// <b>anticipation</b>, not a fact. Execution compares it with where the ball actually is at the contact;
    /// a contact somewhere else means the plan was built on a wrong guess, and the rehearsed touch is only
    /// partly realisable (<see cref="Locomotion.ReceptionTouchModel.AnticipationToleranceMeters"/>). Only
    /// meaningful when <see cref="IsPlanned"/>. It is the planner's belief carried on the command, never a
    /// second trajectory estimate.
    /// </summary>
    public Vector2 MeetingPointXZ { get; init; }

    /// <summary>True when a real touch was planned (and execution may shape a legal contact with it).</summary>
    public bool IsPlanned => TouchKind != ReceptionTouchKind.None;

    /// <summary>Compact text form for the execution diagnostic, the inspector or a trace.</summary>
    public string Describe() => IsPlanned ? TouchKind.ToString() : "none";
}

public readonly record struct FootballerCommand(
    Vector2 DesiredMoveDirectionXZ,
    Vector2 DesiredFacingDirectionXZ,
    bool Sprint,
    bool ShieldBall,
    bool RequestTackle,
    bool RequestJumpHeader,
    bool RequestKeeperDive,
    Vector3 KeeperDiveDirection,
    KickIntentType KickType,
    Vector3 KickAimDirectionXZ,
    float KickPower01,
    float RequestedCurveSpin
)
{
    /// <summary>
    /// Player this kick is meant for when <see cref="KickType"/> is a pass. Null for shots,
    /// clearances and unplanned kicks. Carried into the contact event so reception and possession
    /// no longer have to infer the receiver from "whoever is nearest" (audit F10/F11).
    /// </summary>
    public int? IntendedReceiverId { get; init; }

    /// <summary>
    /// A7.4: the touch this player planned for an arriving ball (A7.3's plan, published on the command
    /// boundary). Defaults to <see cref="ReceptionIntent.None"/>, which is the exact pre-A7.4 execution
    /// path — the command is a readonly record struct, so no existing call site had to change.
    /// </summary>
    public ReceptionIntent ReceptionIntent { get; init; } = ReceptionIntent.None;

    public static FootballerCommand Idle => new(
        Vector2.Zero, new Vector2(1f, 0f), false, false, false, false, false,
        Vector3.Zero, KickIntentType.None, new Vector3(1f, 0f, 0f), 0f, 0f);
}

/// <summary>
/// Who actually has the ball, separated from "who last touched it" (audit F10). The old model used
/// <see cref="BallState.LastTouchedByTeam"/> for both, so a deflection, an unreachable clearance or a
/// keeper parry could be read as sustained possession.
/// </summary>
public enum BallControlState
{
    /// <summary>Nobody controls the ball; it is rolling, bouncing or in flight without a planned receiver.</summary>
    Loose,
    /// <summary>A player has the ball at their feet (dribble touch / first-touch control).</summary>
    Controlled,
    /// <summary>A planned pass is travelling toward a teammate.</summary>
    InFlightPass,
    /// <summary>Goalkeeper has caught or parried the ball and currently holds it.</summary>
    KeeperHeld
}

/// <summary>Snapshot of the authoritative ball-ownership model, refreshed every live tick.</summary>
public readonly record struct BallPossessionSnapshot(
    BallControlState State,
    TeamId? PossessionTeam,
    int? ControllingPlayerId,
    int? IntendedReceiverId
)
{
    public static BallPossessionSnapshot Loose => new(BallControlState.Loose, null, null, null);

    public bool IsControlledBy(TeamId team) =>
        State is BallControlState.Controlled or BallControlState.KeeperHeld && PossessionTeam == team;
}

/// <summary>
/// What the execution layer needs to know to apply (or refuse) reception assistance this tick.
/// </summary>
public readonly record struct ReceptionAssistContext(
    BallControlState ControlState,
    int? IntendedReceiverId,
    int? HumanPlayerId
);

public readonly record struct PerceivedEntity(
    int PlayerId,
    TeamId Team,
    TacticalRole Role,
    Vector3 EstimatedPosition,
    Vector3 EstimatedVelocity,
    float Confidence01
)
{
    /// <summary>
    /// Ticks since this entity was actually observed (A2.2). 0 means the estimate comes from this tick's
    /// observation; a positive value means it is a prediction from memory and its position is where the
    /// entity is *expected* to be, not where it is.
    /// </summary>
    public int ObservedAgeTicks { get; init; }

    /// <summary>
    /// Positional uncertainty introduced by that age, in metres (A2.2). Zero for a fresh observation.
    /// A scorer may widen its tolerance by this instead of pretending a stale position is exact.
    /// </summary>
    public float PositionUncertaintyMeters { get; init; }

    /// <summary>
    /// What this observer believes the player is physically capable of (A2.3). Scouting knowledge or a
    /// conservative default — never the live <see cref="PlayerAttributes"/>. A scorer that estimates
    /// arrival times must go through this, which is what keeps a new attribute from becoming a leak.
    /// </summary>
    public CapabilityEstimate Capability { get; init; } = CapabilityEstimate.Generic;
}

public enum MatchPhase
{
    PreKickoff,
    FirstHalf,
    HalfTime,
    SecondHalf,
    GoalScored,
    ThrowIn,
    GoalKick,
    CornerKick,
    FreeKick,
    PenaltyKick,
    FullTime
}

/// <summary>
/// Which half is being played, tracked independently of <see cref="MatchPhase"/>.
/// Audit F05: the old code inferred the half from the phase, so every restart inside the
/// second half (throw-in, corner, goal kick, free kick, penalty, goal celebration) looked
/// like the first half and flipped attacking directions.
/// </summary>
public enum MatchPeriod
{
    FirstHalf,
    SecondHalf
}

public sealed class FootballerObservation
{
    public required int SelfId { get; init; }
    public required TeamId SelfTeam { get; init; }
    public required TacticalRole SelfRole { get; init; }

    /// <summary>
    /// A7.6: this player's archetype — a preference, never a capability. Default <see cref="PlayerArchetype.None"/>
    /// so an observation built by a fixture that predates A7.6 behaves exactly as it did (no path preference,
    /// no attention change, no keeper change). The archetype's tendency deltas are already folded into
    /// <see cref="SelfTendencies"/> by the match that built the player; this field is what a preference that
    /// has no tendency slot (the run-kind preference) reads.
    /// </summary>
    public PlayerArchetype SelfArchetype { get; init; } = PlayerArchetype.None;

    public required Vector3 SelfPosition { get; init; }
    public required Vector3 SelfVelocity { get; init; }
    public required Vector2 SelfFacingXZ { get; init; }

    /// <summary>
    /// A7.1: this player's head/gaze state — where the head points relative to the body, what attention
    /// turned it, and whether it is holding a completed fix. The default is the neutral state (head not
    /// turned), so an observation built by a fixture that knows nothing about scanning behaves exactly as
    /// it did before A7.1 and a standalone controller never gets a gaze it was not given.
    /// </summary>
    public AI.ScanStatus Scan { get; init; } = AI.ScanStatus.None;

    /// <summary>
    /// Where the head/eyes point in the XZ plane (A7.1). With a neutral <see cref="Scan"/> this is exactly
    /// <see cref="SelfFacingXZ"/> — the pre-A7.1 sight direction — which is what makes scanning an explicit
    /// difference between two otherwise identical observations.
    /// </summary>
    public Vector2 EffectiveGazeXZ => Scan.GazeFrom(SelfFacingXZ);

    /// <summary>
    /// A7.2: what this player hears from his own team this tick — at most one call addressed to him and at
    /// most one team-wide defensive instruction. The default is neutral, so an observation built by a
    /// fixture or a standalone controller behaves exactly as it did before A7.2; the calls arrive here
    /// (rather than being read from the match object inside a scorer) so the knowledge boundary stays in
    /// one place and two observations can differ by exactly one call in a test.
    /// </summary>
    public AI.TeamCallContext Calls { get; init; } = AI.TeamCallContext.None;

    /// <summary>
    /// A8.1: this player's part in a live team combination — a one-two or a third-player move — as he is
    /// entitled to know it. The default is neutral, so a fixture or standalone controller that knows nothing
    /// about combinations behaves exactly as it did before A8.1. Like <see cref="Calls"/> it is carried on
    /// the observation (rather than read from the match object inside a scorer) so the knowledge boundary
    /// stays in one place and two observations can differ by exactly one contract in a test.
    /// </summary>
    public AI.CombinationContext Combination { get; init; } = AI.CombinationContext.None;

    /// <summary>
    /// A8.4: this player's part in a live short trap — a defending contract naming the presser, the screener and
    /// the cover — as he is entitled to know it. The default is neutral, so a fixture or standalone controller
    /// that knows nothing about traps behaves exactly as it did before A8.4. Like <see cref="Calls"/> and
    /// <see cref="Combination"/> it is carried on the observation rather than read from the match object inside a
    /// scorer, so the knowledge boundary stays in one place and two observations can differ by exactly one trap
    /// in a test.
    /// </summary>
    public AI.TrapContext Trap { get; init; } = AI.TrapContext.None;

    /// <summary>
    /// A8.5: this player's part in the live restart routine — the taker, the short support or the longer target
    /// — as he is entitled to know it. The default is neutral, so a fixture or standalone controller that knows
    /// nothing about restart routines behaves exactly as it did before A8.5. Like <see cref="Calls"/>,
    /// <see cref="Combination"/> and <see cref="Trap"/> it is carried on the observation rather than read from the
    /// match object inside a scorer, so the knowledge boundary stays in one place and two observations can differ
    /// by exactly one routine in a test.
    /// </summary>
    public AI.RestartRoutineContext Routine { get; init; } = AI.RestartRoutineContext.None;

    public required float Stamina01 { get; init; }
    public required bool AvailableToKick { get; init; }
    public required float ControlQuality01 { get; init; }

    /// <summary>
    /// How balanced this player currently is (A2.1). Execution scales acceleration by it after a
    /// tackle or a stumble, so a reach estimate that assumed a balanced player would claim a recovery
    /// time the body cannot produce. Defaults to 1 (balanced) for callers that do not set it.
    /// </summary>
    public float SelfBalance01 { get; init; } = 1f;
    public required Vector3 PerceivedBallPosition { get; init; }
    public required Vector3 PerceivedBallVelocity { get; init; }
    public required TeamId? PossessingTeam { get; init; }
    public required int? BallLastTouchedByPlayerId { get; init; }

    /// <summary>
    /// Team of the last touch. This is a physical fact (the execution layer's reach rule depends on
    /// it), not the possession model — use <see cref="BallControlState"/> for "who has the ball".
    /// </summary>
    public TeamId? BallLastTouchedByTeam { get; init; }

    /// <summary>
    /// How the ball was last put into play (A6.6), when the engine knows. It lets a controller answer a
    /// law question the ownership model cannot: a keeper may not handle a ball a team-mate deliberately
    /// kicked to him, but may handle one that merely deflected off him. It is public play information,
    /// like <see cref="BallLastTouchedByTeam"/>, and never an opponent's intent or a live attribute.
    /// </summary>
    public BallLastPlay BallLastPlay { get; init; } = BallLastPlay.None;
    public required Vector3 TacticalAnchorPosition { get; init; }
    public required float AttackingDirectionX { get; init; }
    public required float MatchTimeSeconds { get; init; }
    public required MatchPhase CurrentMatchPhase { get; init; }
    public required bool IsSetPieceTaker { get; init; }

    /// <summary>
    /// Engine tick this observation was built on, or 0 for a controller built without a match (probes,
    /// unit tests). The AI scheduler needs an absolute, monotonic tick so a cadence can never be extended by
    /// a paused or suspended tick; zero means "unknown", which always forces a fresh decision.
    /// </summary>
    public int Tick { get; init; }
    public required IReadOnlyList<PerceivedEntity> VisiblePlayers { get; init; }
    public required TeamTacticalProfile TeamProfile { get; init; }
    public required PlayerTendencies SelfTendencies { get; init; }
    public required PlayerAttributes SelfAttributes { get; init; }

    /// <summary>Authoritative control state of the ball (audit F10).</summary>
    public BallControlState BallControlState { get; init; } = BallControlState.Loose;

    /// <summary>The player currently controlling the ball, when there is one.</summary>
    public int? BallControllingPlayerId { get; init; }

    /// <summary>Receiver of the pass currently in flight, when the play was a planned pass.</summary>
    public int? IntendedReceiverId { get; init; }

    /// <summary>
    /// Seconds left before this player may release a ball they are holding in their hands
    /// (goalkeeper catch, audit F03). Zero for every other player/tick.
    /// </summary>
    public float BallHoldRemainingSeconds { get; init; }

    /// <summary>Persistent team-level off-ball job (audit F04).</summary>
    public TacticalJob TacticalJob { get; init; } = TacticalJob.Shape;

    /// <summary>Target point the assigned job wants this player to occupy/work toward.</summary>
    public Vector3? TacticalJobAnchor { get; init; }

    /// <summary>Opponent to mark, for <see cref="TacticalJob.Marker"/>.</summary>
    public int? JobTargetPlayerId { get; init; }

    /// <summary>Human-readable job reason for debug HUDs/probes.</summary>
    public string JobReason { get; init; } = string.Empty;

    /// <summary>
    /// A3.3: true when the assigned job is a *temporary responsibility* for a region a teammate had to
    /// leave (the CB step-out case), not this player's own duty. Execution is unchanged — the job
    /// anchor is the movement target — but a temporary duty is an exception that ends on handback.
    /// </summary>
    public bool IsTemporaryResponsibility { get; init; }

    /// <summary>A3.3: the teammate whose vacated region this player protects, when the duty is temporary.</summary>
    public int? ResponsibilitySourcePlayerId { get; init; }

    /// <summary>
    /// True when the match-level election has made this player their team's ball responder.
    /// Null means the controller was built without a match (standalone probes/tests); controllers
    /// then fall back to their local nearest-teammate estimate.
    /// </summary>
    public bool? IsDesignatedBallResponder { get; init; }

    /// <summary>
    /// The team-mate the match elected as the ball responder this tick (A6.1), or null when there is no
    /// match-level election behind this controller (standalone probes and fixtures).
    ///
    /// The team's own press is a plan its players can see: knowing who is closing the carrier down is what
    /// lets a second defender avoid committing to the same space independently. It is a team fact, not a
    /// read of any opponent, so it crosses no knowledge boundary (unlike an opponent's intent).
    /// </summary>
    public int? BallResponderPlayerId { get; init; }

    /// <summary>
    /// The team-mate the match elected as the <b>second man</b> beside the ball responder this tick (A6.1),
    /// or null when there is no match-level election behind this controller (standalone probes and
    /// fixtures). Zero means the match looked and found no eligible second man.
    ///
    /// Resolving this independently from each defender's own picture is not safe: every observation carries
    /// its own noise, so two equidistant centre-backs can each conclude that he is the nearest one. The
    /// second man is a team commitment — exactly one player may step in — so it is elected once and
    /// published like <see cref="BallResponderPlayerId"/>.
    /// </summary>
    public int? SupportPresserPlayerId { get; init; }

    /// <summary>
    /// Oldest age, in ticks, among the opponent estimates in <see cref="VisiblePlayers"/> (A2.2), or 0
    /// when every opponent in the picture was seen this tick. This is the staleness the trace records as
    /// knowledge age (A0.4) and the first honest answer that field has had.
    /// </summary>
    public int KnowledgeAgeTicks { get; init; }

    /// <summary>
    /// This player's team context for the current tick (A3.1): possession stability, attack/defence
    /// phase, zone, transition window and any armed restart. Default is the neutral context (no claim,
    /// defending, own third, no transition, no restart), which is what a lookup with no match behind it
    /// should look like — a standalone controller never sees "attacking" it was not told about.
    /// </summary>
    public TeamContext TacticalContext { get; init; }

    /// <summary>
    /// The team's rest-defence picture from its last in-possession pass (A3.6), for scorers that must know
    /// what a forward commitment costs if the ball is lost here. Default is "no picture"
    /// (<see cref="AI.RestDefence.Security01"/> = 0), which a scorer must treat as unknown — not as
    /// "fully exposed" — so a standalone controller is not punished for knowledge it was never given.
    /// </summary>
    public AI.RestDefence TeamSecurity { get; init; }

    /// <summary>
    /// The depth band this team's defensive line is standing in (A6.3), or an invalid band while the team is
    /// in possession or has no line at all. It is the same object the allocator placed the <c>DefensiveLine</c>
    /// job targets from, so a line player's movement target and his own picture of the line cannot disagree.
    /// Default is "no band", which a controller must treat as unknown rather than as depth zero.
    /// </summary>
    public AI.DefensiveLineBand TeamLineBand { get; init; }

    /// <summary>
    /// The team's answer to its own most recent turnover (A6.4): counter-press, hold the carrier up while the
    /// side recovers, or retreat — with the four inputs the answer was compared on. It is a team fact built by
    /// the allocator from the team's belief and its own players, published like
    /// <see cref="TeamLineBand"/> so a defender's press depth and the second man's licence cannot disagree
    /// with the decision the rest of the team made. Default is "no transition": a standalone controller, or a
    /// team that has the ball or has already reorganised.
    /// </summary>
    public AI.TurnoverSituation TeamTurnover { get; init; }
}

public interface IFootballerController
{
    ControllerSourceType SourceType { get; }
    FootballerCommand Decide(in FootballerObservation observation, float dt);
    string GetDebugSummary();
}

/// <summary>Kicking foot, used for the inside/outside-foot strike choice (audit follow-up).</summary>
public enum Footedness
{
    Right,
    Left
}

public sealed class PlayerAttributes
{
    /// <summary>
    /// Strong foot. Shooting across the body to the opposite side forces an outside-foot strike
    /// (or the weak foot), which is less accurate and curves the other way.
    /// </summary>
    public Footedness PreferredFoot { get; init; } = Footedness.Right;

    public float MaxSprintSpeed { get; init; } = 8.4f;
    public float Acceleration { get; init; } = 6.8f;
    public float AgilityTurnRate { get; init; } = 9.5f;
    public float StaminaCapacity { get; init; } = 1.0f;
    public float FootAccuracy01 { get; init; } = 0.82f;
    public float ShotPowerMax { get; init; } = 31.0f;
    public float FirstTouch01 { get; init; } = 0.82f;
    public float Balance01 { get; init; } = 0.80f;
    public float Strength01 { get; init; } = 0.78f;
    public float ReachRadius { get; init; } = 0.75f;
    public float ReactionDelaySeconds { get; init; } = 0.08f;
    public float Composure01 { get; init; } = 0.80f;
    public float GoalkeepingReflex01 { get; init; } = 0.82f;
}

/// <summary>
/// Neutral values for <see cref="PlayerTendencies"/>. Behaviour code applies deltas around these
/// baselines, so the default tendencies (and every place that constructs a default) keep the exact
/// pre-F18 behaviour while a configured tendency has a measurable effect (audit F18).
/// </summary>
public static class PlayerTendencyBaselines
{
    public const float RiskTolerance = 0.55f;
    public const float DirectPassPreference = 0.55f;
    public const float ShootingAppetite = 0.60f;
    public const float DribblePreference = 0.50f;
    public const float PositionalDiscipline = 0.70f;
    public const float PressingAggression = 0.60f;

    /// <summary>A7.6: appetite for a wide delivery (the cross). The cross's own preference term reads it.</summary>
    public const float CrossPreference = 0.55f;

    /// <summary>A7.6: how eager the player is to refresh a stale sighting (the scan attention policy).</summary>
    public const float ScanAppetite = 0.50f;

    /// <summary>A7.6: how boldly the keeper leaves his line to sweep and how deep he supports in possession.</summary>
    public const float SweepAggression = 0.50f;
}

public sealed class PlayerTendencies
{
    public float RiskTolerance01 { get; init; } = PlayerTendencyBaselines.RiskTolerance;
    public float DirectPassPreference01 { get; init; } = PlayerTendencyBaselines.DirectPassPreference;
    public float ShootingAppetite01 { get; init; } = PlayerTendencyBaselines.ShootingAppetite;
    public float DribblePreference01 { get; init; } = PlayerTendencyBaselines.DribblePreference;
    public float PositionalDiscipline01 { get; init; } = PlayerTendencyBaselines.PositionalDiscipline;
    public float PressingAggression01 { get; init; } = PlayerTendencyBaselines.PressingAggression;

    /// <summary>
    /// A7.6: appetite for a wide delivery. Read by the cross candidate's preference term. The neutral value
    /// is <see cref="PlayerTendencyBaselines.CrossPreference"/>, so a default player prices a cross exactly
    /// as he did before A7.6.
    /// </summary>
    public float CrossPreference01 { get; init; } = PlayerTendencyBaselines.CrossPreference;

    /// <summary>
    /// A7.6: how eager the player is to refresh a stale sighting — the scan attention policy's one player
    /// input (<see cref="AI.ScanPlanner.RefreshAgeTicksFor"/>). Read by perception, not by a scorer, and the
    /// neutral value reproduces the pre-A7.6 attention exactly.
    /// </summary>
    public float ScanAppetite01 { get; init; } = PlayerTendencyBaselines.ScanAppetite;

    /// <summary>
    /// A7.6: how boldly the keeper leaves his line — the support depth in possession and the sweep/claim
    /// race window. Read only by the goalkeeper's decision (<see cref="AI.GoalkeeperDecisionModel"/>); the
    /// physical reach and the law are untouched, so this is a preference for leaving the line, not a longer
    /// arm. Neutral reproduces the pre-A7.6 keeper.
    /// </summary>
    public float SweepAggression01 { get; init; } = PlayerTendencyBaselines.SweepAggression;
}

/// <summary>
/// Deterministic tendency presets per role, so a match expresses individual players instead of every
/// footballer being identical. Audit F18: these fields used to be stored and exposed but never read,
/// so no preset could have mattered. Values stay near the neutral baselines — the role differences
/// are what the AI responds to.
///
/// A7.6: <see cref="ForRole"/> is the role alone (the pre-A7.6 player). <see cref="For"/> composes the role
/// with an <see cref="PlayerArchetype"/>'s bounded affinity deltas
/// (<see cref="AI.PlayerArchetypeProfile"/>), which is how one player in a role can lean toward one way of
/// playing without a second candidate or a second scorer.
/// </summary>
public static class PlayerTendencyProfile
{
    /// <summary>The role's own tendency preset, with no archetype deltas (the pre-A7.6 player).</summary>
    public static PlayerTendencies ForRole(TacticalRole role) => RoleTendencies(role);

    /// <summary>
    /// A7.6: the role's preset with <paramref name="archetype"/>'s affinity deltas applied and clamped to
    /// 0..1. <see cref="PlayerArchetype.None"/> returns <see cref="ForRole"/> exactly.
    /// </summary>
    public static PlayerTendencies For(TacticalRole role, PlayerArchetype archetype) =>
        AI.PlayerArchetypeProfile.Apply(RoleTendencies(role), archetype);

    private static PlayerTendencies RoleTendencies(TacticalRole role) => role switch
    {
        TacticalRole.Striker => new PlayerTendencies
        {
            ShootingAppetite01 = 0.80f,
            DirectPassPreference01 = 0.65f,
            RiskTolerance01 = 0.62f,
            DribblePreference01 = 0.55f,
            PositionalDiscipline01 = 0.62f,
            PressingAggression01 = 0.55f
        },
        TacticalRole.Winger => new PlayerTendencies
        {
            ShootingAppetite01 = 0.68f,
            DirectPassPreference01 = 0.60f,
            RiskTolerance01 = 0.62f,
            DribblePreference01 = 0.72f,
            PositionalDiscipline01 = 0.55f,
            PressingAggression01 = 0.52f
        },
        TacticalRole.CentralMidfielder => new PlayerTendencies
        {
            ShootingAppetite01 = 0.55f,
            DirectPassPreference01 = 0.62f,
            RiskTolerance01 = 0.60f,
            DribblePreference01 = 0.52f,
            PositionalDiscipline01 = 0.72f,
            PressingAggression01 = 0.62f
        },
        TacticalRole.DefensiveMidfielder => new PlayerTendencies
        {
            ShootingAppetite01 = 0.40f,
            DirectPassPreference01 = 0.45f,
            RiskTolerance01 = 0.42f,
            DribblePreference01 = 0.45f,
            PositionalDiscipline01 = 0.80f,
            PressingAggression01 = 0.70f
        },
        TacticalRole.FullBack => new PlayerTendencies
        {
            ShootingAppetite01 = 0.30f,
            DirectPassPreference01 = 0.50f,
            RiskTolerance01 = 0.40f,
            DribblePreference01 = 0.45f,
            PositionalDiscipline01 = 0.78f,
            PressingAggression01 = 0.72f
        },
        TacticalRole.CenterBack => new PlayerTendencies
        {
            ShootingAppetite01 = 0.25f,
            DirectPassPreference01 = 0.40f,
            RiskTolerance01 = 0.35f,
            DribblePreference01 = 0.30f,
            PositionalDiscipline01 = 0.86f,
            PressingAggression01 = 0.65f
        },
        _ => new PlayerTendencies()
    };
}

/// <summary>
/// A team's style: a set of stated <b>preferences</b> on the one decision pipeline, never a second tactical
/// model, a candidate set or a capability. Every field is read by an existing scorer or shape rule — the
/// presets below only choose the numbers, so a new style is a configuration and not a behaviour module.
///
/// A9.1 vocabulary (one preset for each configuration the plan names):
/// <list type="bullet">
/// <item><see cref="PossessionControl"/> — positional possession.</item>
/// <item><see cref="Direct"/> — direct play from a mid-block.</item>
/// <item><see cref="DirectCounterattack"/> — counter-attacking from a deep block.</item>
/// <item><see cref="HighPressGegenpress"/> — high press / gegenpressing.</item>
/// <item><see cref="LowBlock"/> — low block (deep, compact, minimal pressing).</item>
/// <item><see cref="ManOriented"/> — man-oriented pressing (the explicit marking axis).</item>
/// </list>
/// The neutral profile (<see cref="Name"/> "Balanced", every field at its default) is not a style preset: it
/// is the control a style is compared against, and it is the profile a fixture that mentions none gets.
/// </summary>
public sealed class TeamTacticalProfile
{
    public string Name { get; init; } = "Balanced";

    /// <summary>
    /// How directly the team wants to attack, in 0–1. It gates the counter-attack transition (A3.2) and,
    /// since A9.1, contributes the same forward-pass preference A4.4 already gives a player's own
    /// direct-pass tendency — weighted around the neutral 0.50, so a balanced profile adds nothing.
    /// </summary>
    public float Directness01 { get; init; } = 0.50f;

    /// <summary>How quickly the team releases the ball after receiving it (A4.2 pass trigger).</summary>
    public float Tempo01 { get; init; } = 0.55f;

    /// <summary>How wide the attacking shape is: width jobs, the anchor's width scale, run lanes.</summary>
    public float AttackingWidth01 { get; init; } = 0.65f;

    /// <summary>How high the back line sits (A6.3 band, keeper sweep line, shape anchors).</summary>
    public float DefensiveLineHeight01 { get; init; } = 0.50f;

    /// <summary>How tightly the out-of-possession shape compresses toward the ball and the goal.</summary>
    public float Compactness01 { get; init; } = 0.60f;

    /// <summary>How hard the side presses (A6.1/A6.2 press policy, turnover response, counter-press gate).</summary>
    public float PressingIntensity01 { get; init; } = 0.55f;

    /// <summary>How much risk the side tolerates in a pass lane (A4.4) and in the shot/carry split.</summary>
    public float RiskTolerance01 { get; init; } = 0.50f;

    /// <summary>How long after a turnover the side keeps counter-pressing (A3.2, a maximum window).</summary>
    public float CounterPressDurationSeconds { get; init; } = 3.5f;

    /// <summary>How far a player may stray from his formation anchor when taking up a support spot (F18).</summary>
    public float PositionalFreedom01 { get; init; } = 0.55f;

    /// <summary>Whether the keeper looks short first (A6.5b) or always goes long.</summary>
    public bool ShortGoalkeeperDistribution { get; init; } = true;

    /// <summary>
    /// A9.1: how man-oriented the marking is, in 0–1. It is the explicit third term of A6.3's zone/man mix
    /// (<see cref="AI.DefensiveLineModel.ManShare01"/>) and the standoff a marker holds from his man
    /// (<see cref="AI.DefensiveLineModel.MarkerStandoffMeters"/>). It exists because "press hard" and
    /// "pick up a man" are different statements: a pressing side can stay zonal, and a moderately pressing
    /// side can mark aggressively. The default 0.50 is the neutral value of a bias — it adds exactly zero
    /// to the share and leaves the 1.9 m standoff untouched, so every pre-A9.1 profile keeps its behaviour.
    /// </summary>
    public float ManOrientation01 { get; init; } = 0.50f;

    /// <summary>
    /// Positional possession: stretch the pitch, keep the ball, squeeze up behind it. The pre-A9.1 default.
    /// </summary>
    public static TeamTacticalProfile PossessionControl => new()
    {
        Name = "Possession / Positional Play",
        Directness01 = 0.25f,
        Tempo01 = 0.55f,
        AttackingWidth01 = 0.82f,
        DefensiveLineHeight01 = 0.68f,
        Compactness01 = 0.75f,
        PressingIntensity01 = 0.70f,
        RiskTolerance01 = 0.40f,
        CounterPressDurationSeconds = 4.5f,
        PositionalFreedom01 = 0.65f,
        ShortGoalkeeperDistribution = true
    };

    /// <summary>
    /// Direct play: go long and early from a mid-block, with no short build-up. Distinct from
    /// <see cref="DirectCounterattack"/> by <i>where</i> it wins the ball and how compact it stays, not by
    /// how directly it plays — a direct side defends in a mid-block, a counter-attacking side defends deep.
    /// </summary>
    public static TeamTacticalProfile Direct => new()
    {
        Name = "Direct",
        Directness01 = 0.92f,
        Tempo01 = 0.82f,
        AttackingWidth01 = 0.50f,
        DefensiveLineHeight01 = 0.58f,
        Compactness01 = 0.55f,
        PressingIntensity01 = 0.55f,
        RiskTolerance01 = 0.66f,
        CounterPressDurationSeconds = 2.5f,
        PositionalFreedom01 = 0.45f,
        ShortGoalkeeperDistribution = false,
        ManOrientation01 = 0.45f
    };

    /// <summary>
    /// Counter-attacking from a deep block: defend deep and compact, concede the ball, then break quickly.
    /// The pre-A9.1 away default of the tactical gate; its numbers are unchanged.
    /// </summary>
    public static TeamTacticalProfile DirectCounterattack => new()
    {
        Name = "Direct Counterattack",
        Directness01 = 0.85f,
        Tempo01 = 0.85f,
        AttackingWidth01 = 0.52f,
        DefensiveLineHeight01 = 0.28f,
        Compactness01 = 0.85f,
        PressingIntensity01 = 0.35f,
        RiskTolerance01 = 0.75f,
        CounterPressDurationSeconds = 2.0f,
        PositionalFreedom01 = 0.40f,
        ShortGoalkeeperDistribution = false
    };

    /// <summary>
    /// High press / gegenpressing: squeeze high, press hard, counter-press for a long window. The pre-A9.1
    /// third preset. Its marking stays the pressing-driven mix (A6.3's derived share), not man-oriented.
    /// </summary>
    public static TeamTacticalProfile HighPressGegenpress => new()
    {
        Name = "High-Press Gegenpressing",
        Directness01 = 0.65f,
        Tempo01 = 0.90f,
        AttackingWidth01 = 0.60f,
        DefensiveLineHeight01 = 0.82f,
        Compactness01 = 0.88f,
        PressingIntensity01 = 0.92f,
        RiskTolerance01 = 0.68f,
        CounterPressDurationSeconds = 5.5f,
        PositionalFreedom01 = 0.60f,
        ShortGoalkeeperDistribution = true
    };

    /// <summary>
    /// Low block: sit deep and very compact, hardly press, take no risks, and clear the ball long when it
    /// arrives. It is the mirror of <see cref="HighPressGegenpress"/> on every defensive axis, and it is
    /// deliberately deeper and more passive than <see cref="DirectCounterattack"/> (12 % line height against
    /// 28 %, minimal press and counter-press windows, lowest risk tolerance): a low block defends its box,
    /// a counter-attacking side defends the middle third and breaks. Its directness stays below the
    /// counter-attack transition threshold, so winning the ball settles the side into its shape rather than
    /// launching a counter — it still clears long (a forward ball is worth slightly more). The standoff is
    /// looser than neutral: a deep block defends space, not men.
    /// </summary>
    public static TeamTacticalProfile LowBlock => new()
    {
        Name = "Low Block",
        Directness01 = 0.60f,
        Tempo01 = 0.38f,
        AttackingWidth01 = 0.42f,
        DefensiveLineHeight01 = 0.12f,
        Compactness01 = 0.95f,
        PressingIntensity01 = 0.15f,
        RiskTolerance01 = 0.22f,
        CounterPressDurationSeconds = 0.8f,
        PositionalFreedom01 = 0.28f,
        ShortGoalkeeperDistribution = false,
        ManOrientation01 = 0.35f
    };

    /// <summary>
    /// Man-oriented press: press hard <i>and</i> pick up opponents man-for-man, which the derived zone/man
    /// mix cannot express — at this pressing and compactness a zonal side marks two, this side marks three
    /// and stands 0.36 m tighter to each of them (A9.1's <see cref="ManOrientation01"/>).
    /// </summary>
    public static TeamTacticalProfile ManOriented => new()
    {
        Name = "Man-Oriented Press",
        Directness01 = 0.55f,
        Tempo01 = 0.62f,
        AttackingWidth01 = 0.58f,
        DefensiveLineHeight01 = 0.62f,
        Compactness01 = 0.68f,
        PressingIntensity01 = 0.85f,
        RiskTolerance01 = 0.55f,
        CounterPressDurationSeconds = 4.5f,
        PositionalFreedom01 = 0.45f,
        ShortGoalkeeperDistribution = true,
        ManOrientation01 = 0.90f
    };
}

public sealed class PreMatchConfig
{
    public ulong Seed { get; init; } = 20260818UL;
    public int PlayersPerTeam { get; init; } = 11;
    public float HalfDurationSeconds { get; init; } = 2700.0f;
    public int? DesignatedHumanPlayerId { get; init; } = null;
    public TeamTacticalProfile HomeProfile { get; init; } = TeamTacticalProfile.PossessionControl;
    public TeamTacticalProfile AwayProfile { get; init; } = TeamTacticalProfile.DirectCounterattack;

    /// <summary>
    /// AI runtime settings for this match. The default reproduces the behaviour that preceded the
    /// settings object, so an existing caller that does not mention it keeps its exact behaviour.
    /// </summary>
    public AiRuntimeConfig Ai { get; init; } = AiRuntimeConfig.Default;
}

public readonly record struct RecordedTickCommands(int Tick, Dictionary<int, FootballerCommand> CommandsByPlayer);
