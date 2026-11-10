# Marrow.Timeline — domain adapter usage

`Marrow.Timeline` is the one shared journal/restore/replay/fork/compare engine. A domain (Revenant, Ghost Lap) plugs in by supplying a pure reducer and three canonical codecs; it never writes Timeline tables directly. Contract authority: `docs/architecture/03-contracts.md`.

## What a domain adapter supplies

For one state/input/context shape `(TState, TInput, TContext)`:

| Piece | Interface | Rule |
| --- | --- | --- |
| Reducer | `IDeterministicTransition<TState,TInput,TContext>` | Pure. No clock, randomness, DB, network, Redis, model services or ambient settings. Everything comes from the previous state, the ordered inputs and the pinned context. |
| State codec | `IStateCodec<TState>` | Encodes **all** simulation-significant fields; equal states give identical bytes; `Decode` throws on malformed bytes. |
| Input codec | `IInputCodec<TInput>` | Versioned separately from state. |
| Context codec | `IContextCodec<TContext>` | Context bytes are stored durably; cold restore decodes them, never current settings. |

Use `Marrow.Kernel.Canonical.CanonicalWriter`/`CanonicalReader` for byte layouts (fixed-width integers, counted strings, `EnsureEnd`). Sort sets/maps by a stable comparer before writing.

Bundle them per version as `TimelineComponents<…>(TimelineVersionKey, reducer, stateCodec, inputCodec, contextCodec)`. The `TimelineVersionKey` (domain id, reducer, input, state and context schema versions) is pinned on every history. **Keep registering every old version that stored histories still use.** A missing key returns `IncompatibleVersion`; nothing falls back to "current".

## Host composition

Hosts (`src/Hosts.*`) compose; domain assemblies must not reference `Marrow.Infrastructure`.

```csharp
builder.Services.AddMarrowDependencies(builder.Configuration);   // NpgsqlDataSource (runtime login)
builder.Services.AddMarrowTimeline<GameState, GameInput, GameContext>(
    [ghostV1Components],            // all pinned versions still readable
    checkpointInterval: 64);        // domain-chosen spacing; position 0 always has a checkpoint
```

The domain then depends only on `ITimelineEngine<TState,TInput,TContext>`. Registering the same shape twice, a duplicate version key or an interval below 1 throws at composition time. The `marrow_timeline` schema is created by the Spec-01 one-shot runner `scripts/db/migrate.sh`; hosts never migrate.

## Lifecycle the adapter drives

1. `CreateSourceAsync(request, operationKey)` — the complete position-0 state, pinned context, rules/seed hashes. `OwnerId` is the verified server actor, never a request field.
2. `AppendAsync(id, expectedLastPosition, new StepRecord<TInput>(expectedLastPosition + 1, inputs), operationKey)` — contiguous steps from 1. A step may have zero inputs (an empty game tick). Inputs apply in `(Ordinal, InputId)` order; `InputId` is unique in the whole logical history. Business timestamps are payload, not the sort key.
3. `CompleteAsync(id, lastPosition)` — the only way to reach `Verified`. Cancellation or failure leaves `Recording` or `Failed`.
4. `RestoreAsync(id, T)` for `0 <= T <= LastPosition` (state after exactly steps 1..T), `ReplayAsync(id)`.
5. `ForkAsync(verifiedSource, T, operationKey, childContext)` — a `Recording` child with the same `TimelineId` type. Its prefix 0..T is rebuilt under each ancestor's own pinned context; `childContext` applies from T+1. Continue it with the **same** `AppendAsync`, starting at `expectedLastPosition = T`.
6. `CompareAlignedAsync(source, child, positions)` — verified states side by side. **The adapter computes any metric** (score, loss, fraud amount); the engine has no domain metrics.

Every operation returns `Result<T, TimelineError>` with `NotFound`, `Forbidden`, `InvalidPosition`, `IncompatibleVersion`, `CorruptHistory`, `Conflict` or `Cancelled`. Domain-specific rules (for example "no playable continuation from the final tick") belong at the domain boundary, not in Timeline.

## Reference adapters

`tests/Marrow/TestDomains` holds the two shapes the gate test runs through the same engine: `TickWorld` (tick clock, timer/cooldown/collected-ID set, empty ticks) and `CounterState` (event clock, nonuniform business timestamps, tied timestamps with distinct ordinals). `tests/Marrow/Timeline/EndToEnd/CrossDomainShapeGateTests.cs` shows the full composition, lifecycle and cold restart against real PostgreSQL. Neither adapter is a game or finance rule engine.
