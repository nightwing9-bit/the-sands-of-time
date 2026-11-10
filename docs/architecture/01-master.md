# A. Phase 1 master architecture — The Sands Of Time

## 1. Decisions and evaluated alternatives

**Selected:** a single-repository **modular monolith** with domain-owned persistence, three executable host configurations (**API**, **Realtime**, **Worker**), one shared generic Marrow Timeline implementation, and independent Revenant/GhostLap domain adapters. **No domain gets to translate the other domain's types.** At initial deployment keep a **single Realtime replica** and thus one in-process authoritative owner per active room. Scale-out is disallowed without a new owner-election/affinity decision.

**Evaluated dependency decompositions:** (A) infrastructure/platform first, then complete finance, then complete game: easy module batching but keeps shared Timeline and second-adapter integration unproven until late; rejected. (B) four baskets with a small foundation, **two early vertical slices** through the same Timeline, then the hard per-domain completeness specs: slightly more cross-domain coordination but rapidly reveals wrong common abstractions and provides recorded proof milestones; selected. A third alternative, game-first single-app architecture refactored to a library afterward, was rejected because extractive refactoring would spend credits rewriting history and security contracts.

**13 specs / four baskets:** B1 four platform specs; B2 two first vertical slices; B3 three Revenant completion specs; B4 four Ghost Lap completion specs. Two apps get actual working branch paths at B2 gates, *before* secondary workflows. B2 is not a wireframe/stub milestone: it uses a real pure domain transition kernel. B3/4 add persistence, transport, ML, social and complete UI against those frozen contracts.

## 2. Physical project map (chosen minimum)

```text
src/Marrow.Kernel/                 (Results, IDs, pure actor/realtime/outbox contract types and codecs)
src/Marrow.Timeline/               (one restore/replay/fork/compare engine + journal port)
src/Marrow.Infrastructure/         (Postgres Timeline adapter, Identity, shared outbox relay and Redis/SignalR implementations)
src/Revenant.Contracts/            (public DTOs and integration events)
src/Revenant/                      (pure finance kernel, application, persistence, worker adapters)
src/GhostLap.Contracts/            (public DTOs and integration events)
src/GhostLap/                      (fixed-tick rules, solo/multiplayer/application/persistence)
src/Hosts.Api/                     (composition root, API, auth endpoints)
src/Hosts.Realtime/                (SignalR, sole room/tick loop owner)
src/Hosts.Worker/                  (outbox relay, bank scenarios, forecasts, replay jobs)
web/packages/contracts/            (OpenAPI-generated DTOs + versioned hub clients)
web/packages/timeline-ui/          (one generic React Timeline scrubber)
web/apps/revenant/                 (Revenant React UI / screens)
web/apps/ghost-lap/                (Ghost Lap React + PixiJS UI)
tests/{Architecture,Marrow,Revenant,GhostLap,Integration,E2E}/
deploy/{compose,nginx}/
scripts/checks/                    (cheap, deterministic shell checks)
.kiro/{specs,steering,settings,hooks}/
docs/{architecture,workflow,adr}/
```

A folder may hold subnamespaces instead of one C# project per logical layer. `*.Contracts` are small public assemblies. No cross-domain EF model reuse, shared Money/GameState, “universal event bus” framework, MediatR dependency by default, or production-grade cloud fleet.

## 3. Legal dependency graph

```text
Hosts.Api / Hosts.Realtime / Hosts.Worker (DI composition only)
    ├── Marrow.Infrastructure ──> Marrow.Timeline ──> Marrow.Kernel
    ├── Revenant ──> Revenant.Contracts ──> Marrow.Kernel (+ Marrow.Timeline)
    └── GhostLap ──> GhostLap.Contracts ──> Marrow.Kernel (+ Marrow.Timeline)
web/apps/* ──> web/packages/contracts + web/packages/timeline-ui
```

`Marrow.Timeline` does **not** reference either domain; domain assemblies do **not** import each other; domain rules do not import SignalR, Postgres, browser, PixiJS or React. **Before Spec 01 scaffolding, shared pure contracts are owned by the existing `Marrow.Kernel` project**, not by Infrastructure: `IActorContext` and actor identity value types; the domain-neutral `RealtimeEnvelope` metadata contract; and the minimal outbox event/inbox-identity records/ports. Kernel must not reference EF, ASP.NET, Redis, SignalR or domain types. `Marrow.Infrastructure` implements shared ports; Revenant/GhostLap implement their own domain-owned repository ports/persistence **within their own assembly/namespace**, composed by hosts. Neither domain is permitted to reference `Marrow.Infrastructure`; do not add a contracts project solely for these small contracts. Update compiled architecture tests to enforce this graph.

Cross-module interactions are explicit command/query interfaces (synchronous within one process when needed), or transactionally recorded outbox events to reliable asynchronous consumers. **No direct foreign schema reads/writes** for business integration; Postgres schemas separate (`marrow_identity`, `marrow_timeline`, `revenant`, `ghostlap`). Identity owns principals; each domain owns resource authorization decisions. **Spec 03 remains dependent on both Specs 01 and 02; do not infer a different graph from an external audit table.**

## 4. State authority and transactions

| Authority | Writer/owner | Read/transport | Failure contract |
|---|---|---|---|
| Historical source + checkpoints | Marrow Timeline append API, with caller domain validation | immutable snapshots/records with source+owner and hash | version mismatch => fail closed, never reinterpret using current rules |
| Financial payment, journal group and balance | Revenant, one Postgres transaction | Revenant projections, outbox | rollback all postings on any unbalance/insufficient funds; retry bounded transient errors |
| Financial counterfactual | Revenant pure sandbox projector via Worker | branch-only result tables + metrics | replay process uses read-only-on-live-ledger DB role; cancellation doesn't change original |
| Game live match state | exactly one Realtime host room loop | signed/typed server snapshots via SignalR | stale inputs rejected; disconnect yields neutral controls, then explicit forfeit |
| Solo original and branch | GhostLap + shared journal port; original append-only | permission-checked history/replay API | save receipt only after DB commit; retry dedupes |
| Match rating | GhostLap durable `match_result`+`rating_effect` | Postgres authoritative rating; Redis sorted-set read cache | UNIQUE match/player effects, consumers may replay safely |
| Account/session | Marrow Identity policy + domain handlers | JWT/HTTP-only refresh cookies/guest session | deny unauthorized or expired access; never grant owner rights by submitted owner ID |

**Outbox:** insert a domain-owned outbox row in the same PostgreSQL transaction as its business write; consumer dedupes by `(consumerId,eventId)` in the same transaction as the consumer business effect. **For cross-host delivery, use the existing Redis Streams transport**: Worker relays committed outbox records at least once; a receiving host consumes and applies idempotently. A direct typed handler is valid **only within the same running host/process** and is not an alternative to Worker→Realtime communication. Each domain owns its outbox table/transaction; Spec 03 supplies reusable mechanics and proves them using an Identity- or test-owned unit of work, not future Revenant/GhostLap schemas. No “exactly once delivery” claim. Redis backplane is transport, not durable session authority. Chat messages use their own domain policies/rate limits, not a scoring side effect.

**Worker authority (Specs 07/09):** run the same `Hosts.Worker` executable in explicit `LiveOps` versus `Replay` **process modes** with separate DI registrations and database credentials. `Replay` has authorized read access to original history and controlled append/create/complete rights **only for new Timeline branches through Marrow Timeline ports**, plus writes to its own counterfactual outputs; PostgreSQL denies live `payment`, `ledger`, `case`, and settlement writes even through raw SQL, and protected Timeline source rows cannot be rewritten/appended after verification. A live privileged credential is never present in the Replay process. No separate codebase, general job platform, or new microservice is introduced. Prove denial with a real DB-grant test before claiming nonmutation.

**Ghost original save boundary (Specs 06/11):** Spec 06 owns the canonical game codec and creates/completes a verified owner-tagged Timeline history; Spec 11 links a **previously verified** source into a GhostLap-owned catalog record with an idempotent single-schema transaction. A failed catalog commit does not unverify or delete the source, and does not report “saved”; retry resolves the same source ID. No cross-schema foreign key or speculative distributed transaction coordinator is required. A recoverable verified-but-unlisted source may be linked on retry; a background reconciliation service is optional only if actual failures justify it.

**Migration execution (Spec 01 decision):** exactly one explicitly invoked **one-shot migration executor**, implemented by `scripts/db/migrate.sh` and called by a Compose `migrate` job before hosts start; CI and manual development use the same command. API/Realtime/Worker **never auto-run migrations on concurrent startup**. Spec 01 provisions Postgres and the runner/ordering mechanism; Specs 02, 03 and later domain specs supply their own module migrations, executed sequentially in stable module order as they become available. No additional long-lived migration service or premature creation of future domain schemas.

## 5. Deterministic replay: common meaning, distinct clocks

`TimelinePosition` is an unsigned/nonnegative 64-bit *logical step index*, serialized as a **decimal string at browser boundaries**. GhostLap has **one step per game tick**, including no-input ticks; Revenant has **one step per causally ordered exogenous payment/action input**, tagged with separate business timestamp. Source records contain stable `ordinal`/`inputId` tie-breaker and domain context/version identity. Do not reorder by wall-clock timestamps. Shared engine accepts a deterministic domain reducer plus a snapshot codec and a comparison projector. Domain pure reducers own all rules.

**Write/restore:** canonical byte encoding and state hash (SHA-256) over **all simulation-significant fields**, pinned reducer/rules/schema/seed/model metadata. Position 0 is the complete *initial state* before step 1. `CreateSource` durably records that checkpoint, a versioned input codec and recoverable pinned transition context before acknowledging the source. Step positions are contiguous from 1, including empty-input game ticks. Restore at T loads the nearest checkpoint <=T, verifies the journal chain and compatible codecs/context, then folds records through T. A state digest calculated at arbitrary T proves deterministic reconstruction against verified inputs/anchors; it is **not** claimed to be an independently persisted per-position state hash. Fail closed on missing gaps or incompatible/corrupt required data. Branch metadata points to immutable parent/prefix through T; its post-T records start at T+1. A branch prefix is always reconstructed with its **original ancestor segment contexts**, while its suffix uses the branch's independently pinned context (e.g. v7 through T, v8 after T). Never recompute the prefix under the new branch policy or copy/rewrite its original steps. Failed/cancelled branch remains incomplete/failed and cannot masquerade as a completed verified run.

**Finance-specific subtlety:** stored source *decisions* are not exogenous payment inputs. Pin the input facts and external commitments, then recompute policy-dependent decisions, holds, settlement and liquidity in the branch; preserve evidence of the original decisions separately. Otherwise policy v8 “replay” would just play policy v7 outcomes again. Original financial replay verifies source hashes using pinned v7 decisions/artifacts; alternate branch uses v8 and the same *upstream facts* (not the same outcomes). Any consequential human action after fork must be represented by a defined deterministic continuation rule, or make replay input incomplete and reject it rather than silently guessing. Reserve a Revenant-owned **`ScenarioRunId`** from Spec 05 onward for repeatable Friday Ring executions: same seed/policy yields reproducible synthetic facts under a distinct run namespace; later payment/idempotency keys include that run identity so repeated demos never need deletion of append-only ledger records. Freeze the identity now; implement persistence and operator workflow in owning Specs 05/07/09. Choose the deterministic post-fork analyst-action continuation policy before Spec 09 replay work, not by inference inside a renderer.

**Game-specific subtlety:** precompute a candle schedule from a versioned seed and layout independent of player pickup behavior. Full snapshots include score/charge/cooldowns/Overlit timers/collection IDs/schedule/RNG and position/velocity. Ghost reads the *old* source projection, current world independently collects its own candles. Original source remains immutable. Preserve the **last effective input/latch state** used when a connected player sends no new input, and the **fractional/fixed-point distance-to-score remainder** within canonical `GameState`; those are deterministic transition state, not renderer state. Spec 06 owns the definitive game codec and Spec 11 reuses it rather than creating a competing serialization authority.

## 6. Stable frontend/realtime boundaries

React `web/packages/timeline-ui` owns one functional scrubber with `sourceId`, selected decimal-string position, range, play/pause/seek/branch controls, verified source marker and comparison-selection callbacks; adapters map positions to tick labels or financial timestamps and render their own charts/scenes. Scrubber **never** computes scores, policy decisions, or authoritative states. Browser UI consumes generated REST DTOs and the **single canonical** typed hub envelope in `03-contracts.md`: (`schemaVersion`, `streamId`, `sequence`, `eventId`, `serverUtc`, `kind`, `payload`), with gap recovery by snapshot/refetch. Unknown critical schema versions fail gracefully.

## 7. Earliest real integration proof

After spec-02, run Marrow contract tests against two **tiny fake reducers only in tests** to prove genericity. Spec-05 brings a real six-bank deterministic financial input stream, pure payment/hold/settlement decisions, paired v7/v8 run and nonmutation hash. Spec-06 brings a real game greybox loop, 90s solo original, save/restore/fork and ghost trace. **Gate B2:** same `Marrow.Timeline` assembly and `timeline-ui` package demonstrably called by both; original hashes unchanged, game branch at tick with nonzero charge/pickup remains intact, bank alternate net balance/held outcome changes. This is the first demo-critical proof, not something to postpone to UI polish.

## 8. Versioning and rollout

Contracts carry explicit schema version; historical reducer/rules/policy/ML artifacts are immutable referenced by content hash. A backwards-compatible API additive field is okay with regenerated web client + consumer checks. Replay-significant changes require a *new* reducer/rules identifier or a migration that proves old hashes. Missing old reducer => display **incompatible/unavailable**; never silently replay with a newer reducer. PostgreSQL migrations are forward-only, owned by each schema, and exercised on a clean database and upgrade path. Deploy Compose single instance with separate API/Realtime/Worker executable configurations (and isolated `Worker.Replay` process mode when enabled) and HTTPS proxy; AWS Lightsail/EC2 after local proof; no claim that a Redis backplane makes two authoritative realtime instances safe.

## 9. Hard prohibitions

No final art in Phase 1; no third app; no multiplayer permanent archives; no real-money operations; no 100x/30Hz/performance claims without actual measurements. Do not alter the supplied demo narrative. Do not delete social/spectator/rating/mobile/ML scope simply because the central moment gets priority; each has an implementation owner in the DAG.
