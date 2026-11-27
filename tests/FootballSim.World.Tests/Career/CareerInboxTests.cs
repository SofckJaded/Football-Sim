using System;
using System.Collections.Generic;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;
using Xunit;

namespace FootballSim.World.Tests.Career;

public sealed class CareerInboxTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveId(int id)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(id, 1, 1, "Feedback"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Constructor_RejectsInvalidSeason(int season)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(1, season, 1, "Feedback"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(53)]
    [InlineData(-1)]
    public void Constructor_RejectsWeekOutsideOneToFiftyTwo(int week)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Feedback(1, 1, week, "Feedback"));
    }

    [Fact]
    public void Constructor_AcceptsWeekBounds()
    {
        Assert.Equal(1, Feedback(1, 1, 1, "Feedback").Week);
        Assert.Equal(52, Feedback(1, 1, 52, "Feedback").Week);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankSubject(string subject)
    {
        Assert.Throws<ArgumentException>(() => Feedback(1, 1, 1, subject));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 53)]
    [InlineData(-1, 10)]
    [InlineData(0, -1)]
    public void Constructor_RejectsInvalidExpiry(int expiresSeason, int expiresWeek)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TransferOffer(1, expiresSeason, expiresWeek));
    }

    [Fact]
    public void IsExpired_ClosesAfterTheExpiryWeek_AndZeroExpiryNeverCloses()
    {
        TransferOfferMessage expiring = TransferOffer(1, expiresSeason: 2, expiresWeek: 10);
        Assert.False(expiring.IsExpired(1, 52));
        Assert.False(expiring.IsExpired(2, 9));
        Assert.False(expiring.IsExpired(2, 10));
        Assert.True(expiring.IsExpired(2, 11));
        Assert.True(expiring.IsExpired(3, 1));
        TransferOfferMessage never = TransferOffer(2, expiresSeason: 0, expiresWeek: 40);
        Assert.Equal(0, never.ExpiresSeason);
        Assert.Equal(0, never.ExpiresWeek);
        Assert.False(never.IsExpired(99, 52));
    }

    [Fact]
    public void Constructor_RejectsInvalidMoneyKindAndTerms()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Offer(fee: -1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => Offer(wage: -1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => Offer(kind: TransferKind.Loan));
        Assert.Throws<ArgumentOutOfRangeException>(() => Offer(kind: TransferKind.YouthIntake));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TrialInviteMessage(1, 1, 1, "Trial", "Body", Club(3), -1L, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContractRenewalMessage(1, 1, 1, "Renewal", "Body", Club(1), 1, -1L, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoanOfferMessage(1, 3, 1, "Loan", "Body", Club(1), Club(2), 2, 3, 10));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContractRenewalMessage(2, 3, 1, "Renewal", "Body", Club(1), 0, 0L, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InjuryNoticeMessage(2, 3, 1, "Injury", "Body", InjuryKind.HamstringTear, InjurySeverity.Minor, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CupDrawMessage(1, 1, 1, "Cup", "Body", CupRound.None, Club(4), true, 20));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CupDrawMessage(1, 1, 1, "Cup", "Body", CupRound.RoundOf16, Club(4), true, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TrialInviteMessage(2, 3, 1, "Trial", "Body", ClubId.None, 0L, 0, 0));

        Assert.Equal(TransferKind.Free, Offer(kind: TransferKind.Free).OfferKind);
        Assert.Equal(0L, Offer(fee: 0L, wage: 0L).Fee);
    }

    [Fact]
    public void EveryMessageType_ReportsKindAndRequiresResponse()
    {
        CareerInboxMessage[] messages =
        {
            CoachReport(1), TransferOffer(2), LoanOffer(3), TrialInvite(4), ContractRenewal(5),
            ManagerFeedback(6), InjuryNotice(7), AwardNotice(8), CupDraw(9)
        };
        CareerInboxKind[] kinds =
        {
            CareerInboxKind.CoachReport, CareerInboxKind.TransferOffer, CareerInboxKind.LoanOffer, CareerInboxKind.TrialInvite, CareerInboxKind.ContractRenewal,
            CareerInboxKind.ManagerFeedback, CareerInboxKind.InjuryNotice, CareerInboxKind.AwardNotice, CareerInboxKind.CupDraw
        };
        bool[] requiresResponse = { false, true, true, true, true, false, false, false, false };

        Assert.Equal(Enum.GetValues<CareerInboxKind>().Length, messages.Length);
        for (int index = 0; index < messages.Length; index++)
        {
            Assert.Equal(kinds[index], messages[index].Kind);
            Assert.Equal(requiresResponse[index], messages[index].RequiresResponse);
        }
    }

    [Fact]
    public void AddAndGet_EnforceUniqueIdsAndResolveById()
    {
        var inbox = new CareerInbox();
        ManagerFeedbackMessage message = ManagerFeedback(5);
        inbox.Add(message);
        inbox.Add(ManagerFeedback(1));

        Assert.Same(message, inbox.Get(5));
        Assert.Null(inbox.Get(6));
        Assert.Throws<InvalidOperationException>(() => inbox.Add(CoachReport(5)));
        Assert.Throws<ArgumentNullException>(() => inbox.Add(null!));
        Assert.Equal(2, inbox.Count);
    }

    [Fact]
    public void ReadState_CountsUnreadAndMarksReadAndAllRead()
    {
        var inbox = new CareerInbox();
        inbox.Add(ManagerFeedback(1));
        inbox.Add(ManagerFeedback(2));
        inbox.Add(ManagerFeedback(3));

        Assert.Equal(3, inbox.UnreadCount);
        Assert.True(inbox.MarkRead(2));
        Assert.True(inbox.Get(2)!.IsRead);
        Assert.Equal(2, inbox.UnreadCount);
        Assert.False(inbox.MarkRead(99));
        Assert.Equal(2, inbox.UnreadCount);
        inbox.MarkAllRead();
        Assert.Equal(0, inbox.UnreadCount);
        Assert.All(inbox.Messages, message => Assert.True(message.IsRead));
    }

    [Fact]
    public void FirstAwaitingResponse_ReturnsOldestActionableAndSkipsTheRest()
    {
        var inbox = new CareerInbox();
        inbox.Add(ManagerFeedback(1));
        inbox.Add(TransferOffer(2, expiresSeason: 1, expiresWeek: 2));
        inbox.Add(TransferOffer(3, expiresSeason: 1, expiresWeek: 8));
        inbox.Add(LoanOffer(4));
        inbox.Add(TrialInvite(5));
        inbox.Get(3)!.MarkResolved();

        CareerInboxMessage? next = inbox.FirstAwaitingResponse(1, 4);

        Assert.NotNull(next);
        Assert.Equal(4, next!.Id);
        Assert.Equal(2, inbox.CountAwaitingResponse(1, 4));

        // Without an answer every actionable message eventually expires, and the clock can move again.
        Assert.Null(inbox.FirstAwaitingResponse(2, 1));
    }

    [Fact]
    public void ExpireDue_SettlesOnlyExpiredActionableMessages()
    {
        var inbox = new CareerInbox();
        inbox.Add(ManagerFeedback(1));
        inbox.Add(TransferOffer(2, expiresSeason: 1, expiresWeek: 2));
        inbox.Add(TransferOffer(3, expiresSeason: 1, expiresWeek: 8));
        inbox.Add(TransferOffer(4, expiresSeason: 1, expiresWeek: 2));
        inbox.Get(4)!.MarkResolved();

        IReadOnlyList<CareerInboxMessage> expired = inbox.ExpireDue(1, 4);

        Assert.Single(expired);
        Assert.Equal(2, expired[0].Id);
        Assert.True(inbox.Get(2)!.IsResolved);
        Assert.False(inbox.Get(1)!.IsResolved);
        Assert.False(inbox.Get(3)!.IsResolved);
        Assert.Empty(inbox.ExpireDue(1, 4));
    }

    [Fact]
    public void ResolveAll_SettlesEveryMessageOfTheKindExceptTheGivenId()
    {
        var inbox = new CareerInbox();
        inbox.Add(TransferOffer(1));
        inbox.Add(LoanOffer(2));
        inbox.Add(TransferOffer(3));
        inbox.Add(TransferOffer(4));

        inbox.ResolveAll(CareerInboxKind.TransferOffer, exceptId: 3);

        Assert.True(inbox.Get(1)!.IsResolved);
        Assert.False(inbox.Get(3)!.IsResolved);
        Assert.True(inbox.Get(4)!.IsResolved);
        Assert.False(inbox.Get(2)!.IsResolved);

        inbox.ResolveAll(CareerInboxKind.LoanOffer);
        Assert.True(inbox.Get(2)!.IsResolved);
    }

    private static ClubId Club(int value) => new(value);

    private static ManagerFeedbackMessage Feedback(int id, int season, int week, string subject) =>
        new(id, season, week, subject, "Body", "Key starter");

    private static ManagerFeedbackMessage ManagerFeedback(int id) => Feedback(id, 1, 1, "Manager feedback");

    private static CoachReportMessage CoachReport(int id) => new(id, 1, 1, "Scout report", "Body", "Rough diamond", new[] { "Good engine." });

    private static TransferOfferMessage Offer(long fee = 0L, long wage = 0L, TransferKind kind = TransferKind.Permanent) =>
        new(1, 1, 1, "Offer", "Body", ClubId.None, Club(2), fee, wage, kind, 0, 0);

    private static TransferOfferMessage TransferOffer(int id, int expiresSeason = 1, int expiresWeek = 10) =>
        new(id, 1, 1, "Transfer offer", "Body", ClubId.None, Club(2), 250_000L, 4_000L, TransferKind.Permanent, expiresSeason, expiresWeek);

    private static LoanOfferMessage LoanOffer(int id) => new(id, 1, 1, "Loan offer", "Body", Club(1), Club(2), 2, 1, 10);

    private static TrialInviteMessage TrialInvite(int id) => new(id, 1, 1, "Trial invite", "Body", Club(3), 0L, 1, 10);

    private static ContractRenewalMessage ContractRenewal(int id) => new(id, 1, 1, "Contract renewal", "Body", Club(1), 2, 6_000L, 1, 10);

    private static InjuryNoticeMessage InjuryNotice(int id) => new(id, 1, 1, "Injury notice", "Body", InjuryKind.HamstringTear, InjurySeverity.Minor, 2);

    private static AwardNoticeMessage AwardNotice(int id) => new(id, 1, 1, "Award notice", "Body", new[] { "Top scorer" });

    private static CupDrawMessage CupDraw(int id) => new(id, 1, 1, "Cup draw", "Body", CupRound.RoundOf16, Club(4), true, 20);
}
