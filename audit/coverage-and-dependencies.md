# Coverage and Dependency Audit

## Dependency result

The declared spec graph is acyclic, and every dependency header matches the primary map. The intended order is executable after the corrections below:

| Spec | Direct dependencies | Dependency result |
|---|---|---|
| 01 | none | Valid; shared-contract placement must be added before scaffolding. |
| 02 | 01 | Valid after complete Timeline/context contract is frozen. |
| 03 | 01 | Valid; abstraction and outbox proof ownership need clarification. |
| 04 | 02, 03 | Valid. |
| 05 | 02, 03, 04 | Valid. |
| 06 | 02, 03, 04 | Header is valid for the slice, but its exit/task also requires parallel Spec 05; move that proof to B2 or declare 05. |
| 07 | 03, 05 | Valid. |
| 08 | 05, 07 | Valid. |
| 09 | 04, 07, 08 | Valid. |
| 10 | 03, 06 | Valid. |
| 11 | 04, 06 | Valid; scope overlaps Spec 06 codec/persistence ownership. |
| 12 | 03, 10, 11 | Valid. |
| 13 | 04, 10, 11, 12 | Valid. |

There is no cyclic dependency. The only impossible ordering is the hidden Spec 06 → Spec 05 proof (`.kiro/specs/spec-06-ghostlap-v1-slice/requirements.md:35-37`; `.kiro/specs/spec-06-ghostlap-v1-slice/tasks.md:35-40`) while `docs/architecture/02-spec-map.md:3` permits them in parallel.

## Requirement-to-spec traceability

The table traces consequential baseline requirements, not every cosmetic statement.

| Original requirement | Implementing spec(s) | Dependent contracts / authority | Acceptance or evidence | Assessment |
|---|---|---|---|---|
| One reusable Marrow Timeline records, restores, replays, forks, preserves and compares (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:698-707`) | 02; adapters in 05/06; consumers 09/11 | `ITimelineTransition`, store/engine, manifest, browser adapter | Spec 02 invariant/concurrency/corruption tests; B2 cross-app proof | Covered in intent; TL-01/TL-02 block executable proof. |
| One reusable React Timeline interaction | 04, consumed by 09/11/13 | typed browser adapter; domain projections | component accessibility/cancellation; domain E2E | Covered. |
| Guest identity and staff authority | 03, 07, 10–13 | `IActorContext`, session/auth, hub/API policies | authorization and remembered-session tests | Covered after ARC-01 placement. |
| Transactional outbox/idempotent delivery | 03, instantiated in 07/10–12 | per-module UoW, inbox/outbox records | duplicate-delivery and business atomicity tests | Covered; PERSIST-01 clarifies ownership. |
| Six-bank Friday Ring deterministic facts | 05 | finance context/codec/policy adapter | same seed/version → same records/hash | Covered. |
| Proper payment lifecycle, holds and staff decisions | 07 | aggregate, case authority, concurrency tokens | legal transition/concurrent approval tests | Covered. |
| Append-only double-entry ledger and DB balance | 07 | PostgreSQL trigger/constraints, settlement UoW | conservation, duplicate settlement, correction tests | Incomplete until FIN-01 tests group completeness. |
| Fraud classifier, anomaly, liquidity forecast, grounded explanations | 08 | versioned artifacts, projections from actual payments | leakage/model/fallback/projection tests | Covered architecturally; quality awaits implementation. |
| v7/v8 real counterfactual with immutable original | 05, 07–09 | shared Timeline; replay worker; counterfactual store | original pre/post hash, cancellation, comparison E2E | Covered in intent; FIN-02/03 and SEC-01 are required. |
| Five functional Revenant screens | 09 | typed clients and Timeline adapter | route/action E2E | Covered; add responsive matrix and actual Skills1 path. |
| Authoritative 30 Hz, approximately 90-second game | 06, 10 | pure fixed-point reducer; room loop | 2,700-step deterministic test and real-time smoke | Covered; effective-input state needs freezing. |
| Candles, Soulfire, Burst, collision and authoritative scoring | 06 | canonical `GameState`; scheduled RNG; phase order | property/same-tick/last-tick tests | Covered; persist fractional distance accumulator. |
| Immutable solo history, exact ghost, rewind and branch | 02, 06, 11 | Timeline record/codec, catalog, separate historical/living worlds | T=0/2699/2700, sibling branch, inert-ghost tests | Covered after Timeline/context and codec ownership fixes. |
| 2–4 real players, lobby/invite/reconnect/results/rematch | 10 | hub/API contracts and server room loop | real-client E2E and duplicate result tests | Covered; crash finalization needs an outcome. |
| Chat/emotes, spectators, shares and ranking | 12 | server authority, capability token, rating inbox/outbox | spectator denial, revoke/expiry, duplicate ranking tests | Covered; token leakage controls missing. |
| Complete React/Pixi browser and mobile experience | 13 | typed server state; renderer-only authority | ten-route E2E, mobile controls, accelerated + real-time smoke | Covered; Skills2 path/authority correction needed. |
| Repeatable judge demo | 05, 09, 11, 13 | scenario seed/run identity, histories, browser routes | scripted hero flows | Missing append-only-safe finance run identity. |
| Public HTTPS URLs, staff access and external smoke (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:873-878`) | No concrete task | TLS proxy, production config/access | external deployed smoke | **Missing owner: DEPLOY-01.** |
| Multiplayer history replay | Explicitly deferred by source | none | none required | Correctly omitted. |

## Producer-consumer contract mapping

| Interface / artifact | Producer and owning spec | Consumers | Source of truth / versioning | Persistence and errors | Responsible proof |
|---|---|---|---|---|---|
| Shared low-level actor/realtime/outbox contracts | Intended Spec 03, location unresolved | 05–13 and hosts | Must be dependency-free and versioned with contracts assembly/Kernel | Domain-owned stores; authorization/duplicate errors | Architecture tests + Spec 03 integration |
| Timeline state/input/context transition | 02 | 05/06 adapters, 09/11 continuation | `docs/architecture/03-contracts.md`, manifest versions/hashes | PostgreSQL journal/checkpoints; typed errors required | deterministic/property/corruption tests |
| Timeline history identity/lifecycle | 02 | 04–06, 09, 11, 13 | Currently incomplete: TimelineId/BranchId mismatch | source/branch records; restore/fork/compare failures | TL-01 correction and invariant suite |
| Pinned context artifact | Domain adapter + 02 resolver | restore/replay after restart | canonical bytes/reference + schema/hash must be frozen | immutable content-addressed artifact or bytes; `IncompatibleVersion` | restart/replay test |
| Browser Timeline adapter | 04 | Revenant and Ghost web apps | typed client schema generated/frozen after 02 | maps server typed errors/cancellation | component + E2E adapter contract tests |
| Finance policy/context/codec | 05 | 08/09 | versioned v7/v8 artifacts | Timeline manifest/counterfactual store | seed/replay/hash tests |
| Payment/ledger/settlement | 07 | 08/09 and LiveOps Worker | domain aggregates + PostgreSQL constraints | append-only ledger/outbox; conflict/idempotency errors | DB/concurrency/conservation suite |
| Finance intelligence projections | 08 | 09 | versioned model + grounded feature evidence | artifacts/results; explicit fallback | leakage/model/projection tests |
| Game reducer/input/state codec | 06 | 10/11/13 | one final canonical codec, pinned `ghost-v1` rules | Timeline/checkpoints; invalid input/version errors | 2,700-step/property/codec tests |
| Realtime room envelopes | 03 + game semantics in 10 | game client, spectators in 12/13 | shared envelope plus versioned game DTOs | in-memory rooms; durable terminal result | hub/application authorization and crash tests |
| Solo history catalog | 11, reusing 06 codec | 12 shares, 13 history UI | catalog metadata + Timeline identity | `Pending → Verified/Failed` recommended | save/reload/reconcile/privacy tests |
| Share capability | 12 | 13 routes | hashed bearer token, expiry/revocation | catalog permission lookup; safe errors | revoke/expiry/leakage tests |

## Missing or unclear implementation owners

1. **Shared abstraction placement:** must be assigned before Spec 01; this is not permission to create a broad shared utility project.
2. **Timeline lifecycle, errors and context resolver:** Spec 02 is the correct owner, but the current master contract is incomplete.
3. **One migration executor:** Spec 01 should own the mechanism; later modules own migrations.
4. **Cross-app integration proof:** the map's B2 gate is the correct owner; remove it from Spec 06's local exit.
5. **Post-fork finance human-action policy:** Spec 09/scenario contract must select and version it.
6. **Repeatable finance run namespace:** Spec 09 E2E setup, supported by Specs 05/07.
7. **Public HTTPS and judge access:** add a bounded late deployment/submission gate, plausibly after Spec 13.

## Is the shared Timeline truly cross-domain?

Conditionally yes. The abstraction does not require identical clocks:

- A Timeline position is a logical ordered step, not wall-clock time.
- Revenant records ordered business facts/events and applies a finance transition/context.
- Ghost Lap records every authoritative simulation tick, including ticks with neutral input, and applies a game transition/context.
- Both use immutable source records, checkpoint plus suffix reconstruction, prefix-preserving branches and time/position-aligned comparison coordination.
- Domain codecs, input payloads, contexts, metric projections and UIs stay in adapters. No finance concept enters the game kernel and no game tick assumption enters the finance reducer.

This proof remains architectural rather than executable until TL-01 defines creation/append/addressing/compare/errors and TL-02 makes the exact context recoverable after restart. Position zero must be one unambiguous state boundary for both adapters; each adapter may map it to “before first finance event” or “initial game state before tick 1” without changing the engine.

## Ghost Lap hero journey

| Step | State authority / persistence | Interface | Spec / evidence |
|---|---|---|---|
| Start with no history and remembered guest | Identity/session | actor context | 03 |
| Create and play solo run | C# reducer is authoritative; browser sends input | game input/state contracts | 06 |
| Record all 2,700 positions and checkpoints | Marrow journal + canonical game codec | Timeline append/checkpoint | 02/06 |
| Save and reload | Timeline bytes + Ghost catalog; acknowledge only verified | history/catalog API | 06/11 |
| Challenge at tick zero | same seed/layout/spawn, separate living world | challenge command | 11 |
| Scrub and restore complete historical state | verified checkpoint + suffix replay | shared browser Timeline adapter | 02/04/11 |
| Fork at T and input from T+1 | immutable shared prefix, independent suffix | fork/continue | 02/11 |
| Continue original ghost unchanged | read-only historical world; never an actor | ghost projection | 06/11 |
| Compare aligned final scores | canonical score components by position | comparison projection | 11 |
| Exercise in browser | renderer consumes server state only | typed routes/clients | 13 |

This is meaningful execution, not a prerecorded ghost. Required corrections are context recovery, effective input/fractional score state and codec/catalog ownership.

## Revenant hero journey

| Step | State authority / persistence | Interface | Spec / evidence |
|---|---|---|---|
| Generate Friday Ring source | deterministic seed and policy artifacts | finance context/codec | 05 |
| Apply v7 screening/holds | pure transition and versioned model/policy | finance Timeline adapter | 05/08 |
| Approve/release and settle | payment aggregate + PostgreSQL ledger/UoW | application services | 07 |
| Calculate actual liquidity | projections consume simulated payment outcomes | intelligence interface | 08 |
| Restore at 09:00 | Marrow checkpoint + event suffix | Timeline restore | 02/09 |
| Continue isolated v8 | Replay DB role writes counterfactual only | replay worker mode | 09 after SEC-01 |
| Resolve human actions deterministically | versioned continuation policy | scenario contract | missing selection: FIN-03 |
| Compare outcomes | terminal/deferred metric definitions | finance comparison adapter | 09 after FIN-02 |
| Prove original unchanged | original pre/post verification hash and append-only DB | audit view | 07/09 |
| Repeat for another judge | fresh run namespace, prior runs retained | scenario-run command | missing: PROD-01 |

The model is causally connected; a chart-only mock is neither required nor permitted. Its claims become defensible after the financial P1 gates.

## Adversarial scenario disposition

| Scenario | Existing prevention/detection | Result |
|---|---|---|
| Fork at first position | Spec 02 arbitrary/zero fork; Spec 11 T=0 | Covered after exact position-zero semantics are frozen. |
| Fork at final position | Inspect-only; branch disabled in Spec 11 | Covered. |
| Fork twice from same source | Independent siblings required/tested | Covered. |
| Concurrent identical fork | one-branch/conflict semantics in Spec 02 design | Covered by planned integration test. |
| Corrupt/missing checkpoint | typed failure and corruption tests | Covered after typed-result API exists. |
| Cancelled replay/background job | incomplete branch cannot become verified | Covered; reconciliation needed for cross-schema save. |
| Incompatible rule/model version | fail-closed requirements preserve bytes | Covered in intent; TL-02 must make artifacts resolvable. |
| Action-dependent RNG after branch | precomputed candle schedule and pinned context | Covered. |
| Same-tick candle pickup | Candle ID then stable slot tie-break | Covered. |
| Manual Burst at auto-expiry | at most one Burst, explicit phase test | Covered. |
| Ghost collision/collection | ghost structurally excluded from living actors | Covered. |
| Packet loss while connected | input windows exist; no latch/neutral rule | **Unresolved: GAME-01.** |
| Player disconnect during finalization | grace/forfeit and unique result; crash boundary absent | Partially covered; **RT-01**. |
| Spectator sends gameplay input | denied at hub and application service | Covered. |
| Duplicate ranking/outbox event | inbox/unique effects and rebuild | Covered. |
| Duplicate settlement | idempotency/concurrency tests | Covered; posting completeness remains FIN-01. |
| Concurrent staff approval | aggregate concurrency/authorization | Covered. |
| Replay invokes live writers | DI excludes writer but DB privilege remains | **Unresolved: SEC-01.** |
| Post-fork analyst action differs | policy placeholder only | **Unresolved: FIN-03.** |
| ML version mismatch | pinned version/fail-closed intent | Covered in plan; implementation proof pending. |
| Shared DTO changes in an early spec | contract freeze/architecture checks | Detectable after corrected contract ownership/versioning. |
| Hook executes repeatedly | all hooks `enabled:false`; scripts absent | Prevented now; enable only after manual validation. |
| Frontend skill absent at UI task | files exist but paths disagree | **Unresolved operationally: UI-01.** |
| Public judge cannot reach apps | no external deployment task | **Unresolved: DEPLOY-01.** |

## Missing acceptance/evidence requirements

The minimal additions are:

- Timeline restart with exact context resolution and hash validation.
- Source create/append/complete, returned-branch restore and typed-error exhaustiveness.
- Empty/one-leg/zero/missing-leg posting-group rejection.
- Replay DB role denied a live ledger write.
- Post-fork analyst action and held-versus-terminal fraud metric tests.
- Fresh repeatable scenario run without deleting prior audit data.
- Packet-loss/effective-input and fractional-distance restore tests.
- Crash-before-result-commit / DB-unavailable-at-finish outcome.
- Cross-schema history save reconciliation.
- Share token absence from referrers, caches and logs.
- Revenant mobile viewport coverage.
- External HTTPS smoke for both apps plus staff access instructions.

These additions target damaging failures. They do not justify exhaustive duplicate suites or a heavier deployment architecture.
