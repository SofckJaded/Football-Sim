using System;
using System.Collections.Generic;
using System.Linq;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;
using Xunit;
using Xunit.Abstractions;

namespace FootballSim.World.Tests.Simulation;

/// <summary>
/// Stage W3 lifecycle tests: weekly injury recovery, severe-injury permanent damage, aging, retirement and
/// youth-intake regens.
///
/// <para>Worlds are generated per test rather than shared because every off-season test mutates its world;
/// generation is a few tens of milliseconds. The large contract world is 108 clubs x 24 players = 2,592
/// players. The retirement identity checks use a second world generated from the same seed as a mirror, which
/// makes "retires exactly the players ShouldRetire accepts" a directly comparable assertion.</para>
/// </summary>
public sealed class LifecycleSystemTests
{
    private const ulong DefaultSeed = 20261127UL;
    private const int PlayersInWorld = 2592;
    private const int ClubsInWorld = 108;
    private const int PlayersPerClub = 24;

    private static readonly Lazy<WorldPack> FullPack =
        new(() => WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack()));

    private readonly ITestOutputHelper _output;

    public LifecycleSystemTests(ITestOutputHelper output) => _output = output;

    private static WorldPack Pack => FullPack.Value;

    private static WorldState NewWorld(ulong seed = DefaultSeed) => WorldGenerator.Generate(Pack, seed);

    // =============================================================================================
    // 1. Active population.
    // =============================================================================================

    [Fact]
    public void CountActivePlayers_CountsEveryRegisteredPlayerThatHasNotRetired()
    {
        WorldState world = NewWorld();

        Assert.Equal(PlayersInWorld, world.Players.Count);
        Assert.Equal(PlayersInWorld, LifecycleSystem.CountActivePlayers(world));

        int retired = 0;
        for (int index = 0; index < world.Players.Count; index += 500)
        {
            world.Players.All[index].IsRetired = true;
            retired++;
        }

        Assert.Equal(PlayersInWorld - retired, LifecycleSystem.CountActivePlayers(world));
    }

    [Fact]
    public void LifecycleSystem_GuardsNullArguments()
    {
        WorldState world = NewWorld();
        WorldPlayer player = world.Players.All[0];
        var rng = new WorldRng(1UL);

        Assert.Throws<ArgumentNullException>(() => LifecycleSystem.CountActivePlayers(null!));
        Assert.Throws<ArgumentNullException>(() => LifecycleSystem.AdvanceInjuries(null!));
        Assert.Throws<ArgumentNullException>(() =>
            LifecycleSystem.ApplyInjury(null!, player, InjuryKind.HamstringTear, InjurySeverity.Minor, 2, rng));
        Assert.Throws<ArgumentNullException>(() =>
            LifecycleSystem.ApplyInjury(world, null!, InjuryKind.HamstringTear, InjurySeverity.Minor, 2, rng));
        Assert.Throws<ArgumentNullException>(() =>
            LifecycleSystem.ApplyInjury(world, player, InjuryKind.HamstringTear, InjurySeverity.Minor, 2, null!));
        Assert.Throws<ArgumentNullException>(() => LifecycleSystem.ShouldRetire(null!, rng));
        Assert.Throws<ArgumentNullException>(() => LifecycleSystem.ShouldRetire(player, null!));
        Assert.Throws<ArgumentNullException>(() => LifecycleSystem.ProcessOffSeason(null!, rng));
        Assert.Throws<ArgumentNullException>(() => LifecycleSystem.ProcessOffSeason(world, null!));
    }

    // =============================================================================================
    // 2. Injury recovery tick.
    // =============================================================================================

    [Fact]
    public void AdvanceInjuries_RecoversOneExtraWeekAtATimeAndNeverBelowZero()
    {
        WorldState world = NewWorld();
        WorldPlayer player = world.Players.All[0];
        var setupRng = new WorldRng(2026UL);

        LifecycleSystem.ApplyInjury(
            world, player, InjuryKind.HamstringTear, InjurySeverity.Minor, 2, setupRng);
        Assert.True(player.Injury.IsInjured);
        Assert.Equal(2, player.Injury.WeeksOutRemaining);

        // Only this player is injured in the whole world, so the tick counts are exactly about him.
        InjuryTickResult firstTick = LifecycleSystem.AdvanceInjuries(world);
        Assert.Equal(1, firstTick.Recovered + firstTick.StillInjured);
        Assert.InRange(player.Injury.WeeksOutRemaining, 1, 2);
        Assert.Equal(player.Injury.IsInjured ? 1 : 0, firstTick.StillInjured);
        Assert.Equal(player.Injury.IsInjured ? 0 : 1, firstTick.Recovered);

        // Extra medical recovery is probabilistic, but with a 10%..45% weekly chance it must land well inside
        // the bound; each landing costs exactly one week and the state clears at zero.
        int ticks = 1;
        while (player.Injury.IsInjured && ticks < 500)
        {
            InjuryTickResult tick = LifecycleSystem.AdvanceInjuries(world);
            Assert.Equal(1, tick.Recovered + tick.StillInjured);
            ticks++;
        }

        Assert.False(player.Injury.IsInjured);
        Assert.Equal(0, player.Injury.WeeksOutRemaining);
        Assert.Equal(InjuryKind.None, player.Injury.Kind);
        Assert.Equal(InjurySeverity.None, player.Injury.Severity);
        Assert.InRange(ticks, 1, 500);
        _output.WriteLine($"2-week injury recovered after {ticks} extra-recovery ticks");
    }

    [Fact]
    public void AdvanceInjuries_CountsEveryInjuredPlayerInRegistryOrder()
    {
        WorldState world = NewWorld();
        WorldRng setupRng = world.Stream(WorldRandomStream.Injuries);

        const int injuredPlayers = 40;
        for (int index = 0; index < injuredPlayers; index++)
        {
            WorldPlayer player = world.Players.All[index * 50];
            LifecycleSystem.ApplyInjury(
                world, player, InjuryKind.CalfStrain, InjurySeverity.Minor, 3, setupRng);
        }

        Assert.Equal(PlayersInWorld, LifecycleSystem.CountActivePlayers(world));
        InjuryTickResult tick = LifecycleSystem.AdvanceInjuries(world);

        // A three-week injury can lose at most one extra week per tick, so nobody recovers in a single tick.
        Assert.Equal(0, tick.Recovered);
        Assert.Equal(injuredPlayers, tick.StillInjured);
    }

    // =============================================================================================
    // 3. Applying injuries.
    // =============================================================================================

    [Fact]
    public void ApplyInjury_MarksPlayerInjuredAndSevereInjuriesApplyPermanentDamageOnce()
    {
        WorldState world = NewWorld();
        var rng = new WorldRng(2026UL);
        WorldPlayer player = PickHighCeilingOutfielder(world);

        float topSpeedBefore = player.Attributes.Ceiling(AttributeId.TopSpeed);
        float accelerationBefore = player.Attributes.Ceiling(AttributeId.Acceleration);
        float agilityBefore = player.Attributes.Ceiling(AttributeId.Agility);

        LifecycleSystem.ApplyInjury(
            world, player, InjuryKind.KneeLigament, InjurySeverity.Severe, 8, rng);

        Assert.True(player.Injury.IsInjured);
        Assert.Equal(InjuryKind.KneeLigament, player.Injury.Kind);
        Assert.Equal(InjurySeverity.Severe, player.Injury.Severity);
        Assert.Equal(8, player.Injury.WeeksOutRemaining);
        Assert.Equal(8, player.Injury.TotalWeeksOut);
        Assert.Equal(1, player.Injury.PermanentDamageApplied);
        AssertCeilingShave(player, AttributeId.TopSpeed, topSpeedBefore, 3.0f, 6.0f);
        AssertCeilingShave(player, AttributeId.Acceleration, accelerationBefore, 2.5f, 5.0f);
        AssertCeilingShave(player, AttributeId.Agility, agilityBefore, 2.0f, 4.0f);

        // The growth engine's ledger guards the damage: a second call for the same injury changes nothing.
        float topSpeedAfter = player.Attributes.Ceiling(AttributeId.TopSpeed);
        GrowthEngine.ApplyPermanentInjuryDamage(player, InjuryKind.KneeLigament, rng);
        Assert.Equal(topSpeedAfter, player.Attributes.Ceiling(AttributeId.TopSpeed));
        Assert.Equal(1, player.Injury.PermanentDamageApplied);

        // A non-severe injury (same kind) injures without any permanent ceiling damage.
        WorldPlayer other = world.Players.All[1];
        float otherTopSpeed = other.Attributes.Ceiling(AttributeId.TopSpeed);
        LifecycleSystem.ApplyInjury(
            world, other, InjuryKind.HamstringTear, InjurySeverity.Moderate, 3, rng);
        Assert.True(other.Injury.IsInjured);
        Assert.Equal(0, other.Injury.PermanentDamageApplied);
        Assert.Equal(otherTopSpeed, other.Attributes.Ceiling(AttributeId.TopSpeed));

        // Season-ending severity is also permanent-damage eligible.
        WorldPlayer seasonEnding = world.Players.All[2];
        LifecycleSystem.ApplyInjury(
            world, seasonEnding, InjuryKind.AnkleLigament, InjurySeverity.SeasonEnding, 26, rng);
        Assert.Equal(1, seasonEnding.Injury.PermanentDamageApplied);
        Assert.Equal(26, seasonEnding.Injury.WeeksOutRemaining);

        // Rejected: already injured, retired, or an invalid injury specification.
        Assert.Throws<InvalidOperationException>(() => LifecycleSystem.ApplyInjury(
            world, player, InjuryKind.AnkleLigament, InjurySeverity.Minor, 1, rng));
        Assert.Throws<InvalidOperationException>(() => LifecycleSystem.ApplyInjury(
            world, seasonEnding, InjuryKind.HamstringTear, InjurySeverity.Minor, 1, rng));
        WorldPlayer retired = world.Players.All[3];
        retired.IsRetired = true;
        Assert.Throws<InvalidOperationException>(() => LifecycleSystem.ApplyInjury(
            world, retired, InjuryKind.HamstringTear, InjurySeverity.Minor, 1, rng));
        Assert.Throws<ArgumentOutOfRangeException>(() => LifecycleSystem.ApplyInjury(
            world, world.Players.All[4], InjuryKind.None, InjurySeverity.Minor, 1, rng));
        Assert.Throws<ArgumentOutOfRangeException>(() => LifecycleSystem.ApplyInjury(
            world, world.Players.All[4], InjuryKind.HamstringTear, InjurySeverity.None, 1, rng));
        Assert.Throws<ArgumentOutOfRangeException>(() => LifecycleSystem.ApplyInjury(
            world, world.Players.All[4], InjuryKind.HamstringTear, InjurySeverity.Minor, 0, rng));
    }

    // =============================================================================================
    // 4. Retirement probability.
    // =============================================================================================

    [Fact]
    public void ShouldRetire_ForcesAgeFortyAndNeverRetiresAYoungPlayer()
    {
        WorldState world = NewWorld();
        WorldPlayer player = world.Players.All[0];

        player.Age = 40;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var rng = new WorldRng((ulong)(1000 + attempt));
            Assert.True(LifecycleSystem.ShouldRetire(player, rng));
            Assert.Equal(rng.Seed, rng.State); // a forced retirement consumes no randomness
        }

        player.Age = 25;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var rng = new WorldRng((ulong)(2000 + attempt));
            Assert.False(LifecycleSystem.ShouldRetire(player, rng));
            Assert.Equal(rng.Seed, rng.State);
        }

        // Below 33 the weak-player/free-agent bonuses are gated off entirely: a 25-year-old with awful
        // attributes and no club still never retires.
        WorldPlayer youngWeak = world.Players[world.Clubs.InTier(6)[0].Squad[0]];
        youngWeak.Age = 25;
        youngWeak.ClubId = ClubId.None;
        SetAllCurrent(youngWeak, 20f);
        Assert.True(youngWeak.Attributes.MeanCurrent() < 32f);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var rng = new WorldRng((ulong)(2500 + attempt));
            Assert.False(LifecycleSystem.ShouldRetire(youngWeak, rng));
            Assert.Equal(rng.Seed, rng.State);
        }

        // A healthy, employed 33-year-old retires on a minority of seeds, deterministically.
        WorldPlayer healthy = PickHighCeilingOutfielder(world);
        healthy.Age = 33;
        Assert.True(healthy.ClubId.IsValid);
        Assert.True(healthy.Attributes.MeanCurrent() >= 32f);

        int healthyRetirements = CountRetirements(healthy, 4000);
        Assert.InRange(healthyRetirements, 1, 3999);
        Assert.Equal(healthyRetirements, CountRetirements(healthy, 4000)); // same seed set, same answer
        _output.WriteLine($"age 33 healthy player retired on {healthyRetirements}/4000 fixed seeds");

        // The weak-player (+0.12) and free-agent (+0.05) bonuses raise the chance well above that.
        WorldPlayer weak = world.Players.All[1];
        weak.Age = 33;
        weak.ClubId = ClubId.None;
        SetAllCurrent(weak, 20f);
        Assert.True(weak.Attributes.MeanCurrent() < 32f);

        int weakFreeAgentRetirements = CountRetirements(weak, 4000);
        _output.WriteLine(
            $"age 33 weak free agent retired on {weakFreeAgentRetirements}/4000 seeds " +
            $"(healthy {healthyRetirements}/4000)");
        Assert.True(
            weakFreeAgentRetirements > healthyRetirements,
            $"weak free-agent rate {weakFreeAgentRetirements}/4000 should exceed healthy " +
            $"{healthyRetirements}/4000");

        // Age is the dominant term: 37 retires more often than 33 for the same seed set.
        healthy.Age = 37;
        int age37 = CountRetirements(healthy, 4000);
        _output.WriteLine($"healthy retirement rate age 33 {healthyRetirements}/4000 vs age 37 {age37}/4000");
        Assert.True(age37 > healthyRetirements);
    }

    // =============================================================================================
    // 5. Off-season rollover: aging and retirements.
    // =============================================================================================

    [Fact]
    public void ProcessOffSeason_AgesEveryActivePlayerAndRetiresExactlyThePredictedSet()
    {
        const ulong lifecycleSeed = 777UL;
        WorldState world = NewWorld();
        WorldState mirror = NewWorld();

        var startAges = new Dictionary<PlayerId, int>(world.Players.Count);
        var startClubs = new Dictionary<PlayerId, ClubId>(world.Players.Count);
        foreach (WorldPlayer player in world.Players.All)
        {
            startAges.Add(player.Id, player.Age);
            startClubs.Add(player.Id, player.ClubId);
        }

        List<PlayerId> expectedRetirements = PredictRetirements(mirror, lifecycleSeed);
        LifecycleRolloverResult result = LifecycleSystem.ProcessOffSeason(world, new WorldRng(lifecycleSeed));

        Assert.Equal(startAges.Count, result.PlayersAged);
        Assert.Equal(expectedRetirements.Count, result.Retirements);
        Assert.Equal(result.Retirements, result.Retired.Count);
        Assert.Equal(expectedRetirements, result.Retired.Select(record => record.PlayerId));

        var retiredIds = new HashSet<PlayerId>(result.Retired.Select(record => record.PlayerId));
        var intakeIds = new HashSet<PlayerId>(result.Intakes.Select(record => record.PlayerId));
        foreach (WorldPlayer player in world.Players.All)
        {
            if (intakeIds.Contains(player.Id)) continue; // generated after the aging pass: age 16/17 already

            int expectedAge = startAges[player.Id] + 1;
            Assert.Equal(expectedAge, player.Age);

            if (!retiredIds.Contains(player.Id))
            {
                Assert.False(player.IsRetired, $"player {player.Id} was not predicted to retire");
                Assert.Equal(startClubs[player.Id], player.ClubId);
                continue;
            }

            Assert.True(player.IsRetired, $"retired player {player.Id} was left active");
            Assert.Equal(ClubId.None, player.ClubId);
            Assert.Equal(InjuryKind.None, player.Injury.Kind);
            Assert.Equal(0, player.Injury.WeeksOutRemaining);
            Assert.Equal(0, player.SuspensionWeeksRemaining);
            Assert.Equal(world.Season, player.Contract.SignedSeason);
            Assert.Equal(world.Season, player.Contract.EndSeason);
            Assert.Equal(0L, player.Contract.WeeklyWage);
            Assert.False(player.Contract.IsOnLoan);
            Assert.Equal(ClubId.None, player.Contract.ParentClubId);
        }

        // Every retirement record agrees with the player and his old club, and the old club dropped him.
        Assert.Equal(result.Retirements, result.Retired.Select(record => record.PlayerId).Distinct().Count());
        foreach (RetirementRecord record in result.Retired)
        {
            WorldPlayer player = world.Players[record.PlayerId];
            Assert.Equal(startClubs[record.PlayerId], record.ClubId);
            Assert.Equal(startAges[record.PlayerId] + 1, record.Age);
            Assert.Equal(record.Age, player.Age);
            Assert.InRange(record.Age, 33, WorldPlayer.MaximumAge);

            if (!record.ClubId.IsValid) continue;
            WorldClub oldClub = world.Clubs[record.ClubId];
            Assert.DoesNotContain(record.PlayerId, oldClub.Squad);
            Assert.DoesNotContain(record.PlayerId, oldClub.ManagerTrust.Keys);
        }

        // The transfer log carries exactly one retirement entry per retiring player, in the same order.
        List<TransferRecord> retirementLog = world.TransferLog
            .Where(entry => entry.Kind == TransferKind.Retirement)
            .ToList();
        Assert.Equal(result.Retirements, retirementLog.Count);
        Assert.Equal(result.Retirements, world.CountTransfers(TransferKind.Retirement));
        for (int index = 0; index < retirementLog.Count; index++)
        {
            RetirementRecord record = result.Retired[index];
            TransferRecord entry = retirementLog[index];
            Assert.Equal(world.Season, entry.Season);
            Assert.Equal(world.Week, entry.Week);
            Assert.Equal(record.PlayerId, entry.PlayerId);
            Assert.Equal(record.ClubId, entry.FromClubId);
            Assert.Equal(ClubId.None, entry.ToClubId);
            Assert.Equal(0L, entry.Fee);
        }

        // The registry keeps retired players for history, and the population books balance exactly.
        Assert.Equal(result.YouthIntakes, result.Intakes.Count);
        Assert.Equal(PlayersInWorld + result.YouthIntakes, world.Players.Count);
        Assert.Equal(
            startAges.Count - result.Retirements + result.YouthIntakes, result.ActivePopulation);
        Assert.Equal(result.ActivePopulation, LifecycleSystem.CountActivePlayers(world));
        _output.WriteLine(
            $"rollover: aged {result.PlayersAged}, retired {result.Retirements}, " +
            $"intakes {result.YouthIntakes}, active population {result.ActivePopulation}");
    }

    [Fact]
    public void ProcessOffSeason_ForcedVeteranRetiresWhileAYoungsterStays()
    {
        WorldState world = NewWorld();
        WorldPlayer veteran = PickHighCeilingOutfielder(world);
        WorldPlayer youngster = world.Players.All[1];

        Assert.NotEqual(veteran.Id, youngster.Id);
        Assert.True(veteran.ClubId.IsValid);
        Assert.True(youngster.ClubId.IsValid);
        ClubId veteranClub = veteran.ClubId;
        ClubId youngsterClub = youngster.ClubId;

        veteran.Age = 39;  // ages to 40: retirement is certain
        youngster.Age = 25; // ages to 26: retirement is impossible

        LifecycleRolloverResult result = LifecycleSystem.ProcessOffSeason(world, new WorldRng(11UL));

        Assert.True(veteran.IsRetired);
        Assert.Equal(40, veteran.Age);
        Assert.Equal(ClubId.None, veteran.ClubId);
        RetirementRecord record = result.Retired.Single(entry => entry.PlayerId == veteran.Id);
        Assert.Equal(veteranClub, record.ClubId);
        Assert.Equal(40, record.Age);
        Assert.DoesNotContain(veteran.Id, world.Clubs[veteranClub].Squad);

        TransferRecord logEntry = world.TransferLog.Single(entry =>
            entry.PlayerId == veteran.Id && entry.Kind == TransferKind.Retirement);
        Assert.Equal(veteranClub, logEntry.FromClubId);
        Assert.Equal(ClubId.None, logEntry.ToClubId);

        Assert.False(youngster.IsRetired);
        Assert.Equal(26, youngster.Age);
        Assert.Equal(youngsterClub, youngster.ClubId);
        Assert.Contains(youngster.Id, world.Clubs[youngsterClub].Squad);
    }

    // =============================================================================================
    // 6. Off-season rollover: youth intake and population balance.
    // =============================================================================================

    [Fact]
    public void ProcessOffSeason_GeneratesValidYouthIntakesAndKeepsThePopulationInBand()
    {
        WorldState world = NewWorld();
        int activeBefore = LifecycleSystem.CountActivePlayers(world);

        LifecycleRolloverResult result = LifecycleSystem.ProcessOffSeason(world, new WorldRng(424242UL));

        // --- Ledger consistency ---------------------------------------------------------------
        Assert.Equal(result.Retirements, result.Retired.Count);
        Assert.Equal(result.YouthIntakes, result.Intakes.Count);
        Assert.Equal(activeBefore, result.PlayersAged);
        Assert.Equal(activeBefore - result.Retirements + result.YouthIntakes, result.ActivePopulation);
        Assert.Equal(result.ActivePopulation, LifecycleSystem.CountActivePlayers(world));
        Assert.Equal(PlayersInWorld + result.YouthIntakes, world.Players.Count);

        // --- Population band (the W3 "world self-sustains" requirement) ------------------------
        Assert.InRange(
            result.ActivePopulation,
            LifecycleSystem.MinimumActivePopulation,
            LifecycleSystem.MaximumActivePopulation);
        _output.WriteLine(
            $"population after one off-season: {activeBefore} -> {result.ActivePopulation} " +
            $"(-{result.Retirements} retirements + {result.YouthIntakes} intakes); " +
            $"band {LifecycleSystem.MinimumActivePopulation}..{LifecycleSystem.MaximumActivePopulation}, " +
            $"target {LifecycleSystem.TargetActivePopulation}");

        // --- Every club receives 1..3 intakes --------------------------------------------------
        Assert.Equal(ClubsInWorld, world.Clubs.Count);
        var intakesPerClub = new Dictionary<ClubId, int>(ClubsInWorld);
        foreach (YouthIntakeRecord record in result.Intakes)
        {
            intakesPerClub.TryGetValue(record.ClubId, out int count);
            intakesPerClub[record.ClubId] = count + 1;
        }

        foreach (WorldClub club in world.Clubs.All)
        {
            Assert.True(
                intakesPerClub.TryGetValue(club.Id, out int clubIntakes),
                $"club {club.Id} received no youth intake");
            Assert.InRange(clubIntakes, 1, 3);
        }

        // --- Every intake is a valid, registered, correctly contracted 16/17-year-old ---------
        var intakeIds = new HashSet<PlayerId>();
        foreach (YouthIntakeRecord record in result.Intakes)
        {
            Assert.True(intakeIds.Add(record.PlayerId), $"duplicate intake id {record.PlayerId}");
            Assert.True(world.Players.TryGet(record.PlayerId, out WorldPlayer? player));
            Assert.NotNull(player);
            WorldPlayer intake = player!;

            Assert.True(record.ClubId.IsValid);
            WorldClub club = world.Clubs[record.ClubId];
            Assert.Equal(record.ClubId, intake.ClubId);
            Assert.Equal(record.Age, intake.Age);
            Assert.InRange(intake.Age, 16, 17);
            Assert.False(intake.IsRetired);
            Assert.True(intake.Attributes.IsValid(), $"intake {intake.Id} has invalid attributes");
            Assert.Contains(intake.Id, club.Squad);
            Assert.Equal(0.5f, club.ManagerTrustOf(intake.Id));
            Assert.False(intake.Injury.IsInjured);
            Assert.Equal(0, intake.SuspensionWeeksRemaining);

            // Simple amateur deal: no wage, 2-3 seasons, not a loan.
            Assert.Equal(0L, intake.Contract.WeeklyWage);
            Assert.Equal(world.Season, intake.Contract.SignedSeason);
            Assert.InRange(intake.Contract.EndSeason, world.Season + 1, world.Season + 2);
            Assert.False(intake.Contract.IsOnLoan);
            Assert.Equal(ClubId.None, intake.Contract.ParentClubId);

            // Valid role/group/training-focus vocabulary: the generator receives a real tactical role and
            // the intake starts on the role's training focus (never the neutral Balanced default).
            Assert.True(Enum.IsDefined(RosterAnalyser.GroupOf(intake)));
            Assert.True(Enum.IsDefined(IdentityEvaluator.Evaluate(intake).PrimaryRole));
            Assert.True(Enum.IsDefined(intake.TrainingFocus));
            Assert.NotEqual(TrainingFocus.Balanced, intake.TrainingFocus);
        }

        // --- Full names stay unique across the whole world (case-insensitive) -----------------
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorldPlayer player in world.Players.All)
        {
            Assert.True(names.Add(player.DisplayName), $"duplicate player name '{player.DisplayName}'");
        }

        Assert.Equal(world.Players.Count, names.Count);

        // --- The club squads and the registry agree -------------------------------------------
        int squadMembers = 0;
        var seenPlayers = new HashSet<PlayerId>();
        foreach (WorldClub club in world.Clubs.All)
        {
            Assert.InRange(club.Squad.Count, 1, PlayersPerClub + 10);
            foreach (PlayerId playerId in club.Squad)
            {
                Assert.True(world.Players.TryGet(playerId, out WorldPlayer? player));
                Assert.NotNull(player);
                Assert.Equal(club.Id, player!.ClubId);
                Assert.False(player.IsRetired);
                Assert.True(seenPlayers.Add(playerId), $"player {playerId} is in two squads");
                squadMembers++;
            }
        }

        Assert.Equal(result.ActivePopulation, squadMembers);
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired) continue;
            Assert.True(player.ClubId.IsValid, $"active player {player.Id} has no club");
            Assert.Contains(player.Id, world.Clubs[player.ClubId].Squad);
        }
    }

    [Fact]
    public void ProcessOffSeason_IsDeterministicForTheSameSeed()
    {
        const ulong lifecycleSeed = 987654321UL;
        WorldState first = NewWorld();
        WorldState second = NewWorld();
        var firstRng = new WorldRng(lifecycleSeed);
        var secondRng = new WorldRng(lifecycleSeed);

        LifecycleRolloverResult firstResult = LifecycleSystem.ProcessOffSeason(first, firstRng);
        LifecycleRolloverResult secondResult = LifecycleSystem.ProcessOffSeason(second, secondRng);

        Assert.Equal(firstResult.Retirements, secondResult.Retirements);
        Assert.Equal(firstResult.YouthIntakes, secondResult.YouthIntakes);
        Assert.Equal(firstResult.PlayersAged, secondResult.PlayersAged);
        Assert.Equal(firstResult.ActivePopulation, secondResult.ActivePopulation);
        Assert.Equal(firstResult.Retired, secondResult.Retired);
        Assert.Equal(firstResult.Intakes, secondResult.Intakes);
        Assert.Equal(firstRng.State, secondRng.State);

        // Full player-state determinism: identity, age, club, retirement flag, contract, attributes.
        Assert.Equal(first.Players.Count, second.Players.Count);
        for (int index = 0; index < first.Players.Count; index++)
        {
            WorldPlayer left = first.Players.All[index];
            WorldPlayer right = second.Players.All[index];
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.DisplayName, right.DisplayName);
            Assert.Equal(left.Age, right.Age);
            Assert.Equal(left.IsRetired, right.IsRetired);
            Assert.Equal(left.ClubId, right.ClubId);
            Assert.Equal(left.TrainingFocus, right.TrainingFocus);
            Assert.Equal(left.Contract.EndSeason, right.Contract.EndSeason);

            for (int attribute = 0; attribute < AttributeMetadata.Count; attribute++)
            {
                AttributeId id = (AttributeId)attribute;
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(left.Attributes.Current(id)),
                    BitConverter.SingleToInt32Bits(right.Attributes.Current(id)));
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(left.Attributes.Ceiling(id)),
                    BitConverter.SingleToInt32Bits(right.Attributes.Ceiling(id)));
            }
        }

        // The transfer log is identical draw-for-draw.
        Assert.Equal(first.TransferLog.Count, second.TransferLog.Count);
        for (int index = 0; index < first.TransferLog.Count; index++)
        {
            Assert.Equal(first.TransferLog[index], second.TransferLog[index]);
        }

        _output.WriteLine(
            $"determinism: {firstResult.Retirements} retirements + {firstResult.YouthIntakes} intakes, " +
            $"{first.Players.Count} registry rows and {first.TransferLog.Count} transfer-log entries identical");
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>
    /// Predicts the retirement set exactly the way the rollover defines it: age everybody by one (on a mirror
    /// world), then roll ShouldRetire in registry order with the same generator seed.
    /// </summary>
    private static List<PlayerId> PredictRetirements(WorldState mirror, ulong seed)
    {
        foreach (WorldPlayer player in mirror.Players.All) player.Age++;

        var rng = new WorldRng(seed);
        var retirements = new List<PlayerId>();
        foreach (WorldPlayer player in mirror.Players.All)
        {
            if (LifecycleSystem.ShouldRetire(player, rng)) retirements.Add(player.Id);
        }

        return retirements;
    }

    /// <summary>A tier-1 outfield player with comfortable attribute headroom for injury tests.</summary>
    private static WorldPlayer PickHighCeilingOutfielder(WorldState world)
    {
        WorldClub club = world.Clubs.All[0];
        foreach (PlayerId playerId in club.Squad)
        {
            WorldPlayer player = world.Players[playerId];
            if (RosterAnalyser.GroupOf(player) == RosterGroup.Goalkeeper) continue;
            if (player.Attributes.Ceiling(AttributeId.TopSpeed) < 40f) continue;
            if (player.Attributes.Ceiling(AttributeId.Acceleration) < 40f) continue;
            if (player.Attributes.Ceiling(AttributeId.Agility) < 40f) continue;
            return player;
        }

        throw new InvalidOperationException("The generated world has no suitable tier-1 outfielder.");
    }

    /// <summary>Sets every attribute's current value (the player's mean follows it).</summary>
    private static void SetAllCurrent(WorldPlayer player, float value)
    {
        for (int attribute = 0; attribute < AttributeMetadata.Count; attribute++)
        {
            player.Attributes.SetCurrent((AttributeId)attribute, value);
        }
    }

    /// <summary>Counts how many of the first <paramref name="samples"/> seed ordinals retire the player.</summary>
    private static int CountRetirements(WorldPlayer player, int samples)
    {
        int count = 0;
        for (int seed = 0; seed < samples; seed++)
        {
            if (LifecycleSystem.ShouldRetire(player, new WorldRng((ulong)(seed + 90_000))))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Asserts a ceiling shave landed inside the injury kind's documented range.</summary>
    private static void AssertCeilingShave(
        WorldPlayer player, AttributeId id, float ceilingBefore, float minimumShave, float maximumShave)
    {
        float shave = ceilingBefore - player.Attributes.Ceiling(id);
        Assert.InRange(shave, minimumShave, maximumShave);
    }
}
