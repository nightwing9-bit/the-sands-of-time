# Prioritized Findings

This register contains confirmed contradictions and credible failure modes. It excludes stylistic preferences and ordinary reversible implementation choices. “Start blocker” means the first Kiro implementation session must wait; later gates identify the first affected spec.

## P0 — Critical

None.

## P1 — Major implementation blockers

### ARC-01 — Shared abstraction ownership contradicts the legal project graph

- **Evidence / claimed contract:** `docs/architecture/01-master.md:37-47` permits domain modules to depend on Kernel/Timeline, not Infrastructure. Spec 03 owns `IActorContext`, realtime envelopes, and reusable outbox/inbox mechanics under Infrastructure/hosts (`.kiro/specs/spec-03-identity-realtime-outbox/design.md:5,15-23`). Specs 05–12 consume those concepts.
- **Defect and failure:** No dependency-free assembly or explicit Kernel location owns the public records/interfaces. Spec 01 can freeze project references that force domains to reference Infrastructure, break the legal graph, or prompt Kiro to invent another assembly later.
- **Why safeguards fail:** Architecture tests can only enforce a graph after interface ownership makes the graph possible.
- **Smallest correction:** Explicitly place pure shared contracts in `Marrow.Kernel`, or add one small dependency-free contracts/application-abstractions project in Spec 01. Infrastructure implements them; domain projects reference only the abstractions. Update `01-master.md`, Spec 01 scaffold ownership, Spec 03 ownership and architecture tests together.
- **Affected / gate:** Specs 01, 03, 05–12. **Blocks the first implementation session and Spec 01 task 1.1.**

### TL-01 — Frozen Timeline API cannot express the lifecycle it promises

- **Evidence / claimed contract:** The baseline requires recording, restore, deterministic replay, fork, immutable source and comparison (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:698-707`). Spec 02 requires ordered append and aligned comparison (`.kiro/specs/spec-02-timeline-journal-simulation/requirements.md:17-26`). `ITimelineStore` has no public API and the engine only exposes restore/replay/fork/continue (`docs/architecture/03-contracts.md:24-34`), yet line 37 calls it the sole comparison coordinator and promises typed failures. `ForkAsync` returns `BranchId`, while restore/replay accept only `TimelineId`.
- **Defect and failure:** A source cannot be created/appended through the frozen API; a returned branch cannot later be addressed; compare has no callable contract; direct success returns cannot carry `NotFound`, `Forbidden`, `InvalidPosition`, `CorruptHistory`, etc. Consumers will invent incompatible operations or bypass the engine.
- **Why safeguards fail:** Prose and tests do not establish producer-consumer signatures or error exhaustiveness.
- **Smallest correction:** Freeze create-source, append, complete/verify and aligned-compare operations; use one `HistoryId` or define complete branch-to-history mapping/overloads; return a typed result/error union. Explicitly define position 0, first applied record, inclusive restore range, final position and append-after-fork semantics. Keep domain metric projection in adapters.
- **Affected / gate:** `03-contracts.md`, Spec 02, Browser adapter and consumers 04–06, 09, 11, 13. **Does not block corrected Spec 01; blocks Spec 02.**

### TL-02 — Pinned transition context is not recoverable after restart

- **Evidence / claimed contract:** Every transition requires `TContext` (`docs/architecture/03-contracts.md:17-20`), but restore/replay accept only a history ID and `HistoryManifest` stores hashes/version strings, not canonical context bytes or a durable resolver reference (`docs/architecture/03-contracts.md:11-14,27-34`). Spec 02 says context is captured and current config must not be read (`.kiro/specs/spec-02-timeline-journal-simulation/design.md:15,19`). Old Ghost histories must replay under pinned rules or fail honestly (`.kiro/specs/spec-11-ghostlap-solo-history-branches/requirements.md:24`).
- **Defect and failure:** After a host restart, the engine can validate a context hash only if it somehow obtains the original context; a hash cannot reconstruct it. Falling back to current policy/rules makes replay false.
- **Why safeguards fail:** Snapshots do not replace the separate context required by the transition function.
- **Smallest correction:** Add an immutable context codec/resolver. Persist canonical context bytes or a durable content-addressed reference, schema version and hash before acknowledging creation/fork; replay must resolve and verify it or return `IncompatibleVersion`.
- **Affected / gate:** Timeline contracts, Spec 02, adapters in 05/06/11. **Blocks Spec 02 finalization, not Spec 01.**

### DAG-01 — Spec 06 has an undeclared hard dependency on parallel Spec 05

- **Evidence / claimed contract:** Spec 06 declares only 02, 03 and 04 (`.kiro/specs/spec-06-ghostlap-v1-slice/requirements.md:3`), while its exit requires the same Timeline already proven in the bank (`.kiro/specs/spec-06-ghostlap-v1-slice/requirements.md:35-37`) and task 6.5 requires “Revenant V1” (`.kiro/specs/spec-06-ghostlap-v1-slice/tasks.md:35-40`). The spec map permits 05 and 06 in parallel and separately defines a basket proof after both (`docs/architecture/02-spec-map.md:3,63-65,71-73`).
- **Defect and failure:** A valid Spec 06 execution reaches an impossible exit while Spec 05 is still running.
- **Why safeguards fail:** The later basket gate does not remove the contradictory local gate.
- **Smallest correction:** Remove the bank proof from Spec 06's local exit and execute it at B2 after both specs; alternatively add 05 as an explicit dependency on 06. The first option preserves useful parallelism.
- **Affected / gate:** Spec 06 requirements/tasks and spec map/workflow. **Blocks Spec 06 completion, not Specs 01–05.**

### FIN-01 — Database parity does not reject incomplete posting groups

- **Evidence / claimed contract:** The source requires database-enforced balance (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:238`). Spec 07 requires unbalanced and incomplete groups to fail (`.kiro/specs/spec-07-revenant-ledger-operations/requirements.md:17`), but its design only checks debit/credit equality (`.kiro/specs/spec-07-revenant-ledger-operations/design.md:17`) and task verification only mentions an unbalanced commit (`.kiro/specs/spec-07-revenant-ledger-operations/tasks.md:7-12`).
- **Defect and failure:** An empty, one-sided or zero-value set can aggregate to zero while a payment is marked settled, so conservation appears to pass although no valid money movement occurred.
- **Why safeguards fail:** Sum equality proves balance, not completeness or connection to the settlement.
- **Smallest correction:** Require a finalized group to have the expected non-zero legs, one currency and a valid settlement link; make `Settled` conditional on that group in the same transaction. Test empty, one-leg, zero-value, missing-leg and valid balanced groups.
- **Affected / gate:** Spec 07 requirements/design/tasks and DB integration tests. **Blocks Spec 07 exit, not initial implementation.**

### FIN-02 — “Fraud prevented” can count fraud merely delayed beyond the horizon

- **Evidence / claimed contract:** Spec 05 counts labelled fraud held/rejected before settlement (`.kiro/specs/spec-05-revenant-v1-slice/design.md:21`). Spec 09 counts fraud settled in v7 but “not v8” at the comparison horizon and books principal as avoided exposure (`.kiro/specs/spec-09-revenant-counterfactual-ui/design.md:17-19`). The source requires outcome metrics to be defined before claims (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:268-273`).
- **Defect and failure:** A v8 hold settles just after the chosen horizon, yet the UI reports the whole principal as prevented and inflates net effect.
- **Why safeguards fail:** Deterministic replay makes the number repeatable, not semantically true.
- **Smallest correction:** Count prevention only for terminal rejection/return or a completed scenario in which non-settlement is final. Otherwise label “fraud settlement deferred at horizon” and exclude it from avoided-exposure/net-effect totals.
- **Affected / gate:** Specs 05/09 metric definitions and demo assertions. **Blocks Spec 09 exit and the financial claim, not Spec 01.**

### FIN-03 — Post-fork human decisions have no frozen continuation policy

- **Evidence / claimed contract:** The master says consequential post-fork human actions need a deterministic continuation rule or rejection (`docs/architecture/01-master.md:69`). Spec 09 says “according to an explicit deterministic policy” but does not select one (`.kiro/specs/spec-09-revenant-counterfactual-ui/design.md:15`). Friday Ring must branch successfully from 09:00 (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:105-107`).
- **Defect and failure:** An original approval may no longer be legal under v8, while v8 may create new held cases. Blind reuse, silent drop and ad-hoc synthetic approval each fabricate a different future.
- **Why safeguards fail:** A policy placeholder is not a versioned policy artifact and cannot produce a test oracle.
- **Smallest correction:** Freeze one versioned Friday Ring continuation rule. For example, replay recorded actions only when stable case/payment preconditions hold, apply a deterministic documented fallback for newly held cases, and fail the run on unresolved actions. Test at least one post-fork analyst action.
- **Affected / gate:** Spec 09 design/tasks and scenario contract. **Blocks Spec 09 and the hero demo, not Spec 01.**

### SEC-01 — Replay isolation is not enforced by database privileges

- **Evidence / claimed contract:** One Worker owns settlement and replay (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:126`; `.kiro/specs/spec-07-revenant-ledger-operations/design.md:21-23`). Spec 09 claims that excluding the live writer from a replay DI graph makes zero ledger writes possible (`.kiro/specs/spec-09-revenant-counterfactual-ui/design.md:13-15`; `.kiro/specs/spec-09-revenant-counterfactual-ui/tasks.md:7-12`).
- **Defect and failure:** A shared privileged connection, raw SQL or registration error can still mutate the live ledger even if one service type is absent.
- **Why safeguards fail:** DI is a composition safeguard, not a database authority boundary.
- **Smallest correction:** Define explicit `Worker.LiveOps` and `Worker.Replay` modes from the same executable. Give replay a distinct DB role/connection that can read source history and write only counterfactual tables. Add a real permission-denial integration test.
- **Affected / gate:** Specs 07/09, Worker composition and PostgreSQL grants. **Blocks Spec 09's nonmutation guarantee, not Spec 01.**

### PROD-01 — Repeatable Friday Ring has no append-only-safe run lifecycle

- **Evidence / claimed contract:** The scenario must be repeatable (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:97-107`); Spec 05 produces deterministic inputs (`.kiro/specs/spec-05-revenant-v1-slice/requirements.md:17-25`); Spec 09 E2E says to generate Friday Ring but defines no run/reset identity (`.kiro/specs/spec-09-revenant-counterfactual-ui/design.md:29`); the ledger is append-only (`.kiro/specs/spec-07-revenant-ledger-operations/requirements.md:17-25`).
- **Defect and failure:** After one judge approves/settles cases, a rerun collides with idempotency keys or final states. Deleting/resetting rows contradicts the audit story.
- **Why safeguards fail:** A deterministic seed does not create an isolated execution namespace.
- **Smallest correction:** Introduce `ScenarioRunId` or a demo-tenant/run namespace and an authorized create-fresh-run operation. Preserve prior runs and reproduce the same business inputs within each new namespace.
- **Affected / gate:** Specs 05, 07, 09 and E2E setup. **Blocks repeatable demo acceptance, not early specs.**

### DEPLOY-01 — Public HTTPS deployment and judge access have no implementing task

- **Evidence / claimed contract:** The source requires public functional URLs, staff instructions and external smoke (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:873-878`). Coverage assigns HTTPS deployment to Specs 01/03 (`docs/architecture/05-coverage-readiness.md:8`), but Spec 01 only establishes local Compose/proxy allowance (`.kiro/specs/spec-01-foundation-bootstrap/design.md:15-17,23-25`) and Spec 13 exits on browser journeys, not deployment (`.kiro/specs/spec-13-ghostlap-complete-browser/requirements.md:35-37`).
- **Defect and failure:** Both apps can pass locally yet be unusable by judges because TLS routing, production secrets, staff onboarding or external reachability was never implemented.
- **Why safeguards fail:** A local browser test and a future proxy placeholder do not prove a public deployment.
- **Smallest correction:** Add one bounded late gate for single-instance production Compose, TLS reverse proxy, both routes/subdomains, production secret validation, staff/MFA access instructions and external Revenant/Ghost smoke tests.
- **Affected / gate:** Coverage plan and a late Spec 13/submission task. **Blocks public completion/submission, not Spec 01.**

## P2 — Material local corrections

### PERSIST-01 — Migration and reusable outbox ownership precede domain owners

- **Evidence:** Spec 01 says owning migrations create schemas but owns no domain migrations (`.kiro/specs/spec-01-foundation-bootstrap/design.md:5,15-17`). Spec 03 requires per-module atomic outbox/inbox before later domain UoWs/migrations exist (`.kiro/specs/spec-03-identity-realtime-outbox/tasks.md:21-26`). Revenant and Ghost later claim their own migrations (`.kiro/specs/spec-07-revenant-ledger-operations/design.md:5,15`; `.kiro/specs/spec-11-ghostlap-solo-history-branches/design.md:5,15`).
- **Failure / insufficiency:** Spec 03 either writes another module's schema or cannot honestly prove its exit; multiple hosts may race migrations.
- **Correction:** Spec 01 provisions DB and one migration-runner mechanism only; name one executor. Spec 03 proves the pattern in an Identity/test-owned transaction; later domains instantiate their own outbox tables and atomic writes.
- **Gate:** Clarify migration executor before Spec 01; instantiate details in Specs 03/07/10–12.

### PERSIST-02 — Specs 06 and 11 duplicate codec/persistence ownership and omit cross-schema finalization

- **Evidence:** Spec 06 owns the complete `GameState` codec, real PostgreSQL original save/reload, branch persistence and verification (`.kiro/specs/spec-06-ghostlap-v1-slice/design.md:5,19-23`; `.kiro/specs/spec-06-ghostlap-v1-slice/tasks.md:21-33`). Spec 11 again owns/implements the codec and immutable records (`.kiro/specs/spec-11-ghostlap-solo-history-branches/design.md:5,13-17`; `.kiro/specs/spec-11-ghostlap-solo-history-branches/tasks.md:7-12`) while catalog metadata and canonical bytes span `ghostlap` and `marrow_timeline` schemas (`.kiro/specs/spec-11-ghostlap-solo-history-branches/design.md:15`).
- **Failure / insufficiency:** Kiro can rewrite the codec or acknowledge a Timeline commit while catalog finalization fails, creating an orphan/inconsistent save.
- **Correction:** Declare Spec 06 the final codec/record-shape owner; Spec 11 reuses/verifies it and adds catalog/privacy/idempotency. Define idempotent `Pending → Verified/Failed` finalization and reconciliation; return “saved” only at `Verified`.
- **Gate:** Correct before Spec 06 freezes the codec and before Spec 11 persistence work.

### GAME-01 — Connected-player missing/late-input semantics are undefined

- **Evidence:** The game contract has tick/slot/sequence but no rule when no usable command exists (`docs/architecture/03-contracts.md:57-59`). Spec 06 says “latest permitted input” without latch/neutral semantics (`.kiro/specs/spec-06-ghostlap-v1-slice/design.md:17`); Spec 10 defines queue windows but not empty-queue behavior (`.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/design.md:17`). Neutral is only specified after disconnect (`.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/design.md:19`).
- **Failure / insufficiency:** Packet loss makes one implementation continue motion and another stop; if latch state is absent from snapshots, replay diverges.
- **Correction:** Freeze selection, allowable age, hold-last versus neutral, Burst edge and rejection rules. If latched, persist effective movement and last accepted sequence in canonical state. Test loss/late/duplicate packets and a checkpoint during active input.
- **Gate:** Before the Spec 06 reducer/network contract is frozen.

### GAME-02 — Distance-score replay state omits the fractional accumulator

- **Evidence:** The source includes `authoritativeDistance` (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:515-535`); the frozen contract lists displayed score but not accumulated fixed-point distance/remainder (`docs/architecture/03-contracts.md:59`). Scoring floors cumulative distance (`.kiro/specs/spec-06-ghostlap-v1-slice/design.md:15`).
- **Failure / insufficiency:** Restore at 1023/1024 of a score unit loses the remainder, changing the next score and branch comparison.
- **Correction:** Make accumulated authoritative distance/remainder and per-component counters mandatory canonical snapshot/codec fields; property-test restore at a non-divisible boundary.
- **Gate:** Before Specs 06/11 codec freeze.

### SEC-02 — PostgreSQL RLS execution semantics are underspecified

- **Evidence:** Spec 07 requires RLS (`.kiro/specs/spec-07-revenant-ledger-operations/requirements.md:24`) and its design combines tenant claim, EF filter and RLS (`.kiro/specs/spec-07-revenant-ledger-operations/design.md:19`) without runtime/migration roles or pooled-session semantics.
- **Failure / insufficiency:** A table owner/BYPASSRLS role bypasses policy, or a pooled connection leaks tenant context.
- **Correction:** Separate migration owner/runtime roles, use `FORCE ROW LEVEL SECURITY` where claimed, set transaction-local tenant context, and test pooled A→B→A isolation. If not implemented, explicitly defer the RLS claim.
- **Gate:** Before Spec 07 can claim RLS completion.

### RT-01 — Realtime crash at terminal finalization has no durable outcome

- **Evidence:** Spec 10 uses one in-memory room loop (`.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/design.md:15-19`) and persists results after completion (`.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/design.md:21-23`).
- **Failure / insufficiency:** Process death or DB failure at the terminal tick loses the only final state; reconnect, rating and rematch outcome are undefined.
- **Correction:** Persist a minimal finalization intent before discarding state, or define recovery as `Aborted/Unrated` with a visible rematch. Test crash-before-result-commit and DB-unavailable-at-finish.
- **Gate:** Before Spec 10 robust-completion exit; does not require multiplayer history.

### UI-01 — Frontend design inputs exist but are unavailable at consuming paths

- **Evidence:** Actual files are `design-inputs/skills1 (1).md` and `design-inputs/skills2 (1).md`. `README.md:28-30` and `docs/workflow/02-steering-and-assets.md:14-18` say they were not supplied/expect `web/apps/revenant/design/Skills1.md` and `web/apps/ghost-lap/design/Skills2.md`. Specs 09/13 inspect only those paths (`.kiro/specs/spec-09-revenant-counterfactual-ui/design.md:29`; `.kiro/specs/spec-13-ghostlap-complete-browser/design.md:29-31`).
- **Failure / insufficiency:** A UI task follows the written path, concludes the asset is absent and improvises; the manifest also omits the actual inputs.
- **Correction:** Before each UI spec, copy the existing file unmodified to its expected path or update the spec to the actual path. Correct readiness/inventory text. Do not load these files for backend work.
- **Gate:** Specs 09/13 only.

### UI-02 — Design inputs conflict with locked product authority

- **Evidence:** The baseline defines Revenant's dark charcoal/bone-white/green system (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:222-232`), while Skills1 makes warm paper primary and rejects a dark sidebar (`design-inputs/skills1 (1).md:14-16,38-51,82-108,324-343`). Skills2 promises offline Race Your Ghost (`design-inputs/skills2 (1).md:149-150`) while Spec 13 uses server-authoritative routes (`.kiro/specs/spec-13-ghostlap-complete-browser/design.md:15-23`). Skills1 says two divergent head hashes are identical (`design-inputs/skills1 (1).md:290`), while Spec 09 correctly proves only the original remains unchanged (`.kiro/specs/spec-09-revenant-counterfactual-ui/requirements.md:23`).
- **Failure / insufficiency:** UI implementation can override locked product/authority or display a false integrity proof.
- **Correction:** Add a short compatibility decision: choose the authoritative Revenant visual system; defer offline solo with an honest unavailable state; compare the original's pre/post verification hash, never source hash to divergent branch hash. Treat exact asset suggestions as presentation guidance, not gameplay authority.
- **Gate:** Before Specs 09/13 UI implementation.

### UI-03 — Revenant responsive acceptance is missing

- **Evidence:** Skills1 defines mobile breakpoints (`design-inputs/skills1 (1).md:143-149`) and the source requires desktop/mobile smoke (`MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md:873-878`), but Spec 09 tasks 9.4–9.5 contain no responsive viewport/touch acceptance (`.kiro/specs/spec-09-revenant-counterfactual-ui/tasks.md:21-26`).
- **Failure / insufficiency:** The financial demo can pass desktop E2E while its table, queue or scrubber is unusable on the promised mobile path.
- **Correction:** Add a narrow phone/tablet Playwright matrix for navigation, Held Queue actions, chart/table fallback and touch seek.
- **Gate:** Spec 09 UI exit, not backend work.

### TOOL-01 — Conditional steering does not match the frontend files it claims to govern

- **Evidence:** Workflow says finance/game steering applies to Specs 09/13 (`docs/workflow/02-steering-and-assets.md:8-10`), while `.kiro/steering/finance-integrity.md:3-6` and `game-authority.md:3-6` match C#/tests but not `web/apps/revenant/**/*` or `web/apps/ghost-lap/**/*`.
- **Failure / insufficiency:** UI sessions omit grounded-explanation, server-authority and inert-ghost invariants.
- **Correction:** Add only the two relevant frontend globs (and narrowly relevant E2E paths). Avoid making these files always-loaded.
- **Gate:** Before Specs 09/13; no need to stop Spec 01.

### SEC-03 — Capability share-token transport lacks leakage controls

- **Evidence:** Spec 12 uses URL capability tokens, stores hashes and supports revoke/expiry (`.kiro/specs/spec-12-ghostlap-social-competitive/design.md:17-19`) but does not require referrer, cache, access-log or telemetry redaction.
- **Failure / insufficiency:** A valid bearer token can leak through a Referer header, proxy log, analytics event or cached page despite secure storage.
- **Correction:** Require `Referrer-Policy: no-referrer`, `Cache-Control: no-store`, no third-party resources on token routes, and redaction from logs/traces/errors. Test that the raw token is absent from observability output.
- **Gate:** Before Spec 12 share links are exposed.

## P3 — Optional improvements

### INV-01 — Manifest count, scope and self-hash are inaccurate

- **Evidence:** `MANIFEST.json:4` claims 63 files excluding the manifest; its `files` object has 63 entries including `MANIFEST.json` (`MANIFEST.json:206-208`). There are 66 non-`.DS_Store` files including the manifest; the context and two design inputs are unlisted. All listed non-self hashes matched; the embedded self-hash did not and cannot be stable after editing itself.
- **Failure / correction:** Audit tooling and humans receive a false completeness signal. Remove the self-entry or explicitly exclude it, define scope, include the three authoritative inputs, and regenerate count/hashes after authorized corrections.
- **Gate:** Does not delay implementation.

## Unsupported or implementation-time claims, not current defects

The package cannot yet prove ML quality, forecast calibration, cross-runtime deterministic hashes, database grants/triggers under concurrency, room-loop health under jitter, mobile FPS, real MCP connectivity, public TLS reachability or the three-minute demo. These require implementation evidence and are not reclassified as architecture failures.
