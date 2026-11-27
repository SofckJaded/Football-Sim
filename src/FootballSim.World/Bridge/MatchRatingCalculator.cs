using System;
using System.Collections.Generic;
using FootballSim.Core;

namespace FootballSim.World.Bridge;

/// <summary>
/// One player's rating inputs (W5): the counting stats the match engine's
/// <see cref="MatchPlayerPerformance"/> observer exposes, plus the team context the individual line cannot
/// know. It exists so the rating formula can be tested and reused without constructing a match, while the
/// observer overload still lets a caller rate a completed match directly.
/// </summary>
/// <param name="Goals">Goals credited to the player (non-negative; negative values are treated as zero).</param>
/// <param name="Assists">Assists credited to the player (non-negative).</param>
/// <param name="Shots">Shots attempted (non-negative).</param>
/// <param name="ShotsOnTarget">Shots on target (clamped into 0..<paramref name="Shots"/>).</param>
/// <param name="Saves">Goalkeeper catches/parries (non-negative; counted for keepers only).</param>
/// <param name="Tackles">Standing tackles that dispossessed an opponent (non-negative).</param>
/// <param name="PassesAttempted">Passes attempted (non-negative).</param>
/// <param name="PassesCompleted">Passes completed (clamped into 0..<paramref name="PassesAttempted"/>).</param>
/// <param name="MinutesPlayed">Match minutes the player was on the pitch for; 0 (or less) rates 0.</param>
/// <param name="Role">The role the player was fielded in, for the role expectation term.</param>
/// <param name="IsHome">
/// Whether the player's team was the home side. It is carried so a caller can aggregate and report
/// home-vs-away ratings and so the observer overload can forward the fixture's side; the rating itself
/// deliberately adds no venue bonus — the same on-pitch performance rates the same anywhere.
/// </param>
/// <param name="TeamGoals">Goals the player's team scored (non-negative).</param>
/// <param name="OpponentGoals">Goals the opponent scored (non-negative).</param>
/// <param name="TeamPossessionShare01">The team's possession share, 0..1; 0.5 is neutral.</param>
public readonly record struct MatchRatingInput(
    int Goals,
    int Assists,
    int Shots,
    int ShotsOnTarget,
    int Saves,
    int Tackles,
    int PassesAttempted,
    int PassesCompleted,
    float MinutesPlayed,
    TacticalRole Role,
    bool IsHome,
    int TeamGoals,
    int OpponentGoals,
    float TeamPossessionShare01);

/// <summary>
/// Stage W5 match bridge: converts one finished performance line into the living world's 4.0..10.0 match
/// rating (<c>docs/WORLD_CAREER_PLAN.md</c> §4.1's <c>MatchAppearance.Rating</c> contract).
///
/// <para><b>Deterministic.</b> The calculator reads only its inputs: no random stream, no clock, no
/// allocation. Identical inputs always produce the identical float, which is what lets a replayed match
/// produce identical career history.</para>
///
/// <para><b>The shape of a rating.</b> Every player starts at <c>6.4</c> and the individual line moves it:
/// team result (±0.35 at a capped three-goal margin), goals and assists (capped), shots on target, saves
/// above a keeper's routine two, tackles (capped), pass completion against the neutral 0.65, role
/// expectations (a keeper punished for conceding, an attacker for not shooting) and a small possession
/// term. The result is clamped to 4.0..10.0 and rounded to one decimal. A player who did not play is
/// <see cref="NotPlayedRating"/>, never a 6.4 baseline — an unused substitute has no rating, not an average
/// one.</para>
/// </summary>
public static class MatchRatingCalculator
{
    /// <summary>
    /// Rating reported for a player who did not get on the pitch (<see cref="MatchRatingInput.MinutesPlayed"/>
    /// of 0). Zero, not a baseline, so career averages can exclude it by testing for this value.
    /// </summary>
    public const float NotPlayedRating = 0f;

    /// <summary>Lowest rating a player who did play can receive.</summary>
    public const float MinimumRating = 4.0f;

    /// <summary>Highest rating a player can receive.</summary>
    public const float MaximumRating = 10.0f;

    /// <summary>Neutral rating every playing performance starts from.</summary>
    private const float BaseRating = 6.4f;

    /// <summary>Result swing per goal of a capped three-goal margin (±0.35 at 3+).</summary>
    private const float ResultSwingPerGoal = 0.35f / 3f;
    private const int ResultMarginCap = 3;

    private const float GoalBonus = 0.9f;
    private const int MaxGoalsCounted = 3;
    private const float AssistBonus = 0.45f;
    private const int MaxAssistsCounted = 2;
    private const float ShotOnTargetBonus = 0.12f;
    private const int MaxShotsOnTargetCounted = 5;
    private const float TackleBonus = 0.06f;
    private const int MaxTacklesCounted = 8;

    /// <summary>A keeper's first two saves are part of the job; only saves above them move the rating.</summary>
    private const int RoutineSaves = 2;
    private const float SaveBonus = 0.35f;
    private const int MaxSavesCounted = 5;

    /// <summary>Pass completion above this ratio earns credit; below it costs. ±0.4 at the swing limits.</summary>
    private const float NeutralPassRatio = 0.65f;
    private const float PassRatioSwing = 0.35f;
    private const float PassSwing = 0.4f;

    private const float PossessionSwing = 0.1f;

    private const float CleanSheetBonus = 0.3f;
    private const float ConcededPenaltyPerGoal = 0.08f;
    private const int ExemptConcededGoals = 1;
    private const int MaxConcededPenalties = 4;
    private const float StrikerNoShotPenalty = 0.25f;
    private const float WingerNoShotPenalty = 0.2f;

    /// <summary>
    /// Rates one player from the engine observer's completed performance line.
    /// </summary>
    /// <param name="performance">The observer line at full time; must not be null.</param>
    /// <param name="isHome">Whether the player's team was the home side (forwarded to <see cref="MatchRatingInput"/>).</param>
    /// <param name="teamGoals">Goals the player's team scored.</param>
    /// <param name="opponentGoals">Goals the opponent scored.</param>
    /// <param name="role">The role the player was fielded in.</param>
    /// <param name="teamPossessionShare01">The team's possession share, 0..1 (0.5 is neutral).</param>
    /// <returns>A rating in 4.0..10.0 rounded to one decimal, or <see cref="NotPlayedRating"/> when the player did not play.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="performance"/> is null.</exception>
    public static float Rate(
        MatchPlayerPerformance performance,
        bool isHome,
        int teamGoals,
        int opponentGoals,
        TacticalRole role,
        float teamPossessionShare01)
    {
        ArgumentNullException.ThrowIfNull(performance);

        var input = new MatchRatingInput(
            performance.Goals,
            performance.Assists,
            performance.Shots,
            performance.ShotsOnTarget,
            performance.Saves,
            performance.Tackles,
            performance.PassesAttempted,
            performance.PassesCompleted,
            performance.MinutesPlayed,
            role,
            isHome,
            teamGoals,
            opponentGoals,
            teamPossessionShare01);

        return Rate(in input);
    }

    /// <summary>
    /// Rates one player from a supplied input line. The formula is documented on the class; this is the
    /// overload a caller uses when it has no <see cref="MatchPlayerPerformance"/> row (tests, quick-sim
    /// adapters) or when it wants to state the team context explicitly.
    /// </summary>
    /// <param name="input">The performance and team context to rate.</param>
    /// <returns>A rating in 4.0..10.0 rounded to one decimal, or <see cref="NotPlayedRating"/> when the player did not play.</returns>
    public static float Rate(in MatchRatingInput input)
    {
        // A player who did not get on the pitch has no performance to rate (this also swallows NaN minutes).
        if (!(input.MinutesPlayed > 0f)) return NotPlayedRating;

        int goals = Math.Max(0, input.Goals);
        int assists = Math.Max(0, input.Assists);
        int shots = Math.Max(0, input.Shots);
        int shotsOnTarget = Math.Clamp(input.ShotsOnTarget, 0, shots);
        int saves = Math.Max(0, input.Saves);
        int tackles = Math.Max(0, input.Tackles);
        int passesAttempted = Math.Max(0, input.PassesAttempted);
        int passesCompleted = Math.Clamp(input.PassesCompleted, 0, passesAttempted);
        int teamGoals = Math.Max(0, input.TeamGoals);
        int opponentGoals = Math.Max(0, input.OpponentGoals);

        float rating = BaseRating;

        // Team result: ±0.35 at a three-goal margin, scaled down for a narrower one, zero for a draw.
        int margin = Math.Clamp(teamGoals - opponentGoals, -ResultMarginCap, ResultMarginCap);
        rating += ResultSwingPerGoal * margin;

        // Individual production, each piece capped so one huge stat cannot decide the whole rating.
        rating += GoalBonus * Math.Min(goals, MaxGoalsCounted);
        rating += AssistBonus * Math.Min(assists, MaxAssistsCounted);
        rating += ShotOnTargetBonus * Math.Min(shotsOnTarget, MaxShotsOnTargetCounted);
        rating += TackleBonus * Math.Min(tackles, MaxTacklesCounted);

        // Pass completion: neutral at 65 %, full credit at 100 %, full cost at 30 % or worse.
        float passRatio = passesAttempted > 0 ? passesCompleted / (float)passesAttempted : NeutralPassRatio;
        float passSwing = Math.Clamp((passRatio - NeutralPassRatio) / PassRatioSwing, -1f, 1f);
        rating += PassSwing * passSwing;

        // Role expectations: what the position is expected to deliver, applied only to the position's own line.
        switch (input.Role)
        {
            case TacticalRole.Goalkeeper:
            {
                int savesAboveRoutine = Math.Max(0, saves - RoutineSaves);
                rating += SaveBonus * Math.Min(savesAboveRoutine, MaxSavesCounted);
                if (opponentGoals == 0)
                {
                    rating += CleanSheetBonus;
                }
                else
                {
                    int excessConceded = Math.Min(opponentGoals - ExemptConcededGoals, MaxConcededPenalties);
                    rating -= ConcededPenaltyPerGoal * excessConceded;
                }

                break;
            }

            case TacticalRole.Striker when shots == 0:
                rating -= StrikerNoShotPenalty;
                break;

            case TacticalRole.Winger when shots == 0:
                rating -= WingerNoShotPenalty;
                break;
        }

        // Possession: a small ±0.1; sharing the ball is a weaker signal than the player's own line.
        float possessionShare = float.IsNaN(input.TeamPossessionShare01)
            ? 0.5f
            : Math.Clamp(input.TeamPossessionShare01, 0f, 1f);
        rating += PossessionSwing * 2f * (possessionShare - 0.5f);

        // Clamp after every term has been applied so no combination of inputs can leave the contract range.
        float clamped = Math.Clamp(rating, MinimumRating, MaximumRating);
        return MathF.Round(clamped * 10f) / 10f;
    }

    /// <summary>
    /// Mean rating of the players who actually played, rounded to one decimal; <see cref="NotPlayedRating"/>
    /// when nobody played. It is a small convenience so a match report does not repeat the loop.
    /// </summary>
    /// <param name="performances">The completed performance lines of one team.</param>
    /// <param name="isHome">Whether that team was the home side.</param>
    /// <param name="teamGoals">Goals that team scored.</param>
    /// <param name="opponentGoals">Goals the opponent scored.</param>
    /// <param name="teamPossessionShare01">That team's possession share, 0..1.</param>
    /// <returns>The rounded mean rating of the playing rows, or <see cref="NotPlayedRating"/> when none played.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="performances"/> is null.</exception>
    public static float RateTeam(
        IReadOnlyList<MatchPlayerPerformance> performances,
        bool isHome,
        int teamGoals,
        int opponentGoals,
        float teamPossessionShare01)
    {
        ArgumentNullException.ThrowIfNull(performances);

        float sum = 0f;
        int playing = 0;
        for (int index = 0; index < performances.Count; index++)
        {
            MatchPlayerPerformance? row = performances[index];
            if (row is null) continue;

            float rating = Rate(
                row, isHome, teamGoals, opponentGoals, row.Role, teamPossessionShare01);
            if (rating <= NotPlayedRating) continue;

            sum += rating;
            playing++;
        }

        return playing == 0 ? NotPlayedRating : MathF.Round(sum / playing * 10f) / 10f;
    }
}
