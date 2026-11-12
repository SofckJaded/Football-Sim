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

    /// <summary>
    /// The clock is what the configured half length governs. The loop therefore runs until half time
    /// rather than only while the ball is in open play: a throw-in inside a five-second half pauses play
    /// (and does not advance the clock), and the old loop treated that armed restart as the end of the
    /// test. The clock assertion below is unchanged, so a restart that *stopped* the clock would still
    /// fail here.
    /// </summary>
    [Fact]
    public void MatchEngine_HonoursTheConfiguredHalfLength()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig { HalfDurationSeconds = 5f });

        for (int i = 0; i < 900 && engine.Rules.Phase != MatchPhase.HalfTime; i++)
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
