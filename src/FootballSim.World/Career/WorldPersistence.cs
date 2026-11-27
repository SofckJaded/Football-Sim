using System;
using System.Collections.Generic;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Career;

/// <summary>
/// Converts the live <see cref="WorldState"/> to a serializable snapshot and back (plan stage W4).
///
/// <para><b>One source of truth.</b> A save is not a replay log: it is the complete mutable world, captured
/// field by field. Every ordered collection is written in its live order (<see cref="PlayerRegistry.All"/>,
/// <see cref="ClubRegistry.All"/>, <see cref="ManagerRegistry.All"/>, leagues, squads, fixtures, cup ties,
/// transfer log), because that order is simulation-significant: growth draws, scouting jitter and lifecycle
/// walks all consume the registries in registration order, and fixture order decides quick-sim draw order.</para>
///
/// <para><b>Determinism.</b> The root seed is immutable and comes back with the snapshot, and every named
/// random stream is restored through <see cref="WorldState.RestoreRngStates"/> from its documented derived
/// seed, so future <see cref="WorldRng.Fork(ulong)"/> calls derive exactly as they would have without a
/// save. Id counters are restored exactly from <see cref="WorldState.CaptureIdCounters"/>. Derived state
/// (league tables, the cup's current round shortcut, selection decisions) is deliberately not stored: it is
/// a pure function of the fixtures and bracket that are.</para>
///
/// <para><b>Validation.</b> Restore never repairs a save silently. Unknown enum values, duplicate ids,
/// dangling club/player/manager references, fixtures with sentinel club ids and out-of-range counters all
/// throw <see cref="InvalidDataException"/> with the offending entry named.</para>
/// </summary>
internal static class WorldPersistence
{
    // =============================================================================================
    // Capture: live world -> snapshot.
    // =============================================================================================

    /// <summary>Captures every piece of mutable world state into a serializable snapshot.</summary>
    /// <param name="world">The live world to capture.</param>
    /// <returns>The snapshot, ready for JSON serialization.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    internal static WorldSaveData Capture(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);

        (int lastPlayerId, int lastMatchId, int lastManagerId) = world.CaptureIdCounters();
        var data = new WorldSaveData
        {
            Seed = world.Seed,
            Season = world.Season,
            Week = world.Week,
            LastPlayerId = lastPlayerId,
            LastMatchId = lastMatchId,
            LastManagerId = lastManagerId,
            RngStates = CaptureRngStates(world)
        };

        IReadOnlyList<WorldPlayer> players = world.Players.All;
        for (int index = 0; index < players.Count; index++)
        {
            data.Players.Add(CapturePlayer(players[index]));
        }

        IReadOnlyList<WorldClub> clubs = world.Clubs.All;
        for (int index = 0; index < clubs.Count; index++)
        {
            data.Clubs.Add(CaptureClub(clubs[index]));
        }

        IReadOnlyList<WorldManager> managers = world.Managers.All;
        for (int index = 0; index < managers.Count; index++)
        {
            data.Managers.Add(CaptureManager(managers[index]));
        }

        IReadOnlyList<WorldLeague> leagues = world.Leagues;
        for (int index = 0; index < leagues.Count; index++)
        {
            data.Leagues.Add(CaptureLeague(leagues[index]));
        }

        data.Cup = CaptureCup(world.Cup);

        IReadOnlyList<TransferRecord> transfers = world.TransferLog;
        for (int index = 0; index < transfers.Count; index++)
        {
            data.Transfers.Add(CaptureTransfer(transfers[index]));
        }

        return data;
    }

    /// <summary>Captures the named random streams in a stable (enum-value) order for stable save bytes.</summary>
    private static List<WorldRngEntrySave> CaptureRngStates(WorldState world)
    {
        IReadOnlyDictionary<WorldRandomStream, ulong> states = world.CaptureRngStates();
        var entries = new List<WorldRngEntrySave>(states.Count);
        foreach (KeyValuePair<WorldRandomStream, ulong> pair in states)
        {
            entries.Add(new WorldRngEntrySave { Stream = (int)pair.Key, State = pair.Value });
        }

        entries.Sort(static (left, right) => left.Stream.CompareTo(right.Stream));
        return entries;
    }

    /// <summary>Captures one player, including the hidden ledger and every bounded history window.</summary>
    private static WorldPlayerSave CapturePlayer(WorldPlayer player)
    {
        var save = new WorldPlayerSave
        {
            Id = player.Id.Value,
            FirstName = player.FirstName,
            LastName = player.LastName,
            Age = player.Age,
            BirthWeek = player.BirthWeek,
            PreferredFoot = (int)player.PreferredFoot,
            WeakFoot = player.WeakFoot01,
            Backstory = (int)player.Backstory,
            ClubId = player.ClubId.Value,
            SuspensionWeeks = player.SuspensionWeeksRemaining,
            Condition = player.Condition01,
            Sharpness = player.MatchSharpness01,
            Form = player.Form01,
            Visibility = player.Visibility01,
            TrainingFocus = (int)player.TrainingFocus,
            IsRetired = player.IsRetired,
            Injury = new WorldInjurySave
            {
                Kind = (int)player.Injury.Kind,
                Severity = (int)player.Injury.Severity,
                WeeksOutRemaining = player.Injury.WeeksOutRemaining,
                TotalWeeksOut = player.Injury.TotalWeeksOut,
                PermanentDamageApplied = player.Injury.PermanentDamageApplied
            },
            Contract = new WorldContractSave
            {
                SignedSeason = player.Contract.SignedSeason,
                EndSeason = player.Contract.EndSeason,
                WeeklyWage = player.Contract.WeeklyWage,
                IsOnLoan = player.Contract.IsOnLoan,
                ParentClubId = player.Contract.ParentClubId.Value,
                LoanEndSeason = player.Contract.LoanEndSeason
            },
            CurrentSeason = CaptureSeasonStats(player.CurrentSeason)
        };

        int attributeCount = AttributeMetadata.Count;
        var current = new float[attributeCount];
        var ceiling = new float[attributeCount];
        var plasticity = new float[attributeCount];
        var nudges = new float[attributeCount];
        for (int index = 0; index < attributeCount; index++)
        {
            AttributeId attribute = AttributeMetadata.All[index];
            PlayerAttributeState state = player.Attributes[attribute];
            current[index] = state.Current;
            ceiling[index] = state.Ceiling;
            plasticity[index] = state.Plasticity;
            nudges[index] = player.CeilingNudgeApplied(attribute);
        }

        save.Current = current;
        save.Ceiling = ceiling;
        save.Plasticity = plasticity;
        save.CeilingNudges = nudges;
        save.Latent = new[]
        {
            player.Latent.Athleticism, player.Latent.Technical,
            player.Latent.Mental, player.Latent.Goalkeeping
        };
        save.Personality = new[]
        {
            player.Personality.Resilience, player.Personality.WorkEthic,
            player.Personality.Creativity, player.Personality.Discipline
        };

        IReadOnlyList<MatchAppearance> appearances = player.Form.Appearances;
        var form = new List<WorldAppearanceSave>(appearances.Count);
        for (int index = 0; index < appearances.Count; index++)
        {
            MatchAppearance appearance = appearances[index];
            form.Add(new WorldAppearanceSave
            {
                Season = appearance.Season,
                Week = appearance.Week,
                Minutes = appearance.Minutes,
                Rating = appearance.Rating
            });
        }

        save.RecentForm = form;

        IReadOnlyList<float[]> snapshots = player.Trends.Snapshots;
        var trends = new List<float[]>(snapshots.Count);
        for (int index = 0; index < snapshots.Count; index++)
        {
            trends.Add((float[])snapshots[index].Clone());
        }

        save.Trends = trends;

        for (int index = 0; index < player.SeasonHistory.Count; index++)
        {
            save.SeasonHistory.Add(CaptureSeasonStats(player.SeasonHistory[index]));
        }

        return save;
    }

    /// <summary>Captures one club's mutable identity, money, facilities, ordered squad and trust table.</summary>
    private static WorldClubSave CaptureClub(WorldClub club)
    {
        var squad = new int[club.Squad.Count];
        for (int index = 0; index < club.Squad.Count; index++)
        {
            squad[index] = club.Squad[index].Value;
        }

        // The live trust table is an unordered dictionary, so the save stores it as a key-sorted list.
        var trust = new List<WorldManagerTrustSave>(club.ManagerTrust.Count);
        foreach (KeyValuePair<PlayerId, float> pair in club.ManagerTrust)
        {
            trust.Add(new WorldManagerTrustSave { PlayerId = pair.Key.Value, Trust = pair.Value });
        }

        trust.Sort(static (left, right) => left.PlayerId.CompareTo(right.PlayerId));

        return new WorldClubSave
        {
            Id = club.Id.Value,
            Name = club.Name,
            LeagueId = club.LeagueId.Value,
            Tier = club.Tier,
            Reputation = club.Reputation,
            ManagerId = club.ManagerId.Value,
            Balance = club.Finances.Balance,
            WeeklyWageBudget = club.Finances.WeeklyWageBudget,
            TransferBudget = club.Finances.TransferBudget,
            WeeklyWageBill = club.Finances.WeeklyWageBill,
            CoachingQuality = club.Facilities.CoachingQuality01,
            MedicalQuality = club.Facilities.MedicalQuality01,
            YouthQuality = club.Facilities.YouthQuality01,
            WeeklyTrainingSessions = club.Facilities.WeeklyTrainingSessions,
            Squad = squad,
            ManagerTrust = trust
        };
    }

    /// <summary>Captures one manager, including every mutable board/performance counter.</summary>
    private static WorldManagerSave CaptureManager(WorldManager manager) => new()
    {
        Id = manager.Id.Value,
        FirstName = manager.FirstName,
        LastName = manager.LastName,
        Age = manager.Age,
        ClubId = manager.ClubId.Value,
        Formation = (int)manager.Formation,
        Reputation = manager.Reputation,
        BoardConfidence = manager.BoardConfidence01,
        SeasonsAtClub = manager.SeasonsAtClub,
        MatchesInCharge = manager.MatchesInCharge,
        Wins = manager.Wins,
        Draws = manager.Draws,
        Losses = manager.Losses,
        TacticalProfile = CaptureTacticalProfile(manager.TacticalProfile)
    };

    /// <summary>Captures a tactical profile by value; the name lets restore reuse a shared preset instance.</summary>
    private static WorldTacticalProfileSave CaptureTacticalProfile(TeamTacticalProfile profile) => new()
    {
        Name = profile.Name,
        Directness = profile.Directness01,
        Tempo = profile.Tempo01,
        AttackingWidth = profile.AttackingWidth01,
        DefensiveLineHeight = profile.DefensiveLineHeight01,
        Compactness = profile.Compactness01,
        PressingIntensity = profile.PressingIntensity01,
        RiskTolerance = profile.RiskTolerance01,
        CounterPressDuration = profile.CounterPressDurationSeconds,
        PositionalFreedom = profile.PositionalFreedom01,
        ShortGoalkeeperDistribution = profile.ShortGoalkeeperDistribution,
        ManOrientation = profile.ManOrientation01
    };

    /// <summary>Captures one league: its ordered club list and this season's fixtures in fixture order.</summary>
    private static WorldLeagueSave CaptureLeague(WorldLeague league)
    {
        var clubIds = new int[league.Clubs.Count];
        for (int index = 0; index < league.Clubs.Count; index++)
        {
            clubIds[index] = league.Clubs[index].Value;
        }

        var save = new WorldLeagueSave
        {
            Id = league.Id.Value,
            Tier = league.Tier,
            Name = league.Name,
            ShortName = league.ShortName,
            ClubIds = clubIds
        };

        IReadOnlyList<WorldFixture> fixtures = league.Fixtures;
        for (int index = 0; index < fixtures.Count; index++)
        {
            save.Fixtures.Add(CaptureFixture(fixtures[index]));
        }

        return save;
    }

    /// <summary>Captures one fixture's immutable identity and recorded result.</summary>
    private static WorldFixtureSave CaptureFixture(WorldFixture fixture) => new()
    {
        Id = fixture.Id.Value,
        Season = fixture.Season,
        Week = fixture.Week,
        Competition = (int)fixture.Competition,
        LeagueId = fixture.LeagueId.Value,
        LeagueMatchday = fixture.LeagueMatchday,
        CupRound = (int)fixture.CupRound,
        HomeClubId = fixture.HomeClubId.Value,
        AwayClubId = fixture.AwayClubId.Value,
        Played = fixture.IsPlayed,
        HomeGoals = fixture.HomeGoals,
        AwayGoals = fixture.AwayGoals,
        HomePenaltyGoals = fixture.HomePenaltyGoals,
        AwayPenaltyGoals = fixture.AwayPenaltyGoals
    };

    /// <summary>Captures the cup: season, ordered rounds (ties plus byes) and the champion ledger.</summary>
    private static WorldCupSave CaptureCup(WorldCup cup)
    {
        var save = new WorldCupSave
        {
            Season = cup.Season,
            Complete = cup.IsComplete,
            ChampionClubId = cup.ChampionClubId.Value
        };

        IReadOnlyList<CupRoundBracket> rounds = cup.Rounds;
        for (int roundIndex = 0; roundIndex < rounds.Count; roundIndex++)
        {
            CupRoundBracket bracket = rounds[roundIndex];
            var byes = new int[bracket.Byes.Count];
            for (int byeIndex = 0; byeIndex < bracket.Byes.Count; byeIndex++)
            {
                byes[byeIndex] = bracket.Byes[byeIndex].Value;
            }

            var round = new WorldCupRoundSave
            {
                Round = (int)bracket.Round,
                Week = bracket.Week,
                Byes = byes
            };

            IReadOnlyList<CupTie> ties = bracket.Ties;
            for (int tieIndex = 0; tieIndex < ties.Count; tieIndex++)
            {
                round.Ties.Add(CaptureFixture(ties[tieIndex].Fixture));
            }

            save.Rounds.Add(round);
        }

        return save;
    }

    /// <summary>Captures one append-only transfer-log entry.</summary>
    private static WorldTransferSave CaptureTransfer(TransferRecord record) => new()
    {
        Season = record.Season,
        Week = record.Week,
        PlayerId = record.PlayerId.Value,
        FromClubId = record.FromClubId.Value,
        ToClubId = record.ToClubId.Value,
        Kind = (int)record.Kind,
        Fee = record.Fee
    };

    /// <summary>Captures one season's counting stats.</summary>
    private static WorldSeasonStatsSave CaptureSeasonStats(PlayerSeasonStats stats) => new()
    {
        Season = stats.Season,
        Appearances = stats.Appearances,
        Starts = stats.Starts,
        SubstituteAppearances = stats.SubstituteAppearances,
        MinutesPlayed = stats.MinutesPlayed,
        Goals = stats.Goals,
        Assists = stats.Assists,
        Shots = stats.Shots,
        PassesAttempted = stats.PassesAttempted,
        PassesCompleted = stats.PassesCompleted,
        Tackles = stats.Tackles,
        Saves = stats.Saves,
        YellowCards = stats.YellowCards,
        RedCards = stats.RedCards,
        RatingSum = stats.RatingSum
    };

    // =============================================================================================
    // Restore: snapshot -> live world.
    // =============================================================================================

    /// <summary>Rebuilds a live, fully restorable world from a snapshot and its data pack.</summary>
    /// <param name="pack">The validated data pack the saved world was generated from.</param>
    /// <param name="data">The snapshot captured by <see cref="Capture"/>.</param>
    /// <returns>A world whose next simulated week is identical to the saved world's next week.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidDataException">The snapshot is internally inconsistent or names unknown data.</exception>
    internal static WorldState Restore(WorldPack pack, WorldSaveData data)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Season < 1 || data.Week is < 1 or > 52)
        {
            throw new InvalidDataException(
                $"The world snapshot has an invalid date (season {data.Season}, week {data.Week}).");
        }

        if (data.LastPlayerId < 0 || data.LastMatchId < 0 || data.LastManagerId < 0)
        {
            throw new InvalidDataException(
                "The world snapshot has a negative id counter (player " + data.LastPlayerId +
                ", match " + data.LastMatchId + ", manager " + data.LastManagerId + ").");
        }

        var world = new WorldState(pack, data.Seed, new WorldDate(data.Season, data.Week));

        HashSet<int> playerIds = CollectIds(data.Players, static entry => entry.Id, "player");
        HashSet<int> clubIds = CollectIds(data.Clubs, static entry => entry.Id, "club");
        HashSet<int> managerIds = CollectIds(data.Managers, static entry => entry.Id, "manager");
        var matchIds = new HashSet<int>();

        RestoreRngStates(world, data.RngStates);
        RestoreLeagues(world, data.Leagues);
        RestoreManagers(world, data.Managers, clubIds);
        RestoreClubs(world, pack, data.Clubs, managerIds);
        RestorePlayers(world, data.Players, clubIds);
        RestoreClubContent(world, data.Clubs, playerIds);
        RestoreLeagueContent(world, data.Leagues, clubIds, matchIds);
        RestoreCup(world, data.Cup, clubIds, matchIds);
        RestoreTransferLog(world, data.Transfers, playerIds, clubIds);

        RequireCounterCovers(data.LastPlayerId, playerIds, "player");
        RequireCounterCovers(data.LastManagerId, managerIds, "manager");
        RequireCounterCovers(data.LastMatchId, matchIds, "match");

        world.RestoreIdCountersExact(data.LastPlayerId, data.LastMatchId, data.LastManagerId);
        return world;
    }

    /// <summary>Restores every captured random stream position, keeping each stream's derived seed.</summary>
    private static void RestoreRngStates(WorldState world, List<WorldRngEntrySave>? saves)
    {
        if (saves is null) throw new InvalidDataException("The world snapshot has no random-stream table.");

        var states = new Dictionary<WorldRandomStream, ulong>(saves.Count);
        for (int index = 0; index < saves.Count; index++)
        {
            WorldRngEntrySave save = saves[index] ?? throw new InvalidDataException("The save has a null RNG entry.");
            RequireEnum<WorldRandomStream>(save.Stream, "random stream");
            var stream = (WorldRandomStream)save.Stream;
            if (!states.TryAdd(stream, save.State))
            {
                throw new InvalidDataException($"The save has two RNG states for stream {stream}.");
            }
        }

        world.RestoreRngStates(states);
    }

    /// <summary>Registers the six leagues in saved (ascending tier) order.</summary>
    private static void RestoreLeagues(WorldState world, List<WorldLeagueSave>? saves)
    {
        if (saves is null) throw new InvalidDataException("The world snapshot has no leagues.");
        if (saves.Count == 0) throw new InvalidDataException("The world snapshot has no leagues.");

        var leagueIds = new HashSet<int>();
        for (int index = 0; index < saves.Count; index++)
        {
            WorldLeagueSave save = saves[index] ?? throw new InvalidDataException("The save has a null league entry.");
            if (save.Id <= 0) throw new InvalidDataException($"The save has an invalid league id {save.Id}.");
            if (!leagueIds.Add(save.Id)) throw new InvalidDataException($"The save has a duplicate league id {save.Id}.");
            if (save.Tier is < 1 or > 6)
            {
                throw new InvalidDataException($"League {save.Id} has tier {save.Tier} outside 1..6.");
            }

            world.AddLeague(new WorldLeague(
                new LeagueId(save.Id),
                save.Tier,
                save.Name ?? string.Empty,
                save.ShortName ?? string.Empty));
        }
    }

    /// <summary>Restores the manager registry in saved order, profile values included.</summary>
    private static void RestoreManagers(WorldState world, List<WorldManagerSave>? saves, HashSet<int> clubIds)
    {
        if (saves is null) throw new InvalidDataException("The world snapshot has no managers.");

        for (int index = 0; index < saves.Count; index++)
        {
            WorldManagerSave save = saves[index] ?? throw new InvalidDataException("The save has a null manager entry.");
            RequireEnum<FormationKind>(save.Formation, "formation");
            if (save.Age is < 30 or > 75)
            {
                throw new InvalidDataException($"Manager {save.Id} has age {save.Age} outside 30..75.");
            }

            if (save.Reputation is < 1 or > 100)
            {
                throw new InvalidDataException($"Manager {save.Id} has reputation {save.Reputation} outside 1..100.");
            }

            if (save.BoardConfidence is < 0f or > 1f || !float.IsFinite(save.BoardConfidence))
            {
                throw new InvalidDataException($"Manager {save.Id} has board confidence {save.BoardConfidence} outside 0..1.");
            }

            RequireNonNegative(save.SeasonsAtClub, $"manager {save.Id} seasons at club");
            RequireNonNegative(save.MatchesInCharge, $"manager {save.Id} matches in charge");
            RequireNonNegative(save.Wins, $"manager {save.Id} wins");
            RequireNonNegative(save.Draws, $"manager {save.Id} draws");
            RequireNonNegative(save.Losses, $"manager {save.Id} losses");
            if (save.ClubId != 0) RequireKnownClub(clubIds, save.ClubId, $"Manager {save.Id}");

            TeamTacticalProfile profile = RestoreTacticalProfile(save.TacticalProfile, save.Id);
            var manager = new WorldManager(
                new ManagerId(save.Id),
                save.FirstName ?? string.Empty,
                save.LastName ?? string.Empty,
                save.Age,
                new ClubId(save.ClubId),
                profile,
                (FormationKind)save.Formation,
                save.Reputation)
            {
                BoardConfidence01 = save.BoardConfidence,
                SeasonsAtClub = save.SeasonsAtClub,
                MatchesInCharge = save.MatchesInCharge,
                Wins = save.Wins,
                Draws = save.Draws,
                Losses = save.Losses
            };

            world.Managers.Add(manager);
        }
    }

    /// <summary>
    /// Rebuilds a tactical profile: the matching shared preset instance when the saved values are exactly
    /// that preset, otherwise a value-identical profile of the saved shape (the properties are init-only).
    /// </summary>
    private static TeamTacticalProfile RestoreTacticalProfile(WorldTacticalProfileSave? save, int managerId)
    {
        if (save is null)
        {
            throw new InvalidDataException($"Manager {managerId} has no tactical profile in the save.");
        }

        RequireFinite(save.Directness, $"manager {managerId} directness");
        RequireFinite(save.Tempo, $"manager {managerId} tempo");
        RequireFinite(save.AttackingWidth, $"manager {managerId} attacking width");
        RequireFinite(save.DefensiveLineHeight, $"manager {managerId} defensive line height");
        RequireFinite(save.Compactness, $"manager {managerId} compactness");
        RequireFinite(save.PressingIntensity, $"manager {managerId} pressing intensity");
        RequireFinite(save.RiskTolerance, $"manager {managerId} risk tolerance");
        RequireFinite(save.CounterPressDuration, $"manager {managerId} counter-press duration");
        RequireFinite(save.PositionalFreedom, $"manager {managerId} positional freedom");
        RequireFinite(save.ManOrientation, $"manager {managerId} man orientation");

        TeamTacticalProfile[] presets =
        {
            TeamTacticalProfile.PossessionControl,
            TeamTacticalProfile.Direct,
            TeamTacticalProfile.DirectCounterattack,
            TeamTacticalProfile.HighPressGegenpress,
            TeamTacticalProfile.LowBlock,
            TeamTacticalProfile.ManOriented
        };

        for (int index = 0; index < presets.Length; index++)
        {
            TeamTacticalProfile preset = presets[index];
            if (string.Equals(preset.Name, save.Name, StringComparison.Ordinal) && Matches(preset, save))
            {
                return preset;
            }
        }

        return new TeamTacticalProfile
        {
            Name = save.Name ?? string.Empty,
            Directness01 = save.Directness,
            Tempo01 = save.Tempo,
            AttackingWidth01 = save.AttackingWidth,
            DefensiveLineHeight01 = save.DefensiveLineHeight,
            Compactness01 = save.Compactness,
            PressingIntensity01 = save.PressingIntensity,
            RiskTolerance01 = save.RiskTolerance,
            CounterPressDurationSeconds = save.CounterPressDuration,
            PositionalFreedom01 = save.PositionalFreedom,
            ShortGoalkeeperDistribution = save.ShortGoalkeeperDistribution,
            ManOrientation01 = save.ManOrientation
        };
    }

    /// <summary>True when every persisted tactic value equals the preset's value.</summary>
    private static bool Matches(TeamTacticalProfile preset, WorldTacticalProfileSave save) =>
        preset.Directness01 == save.Directness &&
        preset.Tempo01 == save.Tempo &&
        preset.AttackingWidth01 == save.AttackingWidth &&
        preset.DefensiveLineHeight01 == save.DefensiveLineHeight &&
        preset.Compactness01 == save.Compactness &&
        preset.PressingIntensity01 == save.PressingIntensity &&
        preset.RiskTolerance01 == save.RiskTolerance &&
        preset.CounterPressDurationSeconds == save.CounterPressDuration &&
        preset.PositionalFreedom01 == save.PositionalFreedom &&
        preset.ShortGoalkeeperDistribution == save.ShortGoalkeeperDistribution &&
        preset.ManOrientation01 == save.ManOrientation;

    /// <summary>Restores the club registry in saved order, resolving each club's identity from its name.</summary>
    private static void RestoreClubs(
        WorldState world, WorldPack pack, List<WorldClubSave>? saves, HashSet<int> managerIds)
    {
        if (saves is null) throw new InvalidDataException("The world snapshot has no clubs.");

        var definitions = new Dictionary<string, ClubDef>(pack.Clubs.Count, StringComparer.Ordinal);
        for (int index = 0; index < pack.Clubs.Count; index++)
        {
            ClubDef definition = pack.Clubs[index];
            definitions.TryAdd(definition.Name, definition);
        }

        for (int index = 0; index < saves.Count; index++)
        {
            WorldClubSave save = saves[index] ?? throw new InvalidDataException("The save has a null club entry.");
            if (string.IsNullOrEmpty(save.Name))
            {
                throw new InvalidDataException($"Club {save.Id} has no name in the save.");
            }

            if (!definitions.TryGetValue(save.Name, out ClubDef? definition))
            {
                throw new InvalidDataException(
                    $"Club '{save.Name}' (id {save.Id}) is not defined by world pack '{pack.PackId}'.");
            }

            if (save.Tier is < 1 or > 6)
            {
                throw new InvalidDataException($"Club {save.Id} has tier {save.Tier} outside 1..6.");
            }

            if (save.LeagueId <= 0) throw new InvalidDataException($"Club {save.Id} has no league.");
            RequireLeague(world, save.LeagueId, $"Club {save.Id}");
            if (save.ManagerId != 0) RequireKnownManager(managerIds, save.ManagerId, $"Club {save.Id}");

            RequireRange(save.CoachingQuality, 0.70f, 1.30f, $"club {save.Id} coaching quality");
            RequireRange(save.MedicalQuality, 0f, 1f, $"club {save.Id} medical quality");
            RequireRange(save.YouthQuality, 0f, 1f, $"club {save.Id} youth quality");
            RequireRange(save.WeeklyTrainingSessions, 1, 6, $"club {save.Id} weekly training sessions");

            var facilities = new ClubFacilities
            {
                CoachingQuality01 = save.CoachingQuality,
                MedicalQuality01 = save.MedicalQuality,
                YouthQuality01 = save.YouthQuality,
                WeeklyTrainingSessions = save.WeeklyTrainingSessions
            };

            var finances = new ClubFinances
            {
                Balance = save.Balance,
                WeeklyWageBudget = save.WeeklyWageBudget,
                TransferBudget = save.TransferBudget,
                WeeklyWageBill = save.WeeklyWageBill
            };

            var club = new WorldClub(
                new ClubId(save.Id),
                definition,
                new LeagueId(save.LeagueId),
                new ManagerId(save.ManagerId),
                facilities,
                finances)
            {
                Tier = save.Tier,
                Reputation = save.Reputation
            };

            world.Clubs.Add(club);
        }
    }

    /// <summary>Restores every player in saved (registration) order.</summary>
    private static void RestorePlayers(WorldState world, List<WorldPlayerSave>? saves, HashSet<int> clubIds)
    {
        if (saves is null) throw new InvalidDataException("The world snapshot has no players.");

        for (int index = 0; index < saves.Count; index++)
        {
            WorldPlayerSave save = saves[index] ?? throw new InvalidDataException("The save has a null player entry.");
            RequireEnum<Footedness>(save.PreferredFoot, "preferred foot");
            RequireEnum<BackstoryKind>(save.Backstory, "backstory");
            RequireEnum<TrainingFocus>(save.TrainingFocus, "training focus");
            if (save.Age is < WorldPlayer.MinimumAge or > WorldPlayer.MaximumAge)
            {
                throw new InvalidDataException($"Player {save.Id} has age {save.Age} outside the supported range.");
            }

            if (save.BirthWeek is < 1 or > 52)
            {
                throw new InvalidDataException($"Player {save.Id} has birth week {save.BirthWeek} outside 1..52.");
            }

            RequireRange(save.WeakFoot, 0f, 1f, $"player {save.Id} weak foot");
            RequireNonNegative(save.SuspensionWeeks, $"player {save.Id} suspension weeks");
            RequireRange(save.Condition, 0f, 1f, $"player {save.Id} condition");
            RequireRange(save.Sharpness, 0f, 1f, $"player {save.Id} sharpness");
            RequireRange(save.Form, 0f, 1f, $"player {save.Id} form");
            RequireRange(save.Visibility, 0f, 1f, $"player {save.Id} visibility");
            if (save.ClubId != 0) RequireKnownClub(clubIds, save.ClubId, $"Player {save.Id}");

            PlayerAttributeSet attributes = RestoreAttributes(save);
            var latent = new LatentAptitudes(
                RequireTrait(save.Latent, 0, $"player {save.Id} latent athleticism"),
                RequireTrait(save.Latent, 1, $"player {save.Id} latent technical"),
                RequireTrait(save.Latent, 2, $"player {save.Id} latent mental"),
                RequireTrait(save.Latent, 3, $"player {save.Id} latent goalkeeping"));
            var personality = new PersonalityTraits(
                RequireTrait(save.Personality, 0, $"player {save.Id} resilience"),
                RequireTrait(save.Personality, 1, $"player {save.Id} work ethic"),
                RequireTrait(save.Personality, 2, $"player {save.Id} creativity"),
                RequireTrait(save.Personality, 3, $"player {save.Id} discipline"));

            var player = new WorldPlayer(
                new PlayerId(save.Id),
                save.FirstName ?? string.Empty,
                save.LastName ?? string.Empty,
                save.Age,
                save.BirthWeek,
                (Footedness)save.PreferredFoot,
                save.WeakFoot,
                attributes,
                latent,
                personality,
                (BackstoryKind)save.Backstory)
            {
                ClubId = new ClubId(save.ClubId),
                SuspensionWeeksRemaining = save.SuspensionWeeks,
                Condition01 = save.Condition,
                MatchSharpness01 = save.Sharpness,
                Form01 = save.Form,
                Visibility01 = save.Visibility,
                TrainingFocus = (TrainingFocus)save.TrainingFocus,
                IsRetired = save.IsRetired
            };

            RestoreInjury(player, save.Injury, save.Id);
            RestoreContract(player, save.Contract, save.Id, clubIds);
            RestoreRecentForm(player, save.RecentForm, save.Id);
            RestoreTrends(player, save.Trends, save.Id);
            RestoreSeasonStats(player.CurrentSeason, save.CurrentSeason);
            for (int historyIndex = 0; historyIndex < save.SeasonHistory.Count; historyIndex++)
            {
                WorldSeasonStatsSave history = save.SeasonHistory[historyIndex]
                    ?? throw new InvalidDataException($"Player {save.Id} has a null season-history entry.");
                var stats = new PlayerSeasonStats();
                RestoreSeasonStats(stats, history);
                player.SeasonHistory.Add(stats);
            }

            RestoreCeilingNudges(player, save.CeilingNudges, save.Id);
            world.Players.Add(player);
        }
    }

    /// <summary>Rebuilds the eighteen attribute states in <see cref="AttributeId"/> order.</summary>
    private static PlayerAttributeSet RestoreAttributes(WorldPlayerSave save)
    {
        int attributeCount = AttributeMetadata.Count;
        float[] current = RequireArray(save.Current, attributeCount, $"player {save.Id} current attributes");
        float[] ceiling = RequireArray(save.Ceiling, attributeCount, $"player {save.Id} attribute ceilings");
        float[] plasticity = RequireArray(save.Plasticity, attributeCount, $"player {save.Id} attribute plasticity");

        var states = new PlayerAttributeState[attributeCount];
        for (int index = 0; index < attributeCount; index++)
        {
            float currentValue = current[index];
            float ceilingValue = ceiling[index];
            float plasticityValue = plasticity[index];
            if (!float.IsFinite(currentValue) || !float.IsFinite(ceilingValue) || !float.IsFinite(plasticityValue))
            {
                throw new InvalidDataException($"Player {save.Id} has a non-finite attribute at index {index}.");
            }

            if (ceilingValue is < AttributeMetadata.MinimumValue or > AttributeMetadata.MaximumCeiling)
            {
                throw new InvalidDataException(
                    $"Player {save.Id} has attribute ceiling {ceilingValue} at index {index} outside " +
                    $"{AttributeMetadata.MinimumValue}..{AttributeMetadata.MaximumCeiling}.");
            }

            if (plasticityValue is < AttributeMetadata.MinimumPlasticity or > AttributeMetadata.MaximumPlasticity)
            {
                throw new InvalidDataException(
                    $"Player {save.Id} has plasticity {plasticityValue} at index {index} outside " +
                    $"{AttributeMetadata.MinimumPlasticity}..{AttributeMetadata.MaximumPlasticity}.");
            }

            if (currentValue < AttributeMetadata.MinimumValue || currentValue > ceilingValue)
            {
                throw new InvalidDataException(
                    $"Player {save.Id} has current attribute {currentValue} at index {index} outside " +
                    $"{AttributeMetadata.MinimumValue}..{ceilingValue}.");
            }

            states[index] = new PlayerAttributeState(currentValue, ceilingValue, plasticityValue);
        }

        return new PlayerAttributeSet(states);
    }

    /// <summary>Restores a saved mid-injury state through the additive <see cref="InjuryState.Restore"/> hook.</summary>
    private static void RestoreInjury(WorldPlayer player, WorldInjurySave? save, int playerId)
    {
        if (save is null) throw new InvalidDataException($"Player {playerId} has no injury state.");
        RequireEnum<InjuryKind>(save.Kind, "injury kind");
        RequireEnum<InjurySeverity>(save.Severity, "injury severity");
        RequireNonNegative(save.WeeksOutRemaining, $"player {playerId} injury weeks remaining");
        RequireNonNegative(save.TotalWeeksOut, $"player {playerId} injury total weeks");
        RequireNonNegative(save.PermanentDamageApplied, $"player {playerId} injury permanent damage");
        if (save.WeeksOutRemaining > save.TotalWeeksOut)
        {
            throw new InvalidDataException(
                $"Player {playerId} has {save.WeeksOutRemaining} injury weeks left of a {save.TotalWeeksOut}-week injury.");
        }

        if (save.WeeksOutRemaining > 0 && save.Kind == (int)InjuryKind.None)
        {
            throw new InvalidDataException($"Player {playerId} is injured without an injury kind.");
        }

        if (save.WeeksOutRemaining > 0 && save.Severity == (int)InjurySeverity.None)
        {
            throw new InvalidDataException($"Player {playerId} is injured without an injury severity.");
        }

        player.Injury.Restore(
            (InjuryKind)save.Kind,
            (InjurySeverity)save.Severity,
            save.WeeksOutRemaining,
            save.TotalWeeksOut,
            save.PermanentDamageApplied);
    }

    /// <summary>Restores the six mutable contract fields in place (the property itself is get-only).</summary>
    private static void RestoreContract(WorldPlayer player, WorldContractSave? save, int playerId, HashSet<int> clubIds)
    {
        if (save is null) throw new InvalidDataException($"Player {playerId} has no contract.");
        if (save.WeeklyWage < 0L)
        {
            throw new InvalidDataException($"Player {playerId} has a negative weekly wage {save.WeeklyWage}.");
        }

        RequireNonNegative(save.LoanEndSeason, $"player {playerId} loan end season");
        if (save.ParentClubId != 0) RequireKnownClub(clubIds, save.ParentClubId, $"Player {playerId} contract");

        player.Contract.SignedSeason = save.SignedSeason;
        player.Contract.EndSeason = save.EndSeason;
        player.Contract.WeeklyWage = save.WeeklyWage;
        player.Contract.IsOnLoan = save.IsOnLoan;
        player.Contract.ParentClubId = new ClubId(save.ParentClubId);
        player.Contract.LoanEndSeason = save.LoanEndSeason;
    }

    /// <summary>Replays the bounded appearance window oldest first.</summary>
    private static void RestoreRecentForm(WorldPlayer player, List<WorldAppearanceSave>? saves, int playerId)
    {
        if (saves is null) throw new InvalidDataException($"Player {playerId} has no recent-form list.");
        if (saves.Count > RecentForm.MaxAppearances)
        {
            throw new InvalidDataException(
                $"Player {playerId} has {saves.Count} recent appearances; the window holds at most " +
                $"{RecentForm.MaxAppearances}.");
        }

        for (int index = 0; index < saves.Count; index++)
        {
            WorldAppearanceSave save = saves[index]
                ?? throw new InvalidDataException($"Player {playerId} has a null appearance entry.");
            RequireNonNegative(save.Minutes, $"player {playerId} appearance minutes");
            player.Form.Record(new MatchAppearance(save.Season, save.Week, save.Minutes, save.Rating));
        }
    }

    /// <summary>Replaces the whole 12-week trend window in one call.</summary>
    private static void RestoreTrends(WorldPlayer player, List<float[]>? saves, int playerId)
    {
        if (saves is null) throw new InvalidDataException($"Player {playerId} has no trend history.");
        if (saves.Count > PlayerTrendHistory.WindowWeeks + 1)
        {
            throw new InvalidDataException(
                $"Player {playerId} has {saves.Count} trend snapshots; the window holds at most " +
                $"{PlayerTrendHistory.WindowWeeks + 1}.");
        }

        for (int index = 0; index < saves.Count; index++)
        {
            float[]? snapshot = saves[index];
            if (snapshot is null)
            {
                throw new InvalidDataException($"Player {playerId} has a null trend snapshot at index {index}.");
            }

            if (snapshot.Length != AttributeMetadata.Count)
            {
                throw new InvalidDataException(
                    $"Player {playerId} has a {snapshot.Length}-value trend snapshot at index {index}; " +
                    $"expected {AttributeMetadata.Count}.");
            }
        }

        player.Trends.Restore(saves);
    }

    /// <summary>Restores one season-stats block.</summary>
    private static void RestoreSeasonStats(PlayerSeasonStats target, WorldSeasonStatsSave? save)
    {
        if (save is null) throw new InvalidDataException("The save has a null season-stats block.");

        target.Season = save.Season;
        target.Appearances = save.Appearances;
        target.Starts = save.Starts;
        target.SubstituteAppearances = save.SubstituteAppearances;
        target.MinutesPlayed = save.MinutesPlayed;
        target.Goals = save.Goals;
        target.Assists = save.Assists;
        target.Shots = save.Shots;
        target.PassesAttempted = save.PassesAttempted;
        target.PassesCompleted = save.PassesCompleted;
        target.Tackles = save.Tackles;
        target.Saves = save.Saves;
        target.YellowCards = save.YellowCards;
        target.RedCards = save.RedCards;
        target.RatingSum = save.RatingSum;
    }

    /// <summary>Replays the lifetime ceiling-nudge ledger (the only non-public writer is the additive method).</summary>
    private static void RestoreCeilingNudges(WorldPlayer player, float[]? saves, int playerId)
    {
        float[] nudges = RequireArray(saves, AttributeMetadata.Count, $"player {playerId} ceiling nudges");
        for (int index = 0; index < nudges.Length; index++)
        {
            float nudge = nudges[index];
            if (!float.IsFinite(nudge) || nudge < 0f || nudge > WorldPlayer.MaxCareerCeilingNudge)
            {
                throw new InvalidDataException(
                    $"Player {playerId} has ceiling nudge {nudge} at index {index} outside " +
                    $"0..{WorldPlayer.MaxCareerCeilingNudge}.");
            }

            if (nudge <= 0f) continue;
            player.RecordCeilingNudge(AttributeMetadata.All[index], nudge);
        }
    }

    /// <summary>Replays each club's squad order and manager-trust table after the players are registered.</summary>
    private static void RestoreClubContent(WorldState world, List<WorldClubSave>? saves, HashSet<int> playerIds)
    {
        if (saves is null) throw new InvalidDataException("The world snapshot has no clubs.");

        for (int index = 0; index < saves.Count; index++)
        {
            WorldClubSave save = saves[index] ?? throw new InvalidDataException("The save has a null club entry.");
            WorldClub club = world.Clubs[new ClubId(save.Id)];

            int[] squad = RequireArray(save.Squad, -1, $"club {save.Id} squad");
            var squadIds = new HashSet<int>();
            for (int playerIndex = 0; playerIndex < squad.Length; playerIndex++)
            {
                int playerId = squad[playerIndex];
                RequireKnownPlayer(playerIds, playerId, $"Club {save.Id} squad");
                if (!squadIds.Add(playerId))
                {
                    throw new InvalidDataException($"Club {save.Id} lists player {playerId} twice in its squad.");
                }

                club.AddPlayer(new PlayerId(playerId));
            }

            List<WorldManagerTrustSave>? trust = save.ManagerTrust;
            if (trust is null) throw new InvalidDataException($"Club {save.Id} has no manager-trust table.");
            var trustIds = new HashSet<int>();
            for (int trustIndex = 0; trustIndex < trust.Count; trustIndex++)
            {
                WorldManagerTrustSave entry = trust[trustIndex]
                    ?? throw new InvalidDataException($"Club {save.Id} has a null manager-trust entry.");
                RequireKnownPlayer(playerIds, entry.PlayerId, $"Club {save.Id} manager trust");
                if (!trustIds.Add(entry.PlayerId))
                {
                    throw new InvalidDataException(
                        $"Club {save.Id} has two manager-trust values for player {entry.PlayerId}.");
                }

                RequireRange(entry.Trust, 0f, 1f, $"club {save.Id} trust");
                club.SetManagerTrust(new PlayerId(entry.PlayerId), entry.Trust);
            }

            // The live trust table always has exactly one entry per squad player (AddPlayer/RemovePlayer keep
            // them in step), so a differing table means the snapshot is not a world this build produced.
            if (!squadIds.SetEquals(trustIds))
            {
                throw new InvalidDataException(
                    $"Club {save.Id} has {squadIds.Count} squad players but {trustIds.Count} manager-trust entries.");
            }
        }
    }

    /// <summary>Registers each league's club list and fixtures (with their recorded results).</summary>
    private static void RestoreLeagueContent(
        WorldState world, List<WorldLeagueSave>? saves, HashSet<int> clubIds, HashSet<int> matchIds)
    {
        if (saves is null) throw new InvalidDataException("The world snapshot has no leagues.");

        for (int index = 0; index < saves.Count; index++)
        {
            WorldLeagueSave save = saves[index] ?? throw new InvalidDataException("The save has a null league entry.");
            if (index >= world.Leagues.Count || world.Leagues[index].Id.Value != save.Id)
            {
                throw new InvalidDataException(
                    $"The save's league order does not match the restored registry at position {index}.");
            }

            WorldLeague league = world.Leagues[index];
            int[] clubIdsForLeague = RequireArray(save.ClubIds, -1, $"league {save.Id} clubs");
            var leagueClubs = new HashSet<int>();
            for (int clubIndex = 0; clubIndex < clubIdsForLeague.Length; clubIndex++)
            {
                int clubId = clubIdsForLeague[clubIndex];
                RequireKnownClub(clubIds, clubId, $"League {save.Id}");
                if (!leagueClubs.Add(clubId))
                {
                    throw new InvalidDataException($"League {save.Id} lists club {clubId} twice.");
                }

                league.AddClub(new ClubId(clubId));
            }

            if (save.Fixtures is null) throw new InvalidDataException($"League {save.Id} has no fixture list.");
            for (int fixtureIndex = 0; fixtureIndex < save.Fixtures.Count; fixtureIndex++)
            {
                WorldFixture fixture = RestoreFixture(
                    world,
                    save.Fixtures[fixtureIndex],
                    clubIds,
                    matchIds,
                    $"League {save.Id}");
                if (fixture.Competition != CompetitionKind.League || fixture.LeagueId != league.Id)
                {
                    throw new InvalidDataException(
                        $"League {save.Id} contains fixture {fixture.Id}, which belongs to another competition.");
                }

                league.AddFixture(fixture);
            }
        }
    }

    /// <summary>Rebuilds the cup bracket round by round, replaying results so each next round is legal.</summary>
    private static void RestoreCup(
        WorldState world, WorldCupSave? save, HashSet<int> clubIds, HashSet<int> matchIds)
    {
        if (save is null) throw new InvalidDataException("The world snapshot has no cup.");
        if (save.Season < 1)
        {
            throw new InvalidDataException($"The cup has season {save.Season}, which is not one-based.");
        }

        if (save.Rounds is null) throw new InvalidDataException("The cup has no round list.");

        world.Cup.Reset(save.Season);

        CupRound previousRound = CupRound.None;
        for (int roundIndex = 0; roundIndex < save.Rounds.Count; roundIndex++)
        {
            WorldCupRoundSave roundSave = save.Rounds[roundIndex]
                ?? throw new InvalidDataException("The save has a null cup round.");
            RequireEnum<CupRound>(roundSave.Round, "cup round");
            var round = (CupRound)roundSave.Round;
            if (round == CupRound.None || round <= previousRound)
            {
                throw new InvalidDataException(
                    $"The cup round {round} is out of order at position {roundIndex}.");
            }

            if (roundSave.Week is < 1 or > 52)
            {
                throw new InvalidDataException($"Cup round {round} has week {roundSave.Week} outside 1..52.");
            }

            CupRoundBracket bracket = world.Cup.AddRound(round, roundSave.Week);

            if (roundSave.Ties is null) throw new InvalidDataException($"Cup round {round} has no tie list.");
            for (int tieIndex = 0; tieIndex < roundSave.Ties.Count; tieIndex++)
            {
                WorldFixture fixture = RestoreFixture(
                    world,
                    roundSave.Ties[tieIndex],
                    clubIds,
                    matchIds,
                    $"Cup round {round}");
                if (!fixture.IsCupTie || fixture.CupRound != round)
                {
                    throw new InvalidDataException(
                        $"Cup round {round} contains fixture {fixture.Id}, which belongs to another round.");
                }

                bracket.AddTie(new CupTie(fixture));
            }

            int[] byes = RequireArray(roundSave.Byes, -1, $"cup round {round} byes");
            var byeIds = new HashSet<int>();
            for (int byeIndex = 0; byeIndex < byes.Length; byeIndex++)
            {
                int clubId = byes[byeIndex];
                RequireKnownClub(clubIds, clubId, $"Cup round {round}");
                if (!byeIds.Add(clubId))
                {
                    throw new InvalidDataException($"Cup round {round} lists club {clubId} twice as a bye.");
                }

                bracket.AddBye(new ClubId(clubId));
            }

            previousRound = round;
        }

        if (save.Complete)
        {
            if (save.ChampionClubId <= 0)
            {
                throw new InvalidDataException("The cup is complete but has no champion.");
            }

            RequireKnownClub(clubIds, save.ChampionClubId, "Cup champion");
            world.Cup.MarkComplete(new ClubId(save.ChampionClubId));
        }
        else if (save.ChampionClubId != 0)
        {
            throw new InvalidDataException("The cup has a champion but is not complete.");
        }
    }

    /// <summary>
    /// Rebuilds one fixture and replays its recorded result. A fixture must reference real clubs and, when
    /// played, carry a scoreline the fixture type allows.
    /// </summary>
    private static WorldFixture RestoreFixture(
        WorldState world,
        WorldFixtureSave? save,
        HashSet<int> clubIds,
        HashSet<int> matchIds,
        string context)
    {
        if (save is null) throw new InvalidDataException($"{context} has a null fixture entry.");
        if (save.Id <= 0) throw new InvalidDataException($"{context} has a fixture with invalid id {save.Id}.");
        if (!matchIds.Add(save.Id)) throw new InvalidDataException($"The save has a duplicate fixture id {save.Id}.");
        RequireEnum<CompetitionKind>(save.Competition, "competition");
        RequireEnum<CupRound>(save.CupRound, "cup round");
        if (save.Season < 1) throw new InvalidDataException($"Fixture {save.Id} has season {save.Season}.");
        if (save.Week is < 1 or > 52)
        {
            throw new InvalidDataException($"Fixture {save.Id} has week {save.Week} outside 1..52.");
        }

        if (save.HomeClubId <= 0 || save.AwayClubId <= 0)
        {
            throw new InvalidDataException(
                $"Fixture {save.Id} references a sentinel club (home {save.HomeClubId}, away {save.AwayClubId}).");
        }

        if (save.HomeClubId == save.AwayClubId)
        {
            throw new InvalidDataException($"Fixture {save.Id} has the same club on both sides.");
        }

        RequireKnownClub(clubIds, save.HomeClubId, $"Fixture {save.Id}");
        RequireKnownClub(clubIds, save.AwayClubId, $"Fixture {save.Id}");

        var competition = (CompetitionKind)save.Competition;
        if (competition == CompetitionKind.League)
        {
            if (save.LeagueId <= 0) throw new InvalidDataException($"League fixture {save.Id} has no league id.");
            RequireLeague(world, save.LeagueId, $"Fixture {save.Id}");
            if (save.LeagueMatchday is < 1 or > WorldCalendar.LeagueMatchdaysPerSeason)
            {
                throw new InvalidDataException(
                    $"League fixture {save.Id} has matchday {save.LeagueMatchday} outside 1.." +
                    $"{WorldCalendar.LeagueMatchdaysPerSeason}.");
            }

            if (save.CupRound != (int)CupRound.None)
            {
                throw new InvalidDataException($"League fixture {save.Id} carries cup round {save.CupRound}.");
            }
        }
        else
        {
            if (save.CupRound == (int)CupRound.None)
            {
                throw new InvalidDataException($"Cup fixture {save.Id} has no cup round.");
            }

            if (save.LeagueId != 0 || save.LeagueMatchday != 0)
            {
                throw new InvalidDataException($"Cup fixture {save.Id} carries league metadata.");
            }
        }

        var fixture = new WorldFixture(
            new MatchId(save.Id),
            save.Season,
            save.Week,
            competition,
            new LeagueId(save.LeagueId),
            save.LeagueMatchday,
            (CupRound)save.CupRound,
            new ClubId(save.HomeClubId),
            new ClubId(save.AwayClubId));

        if (!save.Played)
        {
            if (save.HomeGoals != 0 || save.AwayGoals != 0 ||
                save.HomePenaltyGoals != 0 || save.AwayPenaltyGoals != 0)
            {
                throw new InvalidDataException($"Fixture {save.Id} is unplayed but carries a result.");
            }

            return fixture;
        }

        RequireRange(save.HomeGoals, 0, 30, $"fixture {save.Id} home goals");
        RequireRange(save.AwayGoals, 0, 30, $"fixture {save.Id} away goals");
        if (competition == CompetitionKind.League)
        {
            if (save.HomePenaltyGoals != 0 || save.AwayPenaltyGoals != 0)
            {
                throw new InvalidDataException($"League fixture {save.Id} carries a penalty shoot-out.");
            }

            fixture.RecordLeagueResult(save.HomeGoals, save.AwayGoals);
            return fixture;
        }

        RequireNonNegative(save.HomePenaltyGoals, $"fixture {save.Id} home penalty goals");
        RequireNonNegative(save.AwayPenaltyGoals, $"fixture {save.Id} away penalty goals");
        if (save.HomeGoals == save.AwayGoals)
        {
            if (save.HomePenaltyGoals == save.AwayPenaltyGoals)
            {
                throw new InvalidDataException($"Cup fixture {save.Id} is level without a decisive shoot-out.");
            }
        }
        else if (save.HomePenaltyGoals != 0 || save.AwayPenaltyGoals != 0)
        {
            throw new InvalidDataException($"Cup fixture {save.Id} was decided in normal time but carries a shoot-out.");
        }

        fixture.RecordCupResult(
            save.HomeGoals, save.AwayGoals, save.HomePenaltyGoals, save.AwayPenaltyGoals);
        return fixture;
    }

    /// <summary>Replays the append-only transfer log in order.</summary>
    private static void RestoreTransferLog(
        WorldState world, List<WorldTransferSave>? saves, HashSet<int> playerIds, HashSet<int> clubIds)
    {
        if (saves is null) throw new InvalidDataException("The world snapshot has no transfer log.");

        for (int index = 0; index < saves.Count; index++)
        {
            WorldTransferSave save = saves[index]
                ?? throw new InvalidDataException("The save has a null transfer-log entry.");
            RequireEnum<TransferKind>(save.Kind, "transfer kind");
            if (save.Season < 1)
            {
                throw new InvalidDataException($"Transfer-log entry {index} has season {save.Season}.");
            }

            if (save.Week is < 1 or > 52)
            {
                throw new InvalidDataException($"Transfer-log entry {index} has week {save.Week} outside 1..52.");
            }

            RequireKnownPlayer(playerIds, save.PlayerId, $"Transfer-log entry {index}");
            if (save.FromClubId != 0) RequireKnownClub(clubIds, save.FromClubId, $"Transfer-log entry {index}");
            if (save.ToClubId != 0) RequireKnownClub(clubIds, save.ToClubId, $"Transfer-log entry {index}");
            if (save.Fee < 0L) throw new InvalidDataException($"Transfer-log entry {index} has a negative fee.");

            world.RecordTransfer(new TransferRecord(
                save.Season,
                save.Week,
                new PlayerId(save.PlayerId),
                new ClubId(save.FromClubId),
                new ClubId(save.ToClubId),
                (TransferKind)save.Kind,
                save.Fee));
        }
    }

    // =============================================================================================
    // Validation helpers.
    // =============================================================================================

    /// <summary>Validates ids are one-based and unique, and returns them as a set for reference checks.</summary>
    private static HashSet<int> CollectIds<T>(List<T>? entries, Func<T, int> idOf, string what)
        where T : class
    {
        if (entries is null) throw new InvalidDataException($"The world snapshot is missing its {what} list.");

        var ids = new HashSet<int>();
        for (int index = 0; index < entries.Count; index++)
        {
            T entry = entries[index]
                ?? throw new InvalidDataException($"The save has a null {what} entry at position {index}.");
            int id = idOf(entry);
            if (id <= 0) throw new InvalidDataException($"The save has an invalid {what} id {id}.");
            if (!ids.Add(id)) throw new InvalidDataException($"The save has a duplicate {what} id {id}.");
        }

        return ids;
    }

    /// <summary>Throws when an enum-typed save value is not a defined member.</summary>
    private static void RequireEnum<TEnum>(int value, string what)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(typeof(TEnum), value))
        {
            throw new InvalidDataException($"The save has an unknown {what} value {value}.");
        }
    }

    /// <summary>Throws when a reference names a club the save does not contain.</summary>
    private static void RequireKnownClub(HashSet<int> clubIds, int clubId, string what)
    {
        if (clubId <= 0 || !clubIds.Contains(clubId))
        {
            throw new InvalidDataException($"{what} references club {clubId}, which is not in the save.");
        }
    }

    /// <summary>Throws when a reference names a player the save does not contain.</summary>
    private static void RequireKnownPlayer(HashSet<int> playerIds, int playerId, string what)
    {
        if (playerId <= 0 || !playerIds.Contains(playerId))
        {
            throw new InvalidDataException($"{what} references player {playerId}, which is not in the save.");
        }
    }

    /// <summary>Throws when a reference names a manager the save does not contain.</summary>
    private static void RequireKnownManager(HashSet<int> managerIds, int managerId, string what)
    {
        if (managerId <= 0 || !managerIds.Contains(managerId))
        {
            throw new InvalidDataException($"{what} references manager {managerId}, which is not in the save.");
        }
    }

    /// <summary>Throws when a league reference does not resolve to a registered league.</summary>
    private static void RequireLeague(WorldState world, int leagueId, string what)
    {
        IReadOnlyList<WorldLeague> leagues = world.Leagues;
        for (int index = 0; index < leagues.Count; index++)
        {
            if (leagues[index].Id.Value == leagueId) return;
        }

        throw new InvalidDataException($"{what} references league {leagueId}, which is not in the save.");
    }

    /// <summary>Throws when an id counter is below the highest id it has already handed out.</summary>
    private static void RequireCounterCovers(int counter, HashSet<int> ids, string what)
    {
        int maximum = 0;
        foreach (int id in ids)
        {
            if (id > maximum) maximum = id;
        }

        if (counter < maximum)
        {
            throw new InvalidDataException(
                $"The save's last {what} id ({counter}) is below the highest saved id ({maximum}).");
        }
    }

    /// <summary>Throws when a required fixed-size array is missing or the wrong length.</summary>
    private static float[] RequireArray(float[]? values, int expectedLength, string what)
    {
        if (values is null) throw new InvalidDataException($"The save is missing {what}.");
        if (expectedLength >= 0 && values.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"The save's {what} has {values.Length} values; expected {expectedLength}.");
        }

        return values;
    }

    /// <summary>Throws when a required integer array is missing.</summary>
    private static int[] RequireArray(int[]? values, int expectedLength, string what)
    {
        if (values is null) throw new InvalidDataException($"The save is missing {what}.");
        if (expectedLength >= 0 && values.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"The save's {what} has {values.Length} values; expected {expectedLength}.");
        }

        return values;
    }

    /// <summary>Reads one latent/personality trait, validating it is a finite 0..100 value.</summary>
    private static float RequireTrait(float[]? values, int index, string what)
    {
        if (values is null || values.Length != 4)
        {
            throw new InvalidDataException($"The save is missing the {what} block.");
        }

        float value = values[index];
        if (!float.IsFinite(value) || value is < 0f or > 100f)
        {
            throw new InvalidDataException($"The save's {what} is {value}, outside 0..100.");
        }

        return value;
    }

    /// <summary>Throws when a float is outside an inclusive range (the range setters would silently clamp).</summary>
    private static void RequireRange(float value, float minimum, float maximum, string what)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"The save's {what} is {value}, outside {minimum}..{maximum}.");
        }
    }

    /// <summary>Throws when an integer is outside an inclusive range.</summary>
    private static void RequireRange(int value, int minimum, int maximum, string what)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"The save's {what} is {value}, outside {minimum}..{maximum}.");
        }
    }

    /// <summary>Throws when a float is not finite.</summary>
    private static void RequireFinite(float value, string what)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException($"The save's {what} is not a finite number.");
    }

    /// <summary>Throws when a counter is negative.</summary>
    private static void RequireNonNegative(int value, string what)
    {
        if (value < 0) throw new InvalidDataException($"The save's {what} is negative ({value}).");
    }
}

// =================================================================================================
// Snapshot DTOs. Every member is a primitive, string, array or list of other DTOs so that
// System.Text.Json can round-trip the graph without converters, and the JSON stays self-describing.
// =================================================================================================

/// <summary>The world half of a save file: every mutable piece of <see cref="WorldState"/>.</summary>
internal sealed class WorldSaveData
{
    /// <summary>Root world seed the streams and generation seeds derive from.</summary>
    public ulong Seed { get; set; }

    /// <summary>Current season (one-based).</summary>
    public int Season { get; set; }

    /// <summary>Current calendar week (1..52).</summary>
    public int Week { get; set; }

    /// <summary>Last allocated player id.</summary>
    public int LastPlayerId { get; set; }

    /// <summary>Last allocated match id.</summary>
    public int LastMatchId { get; set; }

    /// <summary>Last allocated manager id.</summary>
    public int LastManagerId { get; set; }

    /// <summary>Every created random stream, sorted by stream value.</summary>
    public List<WorldRngEntrySave> RngStates { get; set; } = new();

    /// <summary>Players in registry (registration) order.</summary>
    public List<WorldPlayerSave> Players { get; set; } = new();

    /// <summary>Clubs in registry order.</summary>
    public List<WorldClubSave> Clubs { get; set; } = new();

    /// <summary>Managers in registry order.</summary>
    public List<WorldManagerSave> Managers { get; set; } = new();

    /// <summary>Leagues in ascending-tier registration order.</summary>
    public List<WorldLeagueSave> Leagues { get; set; } = new();

    /// <summary>The domestic cup's state.</summary>
    public WorldCupSave Cup { get; set; } = new();

    /// <summary>The append-only transfer log, oldest first.</summary>
    public List<WorldTransferSave> Transfers { get; set; } = new();
}

/// <summary>One named random stream's position.</summary>
internal sealed class WorldRngEntrySave
{
    public int Stream { get; set; }
    public ulong State { get; set; }
}

/// <summary>One player's complete mutable state.</summary>
internal sealed class WorldPlayerSave
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }
    public int BirthWeek { get; set; }

    /// <summary><see cref="Footedness"/> ordinal.</summary>
    public int PreferredFoot { get; set; }

    public float WeakFoot { get; set; }

    /// <summary><see cref="Players.BackstoryKind"/> ordinal.</summary>
    public int Backstory { get; set; }

    /// <summary>Employer club, or 0 for a free agent.</summary>
    public int ClubId { get; set; }

    public int SuspensionWeeks { get; set; }
    public float Condition { get; set; }
    public float Sharpness { get; set; }
    public float Form { get; set; }
    public float Visibility { get; set; }

    /// <summary><see cref="Players.TrainingFocus"/> ordinal.</summary>
    public int TrainingFocus { get; set; }

    public bool IsRetired { get; set; }

    /// <summary>Eighteen current values in <see cref="AttributeId"/> order.</summary>
    public float[] Current { get; set; } = Array.Empty<float>();

    /// <summary>Eighteen hidden ceilings in <see cref="AttributeId"/> order.</summary>
    public float[] Ceiling { get; set; } = Array.Empty<float>();

    /// <summary>Eighteen plasticity values in <see cref="AttributeId"/> order.</summary>
    public float[] Plasticity { get; set; } = Array.Empty<float>();

    /// <summary>Eighteen lifetime ceiling nudges in <see cref="AttributeId"/> order.</summary>
    public float[] CeilingNudges { get; set; } = Array.Empty<float>();

    /// <summary>Athleticism, technical, mental, goalkeeping.</summary>
    public float[] Latent { get; set; } = Array.Empty<float>();

    /// <summary>Resilience, work ethic, creativity, discipline.</summary>
    public float[] Personality { get; set; } = Array.Empty<float>();

    public WorldInjurySave Injury { get; set; } = new();
    public WorldContractSave Contract { get; set; } = new();

    /// <summary>Bounded appearance window, oldest first.</summary>
    public List<WorldAppearanceSave> RecentForm { get; set; } = new();

    /// <summary>Up to thirteen weekly snapshots of eighteen current values each, oldest first.</summary>
    public List<float[]> Trends { get; set; } = new();

    public WorldSeasonStatsSave CurrentSeason { get; set; } = new();

    /// <summary>Archived seasons, oldest first.</summary>
    public List<WorldSeasonStatsSave> SeasonHistory { get; set; } = new();
}

/// <summary>One player's injury state.</summary>
internal sealed class WorldInjurySave
{
    /// <summary><see cref="Players.InjuryKind"/> ordinal.</summary>
    public int Kind { get; set; }

    /// <summary><see cref="Players.InjurySeverity"/> ordinal.</summary>
    public int Severity { get; set; }

    public int WeeksOutRemaining { get; set; }
    public int TotalWeeksOut { get; set; }
    public int PermanentDamageApplied { get; set; }
}

/// <summary>One player's contract terms.</summary>
internal sealed class WorldContractSave
{
    public int SignedSeason { get; set; }
    public int EndSeason { get; set; }
    public long WeeklyWage { get; set; }
    public bool IsOnLoan { get; set; }

    /// <summary>Parent club while on loan, or 0.</summary>
    public int ParentClubId { get; set; }

    public int LoanEndSeason { get; set; }
}

/// <summary>One recorded appearance in the recent-form window.</summary>
internal sealed class WorldAppearanceSave
{
    public int Season { get; set; }
    public int Week { get; set; }
    public int Minutes { get; set; }
    public float Rating { get; set; }
}

/// <summary>One season's counting stats (live or archived).</summary>
internal sealed class WorldSeasonStatsSave
{
    public int Season { get; set; }
    public int Appearances { get; set; }
    public int Starts { get; set; }
    public int SubstituteAppearances { get; set; }
    public int MinutesPlayed { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int Shots { get; set; }
    public int PassesAttempted { get; set; }
    public int PassesCompleted { get; set; }
    public int Tackles { get; set; }
    public int Saves { get; set; }
    public int YellowCards { get; set; }
    public int RedCards { get; set; }
    public float RatingSum { get; set; }
}

/// <summary>One club's complete mutable state.</summary>
internal sealed class WorldClubSave
{
    public int Id { get; set; }

    /// <summary>Club name, which resolves the club's immutable data-pack definition.</summary>
    public string Name { get; set; } = string.Empty;

    public int LeagueId { get; set; }
    public int Tier { get; set; }
    public int Reputation { get; set; }
    public int ManagerId { get; set; }
    public long Balance { get; set; }
    public long WeeklyWageBudget { get; set; }
    public long TransferBudget { get; set; }
    public long WeeklyWageBill { get; set; }
    public float CoachingQuality { get; set; }
    public float MedicalQuality { get; set; }
    public float YouthQuality { get; set; }
    public int WeeklyTrainingSessions { get; set; }

    /// <summary>Squad in selection order.</summary>
    public int[] Squad { get; set; } = Array.Empty<int>();

    /// <summary>Manager trust per squad player, sorted by player id.</summary>
    public List<WorldManagerTrustSave> ManagerTrust { get; set; } = new();
}

/// <summary>One manager-trust table entry.</summary>
internal sealed class WorldManagerTrustSave
{
    public int PlayerId { get; set; }
    public float Trust { get; set; }
}

/// <summary>One manager's complete mutable state.</summary>
internal sealed class WorldManagerSave
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }
    public int ClubId { get; set; }

    /// <summary><see cref="FormationKind"/> ordinal.</summary>
    public int Formation { get; set; }

    public int Reputation { get; set; }
    public float BoardConfidence { get; set; }
    public int SeasonsAtClub { get; set; }
    public int MatchesInCharge { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public WorldTacticalProfileSave TacticalProfile { get; set; } = new();
}

/// <summary>The eleven tactical values plus the profile name, which identifies a shared preset.</summary>
internal sealed class WorldTacticalProfileSave
{
    public string Name { get; set; } = string.Empty;
    public float Directness { get; set; }
    public float Tempo { get; set; }
    public float AttackingWidth { get; set; }
    public float DefensiveLineHeight { get; set; }
    public float Compactness { get; set; }
    public float PressingIntensity { get; set; }
    public float RiskTolerance { get; set; }
    public float CounterPressDuration { get; set; }
    public float PositionalFreedom { get; set; }
    public bool ShortGoalkeeperDistribution { get; set; }
    public float ManOrientation { get; set; }
}

/// <summary>One league: its ordered clubs and this season's fixtures.</summary>
internal sealed class WorldLeagueSave
{
    public int Id { get; set; }
    public int Tier { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public int[] ClubIds { get; set; } = Array.Empty<int>();
    public List<WorldFixtureSave> Fixtures { get; set; } = new();
}

/// <summary>One fixture's immutable identity and recorded result.</summary>
internal sealed class WorldFixtureSave
{
    public int Id { get; set; }
    public int Season { get; set; }
    public int Week { get; set; }

    /// <summary><see cref="CompetitionKind"/> ordinal.</summary>
    public int Competition { get; set; }

    public int LeagueId { get; set; }
    public int LeagueMatchday { get; set; }

    /// <summary><see cref="CupRound"/> ordinal.</summary>
    public int CupRound { get; set; }

    public int HomeClubId { get; set; }
    public int AwayClubId { get; set; }
    public bool Played { get; set; }
    public int HomeGoals { get; set; }
    public int AwayGoals { get; set; }
    public int HomePenaltyGoals { get; set; }
    public int AwayPenaltyGoals { get; set; }
}

/// <summary>The cup's season state: its ordered rounds, completion flag and champion.</summary>
internal sealed class WorldCupSave
{
    public int Season { get; set; }
    public bool Complete { get; set; }
    public int ChampionClubId { get; set; }
    public List<WorldCupRoundSave> Rounds { get; set; } = new();
}

/// <summary>One cup round: its ties (fixtures with results) and byes.</summary>
internal sealed class WorldCupRoundSave
{
    /// <summary><see cref="CupRound"/> ordinal.</summary>
    public int Round { get; set; }

    public int Week { get; set; }
    public List<WorldFixtureSave> Ties { get; set; } = new();
    public int[] Byes { get; set; } = Array.Empty<int>();
}

/// <summary>One append-only transfer-log entry.</summary>
internal sealed class WorldTransferSave
{
    public int Season { get; set; }
    public int Week { get; set; }
    public int PlayerId { get; set; }
    public int FromClubId { get; set; }
    public int ToClubId { get; set; }

    /// <summary><see cref="TransferKind"/> ordinal.</summary>
    public int Kind { get; set; }

    public long Fee { get; set; }
}
