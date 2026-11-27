using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FootballSim.Core;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;
using Xunit;

namespace FootballSim.World.Tests.Career;

/// <summary>
/// W4 gate tests for the career facade: creation, the event-driven clock, quick-sim and live-match resolution,
/// the five-season playthrough in both creation modes, and determinism.
/// </summary>
public sealed class CareerSessionTests
{
    [Fact]
    public void StartNewCareer_Authentic_PlacesPlayerAtAnAmateurClubWithAContract()
    {
        CareerSession session = CareerTestHarness.CreateCareer(backstory: BackstoryKind.LocalClubYouth);

        Assert.Equal(108, session.World.Clubs.Count);
        Assert.Equal(2_593, session.World.Players.Count); // 2,592 generated + the career player
        Assert.True(session.HumanPlayerId.IsValid);
        Assert.Equal(CareerCreationMode.Authentic, session.Config.Mode);
        Assert.Equal(17, session.HumanPlayer.Age);
        Assert.NotNull(session.HumanClub);
        Assert.Equal(6, session.HumanClub!.Tier);
        Assert.Contains(session.HumanPlayerId, session.HumanClub.Squad);
        Assert.True(session.HumanPlayer.Contract.EndSeason >= session.Season);
        Assert.False(session.IsCareerOver);
        Assert.True(session.CanSave);
        Assert.Equal(new WorldDate(1, 1), session.Date);
        Assert.Equal(TrainingFocus.Balanced, session.TrainingFocus);
        Assert.NotEmpty(session.Inbox.Messages);
        Assert.Equal(0, session.History.SeasonsPlayed);
        Assert.True(session.World.IsExternallyManaged(session.HumanPlayerId));
    }

    [Fact]
    public void StartNewCareer_AcademyBackground_StartsOneDivisionHigher()
    {
        CareerSession elite = CareerTestHarness.CreateCareer(backstory: BackstoryKind.EliteAcademy);
        CareerSession street = CareerTestHarness.CreateCareer(backstory: BackstoryKind.StreetFootball);

        Assert.Equal(5, elite.HumanClub!.Tier);
        Assert.Equal(6, street.HumanClub!.Tier);
    }

    [Fact]
    public void StartNewCareer_Goalkeeper_RollsAKeeperCeilingAndJoinsAKeeperShortClub()
    {
        CareerSession keeper = CareerSession.StartNewCareer(new CareerConfig
        {
            FirstName = "Keeper",
            LastName = "Test",
            StartingAge = 17,
            PreferGoalkeeper = true,
            Backstory = BackstoryKind.LocalClubYouth
        });

        CareerSession outfield = CareerSession.StartNewCareer(new CareerConfig
        {
            FirstName = "Keeper",
            LastName = "Test",
            StartingAge = 17,
            PreferGoalkeeper = false,
            Backstory = BackstoryKind.LocalClubYouth
        });

        Assert.Equal(6, keeper.HumanClub!.Tier);
        Assert.True(
            keeper.HumanPlayer.Attributes.Ceiling(AttributeId.Goalkeeping) >
            outfield.HumanPlayer.Attributes.Ceiling(AttributeId.Goalkeeping),
            "A keeper prospect must roll a keeper's hands, not an outfielder's.");
    }

    [Fact]
    public void StartNewCareer_CustomPotential_DrivesTheHiddenCeilings()
    {
        CareerSession journeyman = CareerTestHarness.CreateCareer(
            CareerCreationMode.Custom, potential: PotentialPreset.Journeyman);
        CareerSession generational = CareerTestHarness.CreateCareer(
            CareerCreationMode.Custom, potential: PotentialPreset.Generational);

        float journeymanCeiling = MeanCeiling(journeyman.HumanPlayer);
        float generationalCeiling = MeanCeiling(generational.HumanPlayer);

        Assert.True(generationalCeiling > journeymanCeiling + 15f,
            $"Generational ({generationalCeiling:0.0}) should out-ceil Journeyman ({journeymanCeiling:0.0}).");
        foreach (AttributeId attribute in Enum.GetValues<AttributeId>())
        {
            Assert.True(generational.HumanPlayer.Attributes.Ceiling(attribute) >=
                        journeyman.HumanPlayer.Attributes.Ceiling(attribute));
        }
    }

    [Fact]
    public void StartNewCareer_PreferredFoot_IsHonoured()
    {
        CareerSession left = CareerTestHarness.CreateCareer(foot: Footedness.Left);
        CareerSession right = CareerTestHarness.CreateCareer(foot: Footedness.Right);

        Assert.Equal(Footedness.Left, left.HumanPlayer.PreferredFoot);
        Assert.Equal(Footedness.Right, right.HumanPlayer.PreferredFoot);
    }

    [Fact]
    public void GetPlayerPublicView_HidesCeilingsAndConsumesNoSimulationRandomness()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        string before = CareerTestHarness.Fingerprint(session);

        PlayerPublicView view = session.GetPlayerPublicView();
        PlayerPublicView again = session.GetPlayerPublicView();

        Assert.Equal(18, view.AttributeGroups.Sum(group => group.Attributes.Count));
        Assert.Equal(3, view.AttributeGroups.Count);
        Assert.DoesNotContain(view.PrimaryRole, view.SecondaryRoles);
        Assert.False(string.IsNullOrWhiteSpace(view.CoachReport.Text));
        Assert.Equal(view.CoachReport.Text, again.CoachReport.Text);
        AssertNoForbiddenMembers(typeof(PlayerPublicView));

        Assert.Equal(before, CareerTestHarness.Fingerprint(session));
        Assert.Equal(
            (int)MathF.Round(session.HumanPlayer.Attributes.Current(AttributeId.TopSpeed), MidpointRounding.AwayFromZero),
            view.AttributeGroups.SelectMany(group => group.Attributes).First(row => row.Id == AttributeId.TopSpeed).Current);
    }

    [Fact]
    public void SetTrainingFocus_IsKeptByTheRunnerAndRejectsUnknownFocuses()
    {
        CareerSession session = CareerTestHarness.CreateCareer();

        session.SetTrainingFocus(TrainingFocus.StrengthAndPower);
        Assert.Equal(TrainingFocus.StrengthAndPower, session.HumanPlayer.TrainingFocus);

        CareerTestHarness.AdvanceWeeks(session, 6);
        Assert.Equal(TrainingFocus.StrengthAndPower, session.HumanPlayer.TrainingFocus);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetTrainingFocus((TrainingFocus)42));
    }

    [Fact]
    public void AdvanceToNextEventOrMatchday_StopsAtTheFirstMatchdayAndQuickSimPlaysIt()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerAdvanceResult firstEvent = CareerTestHarness.AdvanceUntil(
            session, result => result.Kind == CareerEventKind.HumanMatchday);

        WorldFixture fixture = firstEvent.Fixture!;
        Assert.Equal(WorldCalendar.LeagueFirstHalfFirstWeek, firstEvent.Date.Week);
        Assert.True(fixture.Involves(session.HumanPlayer.ClubId));
        Assert.False(fixture.IsPlayed);
        // Week 5 carries league matchday 1 and (for amateur clubs) the cup preliminary round.
        Assert.NotEmpty(session.HumanFixturesThisWeek());

        IReadOnlyList<CareerMatchResult> results = session.ResolveHumanMatchViaQuickSim();
        Assert.NotEmpty(results);
        Assert.True(session.HasQueuedMatchResult);
        Assert.False(session.CanSave);

        CareerAdvanceResult next = session.AdvanceToNextEventOrMatchday();
        Assert.True(fixture.IsPlayed);
        Assert.False(string.IsNullOrEmpty(next.Description));
        Assert.False(session.HasQueuedMatchResult);
        Assert.True(session.CanSave);
    }

    [Fact]
    public void PrepareAndCommitLiveMatch_AppliesTheReportedScoreAndPlayerLines()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        CareerTestHarness.AdvanceUntil(session, result => result.Kind == CareerEventKind.HumanMatchday);

        CareerMatchContext context = session.PrepareHumanLiveMatch();
        Assert.True(context.FixtureId.IsValid);
        Assert.Equal(11, context.HomeLineup.StartingXI.Count);
        Assert.Equal(7, context.HomeLineup.Bench.Count);
        Assert.InRange(context.HumanShirtNumber, 1, 18);
        Assert.Equal(context.HumanInStartingXI, context.HumanShirtNumber is >= 1 and <= 11);

        Assert.Throws<InvalidOperationException>(() => session.AdvanceToNextEventOrMatchday());
        Assert.False(session.CanSave);

        LiveMatchReport report = CareerTestHarness.BuildLiveReport(context, homeGoals: 2, awayGoals: 1);
        CareerMatchResult result = session.CommitHumanLiveMatchResult(report);

        Assert.Equal(2, result.HomeGoals);
        Assert.Equal(1, result.AwayGoals);
        Assert.Equal(context.HumanInStartingXI, result.HumanStarted);
        Assert.False(session.HasPreparedLiveMatch);
        Assert.True(session.HasQueuedMatchResult);
        Assert.False(session.CanSave); // the result is queued until the week applies it

        session.AdvanceToNextEventOrMatchday();
        Assert.True(session.CanSave);
        WorldFixture played = session.World.CurrentSeasonFixtures().First(f => f.Id == context.FixtureId);
        Assert.True(played.IsPlayed);
        Assert.Equal(3, played.HomeGoals + played.AwayGoals);
    }

    [Fact]
    public void RespondToOffer_AcceptingATrial_SignsAClublessPlayer()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        WorldClub club = session.HumanClub!;
        club.RemovePlayer(session.HumanPlayerId);
        session.HumanPlayer.ClubId = ClubId.None;

        CareerAdvanceResult clubless = session.AdvanceToNextEventOrMatchday();
        Assert.Equal(CareerEventKind.NeedsClub, clubless.Kind);
        Assert.Equal("Free agent", session.SquadStatus);

        CareerInboxMessage invite = session.RequestAmateurTrial();
        Assert.Equal(CareerInboxKind.TrialInvite, invite.Kind);
        Assert.True(session.RespondToOffer(invite.Id, accept: true));

        Assert.True(session.HumanPlayer.ClubId.IsValid);
        Assert.NotEqual(club.Id, session.HumanPlayer.ClubId);
        Assert.True(session.HumanPlayer.Contract.EndSeason >= session.Season);
        Assert.Contains(session.HumanPlayerId, session.HumanClub!.Squad);
        Assert.False(session.RespondToOffer(invite.Id, accept: true)); // already resolved → false
    }

    [Fact]
    public void RequestAmateurTrial_WhenEmployed_IsRejected()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        Assert.Throws<InvalidOperationException>(() => session.RequestAmateurTrial());
    }

    [Fact]
    public void PlayFiveSeasons_Authentic_RecordsTheLedgerCoachReportsAndTraining()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        TrainingFocus[] focuses =
        {
            TrainingFocus.PassingAndVision,
            TrainingFocus.StrengthAndPower,
            TrainingFocus.FinishingAndShooting,
            TrainingFocus.AgilityAndQuickness,
            TrainingFocus.PassingAndVision
        };

        var closed = new List<CareerAdvanceResult>(5);
        for (int season = 0; season < 5; season++)
        {
            session.SetTrainingFocus(focuses[season]);
            closed.Add(CareerTestHarness.AdvanceUntil(
                session, result => result.Kind == CareerEventKind.SeasonRolledOver));
        }

        Assert.Equal(5, closed.Count);
        Assert.Equal(5, session.History.SeasonsPlayed);
        Assert.Equal(5, session.Season - 1);
        for (int index = 0; index < 5; index++)
        {
            CareerSeasonRecord season = session.History.Seasons[index];
            Assert.Equal(index + 1, season.Season);
            Assert.True(season.Tier is >= 1 and <= 6);
            Assert.False(string.IsNullOrWhiteSpace(season.ClubName));
        }

        Assert.True(session.Inbox.Messages.Count(message => message.Kind == CareerInboxKind.CoachReport) >= 6);
        Assert.Equal(focuses[^1], session.TrainingFocus);
        Assert.True(session.HumanPlayer.Age >= 22);
        Assert.False(session.IsCareerOver);
        Assert.True(session.CanSave);
        Assert.Equal(5, closed[^1].SeasonSummary!.Season);

        // The five seasons actually played football and the accepted offers moved the player at least once:
        // both are the playthrough's evidence that matches, growth and career moves were exercised.
        Assert.True(session.History.TotalAppearances > 0, "The career never appeared in a match.");
        Assert.NotEmpty(session.History.Transfers);
        Assert.Contains(session.History.Seasons, season => season.Appearances > 0);
    }

    [Fact]
    public void PlayFiveSeasons_Custom_RecordsTheLedgerAndKeepsTheCreationMode()
    {
        CareerSession session = CareerTestHarness.CreateCareer(
            CareerCreationMode.Custom,
            potential: PotentialPreset.ProProspect,
            age: 18,
            backstory: BackstoryKind.RegionalAcademy);

        List<CareerAdvanceResult> closed = CareerTestHarness.AdvanceSeasons(session, 5);

        Assert.Equal(5, closed.Count);
        Assert.Equal(5, session.History.SeasonsPlayed);
        Assert.Equal(CareerCreationMode.Custom, session.Config.Mode);
        Assert.Equal(PotentialPreset.ProProspect, session.Config.Potential);
        Assert.Equal(23, session.HumanPlayer.Age);
        Assert.True(MeanCeiling(session.HumanPlayer) > 55f);
        Assert.True(session.History.TotalAppearances >= 0);

        // The player view stays fogged in Custom mode too (the numbers are a UI choice, the model is one).
        AssertNoForbiddenMembers(typeof(PlayerPublicView));
    }

    [Fact]
    public void SameSeedAndSameChoices_ProduceIdenticalState()
    {
        CareerSession first = CareerTestHarness.CreateCareer();
        CareerSession second = CareerTestHarness.CreateCareer();

        CareerTestHarness.AdvanceWeeks(first, 12);
        CareerTestHarness.AdvanceWeeks(second, 12);

        Assert.Equal(CareerTestHarness.Fingerprint(first), CareerTestHarness.Fingerprint(second));
    }

    [Fact]
    public void DifferentSeed_ProducesADifferentWorld()
    {
        CareerSession first = CareerTestHarness.CreateCareer(seed: 1111UL);
        CareerSession second = CareerTestHarness.CreateCareer(seed: 2222UL);

        Assert.NotEqual(CareerTestHarness.Fingerprint(first), CareerTestHarness.Fingerprint(second));
    }

    [Fact]
    public void CurrentLeagueTableAndNextFixture_DescribeThePlayersDivision()
    {
        CareerSession session = CareerTestHarness.CreateCareer();

        IReadOnlyList<LeagueTableRow> table = session.CurrentLeagueTable();
        Assert.Equal(18, table.Count);
        Assert.All(table, row => Assert.Equal(session.HumanClub!.Tier, session.World.Clubs[row.ClubId].Tier));

        WorldFixture? next = session.NextFixture();
        Assert.NotNull(next);
        Assert.True(next!.Involves(session.HumanPlayer.ClubId));
        Assert.False(next.IsPlayed);
        Assert.True(next.Week >= session.Date.Week);
    }

    [Fact]
    public void TrainingPlanner_RecommendedFocusMatchesTheAiChoice()
    {
        CareerSession session = CareerTestHarness.CreateCareer();
        TrainingFocus suggested = TrainingPlanner.RecommendedFocusFor(session.HumanPlayer);
        Assert.Equal(TrainingPlanner.RecommendedFocusFor(IdentityEvaluator.Evaluate(session.HumanPlayer).PrimaryRole), suggested);
    }

    private static float MeanCeiling(WorldPlayer player)
    {
        float total = 0f;
        foreach (AttributeId attribute in Enum.GetValues<AttributeId>())
        {
            total += player.Attributes.Ceiling(attribute);
        }
        return total / AttributeMetadata.Count;
    }

    /// <summary>
    /// Walks the public-view object graph and proves structurally that no raw ceiling, plasticity or latent
    /// value can be reached from it (the plan's fog-of-war gate).
    /// </summary>
    private static void AssertNoForbiddenMembers(Type root)
    {
        var visited = new HashSet<Type>();
        Visit(root);

        void Visit(Type type)
        {
            if (!visited.Add(type)) return;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                string name = property.Name;
                Assert.DoesNotContain("Ceiling", name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Plasticity", name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Latent", name, StringComparison.OrdinalIgnoreCase);

                Type propertyType = property.PropertyType;
                if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                {
                    Visit(propertyType.GetGenericArguments()[0]);
                }
                else if (propertyType.Assembly == typeof(PlayerPublicView).Assembly && !propertyType.IsEnum)
                {
                    Visit(propertyType);
                }
            }
        }
    }
}
