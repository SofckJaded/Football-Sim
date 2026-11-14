using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;

namespace FootballSim.AI;

/// <summary>
/// The defensive line's shared depth for the current picture (A6.3).
///
/// Before this each defender's depth came from his own formation anchor and nothing else: the four of them
/// stood wherever the anchor model happened to put them, so the line was only a line by accident, and no
/// picture-level fact (is the passer being pressed? is somebody running in behind? can the keeper cover
/// the space behind?) moved it. A6.3 asks for one band the whole line stands in, derived from those facts,
/// with a bounded range so a local exception can still be a local exception.
///
/// All projections are attack-relative metres (<c>proj = x · attackDirX</c>): the team's own goal line is
/// at <c>-halfLength</c> and its opponent's at <c>+halfLength</c>, so a *smaller* projection is deeper.
/// </summary>
public readonly record struct DefensiveLineBand(
    bool IsValid,
    float ProjX,
    float MinProjX,
    float MaxProjX,
    float AnchorMeanProjX,
    float FieldPosition01,
    float PasserPressure01,
    float RunThreat01,
    float KeeperCoverage01,
    float FieldShiftMeters,
    float PressureShiftMeters,
    float BallCapShiftMeters,
    float RunShiftMeters,
    float KeeperShiftMeters,
    int DeepestRunnerId,
    float DeepestRunnerProjX,
    int KeeperId,
    float KeeperReachMeters)
{
    /// <summary>No usable band: the team is not defending, or it has no line to place.</summary>
    public static DefensiveLineBand None => default;

    /// <summary>
    /// True when the picture contains a credible run in behind, i.e. the runner cap — not a shift term —
    /// decided the band's ceiling. Diagnostics use it to explain a line that did not step up.
    /// </summary>
    public bool HasCredibleRunner => DeepestRunnerId > 0;

    /// <summary>Compact text form for a probe or a failure message.</summary>
    public string Describe() => !IsValid
        ? "no band"
        : $"line {ProjX:F1} m in [{MinProjX:F1}, {MaxProjX:F1}] from anchors {AnchorMeanProjX:F1}" +
          $" · field {FieldShiftMeters:+0.0;-0.0} · pressure {PasserPressure01:F2} ({PressureShiftMeters:+0.0;-0.0})" +
          $" · ball cap {BallCapShiftMeters:+0.0;-0.0}" +
          $" · run {(HasCredibleRunner ? $"#{DeepestRunnerId} at {DeepestRunnerProjX:F1} ({RunShiftMeters:+0.0;-0.0})" : "none")}" +
          $" · keeper #{KeeperId} reach {KeeperReachMeters:F1} m ({KeeperShiftMeters:+0.0;-0.0})";
}

/// <summary>
/// A6.3: one defensive line depth for the whole team, from four facts the team can actually observe, plus
/// the stylistic mix of zone and man responsibility.
///
/// The four inputs, each with one stated job:
/// <list type="bullet">
/// <item><b>field position</b> — the ball's own depth moves the line. The formation anchor already carries a
///   weak version of this (a line tied to the halfway line rather than to the ball); the band completes it,
///   so the side drops when the ball comes into its own third and squeezes up when it goes upfield.</item>
/// <item><b>passer pressure</b> — a pressured carrier is likely to play backwards or lose it, so the line
///   steps up; a carrier with time can play the ball in behind, so the line holds or drops. The pressure
///   itself is the reach model's answer for the best-placed presser (<see cref="TimeToReachModel"/>), not a
///   distance threshold: that keeps A6's one arrival estimate the only one.</item>
/// <item><b>credible runs</b> — the deepest believed opponent who is actually running at this goal caps how
///   high the line may stand, through <see cref="LineToRunnerMeters"/>. It is a cap and not a shift because
///   "somebody is running in behind" is a hard ceiling, not a preference. Age and confidence decide
///   credibility: a memory of a run from a second ago is still a run.</item>
/// <item><b>keeper sweep coverage</b> — a line may only be as high as the space behind it allows. The
///   keeper's own reach over <see cref="SweepBudgetSeconds"/> sets a second cap, which is what makes a high
///   line a property of the keeper rather than of a profile field alone.</item>
/// </list>
///
/// <b>Knowledge boundary.</b> The band is a team-level fact built the way the allocator already works:
/// opponents come only from <see cref="TeamBelief"/> (what this team has seen), the team's own players are
/// its own state, and the keeper is read from his own body because he is on the same side. No opponent
/// intent, hidden attribute or live position is used, and nothing here reads another controller.
/// </summary>
public static class DefensiveLineModel
{
    // ---------------------------------------------------------------------------------------------
    // Field position.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How far the line follows the ball, as a fraction of the ball's own projection. The formation anchor
    /// already follows it at about 0.22; this is the rest of the way to a ball-relative line.
    /// </summary>
    public const float FieldSlope = 0.18f;

    /// <summary>Largest depth the field-position term may move the line, in metres.</summary>
    public const float FieldShiftMaxMeters = 6f;

    /// <summary>
    /// How far ahead of the ball the line may ever stand, in metres. Level with the ball is a compact block;
    /// in front of it while it is near this team's own goal line is not defending at all.
    /// </summary>
    public const float AheadOfBallToleranceMeters = 2f;

    // ---------------------------------------------------------------------------------------------
    // Passer pressure.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Time-to-reach below which a carrier counts as fully pressed, in seconds.</summary>
    public const float PressedSeconds = 0.45f;

    /// <summary>Time-to-reach above which a carrier counts as completely free, in seconds.</summary>
    public const float FreeSeconds = 1.40f;

    /// <summary>
    /// How far the pressure term may move the line either way, in metres. A free passer drops it by this
    /// much and a pressed one raises it by the same, because both are bets on what the next ball is.
    /// </summary>
    public const float PressurePushMeters = 4f;

    /// <summary>Own players further than this from the believed carrier are not candidates to press him.</summary>
    private const float PressureCandidateRangeMeters = 26f;

    // ---------------------------------------------------------------------------------------------
    // Credible runs.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Age beyond which a remembered run stops being credible, in ticks (one second).</summary>
    public const int CredibleRunnerAgeTicks = 60;

    /// <summary>Speed at which an opponent counts as running at this goal, in metres per second.</summary>
    public const float MinRunSpeedMeters = 1.5f;

    /// <summary>Speed at which the threat of a run saturates, in metres per second.</summary>
    public const float RunSpeedSaturationMeters = 6f;

    /// <summary>Credibility a believed runner must reach before he caps the line.</summary>
    public const float MinRunCredibility = 0.20f;

    /// <summary>
    /// How far goal-side of the deepest runner the line stays, in metres. The line must not step up level
    /// with a man already running at the goal: that is the ball in behind that the offside trap loses.
    /// </summary>
    public const float LineToRunnerMeters = 2.5f;

    /// <summary>
    /// How far behind the shape's own opinion a runner may stand and still count as a threat to the line,
    /// in metres. An opponent already clearly behind the line is offside and cannot be played through; only
    /// the man between the line and the ball is the through-ball threat.
    /// </summary>
    public const float RunnerBehindToleranceMeters = 6f;

    /// <summary>
    /// How much of the ball's own line a runner may be ahead of and still be a through-ball threat, in
    /// metres. A man ahead of the ball cannot be played in behind: a pass to him travels away from this goal.
    /// </summary>
    public const float RunnerAheadOfBallToleranceMeters = 1f;

    // ---------------------------------------------------------------------------------------------
    // Keeper coverage.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Time the keeper is assumed to have to reach a ball played behind the line, in seconds.</summary>
    public const float SweepBudgetSeconds = 3f;

    /// <summary>Metres between keeper-reach probes; the scan stops at the first unreachable probe.</summary>
    private const float KeeperProbeStepMeters = 6f;

    /// <summary>Number of probes in the keeper's reach scan (36 m ahead of where he stands).</summary>
    private const int KeeperProbeCount = 6;

    /// <summary>
    /// Metres of slack between the keeper's measured sweep reach and the line he may stand behind. A
    /// defender still has his own recovery run and the crossbar behind him; the keeper is the last resort,
    /// not the only cover. The number is measured, not chosen in the abstract: a keeper covers 17.5 m in the
    /// 3 s budget from a mean depth of -49 m, so at 16 m of slack the term bound on 42 % of defending samples
    /// and pulled the line 3.8 m deeper than the shape it replaced — a goalkeeper deciding the block rather
    /// than covering it. At 24 m it binds only for a deliberately high line, which is the property the plan
    /// asks for (a high line needs a sweeper keeper), while leaving ordinary defending to the other three
    /// inputs.
    /// </summary>
    public const float KeeperSlackMeters = 24f;

    /// <summary>Reach at which the keeper's coverage reads as complete, in metres. Diagnostics only.</summary>
    public const float KeeperReferenceMeters = 30f;

    // ---------------------------------------------------------------------------------------------
    // Bounds and local exceptions.
    // ---------------------------------------------------------------------------------------------

    /// <summary>How close to his own goal line the band's floor sits, in metres.</summary>
    public const float OwnGoalSafetyMeters = 5f;

    /// <summary>How far a line player on the ball's side of the pitch may step ahead of the band, in metres.</summary>
    public const float BallSideAdvanceMeters = 3f;

    /// <summary>Lateral distance from the ball inside which a line player counts as ball-side, in metres.</summary>
    public const float BallSideLaneMeters = 12f;

    /// <summary>How far a line player on the far side of the pitch tucks behind the band, in metres.</summary>
    public const float FarSideTuckMeters = 2f;

    /// <summary>Lateral distance from the ball beyond which a line player counts as far-side, in metres.</summary>
    public const float FarSideLaneMeters = 26f;

    /// <summary>How far a line player's own lateral lane may drift from his slot, in metres.</summary>
    private const float MaxLateralDriftMeters = 6f;

    // ---------------------------------------------------------------------------------------------
    // Style: the zone/man mix.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How man-oriented this profile is, in 0–1. Pressing intensity is the main term — a side that presses
    /// has to pick up runners — and compactness is the second: a compact side can afford to mark because the
    /// space it leaves behind is small. A deep, non-pressing side stays zonal.
    /// </summary>
    public static float ManShare01(in TeamTacticalProfile profile) => Clamp01(
        0.35f +
        0.90f * (Clamp01(profile.PressingIntensity01) - 0.5f) +
        0.50f * (Clamp01(profile.Compactness01) - 0.5f));

    /// <summary>
    /// How many opponents this profile marks man-for-man, 1–3. Two thresholds rather than a rounding, so the
    /// shipped presets land on distinct numbers: direct counterattack 1 (zonal), possession 2, gegenpress 3.
    /// </summary>
    public static int MarkerCount(in TeamTacticalProfile profile)
    {
        float share = ManShare01(profile);
        return 1 + (share >= 0.50f ? 1 : 0) + (share >= 0.85f ? 1 : 0);
    }

    // ---------------------------------------------------------------------------------------------
    // The band.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Evaluates the band for one team. Returns <see cref="DefensiveLineBand.None"/> when the team has no
    /// outfield line player at all (a fragment, an eleven of strikers): there is nothing to give a depth to.
    /// </summary>
    public static DefensiveLineBand Evaluate(
        TeamId team,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        BallPossessionSnapshot possession,
        TeamBelief opponents,
        TeamTacticalProfile profile,
        float attackDirX,
        PitchDimensions pitch)
    {
        float anchorSum = 0f;
        int lineCount = 0;
        FootballerState? keeper = null;

        for (int i = 0; i < players.Count; i++)
        {
            FootballerState player = players[i];
            if (player.Team != team || !player.IsActiveOnPitch) continue;

            if (player.Role == TacticalRole.Goalkeeper)
            {
                keeper = player;
                continue;
            }

            if (!HoldsLineSlot(player.Role)) continue;

            Vector3 anchor = player.TacticalAnchor == Vector3.Zero ? player.Position : player.TacticalAnchor;
            anchorSum += anchor.X * attackDirX;
            lineCount++;
        }

        if (lineCount == 0) return DefensiveLineBand.None;

        float anchorMean = anchorSum / lineCount;
        float ballProj = ball.Position.X * attackDirX;
        float minProj = -pitch.HalfLength + OwnGoalSafetyMeters;

        // Field position: the ball's own depth, bounded so a ball on either byline cannot drag the line a
        // third of the pitch.
        float fieldShift = Math.Clamp(FieldSlope * ballProj, -FieldShiftMaxMeters, FieldShiftMaxMeters);

        // Passer pressure: the one arrival estimate, asked about the best-placed presser.
        float pressure01 = PasserPressure01(players, team, possession, opponents, attackDirX);
        float pressureShift = PressurePushMeters * (pressure01 - 0.5f) * 2f;

        float target = anchorMean + fieldShift + pressureShift;

        // Credible runs cap the line before anything else does.
        float runThreat01 = 0f;
        int runnerId = 0;
        float runnerProj = 0f;
        float maxProj = ballProj + AheadOfBallToleranceMeters;
        if (TryDeepestRunner(opponents, attackDirX, ballProj, anchorMean, out runnerId, out runnerProj, out runThreat01))
        {
            float runCap = runnerProj - LineToRunnerMeters;
            if (runCap < maxProj) maxProj = runCap;
        }

        float beforeRun = MathF.Min(target, ballProj + AheadOfBallToleranceMeters);
        float ballCapShift = beforeRun - target;
        float afterRun = MathF.Min(beforeRun, maxProj);
        float runShift = afterRun - beforeRun;

        // Keeper coverage caps what the space behind the line can be. No keeper in the fixture is not a
        // keeper who cannot sweep: the cap simply does not apply.
        float keeperReach = KeeperReach(keeper, attackDirX);
        float keeperProj = 0f;
        float beforeKeeper = afterRun;
        float afterKeeper = afterRun;
        if (keeper is { IsActiveOnPitch: true })
        {
            keeperProj = keeper.Position.X * attackDirX;
            float keeperCap = keeperProj + keeperReach + KeeperSlackMeters;
            if (keeperCap < maxProj) maxProj = keeperCap;
            afterKeeper = MathF.Min(beforeKeeper, keeperCap);
        }

        float keeperShift = afterKeeper - beforeKeeper;

        if (maxProj < minProj) maxProj = minProj;

        float proj = Math.Clamp(afterKeeper, minProj, maxProj);
        float field01 = Clamp01(0.5f + ballProj / (2f * pitch.HalfLength));
        float keeperCoverage01 = Clamp01(keeperReach / KeeperReferenceMeters);

        return new DefensiveLineBand(
            IsValid: true,
            ProjX: proj,
            MinProjX: minProj,
            MaxProjX: maxProj,
            AnchorMeanProjX: anchorMean,
            FieldPosition01: field01,
            PasserPressure01: pressure01,
            RunThreat01: runThreat01,
            KeeperCoverage01: keeperCoverage01,
            FieldShiftMeters: fieldShift,
            PressureShiftMeters: pressureShift,
            BallCapShiftMeters: ballCapShift,
            RunShiftMeters: runShift,
            KeeperShiftMeters: keeperShift,
            DeepestRunnerId: runnerId,
            DeepestRunnerProjX: runnerProj,
            KeeperId: keeper?.Id ?? 0,
            KeeperReachMeters: keeperReach);
    }

    /// <summary>
    /// The depth one line player stands at inside the band. The band is shared; the exceptions are local and
    /// bounded: the ball-side defender may step up to <see cref="BallSideAdvanceMeters"/> to meet the ball,
    /// the far-side one tucks in by <see cref="FarSideTuckMeters"/>, and neither may leave the band's own
    /// range — so a local exception can never defeat the credible-run or keeper cap that set the range.
    /// </summary>
    public static float ProjFor(in DefensiveLineBand band, float playerZ, float ballZ)
    {
        if (!band.IsValid) return 0f;

        float lateral = MathF.Abs(playerZ - ballZ);
        float proj = band.ProjX;
        if (lateral <= BallSideLaneMeters) proj += BallSideAdvanceMeters;
        else if (lateral >= FarSideLaneMeters) proj -= FarSideTuckMeters;

        return Math.Clamp(proj, band.MinProjX, band.MaxProjX);
    }

    /// <summary>
    /// Where a line player's own slot sits laterally, allowing for the few metres the shape may drift, so a
    /// defender who has stepped across does not flip his local exception every recompute.
    /// </summary>
    public static float ProjForPlayer(in DefensiveLineBand band, FootballerState player, Vector2 ballXZ)
    {
        Vector3 anchor = player.TacticalAnchor == Vector3.Zero ? player.Position : player.TacticalAnchor;
        float drift = MathF.Abs(player.Position.Z - anchor.Z);
        float anchorZ = anchor.Z;
        if (drift > MaxLateralDriftMeters)
        {
            // He is genuinely playing elsewhere (a pulled-across full-back): his body is the honest lane.
            anchorZ = player.Position.Z;
        }

        float ballZ = ballXZ.Y;
        return ProjFor(band, anchorZ, ballZ);
    }

    /// <summary>Roles that hold a place in the defensive line.</summary>
    public static bool HoldsLineSlot(TacticalRole role) =>
        role is TacticalRole.CenterBack or TacticalRole.FullBack or TacticalRole.DefensiveMidfielder;

    // ---------------------------------------------------------------------------------------------
    // Inputs.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How hard the ball carrier is being pressed right now, 0–1, from the best-placed of this team's own
    /// players. 0.5 is "cannot say" (no carrier, or one nobody on this team has seen), which is the value
    /// that leaves the line where the anchors put it.
    /// </summary>
    private static float PasserPressure01(
        IReadOnlyList<FootballerState> players,
        TeamId team,
        BallPossessionSnapshot possession,
        TeamBelief opponents,
        float attackDirX)
    {
        if (possession.State != BallControlState.Controlled ||
            possession.PossessionTeam == team ||
            possession.ControllingPlayerId is not int carrierId)
        {
            return 0.5f;
        }

        if (!TryBelieved(opponents, carrierId, out TeamBelief.Opponent carrier)) return 0.35f;

        float best = float.MaxValue;
        for (int i = 0; i < players.Count; i++)
        {
            FootballerState player = players[i];
            if (player.Team != team || !player.IsActiveOnPitch) continue;
            if (player.Role == TacticalRole.Goalkeeper) continue;

            Vector2 selfXZ = new(player.Position.X, player.Position.Z);
            if (Vector2.Distance(selfXZ, carrier.PositionXZ) > PressureCandidateRangeMeters) continue;

            ReachEstimate estimate = TimeToReachModel.ToMovingPoint(
                ReachKinematics.From(player), carrier.PositionXZ, carrier.VelocityXZ,
                horizonSeconds: FreeSeconds);

            if (estimate.Seconds < best) best = estimate.Seconds;
        }

        if (best == float.MaxValue) return 0.25f;

        return Clamp01((FreeSeconds - best) / (FreeSeconds - PressedSeconds));
    }

    /// <summary>
    /// The deepest believed opponent who is a through-ball threat: between this team's line and the ball,
    /// running at this goal, and credibly seen. Also reports how strong that threat is (speed and the
    /// confidence of the sighting).
    ///
    /// A man ahead of the ball cannot be played in behind — a pass to him travels away from this goal — and
    /// a man already clearly behind the line is offside and cannot be played either. Both are excluded, so
    /// the cap only ever reacts to the striker on the last shoulder, which is the player the line is for.
    /// </summary>
    private static bool TryDeepestRunner(
        TeamBelief opponents,
        float attackDirX,
        float ballProj,
        float lineReferenceProj,
        out int runnerId,
        out float runnerProj,
        out float threat01)
    {
        runnerId = 0;
        runnerProj = 0f;
        threat01 = 0f;

        bool found = false;
        float deepest = float.MaxValue;
        float strongest = 0f;
        float behindFloor = lineReferenceProj - RunnerBehindToleranceMeters;
        float aheadCeiling = ballProj + RunnerAheadOfBallToleranceMeters;

        IReadOnlyList<TeamBelief.Opponent> believed = opponents.Opponents;
        for (int i = 0; i < believed.Count; i++)
        {
            TeamBelief.Opponent opponent = believed[i];
            if (opponent.Role == TacticalRole.Goalkeeper) continue;
            if (opponent.AgeTicks > CredibleRunnerAgeTicks) continue;

            // The opponent attacks the other way, so his progress toward this goal is his velocity along
            // this team's attacking direction, negated.
            float runSpeed = opponent.VelocityXZ.X * -attackDirX;
            if (runSpeed < MinRunSpeedMeters) continue;

            float proj = opponent.PositionXZ.X * attackDirX;
            if (proj > aheadCeiling) continue;
            if (proj < behindFloor) continue;

            float credibility = Clamp01(runSpeed / RunSpeedSaturationMeters) *
                                Clamp01(opponent.Confidence01);
            if (credibility < MinRunCredibility) continue;

            found = true;
            if (credibility > strongest) strongest = credibility;
            if (proj < deepest)
            {
                deepest = proj;
                runnerId = opponent.PlayerId;
                runnerProj = proj;
            }
        }

        threat01 = strongest;
        return found;
    }

    /// <summary>
    /// How far in front of where he stands the keeper can arrive within <see cref="SweepBudgetSeconds"/>,
    /// straight up the pitch, through the one reach model. Zero when there is no keeper in the picture.
    /// </summary>
    private static float KeeperReach(FootballerState? keeper, float attackDirX)
    {
        if (keeper is not { IsActiveOnPitch: true }) return 0f;

        ReachKinematics kinematics = ReachKinematics.From(keeper);
        float keeperProj = keeper.Position.X * attackDirX;
        float best = 0f;

        for (int probe = 1; probe <= KeeperProbeCount; probe++)
        {
            float distance = probe * KeeperProbeStepMeters;
            Vector2 target = new(attackDirX * (keeperProj + distance), 0f);
            ReachEstimate estimate = TimeToReachModel.ToPoint(
                kinematics, target, horizonSeconds: SweepBudgetSeconds);
            if (!estimate.Reachable) break;
            best = distance;
        }

        return best;
    }

    private static bool TryBelieved(TeamBelief belief, int playerId, out TeamBelief.Opponent opponent)
    {
        IReadOnlyList<TeamBelief.Opponent> believed = belief.Opponents;
        for (int i = 0; i < believed.Count; i++)
        {
            if (believed[i].PlayerId != playerId) continue;
            opponent = believed[i];
            return true;
        }

        opponent = default;
        return false;
    }

    private static float Clamp01(float value) =>
        !float.IsFinite(value) ? 0f : value < 0f ? 0f : value > 1f ? 1f : value;
}
