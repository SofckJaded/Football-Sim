# Football Simulation (Godot 4.7.2 .NET / C#)

Autonomous 11v11 physical football match simulation with a pre-match locked human career controller.

## Pinned Toolchain
- **.NET SDK:** `.NET 8.0` (`net8.0`, C# 12), pinned by `global.json` (`8.0.x`, `rollForward: latestFeature`)
- **Godot Engine:** `Godot 4.7.2-stable (.NET / Mono edition)` (`Godot.NET.Sdk/4.7.2`)
- **Test Runner:** `xUnit 2.9.2`

## What is verified

- `dotnet test` runs **203 tests** (about 11 s), including audit regressions for every reproduced
  finding in `docs/AUDIT_HANDOFF_2026-11-06.md`. See `docs/TESTING.md` for the file-by-file map and
  the gates outside the unit suite.
- **Determinism:** a real recorded-command replay re-runs every recorded decision into fresh engines
  and matches the original match tick-for-tick (ball, players, score, phase, clock, metrics).
- **Presentation:** the ball is drawn from the same radius physics uses (0.11 m), the camera smooths by
  elapsed time (identical response at 30/60/144 FPS) and the fixed-step clock bounds and reports
  catch-up after a long frame. Both scenes are booted in Godot as part of verification.
- **Configuration:** invalid match setup, pitch geometry, ball parameters or player attributes are
  rejected at construction with the offending field named instead of failing later inside a match.
- Not verified automatically: how the controls, camera and shooting **feel**. That needs a playtest.

## Fast Start

Double-click `start.bat` (or run `start.bat` from a shell) to build the game assembly and launch the
Godot client. Use the extension when typing in cmd, because `start` alone is a cmd built-in.

| Command | Action |
| :--- | :--- |
| `start.bat` / `start.bat play` | build the game assembly, then launch Godot |
| `start.bat check` | build the solution, then run the test suite |
| `start.bat test` | run the test suite only |
| `start.bat verify` | build + test suite + audit probe JSON |
| `start.bat build` | build the solution only |
| `start.bat match` | headless 11v11 match |
| `start.bat 5v5` | headless 5v5 match |
| `start.bat lab` | ball physics lab (writes a CSV) |
| `start.bat probe` | audit probe (writes `artifacts/audit-probe-latest.json`) |

Godot is located from `%GODOT_EXE%` first, then the pinned path
`C:\Games\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe`, then `godot.exe` on
`PATH`. Set `GODOT_EXE` to use a different install.

## Main Menu

The game boots into a menu (`MainMenu.tscn`):

| Option | What it does |
| :--- | :--- |
| **Match** | Pick a position to control — or **None** for a full 11 v 11 AI simulation — and the half length. The default position is Striker (#9) and the default half length is **4 minutes** (8 minutes for the full match). |
| **Practice** | Drills: Free Training, Free Training with Goalkeeper, Passing & Receiving 1 and Passing & Receiving 2. Practice uses the full match pitch, and its broadcast camera stays centred on the trainee. |
| **Settings** | Window resolution plus editable keyboard **and gamepad** bindings. Gamepad A selects, B goes back. Everything persists in `user://settings.cfg`. |
| **Exit** | Closes the game. |

`Esc` returns to the main menu from a match or a training drill. The match HUD shows the chosen
half length and whether you are controlling a player or spectating.

## Practice Drills

| Drill | Players | Rule |
| :--- | :--- | :--- |
| **Free Training** | 1 player + goal | Empty pitch; spawn just outside the box. A goal or an out-of-play ball respawns the player and ball; the ball settles in the net instead of rebounding. |
| **Free Training with Goalkeeper** | 1 player + AI GK + goal | Same as Free Training, with a goalkeeper defending the target goal. |
| **Passing & Receiving 1** | 2 players | The AI teammate passes to you; pass it back and it passes again. Nothing respawns on reception — only a goal or an out-of-play ball. |
| **Passing & Receiving 2** | 2 players | The AI holds the ball until you press **Pass** to call for it. The pass consumes the call; pass it back and call again. Only a goal or an out-of-play ball respawns. |

New drills implement `TrainingDrill` and register in `TrainingDrillRegistry`; the menu, session and
reset loop pick them up automatically.

## Build & Run Commands

1. **Build the entire solution & run all Phase 0–9 Gate Tests:**
   ```powershell
   dotnet test tests/FootballSim.Tests/FootballSim.Tests.csproj
   ```

2. **Run Phase 1 Ball Physics Lab (generates trajectory CSV):**
   ```powershell
   dotnet run --project src/FootballSim.Headless -- --physics-lab
   ```

3. **Run Headless 5v5 or Full 11v11 Match:**
   ```powershell
   dotnet run --project src/FootballSim.Headless -- --match-5v5
   dotnet run --project src/FootballSim.Headless -- --match-11v11
   ```

4. **Run Phase 6 & 9 Multi-Seed Tactical Differentiation Batch:**
   ```powershell
   dotnet run --project src/FootballSim.Headless -- --tactical-batch
   ```

5. **Launch 3D Visual Match in Godot 4.7.2 .NET:**
   ```powershell
   godot_v4.7.2-stable_mono_win64.exe --path src/FootballSim.Godot
   ```

## Controls Guide (defaults — rebind them in Settings)

| Action | Keyboard | Mouse | Gamepad |
| :--- | :--- | :--- | :--- |
| **Movement (and aim)** | `W`/`A`/`S`/`D` or `Arrow Keys` | — | `Left Analog Stick` / `D-Pad` |
| **Cursor Aim** | — | Cursor Screen Raycast (mouse users only) | — |
| **Sprint / Trivela modifier** | `Shift` (Left or Right) | — | `Right Trigger (RT)` |
| **Shield Ball** | `Ctrl` | — | `Left Bumper (LB)` |
| **Curve / Finesse Modifier** | `Q` (or `Alt`) | — | `Left Trigger (LT)` |
| **Pass (Ground / Driven)** | `Space` | `Left Click` | `Button A` (South) |
| **Shoot** | `F` | `Right Click` | `Button B` (East) |
| **Chip Shot modifier** | `V` | — | `Right Bumper (RB)` |
| **Trivela Shot** | `Shift` + Shoot | — | `RT` + Shoot |
| **Lofted / Through Pass** | `E` | — | `Button Y` (North) |
| **Cross** | `C` | — | `Button X` (with the ball) |
| **Tackle** | `R` | — | `Button X` (without the ball) |
| **Camera Zoom** | `=` / `-` | `Scroll Wheel Up / Down` | — |
| **Tactical Anchors** | Hold `Tab` | — | Hold `Back` (gamepad) |
| **Main Menu** | `Esc` | — | — |

Keyboard bindings are edited in **Settings → Controls → Keyboard**, gamepad bindings in
**Settings → Controls → Gamepad** (trigger axes LT/RT can be bound as well as buttons). Gamepad X is
contextual: Cross while you have the ball, Tackle while you do not. Movement (left stick / D-pad),
arrow keys and `Alt` are fixed. Mouse buttons stay wired to pass/shoot. Every change is saved
automatically; older settings files migrate to the current gamepad defaults.

**Aiming:** the aim is the movement direction (left stick / WASD) — the ball follows where you steer.
The right stick is **not** an aim input. Mouse users without a gamepad connected can also aim with
the cursor (active for about two seconds after the mouse moves or while a button is held). The cyan
arrow shows the resolved aim, not the body facing. A shot follows that aim to the goal line and is
clamped just inside the posts; only a player standing still with no directional input gets the
assisted "corner away from the keeper" shot.

**Shooting (FIFA-style):**
- Power controls both speed and height. A quick tap is a low driven shot; the sweet spot is about
  **80–90 %** (just under the crossbar); a full-power **100 % shot deliberately clears the bar**.
- **Finesse** (`Q` / `LT` + shoot): a curler that bends back toward the goal from the corner you aim
  at; it uses the same power/height rule.
- **Chip** (`V` / `RB` + shoot): a lofted shot that rises over the keeper and drops under the bar.
- **Trivela** (`Shift` / `RT` + shoot): an outside-foot curler with a flatter, faster bend.
- **Foot angle:** every player has a strong foot (left-footed shirts 3, 7 and 11 on both teams).
  Running across the goal so you strike to your strong side is cleaner; shooting across the body to
  the far post is an outside-foot/weak-foot contact with a visible accuracy penalty
  (`OffFoot` in the inspector). Angle your run before shooting to put the ball on the good foot.
- Only one kick button charges at a time — holding a second one is ignored until the first is
  released. The in-match HUD prints the live keys from the same binding table the input reader uses.

**Shielding and body angle:** holding **Shield Ball** while on the ball makes the carrier use their
body: the opponent has to win a real tackle instead of a nudge (contesting scores drop), but the
carrier moves noticeably slower while shielding. Kicks are judged against the body facing *before*
the kick turns you, so a kick struck behind you (for example while sprinting away from the ball) is
less accurate than one in front — the inspector shows `Align=…%` and `OffFoot`/`StrongFoot`.

## Configuration

- **Match setup** is validated before kickoff: team size must be 1–11, the half length must be finite and
  positive, the designated human id must belong to that match, and pitch geometry must be internally
  consistent (goal narrower than the pitch, penalty area inside the half, …). Bad values raise
  `ArgumentOutOfRangeException` naming the field instead of producing a broken match.
- **Player attributes, tendencies and team profiles** are validated too, so a non-finite or
  out-of-range value cannot silently disable an AI behaviour.
- **Debug/inspector settings:** the camera zoom is clamped to 18–65 m, and the inspected player is
  looked up by id with a fallback into the real squad (a 5-a-side match does not have a player #9 index
  assumption).

## Specification references

The proposed advanced AI roadmap is in [docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md](docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md), based on `deep-research-report.md` and the current implementation. Its milestones describe future work.

Older documents (and the audit) cite numbered design sections such as `Section 0`, `Section 3.1`,
`§3.3` or `Phase 0–9`. That numbering belongs to the original design brief, which is **not part of this
repository**; treat those labels as historical, not as promises of a document you can open. The
authoritative references are: this README (product behaviour and controls), `docs/ARCHITECTURE.md`
(design and module boundaries), `docs/TESTING.md` (what is covered and how), `docs/KNOWN_ISSUES.md`
(residual limitations), `docs/AUDIT_HANDOFF_2026-11-06.md` (the 2026-11-06 audit) and `TASKS.md` /
`PROGRESS.md` / `RESUME.md` (work state and per-batch evidence).

