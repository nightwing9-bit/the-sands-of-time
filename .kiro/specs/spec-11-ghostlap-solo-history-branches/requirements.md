# Spec 11 — Ghost Lap: exact solo histories, Race Your Ghost and true timeline forks

**Status:** revised hackathon implementation specification; NOT YET IMPLEMENTED.  
**Priority:** the central Ghost Lap demonstration of the frozen Marrow Timeline.  
**Prerequisites:** completed Spec 02 Timeline, Spec 03 identity/hosts, Spec 06 deterministic solo `ghost-v1` reducer/codec and fixtures. Spec 10's multiplayer backend is completed separately (`ghost-mp-v2` for new matches). **Spec 12 is not a prerequisite.**  
**Ownership:** Ghost Lap solo application integration, owner-scoped history metadata *only if missing*, typed history/seek/branch APIs and focused tests. No Marrow public-API change, Revenant change, new multiplayer subsystem or browser art implementation.

## 1. Product outcome and strict scope

A guest completes a real **90-second / 2,700-tick solo original**, receives a truthful *Saved* result, reloads it later, watches their exact historical self, races that read-only ghost from the same initial conditions, selects a tick on a real Timeline, **restores the full canonical state**, starts a new future at **T+1** and compares both scores at the same tick. Multiple completed challenges/forks may be retained without changing the original. This is the reason the game proves Marrow, not an optional social feature.

**Keep all applicable solo gameplay physics:** fast resolved movement and wall handling; deterministic rapid candle activation/expiry/collection; all Soulfire stages 0–4; charge/timers; manual radial Burst and eight-second (240-tick) Overlit auto-Burst; the full `ghost-v1` scoring, event and collision rules. Do not simplify solo mechanics, fabricate an event or use sprite positions as the source of truth. The historical ghost is non-colliding/read-only; multiplayer opponent-contact rules do not invent a second living solo player.

**Deliberate scope cut:** an *unfinished* run interrupted by page close, process death or disconnected session may be explicitly discarded/restarted from tick 0. **No seamless committed-prefix resume, solo controller takeover generations, old/new writer fencing, multi-process solo attempt recovery or retrying an unfinished live match across restarts is required.** Do not silently keep accruing ticks or represent an interrupted run as complete. **Completed and verified histories remain durable, immutable and correct.** An already-committed completed source must never be replaced by a retry.

A solo attempt may be live-interactive via existing Spec 06 host/session integration or recorded via a bounded tick-input session and verified through the actual `ghost-v1` reducer at completion. The source-inspection gate chooses the simplest *real supported* option. Never introduce a second solo simulation or fictional Marrow method.

## 2. Requirements (EARS-style)

**R11.01 — Canonical source and tick contract.** WHEN a new solo run starts, THE SYSTEM SHALL pin the actual Spec 06 `ghost-v1` rules/codec/layout/seed/spawn and represent tick 0 as the initial state and ticks 1..2700 as exactly one deterministic step each. The real canonical Spec 06 reducer and frozen Timeline ports, rather than a copied browser physics engine, SHALL decide state, actions and scores. No multiplayer match log is a solo history.

**R11.02 — Complete real original.** WHEN a guest finishes all 2700 ticks of a genuinely played solo recording, THE SYSTEM SHALL generate/verify an immutable Timeline original using the existing canonical input/state codec, ordered inputs, required checkpoints and final state/hash. A submitted path, client-reported score or fake 2700-tick counter SHALL NOT substitute for canonical replay/verification. Incomplete runs never become originals.

**R11.03 — Truthful completed save.** WHEN an original/child is saved, THE SYSTEM SHALL return `Saved` only after the canonical Timeline source is verified and the owner-visible history reference is durably committed. A stable owner-scoped save operation/key SHALL prevent retry from creating a different visible history or overwriting an existing one; identical retry retrieves the prior outcome, conflict fails, ambiguous save returns explicit `Pending/Retryable` until resolved. Do not fabricate cross-database atomicity or implement a large background recovery system.

**R11.04 — Interrupted attempts.** WHEN an unfinished solo attempt is abandoned, disconnected or its live execution process fails, THE SYSTEM SHALL stop it without manufacturing a final score or a verified history. The player may begin a fresh run; any previously `Saved` completed histories remain intact. Graceful pause during a live session is optional; restart-across-process-death of partial attempts is explicitly **out of scope**.

**R11.05 — Private verified library.** WHEN the authenticated guest opens their history, THE SYSTEM SHALL return only that owner's verified completed originals and child attempts, with source/parent/fork kind, original seed/versions, tick, score, final hash, status and available play actions. Missing/corrupt/unsupported/pending sources are labeled honestly, not represented as playable `Saved` histories. No public listing, sharing token or expiry of saved originals is required.

**R11.06 — Exact seek.** WHEN the owner selects integer tick `T` from a completed verified original, THE SYSTEM SHALL use the existing full-state Timeline checkpoint+fold/restore semantics, verify source identity/hash, and return the state **after exactly T reducer steps**. This includes all reducer-significant score remainder, velocity/movement, candles, charge, Overlit/auto-Burst, effective input/edge, RNG and schedule/cooldown fields applicable to the pinned solo codec. Position-only snapshots, interpolated state, or final-score substitution are forbidden.

**R11.07 — Tick boundaries.** WHEN T is 0, 1140, 2699 or 2700, THE SYSTEM SHALL distinguish initial, midrun, penultimate and terminal state correctly. Any `0 <= T <= 2700` is inspectable; only `0 <= T < 2700` can be forked. A fork starts accepting different input at **T+1**, not at T. Invalid, noninteger or out-of-range ticks fail predictably.

**R11.08 — Genuine read-only ghost challenge.** WHEN a guest challenges an original, THE SYSTEM SHALL create a separate living solo run from its exact original tick-0 seed/rules/layout/spawn, while a verified historical trace replays the original at matching ticks. The ghost never blocks, picks up living candles, loses charge to the living player, triggers living-world Burst effects or earns living score. Completing a challenge stores a labeled child attempt, not a new original.

**R11.09 — Genuine independent fork.** WHEN an owner forks a selected *completed verified original* at eligible T, THE SYSTEM SHALL restore/hash-check its **complete** state at T and record a distinct child continuation via the actual frozen Timeline branch mechanism, inheriting the exact original prefix through T and persisting only a new independent continuation using supported canonical ports. The original hash, inputs and event history SHALL remain unchanged. Repeated branches at the same T create independent children. Nested child-of-child forks are out of hackathon scope.

**R11.10 — Same future opportunity; separate consumption.** WHEN ghost and living worlds share source seed and pinned rules, THE SYSTEM SHALL reproduce the original candle opportunity schedule (identities, activation ticks, positions and expiry) while preserving **separate collection/charge/pending-action state**. Divergence in the living world may change its consumption and score, not the ghost's verified future. Historical ghost effects are visual cues only.

**R11.11 — True time-aligned comparison.** WHEN the client requests a source/current comparison, THE SYSTEM SHALL provide canonical original/current position, score components, charge and tick-keyed gameplay cues at the **same integer tick**, with `delta(T) = currentTotal(T) - originalTotal(T)` and accurate completed totals. The history trace must support a continuously moving/seekable ghost and readable score progression; visual interpolation may smooth sprites but never change canonical scores, events or fork state.

**R11.12 — Version fidelity.** WHEN reading old histories, THE SYSTEM SHALL use their sealed solo `ghost-v1` reducer/codec/layout and existing goldens. `ghost-mp-v1`, `ghost-mp-v2` and `arena-v1` multiplayer profile changes SHALL NOT reinterpret solo history bytes. If the pinned solo implementation is unavailable or a source fails verification, fail closed with a typed `VersionUnsupported`/`CorruptHistory` state; do not replace old physics or alter Marrow.

**R11.13 — Ownership and limits.** WHEN history, trace, seek, original-save, challenge or fork is requested, THE SYSTEM SHALL authenticate the owner at the API/service boundary, authorize the exact source/child and reject foreign/guessed IDs. Bound input payload size, concurrent work/requests, tick ranges and history queries by measured practicality. Do not leak private original traces through raw Timeline IDs, spectator credentials or a Spec 12 public-sharing feature (there is no share feature in this scope).

**R11.14 — Production composition.** WHEN integrating with existing API/Realtime/Timeline and persistence, THE SYSTEM SHALL reuse actual DI, ports, migrations and least-privilege roles without changing shared Marrow/Revenant auth or runtime semantics. Do not add test-only production bridges, `/__e2e` endpoints, a new Redis queue/worker, duplicate state codec or separate permanent per-tick log.

**R11.15 — Demonstrated integrity and practical performance.** WHEN Spec 11 exits, THE SYSTEM SHALL have directly run a genuine original, cold reload, seek at tick 0/1140/2699/2700, full-state hash/replay, challenge, two independent forks, exact score alignment, privacy denial and a representative simultaneous solo-seek/multiplayer smoke test. Measure stored source size, cold seek/trace latency and any visible server tick impact; fix a measured bottleneck, not a hypothetical commercial-scale problem.

## 3. Invariants and acceptance boundary

- **Original:** `Replay(verified inputs, pinned ghost-v1).FinalHash == saved original final hash`; never modify source on challenge/fork. At any T, `Restore(original,T).Hash == canonical original state hash at T`.
- **Branch:** initial child state at T exactly equals source state at T; the first new action is T+1; source final hash remains constant; a second child is independent.
- **Ghost:** noninteractive and frame/tick-aligned with the original, even when the current player collects a candle differently or triggers Burst.
- **Real score:** source/current score curves come from replayed game state, not artist-authored animation values.
- **Save:** completed verified-and-linked only; pending/incomplete is not completed. User may retry an ambiguous save but cannot get a second conflicting visible original from the same key.
- **No expansion:** no resumable partial-run infrastructure, no MP full archive, no rating/leaderboard/chat/sharing, no new Marrow public API, no speculative multiroom certification matrix, no browser art/sound creation in Spec 11.

**Exit:** the complete original → save → cold reload → ghost race → exact fork at T → distinct verified child → same-tick comparison works with real stored data and no frozen-contract regression. Browser visuals/keyboard/audio and final scene fluidity will be tested separately when the actual game client exists; history data must be sufficient for them now.
