# Engineering Realistic Football AI for Autonomous NPCs

## Executive model and gap analysis

Your notes already point in the right architectural direction: build a common spatial interpretation layer, let collective structure emerge from constraints and utility rather than scripting every pattern, separate team intent from individual execution, and treat football as a continuously changing multi-agent control problem rather than as a library of canned animations. That is a strong starting hypothesis. fileciteturn0file0 Real tracking-data research supports the usefulness of concepts such as pitch control, spatial occupation, pass availability, body orientation, and possession value, while elite coaching material supports principles such as width, depth, occupation of channels, coordinated pressure, cover, rest defence, rotations, switches, and third-player combinations. citeturn624427search8turn624427search3turn111256search0turn111256search2

The main correction is that **none of those concepts should be treated as a universal deterministic rule**. Five-lane occupation is a useful positional-play framework, but not every attack should literally contain one player in each lane. FIFA's analysis of elite teams describes five-channel occupation as a recurring attacking strategy rather than a law of football. citeturn111256search0 Likewise, compactness is context-dependent: tracking research from Bundesliga matches found relatively little relationship between whole-team compactness and defensive success, while compactness among the defenders closest to the ball was more strongly associated with successful defending. citeturn995103search9

A useful distinction throughout the implementation is:

| Category | Meaning in the simulation |
|---|---|
| **Established law** | Rules imposed by IFAB: offside, restarts, fouls, field limits. |
| **Empirical football tendency** | Patterns supported by tracking/event studies, such as effects of pressure, scanning, spatial control, or pass difficulty. |
| **Coaching principle** | Widely used tactical guidance such as pressing angles, width/depth, third-man combinations or rest defence. Useful but not universal. |
| **Engineering heuristic** | Your approximation for making the simulation tractable: grid size, update frequency, utility weights, commitment timers, candidate counts. These require validation rather than being treated as football facts. |

Several items in the original design deserve explicit revision.

**Global slot reassignment is too aggressive.** Re-running an unconstrained global assignment every team tick can produce implausible role teleportation in logical space: two midfielders may repeatedly exchange responsibilities even though neither would interpret the real situation that way. Real defensive organisation commonly involves cover, marking handovers, lateral exchanges and players filling nearby spaces, but these are local and temporally coordinated rather than continuous global optimization. FIFA material on pressing and defensive coverage repeatedly stresses coordinated pressing, cover, passing on opponents and nearby players adapting around the player who steps out. citeturn244808search0turn524330search9 The better engineering model is **persistent role ownership plus local responsibility transfer**, with global reassignment only on slower structural events.

**“Equidistant to two defenders” is not a reliable proxy for ambiguity.** Space between defenders can be valuable because neither defender can engage without exposing another zone, but equidistance alone ignores body orientation, cover, line integrity, passing lanes, teammate pressure, available recovery paths and whether one defender is explicitly responsible for the receiver. Tracking-based space models instead estimate influence or arrival/control continuously and can incorporate motion. citeturn624427search8turn244808search3 Treat “defensive ambiguity” as a richer measure of **responsibility conflict**, not geometry alone.

**“Keep two or three behind the ball” should not be a hard rule.** FIFA examples show rest-defence structures changing according to the attacking structure and match situation; one analysed Spain example left four players securing the space while six attacked, while Chelsea's 3-2-5 used midfielders and defenders to protect transition spaces. citeturn111256search4turn524330search7 The correct abstraction is to defend dangerous counterattack routes and opponent outlets with enough players to achieve the team's desired transition security.

**Naive xT is not receiving value.** xT assigns expected attacking threat primarily from ball location and is deliberately simple; it does not by itself know whether a receiver is marked, facing backwards, able to turn, or likely to lose possession. citeturn244808search6turn244808search8 A simulation should instead score a prospective receiving state using space/control, orientation, pressure, passing success, next-action options and turnover danger. EPV-style methods are conceptually closer because they value richer instantaneous possession states, although a commercial simulation need not replicate a learned EPV model exactly. citeturn624427search3

The target architecture should therefore be:

> **Tactical principles constrain and bias behaviour; spatial models expose affordances; players perceive only part of that state; utility selects among feasible intentions; motor and technical execution determine whether the intended action actually succeeds.**

That produces emergent football because the AI is choosing from football affordances, rather than replaying predetermined football sequences.

## Real-football state model

Football should not be represented as six mutually exclusive phases. It is better modelled as a **hierarchical state with overlapping tactical contexts**. FIFA's modern analysis explicitly examines construction, progression, pressing, defending and reactions to possession changes as interconnected phases, while recent tournament analysis also highlights back-three build-up structures, wide rotations, inside-channel occupation, high pressing and counterpressing. citeturn244808search2

A useful top-level state is:

```text
Possession owner
 ├─ Our stable possession
 │   ├─ Restart
 │   ├─ First build-up
 │   ├─ Progression
 │   ├─ Established final-third attack
 │   └─ Finishing / box attack
 │
 ├─ Opponent stable possession
 │   ├─ High press
 │   ├─ Mid block
 │   ├─ Low block
 │   └─ Emergency box defence
 │
 └─ Unstable / transition
     ├─ We just won ball
     │   ├─ Counterattack
     │   └─ Secure/recycle
     └─ We just lost ball
         ├─ Counterpress
         ├─ Delay
         └─ Recover/retreat
```

This is not merely semantic. Different time horizons apply in each context. A centre-back in stable build-up may reason two or three actions ahead; a full-back facing a turnover should react primarily to immediate ball access, central danger and runners; a goalkeeper defending a ball over the high line must choose within a much shorter window. Modern goalkeeping analysis stresses both build-up participation and proactive management of the space behind a high defensive line. citeturn524330search0turn524330search1turn524330search2

### Reference shape versus current responsibilities

Treat formations such as 4-3-3, 4-2-3-1, 3-4-3 and 3-2-5 as **reference topology**, not fixed coordinates. FIFA examples show 3-4-3 becoming 3-2-5 in possession, with five players occupying advanced channels, and 4-3-3 structures changing according to opponent pressing shape. citeturn524330search7turn524330search3

Each player should simultaneously have:

```text
NominalRole        e.g. LeftBack
PhaseRole          e.g. InvertedSupport
CurrentDuty        e.g. CoverCentralTransition
LocalResponsibility e.g. TrackRunner#17
Intent             e.g. MoveToSupportPocket
MotorAction        e.g. AccelerateTo(position)
```

This distinction is critical. The same nominal left-back may:

- form the fourth defender in a low block;
- invert beside the holding midfielder during build-up;
- overlap a winger in final-third attack;
- stay deep as part of rest defence;
- temporarily become the nearest centre-back when a central defender steps out.

Those should be changes in **duty and responsibility**, not necessarily changes in nominal formation identity.

### Four reference frames for every movement

Every target position should be evaluated relative to four simultaneous frames.

**Ball-relative:** distance, angle, passing lane, whether ahead/behind the ball, ability to support or press.

**Teammate-relative:** spacing, coverage, triangles, staggering, line balance, double occupation, cover relationships.

**Opponent-relative:** marking, cover shadows, pressure, blind side, line-breaking position, pinning, isolation.

**Space-relative:** pitch control, goal value, touchline constraints, offside line, free space and likely future space.

This is better than assigning a point target such as “LW goes to `(x,y)`.” The desired position is the optimum of several contextual constraints.

A generic positional score can be written:

\[
U_{pos}(x)=
w_s S(x)+
w_c C(x)+
w_l L(x)+
w_p P(x)+
w_o O(x)+
w_t T(x)
-
w_d D(x)
-
w_r R(x)
\]

where:

- \(S\) = space availability;
- \(C\) = team control;
- \(L\) = lane/line structural value;
- \(P\) = pass availability;
- \(O\) = orientation quality on reception;
- \(T\) = tactical-role usefulness;
- \(D\) = duplication/congestion;
- \(R\) = transition risk.

The weights should vary by phase, role, player and tactical style.

### Pitch control is useful but should not become truth

Pitch-control models estimate which player or team can influence or reach regions of the pitch. Research has progressed from Voronoi-like distance models to physics- and movement-informed continuous control models, and empirical work has connected spatial-control measures with football outcomes. citeturn244808search7turn624427search8turn244808search3

A simulation does not require the computational complexity of a research model. A good real-time approximation is:

\[
T_i(x)=T_{\text{turn}}+
T_{\text{accelerate}}+
T_{\text{travel}}
\]

then convert arrival-time differences into a soft probability:

\[
P_i(x)=\frac{e^{-kT_i(x)}}{\sum_j e^{-kT_j(x)}}.
\]

Do not use current Euclidean distance alone. Orientation matters in real pass feasibility modelling, and player motion models perform better when velocity and directional constraints are incorporated. citeturn576061search7turn624427search8

For gameplay you can separately maintain:

```csharp
struct SpatialCell
{
    public float OurControl;
    public float OppControl;
    public float ReceiveValue;
    public float TransitionRisk;
    public float GoalThreat;
    public byte Lane;
    public byte Third;
}
```

A 2–5 m cell size is an acceptable **engineering starting point**, not a football-derived constant. A hybrid approach is even better: coarse whole-pitch grid plus dynamically sampled continuous candidate positions around relevant players.

### Laws must be deterministic

Offside should not be approximated tactically. IFAB Law 11 defines offside position relative to the ball and second-last opponent at the instant a teammate plays or touches the ball; being in an offside position is not by itself an offence. Goal kicks, throw-ins and corner kicks also have specific exceptions. citeturn995103search1

Therefore use two layers:

```text
Tactical onside margin
    "Should I delay my run?"

Legal offside resolver
    "Was this player offside at the exact pass-contact instant?"
```

The legal resolver needs a historical snapshot at ball contact rather than whatever positions exist when the receiver reaches the ball.

Set pieces need their own phase handlers because IFAB prescribes restart geometry and opponent distances. Kick-offs, free kicks and corners are not ordinary open-play possessions. citeturn995103search2turn995103search3turn995103search4

## Tactical behaviour library

The most robust design is not “one AI per formation.” It is a shared library of tactical actions whose **selection weights and structural constraints** differ by style.

### Attacking structure and positional play

FIFA tactical analysis documents five vertical attacking channels—two outside channels, two inside channels and the centre—and shows teams using a player in each channel to stretch a defensive line and create positional advantages. citeturn111256search0 The useful simulation principle is therefore:

```text
Reward complementary horizontal occupation,
but do not force five-lane occupancy every tick.
```

The important attacking variables are:

- width;
- depth;
- vertical staggering;
- different passing lines;
- players between opposition lines;
- players threatening behind;
- support behind the ball;
- far-side availability.

FIFA coaching material consistently teaches short and long support options, players on different lines, width/depth and timed movement into free space rather than static occupation. citeturn111256search1turn111256search3

Instead of a binary “half-space bonus,” calculate whether a player receives **between responsibilities**:

```text
ResponsibilityConflict =
    CanNearestDefenderStepOutWithoutBreakingLine?
  + CanSecondDefenderCover?
  + IsReceiverBehindMidfieldLine?
  + IsReceiverOutsideCBImmediateZone?
  + IsPassingLaneOpen?
  + IsReceiverBodyOpen?
```

That recreates why half-spaces are often useful without making the geometric label itself magical.

### Superiorities

The traditional tactical language of superiorities maps cleanly to simulation variables.

**Numerical superiority:** more effective participants locally.

```text
OurReachablePlayers(area, horizon)
-
TheirReachablePlayers(area, horizon)
```

**Positional superiority:** receiver can exploit a structurally advantageous position even without numbers.

```text
CanReceiveBetweenLines
+ CanTurnForward
+ CanBreakNextLine
- ImmediatePressure
```

**Qualitative superiority:** favourable individual matchup.

```text
DribblerSkill vs Defender1v1Ability
SprinterAcceleration vs DefenderRecovery
TargetForwardAerial vs DefenderAerial
```

**Dynamic superiority:** movement creates an arrival advantage.

```text
Receiver reaches valuable zone
before defender can reorganize.
```

These should interact rather than produce four separate tactics.

### Runs should be candidate generators

For each off-ball attacker generate a small set of plausible runs:

```csharp
enum RunType
{
    HoldWidth,
    RunInBehind,
    DiagonalInBehind,
    CheckToBall,
    PullWide,
    Overlap,
    Underlap,
    ThirdMan,
    BlindSideBox,
    LateBox,
    Pin,
    Decoy,
    RecycleSupport
}
```

Each generator proposes a path rather than a destination.

```text
RunScore =
    1.4 * ReceiveValue
  + 1.2 * PassProbability
  + 0.9 * LineBreakValue
  + 0.7 * DefenderDisplacement
  + 0.6 * FutureOptionValue
  - 1.5 * OffsideRisk
  - 0.9 * CollisionOrCongestion
  - 0.7 * TransitionExposure
```

All coefficients above are **provisional engineering defaults**, not empirical constants.

FIFA analysis supports the football logic behind overlaps and underlaps: they can create or exploit overloads and also act as decoys that force defenders to choose who to track. citeturn524330search5 Blind-side and in-behind runs should additionally score the defender's field of view and body orientation, because attacking a defender's unseen side is tactically different from merely entering a free coordinate.

### Pinning and decoys require counterfactual value

A decoy is valuable when the movement changes another player's opportunity.

Do not score only:

```text
Did runner become available?
```

Score:

```text
What does defender do if runner moves?
```

A lightweight approximation is:

\[
DecoyValue =
V_{\text{best teammate after defender response}}
-
V_{\text{best teammate without run}}.
\]

You do not need full opponent simulation. Sample two likely defender responses:

1. defender follows;
2. defender holds zone.

A run is useful if either response creates a favourable consequence.

### Third-man combinations

Third-player combinations are well-established coaching concepts for breaking lines, with the third player exploiting space created by an initial pass and supporting movement. citeturn111256search2

They can emerge from short-horizon action valuation:

```text
A -> B
B cannot turn
C becomes free because defender engages B
B -> C
```

The key is that B's utility includes the **next receiver's state**, not merely B's own reception value.

Use shallow search rather than general-purpose long planning:

```text
Depth 0: immediate action score
Depth 1: expected receiver state
Depth 2: best likely continuation
```

For only the top few passes, this is cheap enough to run selectively.

A one-two is different because the initiator usually commits to immediate movement after the first pass. Represent it as a short coordination contract:

```csharp
struct CombinationIntent
{
    public int Initiator;
    public int LinkPlayer;
    public Vector2 ReturnRegion;
    public float ExpiryTime;
    public CombinationType Type;
    public float Confidence;
}
```

Do not force the return pass. The link player should still abandon it if the lane disappears.

### Overload-to-isolate and switches

Switches work because moving the defensive block toward one area can expose a distant player or create a 1v1 or numerical advantage on the opposite side. FIFA analysis explicitly describes switches as a means of exploiting narrow defensive organisation and creating favourable wide situations. citeturn111256search5

Track:

```text
BallSideDensity
FarSideControl
FarSideDefenderCount
ReceiverIsolationQuality
SwitchPassFeasibility
```

Then:

\[
SwitchValue =
FarSideOpportunity
\times PassSuccess
-
TurnoverCost.
\]

“Overload-to-isolate” should therefore be emergent: several players gathering locally increases local options but also shifts defenders; the switch becomes attractive only when the far-side matchup improves.

### Crosses, cutbacks and box occupation

A cross is not simply “ball wide + player in box.” FIFA defines cutbacks as backwards diagonal deliveries from near the byline and highlights their usefulness because defenders recovering toward goal often have difficulty seeing late runners behind them. citeturn111256search6turn111256search7

Create box-arrival roles dynamically:

```text
Near-post threat
Central / penalty-spot threat
Far-post threat
Cutback / edge runner
Rebound / rest-attack support
```

Do not demand all five. Assign candidates according to arrival time, role, height, finishing ability and transition risk.

A crossing decision should compare:

```text
EarlyCross
DrivenCross
ClippedCross
Cutback
CarryFurther
Recycle
```

The target should be a **future interception point**, not current player position.

### First touch and receiving

Research on player orientation shows that body orientation materially improves pass-feasibility modelling. citeturn576061search7 Scanning studies also find associations between scanning, body orientation and pass completion, while pressure strongly affects receiving context. citeturn995103search6turn995103search7

Treat reception as a planning problem before ball arrival:

```text
Incoming ball
    ↓
Predict arrival time and point
    ↓
Update local perceptual memory
    ↓
Generate body orientations
    ↓
Generate first-touch targets
    ↓
Evaluate next action from each
```

Possible touch objectives:

```text
Kill ball
Open body
Escape pressure
Set up pass
Push into running stride
Shield
Prepare shot
Prepare cross
```

A simplified score:

\[
U_{touch} =
V(nextState)
-
Pressure(nextState)
-
ExecutionRisk
-
TouchDistanceRisk.
\]

Touch error should depend on at least:

```text
technical control
preferred/nonpreferred foot
ball speed
ball height
pressure
turn angle
movement speed
fatigue
```

The player should not know their exact future error; they only estimate success probability.

### Defensive structure

Defensive AI needs three distinct concepts:

```text
pressure   = engage ball carrier
cover      = protect consequence if pressure fails
balance    = protect weak-side / deeper threats
```

FIFA coaching material teaches nearest-player pressure combined with a covering player anticipating the next pass; pressing players are also encouraged to approach at angles that close passing lanes. citeturn244808search0

A presser therefore does not target the ball carrier's current position directly.

```text
DesiredPressVelocity =
    intercept(carrier future path)
  + steeringBias(blockedPassingLane)
```

This naturally creates curved or angled pressure.

### Cover shadows

Represent the presser's shadow geometrically as an occlusion wedge extending from the ball through the pressing player.

For receiver \(r\):

\[
Shadow(r)=
f(\text{angular overlap},
\text{presser distance},
\text{receiver depth}).
\]

Do not make shadowed passes impossible. A technically strong carrier may still bend, chip or play around pressure.

### Zonal versus man-oriented defending

Do not implement “zonal” and “man marking” as binary modes. Real systems frequently combine zone responsibility, opponent orientation, passing-on and individual tracking. FIFA examples explicitly discuss mixed marking and responsibility handovers. citeturn524330search9

Each defender can score two competing obligations:

\[
U =
w_z\,ZoneIntegrity +
w_m\,OpponentThreat.
\]

A zonal team has high \(w_z\); an aggressive man-oriented side has high \(w_m\). Both still need emergency overrides.

Opponent threat can use:

```text
distance to goal
line-breaking position
pass likelihood
run momentum
receiving orientation
relative matchup
```

### When a defender steps out

This is where your slot model should be changed most.

Suppose LCB leaves the line to press an attacking midfielder.

Do **not** immediately recompute all ten slots.

Instead publish:

```csharp
CoverageRequest {
    VacatedRegion = LeftCentreBackZone;
    CausePlayer = LCB;
    ExpectedDuration = 1.4f;
    Threat = runner/space;
}
```

Nearby teammates evaluate local actions:

```text
DM: drop into gap?
LB: narrow?
RCB: shift across?
far winger: tuck?
GK: advance?
```

Each response has cost and responsibility.

A plausible cascade is:

```text
LCB steps → DM drops 5–8 m → LB narrows →
RCB shifts slightly → opposite FB balances →
GK raises sweep position.
```

The values are situational; the distances should be tuned rather than treated as tactical laws.

The responsibility should have a minimum life and only be handed back when the original defender recovers into a valid line position.

### Defensive line and offside management

A defensive line needs a **shared depth target** plus local exceptions.

```text
LineTarget =
    ball pressure state
  + opponent run threat
  + goalkeeper sweep range
  + team style
  + field zone
```

Step forward when the passer is constrained and backward when a credible in-behind pass is becoming executable. FIFA coaching material links high-line play with offside management and also stresses goalkeeper responsibility behind that line. citeturn244808search1turn524330search2

Individual defenders should not snap to a line coordinate. Give them tolerance bands and leadership/discipline differences.

### Pressing triggers

Back passes, poor touches, receivers facing their own goal, slow/lofted passes and touchline situations are all credible **trigger candidates**, but the simulation should treat them as evidence that a press may succeed rather than magic events.

Use:

\[
PressOpportunity =
P(win\ ball)
\times Value(turnover)
-
P(bypass)\times Exposure.
\]

A poor touch increases \(P(win)\). A touchline reduces escape directions. A backward-facing receiver increases turning cost. But the team should still refrain from pressing if distances behind the first presser are too large.

### Counterpress versus retreat

FIFA describes counterpressing as an immediate collective reaction after loss, with nearby players applying pressure and others protecting central spaces; coaching examples often use time constraints such as five seconds as drill rules, not universal match laws. citeturn111256search10turn111256search12

The choice should be state-driven:

```text
Counterpress if:
    local numbers favorable
    AND ball carrier constrained
    AND central cover adequate
    AND nearest players can arrive soon
    AND escape pass danger acceptable
Else:
    delay / retreat / rebuild block
```

The original “nearest two or three counterpress” is therefore better interpreted as **dynamic local participation**.

### Goalkeeper behaviour

The goalkeeper belongs in all spatial calculations. FIFA materials describe modern keepers as active build-up players, scanners, support options and defenders of the space behind the line. citeturn524330search0turn524330search1

The goalkeeper's utility set should include:

```text
Hold goal
Adjust depth
Sweep
Claim cross
Punch
Catch
Block 1v1
Support build-up
Short distribution
Medium distribution
Long distribution
Emergency clearance
```

Sweep decisions should compare goalkeeper and striker arrival times, ball trajectory, defender recovery and risk of leaving goal.

## Cognition, perception and player imperfection

The simulation becomes much more believable when **decision quality and execution quality are separated**.

A player can:

1. fail to perceive the best option;
2. perceive it but evaluate it wrongly;
3. choose it correctly but react late;
4. execute it badly;
5. execute correctly but be defeated by an opponent.

That separation is essential for player differentiation.

A recent review of elite-football cognition describes a perception–action cycle involving situational assessment, action selection/execution and outcome evaluation, with visual attention, pattern recognition, anticipation, memory and contextual knowledge contributing to decisions. citeturn576061search4

### No omniscient agents

The central simulation may know every player position for physics, but player AI should query a **perceived world state**.

```csharp
struct ObservedPlayer
{
    public int Id;
    public Vector2 EstimatedPosition;
    public Vector2 EstimatedVelocity;
    public float Confidence;
    public float LastObservedTime;
}

sealed class PlayerMemory
{
    public ObservedPlayer[] Teammates;
    public ObservedPlayer[] Opponents;
    public BallObservation Ball;
}
```

Visibility should depend on:

```text
head direction
body direction
distance
occlusion
attention target
recent scanning
perception attribute
```

Information behind the player's current visual direction should either come from memory or from a recent scan.

Scanning should therefore be an actual cognitive/motor behaviour, not an invisible stat buff. Empirical research finds that scanning frequency relates to contextual factors including pressure and is positively associated with successful passing, though the effect should not be interpreted as deterministic. citeturn995103search6turn995103search7

Possible implementation:

```text
Every 150–600 ms:
    consider whether to redirect gaze/head

Scan utility rises when:
    ball arrival approaching
    player is between lines
    unseen opponents nearby
    next action uncertain
    teammate calls
```

Those timing values are **provisional**.

### Memory decay

Use uncertainty growth rather than instantly deleting unseen players:

\[
\sigma(t)=\sigma_0 + k(t-t_{seen}).
\]

Predict:

\[
\hat{x}(t)=x_{last}+v_{last}\Delta t
\]

but increase uncertainty as elapsed time rises.

A tactically intelligent player can have:

- slower uncertainty growth;
- better motion prediction;
- stronger opponent-pattern priors;
- more frequent useful scans.

### Anticipation

Do not give elite players faster reflexes only. Research suggests skilled footballers make better use of contextual and postural cues and that anticipation involves a speed–accuracy trade-off. citeturn576061search4

Represent predicted intents probabilistically:

```text
Opponent:
  pass inside       0.41
  carry down line   0.31
  recycle           0.18
  cross             0.10
```

A high-anticipation defender starts moving before the choice is certain but can therefore be wrong.

### Communication

Communication can be implemented as **perception sharing with latency and ambiguity**, not direct mind-sharing.

Messages:

```csharp
enum CallType
{
    ManOn,
    Turn,
    Hold,
    Step,
    Drop,
    Switch,
    Leave,
    Time,
    Run,
    Cover
}
```

A spoken call updates the receiver's belief state or utility weights.

```text
"Man on"
→ increases rear-pressure belief
→ lowers turn utility
→ raises one-touch/shield/recycle utility
```

### Intent inference

Players should infer intent from:

```text
teammate movement direction
body orientation
gaze
role
tactical context
recent actions
explicit communication
```

This lets a winger recognise an overlapping full-back even before a blackboard contract is created.

### Correlated error

Independent random noise looks artificial. Errors should share causes.

Example: fatigue or pressure can simultaneously worsen:

```text
perceptual certainty
decision time
touch quality
pass accuracy
recovery acceleration
```

Experimental football studies report that mental fatigue can impair passing decisions, technical outcomes and other performance measures, although effect sizes and study designs vary. citeturn576061search0turn576061search1turn576061search2

Use latent state:

```text
ComposureState
PhysicalFatigue
MentalLoad
Confidence
PressureLevel
```

Then execution error samples depend on those variables.

### Archetypes as preferences, not unique code

Examples:

```text
Deep Playmaker
    high scan frequency
    high switch value
    high progressive-pass tolerance

Ball-Winning Midfielder
    high engage utility
    high duel confidence
    moderate positional freedom

Inverted Fullback
    high central-support value
    high rest-defence priority

Touchline Winger
    high width preference
    high isolation/dribble value

Inside Forward
    high diagonal-run and box-entry value

Target Forward
    high aerial-receive and wall-pass value

Sweeper Keeper
    higher sweep range
    higher build-up involvement
```

These should change weights and candidate generation, not create completely separate brains.

## System architecture and algorithms

A **hierarchical utility architecture with constrained behaviour execution** fits football better than pure behavior trees or unrestricted GOAP.

Behavior trees are effective for modular reactive control and became influential in game AI because they manage complexity better than large flat FSMs. citeturn180109search2turn180109academia42 GOAP is useful for sequences where actions explicitly create prerequisites for later actions, but planning under partial observability, simultaneous agents and rapidly changing state becomes much harder. citeturn180109search37turn180109search30

Recommended architecture:

```text
Physics / Laws
        ↓
Authoritative World State
        ↓
Shared Spatial Analysis
        ↓
Team Tactical Brain
        ↓
Per-Agent Perceived State
        ↓
Candidate Generator
        ↓
Utility Selector
        ↓
Short Coordination / Limited Lookahead
        ↓
Behavior Executor
        ↓
Motor + Animation + Ball Interaction
```

### What should be centralized

Central/shared computation:

```text
pitch geometry
legal offside line
coarse pitch-control surfaces
ball trajectory predictions
team phase state
reference defensive/attacking structure
global transition risk
restart state
```

Compute once because duplicating it for 22 players is wasteful.

Per-team:

```text
press policy
block height
rest-defence objectives
occupation targets
structural role ownership
active tactical coordination objects
```

Per-agent:

```text
perceived world
scan state
memory
candidate actions
local pressure
personal risk utility
role/duty preferences
execution uncertainty
```

The shared layer should not expose perfect information directly to the player brain.

### Starting update rates

These are **engineering starting points only**:

| System | Starting rate |
|---|---:|
| Physics / ball integration | 60–120 Hz |
| Motor steering | 30–60 Hz |
| Fine local collision/duel logic | 30–60 Hz |
| Perception/head scan | 10–20 Hz |
| Player utility decision | 5–10 Hz |
| Local pitch-control query refresh | 10 Hz |
| Full-pitch coarse control | 4–8 Hz |
| Team tactical structure | 2–5 Hz |
| Slow role/archetype reassessment | 1–2 Hz |

Do not synchronize all player decisions on the same frame. Staggering is a sensible optimization and also avoids robotic simultaneous responses.

### World state

```csharp
public readonly struct PlayerKinematics
{
    public readonly Vector2 Position;
    public readonly Vector2 Velocity;
    public readonly float FacingRad;
    public readonly float HeadFacingRad;
    public readonly float Stamina01;
}

public readonly struct BallState
{
    public readonly Vector3 Position;
    public readonly Vector3 Velocity;
    public readonly int LastTouchPlayer;
    public readonly int PossessingTeam;
}

public sealed class MatchWorld
{
    public PlayerKinematics[] Players = new PlayerKinematics[22];
    public BallState Ball;
    public MatchLawState Laws;
    public MatchClock Clock;
}
```

### Team blackboard

```csharp
public sealed class TeamBlackboard
{
    public TeamPhase Phase;
    public TeamStyle Style;

    public RoleAssignment[] StructuralRoles;
    public CoverageRequest[] CoverageRequests;
    public PressIntent Press;
    public CombinationIntent[] Combinations;
    public RunIntent[] AnnouncedRuns;

    public float BlockHeight;
    public float DesiredWidth;
    public float TransitionSecurity;
}
```

Prefer fixed-size arrays or pools in hot paths.

### Individual perceived state

```csharp
public sealed class PerceivedState
{
    public int SelfId;
    public BallObservation Ball;
    public ObservedPlayer[] VisiblePlayers;
    public ObservedPlayer[] RememberedPlayers;

    public float LocalPressure;
    public float DecisionConfidence;
    public float BallArrivalTime;
}
```

### Candidate generation before scoring

Never utility-score every theoretically possible coordinate or action. First generate football-plausible candidates.

Carrier:

```text
passes to visible teammate
passes into visible run corridor
carry targets
dribble matchups
shots
cross variants
shield
clear
recycle
```

Off-ball attacker:

```text
structural support
check
in-behind
overlap
underlap
box run
far-side width
countermovement
```

Defender:

```text
press
delay
cover
screen
track
intercept
hold line
drop
handover
```

This reduces CPU and makes behaviour easier to debug.

### Action utility

A general action formula:

\[
U(a)=
P_{success}(a)
\left[
V(S'|a)-V(S)
\right]
-
P_{turnover}(a)C_{turnover}
-
C_{physical}
-
C_{structural}
+
B_{role}
+
B_{intent}.
\]

Then apply uncertainty:

\[
\hat U(a)=U(a)+\epsilon_{perception}
\]

before selection, while physical execution noise occurs later.

Do not put all randomness in the final kick.

### Passing probability

Pass completion should combine trajectory and player arrival rather than use only an unobstructed line segment. Research models of pass completion explicitly combine ball dynamics, player movement and interception/blocking likelihood. citeturn624427search0turn624427search1

Simplified algorithm:

```pseudo
function PassSuccess(start, target, speed):
    path = PredictBallPath(start, target, speed)

    receiverArrival = TimeToControl(receiver, path.end)

    bestOpponentMargin = +INF

    for opponent in plausibleInterceptors:
        for sample in path:
            opponentT = TimeToReach(opponent, sample.position)
            ballT = sample.time
            bestOpponentMargin =
                min(bestOpponentMargin, opponentT - ballT)

    interceptRisk = Logistic(-bestOpponentMargin)

    controlRisk = ReceiverControlFailure(
        speedAtArrival,
        receiverOrientation,
        pressure)

    return (1 - interceptRisk) * (1 - controlRisk)
```

### Receiving value

Replace pure xT with:

\[
ReceiveValue =
P(pass)
\times
[
EPV_{approx}
+
TurnPotential
+
NextPassOptions
+
ShotPotential
]
-
TurnoverExposure.
\]

xT can remain one feature in `EPV_approx`, but not the complete answer. xT's simplifying assumption is precisely that state value can be represented mainly by ball location, while richer EPV approaches model more detailed instantaneous possession context. citeturn244808search8turn624427search3

### Spatial assignment with hysteresis

Use structural slot assignment only at the appropriate level.

For long-lived formation structure:

\[
Cost(i,s)=
T_{reach}+
RoleMismatch+
StructuralDisruption-
StayBonus.
\]

For local cover, do not invoke the whole assignment system.

```pseudo
if playerLeavesResponsibilityZone:
    publish CoverageRequest

for nearbyTeammate:
    score local cover response

winner commits CoverRole for MinCommitTime
```

A second optimization can later reorganize the shape if the temporary situation persists.

### Hysteresis and commitment

Two different mechanisms are needed.

**Hysteresis:** prevents small utility differences from causing switching.

```text
New action must exceed current utility by 8–15%
```

**Commitment:** prevents cancellation during actions that need continuity.

```text
press: 0.3–1.2 s
run: 0.5–2.5 s
support reposition: 0.4–1.5 s
combination: until defined event/timeout
```

All figures are provisional tuning values.

Allow emergency interrupts:

```text
ball suddenly loose
immediate shot threat
collision
new legal restart
pass delivered
offside state invalidates run
```

### Phase-specific decision flow

```text
                    BALL STATE
                        |
          +-------------+-------------+
          |                           |
      WE HAVE IT                  THEY HAVE IT
          |                           |
   stable or transition?        stable or transition?
      /          \                /          \
 stable         won ball      stable         just lost
   |               |             |               |
build/progress   counter?     block/press      counterpress?
   |               |             |               |
structure +      exploit      mark/cover/       if no:
affordances      imbalance     pressure          recover
```

The phase supplies prior weights. The local world state still determines the actual action.

## Worked multi-player scenarios and C#-friendly behaviour logic

### Centre-back steps out, defensive midfielder covers

Initial state:

```text
Opponent AM receives between lines.
LCB is nearest capable defender.
DM is 6 m goal-side/inside of AM.
LB has wide winger.
RCB protects striker.
```

**t = 0.00 s**

LCB computes:

```text
Press = 0.76
HoldLine = 0.41
Delay = 0.58
```

LCB chooses `StepAndPress`.

Blackboard adds:

```text
VacatedRegion = left central channel
ExpectedExpiry = +1.2 s
```

**t = 0.15 s**

DM evaluates:

```text
CoverVacatedCB = 0.82
ContinueMarkingMidfield = 0.46
PressReceiver = 0.38
```

DM begins dropping diagonally, not directly to the old CB coordinate.

**t = 0.25 s**

LB sees central coverage weakening and narrows his defensive reference position while retaining awareness of the winger.

RCB shifts slightly toward the ball but does not abandon the striker.

GK raises sweep readiness because the defensive line is temporarily less stable.

**t = 0.55 s**

AM passes backward.

LCB no longer needs to engage, but does not instantly teleport back logically. He enters `RecoverToLine`.

DM maintains cover until LCB is again line-compatible.

**t = 1.10 s**

Coverage request is released.

This gives natural defensive “accordion” movement rather than instantaneous slot swapping. FIFA coaching material supports the underlying pressure-cover relationship and the need for players behind a presser to close the spaces exposed by that press. citeturn244808search0

### Winger plus full-back overlap

State:

```text
RW receives touchline-side.
Opposition LB engages.
Our RB behind RW.
Opposition wide midfielder recovering inside.
```

RB generates:

```text
StayRestDefense
Overlap
Underlap
SupportBehind
```

RW simultaneously assesses:

```text
dribble inside
dribble line
pass inside
wait for overlap
recycle
```

The overlap's utility rises because:

```text
LB already engaged by RW
outside corridor is reachable
RB arrival precedes wide midfielder
crossing zone can be reached
rest defence remains acceptable
```

RB announces an overlap intent.

RW does not automatically pass. The overlap changes defender response:

```text
LB tracks RB → RW inside lane improves
LB stays with RW → RB receives outside
second defender helps → central teammate becomes freer
```

This models the overload/decoy logic described in elite coaching material. citeturn524330search5

### Third-man progression

```text
CB -> DM -> AM
```

At the initial state, AM is screened.

DM drops toward CB.

That movement attracts the opponent midfielder.

AM makes a blind-side movement into the newly opened pocket.

CB sees:

```text
direct CB -> AM probability = 0.28
CB -> DM probability = 0.91
expected DM -> AM probability = 0.77
```

CB's two-step evaluation raises the DM pass.

DM has pre-scanned and receives side-on.

Because the AM movement is already represented as an intent, the progressive first-time pass gets an intent-match bonus.

This is a genuine third-man pattern without scripting “if CB passes DM, DM must pass AM.” FIFA describes the third player's off-ball positioning and timing as central to such combinations. citeturn111256search2

### Coordinated touchline pressing trap

State:

```text
Opponent CB passes to RB near touchline.
Receiver is facing own goal.
Our LW is first presser.
Our CF can screen return to CB.
Our CM can jump toward opposition midfielder.
```

At pass release:

```text
PressOpportunity rises:
    touchline constraint
    receiver orientation
    ball travel time
```

LW targets an approach vector that blocks inside progression rather than running directly at the RB.

CF adjusts cover shadow toward CB.

CM moves to discourage the nearest central escape.

LB behind LW does not attack immediately; he covers the ball down the line and opponent winger.

If RB plays backward successfully, team evaluates whether to continue or exit the press.

This matches FIFA coaching descriptions of angled/arched pressure, central screening and using the touchline to restrict options. citeturn244808search0

### Defensive transition

At turnover:

```pseudo
for each defender:
    immediateThreat = ThreatIfOpponentReceivesForward()

team:
    localNumbers = CountReachableAroundBall(2.0s)
    centralSecurity = EvaluateCentralProtection()
    outletRisk = EvaluateOpponentEscapeRoutes()

if localNumbers >= required
   && centralSecurity > threshold
   && outletRisk < threshold:
       mode = Counterpress
else if nearestPlayerCanDelay:
       mode = DelayAndRecover
else:
       mode = FullRetreat
```

Do not hard-code `2.0 s`, `required`, or the thresholds as truths; expose them as tunable style parameters.

### Example C# utility interface

```csharp
public interface IActionCandidate
{
    ActionType Type { get; }

    bool IsFeasible(
        in PerceivedState state,
        in TeamContext team);

    float Score(
        in PerceivedState state,
        in TeamContext team,
        in PlayerProfile profile);

    IPlayerIntent BuildIntent();
}
```

Scorer:

```csharp
public float ScorePass(
    in PassCandidate pass,
    in DecisionContext ctx)
{
    float pComplete = _passModel.Estimate(pass, ctx);

    float receiveValue =
        _stateValue.EvaluateReception(pass, ctx);

    float turnoverCost =
        _transitionModel.LossCost(pass, ctx);

    float structuralCost =
        _shapeModel.DisruptionCost(pass, ctx);

    float styleBias =
        ctx.Profile.PassPreference(pass.Type);

    return
        pComplete * receiveValue
        - (1f - pComplete) * turnoverCost
        - structuralCost
        + styleBias;
}
```

Selection with inertia:

```csharp
public Candidate Select(
    Span<Candidate> candidates,
    in CurrentIntent current,
    float switchMargin)
{
    Candidate best = candidates[0];

    for (int i = 1; i < candidates.Length; ++i)
        if (candidates[i].Utility > best.Utility)
            best = candidates[i];

    if (current.IsValid &&
        current.Utility * (1f + switchMargin) >= best.Utility)
    {
        return Candidate.From(current);
    }

    return best;
}
```

### Style parameters

A data-driven style definition can be:

```csharp
public struct TeamStyle
{
    public float BuildShortBias;
    public float DirectPassBias;
    public float WidthBias;
    public float CentralOccupationBias;

    public float HighPressBias;
    public float ManOrientation;
    public float PressRiskTolerance;
    public float CounterPressBias;

    public float DefensiveLineHeight;
    public float BlockCompactnessBias;

    public float CounterAttackBias;
    public float PossessionSecurityBias;
    public float RestDefensePriority;
}
```

Example **provisional** presets:

| Style | Short build | Directness | Press | Transition attack | Man orientation |
|---|---:|---:|---:|---:|---:|
| Positional possession | .85 | .30 | .65 | .45 | .35 |
| Direct | .35 | .90 | .50 | .75 | .45 |
| Counterattacking | .40 | .75 | .35 | .95 | .45 |
| High press | .60 | .55 | .95 | .80 | .65 |
| Low block | .35 | .70 | .20 | .85 | .40 |
| Aggressive man-oriented | .55 | .55 | .85 | .65 | .90 |

These are configuration examples only.

## Implementation roadmap, validation and research-backed test strategy

The biggest risk is building sophisticated combination logic before fundamental spacing, movement and possession behaviour is credible. Development should therefore proceed from physical/spatial foundations toward cognition and coordination.

### MVP foundation

Build:

- deterministic movement and ball physics;
- legal game state and offside;
- continuous time-to-reach;
- simple player pressure;
- coarse pitch control;
- basic phase detector;
- persistent positional roles;
- carrier pass/carry/shoot selection;
- zonal defensive shape.

At this stage football should already look recognizable with no scripted tactical patterns.

Test:

```text
Can players support the ball?
Do defenders remain goal-side when appropriate?
Does a free receiver attract passes?
Does pressure reduce ambitious passing?
Does an unpressured team progress rather than circulate forever?
```

### Collective structure

Add:

- attacking width/depth;
- between-line occupation;
- high/mid/low block;
- line shifting;
- pressure-cover-balance;
- local coverage requests;
- rest-defence evaluation;
- goalkeeper support/sweep positioning.

Avoid adding specific named patterns yet.

Use tracking-derived measures such as centroid, team width, length, surface area, line distances and local compactness. Tactical-metric research commonly uses measures of this type to characterize collective behaviour. citeturn995103search8turn995103search9

### Off-ball intelligence

Add:

```text
run candidate library
receiver-value map
pass intent matching
offside-aware path timing
overlap/underlap
late box arrival
blind-side runs
decoys
```

At this point, five-lane occupation should be an outcome of structural scoring for applicable styles rather than a forced formation. FIFA observations of five-channel attacking structures can be used as a qualitative validation reference. citeturn111256search0

### Defensive intelligence

Add:

```text
press triggers
press angle
cover shadows
trap policy
local marking handovers
counterpress decision
recovery logic
line step/drop
sweeper keeper
```

A key property check:

```text
IF first defender presses
THEN at least one nearby defender should usually
protect a consequential space rather than independently press.
```

Not always—but statistically often enough to produce pressure-cover behaviour.

### Technical realism and cognition

Add:

```text
body orientation
head scanning
perceptual memory
reaction latency
first-touch planning
execution errors
fatigue
anticipation
communication
```

Scanning research provides useful behavioural validation: players scan before reception, pressure affects scanning/orientation, and scanning is associated with passing outcomes. citeturn995103search6turn995103search7

### Combinations and shallow planning

Only after single-action AI works should you add:

```text
one-two contracts
third-man lookahead
overload-to-isolate
multi-step pressing traps
restart routines
```

Use planning selectively. GOAP-style planning is valuable for explicit sequential goals, but continuously replanning all football behaviour for 22 players is unnecessary when most choices are reactive and time-critical. citeturn180109search36turn180109search30

### Advanced data-informed model

Later versions can fit or calibrate:

- pass-completion likelihood;
- movement/reach models;
- pressure models;
- reception value;
- shot probability;
- action-selection tendencies;
- role positioning distributions.

Open resources exist for this work. Metrica provides anonymized synchronized sample tracking/event matches, while StatsBomb's open repository provides event data and selected 360-degree contextual data. citeturn624427search5turn624427search6 These datasets are useful for model development and scenario comparison but should not be assumed to represent every league, tactical era or playing level.

### Deterministic scenario tests

Create unit-like football scenes:

```text
3v2 build-up
4v3 wide overload
CB steps out
DM receives under rear pressure
two attackers vs high defensive line
winger + overlapping fullback
3v3 counterattack
touchline pressing trap
5v4 box cross
keeper sweep race
```

A scenario should be replayable with deterministic RNG seed and complete AI decision logging.

Example assertion:

```csharp
Assert.That(
    dm.Intent.Type,
    Is.AnyOf(IntentType.Cover,
             IntentType.Drop,
             IntentType.Screen));
```

Do not require a single “correct” football move unless the case is governed by law.

### Property-based tests

Useful invariant-style tests:

```text
No player target outside legal pitch unless action permits it.
Offside law uses pass-contact snapshot.
Defensive role ownership never has impossible duplicate IDs.
Intent durations never become negative.
Pass probability remains [0,1].
Pitch-control probabilities remain normalized.
No NaN in spatial maps.
A player cannot perceive an unseen instantaneous position perfectly.
Goalkeeper hand restrictions respect laws.
```

Football properties:

```text
Increasing defender arrival speed should not make
a pass systematically safer.

Increasing pressure should not systematically increase
control quality.

Moving a receiver offside should not increase the
legal playable-pass set.

A teammate covering a vacated central zone should
reduce structural exposure, all else equal.
```

### Debug overlays

Essential overlays:

```text
control field
receive-value field
per-player perceived opponents
head / vision cone
memory uncertainty
press targets
cover shadows
mark responsibility
role anchor
temporary cover responsibility
offside line
candidate runs
candidate pass utilities
decision winner + second-best
first-touch targets
transition danger
```

Show **why** an action won.

Example debug row:

```text
PASS_TO_8       0.71
  completion    +0.55
  progression   +0.23
  orientation   +0.14
  turnover      -0.11
  role bias     -0.10

CARRY_RIGHT     0.65
RECYCLE_CB      0.54
```

This is far more useful than debugging final movement alone.

### Emergent-behaviour metrics

Match aggregate:

```text
possession
passes
pass completion
progressive passes
shots
shot quality
crosses
turnovers by zone
recoveries
```

Spatial:

```text
team width
team length
centroid
line spacing
local defensive density
average pitch control
```

Behavioural:

```text
runs offered per possession
runs ignored
offside runs
press attempts
press bypasses
counterpress attempts
counterpress escapes
coverage handovers
switches
one-twos
third-man sequences
```

Cognitive:

```text
scans before reception
memory age at decision
decision latency
one-touch rate under pressure
poor-touch rate
```

Do not tune only to aggregate match statistics. Two very unrealistic simulations can both produce 500 passes and ten shots. Combine statistics with spatial and behavioural validation.

### Ablation tests

Disable one system at a time:

```text
no body orientation
no local cover
no hysteresis
no perception limits
no rest defence
no transition cost
no run intent
no goalkeeper sweep behaviour
```

Then measure how match behaviour changes.

This helps detect systems that exist in code but do nothing.

### Common failure modes and fixes

| Failure | Likely cause | Fix |
|---|---|---|
| Everyone chases ball | local ball utility dominates | explicit cover/balance duties |
| Robotic formation | anchors too strong | context-sensitive role freedom |
| Chaotic swapping | global assignment too frequent | persistent role ownership + local handovers |
| Endless safe passing | turnover cost too dominant | diminishing recycle value, progression intent |
| Constant through balls | receive value ignores interception/orientation | richer pass feasibility |
| Wingers always overlap | pattern bonus too large | response-dependent overload value |
| Defenders never step | line integrity too dominant | threat-based press utility |
| Defenders overstep | engagement ignores cover | exposure term |
| Counterpress every loss | fixed transition rule | compare local superiority and escape risk |
| Players instantly know runners | omniscient state | visual perception + memory |
| Every good player plays identically | attributes only change accuracy | vary perception, risk, role and anticipation |
| Runs start/stop repeatedly | no commitment | intent hysteresis |
| Midfield looks empty | slots optimize destination rather than connections | passing-network and support terms |
| Low block impossible to break | space value too static | movement-induced defender response / switches / 1v1 |
| Press impossible to escape | pressure modeled as distance only | body orientation, bounce passes, third-man routes |
| xT causes strange risk | xT treated as full state value | pressure/orientation/transition-aware reception model |

### Performance target for 22 agents

A scalable approach should avoid computing every player against every fine-grid position every render frame.

A practical CPU strategy is:

```text
coarse map globally
fine sampling locally
nearest-threat sets
cached arrival fields
staggered cognition
fixed allocation buffers
selective two-step search
```

With 22 players, \(O(22^2)\) local relationship calculations are trivial compared with thousands of trajectory candidates. The expensive part is dense spatial and future-state evaluation, so spend resolution around the ball, receivers, defensive line and predicted landing zones.

### Research-data calibration

Pass models trained on tracking data show that pass completion can be estimated from ball trajectory, player movement and interception context rather than simple pass distance alone. citeturn624427search0turn624427search1 Pitch-control research likewise shows why binary nearest-player ownership is insufficient for realistic space modelling. citeturn624427search8 These models should inspire calibration targets, but your simulation's physics, animation, difficulty and intended playing level will require independent tuning.

The strongest overall design principle is therefore:

```text
Do not encode "football moves."
Encode:
    physical possibilities
    perceived opportunities
    tactical responsibilities
    risk/reward preferences
    coordination mechanisms
    imperfect execution
```

Third-man combinations, overlaps, pressing traps, rotations, cover movements and overload-to-isolate patterns should then occur because multiple agents perceive mutually compatible opportunities—not because a global script has decided it is time to play one.

## Source foundation and confidence map

The most important rules source is the **IFAB Laws of the Game**, with the current documents page listing the 2026/27 Laws and specific law pages covering offside and restart procedures. citeturn995103search0turn995103search1turn995103search2turn995103search3turn995103search4 These should be treated as authoritative for match legality.

For tactical principles, the FIFA Training Centre provides practitioner and tournament-analysis material on five-channel occupation, third-player combinations, switching play, pressing, counterpressing, goalkeeper participation, rest defence and wide overloads. These are highly useful descriptions of elite-football concepts, but they remain **tactical analysis and coaching guidance rather than universal scientific laws**. citeturn111256search0turn111256search2turn111256search5turn244808search0turn111256search10turn524330search0turn524330search7

For spatial computation, strong research foundations include Higgins et al., *Measuring the pitch control of professional football players using spatiotemporal tracking data* (2023), and broader work on continuous space/control modelling from Bundesliga tracking data. citeturn244808search3turn624427search8 These support pitch-control-inspired engineering but do not prescribe a single correct in-game algorithm.

For state valuation, Singh's xT formulation is useful as an interpretable location-based threat model, while Fernández, Bornn and Cervone's EPV work provides a substantially richer conceptual framework for instantaneous possession value. citeturn244808search6turn624427search3 This is the main reason the original proposal should use xT only as one feature of prospective receiving value.

For passing, Anzer and Bauer's *Expected passes* (2022) and the related pass-availability research demonstrate the value of combining movement, ball trajectory and interception likelihood. citeturn624427search0turn624427search1 Orientation-specific pass-feasibility research further shows that body orientation adds information beyond positions alone. citeturn576061search7

For cognition, scanning studies in professional and elite-youth football support explicit modelling of visual exploration, body orientation, pressure effects and incomplete information. citeturn995103search6turn995103search7 The broader cognitive review published in 2024 provides a useful perception–action framework involving visual attention, anticipation, action selection and outcome assessment. citeturn576061search4

For validation data, Metrica's open sample repository provides synchronized anonymized tracking and event data, while StatsBomb's open data repository provides event and selected 360 data suitable for developing and validating tactical metrics. citeturn624427search5turn624427search6

For software architecture, hierarchical behaviour structures and limited goal-oriented planning are supported by established game-AI practice and research, including Isla's work on *Halo 2* behaviour architecture and Orkin's GOAP work for *F.E.A.R.*. citeturn180109search2turn180109search37 The appropriate conclusion for football is not to choose one architecture exclusively, but to use **utility AI for continuous tactical choice, behavior-tree/state-machine logic for execution, and shallow planning or explicit coordination objects only where sequential cooperation materially matters**.