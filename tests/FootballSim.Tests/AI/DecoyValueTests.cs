using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A5.6 — the pin/decoy value, built on a bounded follow-versus-hold defender response.
///
/// A decoy is the one off-ball path whose value is not the space it reaches but the space it takes a defender
/// away from. These tests pin the model that answers the question, one input at a time: a defender closer to
/// the run is more likely to answer it, a run into a dangerous space is more likely to be answered than one
/// across the halfway line, a side's deepest defender holds more than he steps out, and following only has a
/// value when it frees a team-mate who was actually being marked.
///
/// The last pair is the generator's side of the same contract: a decoy path is offered from the duties that
/// own one, its region is the marker's own ground, and a world with nobody marked produces no decoy at all.
/// </summary>
public sealed class DecoyValueTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int RunnerId = 7;
    private const int MarkedMateId = 9;
    private const int MarkerId = 25;

    // ---- the defender response ----------------------------------------------------------------------

    /// <summary>The closer the run is to a defender, the more of a decision it is for him.</summary>
    [Fact]
    public void ADefenderCloserToTheRun_IsExpectedToFollowIt()
    {
        // The same marked team-mate and marker; only the run's region changes, from tight to far.
        Vector2 tight = new(30f, 0f);
        Vector2 loose = new(38f, 0f);

        DefenderResponse near = DefenderResponseModel.Evaluate(World(markerX: 29f, markerZ: 0f), tight);
        DefenderResponse far = DefenderResponseModel.Evaluate(World(markerX: 29f, markerZ: 0f), loose);

        Assert.True(near.IsPresent, "the marker is a metre from a dangerous space: he has to answer it");
        Assert.True(near.Follow01 > far.Follow01,
            $"a run on top of the marker ({near.Follow01:F2}) must be a harder decision than one far beyond " +
            $"him ({far.Follow01:F2})");
    }

    /// <summary>A run toward the opponent's goal is answered; a run back toward your own is not.</summary>
    [Fact]
    public void ARunIntoADangerousSpace_IsMoreLikelyAnswered()
    {
        // One world, one marker: the only variable is where the run goes. Forward of the marker is a threat;
        // behind it is a run back toward the halfway line that nobody has to chase.
        FootballerObservation world = World(markerX: 12f, markerZ: 20f, mateX: 10f, mateZ: 20f);
        Vector2 dangerous = new(16f, 20f);
        Vector2 harmless = new(4f, 20f);

        DefenderResponse forward = DefenderResponseModel.Evaluate(world, dangerous);
        DefenderResponse backward = DefenderResponseModel.Evaluate(world, harmless);

        Assert.True(forward.IsPresent && backward.IsPresent,
            $"both runs are near the marker: forward {forward.Describe()}, backward {backward.Describe()}");
        Assert.True(forward.Follow01 > backward.Follow01,
            $"a run toward their goal ({forward.Follow01:F2}) must be a bigger decision than one back toward " +
            $"ours ({backward.Follow01:F2})");
    }

    /// <summary>
    /// The last line holds. The same run, the same distance, one defender deeper in the world: the marker in
    /// front of him is no longer the deepest man and the model gives him the full follow.
    /// </summary>
    [Fact]
    public void TheDeepestDefender_HoldsMoreThanHeStepsOut()
    {
        Vector2 region = new(34f, 4f);
        DefenderResponse lastLine = DefenderResponseModel.Evaluate(World(markerX: 33f, markerZ: 4f), region);
        DefenderResponse covered = DefenderResponseModel.Evaluate(
            World(markerX: 33f, markerZ: 4f, deeperDefenderX: 40f), region);

        Assert.Equal(MarkerId, lastLine.DefenderId);
        Assert.Equal(MarkerId, covered.DefenderId);
        Assert.True(covered.Follow01 > lastLine.Follow01,
            $"with a defender behind him ({covered.Follow01:F2}) the marker must commit more than as the " +
            $"last man ({lastLine.Follow01:F2})");
        Assert.Equal(DefenderResponseModel.LastLineHoldScale, lastLine.Follow01 / covered.Follow01, 2);
    }

    /// <summary>Following is only worth something when the man left behind was being marked.</summary>
    [Fact]
    public void ADefenderMarkingNobody_FreesNothing()
    {
        // The same marker and region, but the likely marker is thrown far from the team-mate: nobody marked.
        DefenderResponse marked = DefenderResponseModel.Evaluate(World(markerX: 29f, markerZ: 0f), new Vector2(30f, 0f));
        DefenderResponse unmarked = DefenderResponseModel.Evaluate(
            World(markerX: 29f, markerZ: 0f, mateX: 5f, mateZ: -20f), new Vector2(30f, 0f));

        Assert.True(marked.FreedSpace01 > 0.5f, $"a marker a metre from his man frees him: {marked.FreedSpace01:F2}");
        Assert.True(unmarked.FreedSpace01 < marked.FreedSpace01,
            $"a defender marking nobody frees less ({unmarked.FreedSpace01:F2}) than one on his man " +
            $"({marked.FreedSpace01:F2})");
    }

    /// <summary>A world with no opponents answers nothing, and a nonsense region does not throw.</summary>
    [Fact]
    public void NoOpponent_MeansNoResponse()
    {
        FootballerObservation empty = World(markerX: 0f, markerZ: 0f, withMarker: false);
        Assert.False(DefenderResponseModel.Evaluate(empty, new Vector2(30f, 0f)).IsPresent);

        FootballerObservation world = World(markerX: 29f, markerZ: 0f);
        Assert.False(DefenderResponseModel.Evaluate(world, new Vector2(float.NaN, 0f)).IsPresent);
        Assert.Equal(
            DefenderResponseModel.Evaluate(world, new Vector2(30f, 0f)),
            DefenderResponseModel.Evaluate(world, new Vector2(30f, 0f)));
    }

    // ---- the path -----------------------------------------------------------------------------------

    /// <summary>
    /// A decoy is offered from the duty that owns one, and it runs into the marker's own ground: goal-side of
    /// him, which is the space he must cover to stay with his man.
    /// </summary>
    [Fact]
    public void ADecoyPath_IsOfferedAndAimedAtTheMarkersGround()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = WideObservation();

        generator.Generate(obs, offsideLineProjX: 44f, obs.Tick, maxPaths: 4);

        RunPath decoy = PathOfKind(generator, RunKind.Decoy);
        RunScoreTerms terms = TermsOfKind(generator, RunKind.Decoy);

        Assert.Equal(RunKind.Decoy, decoy.Kind);

        // The marker stands at (29, 2) with his man at (28, 2): the run goes past him, not at him.
        Assert.True(decoy.MeetXZ.X > 29f,
            $"the decoy runs to x={decoy.MeetXZ.X:F1}, not past the marker at 29.0");
        Assert.True(MathF.Abs(decoy.MeetXZ.Y - 2f) < 6f);

        // And it is priced on the response, not on the receiving terms: it is the only kind that does.
        Assert.True(terms.IsDecoy);
        Assert.Equal(MarkerId, terms.DecoyDefenderId);
        Assert.True(terms.DecoySpace01 > 0f, "the run has to free somebody to be worth anything");
        Assert.True(terms.Score > 0f, $"a decoy that pulls a marker off a man is a positive path: {terms.Score:F2}");
    }

    /// <summary>Nobody is marked, so there is no decoy to run: the path is not offered at all.</summary>
    [Fact]
    public void WithNobodyMarked_ThereIsNoDecoy()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = WideObservation(mateX: 5f, mateZ: -20f, markerX: 30f, markerZ: 20f);

        generator.Generate(obs, offsideLineProjX: 44f, obs.Tick, maxPaths: 4);

        for (int i = 0; i < generator.Count; i++)
            Assert.NotEqual(RunKind.Decoy, generator.PathAt(i).Kind);
    }

    /// <summary>The pin and the decoy are different bets: a pin holds a man, a decoy moves one.</summary>
    [Fact]
    public void KindsByDuty_OfferTheDecoyWhereItBelongsTo()
    {
        Span<RunKind> kinds = stackalloc RunKind[RunCandidateGenerator.MaxKindsPerDuty];

        int wide = RunCandidateGenerator.KindsFor(TacticalJob.FarSideWidth, kinds);
        Assert.Contains(RunKind.Decoy, ToList(kinds, wide));

        int third = RunCandidateGenerator.KindsFor(TacticalJob.ThirdPlayerOption, kinds);
        Assert.Contains(RunKind.Decoy, ToList(kinds, third));

        // The depth run's four paths are all ways of receiving the ball in behind; a decoy is not one.
        int depth = RunCandidateGenerator.KindsFor(TacticalJob.DepthRun, kinds);
        Assert.DoesNotContain(RunKind.Decoy, ToList(kinds, depth));
    }

    // ---- fixtures -----------------------------------------------------------------------------------

    private static List<RunKind> ToList(Span<RunKind> kinds, int count)
    {
        var list = new List<RunKind>(count);
        for (int i = 0; i < count; i++) list.Add(kinds[i]);
        return list;
    }

    /// <summary>
    /// A marked striker and his marker; the runner is a team-mate a few metres away. Only the bodies the two
    /// tests vary are parameters.
    /// </summary>
    private static FootballerObservation World(
        float markerX,
        float markerZ,
        float deeperDefenderX = 0f,
        float mateX = 28f,
        float mateZ = 2f,
        bool withMarker = true)
    {
        var visible = new List<PerceivedEntity>
        {
            Entity(MarkedMateId, TeamId.Home, TacticalRole.Striker, mateX, mateZ)
        };

        if (withMarker) visible.Add(Entity(MarkerId, TeamId.Away, TacticalRole.CenterBack, markerX, markerZ));
        if (deeperDefenderX > 0f)
            visible.Add(Entity(26, TeamId.Away, TacticalRole.CenterBack, deeperDefenderX, -6f));

        return Observation(
            TacticalJob.FarSideWidth,
            TacticalRole.Winger,
            new Vector2(18f, 10f),
            new Vector2(18f, 12f),
            new Vector2(20f, 14f),
            carrierId: 8,
            visible);
    }

    /// <summary>A wide possession with a striker marked by a centre-back and a defender deeper still.</summary>
    private static FootballerObservation WideObservation(
        float mateX = 28f,
        float mateZ = 2f,
        float markerX = 29f,
        float markerZ = 2f) =>
        Observation(
            TacticalJob.FarSideWidth,
            TacticalRole.Winger,
            new Vector2(18f, 10f),
            new Vector2(18f, 12f),
            new Vector2(20f, 14f),
            carrierId: 8,
            new List<PerceivedEntity>
            {
                Entity(8, TeamId.Home, TacticalRole.CentralMidfielder, 20f, 14f),
                Entity(MarkedMateId, TeamId.Home, TacticalRole.Striker, mateX, mateZ),
                Entity(MarkerId, TeamId.Away, TacticalRole.CenterBack, markerX, markerZ),
                Entity(26, TeamId.Away, TacticalRole.CenterBack, 40f, -6f),
                Entity(30, TeamId.Away, TacticalRole.Goalkeeper, 50f, 0f)
            });

    private static PerceivedEntity Entity(int id, TeamId team, TacticalRole role, float x, float z) =>
        new(id, team, role, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static FootballerObservation Observation(
        TacticalJob duty,
        TacticalRole selfRole,
        Vector2 selfXZ,
        Vector2 dutyAnchorXZ,
        Vector2 ballXZ,
        int? carrierId,
        IReadOnlyList<PerceivedEntity> visible) => new()
    {
        SelfId = RunnerId,
        SelfTeam = TeamId.Home,
        SelfRole = selfRole,
        SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = false,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(ballXZ.X, 0.11f, ballXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = carrierId ?? 8,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = carrierId,
        TacticalAnchorPosition = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        TacticalJob = duty,
        TacticalJobAnchor = new Vector3(dutyAnchorXZ.X, 0f, dutyAnchorXZ.Y),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 100,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(selfRole),
        SelfAttributes = new PlayerAttributes(),
        TeamSecurity = new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f)
    };

    private static RunPath PathOfKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
            if (generator.PathAt(i).Kind == kind) return generator.PathAt(i);

        throw new Xunit.Sdk.XunitException($"no {kind} path was offered: {Describe(generator)}");
    }

    private static RunScoreTerms TermsOfKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
            if (generator.PathAt(i).Kind == kind) return generator.TermsAt(i);

        throw new Xunit.Sdk.XunitException($"no {kind} path was offered: {Describe(generator)}");
    }

    private static string Describe(RunCandidateGenerator generator)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < generator.Count; i++)
            text.Append($"[{generator.PathAt(i).Describe()} {generator.TermsAt(i).Describe()}] ");
        return text.ToString();
    }
}
