using System;
using System.Collections.Generic;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>One goal: minute, side, scorer and (optional) assist.</summary>
/// <param name="Minute">Match minute, 1..90.</param>
/// <param name="IsHome">True when the scoring side is the home side.</param>
/// <param name="ScorerId">The scorer.</param>
/// <param name="AssistId">The assisting team-mate, or <see cref="PlayerId.None"/>.</param>
public sealed record GoalEvent(int Minute, bool IsHome, PlayerId ScorerId, PlayerId AssistId);

/// <summary>One card: minute, side, player and whether it was a red card.</summary>
/// <param name="Minute">Match minute, 1..90.</param>
/// <param name="IsHome">True when the carded player is on the home side.</param>
/// <param name="PlayerId">The carded player.</param>
/// <param name="IsRed">True for a red card, false for a yellow.</param>
public sealed record CardEvent(int Minute, bool IsHome, PlayerId PlayerId, bool IsRed);

/// <summary>
/// One in-match injury event. The quick sim never mutates the player: the season runner (or the W3
/// lifecycle) is the applier that records the injury on the <see cref="WorldPlayer"/>.
/// </summary>
/// <param name="Minute">Match minute, 1..90.</param>
/// <param name="IsHome">True when the injured player is on the home side.</param>
/// <param name="PlayerId">The injured player.</param>
/// <param name="Kind">Injury kind.</param>
/// <param name="Severity">Injury severity.</param>
/// <param name="WeeksOut">Weeks the player is expected to miss (1..26).</param>
public sealed record InjuryEvent(
    int Minute,
    bool IsHome,
    PlayerId PlayerId,
    InjuryKind Kind,
    InjurySeverity Severity,
    int WeeksOut);

/// <summary>One substitution: minute, side, the player coming off and the player coming on.</summary>
/// <param name="Minute">Match minute, 60..82.</param>
/// <param name="IsHome">True when the substitution is made by the home side.</param>
/// <param name="OffPlayerId">The starter coming off.</param>
/// <param name="OnPlayerId">The bench player coming on.</param>
public sealed record SubstitutionEvent(int Minute, bool IsHome, PlayerId OffPlayerId, PlayerId OnPlayerId);

/// <summary>One participant's match line. Ratings are 4.0..10.0.</summary>
public sealed record PlayerMatchLine(
    PlayerId PlayerId,
    int Minutes,
    int Goals,
    int Assists,
    int Shots,
    int ShotsOnTarget,
    int PassesAttempted,
    int PassesCompleted,
    int Tackles,
    int Saves,
    int YellowCards,
    int RedCards,
    float Rating,
    float EndingStamina01,
    float ConditionCost01);

/// <summary>One team's aggregate match statistics.</summary>
/// <param name="PossessionPercent">Possession percentage; the two sides always sum to 100.</param>
/// <param name="Shots">Shots attempted.</param>
/// <param name="ShotsOnTarget">Shots on target (goals are a subset).</param>
/// <param name="Corners">Corners won.</param>
/// <param name="Fouls">Fouls committed.</param>
/// <param name="Offsides">Offsides caught.</param>
public sealed record QuickSimTeamStats(
    int PossessionPercent,
    int Shots,
    int ShotsOnTarget,
    int Corners,
    int Fouls,
    int Offsides);

/// <summary>
/// The complete result of one quick-simulated match: scoreline, shoot-out (cup ties only), event log,
/// per-player lines for everyone who took the field and aggregate team statistics.
/// </summary>
public sealed class QuickSimResult
{
    /// <summary>Home club.</summary>
    public ClubId HomeClubId { get; init; }

    /// <summary>Away club.</summary>
    public ClubId AwayClubId { get; init; }

    /// <summary>Home goals in normal time.</summary>
    public int HomeGoals { get; init; }

    /// <summary>Away goals in normal time.</summary>
    public int AwayGoals { get; init; }

    /// <summary>True when a level knockout tie was decided by a shoot-out.</summary>
    public bool WentToPenalties { get; init; }

    /// <summary>Home shoot-out goals; 0 unless <see cref="WentToPenalties"/>.</summary>
    public int HomePenaltyGoals { get; init; }

    /// <summary>Away shoot-out goals; 0 unless <see cref="WentToPenalties"/>.</summary>
    public int AwayPenaltyGoals { get; init; }

    /// <summary>Every goal, in ascending minute order.</summary>
    public IReadOnlyList<GoalEvent> Goals { get; init; } = Array.Empty<GoalEvent>();

    /// <summary>Every card, in ascending minute order.</summary>
    public IReadOnlyList<CardEvent> Cards { get; init; } = Array.Empty<CardEvent>();

    /// <summary>Every in-match injury, in ascending minute order.</summary>
    public IReadOnlyList<InjuryEvent> Injuries { get; init; } = Array.Empty<InjuryEvent>();

    /// <summary>Every substitution, in ascending minute order.</summary>
    public IReadOnlyList<SubstitutionEvent> Substitutions { get; init; } = Array.Empty<SubstitutionEvent>();

    /// <summary>Home participants who took the field (starting XI plus substitutes who came on).</summary>
    public IReadOnlyList<PlayerMatchLine> HomePlayers { get; init; } = Array.Empty<PlayerMatchLine>();

    /// <summary>Away participants who took the field (starting XI plus substitutes who came on).</summary>
    public IReadOnlyList<PlayerMatchLine> AwayPlayers { get; init; } = Array.Empty<PlayerMatchLine>();

    /// <summary>Home team statistics.</summary>
    public QuickSimTeamStats HomeStats { get; init; } = new(50, 0, 0, 0, 0, 0);

    /// <summary>Away team statistics.</summary>
    public QuickSimTeamStats AwayStats { get; init; } = new(50, 0, 0, 0, 0, 0);

    /// <summary>
    /// The winning club: the higher score, then the shoot-out winner for a level knockout tie, or
    /// <see cref="ClubId.None"/> for a drawn league match.
    /// </summary>
    public ClubId WinnerClubId
    {
        get
        {
            if (HomeGoals != AwayGoals) return HomeGoals > AwayGoals ? HomeClubId : AwayClubId;
            if (!WentToPenalties) return ClubId.None;
            return HomePenaltyGoals > AwayPenaltyGoals ? HomeClubId : AwayClubId;
        }
    }

    /// <summary>Looks one participant's match line up across both sides; null when he did not play.</summary>
    public PlayerMatchLine? LineOf(PlayerId playerId)
    {
        for (int i = 0; i < HomePlayers.Count; i++)
        {
            if (HomePlayers[i].PlayerId == playerId) return HomePlayers[i];
        }

        for (int i = 0; i < AwayPlayers.Count; i++)
        {
            if (AwayPlayers[i].PlayerId == playerId) return AwayPlayers[i];
        }

        return null;
    }
}

/// <summary>Aggregated team strength from the selected XI (exposed so calibration tests can reason about it).</summary>
/// <param name="Attack">Attacking unit, 0..100.</param>
/// <param name="Midfield">Midfield unit, 0..100.</param>
/// <param name="Defence">Defensive unit, 0..100.</param>
/// <param name="Goalkeeping">Goalkeeping unit, 0..100.</param>
/// <param name="Overall">Mean of the four units, 0..100.</param>
public readonly record struct TeamRatings(
    float Attack,
    float Midfield,
    float Defence,
    float Goalkeeping,
    float Overall);

/// <summary>
/// Tier-2 match resolution (plan §5.3): a deterministic, allocation-light quick simulation of one
/// background fixture. The only randomness source is the supplied <see cref="WorldRng"/>; nothing here
/// mutates the world, a player or the fixture, so the season runner can replay a fixture exactly by
/// re-deriving the same fork of the quick-sim stream.
///
/// <para><b>Team ratings.</b> Attack, midfield, defence and goalkeeping are weighted attribute means over
/// the selected XI's slot groups, multiplied by each player's condition/sharpness state factor and
/// tilted by recent form. The home side gets the plan's +4% on attack and defence, and each side's
/// attacking efficiency is modulated by a small, bounded tactical matchup term (see
/// <see cref="TacticalAttackModifier"/>).</para>
///
/// <para><b>Calibration deviation (documented deliberately).</b> The plan's "12-18 key possessions" is
/// implemented as a <i>per-side</i> budget (24-36 possessions per match, split by the logistic midfield
/// share): one shared pool of 12-18 possessions cannot reach the plan's own 2.3-3.1 goals-per-game band
/// with the rest of its stated conversion chain (0.55 shots * 0.46 on target * 0.28 goal chance yields
/// about one goal per match). The 0.46 on-target chance and the 0.28-base goal-chance formula are kept
/// exactly; the per-possession shot chance is instead solved from the fixture's expected-goals lambda
/// against the side's expected possession count, so the possession chain follows the strength model
/// (an even match converts ~0.65 of its key possessions into a shot and averages ~2.7 goals). A red card
/// applies the plan's ~8% ratings reduction to that side for the rest of the match; v1 deliberately does
/// not model ten men on the pitch.</para>
/// </summary>
public static class WorldQuickSim
{
    /// <summary>Lowest valid match rating.</summary>
    public const float MinimumRating = 4.0f;

    /// <summary>Highest valid match rating.</summary>
    public const float MaximumRating = 10.0f;

    // ---------------------------------------------------------------------------------------------
    // Match shape.
    // ---------------------------------------------------------------------------------------------
    private const int MinutesPerMatch = 90;
    private const int StartingSize = 11;
    private const int BenchSize = 7;
    private const int FormWindow = 8;

    // ---------------------------------------------------------------------------------------------
    // Expected goals (plan §5.3).
    // ---------------------------------------------------------------------------------------------
    private const float BaseExpectedGoals = 1.545f;
    private const float StrengthGoalExponent = 0.0225f;
    private const float MinimumExpectedGoals = 0.12f;
    private const float MaximumExpectedGoals = 5.0f;
    private const float HomeAdvantage = 1.04f;
    private const float DefenceWeight = 0.72f;
    private const float GoalkeepingWeight = 0.28f;
    private const float MidfieldLogisticScale = 7.0f;

    // ---------------------------------------------------------------------------------------------
    // Possession conversion chain.
    // ---------------------------------------------------------------------------------------------
    private const int MinimumPossessionsPerSide = 12;
    private const int MaximumPossessionsPerSideExclusive = 19;
    private const float OnTargetChance = 0.46f;
    private const float GoalChanceBase = 0.28f;
    private const float GoalChancePerRatingPoint = 0.004f;
    private const float MinimumGoalChance = 0.10f;
    private const float MaximumGoalChance = 0.62f;
    private const float MinimumShotChance = 0.30f;
    private const float MaximumShotChance = 0.85f;
    private const float AssistProbability = 0.65f;
    private const float RedCardRatingScale = 0.92f;
    private const float ShootoutConversionChance = 0.76f;
    private const int ShootoutRounds = 5;

    // The plan's expected-goals model assumes attack and defence ratings share one scale; EvaluateTeam's
    // two group formulas are not exactly centred on each other for generated squads (an attacking unit
    // rates a few points below 0.72*Defence + 0.28*Goalkeeping because generated outfielders carry little
    // role-specific shaping yet), so a pure 1.35 base with a steep 0.045 exponent yields about 1.9 goals
    // per match across a real 1,836-match season — below the plan's 2.3..3.1 band. The base/exponent are
    // therefore calibrated on the real generated world: 1.545 * exp(0.0225 * gap) reproduces ~2.5 goals
    // per match across the six divisions while still separating tiers strongly (a tier-1 attack meets a
    // tier-6 defence at a much larger exponent). The possession chain then targets 0.92 * lambda, which
    // keeps evenly matched fixtures inside the same band. Re-calibrate both numbers whenever EvaluateTeam
    // or the player generator changes its rating scales.
    private const float PossessionChainCalibration = 0.92f;

    // ---------------------------------------------------------------------------------------------
    // Discipline, injuries and substitutions.
    // ---------------------------------------------------------------------------------------------
    private const int MinimumFouls = 8;
    private const int MaximumFoulsExclusive = 15;
    private const float RedCardProbability = 0.02f;
    private const float OutfieldInjuryChance = 0.0025f;
    private const float KeeperInjuryChance = 0.0015f;
    private const float InjuryConditionMultiplier = 1.5f;
    private const int MinimumSubstitutions = 2;
    private const int MaximumSubstitutionsExclusive = 4;
    private const int MinimumSubstitutionMinute = 60;
    private const int MaximumSubstitutionMinute = 82;

    // ---------------------------------------------------------------------------------------------
    // Ratings (plan §5.3 / W2 deliverable).
    // ---------------------------------------------------------------------------------------------
    private const float BaseRating = 6.3f;
    private const float RatingPerGoal = 0.90f;
    private const float RatingPerAssist = 0.45f;
    private const float RatingPerShotOnTarget = 0.06f;
    private const float RatingPerTackle = 0.04f;
    private const float RatingPerSave = 0.10f;
    private const float RatingPerGoalConceded = 0.05f;
    private const float MaximumGoalConcededPenalty = 0.40f;
    private const float RatingWinBonus = 0.25f;
    private const float RatingDrawBonus = 0.05f;
    private const float RatingLossBonus = -0.15f;
    private const float RatingNoiseStdDev = 0.30f;

    /// <summary>
    /// Quick-simulates one fixture. Pure and deterministic: the caller supplies every input, the rng is the
    /// only randomness source, and neither the world nor the fixture is mutated.
    /// </summary>
    /// <param name="world">The world whose player registry resolves both lineups.</param>
    /// <param name="home">The home club.</param>
    /// <param name="away">The away club.</param>
    /// <param name="homeLineup">The home team, selected by <see cref="SquadSelector"/>.</param>
    /// <param name="awayLineup">The away team, selected by <see cref="SquadSelector"/>.</param>
    /// <param name="rng">The match's private random stream.</param>
    /// <param name="knockout">True for a cup tie: a level score is resolved by a penalty shoot-out.</param>
    public static QuickSimResult Simulate(
        WorldState world,
        WorldClub home,
        WorldClub away,
        SelectedLineup homeLineup,
        SelectedLineup awayLineup,
        WorldRng rng,
        bool knockout = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(away);
        ArgumentNullException.ThrowIfNull(homeLineup);
        ArgumentNullException.ThrowIfNull(awayLineup);
        ArgumentNullException.ThrowIfNull(rng);
        if (home.Id == away.Id) throw new ArgumentException("A club cannot play itself.", nameof(away));
        ValidateLineup(homeLineup, nameof(homeLineup));
        ValidateLineup(awayLineup, nameof(awayLineup));
        if (homeLineup.ClubId != home.Id)
            throw new ArgumentException($"The home lineup belongs to club {homeLineup.ClubId}.", nameof(homeLineup));
        if (awayLineup.ClubId != away.Id)
            throw new ArgumentException($"The away lineup belongs to club {awayLineup.ClubId}.", nameof(awayLineup));

        TeamTacticalProfile homeProfile = ResolveProfile(world, home, TeamTacticalProfile.PossessionControl);
        TeamTacticalProfile awayProfile = ResolveProfile(world, away, TeamTacticalProfile.DirectCounterattack);

        TeamRatings homeRatings = EvaluateTeam(world, homeLineup);
        TeamRatings awayRatings = EvaluateTeam(world, awayLineup);

        // Home advantage (plan §5.3): +4% on attack and defence for the home side.
        float homeAttack = homeRatings.Attack * HomeAdvantage;
        float homeDefence = homeRatings.Defence * HomeAdvantage;
        float awayAttack = awayRatings.Attack;
        float awayDefence = awayRatings.Defence;

        float homeLambda = ExpectedGoals(
            homeAttack,
            CombinedDefence(awayDefence, awayRatings.Goalkeeping),
            1f + TacticalAttackModifier(homeProfile, awayProfile));
        float awayLambda = ExpectedGoals(
            awayAttack,
            CombinedDefence(homeDefence, homeRatings.Goalkeeping),
            1f + TacticalAttackModifier(awayProfile, homeProfile));

        float homePossessionShare =
            1f / (1f + MathF.Exp(-(homeRatings.Midfield - awayRatings.Midfield) / MidfieldLogisticScale));

        // See the class remarks: the plan's possession budget is applied per side.
        int possessionBudget =
            2 * rng.NextInt(MinimumPossessionsPerSide, MaximumPossessionsPerSideExclusive);

        var homeSide = new MatchSide(
            world, homeLineup, isHome: true, homeProfile, homeAttack, homeDefence,
            homeRatings.Goalkeeping, homeLambda, possessionBudget * homePossessionShare);
        var awaySide = new MatchSide(
            world, awayLineup, isHome: false, awayProfile, awayAttack, awayDefence,
            awayRatings.Goalkeeping, awayLambda, possessionBudget * (1f - homePossessionShare));

        // Substitutions are decided first so that every later event names a player actually on the pitch.
        PlanSubstitutions(homeSide, rng);
        PlanSubstitutions(awaySide, rng);

        // Cards are generated before the possessions so a red card's minute can cut that side's ratings
        // for the rest of the match (plan §5.3 v1: no ten-men modelling, only the ratings reduction).
        var cards = new List<CardEvent>(6);
        int totalFouls = rng.NextInt(MinimumFouls, MaximumFoulsExclusive);
        int homeFouls = Math.Clamp(
            (int)MathF.Round(totalFouls * (1f - homePossessionShare)), 1, totalFouls - 1);
        GenerateFoulsAndCards(homeSide, homeFouls, rng, cards);
        GenerateFoulsAndCards(awaySide, totalFouls - homeFouls, rng, cards);

        var goals = new List<GoalEvent>(4);
        var possessions = new List<Possession>(possessionBudget);
        for (int i = 0; i < possessionBudget; i++)
        {
            bool isHome = rng.NextFloat() < homePossessionShare;
            int minute = rng.NextInt(1, MinutesPerMatch + 1);
            possessions.Add(new Possession(minute, isHome, i));
        }

        possessions.Sort(static (left, right) =>
        {
            int byMinute = left.Minute.CompareTo(right.Minute);
            return byMinute != 0 ? byMinute : left.Sequence.CompareTo(right.Sequence);
        });
        for (int i = 0; i < possessions.Count; i++)
        {
            ResolvePossession(homeSide, awaySide, possessions[i], rng, goals);
        }

        homeSide.Corners = PickCorners(homeSide, rng);
        awaySide.Corners = PickCorners(awaySide, rng);
        homeSide.Offsides = rng.NextInt(0, 6);
        awaySide.Offsides = rng.NextInt(0, 6);

        var injuries = new List<InjuryEvent>(2);
        GenerateInjuries(homeSide, rng, injuries);
        GenerateInjuries(awaySide, rng, injuries);

        int homeGoals = 0;
        int awayGoals = 0;
        for (int i = 0; i < goals.Count; i++)
        {
            if (goals[i].IsHome) homeGoals++;
            else awayGoals++;
        }

        bool wentToPenalties = false;
        int homePenaltyGoals = 0;
        int awayPenaltyGoals = 0;
        if (knockout && homeGoals == awayGoals)
        {
            wentToPenalties = true;
            SimulateShootout(rng, out homePenaltyGoals, out awayPenaltyGoals);
        }

        int homeOutcome = ResolveOutcome(homeGoals, awayGoals, wentToPenalties, homePenaltyGoals, awayPenaltyGoals);
        int awayOutcome = -homeOutcome;

        List<PlayerMatchLine> homePlayers = BuildPlayerLines(homeSide, homeOutcome, awayGoals, rng);
        List<PlayerMatchLine> awayPlayers = BuildPlayerLines(awaySide, awayOutcome, homeGoals, rng);

        int totalPossessions = homeSide.PossessionsHeld + awaySide.PossessionsHeld;
        int homePossessionPercent = totalPossessions == 0
            ? 50
            : Math.Clamp((int)MathF.Round(100f * homeSide.PossessionsHeld / totalPossessions), 1, 99);
        int awayPossessionPercent = 100 - homePossessionPercent;

        cards.Sort(static (left, right) => left.Minute.CompareTo(right.Minute));
        injuries.Sort(static (left, right) => left.Minute.CompareTo(right.Minute));

        var substitutions = new List<SubstitutionEvent>(homeSide.Substitutions.Count + awaySide.Substitutions.Count);
        substitutions.AddRange(homeSide.Substitutions);
        substitutions.AddRange(awaySide.Substitutions);
        substitutions.Sort(static (left, right) => left.Minute.CompareTo(right.Minute));

        return new QuickSimResult
        {
            HomeClubId = home.Id,
            AwayClubId = away.Id,
            HomeGoals = homeGoals,
            AwayGoals = awayGoals,
            WentToPenalties = wentToPenalties,
            HomePenaltyGoals = homePenaltyGoals,
            AwayPenaltyGoals = awayPenaltyGoals,
            Goals = goals,
            Cards = cards,
            Injuries = injuries,
            Substitutions = substitutions,
            HomePlayers = homePlayers,
            AwayPlayers = awayPlayers,
            HomeStats = new QuickSimTeamStats(
                homePossessionPercent, homeSide.Shots, homeSide.ShotsOnTarget,
                homeSide.Corners, homeSide.Fouls, homeSide.Offsides),
            AwayStats = new QuickSimTeamStats(
                awayPossessionPercent, awaySide.Shots, awaySide.ShotsOnTarget,
                awaySide.Corners, awaySide.Fouls, awaySide.Offsides)
        };
    }

    /// <summary>
    /// Convenience entry point for the season runner: selects both lineups with
    /// <see cref="SquadSelector"/> and derives the fixture's private stream as
    /// <c>world.Stream(QuickSim).Fork((ulong)fixture.Id.Value)</c>, so replaying a fixture reproduces it
    /// exactly without advancing the world's quick-sim stream. Neither the world nor the fixture is mutated.
    /// </summary>
    /// <param name="world">The world the fixture belongs to.</param>
    /// <param name="fixture">The fixture to resolve.</param>
    public static QuickSimResult Simulate(WorldState world, WorldFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fixture);

        WorldClub home = world.Clubs[fixture.HomeClubId];
        WorldClub away = world.Clubs[fixture.AwayClubId];
        SelectedLineup homeLineup = SquadSelector.Select(world, home);
        SelectedLineup awayLineup = SquadSelector.Select(world, away);
        WorldRng fixtureRng = world.Stream(WorldRandomStream.QuickSim).Fork((ulong)fixture.Id.Value);
        return Simulate(world, home, away, homeLineup, awayLineup, fixtureRng, fixture.IsCupTie);
    }

    /// <summary>
    /// Aggregates the selected XI into attack, midfield, defence and goalkeeping ratings, 0..100 each.
    ///
    /// <para>Attack is the mean over the four most attacking slots (strikers, then wingers, then the most
    /// advanced central midfielder when the formation fields only three forwards). Midfield is the mean
    /// over the central-midfielder, defensive-midfielder and winger slots, defence the mean over the
    /// centre-back, full-back and defensive-midfielder slots, and goalkeeping is the XI keeper's own
    /// shot-stopping blend. Every unit is weighted by its players' condition and match sharpness
    /// (<c>0.80 + 0.20 * (0.5*Condition + 0.5*Sharpness)</c>) with a small recent-form tilt.</para>
    /// </summary>
    /// <param name="world">The world whose player registry resolves the lineup.</param>
    /// <param name="lineup">The XI to rate.</param>
    public static TeamRatings EvaluateTeam(WorldState world, SelectedLineup lineup)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(lineup);
        ValidateLineup(lineup, nameof(lineup));

        IReadOnlyList<WorldRole> roles = lineup.StartingRoles;

        // Attack group: the four most attacking slots.
        var attackSlots = new int[4];
        int attackCount = 0;
        AddSlotsWithRole(roles, WorldRole.Striker, attackSlots, ref attackCount);
        AddSlotsWithRole(roles, WorldRole.Winger, attackSlots, ref attackCount);
        AddSlotsWithRole(roles, WorldRole.AttackingMidfielder, attackSlots, ref attackCount);
        AddSlotsWithRole(roles, WorldRole.CentralMidfielder, attackSlots, ref attackCount);

        float attackSum = 0f;
        for (int i = 0; i < attackCount; i++)
        {
            attackSum += AttackingUnitValue(world.Players[lineup.StartingXI[attackSlots[i]]]);
        }

        float attack = attackCount == 0 ? 0f : Math.Clamp(attackSum / attackCount, 0f, 100f);

        // Midfield group: CM / DM / W (excluding GK / CB / FB / ST).
        float midfieldSum = 0f;
        int midfieldCount = 0;
        for (int slot = 0; slot < roles.Count; slot++)
        {
            WorldRole role = roles[slot];
            if (role is not (WorldRole.CentralMidfielder or WorldRole.DefensiveMidfielder or WorldRole.Winger))
            {
                continue;
            }

            midfieldSum += MidfieldUnitValue(world.Players[lineup.StartingXI[slot]]);
            midfieldCount++;
        }

        float midfield = midfieldCount == 0 ? 0f : Math.Clamp(midfieldSum / midfieldCount, 0f, 100f);

        // Defence group: CB / FB / DM.
        float defenceSum = 0f;
        int defenceCount = 0;
        for (int slot = 0; slot < roles.Count; slot++)
        {
            WorldRole role = roles[slot];
            if (role is not (WorldRole.CenterBack or WorldRole.FullBack or WorldRole.DefensiveMidfielder))
            {
                continue;
            }

            defenceSum += DefensiveUnitValue(world.Players[lineup.StartingXI[slot]]);
            defenceCount++;
        }

        float defence = defenceCount == 0 ? 0f : Math.Clamp(defenceSum / defenceCount, 0f, 100f);

        // Goalkeeping: the XI keeper.
        int goalkeeperSlot = 0;
        for (int slot = 0; slot < roles.Count; slot++)
        {
            if (roles[slot] == WorldRole.Goalkeeper)
            {
                goalkeeperSlot = slot;
                break;
            }
        }

        float goalkeeping = Math.Clamp(
            GoalkeepingUnitValue(world.Players[lineup.StartingXI[goalkeeperSlot]]), 0f, 100f);

        float overall = (attack + midfield + defence + goalkeeping) / 4f;
        return new TeamRatings(attack, midfield, defence, goalkeeping, overall);
    }

    // ---------------------------------------------------------------------------------------------
    // Simulated team ratings and tactical matchup.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The bounded tactical matchup modifier on a side's attacking efficiency (plan §5.3), clamped to
    /// ±8%. Every term compares one of the attacker's stated preferences with the defender's matching
    /// defensive preference, so the formula is symmetric, deterministic and small:
    /// <list type="bullet">
    /// <item>Directness vs the opponent's defensive line height: a direct side beats a high line.</item>
    /// <item>Directness vs the opponent's compactness: direct play bypasses a compact block.</item>
    /// <item>Pressing intensity vs the opponent's tempo: pressing a slow build-up wins the ball back.</item>
    /// <item>Man orientation vs the opponent's positional freedom: man-marking a free shape.</item>
    /// <item>Short goalkeeper distribution vs the opponent's pressing: building short into a press costs.</item>
    /// </list>
    /// </summary>
    private static float TacticalAttackModifier(TeamTacticalProfile attacker, TeamTacticalProfile defender)
    {
        float directVsLine = attacker.Directness01 - defender.DefensiveLineHeight01;
        float directVsBlock = attacker.Directness01 - defender.Compactness01;
        float pressVsTempo = attacker.PressingIntensity01 - defender.Tempo01;
        float manVsFreedom = attacker.ManOrientation01 - defender.PositionalFreedom01;
        float shortVsPress = attacker.ShortGoalkeeperDistribution
            ? 0.5f - defender.PressingIntensity01
            : 0.25f * defender.PressingIntensity01;

        float delta =
            0.040f * directVsLine +
            0.020f * directVsBlock +
            0.030f * pressVsTempo +
            0.010f * manVsFreedom +
            0.010f * shortVsPress;

        return Math.Clamp(delta, -0.08f, 0.08f);
    }

    private static float ExpectedGoals(float attackRating, float combinedDefenceRating, float tacticalModifier)
    {
        float lambda = BaseExpectedGoals
            * MathF.Exp(StrengthGoalExponent * (attackRating - combinedDefenceRating))
            * tacticalModifier;
        return Math.Clamp(lambda, MinimumExpectedGoals, MaximumExpectedGoals);
    }

    private static float GoalChance(float attackRating, float combinedDefenceRating) =>
        Math.Clamp(
            GoalChanceBase + GoalChancePerRatingPoint * (attackRating - combinedDefenceRating),
            MinimumGoalChance,
            MaximumGoalChance);

    private static float CombinedDefence(float defence, float goalkeeping) =>
        DefenceWeight * defence + GoalkeepingWeight * goalkeeping;

    // ---------------------------------------------------------------------------------------------
    // Per-slot unit values.
    // ---------------------------------------------------------------------------------------------

    private static float AttackingUnitValue(WorldPlayer player)
    {
        float raw =
            0.30f * player.Attributes.Current(AttributeId.Finishing) +
            0.25f * player.Attributes.Current(AttributeId.OffTheBall) +
            0.20f * player.Attributes.Current(AttributeId.TopSpeed) +
            0.15f * player.Attributes.Current(AttributeId.Dribbling) +
            0.10f * player.Attributes.Current(AttributeId.FirstTouch);
        return StateAdjustedValue(player, raw);
    }

    private static float MidfieldUnitValue(WorldPlayer player)
    {
        float raw =
            0.30f * player.Attributes.Current(AttributeId.Passing) +
            0.20f * player.Attributes.Current(AttributeId.Vision) +
            0.20f * player.Attributes.Current(AttributeId.Stamina) +
            0.15f * player.Attributes.Current(AttributeId.FirstTouch) +
            0.15f * player.Attributes.Current(AttributeId.Tackling);
        return StateAdjustedValue(player, raw);
    }

    private static float DefensiveUnitValue(WorldPlayer player)
    {
        float raw =
            0.30f * player.Attributes.Current(AttributeId.Tackling) +
            0.25f * player.Attributes.Current(AttributeId.Positioning) +
            0.15f * player.Attributes.Current(AttributeId.Strength) +
            0.15f * player.Attributes.Current(AttributeId.Balance) +
            0.15f * player.Attributes.Current(AttributeId.Composure);
        return StateAdjustedValue(player, raw);
    }

    private static float GoalkeepingUnitValue(WorldPlayer player)
    {
        float raw =
            0.60f * player.Attributes.Current(AttributeId.Goalkeeping) +
            0.15f * player.Attributes.Current(AttributeId.Composure) +
            0.15f * player.Attributes.Current(AttributeId.Positioning) +
            0.10f * player.Attributes.Current(AttributeId.NaturalReach);
        return StateAdjustedValue(player, raw);
    }

    private static float StateAdjustedValue(WorldPlayer player, float raw)
    {
        float stateFactor = 0.80f + 0.20f * (0.5f * player.Condition01 + 0.5f * player.MatchSharpness01);
        float formAdjustment = 0.05f * (player.Form.Form01(FormWindow) - 0.5f) * 10f;
        return raw * stateFactor + formAdjustment;
    }

    private static void AddSlotsWithRole(
        IReadOnlyList<WorldRole> roles, WorldRole role, int[] slots, ref int count)
    {
        for (int slot = 0; slot < roles.Count && count < slots.Length; slot++)
        {
            if (roles[slot] == role) slots[count++] = slot;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Possession simulation.
    // ---------------------------------------------------------------------------------------------

    private static void ResolvePossession(
        MatchSide homeSide, MatchSide awaySide, Possession possession, WorldRng rng, List<GoalEvent> goals)
    {
        MatchSide attack = possession.IsHome ? homeSide : awaySide;
        MatchSide defence = possession.IsHome ? awaySide : homeSide;
        attack.PossessionsHeld++;

        // A red card cuts the side's ratings by ~8% for the rest of the match (v1: no ten men).
        float attackScale =
            attack.RedMinute >= 0 && possession.Minute >= attack.RedMinute ? RedCardRatingScale : 1f;
        float defenceScale =
            defence.RedMinute >= 0 && possession.Minute >= defence.RedMinute ? RedCardRatingScale : 1f;

        float attackRating = attack.Attack * attackScale;
        float combinedDefence = CombinedDefence(defence.Defence * defenceScale, defence.Goalkeeping);
        float goalChance = GoalChance(attackRating, combinedDefence);

        // Solve the per-possession shot chance from the fixture's expected-goals lambda (see class remarks).
        float expectedPossessions = attack.ExpectedPossessions;
        float shotChance = expectedPossessions <= 0.0001f
            ? MaximumShotChance
            : Math.Clamp(
                attack.Lambda * PossessionChainCalibration * attackScale /
                (expectedPossessions * OnTargetChance * goalChance),
                MinimumShotChance,
                MaximumShotChance);
        if (rng.NextFloat() >= shotChance) return;

        int shooterIndex = PickShooter(attack, possession.Minute, rng);
        if (shooterIndex < 0) return;
        PlayerSlot shooter = attack.Slots[shooterIndex];
        attack.Shots++;
        shooter.Shots++;

        if (rng.NextFloat() >= OnTargetChance) return;
        attack.ShotsOnTarget++;
        shooter.ShotsOnTarget++;

        if (rng.NextFloat() < goalChance)
        {
            shooter.Goals++;
            int assistIndex = PickAssist(attack, shooterIndex, possession.Minute, rng);
            PlayerId assistId = PlayerId.None;
            if (assistIndex >= 0)
            {
                attack.Slots[assistIndex].Assists++;
                assistId = attack.Slots[assistIndex].Player.Id;
            }

            goals.Add(new GoalEvent(possession.Minute, attack.IsHome, shooter.Player.Id, assistId));
        }
        else
        {
            defence.Slots[defence.KeeperIndex].Saves++;
        }
    }

    /// <summary>Picks the shooter among the players on the pitch, by Finishing + OffTheBall + ShotPower/2.</summary>
    private static int PickShooter(MatchSide side, int minute, WorldRng rng)
    {
        float total = 0f;
        for (int i = 0; i < side.Slots.Length; i++)
        {
            if (CanShoot(side.Slots[i], minute)) total += ShooterWeight(side.Slots[i].Player);
        }

        if (total <= 0f) return -1;
        float target = rng.NextFloat() * total;
        for (int i = 0; i < side.Slots.Length; i++)
        {
            if (!CanShoot(side.Slots[i], minute)) continue;
            target -= ShooterWeight(side.Slots[i].Player);
            if (target <= 0f) return i;
        }

        for (int i = side.Slots.Length - 1; i >= 0; i--)
        {
            if (CanShoot(side.Slots[i], minute)) return i;
        }

        return -1;
    }

    /// <summary>Picks the assist provider among the other players on the pitch, by Passing + Vision.</summary>
    private static int PickAssist(MatchSide side, int scorerIndex, int minute, WorldRng rng)
    {
        if (rng.NextFloat() >= AssistProbability) return -1;

        float total = 0f;
        for (int i = 0; i < side.Slots.Length; i++)
        {
            if (i == scorerIndex || !IsOnField(side.Slots[i], minute)) continue;
            total += AssistWeight(side.Slots[i].Player);
        }

        if (total <= 0f) return -1;
        float target = rng.NextFloat() * total;
        for (int i = 0; i < side.Slots.Length; i++)
        {
            if (i == scorerIndex || !IsOnField(side.Slots[i], minute)) continue;
            target -= AssistWeight(side.Slots[i].Player);
            if (target <= 0f) return i;
        }

        for (int i = side.Slots.Length - 1; i >= 0; i--)
        {
            if (i != scorerIndex && IsOnField(side.Slots[i], minute)) return i;
        }

        return -1;
    }

    private static bool CanShoot(PlayerSlot slot, int minute) => !slot.IsKeeper && IsOnField(slot, minute);

    private static float ShooterWeight(WorldPlayer player) =>
        player.Attributes.Current(AttributeId.Finishing) +
        player.Attributes.Current(AttributeId.OffTheBall) +
        0.5f * player.Attributes.Current(AttributeId.ShotPower);

    private static float AssistWeight(WorldPlayer player) =>
        player.Attributes.Current(AttributeId.Passing) + player.Attributes.Current(AttributeId.Vision);

    // ---------------------------------------------------------------------------------------------
    // Cards, injuries, substitutions and secondary statistics.
    // ---------------------------------------------------------------------------------------------

    private static void GenerateFoulsAndCards(
        MatchSide side, int fouls, WorldRng rng, List<CardEvent> cards)
    {
        side.Fouls = fouls;
        for (int i = 0; i < fouls; i++)
        {
            int minute = rng.NextInt(1, MinutesPerMatch + 1);
            int foulerIndex = PickFouler(side, minute, rng);
            if (foulerIndex < 0) continue;

            PlayerSlot fouler = side.Slots[foulerIndex];
            float discipline01 = fouler.Player.Personality.Discipline / 100f;
            float yellowChance = Math.Clamp(
                0.10f + 0.25f * (1f - discipline01) + 0.10f * side.Profile.PressingIntensity01,
                0.05f,
                0.45f);
            if (rng.NextFloat() >= yellowChance) continue;

            if (rng.NextBool(RedCardProbability))
            {
                fouler.RedCards++;
                if (side.RedMinute < 0 || minute < side.RedMinute) side.RedMinute = minute;
                cards.Add(new CardEvent(minute, side.IsHome, fouler.Player.Id, IsRed: true));
            }
            else
            {
                fouler.YellowCards++;
                cards.Add(new CardEvent(minute, side.IsHome, fouler.Player.Id, IsRed: false));
            }
        }
    }

    private static int PickFouler(MatchSide side, int minute, WorldRng rng)
    {
        float total = 0f;
        for (int i = 0; i < side.Slots.Length; i++)
        {
            if (!CanBeBooked(side.Slots[i], minute)) continue;
            total += FoulerWeight(side.Slots[i]);
        }

        if (total <= 0f) return -1;
        float target = rng.NextFloat() * total;
        for (int i = 0; i < side.Slots.Length; i++)
        {
            if (!CanBeBooked(side.Slots[i], minute)) continue;
            target -= FoulerWeight(side.Slots[i]);
            if (target <= 0f) return i;
        }

        for (int i = side.Slots.Length - 1; i >= 0; i--)
        {
            if (CanBeBooked(side.Slots[i], minute)) return i;
        }

        return -1;
    }

    private static bool CanBeBooked(PlayerSlot slot, int minute) =>
        !slot.IsKeeper && slot.YellowCards == 0 && slot.RedCards == 0 && IsOnField(slot, minute);

    private static float FoulerWeight(PlayerSlot slot)
    {
        float roleWeight = slot.Role switch
        {
            WorldRole.CenterBack => 3.0f,
            WorldRole.FullBack => 3.0f,
            WorldRole.DefensiveMidfielder => 3.2f,
            WorldRole.CentralMidfielder => 2.6f,
            WorldRole.AttackingMidfielder => 2.0f,
            WorldRole.Winger => 1.8f,
            WorldRole.Striker => 1.4f,
            _ => 0f
        };
        float discipline = slot.Player.Personality.Discipline / 100f;
        return roleWeight * (1.2f - discipline);
    }

    private static void GenerateInjuries(MatchSide side, WorldRng rng, List<InjuryEvent> injuries)
    {
        for (int i = 0; i < StartingSize; i++)
        {
            PlayerSlot slot = side.Slots[i];
            float chance = slot.IsKeeper ? KeeperInjuryChance : OutfieldInjuryChance;
            chance *= 1f + InjuryConditionMultiplier * (1f - slot.Player.Condition01);
            if (!rng.NextBool(chance)) continue;

            int minute = rng.NextInt(1, slot.OffMinute + 1);
            InjuryKind kind = PickInjuryKind(rng);
            InjurySeverity severity = PickInjurySeverity(rng);
            int weeksOut = WeeksOut(severity, rng);
            injuries.Add(new InjuryEvent(minute, side.IsHome, slot.Player.Id, kind, severity, weeksOut));
        }
    }

    private static InjuryKind PickInjuryKind(WorldRng rng)
    {
        float roll = rng.NextFloat();
        if (roll < 0.28f) return InjuryKind.HamstringTear;
        if (roll < 0.46f) return InjuryKind.CalfStrain;
        if (roll < 0.62f) return InjuryKind.GroinStrain;
        if (roll < 0.78f) return InjuryKind.AnkleLigament;
        if (roll < 0.90f) return InjuryKind.BackInjury;
        return InjuryKind.Illness;
    }

    private static InjurySeverity PickInjurySeverity(WorldRng rng)
    {
        float roll = rng.NextFloat();
        if (roll < 0.55f) return InjurySeverity.Knock;
        if (roll < 0.85f) return InjurySeverity.Minor;
        if (roll < 0.97f) return InjurySeverity.Moderate;
        return InjurySeverity.Severe;
    }

    private static int WeeksOut(InjurySeverity severity, WorldRng rng) => severity switch
    {
        InjurySeverity.Knock => 1,
        InjurySeverity.Minor => rng.NextInt(1, 4),
        InjurySeverity.Moderate => rng.NextInt(3, 9),
        InjurySeverity.Severe => rng.NextInt(10, 27),
        _ => 1
    };

    private static void PlanSubstitutions(MatchSide side, WorldRng rng)
    {
        int numberOfSubs = rng.NextInt(MinimumSubstitutions, MaximumSubstitutionsExclusive);
        var usedBench = new bool[BenchSize];

        var candidates = new int[StartingSize];
        int candidateCount = 0;
        for (int i = 0; i < StartingSize; i++)
        {
            if (!side.Slots[i].IsKeeper) candidates[candidateCount++] = i;
        }

        // Lowest condition first, then lower player id: the two/three most tired starters come off.
        for (int i = 1; i < candidateCount; i++)
        {
            int current = candidates[i];
            int j = i - 1;
            while (j >= 0 && CompareSubPriority(side.Slots[candidates[j]], side.Slots[current]) > 0)
            {
                candidates[j + 1] = candidates[j];
                j--;
            }

            candidates[j + 1] = current;
        }

        for (int sub = 0; sub < numberOfSubs && sub < candidateCount; sub++)
        {
            int outIndex = candidates[sub];
            int benchIndex = FindBenchReplacement(side, side.Slots[outIndex].Role, usedBench);
            if (benchIndex < 0) break;

            int minute = SubstitutionMinute(sub, numberOfSubs, rng);
            PlayerSlot on = side.Slots[benchIndex];
            usedBench[benchIndex - StartingSize] = true;
            side.Slots[outIndex].OffMinute = minute;
            on.CameOn = true;
            on.OnMinute = minute;
            side.Substitutions.Add(new SubstitutionEvent(
                minute, side.IsHome, side.Slots[outIndex].Player.Id, on.Player.Id));
        }
    }

    private static int CompareSubPriority(PlayerSlot left, PlayerSlot right)
    {
        int byCondition = left.Player.Condition01.CompareTo(right.Player.Condition01);
        return byCondition != 0 ? byCondition : left.Player.Id.Value.CompareTo(right.Player.Id.Value);
    }

    private static int SubstitutionMinute(int subIndex, int numberOfSubs, WorldRng rng)
    {
        int baseMinute = MinimumSubstitutionMinute +
            subIndex * (MaximumSubstitutionMinute - MinimumSubstitutionMinute) / numberOfSubs;
        int minute = baseMinute + rng.NextInt(0, 8);
        return Math.Clamp(minute, MinimumSubstitutionMinute, MaximumSubstitutionMinute);
    }

    private static int FindBenchReplacement(MatchSide side, WorldRole outgoingRole, bool[] usedBench)
    {
        // Prefer a like-for-like substitute so the bench covers the player coming off.
        for (int bench = 0; bench < BenchSize; bench++)
        {
            if (usedBench[bench] || side.Slots[StartingSize + bench].IsKeeper) continue;
            if (side.Slots[StartingSize + bench].Role == outgoingRole) return StartingSize + bench;
        }

        for (int bench = 0; bench < BenchSize; bench++)
        {
            if (usedBench[bench] || side.Slots[StartingSize + bench].IsKeeper) continue;
            return StartingSize + bench;
        }

        return -1;
    }

    private static int PickCorners(MatchSide side, WorldRng rng) =>
        Math.Clamp(side.Shots * 2 / 5 + rng.NextInt(0, 3), 0, 20);

    // ---------------------------------------------------------------------------------------------
    // Result, shoot-out and player lines.
    // ---------------------------------------------------------------------------------------------

    private static void SimulateShootout(WorldRng rng, out int homeGoals, out int awayGoals)
    {
        homeGoals = 0;
        awayGoals = 0;
        for (int round = 0; round < ShootoutRounds; round++)
        {
            if (rng.NextBool(ShootoutConversionChance)) homeGoals++;
            if (rng.NextBool(ShootoutConversionChance)) awayGoals++;
        }

        // Sudden death: both sides take one kick per round until the round is not shared.
        while (homeGoals == awayGoals)
        {
            bool homeScores = rng.NextBool(ShootoutConversionChance);
            bool awayScores = rng.NextBool(ShootoutConversionChance);
            if (homeScores) homeGoals++;
            if (awayScores) awayGoals++;
        }
    }

    /// <summary>1 = home win, 0 = draw, -1 = away win (a shoot-out counts as a win/loss).</summary>
    private static int ResolveOutcome(
        int homeGoals, int awayGoals, bool wentToPenalties, int homePenaltyGoals, int awayPenaltyGoals)
    {
        if (wentToPenalties) return homePenaltyGoals > awayPenaltyGoals ? 1 : -1;
        if (homeGoals > awayGoals) return 1;
        if (homeGoals < awayGoals) return -1;
        return 0;
    }

    private static List<PlayerMatchLine> BuildPlayerLines(
        MatchSide side, int outcome, int goalsAgainst, WorldRng rng)
    {
        var lines = new List<PlayerMatchLine>(StartingSize + 3);
        for (int i = 0; i < side.Slots.Length; i++)
        {
            PlayerSlot slot = side.Slots[i];
            int minutes = slot.MinutesPlayed;
            if (minutes <= 0) continue;

            GeneratePassingAndTackling(slot, minutes, rng);
            float conditionCost = ConditionCost(slot.Player, minutes, slot.IsKeeper);
            float rating = ComputeRating(slot, goalsAgainst, outcome, rng);
            float endingStamina = Math.Clamp(slot.Player.Condition01 - conditionCost, 0.02f, 1f);

            lines.Add(new PlayerMatchLine(
                slot.Player.Id,
                minutes,
                slot.Goals,
                slot.Assists,
                slot.Shots,
                slot.ShotsOnTarget,
                slot.PassesAttempted,
                slot.PassesCompleted,
                slot.Tackles,
                slot.Saves,
                slot.YellowCards,
                slot.RedCards,
                rating,
                endingStamina,
                conditionCost));
        }

        return lines;
    }

    private static void GeneratePassingAndTackling(PlayerSlot slot, int minutes, WorldRng rng)
    {
        float passing = slot.Player.Attributes.Current(AttributeId.Passing);
        float passVolume = minutes
            * PassesPerMinute(slot.Role)
            * (0.75f + rng.NextFloat() * 0.5f)
            * (0.60f + passing / 125f);
        slot.PassesAttempted = Math.Max(1, (int)MathF.Round(passVolume));

        float completionRate = Math.Clamp(
            0.62f + (passing - 50f) * 0.004f + rng.NextFloat() * 0.06f, 0.55f, 0.95f);
        slot.PassesCompleted = Math.Clamp(
            (int)MathF.Round(slot.PassesAttempted * completionRate), 0, slot.PassesAttempted);

        float tackling = slot.Player.Attributes.Current(AttributeId.Tackling);
        float tackleVolume = minutes
            * TacklesPerMinute(slot.Role)
            * (0.5f + rng.NextFloat())
            * (0.60f + tackling / 125f);
        slot.Tackles = Math.Max(0, (int)MathF.Round(tackleVolume));
    }

    private static float PassesPerMinute(WorldRole role) => role switch
    {
        WorldRole.Goalkeeper => 0.18f,
        WorldRole.CenterBack => 0.38f,
        WorldRole.FullBack => 0.48f,
        WorldRole.DefensiveMidfielder => 0.52f,
        WorldRole.CentralMidfielder => 0.58f,
        WorldRole.AttackingMidfielder => 0.55f,
        WorldRole.Winger => 0.46f,
        WorldRole.Striker => 0.34f,
        _ => 0.40f
    };

    private static float TacklesPerMinute(WorldRole role) => role switch
    {
        WorldRole.Goalkeeper => 0f,
        WorldRole.CenterBack => 0.035f,
        WorldRole.FullBack => 0.045f,
        WorldRole.DefensiveMidfielder => 0.055f,
        WorldRole.CentralMidfielder => 0.040f,
        WorldRole.AttackingMidfielder => 0.022f,
        WorldRole.Winger => 0.020f,
        WorldRole.Striker => 0.012f,
        _ => 0.030f
    };

    private static float ConditionCost(WorldPlayer player, int minutes, bool isKeeper)
    {
        float stamina = player.Attributes.Current(AttributeId.Stamina);
        float cost = (minutes / (float)MinutesPerMatch) * 0.55f * (1.15f - 0.30f * (stamina / 100f));
        if (isKeeper) cost *= 0.55f;
        return cost;
    }

    private static float ComputeRating(PlayerSlot slot, int goalsAgainst, int outcome, WorldRng rng)
    {
        float rating =
            BaseRating +
            RatingPerGoal * slot.Goals +
            RatingPerAssist * slot.Assists +
            RatingPerShotOnTarget * slot.ShotsOnTarget +
            RatingPerTackle * slot.Tackles +
            RatingPerSave * slot.Saves;

        if (IsDefensiveRole(slot.Role))
        {
            rating -= MathF.Min(MaximumGoalConcededPenalty, RatingPerGoalConceded * goalsAgainst);
        }

        rating += outcome > 0 ? RatingWinBonus : outcome < 0 ? RatingLossBonus : RatingDrawBonus;
        rating += rng.NextGaussian(0f, RatingNoiseStdDev);
        return Math.Clamp(rating, MinimumRating, MaximumRating);
    }

    private static bool IsDefensiveRole(WorldRole role) =>
        role is WorldRole.Goalkeeper or WorldRole.CenterBack or WorldRole.FullBack;

    // ---------------------------------------------------------------------------------------------
    // Shared helpers.
    // ---------------------------------------------------------------------------------------------

    private static TeamTacticalProfile ResolveProfile(
        WorldState world, WorldClub club, TeamTacticalProfile fallback)
    {
        if (club.ManagerId.IsValid &&
            world.Managers.TryGet(club.ManagerId, out WorldManager? manager) &&
            manager is not null)
        {
            return manager.TacticalProfile;
        }

        return fallback;
    }

    private static bool IsOnField(PlayerSlot slot, int minute) =>
        slot.IsStarter ? minute <= slot.OffMinute : slot.CameOn && minute >= slot.OnMinute;

    private static void ValidateLineup(SelectedLineup lineup, string parameterName)
    {
        if (lineup.StartingXI.Count != StartingSize || lineup.StartingRoles.Count != StartingSize)
        {
            throw new ArgumentException(
                $"A lineup needs exactly {StartingSize} starting players and slot roles.", parameterName);
        }

        if (lineup.Bench.Count != BenchSize || lineup.BenchRoles.Count != BenchSize)
        {
            throw new ArgumentException(
                $"A lineup needs exactly {BenchSize} bench players and slot roles.", parameterName);
        }

        for (int i = 0; i < StartingSize; i++)
        {
            if (!lineup.StartingXI[i].IsValid)
                throw new ArgumentException("A lineup contains an invalid starting player id.", parameterName);
        }

        for (int i = 0; i < BenchSize; i++)
        {
            if (!lineup.Bench[i].IsValid)
                throw new ArgumentException("A lineup contains an invalid bench player id.", parameterName);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Private match state.
    // ---------------------------------------------------------------------------------------------

    private readonly record struct Possession(int Minute, bool IsHome, int Sequence);

    private sealed class MatchSide
    {
        public MatchSide(
            WorldState world,
            SelectedLineup lineup,
            bool isHome,
            TeamTacticalProfile profile,
            float attack,
            float defence,
            float goalkeeping,
            float lambda,
            float expectedPossessions)
        {
            IsHome = isHome;
            Profile = profile;
            Attack = attack;
            Defence = defence;
            Goalkeeping = goalkeeping;
            Lambda = lambda;
            ExpectedPossessions = expectedPossessions;
            Substitutions = new List<SubstitutionEvent>(3);

            int squadSize = StartingSize + BenchSize;
            Slots = new PlayerSlot[squadSize];
            for (int i = 0; i < StartingSize; i++)
            {
                WorldRole role = lineup.StartingRoles[i];
                Slots[i] = new PlayerSlot(
                    world.Players[lineup.StartingXI[i]], role, isStarter: true,
                    isKeeper: role == WorldRole.Goalkeeper);
            }

            for (int i = 0; i < BenchSize; i++)
            {
                WorldRole role = lineup.BenchRoles[i];
                Slots[StartingSize + i] = new PlayerSlot(
                    world.Players[lineup.Bench[i]], role, isStarter: false,
                    isKeeper: role == WorldRole.Goalkeeper);
            }

            KeeperIndex = 0;
            for (int i = 0; i < StartingSize; i++)
            {
                if (Slots[i].IsKeeper)
                {
                    KeeperIndex = i;
                    break;
                }
            }
        }

        public bool IsHome { get; }

        public TeamTacticalProfile Profile { get; }

        public float Attack { get; }

        public float Defence { get; }

        public float Goalkeeping { get; }

        public float Lambda { get; }

        public float ExpectedPossessions { get; }

        public PlayerSlot[] Slots { get; }

        public int KeeperIndex { get; }

        public List<SubstitutionEvent> Substitutions { get; }

        public int RedMinute { get; set; } = -1;

        public int Shots { get; set; }

        public int ShotsOnTarget { get; set; }

        public int Fouls { get; set; }

        public int Corners { get; set; }

        public int Offsides { get; set; }

        public int PossessionsHeld { get; set; }
    }

    private sealed class PlayerSlot
    {
        public PlayerSlot(WorldPlayer player, WorldRole role, bool isStarter, bool isKeeper)
        {
            Player = player;
            Role = role;
            IsStarter = isStarter;
            IsKeeper = isKeeper;
            OffMinute = MinutesPerMatch;
        }

        public WorldPlayer Player { get; }

        public WorldRole Role { get; }

        public bool IsStarter { get; }

        public bool IsKeeper { get; }

        public bool CameOn { get; set; }

        public int OnMinute { get; set; }

        public int OffMinute { get; set; }

        public int MinutesPlayed => IsStarter
            ? OffMinute
            : CameOn ? MinutesPerMatch - OnMinute : 0;

        public int Goals { get; set; }

        public int Assists { get; set; }

        public int Shots { get; set; }

        public int ShotsOnTarget { get; set; }

        public int PassesAttempted { get; set; }

        public int PassesCompleted { get; set; }

        public int Tackles { get; set; }

        public int Saves { get; set; }

        public int YellowCards { get; set; }

        public int RedCards { get; set; }
    }
}
