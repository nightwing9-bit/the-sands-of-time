# Spec 06 — Ghost Lap V1: deterministic greybox and first solo ghost

**Basket:** 2 | **Dependencies:** spec-02, spec-03, spec-04 | **Owner paths:** `src/GhostLap`, `src/GhostLap.Contracts`, `web/apps/ghost-lap` greybox, `tests/GhostLap`

## User value and outcome

A server-owned deterministic 90-second arena with real movement/candles/charge/contact/Burst/score, a recorded original solo run and fork/ghost proof via the same Marrow Timeline.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No network rooms with remote players yet (10), durable share UI (11/12), final art (13), AI adversaries, multiplayer history archives.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R06.01 — Deterministic seed.** WHEN a known seed/rules/layout version initializes the arena, THE SYSTEM SHALL produce fixed world geometry and tick-indexed candle opportunities independently of player decisions.
**R06.02 — Movement and score.** WHEN a living player submits legal bounded movement, THE SYSTEM SHALL move/collide under C# fixed-step rules, normalize diagonal, and earn distance only from authoritative post-collision motion.
**R06.03 — Candle charge.** WHEN a player contacts a valid active candle, THE SYSTEM SHALL collect stable ID once per living world, award pickup score and bounded 0–4 Soulfire with readable charge state.
**R06.04 — Contact and Burst.** WHEN live simulation has players of different charge or a charged Burst owner, THE SYSTEM SHALL resolve contact damage/cooldown and radial Burst server-side with deterministic sorted same-tick events.
**R06.05 — Finish.** WHEN 90-second default duration reaches final tick, THE SYSTEM SHALL finalize one score-breakdown result with total-score winner/tie semantics, including last-tick scheduled effects.
**R06.06 — Original solo.** WHEN a full solo game completes, THE SYSTEM SHALL record every tick including empty steps, full versioned checkpoints and a verified immutable original history.
**R06.07 — Restoration.** WHEN a saved solo history seeks to a valid tick with nonzero charge or prior pickups, THE SYSTEM SHALL restore charge, score, position, cooldown, active/collected candles and future schedule identically.
**R06.08 — Branch.** WHEN a user forks at nonterminal T and changes subsequent inputs, THE SYSTEM SHALL create independent living world from exact historical T while source ghost continues its original trace without collision/pickup.
**R06.09 — Comparison.** WHEN source and current worlds reach aligned ticks, THE SYSTEM SHALL show independent scores and signed delta for same time, not final score until final.
**R06.10 — Greybox controls.** WHEN a user opens Ghost Lap on browser, THE SYSTEM SHALL move and Burst using basic keyboard and see whole 16:9 arena, candles, score, charge and real shared scrubber; placeholders clearly allowed.
**R06.11 — Canonical input and score restoration.** WHEN inputs are missing/late or a full-state checkpoint is restored during motion, THE SYSTEM SHALL replay the recorded effective per-tick input/Burst edge and preserve cumulative fixed-point distance/remainder so next-tick score equals uninterrupted execution.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

User plays full greybox solo, reloads original, rewinds at an already-collected-candle/nonzero-charge tick, forks, witnesses independent inert ghost and aligned score delta. The cross-app shared-engine proof belongs to the **B2 basket gate after both Specs 05 and 06**; Spec 06 can exit independently.
