# Spec 11 — Ghost Lap V2: persistent solo histories, exact ghosts and branching

**Basket:** 4 | **Dependencies:** spec-02, spec-03, spec-06 | **Owner paths:** `src/GhostLap` solo application/catalog consuming Spec-06 codec, `ghostlap` schema, `web/apps/ghost-lap` domain adapters, `tests/GhostLap/Replay`

## User value and outcome

Durable owner-verified originals and repeated challenge/fork attempts at any valid tick, exact historical traces, independent candle worlds and time-aligned comparison.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No multiplayer replay archives, AI/simulated opponent, branchable MP sessions, ghost interaction with present world.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R11.01 — Original lifecycle.** WHEN first-time guest finishes real solo run, THE SYSTEM SHALL automatically persist one complete verified immutable original with owner/seed/rules/layout/spawn, tick inputs and checkpoints.
**R11.02 — Persist honest success.** WHEN a save fails or retry duplicates, THE SYSTEM SHALL not report saved until commit; return same original for identical idempotency key and expose recoverable error.
**R11.03 — History retrieval.** WHEN a remembered guest returns/reloads, THE SYSTEM SHALL list own originals, distinguish original/branch and retain source replayability under pinned code version.
**R11.04 — Full challenge.** WHEN a user challenges an original, THE SYSTEM SHALL start at same tick0 seed/rules/spawn, living/ghost visually overlap at zero and ghost trace remains read-only.
**R11.05 — Exact branch.** WHEN user chooses any T 0≤T<final tick, THE SYSTEM SHALL restore all authoritative source state (score, charge, Overlit, cooldowns, candles and RNG) and make independent branch suffix from T+1.
**R11.06 — Terminal edge.** WHEN user inspects final tick, THE SYSTEM SHALL permit read-only seek but disable branch because no ticks remain.
**R11.07 — Time aligned.** WHEN current and past progress, THE SYSTEM SHALL derive two score curves and signed delta only from source and current state at same tick.
**R11.08 — Version safety.** WHEN a source uses unsupported rules/codec, THE SYSTEM SHALL return incompatibility and preserve original bytes instead of reinterpreting with new rules.
**R11.09 — Privacy.** WHEN guest guesses different owner history, THE SYSTEM SHALL deny read/branch unless authorized owner or explicit supported read-only share.
**R11.10 — Branch family.** WHEN multiple children fork the same source position, THE SYSTEM SHALL persist independently with parent/source/fork metadata and unchanged original hash.
**R11.11 — Single codec authority.** WHEN an original/branch is saved or replayed, THE SYSTEM SHALL reuse the canonical versioned Spec-06 input/state codec, not create a second game serialization implementation.
**R11.12 — Verified catalog link.** WHEN a verified Timeline source is added to the GhostLap history catalog, THE SYSTEM SHALL commit a run/idempotency-keyed catalog link once, show saved only after verification/commit, and retry/relink safely after a failed catalog transaction without altering source history.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

Owner records full solo once, refreshes, repeatedly challenges, rewinds nonzero-state tick, creates multiple independent saved children, inspects true tick-aligned scores; hashes/privacy/version tests pass.
