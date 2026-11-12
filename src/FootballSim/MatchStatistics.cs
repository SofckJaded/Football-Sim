using System;
using System.Collections.Generic;
using FootballSim.Core;

namespace FootballSim;

/// <summary>
/// Match statistics derived from the execution layer's authoritative contacts (audit F17).
///
/// The old engine counted a pass or a shot from the *command* it had just decided, using its own
/// distance filter, before execution arbitration had run. A kick that a rival intercepted, that the
/// player could not physically reach, or that was refused for a header was still counted, and
/// through balls were omitted from the pass count altogether. Here:
///
/// <list type="bullet">
/// <item>an attempt exists only when the execution layer actually played the ball (a
/// <see cref="BallContactKind.Kick"/> contact), so a lost arbitration can never count;</item>
/// <item>every pass type is counted exactly once through <see cref="KickIntents"/>;</item>
/// <item>attempts and completions are separate: a pass completes when a team-mate controls the ball
/// next, and is only incomplete/intercepted when somebody else does or it is played again.</item>
/// </list>
/// </summary>
public sealed class MatchStatisticsTracker
{
    /// <summary>A pass that has not been controlled within this window is no longer completable.</summary>
    public const float MaxPassCompletionSeconds = 6.0f;

    private readonly MatchAnalyticsSummary _summary;

    private TeamId? _passTeam;
    private int _passerId;
    private int? _intendedReceiverId;
    private int _passTick;

    public MatchStatisticsTracker(MatchAnalyticsSummary summary)
    {
        _summary = summary;
    }

    /// <summary>True while a pass is in flight and can still be completed or intercepted.</summary>
    public bool HasPassInFlight => _passTeam.HasValue;

    /// <summary>Team of the pass in flight, if any (diagnostics).</summary>
    public TeamId? PassInFlightTeam => _passTeam;

    /// <summary>Intended receiver of the pass in flight, if it had one (diagnostics).</summary>
    public int? PassInFlightReceiverId => _intendedReceiverId;

    /// <summary>
    /// A whistle or a reset means the passer's flight is over: a ball that comes down later must not
    /// be scored as a completed pass (audit F17).
    /// </summary>
    public void ResetPassPhase()
    {
        _passTeam = null;
        _intendedReceiverId = null;
    }

    public void ApplyContacts(IReadOnlyList<BallContactEvent>? contacts, int currentTick)
    {
        ExpirePass(currentTick);
        if (contacts is null) return;

        for (int i = 0; i < contacts.Count; i++)
            ApplyContact(contacts[i], currentTick);
    }

    private void ApplyContact(in BallContactEvent contact, int currentTick)
    {
        switch (contact.Kind)
        {
            case BallContactKind.Kick:
            {
                // A team-mate playing the ball straight out of the air (a one-touch flick, a
                // first-time pass onwards) still means the pass reached a team-mate: it completes.
                // The passer kicking his own ball again, or an opponent clearing it, does not.
                if (_passTeam is TeamId passingTeam &&
                    contact.Team == passingTeam &&
                    contact.PlayerId != _passerId)
                {
                    AddPassCompletion(passingTeam);
                }

                // The previous flight is over either way; this kick starts the next one.
                ResetPassPhase();

                if (KickIntents.IsPass(contact.Intent))
                {
                    AddPassAttempt(contact.Team);
                    _passTeam = contact.Team;
                    _passerId = contact.PlayerId;
                    _intendedReceiverId = contact.IntendedReceiverId;
                    _passTick = currentTick;
                }
                else if (KickIntents.IsShot(contact.Intent))
                {
                    AddShot(contact.Team);
                }
                break;
            }

            case BallContactKind.Collection:
            case BallContactKind.Tackle:
            case BallContactKind.Save:
                // Control arrives: a team-mate other than the passer completes the pass, an
                // opponent (or the passer re-touching their own ball) only ends it.
                if (_passTeam is TeamId controlledBy)
                {
                    if (contact.Team == controlledBy && contact.PlayerId != _passerId)
                        AddPassCompletion(controlledBy);
                    ResetPassPhase();
                }
                break;
        }
    }

    private void ExpirePass(int currentTick)
    {
        if (!_passTeam.HasValue) return;
        int maxTicks = (int)(MaxPassCompletionSeconds / SimConstants.FixedDeltaTime);
        if (currentTick - _passTick > maxTicks)
            ResetPassPhase();
    }

    private void AddPassAttempt(TeamId team)
    {
        if (team == TeamId.Home) _summary.HomePassesAttempted++;
        else _summary.AwayPassesAttempted++;
    }

    private void AddPassCompletion(TeamId team)
    {
        if (team == TeamId.Home) _summary.HomePassesCompleted++;
        else _summary.AwayPassesCompleted++;
    }

    private void AddShot(TeamId team)
    {
        if (team == TeamId.Home) _summary.HomeShots++;
        else _summary.AwayShots++;
    }
}
