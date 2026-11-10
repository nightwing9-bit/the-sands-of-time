# Spec 10 — Ghost Lap V2: authoritative 2–4-player multiplayer and rooms

**Basket:** 4 | **Dependencies:** spec-03, spec-06 | **Owner paths:** `src/GhostLap` room/session loop, `src/Hosts.Realtime`, multiplayer API and contracts, `tests/GhostLap/Multiplayer`

## User value and outcome

One authority per room, true 2–4 remote players, fair room/lobby lifecycle, real interactive matches and idempotent final result persistence.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No permanent full multiplayer input archive, no ghost as live rival, no multi-replica tick ownership without fencing, no client-written score.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R10.01 — Real lobby.** WHEN a guest creates/joins valid invitation room, THE SYSTEM SHALL enforce 2–4 active competitor slots and separate read-only spectators with owner-controlled membership.
**R10.02 — Ready/start.** WHEN 2–4 valid ready players request start, THE SYSTEM SHALL server commit seed/rules/layout/spawn sockets and count down before exactly one active tick loop.
**R10.03 — Authority.** WHEN a client submits movement/Burst input, THE SYSTEM SHALL validate slot, sequence, tick window and rate, never accept position/score/hit claims.
**R10.04 — Deterministic simulation.** WHEN real 2/3/4-player match runs, THE SYSTEM SHALL reuse spec-06 GameRules engine for walls, candles, collisions, contact, Burst, authoritative score and terminal tick.
**R10.05 — Room single owner.** WHEN multiple starts/reconnects are attempted, THE SYSTEM SHALL retain at most one active tick loop with explicit lifecycle fence in single Realtime replica.
**R10.06 — Connectivity.** WHEN an active user disconnects, THE SYSTEM SHALL use neutral input and grace window, valid reconnection restores snapshot/sequence, expired grace records clear forfeit policy.
**R10.07 — Result finalization.** WHEN match expires or finalization retries, THE SYSTEM SHALL persist one roster/score/stat/placement result and one outbox event without double rating trigger.
**R10.08 — Rematch.** WHEN finished roster votes/request rematch, THE SYSTEM SHALL create fresh match ID/seed and reset game state without leaking old score/charge.
**R10.09 — Spectator restraint.** WHEN a spectator calls any gameplay command, THE SYSTEM SHALL deny at hub and app service; display state without changing world.
**R10.10 — Fairness.** WHEN 2,3,4 participants spawn, THE SYSTEM SHALL spawn assignments and candle reachability pass seed/fairness checks before match start.
**R10.11 — Effective input.** WHEN a connected player omits input, sends a stale command, disconnects or retries Burst, THE SYSTEM SHALL use the versioned Spec-06 effective-input/latch rule without phantom score/Burst; resumed snapshots show accepted sequence.
**R10.12 — Crash-safe result claim.** WHEN final-result persistence fails or the authoritative room dies without a committed result, THE SYSTEM SHALL show a bounded pending outcome then `Aborted/Unrated` (no rating event), **using a minimal committed match-start identity receipt**, unless a valid durable result already exists; it shall never fabricate a winner or rating.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

Actual 2–4 browser/socket clients play 90s authoritative session through result/rematch; spectator denied control; reconnect and single-match-result tests pass.
