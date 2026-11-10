# Spec 02 — Marrow Journal, Simulation and Timeline

**Basket:** 1 | **Dependencies:** spec-01 | **Owner paths:** `src/Marrow.Timeline`, shared canonical codec under `Marrow.Kernel`, `src/Marrow.Infrastructure` Timeline adapter, `tests/Marrow`

## User value and outcome

One real generic versioned append/restore/replay/fork/continue/comparison engine with complete snapshots and immutable parent prefix, independently proven by two test reducers.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No finance/business rules, no game rule engine, no browser scrubber, no cross-domain event-payload classes.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R02.01 — Ordered source.** WHEN a source step is appended, THE SYSTEM SHALL accept only the expected next logical position, with stable input ordinals/IDs, and reject gaps, reordering, or conflicting duplicate IDs.
**R02.02 — Cold restore.** WHEN a prior position T is requested, THE SYSTEM SHALL load nearest compatible complete checkpoint and replay every step through T with verified canonical state hash.
**R02.03 — Full replay.** WHEN an immutable history replays with its version/seed, THE SYSTEM SHALL final state hash and source journal hash-chain verify against originally recorded history.
**R02.04 — Fork exactness.** WHEN a branch is created at any legal position including zero, THE SYSTEM SHALL its verified source-prefix state hash at fork equals source T and source content remains unchanged.
**R02.05 — Suffix isolation.** WHEN two sibling branches receive distinct future inputs, THE SYSTEM SHALL they persist independently and neither change the source or one another.
**R02.06 — Idempotency.** WHEN the same fork operation key retries, THE SYSTEM SHALL return same branch identity; conflicting payload for reused key returns conflict.
**R02.07 — Cancellation.** WHEN a replay job is cancelled or crashes, THE SYSTEM SHALL no partially continued history is marked verified and source remains immutable.
**R02.08 — Incompatibility.** WHEN codec, reducer, pinned context, checkpoint or hash is unavailable or corrupt, THE SYSTEM SHALL return explicit incompatible/corrupt result rather than default current-version results.
**R02.09 — Domain neutrality.** WHEN the test suite executes a tick reducer and an event reducer, THE SYSTEM SHALL both run through exact same engine code without finance/game domain dependencies.
**R02.10 — Aligned compare.** WHEN a caller requests matching positions from the source and a branch, THE SYSTEM SHALL return verified aligned reconstructed states/positions or typed errors without assuming game score or currency; the domain adapter then applies its own pure outcome projector.
**R02.11 — Source lifecycle and single identity.** WHEN a source is created, appended and completed, THE SYSTEM SHALL durably establish position-0 state, append only contiguous positions and return an addressable `TimelineId` for any source or child branch, with typed success/failure for all public operations. A `Recording` history is not a completed `Verified` history.
**R02.12 — Cold version recovery.** WHEN a process restarts, THE SYSTEM SHALL decode recorded inputs and load/verify the original pinned context and compatible reducer from durable versioned data; missing versions return `IncompatibleVersion` rather than using current defaults.
**R02.13 — Prefix/suffix context.** WHEN branching under alternative rules/policy, THE SYSTEM SHALL reconstruct the inherited prefix using its historical ancestor contexts through T and apply the child context only to T+1 onward; incompatible state/context fails closed.
**R02.14 — Precise positions and errors.** WHEN restoring/comparing/forking, THE SYSTEM SHALL use position 0 as the pre-step initial state, steps start at 1, forks continue from T+1, and all promised error cases map to typed failures, including invalid final-position game actions at the domain boundary.
**R02.15 — No duplicate persistence API.** WHEN a child continues, THE SYSTEM SHALL use the same serialized append/checkpoint path as a source; browser/domain consumers do not directly invoke a second `IJournalStore` write authority.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

One `Marrow.Timeline` assembly and real Postgres store pass both event-ordered and tick-ordered reducers, arbitrary valid T, hashes, branch isolation, corrupt history and cancellation checks.
