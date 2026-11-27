using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using FootballSim.Core;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;

namespace FootballSim.World.Tests.Career;

/// <summary>
/// Shared helpers for the W4 career tests: a quiet career factory, an event pump that answers whatever the
/// career clock stops on, a live-match report builder and a deterministic state fingerprint.
///
/// <para>The fingerprint is the save/round-trip gate's measuring instrument: it walks the whole world and the
/// career in a fixed order and hashes every value that can affect a future tick. Two careers that hash the
/// same are byte-equivalent for simulation purposes, regardless of whether they arrived there directly or
/// through a save file.</para>
/// </summary>
internal static class CareerTestHarness
{
    /// <summary>Root seed used by the career tests unless a test overrides it.</summary>
    public const ulong DefaultSeed = 20261127UL;

    /// <summary>Creates a deterministic career with a valid identity and no club preference.</summary>
    public static CareerSession CreateCareer(
        CareerCreationMode mode = CareerCreationMode.Authentic,
        ulong seed = DefaultSeed,
        PotentialPreset? potential = null,
        int age = 17,
        BackstoryKind backstory = BackstoryKind.LocalClubYouth,
        Footedness? foot = null,
        ClubId startingClubId = default,
        TrainingFocus focus = TrainingFocus.Balanced)
    {
        return CareerSession.StartNewCareer(new CareerConfig
        {
            Mode = mode,
            WorldSeed = seed,
            FirstName = "Test",
            LastName = "Career",
            StartingAge = age,
            Backstory = backstory,
            Potential = potential,
            PreferredFoot = foot,
            StartingClubId = startingClubId,
            InitialTrainingFocus = focus
        });
    }

    /// <summary>How the pump answers an actionable message.</summary>
    public enum ResponsePolicy
    {
        /// <summary>Accept every offer (transfers, loans, trials, renewals).</summary>
        AcceptAll,

        /// <summary>Accept renewals/trials/free moves, decline permanent transfers and loans.</summary>
        StayPut,

        /// <summary>Accept renewals and trials only.</summary>
        RenewalsOnly
    }

    /// <summary>
    /// Advances the career until <paramref name="isDone"/> returns true, handling every event the clock stops
    /// on (matches are quick-simmed; actionable messages are answered by the policy). Returns the result that
    /// satisfied the predicate.
    /// </summary>
    public static CareerAdvanceResult AdvanceUntil(
        CareerSession session,
        Func<CareerAdvanceResult, bool> isDone,
        ResponsePolicy policy = ResponsePolicy.AcceptAll,
        int maximumEvents = 20_000)
    {
        for (int step = 0; step < maximumEvents; step++)
        {
            CareerAdvanceResult result = session.AdvanceToNextEventOrMatchday();
            if (isDone(result)) return result;

            switch (result.Kind)
            {
                case CareerEventKind.HumanMatchday:
                    session.ResolveHumanMatchViaQuickSim();
                    break;

                case CareerEventKind.InboxAction:
                    Answer(session, result.Message!, policy);
                    break;

                case CareerEventKind.NeedsClub:
                {
                    CareerInboxMessage trial = session.RequestAmateurTrial();
                    session.RespondToOffer(trial.Id, accept: true);
                    break;
                }

                case CareerEventKind.SeasonRolledOver:
                case CareerEventKind.CareerOver:
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected career event {result.Kind}.");
            }
        }

        throw new InvalidOperationException($"The career did not reach the requested event in {maximumEvents} steps.");
    }

    /// <summary>Advances until <paramref name="seasons"/> full seasons have rolled over.</summary>
    public static List<CareerAdvanceResult> AdvanceSeasons(
        CareerSession session,
        int seasons,
        ResponsePolicy policy = ResponsePolicy.AcceptAll)
    {
        var closedSeasons = new List<CareerAdvanceResult>(seasons);
        for (int season = 0; season < seasons; season++)
        {
            closedSeasons.Add(AdvanceUntil(
                session,
                result => result.Kind == CareerEventKind.SeasonRolledOver,
                policy));
        }

        return closedSeasons;
    }

    /// <summary>Advances the career by a fixed number of whole weeks (no human-matchday handling).</summary>
    public static void AdvanceWeeks(CareerSession session, int weeks, ResponsePolicy policy = ResponsePolicy.AcceptAll)
    {
        WorldDate target = WorldCalendar.AddWeeks(session.Date, weeks);
        AdvanceUntil(
            session,
            result => result.Kind == CareerEventKind.CareerOver || result.Date.CompareTo(target) >= 0,
            policy);
    }

    /// <summary>Answers one actionable message according to the policy.</summary>
    public static void Answer(CareerSession session, CareerInboxMessage message, ResponsePolicy policy)
    {
        bool accept = message.Kind switch
        {
            CareerInboxKind.ContractRenewal => true,
            CareerInboxKind.TrialInvite => true,
            CareerInboxKind.TransferOffer => policy == ResponsePolicy.AcceptAll,
            CareerInboxKind.LoanOffer => policy == ResponsePolicy.AcceptAll,
            _ => false
        };

        bool answered = session.RespondToOffer(message.Id, accept);
        if (!answered && accept && message.Kind is CareerInboxKind.TransferOffer or CareerInboxKind.LoanOffer)
        {
            // A world-refused move (budget changed under it) stays open; decline it so the clock can move.
            answered = session.RespondToOffer(message.Id, accept: false);
        }

        if (!answered)
        {
            throw new InvalidOperationException(
                $"The career refused the answer to message {message.Id} ({message.Kind}).");
        }
    }

    /// <summary>
    /// Builds a syntactically complete live match report: every starter of both sides plays 90 minutes at the
    /// given rating. Enough for <see cref="CareerSession.CommitHumanLiveMatchResult"/> to validate and apply.
    /// </summary>
    public static LiveMatchReport BuildLiveReport(
        CareerMatchContext context,
        int homeGoals,
        int awayGoals,
        bool wentToPenalties = false,
        int homePenaltyGoals = 0,
        int awayPenaltyGoals = 0)
    {
        return new LiveMatchReport
        {
            FixtureId = context.FixtureId,
            HomeGoals = homeGoals,
            AwayGoals = awayGoals,
            WentToPenalties = wentToPenalties,
            HomePenaltyGoals = homePenaltyGoals,
            AwayPenaltyGoals = awayPenaltyGoals,
            HomePlayers = LinesFor(context.HomeLineup, 7.0f),
            AwayPlayers = LinesFor(context.AwayLineup, 7.0f)
        };

        static IReadOnlyList<PlayerMatchLine> LinesFor(SelectedLineup lineup, float rating)
        {
            var lines = new PlayerMatchLine[lineup.StartingXI.Count];
            for (int index = 0; index < lines.Length; index++)
            {
                lines[index] = new PlayerMatchLine(
                    lineup.StartingXI[index],
                    Minutes: 90,
                    Goals: 0,
                    Assists: 0,
                    Shots: 1,
                    ShotsOnTarget: 0,
                    PassesAttempted: 20,
                    PassesCompleted: 15,
                    Tackles: 1,
                    Saves: 0,
                    YellowCards: 0,
                    RedCards: 0,
                    Rating: rating,
                    EndingStamina01: 0.7f,
                    ConditionCost01: 0.3f);
            }

            return lines;
        }
    }

    /// <summary>
    /// A stable, order-sensitive hash of the whole simulation state: world registries, every mutable player/
    /// club/manager field, fixtures, cup, transfer log, RNG positions, id counters and the career's own state.
    /// </summary>
    public static string Fingerprint(CareerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var stream = new MemoryStream(1 << 20);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteWorld(writer, session.World);
            WriteCareer(writer, session);
        }

        stream.Position = 0;
        using SHA256 sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static void WriteWorld(BinaryWriter writer, WorldState world)
    {
        writer.Write(world.Season);
        writer.Write(world.Week);
        writer.Write(world.Seed);

        (int lastPlayer, int lastMatch, int lastManager) = world.CaptureIdCounters();
        writer.Write(lastPlayer);
        writer.Write(lastMatch);
        writer.Write(lastManager);

        IReadOnlyDictionary<WorldRandomStream, ulong> states = world.CaptureRngStates();
        writer.Write(states.Count);
        foreach ((WorldRandomStream key, ulong value) in SortStreams(states))
        {
            writer.Write((int)key);
            writer.Write(value);
        }

        writer.Write(world.Players.Count);
        for (int index = 0; index < world.Players.All.Count; index++)
        {
            WritePlayer(writer, world.Players.All[index]);
        }

        writer.Write(world.Clubs.Count);
        for (int index = 0; index < world.Clubs.All.Count; index++)
        {
            WriteClub(writer, world.Clubs.All[index]);
        }

        writer.Write(world.Managers.Count);
        for (int index = 0; index < world.Managers.All.Count; index++)
        {
            WriteManager(writer, world.Managers.All[index]);
        }

        writer.Write(world.Leagues.Count);
        for (int index = 0; index < world.Leagues.Count; index++)
        {
            WriteLeague(writer, world.Leagues[index]);
        }

        WriteCup(writer, world.Cup);

        writer.Write(world.TransferLog.Count);
        for (int index = 0; index < world.TransferLog.Count; index++)
        {
            TransferRecord record = world.TransferLog[index];
            writer.Write(record.Season);
            writer.Write(record.Week);
            writer.Write(record.PlayerId.Value);
            writer.Write(record.FromClubId.Value);
            writer.Write(record.ToClubId.Value);
            writer.Write((int)record.Kind);
            writer.Write(record.Fee);
        }
    }

    private static void WritePlayer(BinaryWriter writer, WorldPlayer player)
    {
        writer.Write(player.Id.Value);
        writer.Write(player.FirstName);
        writer.Write(player.LastName);
        writer.Write(player.Age);
        writer.Write(player.BirthWeek);
        writer.Write((int)player.PreferredFoot);
        writer.Write(Bits(player.WeakFoot01));
        writer.Write(player.ClubId.Value);
        writer.Write(player.SuspensionWeeksRemaining);
        writer.Write(Bits(player.Condition01));
        writer.Write(Bits(player.MatchSharpness01));
        writer.Write(Bits(player.Form01));
        writer.Write(Bits(player.Visibility01));
        writer.Write((int)player.TrainingFocus);
        writer.Write(player.IsRetired);
        writer.Write((int)player.Backstory);

        InjuryState injury = player.Injury;
        writer.Write((int)injury.Kind);
        writer.Write((int)injury.Severity);
        writer.Write(injury.WeeksOutRemaining);
        writer.Write(injury.TotalWeeksOut);
        writer.Write(injury.PermanentDamageApplied);

        PlayerContract contract = player.Contract;
        writer.Write(contract.SignedSeason);
        writer.Write(contract.EndSeason);
        writer.Write(contract.WeeklyWage);
        writer.Write(contract.IsOnLoan);
        writer.Write(contract.ParentClubId.Value);
        writer.Write(contract.LoanEndSeason);

        for (int attribute = 0; attribute < AttributeMetadata.Count; attribute++)
        {
            PlayerAttributeState state = player.Attributes[(AttributeId)attribute];
            writer.Write(Bits(state.Current));
            writer.Write(Bits(state.Ceiling));
            writer.Write(Bits(state.Plasticity));
        }

        writer.Write(Bits(player.Latent.Athleticism));
        writer.Write(Bits(player.Latent.Technical));
        writer.Write(Bits(player.Latent.Mental));
        writer.Write(Bits(player.Latent.Goalkeeping));

        writer.Write(Bits(player.Personality.Resilience));
        writer.Write(Bits(player.Personality.WorkEthic));
        writer.Write(Bits(player.Personality.Creativity));
        writer.Write(Bits(player.Personality.Discipline));

        WriteSeasonStats(writer, player.CurrentSeason);
        writer.Write(player.SeasonHistory.Count);
        for (int index = 0; index < player.SeasonHistory.Count; index++)
        {
            WriteSeasonStats(writer, player.SeasonHistory[index]);
        }

        writer.Write(player.Form.Appearances.Count);
        for (int index = 0; index < player.Form.Appearances.Count; index++)
        {
            MatchAppearance appearance = player.Form.Appearances[index];
            writer.Write(appearance.Season);
            writer.Write(appearance.Week);
            writer.Write(appearance.Minutes);
            writer.Write(Bits(appearance.Rating));
        }

        writer.Write(player.Trends.Snapshots.Count);
        for (int snapshot = 0; snapshot < player.Trends.Snapshots.Count; snapshot++)
        {
            float[] values = player.Trends.Snapshots[snapshot];
            writer.Write(values.Length);
            for (int index = 0; index < values.Length; index++) writer.Write(Bits(values[index]));
        }

        for (int attribute = 0; attribute < AttributeMetadata.Count; attribute++)
        {
            writer.Write(Bits(player.CeilingNudgeApplied((AttributeId)attribute)));
        }
    }

    private static void WriteSeasonStats(BinaryWriter writer, PlayerSeasonStats stats)
    {
        writer.Write(stats.Season);
        writer.Write(stats.Appearances);
        writer.Write(stats.Starts);
        writer.Write(stats.SubstituteAppearances);
        writer.Write(stats.MinutesPlayed);
        writer.Write(stats.Goals);
        writer.Write(stats.Assists);
        writer.Write(stats.Shots);
        writer.Write(stats.PassesAttempted);
        writer.Write(stats.PassesCompleted);
        writer.Write(stats.Tackles);
        writer.Write(stats.Saves);
        writer.Write(stats.YellowCards);
        writer.Write(stats.RedCards);
        writer.Write(Bits(stats.RatingSum));
    }

    private static void WriteClub(BinaryWriter writer, WorldClub club)
    {
        writer.Write(club.Id.Value);
        writer.Write(club.Name);
        writer.Write(club.Tier);
        writer.Write(club.LeagueId.Value);
        writer.Write(club.ManagerId.Value);
        writer.Write(club.Reputation);
        writer.Write(club.Finances.Balance);
        writer.Write(club.Finances.WeeklyWageBudget);
        writer.Write(club.Finances.TransferBudget);
        writer.Write(club.Finances.WeeklyWageBill);
        writer.Write(Bits(club.Facilities.CoachingQuality01));
        writer.Write(Bits(club.Facilities.MedicalQuality01));
        writer.Write(Bits(club.Facilities.YouthQuality01));
        writer.Write(club.Facilities.WeeklyTrainingSessions);

        writer.Write(club.Squad.Count);
        for (int index = 0; index < club.Squad.Count; index++) writer.Write(club.Squad[index].Value);

        IReadOnlyDictionary<PlayerId, float> trust = club.ManagerTrust;
        var keys = new List<PlayerId>(trust.Keys);
        keys.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        writer.Write(keys.Count);
        for (int index = 0; index < keys.Count; index++)
        {
            writer.Write(keys[index].Value);
            writer.Write(Bits(trust[keys[index]]));
        }
    }

    private static void WriteManager(BinaryWriter writer, WorldManager manager)
    {
        writer.Write(manager.Id.Value);
        writer.Write(manager.FirstName);
        writer.Write(manager.LastName);
        writer.Write(manager.Age);
        writer.Write(manager.ClubId.Value);
        writer.Write((int)manager.Formation);
        writer.Write(manager.Reputation);
        writer.Write(Bits(manager.BoardConfidence01));
        writer.Write(manager.SeasonsAtClub);
        writer.Write(manager.MatchesInCharge);
        writer.Write(manager.Wins);
        writer.Write(manager.Draws);
        writer.Write(manager.Losses);

        TeamTacticalProfile profile = manager.TacticalProfile;
        writer.Write(profile.Name);
        writer.Write(Bits(profile.Directness01));
        writer.Write(Bits(profile.Tempo01));
        writer.Write(Bits(profile.AttackingWidth01));
        writer.Write(Bits(profile.DefensiveLineHeight01));
        writer.Write(Bits(profile.Compactness01));
        writer.Write(Bits(profile.PressingIntensity01));
        writer.Write(Bits(profile.RiskTolerance01));
        writer.Write(Bits(profile.CounterPressDurationSeconds));
        writer.Write(Bits(profile.PositionalFreedom01));
        writer.Write(profile.ShortGoalkeeperDistribution);
        writer.Write(Bits(profile.ManOrientation01));
    }

    private static void WriteLeague(BinaryWriter writer, WorldLeague league)
    {
        writer.Write(league.Id.Value);
        writer.Write(league.Tier);
        writer.Write(league.Name);
        writer.Write(league.ShortName);
        writer.Write(league.Clubs.Count);
        for (int index = 0; index < league.Clubs.Count; index++) writer.Write(league.Clubs[index].Value);

        writer.Write(league.Fixtures.Count);
        for (int index = 0; index < league.Fixtures.Count; index++)
        {
            WriteFixture(writer, league.Fixtures[index]);
        }
    }

    private static void WriteFixture(BinaryWriter writer, WorldFixture fixture)
    {
        writer.Write(fixture.Id.Value);
        writer.Write(fixture.Season);
        writer.Write(fixture.Week);
        writer.Write((int)fixture.Competition);
        writer.Write(fixture.LeagueId.Value);
        writer.Write(fixture.LeagueMatchday);
        writer.Write((int)fixture.CupRound);
        writer.Write(fixture.HomeClubId.Value);
        writer.Write(fixture.AwayClubId.Value);
        writer.Write(fixture.IsPlayed);
        writer.Write(fixture.HomeGoals);
        writer.Write(fixture.AwayGoals);
        writer.Write(fixture.HomePenaltyGoals);
        writer.Write(fixture.AwayPenaltyGoals);
    }

    private static void WriteCup(BinaryWriter writer, WorldCup cup)
    {
        writer.Write(cup.Season);
        writer.Write(cup.IsComplete);
        writer.Write(cup.ChampionClubId.Value);
        writer.Write(cup.Rounds.Count);

        for (int roundIndex = 0; roundIndex < cup.Rounds.Count; roundIndex++)
        {
            CupRoundBracket round = cup.Rounds[roundIndex];
            writer.Write((int)round.Round);
            writer.Write(round.Week);
            writer.Write(round.Ties.Count);
            for (int tieIndex = 0; tieIndex < round.Ties.Count; tieIndex++)
            {
                WriteFixture(writer, round.Ties[tieIndex].Fixture);
            }

            writer.Write(round.Byes.Count);
            for (int byeIndex = 0; byeIndex < round.Byes.Count; byeIndex++)
            {
                writer.Write(round.Byes[byeIndex].Value);
            }
        }
    }

    private static void WriteCareer(BinaryWriter writer, CareerSession session)
    {
        writer.Write(session.HumanPlayerId.Value);
        writer.Write((int)session.Config.Mode);
        writer.Write(session.Config.WorldSeed);
        writer.Write(session.Config.FirstName);
        writer.Write(session.Config.LastName);
        writer.Write(session.Config.StartingAge);
        writer.Write((int)session.Config.Backstory);
        writer.Write(session.Config.PreferGoalkeeper);
        writer.Write(session.Config.ShowHiddenPotential);
        writer.Write((int)session.TrainingFocus);
        writer.Write(session.SquadStatus);

        writer.Write(session.Inbox.Count);
        for (int index = 0; index < session.Inbox.Messages.Count; index++)
        {
            CareerInboxMessage message = session.Inbox.Messages[index];
            writer.Write(message.Id);
            writer.Write((int)message.Kind);
            writer.Write(message.Season);
            writer.Write(message.Week);
            writer.Write(message.Subject);
            writer.Write(message.IsRead);
            writer.Write(message.IsResolved);
            writer.Write(message.ExpiresSeason);
            writer.Write(message.ExpiresWeek);
        }

        writer.Write(session.History.SeasonsPlayed);
        for (int index = 0; index < session.History.Seasons.Count; index++)
        {
            CareerSeasonRecord season = session.History.Seasons[index];
            writer.Write(season.Season);
            writer.Write(season.ClubId.Value);
            writer.Write(season.ClubName);
            writer.Write(season.Tier);
            writer.Write(season.Appearances);
            writer.Write(season.Starts);
            writer.Write(season.SubstituteAppearances);
            writer.Write(season.MinutesPlayed);
            writer.Write(season.Goals);
            writer.Write(season.Assists);
            writer.Write(season.YellowCards);
            writer.Write(season.RedCards);
            writer.Write(Bits(season.AverageRating));
            writer.Write(season.DivisionMovement);
            writer.Write(season.Honours.Count);
            for (int honour = 0; honour < season.Honours.Count; honour++) writer.Write(season.Honours[honour]);
        }

        writer.Write(session.History.Transfers.Count);
        for (int index = 0; index < session.History.Transfers.Count; index++)
        {
            CareerTransferRecord transfer = session.History.Transfers[index];
            writer.Write(transfer.Season);
            writer.Write(transfer.Week);
            writer.Write(transfer.FromClubId.Value);
            writer.Write(transfer.ToClubId.Value);
            writer.Write((int)transfer.Kind);
            writer.Write(transfer.Fee);
        }
    }

    private static IEnumerable<(WorldRandomStream Key, ulong Value)> SortStreams(
        IReadOnlyDictionary<WorldRandomStream, ulong> states)
    {
        var keys = new List<WorldRandomStream>(states.Keys);
        keys.Sort(static (left, right) => ((int)left).CompareTo((int)right));
        for (int index = 0; index < keys.Count; index++) yield return (keys[index], states[keys[index]]);
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}
