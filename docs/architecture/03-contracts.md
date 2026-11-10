# Shared contracts and frozen invariants (architecture authority)

This is the one authoritative cross-module decision registry for exact **conceptual** contract shape; `spec-02` creates the corresponding compilable types. Private method names may vary but **semantics cannot** change without an ADR and dependent-spec compatibility review.

## Marrow Timeline API v1

**Contract scope:** conceptual C# shape and **frozen observable semantics**. Spec 02 must compile the minimal equivalent types; private data-access methods may vary. `ITimelineEngine` is the **one domain-facing lifecycle authority**; `ITimelineStore` is its persistence port, **not** a second public API. One addressable `TimelineId` identifies both original sources and child branches. The UI may label child IDs `branchId`, but they are the same underlying history identity; do not create an unrelated `BranchId` that cannot be restored.

```csharp
public readonly record struct TimelineId(Guid Value); // source OR child branch
public readonly record struct TimelinePosition(long Value); // >= 0; browser wire: decimal string
public enum HistoryStatus { Recording, Verified, Failed }
public enum TimelineErrorCode {
  NotFound, Forbidden, InvalidPosition, IncompatibleVersion,
  CorruptHistory, Conflict, Cancelled
}
// Conceptual discriminated success/error result; actual implementation uses the
// existing dependency-free Kernel Result conventions, never exception-only APIs.
public record TimelineResult<T>(T? Value, TimelineErrorCode? Error);

public record HistoryManifest(
  TimelineId Id, string DomainId, string OwnerId,
  string InputSchemaVersion, string StateSchemaVersion,
  string ContextSchemaVersion, string ContextHash, string ContextArtifactRef,
  string ReducerVersion, string RulesHash, string SeedHash,
  long LastPosition, string LatestStateHash, string LastRecordHash,
  TimelineId? ParentId, long? ForkPosition, HistoryStatus Status);
public record StepRecord<TInput>(long Position, IReadOnlyList<OrderedInput<TInput>> Inputs);
public record OrderedInput<T>(string InputId, int Ordinal, T Payload);

public interface IDeterministicTransition<TState,TInput,TContext> {
  TState Apply(TState previous, long position, IReadOnlyList<OrderedInput<TInput>> inputs,
               TContext pinnedContext);
}
public interface IStateCodec<TState> {
  byte[] EncodeCanonical(TState state); TState Decode(ReadOnlySpan<byte> bytes);
}
public interface IInputCodec<TInput> {
  byte[] EncodeCanonical(TInput input); TInput Decode(ReadOnlySpan<byte> bytes);
}
public interface IContextCodec<TContext> {
  byte[] EncodeCanonical(TContext context); TContext Decode(ReadOnlySpan<byte> bytes);
}
// Stable schema/reducer resolver selects the historically compatible codec and
// transition implementation; a missing historical implementation fails closed.

public interface ITimelineEngine<TState,TInput,TContext> {
  Task<TimelineResult<TimelineId>> CreateSourceAsync(
    CreateSourceRequest<TState,TContext> source, string operationKey, CancellationToken ct);
  Task<TimelineResult<AppendReceipt>> AppendAsync(
    TimelineId history, long expectedLastPosition, StepRecord<TInput> nextStep,
    string operationKey, CancellationToken ct);
  Task<TimelineResult<CompletionReceipt>> CompleteAsync(
    TimelineId history, long expectedLastPosition, CancellationToken ct);
  Task<TimelineResult<VerifiedState<TState>>> RestoreAsync(
    TimelineId history, long position, CancellationToken ct);
  Task<TimelineResult<VerifiedReplay<TState>>> ReplayAsync(
    TimelineId history, CancellationToken ct);
  Task<TimelineResult<TimelineId>> ForkAsync(
    TimelineId source, long forkPosition, string operationKey,
    TContext pinnedBranchContext, CancellationToken ct);
  Task<TimelineResult<IReadOnlyList<AlignedState<TState>>>> CompareAlignedAsync(
    TimelineId source, TimelineId alternative, IReadOnlyList<long> positions,
    CancellationToken ct);
}
```

`CreateSourceRequest`, receipts and verified-state/alignment DTOs are small Spec-02-owned types representing the initial complete canonical state, validated actor/owner, domain/reducer/schema references, pinned source context and committed position/hash/verification metadata. The interface above is a **minimal conceptual contract**, not permission to generate another generic query framework. **Continue a child by calling the same `AppendAsync` on its `TimelineId`**; a streaming `ContinueAsync` helper is optional and must delegate to this single append/verification path, not create a second write authority. A branch and source may be read with the same `RestoreAsync`/`ReplayAsync` calls.

**Lifecycle and positions:** `CreateSourceAsync` atomically writes manifest, **complete initial checkpoint at position 0**, pinned context artifact and verification metadata before returning success. The first applied transition is step **1**; each subsequent step, including zero-input steps, has position previous+1. `AppendAsync` requires `nextStep.Position == expectedLastPosition+1`, optimistic expected-last check and idempotency on `(history,operationKey)` with payload conflict rejection. `CompleteAsync` validates contiguous history, record chain and terminal state/hash before moving `Recording → Verified`; a failed or cancelled partial history never reports `Verified`. Source records and checkpoints are append-only; lifecycle metadata (`LastPosition`, `Status`, hashes) may advance by controlled transactional updates. `RestoreAsync(id,T)` returns the state **after applying exactly steps 1..T** and requires `0 <= T <= LastPosition`; verified-prefix restoration of a recording history does not imply that the **whole** history has completed. A completed game source has final state at 2700; game adapter may forbid starting a playable continuation from 2700, while core restoration still permits inspection. Fork at T shares verified source steps through T and appends child steps from T+1; no copy/rewrite. The initial branch state is the complete restored source state at T, not a reset. `ForkAsync` is idempotent by `(owner,source,T,operationKey)` and rejects changed override content for the same operation key. For v1, branching requires a complete verified source; a branch may be continued in recording state and finalized separately.

**Durable context and version resolution:** Context hashes cannot reconstruct context. Store immutable canonical `TContext` bytes, or an immutable content-addressed artifact reference and its verified bytes, plus `ContextSchemaVersion`, reducer version, input/state versions and hash **before** acknowledging source or fork creation. Large versioned model artifacts can be stored separately by the finance adapter using stable hash-verified references; do **not** create a universal artifact registry. On cold restore/replay, a registered codec/resolver loads and validates the original historical versions and context; never read current runtime defaults. For parent-prefix positions, resolve each ancestor segment using **that segment's own pinned context and reducer**; use the child's new context only on its suffix. The adapter must validate that restored state is compatible with the child's continuation context at T. Missing old bytes, model, codec or reducer returns `IncompatibleVersion` (corrupted canonical bytes/hash return `CorruptHistory`) without changing historical data. Input records use their own pinned input codec; state codec alone is insufficient for recovery.

**Journal and integrity:** `ITimelineStore<TInput>` is a Timeline-owned internal/implementation-facing journal/checkpoint port (manifest, immutable ordered steps, context artifacts, checkpoints and prefix references); its exact storage method names are not domain APIs. Enforce unique `(timeline_id,position)`, `(timeline_id,input_id)` and transactional optimistic append. Order each step by `(Ordinal,InputId)` with unique `InputId`; equal ordinals are permitted when deterministic `InputId` tie-breaking applies, unless a domain's stricter input policy disallows them. A `StepRecord` may have **zero inputs**. Journal record chain `H(previousHash || canonicalEnvelope)` is distinct from state hash `H(stateSchemaVersion || canonicalStateBytes)`. At arbitrary T, verified-chain + compatible deterministic reconstruction establishes a **verified reconstructed state**; do not claim an independently stored per-tick hash unless one exists. Compare returns pairs of aligned verified reconstructed states at requested positions (or typed errors); **domain adapters** compute fraud amounts, scores and chart metrics. Comparison of an unfinished branch may report verified prefixes only, clearly separate from completed result status. Never claim external notarization from a database-local hash chain.

## Security interfaces v1

`IActorContext` includes stable `SubjectId`, `PrincipalKind=Staff|Guest`, `TenantId?`, role/claims and authenticated status; domain authorization checks are `CanReadHistory(actor, history)` and command policy handlers. OwnerId from requests is never trusted. Staff: separate demo staff identities, rotating refresh cookie, access JWT, CSRF where cookie-authenticated and TOTP MFA for privileged actions. Guest: signed, remembered cookie with server-backed revocation/session, no registration forced; guest still needs authorization for owned histories and room membership. Explicit replay share tokens have revocation and expiry; **private is default**. Token/credentials never enter logs or committed files.

## Realtime envelope v1

`{ schemaVersion: 1, streamId, sequence: decimal-string, eventId, serverUtc, kind, payload }` with per-stream monotonic sequence. On gap/reconnection client fetches authoritative snapshot + current sequence and resubscribes; sequence numbers are not gameplay ticks. Realtime transport can repeat/drop messages; financial/event outcome authority resides in DB, match score authority in C# room loop. Consumers never rely on hub delivery for exactly-once business effects. Room owner is exactly one running loop on **one Realtime replica** for initial deployment.

## Outbox v1

`{ eventId, eventType, eventVersion, aggregateId, occurredUtc, payload, correlationId }` stored in the initiating module schema in the *same transaction* as business effect. Relay may retry; consumer inbox unique `(consumerId,eventId)`. Retry bounded exponential + dead-letter or operator-visible poison state. Dedupe boundary must include the same transaction as the consumer business write.

## Revenant domain contract v1

`PaymentIntent` immutable exogenous input (stable ID, sourceBank/account, destBank/account, `Money(currency, minorUnits)`, simulated truth label, eventTimeUtc, sequence, feature inputs); `Decision` derived screen/hold/reject/approve; `SettlementPosting` debit/credit entries, same-currency balanced per group; `PolicyRef` ('v7'/'v8', config hash, model artifact hash, feature schema hash); `FinanceProjectionState` complete balances, held reservations, payment statuses, feature windows, derived metrics and scenario clock. Versioned domain reducer advances **exogenous inputs**, not a feed of already-settled decisions. Policy v7/v8 differ by explicit threshold and/or decision rules. Commit ledger only through `Revenant` live processing; branch projector receives **no** live ledger write capability. USD synthetic Friday Ring first; other currencies represented exactly by integer minor units, no cross-currency conversion without explicit new rule. Reserve a Revenant-owned `ScenarioRunId` for each new seeded Friday Ring execution; stable exogenous identifiers and later durable payment/idempotency keys are scoped to that run. Different runs retain old ledger/audit history without deleting it. Live ledger writers and replay projections must eventually use distinct database permissions; Spec 07/09 own those details. A held fraudulent payment is **deferred at the horizon**, not automatically prevented. Post-fork recorded analyst actions require a pinned deterministic continuation rule chosen by the finance owner, not copied blindly or silently dropped.

## GhostLap domain contract v1

`GameRulesVersion=ghost-v1`, `LayoutVersion=arena-v1`, 30 ticks/second *target* and 2700 ticks per 90s match *initial default*. Stable game input `(tick,slot,sequence,moveX,moveY,requestBurst)`, with bounded values. `GameState` contains full current world (players pos/velocity/score/charge/Overlit deadline, pair cooldowns, active/collected candle IDs, pending actions, rules/layout/schedule/seed identifiers and needed RNG state). All deterministic coordinates use versioned fixed-point integer units; renderer may interpolate floating pixels but cannot score them. Source ghost trace is read-only historical projection, **not added to living-world actors**. Stable per-tick phase order: validate and sort slot inputs → move and compute authoritative distance score → expire/activate candle schedule → collect by stable candle ID and tie-break slot → contact pairs sorted by slot IDs → manual Burst first, then due auto-Burst (max one per owner/tick) → if terminal tick, finalize after tick effects. Candle active at `activate <= tick < expire`; completed tick 2700 is inspect-only fork boundary. A future rules change creates a new `GameRulesVersion` and retains old replay availability. Canonical `GameState` **also includes** the last effective input/pressed-state and connected-player missing-input policy state, plus the fractional fixed-point distance-to-score remainder; restoring mid-run must not discard either. Spec 06 owns the unique versioned input/state codec; Spec 11 consumes that codec and adds history catalog/branch workflows, never a second serialization authority. **Effective input v1:** For an actively connected player, a valid movement vector is held for at most **six authoritative ticks** without a newer accepted input, then becomes neutral; disconnection/blur explicitly neutralizes immediately. Accept only a newer per-slot sequence in the allowed tick window, reject stale/late inputs, and treat Burst as an edge-triggered request consumed at most once (never re-fire it because a vector is held). The server applies a canonical **effective** per-slot input on each tick, including explicit neutral/no-input ticks in solo histories. The canonical state/codec contains last accepted sequence, held direction/expiry, Burst-edge latch if needed, and cumulative post-collision movement distance (or equivalent precise remainder) so a checkpoint resumes identically near score-unit boundaries. The transport acceptance/window policy is owned by Specs 06/10 and pinned by rules version; UI prediction is never authoritative. Disconnected participants follow the server's neutral/forfeit rule, not an unrecorded browser assumption.

## Browser Timeline adapter v1

```ts
type TimelinePosition = string;  // decimal, never floating point
interface TimelineAdapter {
  sourceId: string; branchId?: string;
  bounds: {start: TimelinePosition; end: TimelinePosition};
  readOnly: boolean;
  getLabel(position: TimelinePosition): string;
  getMarkers(): {position:TimelinePosition;label:string}[];
  seek(position: TimelinePosition): Promise<VerifiedPreview>;
  fork(position: TimelinePosition, options: unknown, idempotencyKey:string): Promise<{branchId:string}>;
}
```

`unknown` stands for caller-provided **domain-specific options**, never a shared game/bank options schema. Shared component owns navigation, seek debounce/keyboard semantics, loading/incompatible/error state and disabled branch at invalid/final tick; domain adapters own rules, auth and visualization. Both products use same compiled package instance/source path; adapter files reside within each app.

## Change control

Only the owning spec or an explicit follow-on ADR may change a contract. Cross-domain breaking changes require compatibility review, generated DTO/client regeneration, architecture test and both end-to-end adapter tests. Never update only a producer or only a consumer. For targets like score coefficients/ML thresholds, pin a typed version and allow later **measured** tuning with a new version, not silent mutation.
