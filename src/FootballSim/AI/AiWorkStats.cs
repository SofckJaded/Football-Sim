namespace FootballSim.AI;

/// <summary>
/// Cumulative AI work counters for one match (A9.3).
///
/// This is the measurement half of the milestone: the plan asks for candidate counts, search nodes and
/// trajectory samples to be <b>bounded</b>, and a bound nobody measures is a comment. Every counter is
/// incremented beside the work it counts, is read only by evidence probes and tests, and never feeds a
/// decision — the value is deterministic and consumes no simulation randomness, so a match run with the
/// counters is the same match.
///
/// The counters are cumulative and mergeable (<see cref="Add"/>): a controller owns its own instance, so
/// the per-tick decision path never reaches across players to update one shared object, and a probe asks
/// the engine for the merged picture once at the end.
/// </summary>
public sealed class AiWorkStats
{
    // ---- Off-ball candidate generation (A9.3 consumes MaxOffBallCandidates) ------------------------

    /// <summary>Off-ball decisions taken (each one ends in exactly one cap application).</summary>
    public long OffBallDecisions { get; private set; }

    /// <summary>Off-ball candidates generated before the cap was applied.</summary>
    public long OffBallCandidatesGenerated { get; private set; }

    /// <summary>Off-ball candidates kept after the cap was applied.</summary>
    public long OffBallCandidatesKept { get; private set; }

    /// <summary>Largest off-ball set generated in one decision, before the cap.</summary>
    public int MaxOffBallCandidatesGenerated { get; private set; }

    /// <summary>Decisions whose off-ball set was trimmed by <see cref="AiRuntimeConfig.MaxOffBallCandidates"/>.</summary>
    public long OffBallCapTrims { get; private set; }

    // ---- Carrier candidate generation (A4.1's cap, measured for the same sentence) ----------------

    /// <summary>Carrier decisions taken (each one ends in exactly one cap application).</summary>
    public long CarrierDecisions { get; private set; }

    /// <summary>Carrier candidates generated before the cap was applied.</summary>
    public long CarrierCandidatesGenerated { get; private set; }

    /// <summary>Carrier candidates kept after the cap was applied.</summary>
    public long CarrierCandidatesKept { get; private set; }

    /// <summary>Largest carrier set generated in one decision, before the cap.</summary>
    public int MaxCarrierCandidatesGenerated { get; private set; }

    /// <summary>Decisions whose carrier set was trimmed by <see cref="AiRuntimeConfig.MaxCarrierCandidates"/>.</summary>
    public long CarrierCapTrims { get; private set; }

    // ---- A8.2 shallow search ---------------------------------------------------------------------

    /// <summary>First actions the shallow continuation search scored, summed over carrier decisions.</summary>
    public long SearchFirstActions { get; private set; }

    /// <summary>Continuation branches the shallow continuation search scored, summed over carrier decisions.</summary>
    public long SearchContinuations { get; private set; }

    /// <summary>Most continuation branches scored by one search (the A8.2 node budget is 3 × 3).</summary>
    public int MaxSearchContinuationsPerDecision { get; private set; }

    // ---- Trajectory estimates (A2.3/A6.5/A7.3) ---------------------------------------------------

    /// <summary>Ball trajectory estimates built by keepers and reception planners.</summary>
    public long TrajectoryBuilds { get; private set; }

    /// <summary>Trajectory samples produced by those estimates (one physics step each).</summary>
    public long TrajectorySamples { get; private set; }

    /// <summary>Most samples one estimate produced; bounded by <see cref="BallTrajectoryEstimate.MaxSamples"/>.</summary>
    public int MaxTrajectorySamples { get; private set; }

    // ---- Team-level refreshes --------------------------------------------------------------------

    /// <summary>Team-belief map rebuilds driven by the job cadence.</summary>
    public long BeliefRebuilds { get; private set; }

    /// <summary>Every player decision taken (carrier + off-ball).</summary>
    public long Decisions => CarrierDecisions + OffBallDecisions;

    /// <summary>Records one carrier decision after its cap was applied.</summary>
    public void RecordCarrierDecision(int generated, int kept, int cap)
    {
        CarrierDecisions++;
        CarrierCandidatesGenerated += generated;
        CarrierCandidatesKept += kept;
        if (generated > MaxCarrierCandidatesGenerated) MaxCarrierCandidatesGenerated = generated;
        if (generated > cap) CarrierCapTrims++;
    }

    /// <summary>Records one off-ball decision after its cap was applied.</summary>
    public void RecordOffBallDecision(int generated, int kept, int cap)
    {
        OffBallDecisions++;
        OffBallCandidatesGenerated += generated;
        OffBallCandidatesKept += kept;
        if (generated > MaxOffBallCandidatesGenerated) MaxOffBallCandidatesGenerated = generated;
        if (generated > cap) OffBallCapTrims++;
    }

    /// <summary>Records one shallow-search call and the branch counts it evaluated.</summary>
    public void RecordSearch(int firstActions, int continuations)
    {
        SearchFirstActions += firstActions;
        SearchContinuations += continuations;
        if (continuations > MaxSearchContinuationsPerDecision)
            MaxSearchContinuationsPerDecision = continuations;
    }

    /// <summary>Records one trajectory estimate and the number of samples it produced.</summary>
    public void RecordTrajectory(int samples)
    {
        TrajectoryBuilds++;
        TrajectorySamples += samples;
        if (samples > MaxTrajectorySamples) MaxTrajectorySamples = samples;
    }

    /// <summary>Records one team-belief map rebuild.</summary>
    public void RecordBeliefRebuild() => BeliefRebuilds++;

    /// <summary>Adds a batch of team-belief map rebuilds (the engine counts them and merges once).</summary>
    public void RecordBeliefRebuilds(int count) => BeliefRebuilds += count;

    /// <summary>Adds another controller's cumulative counters into this aggregate.</summary>
    public void Add(AiWorkStats other)
    {
        CarrierDecisions += other.CarrierDecisions;
        CarrierCandidatesGenerated += other.CarrierCandidatesGenerated;
        CarrierCandidatesKept += other.CarrierCandidatesKept;
        if (other.MaxCarrierCandidatesGenerated > MaxCarrierCandidatesGenerated)
            MaxCarrierCandidatesGenerated = other.MaxCarrierCandidatesGenerated;
        CarrierCapTrims += other.CarrierCapTrims;

        OffBallDecisions += other.OffBallDecisions;
        OffBallCandidatesGenerated += other.OffBallCandidatesGenerated;
        OffBallCandidatesKept += other.OffBallCandidatesKept;
        if (other.MaxOffBallCandidatesGenerated > MaxOffBallCandidatesGenerated)
            MaxOffBallCandidatesGenerated = other.MaxOffBallCandidatesGenerated;
        OffBallCapTrims += other.OffBallCapTrims;

        SearchFirstActions += other.SearchFirstActions;
        SearchContinuations += other.SearchContinuations;
        if (other.MaxSearchContinuationsPerDecision > MaxSearchContinuationsPerDecision)
            MaxSearchContinuationsPerDecision = other.MaxSearchContinuationsPerDecision;

        TrajectoryBuilds += other.TrajectoryBuilds;
        TrajectorySamples += other.TrajectorySamples;
        if (other.MaxTrajectorySamples > MaxTrajectorySamples)
            MaxTrajectorySamples = other.MaxTrajectorySamples;

        BeliefRebuilds += other.BeliefRebuilds;
    }

    /// <summary>
    /// Returns the work done after <paramref name="earlier"/> was captured. Used by a probe that wants the
    /// measured window only (a warm-up run must not dilute the per-tick means).
    /// </summary>
    public AiWorkStats Minus(AiWorkStats earlier)
    {
        var result = new AiWorkStats
        {
            CarrierDecisions = CarrierDecisions - earlier.CarrierDecisions,
            CarrierCandidatesGenerated = CarrierCandidatesGenerated - earlier.CarrierCandidatesGenerated,
            CarrierCandidatesKept = CarrierCandidatesKept - earlier.CarrierCandidatesKept,
            MaxCarrierCandidatesGenerated = MaxCarrierCandidatesGenerated,
            CarrierCapTrims = CarrierCapTrims - earlier.CarrierCapTrims,
            OffBallDecisions = OffBallDecisions - earlier.OffBallDecisions,
            OffBallCandidatesGenerated = OffBallCandidatesGenerated - earlier.OffBallCandidatesGenerated,
            OffBallCandidatesKept = OffBallCandidatesKept - earlier.OffBallCandidatesKept,
            MaxOffBallCandidatesGenerated = MaxOffBallCandidatesGenerated,
            OffBallCapTrims = OffBallCapTrims - earlier.OffBallCapTrims,
            SearchFirstActions = SearchFirstActions - earlier.SearchFirstActions,
            SearchContinuations = SearchContinuations - earlier.SearchContinuations,
            MaxSearchContinuationsPerDecision = MaxSearchContinuationsPerDecision,
            TrajectoryBuilds = TrajectoryBuilds - earlier.TrajectoryBuilds,
            TrajectorySamples = TrajectorySamples - earlier.TrajectorySamples,
            MaxTrajectorySamples = MaxTrajectorySamples,
            BeliefRebuilds = BeliefRebuilds - earlier.BeliefRebuilds
        };
        return result;
    }
}
