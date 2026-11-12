using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.Scenarios;

/// <summary>
/// A3.3 exit gate, on the fixture it names: in <c>cb-step-out</c> the centre-back whose press leaves
/// the back line has that gap taken by the next defender, the responsibility is an exception on the
/// owner's duty (not his own), it targets the slot the centre-back owns, and it is handed back while
/// the fragment is still running.
/// </summary>
public sealed class CentreBackStepsOutTests
{
    [Fact]
    public void TheVacatedGap_IsCovered_AndHandedBack()
    {
        var engine = new MatchSimulationEngine(ScenarioLibrary.CenterBackStepsOut());
        TeamFormation formation = engine.Tactics.HomeFormation;

        int acquiredTick = -1;
        int releasedTick = -1;
        int ownerId = -1;
        int sourceId = -1;
        Vector3 region = default;
        FootballerObservation? ownerObservationWhileHeld = null;
        Vector3 stepperPositionAtAcquisition = default;

        for (int tick = 1; tick <= 900; tick++)
        {
            engine.StepTick();

            // The request instance is reused, so its values are captured by value at acquisition.
            CoverageRequest current = engine.TeamJobs.CoverageOf(TeamId.Home);
            if (current.IsLive)
            {
                if (acquiredTick < 0)
                {
                    acquiredTick = tick;
                    ownerId = current.OwnerPlayerId;
                    sourceId = current.SourcePlayerId;
                    region = current.Region;
                    ownerObservationWhileHeld = engine.GetLastObservation(ownerId);
                    stepperPositionAtAcquisition = engine.Players.First(p => p.Id == sourceId).Position;
                }

                continue;
            }

            if (acquiredTick >= 0 && releasedTick < 0)
                releasedTick = tick;
        }

        Assert.True(acquiredTick > 0 && acquiredTick < 300,
            $"no teammate covered the vacated gap inside five seconds (acquired at {acquiredTick})");
        Assert.True(releasedTick > acquiredTick,
            $"the responsibility was never handed back (acquired {acquiredTick}, released {releasedTick})");

        // The source is the centre-back who stepped out, and the owner is another outfield defender.
        Assert.Equal(ScenarioLibrary.StepOutHomeCb, sourceId);
        Assert.NotEqual(sourceId, ownerId);
        FootballerState owner = engine.Players.First(p => p.Id == ownerId);
        Assert.NotEqual(TacticalRole.Goalkeeper, owner.Role);

        // What is protected is his place in the back line, not wherever the ball happens to be.
        Assert.True(formation.TryGetWorldAnchor(ScenarioLibrary.StepOutHomeCb, 1f, out Vector3 slot));
        Assert.True(Vector3.Distance(region, slot) < 0.5f,
            $"the protected region {region} is not the vacated slot {slot}");

        // And the player knew it: the observation carries the exception, not just a bare job.
        Assert.NotNull(ownerObservationWhileHeld);
        Assert.True(ownerObservationWhileHeld!.IsTemporaryResponsibility);
        Assert.Equal(TacticalJob.ProtectGap, ownerObservationWhileHeld.TacticalJob);
        Assert.Equal(ScenarioLibrary.StepOutHomeCb, ownerObservationWhileHeld.ResponsibilitySourcePlayerId);
        Assert.Contains("vacated", ownerObservationWhileHeld.JobReason);

        // He really was out of his line when the region was handed out: the gap was behind him.
        Assert.True(stepperPositionAtAcquisition.X > slot.X + 8f,
            $"the centre-back was not out of his line when the gap was covered " +
            $"(x={stepperPositionAtAcquisition.X:F1}, slot x={slot.X:F1})");
    }
}
