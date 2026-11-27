using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Career;

/// <summary>The typed message families the career inbox carries (plan stage W4).</summary>
public enum CareerInboxKind
{
    /// <summary>A qualitative coach/scout report (no action; never shows raw ceilings).</summary>
    CoachReport = 0,

    /// <summary>A permanent or free move offered by another club (accept/decline).</summary>
    TransferOffer = 1,

    /// <summary>A loan offer from another club (accept/decline).</summary>
    LoanOffer = 2,

    /// <summary>An amateur trial invite for an unattached player (accept = sign).</summary>
    TrialInvite = 3,

    /// <summary>A contract renewal the club is prepared to sign (accept/decline).</summary>
    ContractRenewal = 4,

    /// <summary>Managerial feedback: squad status, season review, a completed move.</summary>
    ManagerFeedback = 5,

    /// <summary>A new injury and its expected length.</summary>
    InjuryNotice = 6,

    /// <summary>An award or honour (top scorer, champion, promotion).</summary>
    AwardNotice = 7,

    /// <summary>A cup round drawn for the player's club.</summary>
    CupDraw = 8
}

/// <summary>
/// One message in the career inbox (plan stage W4). A message is an immutable snapshot of an event — it
/// never keeps the world alive and it carries its own response window, so it can sit in a save and still be
/// answerable weeks later. Read state and the resolution state are the only mutable parts.
///
/// <para>Actionable messages (<see cref="RequiresResponse"/>) stop
/// <see cref="CareerSession.AdvanceToNextEventOrMatchday"/>: a transfer offer must be accepted or declined
/// before the career clock moves on.</para>
/// </summary>
public abstract class CareerInboxMessage
{
    /// <summary>Creates a message; ids are assigned by the session and never reused.</summary>
    /// <param name="id">Stable one-based message id inside the career.</param>
    /// <param name="season">World season the message belongs to.</param>
    /// <param name="week">World week the message belongs to (1..52).</param>
    /// <param name="subject">Short headline.</param>
    /// <param name="body">Full readable body.</param>
    /// <param name="expiresSeason">Last season the message can be answered in (0 = never expires).</param>
    /// <param name="expiresWeek">Last week inside <paramref name="expiresSeason"/> it can be answered in.</param>
    /// <exception cref="ArgumentOutOfRangeException">An id, season, week or expiry is out of range.</exception>
    /// <exception cref="ArgumentException">The subject is empty.</exception>
    protected CareerInboxMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        int expiresSeason = 0,
        int expiresWeek = 0)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), id, "Message ids are one-based.");
        if (season < 1) throw new ArgumentOutOfRangeException(nameof(season), season, "Seasons are one-based.");
        if (week is < 1 or > 52) throw new ArgumentOutOfRangeException(nameof(week), week, "Weeks run 1..52.");
        if (expiresSeason < 0) throw new ArgumentOutOfRangeException(nameof(expiresSeason));
        if (expiresWeek < 0) throw new ArgumentOutOfRangeException(nameof(expiresWeek));
        if (expiresSeason > 0 && expiresWeek is < 1 or > 52)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresWeek), expiresWeek, "An expiry needs a week inside 1..52.");
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("A message needs a subject.", nameof(subject));
        }

        Id = id;
        Season = season;
        Week = week;
        Subject = subject;
        Body = body ?? string.Empty;
        ExpiresSeason = expiresSeason;
        ExpiresWeek = expiresSeason > 0 ? expiresWeek : 0;
    }

    /// <summary>Stable one-based message id (unique inside the career).</summary>
    public int Id { get; }

    /// <summary>World season the message was created in.</summary>
    public int Season { get; }

    /// <summary>World week the message was created in.</summary>
    public int Week { get; }

    /// <summary>Short headline shown in the inbox list.</summary>
    public string Subject { get; }

    /// <summary>Full readable body.</summary>
    public string Body { get; }

    /// <summary>Which typed message this is.</summary>
    public abstract CareerInboxKind Kind { get; }

    /// <summary>True when the frontend must accept or decline before the career clock advances.</summary>
    public virtual bool RequiresResponse => false;

    /// <summary>True once the frontend has opened the message.</summary>
    public bool IsRead { get; private set; }

    /// <summary>True once the message was answered, expired or otherwise settled.</summary>
    public bool IsResolved { get; private set; }

    /// <summary>Last season the message can be answered in, or 0 when it never expires.</summary>
    public int ExpiresSeason { get; }

    /// <summary>Last week inside <see cref="ExpiresSeason"/> the message can be answered in (0 = none).</summary>
    public int ExpiresWeek { get; }

    /// <summary>True when the response window has already closed at the given calendar position.</summary>
    /// <param name="season">Current world season.</param>
    /// <param name="week">Current world week (1..52).</param>
    public bool IsExpired(int season, int week) =>
        ExpiresSeason > 0 && (season > ExpiresSeason || (season == ExpiresSeason && week > ExpiresWeek));

    /// <summary>
    /// True when the frontend is expected to answer this message now: it needs a response, has not been
    /// settled and its window has not closed.
    /// </summary>
    /// <param name="season">Current world season.</param>
    /// <param name="week">Current world week (1..52).</param>
    public bool IsAwaitingResponse(int season, int week) =>
        RequiresResponse && !IsResolved && !IsExpired(season, week);

    /// <summary>Marks the message as opened.</summary>
    public void MarkRead() => IsRead = true;

    /// <summary>Marks the message as settled (answered, expired or superseded).</summary>
    public void MarkResolved() => IsResolved = true;

    /// <inheritdoc />
    public override string ToString() => $"#{Id} [{Kind}] S{Season}W{Week:00} {Subject}";
}

/// <summary>A qualitative coach/scout report delivered as an inbox message (plan §4.6).</summary>
public sealed class CoachReportMessage : CareerInboxMessage
{
    /// <summary>Creates a coach-report message.</summary>
    public CoachReportMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        string summary,
        IReadOnlyList<string> sentences)
        : base(id, season, week, subject, body)
    {
        Summary = summary ?? string.Empty;
        Sentences = sentences ?? Array.Empty<string>();
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.CoachReport;

    /// <summary>One fixed short verdict, e.g. "Rough diamond".</summary>
    public string Summary { get; }

    /// <summary>The report's qualitative sentences, in generation order.</summary>
    public IReadOnlyList<string> Sentences { get; }
}

/// <summary>A permanent or free transfer offer from another club (accept/decline).</summary>
public sealed class TransferOfferMessage : CareerInboxMessage
{
    /// <summary>Creates a transfer-offer message.</summary>
    public TransferOfferMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        ClubId fromClubId,
        ClubId toClubId,
        long fee,
        long weeklyWage,
        TransferKind offerKind,
        int expiresSeason,
        int expiresWeek)
        : base(id, season, week, subject, body, expiresSeason, expiresWeek)
    {
        if (!toClubId.IsValid) throw new ArgumentOutOfRangeException(nameof(toClubId), "An offer needs a club.");
        if (fee < 0L) throw new ArgumentOutOfRangeException(nameof(fee));
        if (weeklyWage < 0L) throw new ArgumentOutOfRangeException(nameof(weeklyWage));
        if (offerKind is not (TransferKind.Permanent or TransferKind.Free))
        {
            throw new ArgumentOutOfRangeException(nameof(offerKind), offerKind, "A transfer offer is permanent or free.");
        }

        FromClubId = fromClubId;
        ToClubId = toClubId;
        Fee = fee;
        WeeklyWage = weeklyWage;
        OfferKind = offerKind;
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.TransferOffer;

    /// <inheritdoc />
    public override bool RequiresResponse => true;

    /// <summary>The club the player would leave, or <see cref="ClubId.None"/> for a free agent.</summary>
    public ClubId FromClubId { get; }

    /// <summary>The club making the offer.</summary>
    public ClubId ToClubId { get; }

    /// <summary>The fee the buyer would pay (0 for a free move).</summary>
    public long Fee { get; }

    /// <summary>The weekly wage the buyer would pay.</summary>
    public long WeeklyWage { get; }

    /// <summary>Permanent transfer or free move.</summary>
    public TransferKind OfferKind { get; }
}

/// <summary>A loan offer from another club (accept/decline).</summary>
public sealed class LoanOfferMessage : CareerInboxMessage
{
    /// <summary>Creates a loan-offer message.</summary>
    public LoanOfferMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        ClubId fromClubId,
        ClubId toClubId,
        int loanEndSeason,
        int expiresSeason,
        int expiresWeek)
        : base(id, season, week, subject, body, expiresSeason, expiresWeek)
    {
        if (!fromClubId.IsValid) throw new ArgumentOutOfRangeException(nameof(fromClubId));
        if (!toClubId.IsValid) throw new ArgumentOutOfRangeException(nameof(toClubId));
        if (loanEndSeason < season) throw new ArgumentOutOfRangeException(nameof(loanEndSeason));

        FromClubId = fromClubId;
        ToClubId = toClubId;
        LoanEndSeason = loanEndSeason;
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.LoanOffer;

    /// <inheritdoc />
    public override bool RequiresResponse => true;

    /// <summary>The parent club (registration stays his).</summary>
    public ClubId FromClubId { get; }

    /// <summary>The club taking the player on loan.</summary>
    public ClubId ToClubId { get; }

    /// <summary>Season after which the loan ends and the player returns.</summary>
    public int LoanEndSeason { get; }
}

/// <summary>An amateur trial invite for an unattached player (accept = sign for the club).</summary>
public sealed class TrialInviteMessage : CareerInboxMessage
{
    /// <summary>Creates a trial-invite message.</summary>
    public TrialInviteMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        ClubId clubId,
        long weeklyWage,
        int expiresSeason,
        int expiresWeek)
        : base(id, season, week, subject, body, expiresSeason, expiresWeek)
    {
        if (!clubId.IsValid) throw new ArgumentOutOfRangeException(nameof(clubId));
        if (weeklyWage < 0L) throw new ArgumentOutOfRangeException(nameof(weeklyWage));

        ClubId = clubId;
        WeeklyWage = weeklyWage;
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.TrialInvite;

    /// <inheritdoc />
    public override bool RequiresResponse => true;

    /// <summary>The club offering the trial.</summary>
    public ClubId ClubId { get; }

    /// <summary>The weekly wage the club would pay (0 for amateur terms).</summary>
    public long WeeklyWage { get; }
}

/// <summary>A contract renewal the club is prepared to sign (accept/decline).</summary>
public sealed class ContractRenewalMessage : CareerInboxMessage
{
    /// <summary>Creates a contract-renewal message.</summary>
    public ContractRenewalMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        ClubId clubId,
        int seasons,
        long weeklyWage,
        int expiresSeason,
        int expiresWeek)
        : base(id, season, week, subject, body, expiresSeason, expiresWeek)
    {
        if (!clubId.IsValid) throw new ArgumentOutOfRangeException(nameof(clubId));
        if (seasons < 1) throw new ArgumentOutOfRangeException(nameof(seasons));
        if (weeklyWage < 0L) throw new ArgumentOutOfRangeException(nameof(weeklyWage));

        ClubId = clubId;
        Seasons = seasons;
        WeeklyWage = weeklyWage;
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.ContractRenewal;

    /// <inheritdoc />
    public override bool RequiresResponse => true;

    /// <summary>The club offering the renewal.</summary>
    public ClubId ClubId { get; }

    /// <summary>Contract length in seasons.</summary>
    public int Seasons { get; }

    /// <summary>Weekly wage the renewal would pay.</summary>
    public long WeeklyWage { get; }
}

/// <summary>Managerial feedback: squad status, a season review or a completed move.</summary>
public sealed class ManagerFeedbackMessage : CareerInboxMessage
{
    /// <summary>Creates a manager-feedback message.</summary>
    public ManagerFeedbackMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        string verdict)
        : base(id, season, week, subject, body)
    {
        Verdict = verdict ?? string.Empty;
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.ManagerFeedback;

    /// <summary>Short managerial verdict, e.g. "Key Starter" or "Not in my plans".</summary>
    public string Verdict { get; }
}

/// <summary>A new injury and its expected length.</summary>
public sealed class InjuryNoticeMessage : CareerInboxMessage
{
    /// <summary>Creates an injury-notice message.</summary>
    public InjuryNoticeMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        InjuryKind injury,
        InjurySeverity severity,
        int weeksOut)
        : base(id, season, week, subject, body)
    {
        if (injury == InjuryKind.None) throw new ArgumentOutOfRangeException(nameof(injury));
        if (severity == InjurySeverity.None) throw new ArgumentOutOfRangeException(nameof(severity));
        if (weeksOut <= 0) throw new ArgumentOutOfRangeException(nameof(weeksOut));

        Injury = injury;
        Severity = severity;
        WeeksOut = weeksOut;
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.InjuryNotice;

    /// <summary>The injury kind.</summary>
    public InjuryKind Injury { get; }

    /// <summary>The injury's severity band.</summary>
    public InjurySeverity Severity { get; }

    /// <summary>Weeks the player is expected to miss.</summary>
    public int WeeksOut { get; }
}

/// <summary>An award or honour delivered to the inbox.</summary>
public sealed class AwardNoticeMessage : CareerInboxMessage
{
    /// <summary>Creates an award-notice message.</summary>
    public AwardNoticeMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        IReadOnlyList<string> awards)
        : base(id, season, week, subject, body)
    {
        Awards = awards ?? Array.Empty<string>();
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.AwardNotice;

    /// <summary>The awards won, in display order.</summary>
    public IReadOnlyList<string> Awards { get; }
}

/// <summary>A cup round drawn for the player's club.</summary>
public sealed class CupDrawMessage : CareerInboxMessage
{
    /// <summary>Creates a cup-draw message.</summary>
    public CupDrawMessage(
        int id,
        int season,
        int week,
        string subject,
        string body,
        CupRound round,
        ClubId opponentClubId,
        bool isHome,
        int matchWeek)
        : base(id, season, week, subject, body)
    {
        if (round == CupRound.None) throw new ArgumentOutOfRangeException(nameof(round));
        if (!opponentClubId.IsValid) throw new ArgumentOutOfRangeException(nameof(opponentClubId));
        if (matchWeek is < 1 or > 52) throw new ArgumentOutOfRangeException(nameof(matchWeek));

        Round = round;
        OpponentClubId = opponentClubId;
        IsHome = isHome;
        MatchWeek = matchWeek;
    }

    /// <inheritdoc />
    public override CareerInboxKind Kind => CareerInboxKind.CupDraw;

    /// <summary>Which cup round was drawn.</summary>
    public CupRound Round { get; }

    /// <summary>The opponent.</summary>
    public ClubId OpponentClubId { get; }

    /// <summary>True when the player's club is at home.</summary>
    public bool IsHome { get; }

    /// <summary>Week the tie is played.</summary>
    public int MatchWeek { get; }
}

/// <summary>
/// The career's message list (plan stage W4): append-only typed messages with read state, a response window
/// and an explicit resolution state. The inbox is part of the saved career, so the order is stable
/// (creation order) and every mutation is deterministic.
/// </summary>
public sealed class CareerInbox
{
    private readonly List<CareerInboxMessage> _messages = new();

    /// <summary>Every message, newest last (creation order).</summary>
    public IReadOnlyList<CareerInboxMessage> Messages => _messages;

    /// <summary>Number of messages.</summary>
    public int Count => _messages.Count;

    /// <summary>Messages not opened yet.</summary>
    public int UnreadCount
    {
        get
        {
            int count = 0;
            for (int index = 0; index < _messages.Count; index++)
            {
                if (!_messages[index].IsRead) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Adds a message. Ids must be unique; the session allocates them in creation order so a saved inbox
    /// loads back with the identical ordering and ids.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The id is already used.</exception>
    public void Add(CareerInboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Get(message.Id) is not null)
        {
            throw new InvalidOperationException($"Inbox message id {message.Id} is already used.");
        }

        _messages.Add(message);
    }

    /// <summary>Finds a message by id, or null.</summary>
    /// <param name="id">Message id.</param>
    public CareerInboxMessage? Get(int id)
    {
        for (int index = 0; index < _messages.Count; index++)
        {
            if (_messages[index].Id == id) return _messages[index];
        }
        return null;
    }

    /// <summary>Marks one message as read; returns false when the id is unknown.</summary>
    /// <param name="id">Message id.</param>
    public bool MarkRead(int id)
    {
        CareerInboxMessage? message = Get(id);
        if (message is null) return false;
        message.MarkRead();
        return true;
    }

    /// <summary>Marks every message as read.</summary>
    public void MarkAllRead()
    {
        for (int index = 0; index < _messages.Count; index++) _messages[index].MarkRead();
    }

    /// <summary>
    /// The first message the frontend still has to answer (oldest first), or null. This is the career's
    /// "urgent offer" stop condition.
    /// </summary>
    /// <param name="season">Current world season.</param>
    /// <param name="week">Current world week (1..52).</param>
    public CareerInboxMessage? FirstAwaitingResponse(int season, int week)
    {
        for (int index = 0; index < _messages.Count; index++)
        {
            if (_messages[index].IsAwaitingResponse(season, week)) return _messages[index];
        }
        return null;
    }

    /// <summary>Number of messages still waiting for an answer at the given calendar position.</summary>
    /// <param name="season">Current world season.</param>
    /// <param name="week">Current world week (1..52).</param>
    public int CountAwaitingResponse(int season, int week)
    {
        int count = 0;
        for (int index = 0; index < _messages.Count; index++)
        {
            if (_messages[index].IsAwaitingResponse(season, week)) count++;
        }
        return count;
    }

    /// <summary>
    /// Settles every unanswered message whose response window has closed. Called once per advanced week so a
    /// stale offer can never block the career clock.
    /// </summary>
    /// <param name="season">Current world season.</param>
    /// <param name="week">Current world week (1..52).</param>
    /// <returns>The messages that just expired.</returns>
    public IReadOnlyList<CareerInboxMessage> ExpireDue(int season, int week)
    {
        List<CareerInboxMessage>? expired = null;
        for (int index = 0; index < _messages.Count; index++)
        {
            CareerInboxMessage message = _messages[index];
            if (!message.RequiresResponse || message.IsResolved || !message.IsExpired(season, week)) continue;

            message.MarkResolved();
            (expired ??= new List<CareerInboxMessage>()).Add(message);
        }

        return expired is null ? Array.Empty<CareerInboxMessage>() : expired.AsReadOnly();
    }

    /// <summary>Settles every message of one kind that still awaits a response (e.g. competing offers).</summary>
    /// <param name="kind">Message family to settle.</param>
    /// <param name="exceptId">Message id to leave untouched (the one being answered).</param>
    public void ResolveAll(CareerInboxKind kind, int exceptId = 0)
    {
        for (int index = 0; index < _messages.Count; index++)
        {
            CareerInboxMessage message = _messages[index];
            if (message.Kind != kind || message.Id == exceptId || !message.RequiresResponse) continue;
            if (message.IsResolved) continue;
            message.MarkResolved();
        }
    }
}
