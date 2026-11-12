# Building a Football AI: Positional Play, Pressing and Combinations

*Design notes for a C# football simulation*

The main job is translating it into data the AI can reason about. Concepts like half-spaces, triangles and rotations shouldn't be scripted one by one. If you build a good shared model of the pitch, they become things the AI can find on its own, plus a few authored patterns on top.

## 1. Build a shared "understanding" layer first

Everything else reads from this. Compute it once per team tick, not per player.

- **Pitch grid.** Divide the pitch into cells (2-5 m is fine) and tag each with its channel (left wing, left half-space, centre, right half-space, right wing), its line (own third, midfield, final third), and its distance and angle to goal.
- **Pitch control.** For each cell, estimate which team gets there first, using each player's position, velocity, top speed and acceleration. This is your "space ownership" map, and it's the most valuable tool you can build. It tells you where there's space, who owns it, and how quickly that changes.
- **Pitch value.** A map of how dangerous each cell is to receive the ball in. It's higher near goal, higher centrally, and particularly high in the half-spaces near the box. Combined with pitch control, you get "valuable space we can actually reach".
- **Pass lanes.** For each carrier-to-teammate pair, estimate the chance a defender intercepts, by comparing the defender's time to reach the ball's path against the ball's travel time.
- **Pressure.** For each player, count nearby opponents, weighted by distance, closing speed and direction. Direction matters because it tells the player which side is open for the first touch.
- **Phase detection.** Build-up, progression, final-third attack, defensive transition, counter-press, organised defence (high, mid or low block).

**Half-spaces and "between the lines" without hard-coding.** A cell is great when the two nearest defenders are about equally far from it. They hesitate, and nobody owns it. The half-space is exactly where fullbacks and centre-backs have ambiguous responsibility. So give half-space cells a bonus, and add a "defender ambiguity" bonus for cells between the defensive and midfield lines. Players will find these spots by themselves.

## 2. Team layer: slots, not players

This answers your point about a pressing player's position being taken over by someone else.

- A formation is a set of **slots**. Each slot has a role, an anchor zone, and offsets per phase. A 4-3-3 defending can become a 3-2-5 in possession, with a fullback inverting into midfield.
- Every team tick, run an **assignment**: match players to slots by minimising total cost. Cost is roughly time to reach the slot, plus a penalty for role mismatch (a striker in a centre-back slot is very expensive), plus a **hysteresis bonus** for staying in the current assignment so players don't swap constantly. With 10 outfield players you can solve this exactly, or just use a greedy pass.
- When the centre-back steps out to press, his slot is vacated and the assignment recomputes. The defensive midfielder drops in, the fullback tucks in, and so on.
- **"Sometimes it doesn't"** falls out naturally. If the swap cost is too high, or the vacated slot isn't dangerous (far side, ball is far away), the slot stays empty and the team compensates by compressing toward the ball. Give slots different importance: central slots near the ball matter much more than wide slots on the far side.
- **Free roles**, like Messi, are just slots with a weak anchor and a strong pull toward high pitch value and the ball. The slot system lets the rest of the team cover for his wandering automatically.

Also apply positional-play rules as scoring terms for teammates' positions:

- Fill the channels and lines (width, depth, between the lines).
- Avoid two players in the same channel and the same line unless a rotation is intended.
- Keep 3-4 options around the carrier.
- Keep **rest defence**: 2-3 players stay behind the ball line so that losing it doesn't expose you.

## 3. Defending

- **Press triggers.** Back pass, bad first touch, receiver with back to goal, ball near the touchline, slow or lofted pass. Pressing intensity is a team parameter that changes how many triggers fire and how far players are willing to travel.
- **Roles.** First presser (lowest cost to reach the ball, but also considering angle), second player who blocks the key passing lane, others who mark the next options. The presser should approach with a curved run so their body creates a **cover shadow**, blocking the lane to one opponent while closing the carrier.
- **Pressing as a trap.** Steer the carrier toward the touchline or toward a pre-set pressing zone, then close the lane behind them.
- **Marking.** Zonal with man orientation. Mark the lane and the goal-side position, not just the opponent.
- **Compactness.** The block shifts with the ball (ball-side compaction, far-side tuck), keeps vertical distance between lines at a sensible figure, and the defensive line steps up and drops together. An offside trap should be a synchronised line action triggered when the carrier can't play long.
- **Containment vs tackle.** Jockeying is the default. A tackle is a separate action with its own utility: win chance against the cost of being beaten.
- **Counter-press.** After losing the ball, the nearest 2-3 players press for a few seconds, and the rest recover into shape. Whether to counter-press or fall back depends on rest defence and how close you are to the ball.

## 4. Attacking off the ball: runs, triangles, combinations

**Runs as a library of templates.** Each template has preconditions, a target area and a score:

- Run in behind, overlap, underlap, diagonal run, checking run (drags a defender), dropping run, late box arrival, decoy run.
- To score a run, ask: Is the target space valuable and reachable before defenders? Can the carrier actually play that pass right now (lane, time on ball, body orientation)? Am I onside? Does this run pull a defender out of a lane that helps a teammate? That last one is the decoy value.

**Linking runner and passer.** In real football, runs happen because the runner predicts the pass. Do the same: the runner evaluates "would a pass here be good for the carrier?" using the same data the carrier uses, so they agree implicitly. You can add a lightweight "intent" board where the runner announces a run and gives the carrier's matching pass a bonus.

**When no run is on, form structure.** Support players pick positions that create **triangles** with the carrier: one safe option behind or level, one progressive option ahead, ideally in different lanes and lines. Score candidate positions by lane openness, triangle quality (roughly 10-20 m sides, not collinear), pressure at that spot, not duplicating a teammate's channel, and distance from the slot anchor. The triangle moves with the ball.

**Combinations (one-twos, third-man runs).** These rarely emerge from independent decisions, so use two tools:

- A **2-3 step lookahead** on the carrier's decisions. Evaluating "pass to B, then B passes to C" finds third-man plays naturally.
- A short-lived **combination object** shared by the two players, for one-twos. The initiator passes and runs into space past the defender, and the partner knows to return the ball first-time. It has a timeout and cancels if the defender intercepts.

## 5. Ball carrier and first touch

- **Anticipate before the ball arrives.** The receiver scans, predicts where defenders will be when the ball lands, and picks the next action in advance. That decides the touch.
- **First touch into space.** Pick a target cell for the touch: away from pressure, toward the highest-value reachable cell, with body orientation that opens the next pass. Touch quality depends on skill, ball speed and pressure, and a poor touch can leak the ball to the opponent.
- **Candidate actions.** One-touch pass, control and pass, carry, dribble, shield, shoot, cross, switch, clear, recycle. Score each as: gain in pitch value × success chance − expected cost of losing the ball. The turnover cost is key: it should be high in your own third and low in the final third, which gives you safe build-up and bold final-third play without special cases.
- **Switches of play** get a bonus when the ball side is overloaded and the far side has free valuable space.
- **Time on ball.** Under heavy pressure only one-touch or shielding options are available. This is what separates calm players from rushed ones.

## 6. Player attributes and imperfection

Don't make every player a perfect evaluator. Use attributes to modulate:

- **Perception.** Noise in lane and space estimates (vision, anticipation).
- **Reaction delay** in decisions.
- **Risk appetite** in utility weights.
- **Execution noise** in pass and touch accuracy, rising with pressure.
- **Positional discipline** versus freedom (the anchor weight).

Roles and team style are then just different weight sets stored as data.

## 7. Architecture

1. **Physics** at full rate.
2. **Analysis layer** (pitch control, value, lanes, pressure) at about 5-10 Hz.
3. **Team brain** (phase, press triggers, slot assignment) at about 2-4 Hz.
4. **Player brain** (choose an intent: press, mark, run, support, ball action) at about 5 Hz, staggered across players so the load spreads.
5. **Motor layer** (steering, speed, acceleration, turn rate, kick and touch execution).

A few practical points:

- Give each intent a **minimum duration and hysteresis** so players don't flicker between choices.
- Share intents through a simple **blackboard**: who is pressing, active runs, active combinations.
- In C#, use preallocated arrays and structs in the tick loop to avoid garbage collection spikes.
- Use cheap approximations (time-to-reach, not full trajectory integration) for pitch control.

## 8. Debugging and tuning

Build the overlays early: pitch control heatmap, value map, slot anchors and assignments, active runs, pass lane scores, and the action scores for the carrier. Then run AI vs AI matches and compare team-level stats to real football (pass counts, pass accuracy, shots, compactness, average team width and depth, how often presses win the ball). Tune weights until the stats look realistic, then until the shapes look right on screen.

## 9. Build order

1. Pitch grid, pitch control, pitch value
2. Slots, phase shapes, and assignment with hysteresis
3. Defensive roles, press triggers, compactness
4. Carrier action scoring with turnover cost
5. Support triangles and runs
6. First touch and anticipation
7. Lookahead and combinations
8. Attribute noise and role weights

Slot assignment and pitch control are the best places to start, since most of the rest depends on them.
