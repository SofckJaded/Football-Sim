using System;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.5 — the four error sources, separated.
///
/// The model is pure, so these pin it directly: the default athlete at full stamina is exactly neutral (the
/// pre-A7.5 build), each shared cause moves exactly one number where it should, and the evaluation draw is
/// bounded, deterministic and independent of the order candidates were scored in.
/// </summary>
public sealed class PlayerCognitiveStateTests
{
    [Fact]
    public void TheDefaultAthleteAtFullStamina_IsExactlyNeutral()
    {
        var attributes = new PlayerAttributes();
        CognitiveState state = PlayerCognitiveState.Compute(attributes, stamina01: 1f);

        Assert.Equal(1f, state.ObservationNoiseScale);
        Assert.Equal(0f, state.EvaluationError01);
        Assert.Equal(attributes.ReactionDelaySeconds, state.ReactionDelaySeconds);
        Assert.True(state.IsNeutral);
    }

    [Fact]
    public void Fatigue_RaisesTheNoiseTheMisjudgementAndTheDelay()
    {
        var attributes = new PlayerAttributes();
        CognitiveState fresh = PlayerCognitiveState.Compute(attributes, stamina01: 1f);
        CognitiveState tired = PlayerCognitiveState.Compute(attributes, stamina01: 0.3f);
        CognitiveState exhausted = PlayerCognitiveState.Compute(attributes, stamina01: 0f);

        Assert.True(tired.ObservationNoiseScale > fresh.ObservationNoiseScale);
        Assert.True(exhausted.ObservationNoiseScale > tired.ObservationNoiseScale);
        Assert.True(tired.EvaluationError01 > 0f);
        Assert.True(exhausted.EvaluationError01 > tired.EvaluationError01);
        Assert.True(tired.ReactionDelaySeconds > attributes.ReactionDelaySeconds);
        Assert.True(exhausted.ReactionDelaySeconds > tired.ReactionDelaySeconds);

        // The numbers stay usable: a scale below 2, an error below 1, and a delay the perception history
        // can still represent (20 ticks = 0.333 s).
        Assert.True(exhausted.ObservationNoiseScale < 2f);
        Assert.True(exhausted.EvaluationError01 <= 1f);
        Assert.True(exhausted.ReactionDelaySeconds < 0.33f);
    }

    [Fact]
    public void LowComposure_RaisesOnlyTheEvaluationError()
    {
        var calm = new PlayerAttributes { Composure01 = 0.9f };
        var rattled = new PlayerAttributes { Composure01 = 0.4f };

        CognitiveState calmState = PlayerCognitiveState.Compute(calm, stamina01: 1f);
        CognitiveState rattledState = PlayerCognitiveState.Compute(rattled, stamina01: 1f);

        Assert.True(rattledState.EvaluationError01 > calmState.EvaluationError01);
        // Composure is a judgement cause, not a perception one: the picture of the pitch is identical.
        Assert.Equal(calmState.ObservationNoiseScale, rattledState.ObservationNoiseScale);
        Assert.Equal(calmState.ReactionDelaySeconds, rattledState.ReactionDelaySeconds);
    }

    [Fact]
    public void NoPressureNoFatigue_CostsTheFirstTouchNothing()
    {
        var attributes = new PlayerAttributes();
        Assert.Equal(0f, PlayerCognitiveState.ExecutionError01(attributes, stamina01: 1f, pressure01: 0f));

        float light = PlayerCognitiveState.ExecutionError01(attributes, 1f, 0.3f);
        float heavy = PlayerCognitiveState.ExecutionError01(attributes, 1f, 0.9f);
        Assert.True(light > 0f);
        Assert.True(heavy > light);

        float tired = PlayerCognitiveState.ExecutionError01(attributes, 0.3f, 0f);
        Assert.True(tired > 0f, "fatigue alone must cost the first touch something");

        // Composure is the relief valve, not a second cause: a composed player under the same pressure
        // loses less than a rattled one.
        var composed = new PlayerAttributes { Composure01 = 0.95f };
        var rattled = new PlayerAttributes { Composure01 = 0.35f };
        Assert.True(PlayerCognitiveState.ExecutionError01(composed, 1f, 0.9f) <
                    PlayerCognitiveState.ExecutionError01(rattled, 1f, 0.9f));
        Assert.True(PlayerCognitiveState.ExecutionError01(rattled, 0f, 1f) <= 1f);
    }

    [Fact]
    public void TheEvaluationBias_IsBounded_AndNamesEachCandidate()
    {
        var key = new CandidateKey(CandidateKind.Pass, TargetPlayerId: 9, TargetTag: "GroundPass");
        var other = new CandidateKey(CandidateKind.Pass, TargetPlayerId: 9, TargetTag: "ThroughPass");

        float bias = PlayerCognitiveState.EvaluationBias(playerId: 7, tick: 120, key);
        Assert.InRange(bias, -1f, 1f);
        Assert.Equal(bias, PlayerCognitiveState.EvaluationBias(playerId: 7, tick: 120, key));

        // A different decision, a different player or a different candidate is a different draw.
        Assert.NotEqual(bias, PlayerCognitiveState.EvaluationBias(playerId: 7, tick: 121, key));
        Assert.NotEqual(bias, PlayerCognitiveState.EvaluationBias(playerId: 8, tick: 120, key));
        Assert.NotEqual(bias, PlayerCognitiveState.EvaluationBias(playerId: 7, tick: 120, other));
    }

    [Fact]
    public void TheEvaluationBias_DoesNotDependOnWhatElseWasScored()
    {
        // The claim behind using a hash instead of a random stream: the draw belongs to the candidate, not
        // to the call order, so adding or removing a candidate cannot shift another one's error.
        var key = new CandidateKey(CandidateKind.Carry, TargetTag: "ahead");
        float alone = PlayerCognitiveState.EvaluationBias(playerId: 3, tick: 50, key);

        Span<CandidateKind> kinds = stackalloc CandidateKind[]
        {
            CandidateKind.Pass, CandidateKind.Shot, CandidateKind.Press, CandidateKind.Job
        };
        foreach (CandidateKind kind in kinds)
            PlayerCognitiveState.EvaluationBias(playerId: 3, tick: 50, new CandidateKey(kind, 5, "x"));

        Assert.Equal(alone, PlayerCognitiveState.EvaluationBias(playerId: 3, tick: 50, key));
    }    [Fact]
    public void TheBias_IsRoughlySymmetricAroundZero()
    {
        // A misjudgement model that leaned one way would be a bias in the strategy, not an error. Over a
        // thousand draws the mean must be close to zero.
        double sum = 0;
        for (int tick = 0; tick < 1000; tick++)
        {
            sum += PlayerCognitiveState.EvaluationBias(4, tick,
                new CandidateKey(CandidateKind.Pass, 9, "GroundPass"));
        }

        double mean = sum / 1000.0;
        Assert.InRange(mean, -0.1, 0.1);
    }

    [Fact]
    public void ANonFiniteOrOutOfRangeStamina_IsClampedNotPropagated()
    {
        var attributes = new PlayerAttributes();
        CognitiveState overfull = PlayerCognitiveState.Compute(attributes, stamina01: 1.5f);
        CognitiveState negative = PlayerCognitiveState.Compute(attributes, stamina01: -0.5f);

        Assert.Equal(1f, overfull.ObservationNoiseScale);
        Assert.Equal(0f, overfull.EvaluationError01);
        Assert.True(negative.EvaluationError01 <= 1f);
        Assert.True(float.IsFinite(negative.ReactionDelaySeconds));
        Assert.Equal(0f, PlayerCognitiveState.ExecutionError01(attributes, 1.5f, 0f));
    }
}
