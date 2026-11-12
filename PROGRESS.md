# PROGRESS.md — completed work log

Newest entries first. One entry per completed task or batch. Keep entries short and factual: what
changed, where, and what proves it. Details that a future session needs go in `RESUME.md`.

---

## 2026-11-12 — Session 1, batch 0: repository bootstrap

**Goal of this batch:** make the repository a self-describing, versioned working environment before any
AI code changes.

- **S1.1 Repo initialised.** `git init` on `master`, remote `origin` →
  `https://github.com/SofckJaded/Football-Sim.git`. Repo-local identity set (no global git identity was
  configured on this machine).
- **S1.2 `.gitignore` rewritten.** Now covers .NET build output (`bin/`, `obj/`, `*.dll`, `*.pdb`,
  `*.exe`, NuGet caches), IDE state, Godot (`.godot/`, `*.translation`, `*.import`,
  `export_presets.cfg`), test/coverage output, logs, OS junk, and the stale duplicated source tree under
  `artifacts/baseline-*/`. Recorded evidence under `artifacts/` stays versioned.
- **S1.3 `AGENTS.md` added.** Durable operating contract: toolchain commands, batching protocol, tracker
  protocol, architecture rules that are easy to violate, codebase map, definition of done, style, git.
- **S1.4 Tracker files created.** `TASKS.md` (A0–A10 backlog transcribed from the plan, plus cross-cutting
  obligations), `PROGRESS.md` (this file), `RESUME.md` (checkpoint).
- **S1.5 Baseline verified.** `dotnet test tests\FootballSim.Tests` → **191 passed, 0 failed, 13 s**
  (2026-11-12, .NET SDK 8.0.426, Debug). README still claims 180 tests; corrected later with A0.1 evidence.

`docs/ADVANCED_AI_IMPLEMENTATION_PLAN.md` left untouched — it is the source of truth for scope.
