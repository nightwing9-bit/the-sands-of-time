# Spec 02 design — Marrow Journal, Simulation and Timeline

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-01. **This spec owns:** `src/Marrow.Timeline`, shared canonical codec under `Marrow.Kernel`, `src/Marrow.Infrastructure` Timeline adapter, `tests/Marrow`. **Do not implement:** No finance/business rules, no game rule engine, no browser scrubber, no cross-domain event-payload classes.

**Consumes:** Public contracts from spec-01; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Contract authority and ordering

Implement the minimal public contract and lifecycle semantics in `docs/architecture/03-contracts.md`; `spec-02` owns actual compilable C# signatures and canonical serialization. One `TimelineId` addresses both sources and branches; typed `Result`/error codes cover create, append, complete, restore, replay, fork and aligned compare. Position 0 is the full initial checkpoint, first applied step is 1, and append is contiguous; a `StepRecord<TInput>` exists even with zero inputs. Order inside each step by `(Ordinal,InputId)`, reject duplicate `InputId` and conflicting retries; equal ordinals use deterministic `InputId` tie-breaking unless a domain adapter requires stricter rules. Finance business-time metadata is **not** the source sort key. The creation receipt is valid only once the owner, pinned context/codec versions, initial checkpoint and hashes commit durably. Recording manifests have controlled mutable progress/status; historical inputs/checkpoints remain immutable.

Design the **single** Timeline-owned `ITimelineStore` persistence port (its implementation may use private `JournalStore` classes) for manifest, steps, complete checkpoints, pinned context artifacts and branches; do not create a competing domain-facing journal lifecycle API or a generic EF repository. Spec 02 owns only `marrow_timeline` migrations, run via the sole Spec-01 migration executor. Each append atomically verifies expected previous position, canonical input IDs and append chain, updates controlled manifest progress and persists the step/checkpoint as required. Immutable steps and checkpoints cannot be UPDATEd/DELETEd by runtime roles. Branch record stores `(child_timeline_id,parent_timeline_id,fork_position,operation_key,child_context_reference)` and only its own suffix. Resolve parent prefixes read-only, bounded in depth or normalized as an implementation detail; measure checkpoint spacing for game ticks versus finance events without a universal game tick assumption. Avoid N+1 per-step DB I/O.

Canonical determinism: domain **state, input and context** codecs serialize all relevant data with stable field/order/number rules. Hash SHA-256 of `(stateSchemaVersion || canonicalStateBytes)` separately from journal chain `H(previousHash || canonicalStepEnvelope)`. Persist canonical pinned `TContext` bytes or a durable content-addressed artifact reference, with context schema/hash and registered resolver/transition version; cold restore must verify and recover the exact historical context and old reducer instead of current settings. A fork inherits prefix state reconstructed with each ancestor's pinned context; its child context/reducer applies **only after** the fork. Child-state compatibility is checked before first child transition. Small finance policy/model references may point to separately retained immutable content with verified hashes; no general artifact platform. At arbitrary T, a reconstructed digest plus verified chain/anchors is not falsely advertised as an independently stored per-tick state hash. The engine returns position-aligned verified states; adapters calculate outcome metrics, never Timeline.

### Transaction and side-effect safety

Reducers are pure, not injected with EF, timers, Redis or model services. Cancellation propagates at bounded intervals; persistence writes only manifest/steps/checkpoints/context/branch metadata, never domain business postings. Fork requires a completed verified source in v1, restores *all* state at T and creates a recording child (sharing the immutable prefix); do not copy partial mutable source references. **Append on the child uses the same `AppendAsync` path as a source**; an optional streaming `ContinueAsync` orchestration helper delegates to it. A partial batch/cancellation may leave committed recording steps but never a misleading `Verified` status. Only `CompleteAsync` verifies and finalizes. Idempotent retries dedupe operations and reject changed payload. Mark irrecoverable history `Failed` with auditable status, never silently delete or rewrite history.

### Required two-reducer integration proof

Test adapter A: integer tick world with timer/cooldown/collected-ID set and **empty ticks**; adapter B: event-driven synthetic counter with nonuniform business timestamps and same-time different ordinals. Property tests generate seeds/input streams/fork T/sibling suffixes and assert prefix state hash equality and zero source mutation, including after cancellation. This checks **genericity**, not yet functional applications.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- FsCheck sequence: Restore(T) reproduces canonical deterministic state for all valid T; compare to stored hashes **where persisted**, and to verified full replay where not (no unsupported claim of a stored hash at every tick)
- Original bytes, row count and journal chain unchanged after any sibling branch
- Missing/gapped step or duplicate/conflicting input identity; missing/wrong input, state, context or reducer version => typed `InvalidPosition`/`Conflict`/`IncompatibleVersion` or `CorruptHistory` as applicable
- Concurrent same fork key => one branch; same key changed override => conflict
- Corrupted stored checkpoint causes explicit failure; cancellation leaves incomplete branch
- Fresh-create at position 0; append first/last/empty step; complete; child fork/restore/replay by the *same* `TimelineId` type; typed errors through every public operation
- Cold-process restart resolves original context/input bytes, verifies their hashes, replays source under v7-like context, child prefix under source context and suffix under changed v8-like context; missing artifact fails closed
- Aligned compare for complete source and verified prefix of a recording child; domain metric calculation remains outside engine

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong reasoning for codec/contracts and immutable persistence; economical for local wiring.
