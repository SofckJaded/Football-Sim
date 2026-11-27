using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;
using Xunit;
using Xunit.Abstractions;

namespace FootballSim.World.Tests.Career;

/// <summary>
/// W4 save/load regression tests: a career saved mid-season must reload into a byte-equivalent world and a
/// career that continues with the exact same deterministic future.
///
/// <para><b>Cost discipline.</b> The class fixture generates one world and saves it once; the tests then load
/// that file (twice across the whole class, as the W4 plan budgets). The fixture also keeps the original
/// session so it can record the reference continuation fingerprint the reloaded career must reproduce and
/// reach a prepared live match for the atomic-write failure test. State equality uses the shared
/// <see cref="CareerTestHarness.Fingerprint"/> (a hash of every simulation-relevant value) plus explicit
/// anchors for the fields the save contract names.</para>
/// </summary>
public sealed class SaveManagerTests : IClassFixture<SaveFixture>
{
    private readonly SaveFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SaveManagerTests(SaveFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void SaveThenLoad_RestoresAnIdenticalWorldAndCareer()
    {
        var stopwatch = Stopwatch.StartNew();
        CareerSession loaded = SaveManager.Load(_fixture.SavePath);
        stopwatch.Stop();

        // The fixture's hash was taken from the live session immediately before it was saved.
        Assert.Equal(_fixture.SnapshotFingerprint, CareerTestHarness.Fingerprint(loaded));
        WorldState world = loaded.World;

        // Calendar, seed, identity and counters.
        Assert.Equal(CareerTestHarness.DefaultSeed, world.Seed);
        Assert.Equal(_fixture.SnapshotWeek, loaded.Date.Week);
        Assert.Equal(_fixture.SnapshotSeason, loaded.Date.Season);
        Assert.Equal(_fixture.HumanPlayerId, loaded.HumanPlayerId);
        Assert.Equal(_fixture.LastPlayerId, world.CaptureIdCounters().LastPlayerId);

        // Every player is back: 108 clubs x 24 generated players + the career player.
        Assert.Equal(2592 + 1, world.Players.Count);
        Assert.Equal(108, world.Clubs.Count);
        Assert.Equal(108, world.Managers.Count);

        // All 2,593 attribute triples are covered by the fingerprint; these named anchors sample the
        // first/third/middle/two-thirds/last player to name a readable failure target.
        Assert.Equal(_fixture.SampledAttributeTriples, SampledAttributeTriples(world));

        // RNG streams (keyed map, sorted by enum value), league composition and every recorded result.
        Assert.Equal(_fixture.RngStatesFingerprint, SortedRngStates(world));
        Assert.Equal(_fixture.LeagueClubOrder, LeagueClubOrder(world));
        Assert.Equal(_fixture.PlayedLeagueFixtureCount, PlayedLeagueFixtureCount(world));
        Assert.True(_fixture.PlayedLeagueFixtureCount > 0);

        // Cup state.
        Assert.Equal(_fixture.CupSeason, world.Cup.Season);
        Assert.Equal(world.Cup.Season, world.Season);
        Assert.Equal(_fixture.CupRoundCount, world.Cup.Rounds.Count);
        Assert.True(world.Cup.Rounds.Count >= 1);
        Assert.Equal(_fixture.CupComplete, world.Cup.IsComplete);
        Assert.Equal(_fixture.CupChampion, world.Cup.ChampionClubId);

        // Transfer log and career state.
        Assert.Equal(_fixture.TransferCount, world.TransferLog.Count);
        Assert.Equal(_fixture.InboxCount, loaded.Inbox.Count);
        Assert.Equal(_fixture.HistorySeasons, loaded.History.SeasonsPlayed);
        Assert.Equal(_fixture.TrainingFocus, loaded.TrainingFocus);
        Assert.Equal(_fixture.HumanClubId, loaded.HumanClub?.Id ?? ClubId.None);
        Assert.Equal(_fixture.HumanCondition, loaded.HumanPlayer.Condition01);

        _output.WriteLine(
            $"save file: {_fixture.SaveSize:N0} bytes; fixture build: {_fixture.CreationMilliseconds:N0} ms; " +
            $"one load: {stopwatch.ElapsedMilliseconds} ms; week {loaded.Date.Week} of season " +
            $"{loaded.Date.Season}; fixtures played: {_fixture.PlayedLeagueFixtureCount}");
    }

    [Fact]
    public void LoadedCareer_ContinuesDeterministicallyFromTheSameSave()
    {
        // One load here plus the one in the round-trip test is the file's whole read budget.
        CareerSession loaded = SaveManager.Load(_fixture.SavePath);
        Assert.Equal(_fixture.SnapshotFingerprint, CareerTestHarness.Fingerprint(loaded));

        // The reference fingerprint was produced by advancing the live session the same way.
        CareerTestHarness.AdvanceWeeks(loaded, _fixture.ContinuationWeeks, CareerTestHarness.ResponsePolicy.RenewalsOnly);

        Assert.Equal(_fixture.ContinuationFingerprint, CareerTestHarness.Fingerprint(loaded));
        Assert.True(loaded.Date.AbsoluteWeek > _fixture.SnapshotAbsoluteWeek);
        Assert.False(loaded.IsCareerOver);
    }

    [Fact]
    public void FailedSave_LeavesTheExistingFileUntouched_AndSuccessfulSaveLeavesNoTempFile()
    {
        CareerSession live = _fixture.LiveSession;
        Assert.True(live.HasPreparedLiveMatch);
        Assert.False(live.CanSave);

        byte[] existing = File.ReadAllBytes(_fixture.SavePath);
        Assert.Throws<InvalidOperationException>(() => SaveManager.Save(live, _fixture.SavePath));
        Assert.Throws<InvalidOperationException>(() => SaveManager.Serialize(live));
        Assert.Equal(existing, File.ReadAllBytes(_fixture.SavePath));
        Assert.False(File.Exists(_fixture.SavePath + ".tmp"));

        string directory = Path.GetDirectoryName(_fixture.SavePath)!;
        string successPath = Path.Combine(directory, "atomic-" + Guid.NewGuid().ToString("N") + ".json");
        live.DiscardLiveMatch();
        try
        {
            Assert.True(live.CanSave);
            SaveManager.Save(live, successPath);

            Assert.True(File.Exists(successPath));
            Assert.True(new FileInfo(successPath).Length > 0L);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            // Saving the live session elsewhere must not have rewritten the existing save.
            Assert.Equal(existing, File.ReadAllBytes(_fixture.SavePath));
        }
        finally
        {
            if (File.Exists(successPath)) File.Delete(successPath);
        }
    }

    [Fact]
    public void Load_RejectsUnknownVersionsMismatchedPacksAndMissingFiles()
    {
        Assert.Throws<ArgumentNullException>(() => SaveManager.Save(null!, "career.json"));
        Assert.Throws<ArgumentNullException>(() => SaveManager.Save(_fixture.LiveSession, null!));
        Assert.Throws<ArgumentNullException>(() => SaveManager.Load(null!));
        Assert.Throws<ArgumentException>(() => SaveManager.Load("   "));

        string missing = Path.Combine(
            Path.GetTempPath(), "geminiball-missing-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<FileNotFoundException>(() => SaveManager.Load(missing));

        InvalidDataException version = Assert.Throws<InvalidDataException>(
            () => SaveManager.Deserialize("{\"saveVersion\":2,\"packId\":\"default-country\"}"));
        Assert.Contains("version", version.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<InvalidDataException>(() => SaveManager.Deserialize("{}"));
        Assert.Throws<InvalidDataException>(() => SaveManager.Deserialize("this is not json"));
        Assert.Throws<InvalidDataException>(
            () => SaveManager.Deserialize("{\"saveVersion\":1,\"packId\":\"default-country\"}"));

        // The file header is the cheap, stable part of the format: version first, camelCase, named pack.
        byte[] header = new byte[64];
        using (var stream = new FileStream(_fixture.SavePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(header.Length, stream.Read(header, 0, header.Length));
        }

        string headerText = Encoding.UTF8.GetString(header);
        Assert.StartsWith("{\"saveVersion\":1,\"packId\":\"default-country\"", headerText, StringComparison.Ordinal);

        // A save naming another pack is rejected before its (much larger) world graph is rebuilt.
        InvalidDataException mismatched = Assert.Throws<InvalidDataException>(
            () => SaveManager.Deserialize("{\"saveVersion\":1,\"packId\":\"another-country\"}"));
        Assert.Contains("another-country", mismatched.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsCorruptWorldSnapshots()
    {
        const string Header = "{\"saveVersion\":1,\"packId\":\"default-country\",\"world\":";
        const string Footer = ",\"career\":{}}";

        // Unknown random-stream enum value (stream 99 is not a WorldRandomStream).
        string unknownStream = Header +
            "{\"seed\":1,\"season\":1,\"week\":1,\"rngStates\":[{\"stream\":99,\"state\":1}]," +
            "\"players\":[],\"clubs\":[],\"managers\":[],\"leagues\":[],\"transfers\":[]}" + Footer;
        InvalidDataException streamError = Assert.Throws<InvalidDataException>(
            () => SaveManager.Deserialize(unknownStream));
        Assert.Contains("stream", streamError.Message, StringComparison.OrdinalIgnoreCase);

        // Duplicate entity ids.
        string duplicatePlayers = Header +
            "{\"seed\":1,\"season\":1,\"week\":1,\"rngStates\":[],\"players\":[{\"id\":1},{\"id\":1}]," +
            "\"clubs\":[],\"managers\":[],\"leagues\":[],\"transfers\":[]}" + Footer;
        InvalidDataException duplicateError = Assert.Throws<InvalidDataException>(
            () => SaveManager.Deserialize(duplicatePlayers));
        Assert.Contains("duplicate", duplicateError.Message, StringComparison.OrdinalIgnoreCase);

        // A player whose club is not in the restored registry.
        string danglingClub = Header +
            "{\"seed\":1,\"season\":1,\"week\":1,\"rngStates\":[]," +
            "\"players\":[{\"id\":1,\"age\":17,\"birthWeek\":10,\"preferredFoot\":0,\"backstory\":2," +
            "\"trainingFocus\":0,\"weakFoot\":0.5,\"condition\":1.0,\"sharpness\":0.6,\"form\":0.5," +
            "\"visibility\":0.3,\"clubId\":999}]," +
            "\"clubs\":[],\"managers\":[]," +
            "\"leagues\":[{\"id\":1,\"tier\":1,\"name\":\"Division\",\"shortName\":\"Div\"," +
            "\"clubIds\":[],\"fixtures\":[]}],\"transfers\":[]}" + Footer;
        InvalidDataException clubError = Assert.Throws<InvalidDataException>(
            () => SaveManager.Deserialize(danglingClub));
        Assert.Contains("club 999", clubError.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Attribute triples and ceiling nudges for five deterministically sampled players.</summary>
    internal static string SampledAttributeTriples(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int count = world.Players.Count;
        int[] samples = { 0, count / 3, count / 2, (count * 2) / 3, count - 1 };
        var builder = new StringBuilder(1 << 16);
        for (int sample = 0; sample < samples.Length; sample++)
        {
            WorldPlayer player = world.Players.All[samples[sample]];
            builder.Append('P').Append(player.Id.Value).Append('|');
            for (int index = 0; index < AttributeMetadata.Count; index++)
            {
                AttributeId attribute = AttributeMetadata.All[index];
                PlayerAttributeState state = player.Attributes[attribute];
                builder.Append(state.Current).Append(',').Append(state.Ceiling).Append(',')
                    .Append(state.Plasticity).Append(':').Append(player.CeilingNudgeApplied(attribute))
                    .Append(';');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Every named RNG stream position, sorted by stream value.</summary>
    internal static string SortedRngStates(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        IReadOnlyDictionary<WorldRandomStream, ulong> states = world.CaptureRngStates();
        var pairs = new List<KeyValuePair<WorldRandomStream, ulong>>(states.Count);
        foreach (KeyValuePair<WorldRandomStream, ulong> pair in states) pairs.Add(pair);
        pairs.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        var builder = new StringBuilder();
        for (int index = 0; index < pairs.Count; index++)
        {
            builder.Append((int)pairs[index].Key).Append('=').Append(pairs[index].Value).Append(';');
        }

        return builder.ToString();
    }

    /// <summary>Every league's club ids in registration order.</summary>
    internal static string LeagueClubOrder(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var builder = new StringBuilder();
        for (int leagueIndex = 0; leagueIndex < world.Leagues.Count; leagueIndex++)
        {
            WorldLeague league = world.Leagues[leagueIndex];
            builder.Append(league.Id.Value).Append(':');
            for (int clubIndex = 0; clubIndex < league.Clubs.Count; clubIndex++)
            {
                builder.Append(league.Clubs[clubIndex].Value).Append(',');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Count of played league fixtures (the world's recorded result volume).</summary>
    internal static int PlayedLeagueFixtureCount(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int count = 0;
        for (int leagueIndex = 0; leagueIndex < world.Leagues.Count; leagueIndex++)
        {
            WorldLeague league = world.Leagues[leagueIndex];
            for (int fixtureIndex = 0; fixtureIndex < league.Fixtures.Count; fixtureIndex++)
            {
                if (league.Fixtures[fixtureIndex].IsPlayed) count++;
            }
        }

        return count;
    }
}

/// <summary>
/// Generated-once save fixture: one mid-season career saved to a temp file, plus the reference fingerprints
/// and the prepared-live-match session the tests need. The temp directory is removed on dispose.
/// </summary>
public sealed class SaveFixture : IDisposable
{
    /// <summary>Calendar week the saved career has reached (mid-season, after real league football).</summary>
    private const int MidSeasonWeek = 20;

    private readonly string _directory;

    public SaveFixture()
    {
        var stopwatch = Stopwatch.StartNew();
        _directory = Path.Combine(Path.GetTempPath(), "geminiball-savemanager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        SavePath = Path.Combine(_directory, "career-save.json");

        CareerSession original = CareerTestHarness.CreateCareer();
        CareerTestHarness.AdvanceUntil(
            original,
            result => result.Date.Week >= MidSeasonWeek,
            CareerTestHarness.ResponsePolicy.RenewalsOnly);
        Assert.True(original.CanSave);

        SnapshotWeek = original.Date.Week;
        SnapshotSeason = original.Date.Season;
        SnapshotAbsoluteWeek = original.Date.AbsoluteWeek;
        HumanPlayerId = original.HumanPlayerId;
        HumanClubId = original.HumanClub?.Id ?? ClubId.None;
        HumanCondition = original.HumanPlayer.Condition01;
        TrainingFocus = original.TrainingFocus;
        LastPlayerId = original.World.CaptureIdCounters().LastPlayerId;
        PlayedLeagueFixtureCount = SaveManagerTests.PlayedLeagueFixtureCount(original.World);
        TransferCount = original.World.TransferLog.Count;
        InboxCount = original.Inbox.Count;
        HistorySeasons = original.History.SeasonsPlayed;
        CupSeason = original.World.Cup.Season;
        CupRoundCount = original.World.Cup.Rounds.Count;
        CupComplete = original.World.Cup.IsComplete;
        CupChampion = original.World.Cup.ChampionClubId;
        SampledAttributeTriples = SaveManagerTests.SampledAttributeTriples(original.World);
        RngStatesFingerprint = SaveManagerTests.SortedRngStates(original.World);
        LeagueClubOrder = SaveManagerTests.LeagueClubOrder(original.World);
        SnapshotFingerprint = CareerTestHarness.Fingerprint(original);

        SaveManager.Save(original, SavePath);
        SaveSize = new FileInfo(SavePath).Length;
        Assert.True(SaveSize > 0L);

        // Reference continuation: the live session is advanced exactly like a reloaded one will be, so the
        // test compares "saved-and-reloaded future" against "same-session future".
        ContinuationWeeks = 2;
        CareerTestHarness.AdvanceWeeks(
            original, ContinuationWeeks, CareerTestHarness.ResponsePolicy.RenewalsOnly);
        ContinuationFingerprint = CareerTestHarness.Fingerprint(original);

        // Reach a prepared live match: the one career state the save layer refuses to capture.
        CareerTestHarness.AdvanceUntil(
            original,
            result => result.Kind == CareerEventKind.HumanMatchday,
            CareerTestHarness.ResponsePolicy.RenewalsOnly);
        original.PrepareHumanLiveMatch();
        LiveSession = original;
        stopwatch.Stop();
        CreationMilliseconds = stopwatch.ElapsedMilliseconds;
    }

    internal string SavePath { get; }

    internal long SaveSize { get; }

    internal long CreationMilliseconds { get; }

    internal int SnapshotWeek { get; }

    internal int SnapshotSeason { get; }

    internal int SnapshotAbsoluteWeek { get; }

    internal PlayerId HumanPlayerId { get; }

    internal ClubId HumanClubId { get; }

    internal float HumanCondition { get; }

    internal TrainingFocus TrainingFocus { get; }

    internal int LastPlayerId { get; }

    internal int PlayedLeagueFixtureCount { get; }

    internal int TransferCount { get; }

    internal int InboxCount { get; }

    internal int HistorySeasons { get; }

    internal int CupSeason { get; }

    internal int CupRoundCount { get; }

    internal bool CupComplete { get; }

    internal ClubId CupChampion { get; }

    internal string SampledAttributeTriples { get; }

    internal string RngStatesFingerprint { get; }

    internal string LeagueClubOrder { get; }

    internal string SnapshotFingerprint { get; }

    internal string ContinuationFingerprint { get; }

    internal int ContinuationWeeks { get; }

    /// <summary>The original session with a prepared live match (not savable) and its match context.</summary>
    internal CareerSession LiveSession { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail the suite.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
