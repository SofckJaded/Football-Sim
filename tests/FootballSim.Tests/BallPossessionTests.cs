using System;
using System.Numerics;
using FootballSim;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// The ownership model's flight clock is per pass, not per team. A one-touch second pass by the same
/// team is a new flight; inheriting the previous one lets the new pass time out on its first tick.
/// </summary>
public sealed class BallPossessionTests
{
    [Fact]
    public void ASecondPassByTheSameTeam_StartsANewFlightClock()
    {
        var tracker = new BallPossessionTracker();
        var ball = new BallState
        {
            Position = Vector3.Zero,
            PreviousPosition = Vector3.Zero,
            Velocity = new Vector3(5f, 0f, 0f)
        };

        tracker.Update(Array.Empty<FootballerState>(), ball,
            new[] { new BallContactEvent(1, BallContactKind.Kick, 1, TeamId.Home, ball.Position)
            {
                Intent = KickIntentType.GroundPass,
                IntendedReceiverId = 2
            } }, dt: 0.05f);

        // Let the first flight age just below the 8 s timeout, without settling it: the ball is still
        // moving too fast to be "settled", so only the old elapsed clock can reject the next pass.
        tracker.Update(Array.Empty<FootballerState>(), ball, contacts: null, dt: 7.90f);
        Assert.Equal(BallControlState.InFlightPass, tracker.Current.State);
        Assert.Equal(2, tracker.Current.IntendedReceiverId);

        // A one-touch second pass by the same team: state, team and controlling player are unchanged,
        // only the intended receiver differs. The ball is slow enough that the stale clock would settle
        // or time it out immediately.
        ball.Velocity = new Vector3(1.5f, 0f, 0f);
        tracker.Update(Array.Empty<FootballerState>(), ball,
            new[] { new BallContactEvent(2, BallContactKind.Kick, 1, TeamId.Home, ball.Position)
            {
                Intent = KickIntentType.GroundPass,
                IntendedReceiverId = 1
            } }, dt: 0.10f);

        Assert.Equal(BallControlState.InFlightPass, tracker.Current.State);
        Assert.Equal(1, tracker.Current.IntendedReceiverId);
        Assert.Equal(TeamId.Home, tracker.Current.PossessionTeam);
    }
}
