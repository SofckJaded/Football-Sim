using Xunit;
using FootballSim;
using FootballSim.Core;

namespace FootballSim.Tests;

public sealed class MatchSetupTests
{
    [Fact]
    public void DefaultMatchSetup_IsFourMinuteHalves()
    {
        var setup = new MatchSetupOptions();

        Assert.Equal(4, MatchPresets.DefaultHalfLengthMinutes);
        Assert.Equal(240f, MatchSetupOptions.DefaultHalfDurationSeconds);
        Assert.Equal(240f, setup.HalfDurationSeconds);
        Assert.Equal(4, setup.HalfLengthMinutes);
    }

    [Fact]
    public void MatchEngine_HonoursTheConfiguredHalfLength()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = 5f });

        for (int i = 0; i < 400 && engine.Rules.Phase == MatchPhase.FirstHalf; i++)
            engine.StepTick();

        Assert.Equal(MatchPhase.HalfTime, engine.Rules.Phase);
        Assert.InRange(engine.Rules.MatchClockSeconds, 4.9f, 5.1f);
    }

    [Fact]
    public void MatchEngine_SpectatorConfigLeavesNoHumanController()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { DesignatedHumanPlayerId = null });

        Assert.Null(engine.LockedHumanController);
        Assert.Equal(22, engine.Players.Count);
    }

    [Fact]
    public void HalfLengthOptions_IncludeFourMinutesAndClampToTheList()
    {
        Assert.Contains(4, MatchPresets.HalfLengthMinuteOptions);
        Assert.Equal(1, MatchPresets.ClampHalfLengthMinutes(-10));
        Assert.Equal(45, MatchPresets.ClampHalfLengthMinutes(120));
        Assert.Equal(4, MatchPresets.ClampHalfLengthMinutes(4));
    }
}
