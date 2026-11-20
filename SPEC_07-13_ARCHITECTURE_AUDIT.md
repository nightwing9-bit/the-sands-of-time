# Specs 07–13: final pre-implementation architecture audit

Audit date: 11 November 2026. Repository: `/Users/rroy/Desktop/the-sands-of-time-phase1`.

## A. Executive verdict

**PROCEED AFTER TARGETED AMENDMENTS.** The architecture is strong enough to retain. The remaining work should proceed substantially as planned, after resolving eight focused findings: **0 P0, 7 P1 and 1 P2**. These are specification amendments, not evidence that the unimplemented applications currently exhibit these failures. Most of the detailed accounting, fraud-metric, privacy and replay requirements already provide useful, adversarial acceptance criteria.

The highest-impact issue is the finance version handoff. The completed reducer accepts payment intents only, deliberately leaves holds unresolved, and recognizes two frozen rules-only policies. Specs 07–09 nevertheless require review actions, compensating returns and learned risk to participate in the same deterministic history. A small, explicit additive domain-version plan is needed before payment workflow implementation. New complete runs can use that version from their creation; existing V1 runs and hashes must remain supported. Marrow's current fork cannot change a history's schema/reducer key, so changing the branch context alone cannot supply this extension.

Second, multiplayer forfeiture exceeds the current game reducer's semantics. Neutralizing movement does not remove a player from pickups, contact or automatic Burst. Spec 10 needs a precise participation rule and a compatible versioned extension of the existing simulation, rather than a second simulation or ad hoc score corrections. Third, Spec 10 must connect its new durable match receipt to lock-loss and replacement behavior. The current advisory lock explicitly permits a brief overlap after silent session loss. Unique result IDs prevent duplicate inserts but do not, by themselves, reject a stale authority's first result or resolve its race with an aborted receipt.

The other P1 corrections establish byte-exact inference evidence, make production composition and resume handoffs executable, remove an unsupported active-history fork instruction, and protect ranking caches against reordered delivery. The P2 item keeps optional sound design within the presentation scope requested for this audit. None requires a new platform, distributed coordinator or broad task expansion.

The most useful credit-saving change is to add the named existing interfaces, responsible task and one meaningful failure test to the relevant task descriptions. Existing deferral registers already assign many responsibilities correctly; those assignments need to appear at the point of implementation, not be rediscovered during a failing exit gate.

Do **not** redesign Marrow Timeline, its immutable journal, hash/codec formats, the shared scrubber, identity/session foundation, transactional outbox/inbox or modular-monolith boundaries. Preserve the one-Realtime deployment and the simple aborted/unrated crash policy. The separate **B2 joint bank/game integration gate remains PENDING**; passing individual Specs 05 and 06 does not complete it.

## B. Verified foundation and audit boundaries

### Repository and method

- Inspected HEAD: `c4c60356941b7cd41e217cc1d61bd34ee92da87e`, `feat(ghost-lap): complete Spec 06 deterministic solo and ghost branching`.
- Initial `git status --short` was empty: **no pre-existing working-tree changes**. Recent commits separately cover Specs 02–06 and the Spec 05 metric correction.
- Pass A read all 21 requirements/design/tasks documents for Specs 07–13, steering, architectural contracts/decisions/map/readiness notes, and relevant implementation-log entries. Pass B followed specific assumptions into interfaces, migrations, host compositions, reducers and tests.
- This was a static repository audit. **No build, test suite, browser journey, migration or benchmark was run.** Test source and historical test reports are distinguished below. No dependency installation, secret/local `.env` inspection, internet browsing, branch change, implementation or spec edit occurred. No intermediate research files were created.

### Implemented facts established from source

| Foundation | Inspected evidence and practical boundary |
|---|---|
| One generic Timeline engine | `src/Marrow.Timeline/TimelineEngine.cs:80–118`; `Internal/PersistentTimelineEngine.cs:107–144,224–276`. Canonical input round-trip, deterministic transition, hashed state, append/finalize/restore/fork/compare; a child inherits the source version key. |
| Durable history and version resolution | `src/Marrow.Infrastructure/Timeline/TimelineServiceCollectionExtensions.cs:8–44`; `scripts/db/migrations/marrow_timeline/0001_timeline_journal.sql`; tests in `tests/Marrow/Timeline/Persistence/` and `tests/Marrow/Timeline/Engine/`. Host registration is available, not automatically performed. |
| Identity and domain authorization | `src/Marrow.Kernel/Security/IActorContext.cs:5–14`; `ResourceAuthorization.cs:44–103`; `src/Marrow.Infrastructure/Identity/Policy/DomainPolicy.cs:7–18,74–103`. Defaults deny, domains supply resource/membership policy. Real actors currently have null tenant (`Identity/ActorContexts.cs:32–35`). |
| Reliable messaging | `src/Marrow.Infrastructure/Messaging/PostgresOutboxWriter.cs:5–32,63–84`; `RedisStreamInboxConsumer.cs:167–178`. Business/outbox and effect/inbox share their respective transactions; deduplication does not establish delivery ordering. `tests/Integration/Messaging/OutboxInboxTests.cs` contains crash/outage/duplicate evidence cases. |
| Realtime and deployment | `src/Marrow.Infrastructure/Realtime/RealtimeInstanceGuard.cs:9–18,190–215`; `RealtimeAccessRevalidation.cs:92–155`; `docs/architecture/07-realtime-room-authority.md`, “Known limits” and “Revocation of open hub connections”. One-instance startup guard, bounded revocation and snapshot recovery exist; fencing is not implied. |
| Revenant V1 | `src/Revenant/Finance/FinanceReducer.cs:8–25,67–71,128–175`; `PolicyConfig.cs:81–85,95–115,138–158`; `Timeline/FinanceTimelineVersions.cs:53–70`. Six-bank pure projection, pinned V7/V8 rules and a real Timeline adapter. No persisted ledger operations or analyst-action input. |
| Honest financial metrics | `src/Revenant/Finance/CounterfactualMetrics.cs:6–20,76–130`; `tests/Revenant/Finance/HandoffContractTests.cs:19–58,73–87`. USD minor-unit costs and pending fraud separate from terminal prevention. |
| Ghost Lap V1 | `src/GhostLap/Simulation/GameModel.cs:16–33,78–138`; `GhostLapTransition.cs:55–205`; `Solo/SoloOriginals.cs:32–87,95–150`; `Solo/SoloBranches.cs:128–206`. Canonical fixed-point state, effective input, full-round durable original and independent child; ghost is a read-only comparison projection. |
| Existing compatible ghost persistence | `scripts/db/migrations/ghostlap/0001_solo_originals.sql:21–41,64–98` and `0002_solo_branches.sql`; `tests/GhostLap/Solo/SoloOriginalTests.cs:29–31,75–76,97–98,198–229`; `SoloBranchTests.cs:30–57`. Owner/completion rows already exist; Spec 11 is a catalog/application extension. |
| Shared browser contracts/control | `web/packages/contracts/src/realtime-link.ts:1–4,62–83`; `web/packages/timeline-ui/src/TimelineScrubber.tsx`; both apps' Timeline adapters. Source includes reconnect/refetch supervision and one shared scrubber. |

### Historical evidence and uncertainty

`docs/kiro-log.md:66–120` records Spec 05's corrected units, independent reference anchors, PostgreSQL replay and browser checks. Lines 128–132 record Spec 06 automated checks, a user-played complete original and branch, and after-the-fact database/hash verification. These are **historical reports**, not tests rerun by this audit. Older log entries describing uncommitted work are superseded by current HEAD; they do not describe the present clean tree.

The latest Spec 06 exit statement (`docs/kiro-log.md:132`) explicitly preserves B2 as pending. Neither current production execution nor future ML quality, room capacity, phone FPS, final hosting or a current remote CI run was verified here. No models, ledger implementation or authoritative multiplayer implementation are claimed to exist.

## C. Spec-by-spec readiness assessment

| Spec | Readiness | Assessment and minimal action |
|---|---|---|
| **07 — Ledger/operations** | **BLOCKING AMENDMENT for 7.2 and the full parity claim in 7.5** | Excellent DB balance **and completeness** checks, immutable corrections, run namespaces, locked funds, idempotency, RLS and distinct four-eyes actors. 7.1 can begin with its current accounting invariants. Before 7.2, define the additive finance history/action version (F01). Surface production API/tenant/grant ownership in 7.2/7.4/7.5 (F03). Existing 7.1→7.5 ordering otherwise works. Current tests prove posting safety; V1 parity alone cannot prove reviewed/ML-enabled histories. |
| **08 — Fraud/ML/liquidity** | **MINOR AMENDMENTS** | Leakage protections, group/time splits, provenance, held-out confusion/prevalence, causal forecasts, qualified explanations and artifact failures are already strong. **NO CHANGE RECOMMENDED** to those evaluation requirements or to selecting supported local ML.NET APIs during implementation. Consume F01's new domain version; remove tolerance as an alternative to exact canonical replay and choose durable normalized inference evidence (F02, 8.5). Offline ETL→classifier→anomaly→forecast→pinning ordering is sound; agree the evidence shape before classifier integration. |
| **09 — Counterfactual/presentation** | **MINOR AMENDMENTS** | Strong separate Replay process/credential/DI boundary, source-before/after proof, explicit human-continuation policy, fresh runs and real five-screen workflows. **NO CHANGE RECOMMENDED** to cost units, pending-versus-prevented semantics, cancellation states or responsive workflow scope. 9.1 must consume F01/F02 rather than attempt to add review/ML behavior by a V1 context swap. F03 makes production contracts available before 9.3; 9.5 should exercise the actual product hosts. Ordering remains sound. |
| **10 — Multiplayer** | **BLOCKING AMENDMENT for 10.2–10.4** | Real clients, bounded input, single process ownership, durable start receipt, reconnect grace, explicit aborted/unrated failure and no MP archive are appropriately restrained. 10.1 can begin. Add F04 participation semantics before the loop/reconnect work and F05 receipt/authority retirement before durable finalization. F03 identifies command ingress and grants. Same-tick stable reducer behavior and 2/3/4 socket tests are useful; add stationary-forfeit and silent-lock-loss cases. No distributed room coordinator is justified. |
| **11 — Histories/replay** | **MINOR AMENDMENTS** | Reuses the codec and Timeline; verified-source→catalog single-schema transaction, no false saved receipt, private ownership, sibling isolation and storage measurements are good. **NO CHANGE RECOMMENDED** to that save boundary or to retaining originals by default. 11.1 should extend existing original/branch metadata rather than create another recording authority. F03 assigns production solo scheduling/resume and role wiring to 11.2/11.3. F06 restricts the ambiguous active-attempt instruction to the already-supported verified-original source. Catalog→save/list→challenge→viewer→gate ordering is sound. |
| **12 — Social/ranking** | **MINOR AMENDMENTS** | Bounded chat, server membership, read-only spectators, hashed/revocable shares, real proxy/browser privacy tests, transactional multi-player rating effects and casual-rating caveats are sufficient. **NO CHANGE RECOMMENDED** to these protections or to the provisional Elo formula. Add monotonic versioned cache application/rebuild and an explicit audited rating-application order (F07, 12.3/12.4). Unique match effects alone cannot prove convergence after reordered distinct updates. Dependencies on 10 and 11 are sufficient. |
| **13 — Browser/integration** | **MINOR AMENDMENTS** | Full routes, authoritative Pixi rendering, mobile cancel/blur, truthful outages, shared scrubber, real multi-context tests and human fairness review are well scoped. F03 anchors acceptance to production compositions and the C4 two-app smoke, without moving the separately owned public deployment gate into this spec. F08 makes audio optional presentation work. **NO CHANGE RECOMMENDED** to renderer boundaries, semantic placeholders, accessibility, measured performance or manual game-feel gate. No sound engine or rule retuning is needed. |

“Blocking amendment” applies to the named future task's design, not a P0 defect in implemented code. All future exit gates remain future requirements.

## D. Cross-spec dependency and ownership matrix

| Capability | Producer / owner | Consumer | Existing contract | Status / amendment |
|---|---|---|---|---|
| Finance intent projection and history | 05, completed | 7.2/7.5; 8.2/8.5; 9.1 | `FinanceReducer`, `PaymentIntent`, `FinanceTimelineVersions.V1` | Real and frozen; **F01** defines additive reviewed/ML-capable run version, preserving V1. |
| Durable run identity and ledger effects | 7.1–7.5 | 8.1–8.4; 9.1/9.5 | `ScenarioRunId`, `(run,intent)` keys; future Revenant tables/outbox | Explicitly owned, no missing DAG edge. Keep fresh account namespaces and transaction-local RLS. |
| Analyst actions and ordered replay inputs | 7.2/7.4/7.5 | 9.1, `analyst-continuation-v1` | V1 has **no** analyst-action input | **F01**: persist action facts/order with live transaction; idempotently project into new-version Timeline before verifying history. |
| Pinned risk/forecast evidence | 8.2–8.5 | 7's live workflow integration in 08; 9.1/9.2 | `PolicyRef`; existing policies require `ModelArtifactHash="none"` | **F01/F02**: new version/identity and exact evidence boundary. No dependency from 07 back to 08. |
| Replay DB/DI isolation | 7.1 defines boundary; 9.1 implements/proves | 9.3–9.5 | Same Worker binary, separate modes; Timeline public ports | Sufficient plan: permit new child/results, deny original live tables; **NO CHANGE** to boundary. |
| Product endpoints, tenants, roles, generated clients | 7.2/7.4/7.5; 10.1/10.2; 11.2/11.3 | 09, 12, 13 | `IActorContext`, resource/membership ports, `AddMarrowTimeline`, migration executor | Deferral register names owners; **F03** makes the handoffs executable at the named tasks. |
| Multiplayer participation | 10.2/10.3 | 10.4; 12.3; 13.2/13.3 | Existing reducer simulates every slot, including stationary slots | **F04**: versioned inactive-slot semantics, preserve solo V1 bytes. |
| Durable result and authority loss | 10.2/10.4 | 12.3; 13.1/13.3 | Future `match_session`, `MatchFinalizedV1`; existing advisory guard | **F05**: stale generation rejected, result/abort terminal choice atomic; no hot failover. |
| Saved original/child and catalog | 06 completed; 11.1–11.5 | 12.2; 13.4 | `SoloOriginals`, `SoloBranches`, existing stores/codecs | Catalog boundary adequate; **F03** production/resume, **F06** fork-source clarification. No new replay engine. |
| Finalized match→rating→cache | 10.4; 12.3/12.4 | 13.5 | Outbox/inbox ports; future durable rating revision | **F07** handles reordered cache delivery; all rating effects remain one PG transaction. |
| Messaging recovery/retention | 7.5 financial; 12.3 social, per existing deferrals | Their exit gates; later deployment gate | `MessagingTables`, poison/dead-letter paths | Already assigned at `docs/architecture/02-spec-map.md:93–97`; carry these named prerequisites into tasks via **F03**, no separate recovery platform. |
| Joint foundation proof | 05+06 B2 basket gate | Baseline for subsequent integrated claims | Same Timeline assembly and scrubber; both source integrity proofs | **PENDING**. Existing owner/schedule is adequate; do not relabel individual exits as B2 completion. |
| Both applications together / public access | 9.5 and 13.6 for C4; **late deployment/submission gate after 13** for public HTTPS | Final demonstration | `docs/architecture/02-spec-map.md:73`; `05-coverage-readiness.md`, “Public HTTPS/judge access” row | **F03** adds product-host/C4 verification; public deployment already has an owner and remains separate. |

Recommended sequential 07→08→09 and 10→11→12→13 order has no cycle. Independent Spec 11 does not require adding a dependency on 10 merely to reuse domain solo recording. If implemented independently, it must supply its own minimal production solo loop composition; later multiplayer can reuse the same host infrastructure.

## E. Prioritized findings register

### F01 — Define the additive finance history version before implementing reviewed payments

**P1 · HIGH confidence · 07 tasks 7.2, 7.4, 7.5; 08 tasks 8.2, 8.5; 09 task 9.1.**

- **Text at issue:** Spec 07 design §“Coupling to V1 and Worker” says the pure reducer remains authority and differences fail parity (`.kiro/specs/spec-07-revenant-ledger-operations/design.md:23`). Spec 09 requires the “same” reducer with a model adapter and replayed analyst actions (`.kiro/specs/spec-09-revenant-counterfactual-ui/design.md:15`). Their semantic goals are valid; the version/data extension is unassigned.
- **Verified evidence:** `src/Revenant/Finance/FinanceReducer.cs:20–25,67–71` explicitly excludes later review actions. `src/Revenant.Contracts/PaymentIntent.cs:36–56` has no action or inference payload. `src/Revenant/Finance/PolicyConfig.cs:81–85,138–158` resolves exactly V7/V8 rules with no model; `FinanceContext.cs:79–108` rejects other pins. `tests/Revenant/Finance/HandoffContractTests.cs:19–35` freezes the reducer surface/version. `src/Marrow.Timeline/Internal/PersistentTimelineEngine.cs:248–273` inherits the source version key on fork. `docs/architecture/02-spec-map.md:108,113–114` confirms the handoff obligations.
- **Failure inference:** A literal implementation either mutates historical V1 behavior, creates DB-only approval outcomes that replay cannot reproduce, or reaches 09 and discovers that a context override cannot encode the missing inputs. A source that verifies intents only cannot verify the actual reviewed live ledger. Model score metadata with no policy integration also fails R08.02.
- **Minimal correction:** 7.2 defines an additive Revenant history/input/context/reducer version for **new complete runs**, including ordered external review facts and extension points for pinned score evidence. Preserve existing V1 types/registrations/hashes. Persist canonical external action facts and a run-local order in the same financial transaction as the action/outbox; use restartable idempotent projection into Timeline rather than pretending its independently committed port is the ledger transaction. Declare source Verified only after the projection has caught up and matches the relevant live outcomes. 08 supplies learned evidence; 09 applies the already-defined continuation policy. New-version source and branch use the same version key from creation. Existing V1 comparisons remain supported within V1; no cross-version fork is promised.
- **Acceptance adjustment:** A hold→two approvals→settlement→permitted compensating return, interleaved with another intent, survives a crash after the live commit and before Timeline append. Cold replay reproduces statuses, reservations and balances exactly. A new-version branch records an inapplicable action explicitly; old V1 fixtures remain byte/hash identical. The 08 extension demonstrably changes a screening outcome using pinned evidence.
- **Rework avoided:** HIGH. **Amendment effort:** MEDIUM, one small domain-version subsection plus task references. **Compatibility/ADR:** additive domain version, no Timeline API change; record a focused Revenant compatibility ADR before implementation because the plan currently promises unchanged V1 reuse for broader semantics. Do not migrate or rewrite old histories by default.
- **Why not leave unchanged:** General versioning instructions do not resolve the specific inherited-fork-key constraint or identify who records actions before 09 consumes them.

### F02 — Require byte-exact canonical inference evidence, not a replay tolerance

**P1 · HIGH confidence · 08 task 8.5, evidence shape agreed before 8.2; consumed by 9.1.**

- **Text/evidence:** `.kiro/specs/spec-08-revenant-intelligence-liquidity/design.md:22` offers recomputation with “verification tolerance” or a deterministic memo. But `src/Marrow.Timeline/Internal/PersistentTimelineEngine.cs:107–144,256–268` canonically encodes inputs/state and verifies exact hashes. The pure transition is synchronously applied at line 137; it has no inference I/O transaction. Spec 09 requires exact source integrity (`design.md:15,40–42`).
- **Failure inference:** Scores within a numerical tolerance can encode different state bytes or cross a policy threshold. Restoring on another run can then produce a hash failure or a different hold. Adding tolerances to Timeline would weaken a frozen cross-app guarantee.
- **Minimal correction:** Choose the already-permitted persisted-evidence option for replay-significant scoring. Normalize/quantize with a pinned rule, record canonical feature hash/model/runtime/score evidence before verification, and replay those exact values. Generate new branch evidence from its own causal features outside the pure reducer; freeze it for later replay. Missing required evidence fails visibly. Numerical tolerances may be used to evaluate the model, never to accept different Timeline state bytes. No live predictor, database lookup or training inside `Apply`.
- **Acceptance adjustment:** Cold replay with the predictor unavailable produces identical state bytes/hashes from recorded evidence; an altered feature hash is rejected. Near-threshold score fixtures prove policy decisions use the same canonical value. A branch with changed causal features cannot reuse a source memo merely by payment ID.
- **Rework avoided:** HIGH. **Amendment effort:** SMALL. **Compatibility/ADR:** use F01's additive domain version; no generic engine change or separate ADR beyond that version plan.
- **Why not leave unchanged:** The design explicitly permits a choice that cannot satisfy exact hash replay; settling it early prevents agent experimentation inside the shared engine.

### F03 — Put production composition and handoff proofs in the owning tasks

**P1 · HIGH confidence · 7.2/7.4/7.5, 9.1/9.5, 10.1/10.2, 11.2/11.3/11.5, 12.3, 13.6.**

- **Text at issue:** Future tasks consume “REST endpoints”, hosts, queues and typed clients, but do not explicitly turn the accumulated production-host deferrals into an early deliverable. Examples: `.kiro/specs/spec-11-ghostlap-solo-history-branches/tasks.md:14–25`; `.kiro/specs/spec-07-revenant-ledger-operations/tasks.md:14–18,35–40`; `.kiro/specs/spec-13-ghostlap-complete-browser/tasks.md:42–51`.
- **Verified evidence:** `src/Hosts.Api/Program.cs:5–22`, `Hosts.Worker/Program.cs:3–11` have no domain registration. `scripts/db/migrate.sh:122–125,163–165` grants identity only to API/Realtime and no schema role to Worker; `ghostlap_runtime` is not in `RUNTIME_ROLES`. `tests/E2E/backend/GhostLapSoloEndpoints.cs:247–271` owns test-only live solo endpoints/loop. `tests/Integration/Timeline/TimelineSurfaceIsolationTests.cs:17–60` expressly expects all current domain routes absent from product API/OpenAPI. `docs/kiro-log.md:128` assigns production solo wiring/resume to 11 and Realtime grants to 10; `docs/architecture/02-spec-map.md:91–97,110–114` assigns the finance/host/messaging counterparts.
- **Security-specific evidence:** `ClaimsActorContext.TenantId` is always null (`src/Marrow.Infrastructure/Identity/ActorContexts.cs:34`); staff tokens and hub revalidation do not supply tenant (`Staff/StaffAuthService.cs:508–529`; `Realtime/RealtimeAccessRevalidation.cs:103–114`). The generic owner helper grants no second-staff role override (`src/Marrow.Kernel/Security/ResourceAuthorization.cs:72–103`). Registering both domains using the convenience methods replaces the same service (`Identity/Policy/DomainPolicy.cs:88–102`). These are extension prerequisites, not requests to rebuild authentication.
- **Failure inference:** Domain tests and fixture browser journeys pass while deployed hosts return 404/permission errors; the second approver is denied by owner-only policy; tenant tests pass only with stub actors; registering Ghost Lap replaces Revenant's policy. In-memory solo session state still prevents branch resume despite durable domain support. These failures closely match already logged integration deferrals.
- **Minimal correction:** Give each owner an explicit product-host deliverable: 07 API/LiveOps/domain policy and authenticated tenant bridge; 09 Replay; 10 room command ingress to the sole Realtime owner; 11 solo API, loop, catalog and durable resume. Register the existing Timeline and domain stores, provision least-privilege login mappings, and update production OpenAPI/generated REST DTOs plus typed hub contracts in the same owning change. Compose policies by domain/resource kind without changing the frozen ports. Preserve private guest-owner behavior and add Revenant tenant/role workflow policy. Re-read current membership/tenant during revocation checks. Carry existing messaging recovery/grant deferral rows into 7.5/12.3 acceptance, retaining their current owners.
- **Acceptance adjustment:** On real product compositions/runtime logins, two authenticated staff complete four-eyes and are denied across tenants; guest originals/branches remain private; both domain policies work when composed together. Restart production solo authority, reopen a completed original and resume a recording child at its last committed tick using durable identity; racing resumes yield one loop. Product contracts include intended domain routes while `/__e2e` remains absent. 9.5/13.6 use actual API/Realtime/Worker registrations; C4 includes both app journeys, not just fixtures. Tests should adapt the old blanket “no domain route” isolation assertion to test-only route/assembly isolation as production routes are introduced.
- **Rework avoided:** HIGH. **Amendment effort:** SMALL: pointers/acceptance additions to existing tasks, not a new spec. **Compatibility/ADR:** additive host registrations, DTOs and grants. Do not bypass domain policy or introduce cross-domain references. The slice-only assembly-wide no-Npgsql assertion in `tests/Revenant/Finance/HandoffContractTests.cs:63–69` must be scoped to pure finance code as Spec 07 introduces the domain-owned persistence already permitted by `tests/Architecture/ModuleDependencyRules.cs:43–55`; preserve reducer ABI/hash checks. This is a compatible test adaptation owned by 07, not permission to weaken pure reducer boundaries.
- **Why not leave unchanged:** The architectural registers correctly name owners, but bounded task inputs and fixture-specific acceptance can omit the actual work. A concise task reference is cheaper than repairing deployment and authorization after UI integration.

### F04 — Specify forfeited-player participation; neutral movement is insufficient

**P1 · HIGH confidence · 10 tasks 10.2/10.3; result consumer 10.4 and 12.3.**

- **Text/evidence:** `.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/design.md:17–19` requires reuse of the exact Spec 06 reducer, then “no later score accrual” after forfeit. `src/GhostLap/Simulation/GameModel.cs:16–33,78–82` has no active/forfeited participation state. `GhostLapTransition.cs:130–163` considers every overlapping slot for pickups; lines 167–205 consider all slots for contact and auto-Burst. `GameContext` pins player count (`GameModel.cs:165–177`), so deleting a slot/reindexing is not a safe neutralization trick.
- **Failure inference:** A stationary forfeited player can collect a later candle, block the living collector, contact another player or auto-Burst. Freezing only its displayed/final score leaves those world effects and the breakdown inconsistent. Conversely changing `ghost-v1` in place changes saved solo replay.
- **Minimal correction:** Pin a multiplayer participation rule: disconnect-grace players remain ordinary neutral-input participants; after forfeit preserve stable roster/slot identity but exclude them from movement, pickups, contact and Burst as source or target, and freeze their authoritative breakdown. Implement as a versioned extension of the same simulation phases with an active-participation mask; retain historical solo V1 behavior/codecs. Final placement derives from forfeit status first, as already specified. No independent multiplayer physics/scoring engine.
- **Acceptance adjustment:** Forfeit while on a future candle site and while Overlit; advance through activation/deadline with another player present. No later score/stat/world interaction belongs to the forfeited slot, the live player can collect, and result breakdown equals snapshots. Cover disconnect within grace separately and retain all existing V1 replay fixtures.
- **Rework avoided:** HIGH. **Amendment effort:** SMALL specification subsection. **Compatibility/ADR:** new multiplayer rule identity, focused version/participation ADR or explicit compatibility note under the existing version policy; no old codec mutation.
- **Why not leave unchanged:** The frozen reducer does not represent the required distinction. “Forfeit” in a room dictionary and neutral input cannot enforce it inside simulation phases.

### F05 — Fence durable result/abort decisions against a retired authority

**P1 · HIGH confidence · 10 tasks 10.2 and 10.4.**

- **Text/evidence:** `.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/design.md:17,23` relies on one Realtime process, unique result ID, lifecycle version and post-crash abort. `docs/architecture/07-realtime-room-authority.md`, “Known limits”, explicitly describes up to about 10 seconds of overlap after silent advisory-lock-session loss. `src/Marrow.Infrastructure/Realtime/RealtimeInstanceGuard.cs:190–215` discovers loss by a periodic probe; `IsHeld` is local observed state, not a transaction fence. The architecture's deferral register calls for rejecting stale owner writes (`docs/architecture/02-spec-map.md:96`).
- **Failure inference:** Terminate only the advisory-lock connection while the old process and its ordinary DB pool still work. A replacement starts before the next probe. The old process can attempt its first final result while the replacement/read path decides the receipt is aborted. A unique result key neither rejects a stale first insert nor makes a separate abort update atomic with result/outbox.
- **Minimal correction:** Keep one configured replica and **no active-room takeover**. Add a process generation/owner epoch to the minimal start receipt. Replacement startup retires unfinished predecessor receipts; finalization checks the current durable generation and atomically claims the receipt's terminal state with result+outbox. Abort and finalization serialize on that same receipt/generation decision; stale generations cannot commit. Detected guard loss also cancels all domain loops/finalization. Preserve already committed valid results. This is a small DB-backed terminal fence, not a room lease service, consensus algorithm or full state recovery.
- **Acceptance adjustment:** Delay the old guard probe, kill its lock session only, start the replacement, then race stale finalization with receipt retirement/lookup. Exactly one truthful durable terminal outcome exists; no result/event after Aborted, no stale-generation result, and a result committed before retirement survives lost acknowledgement. Assert room-loop cancellation on detected loss. Do not claim this eliminates the guard's already-documented brief stale-stream window.
- **Rework avoided:** HIGH. **Amendment effort:** SMALL. **Compatibility/ADR:** focused Spec 10 authority ADR, additive Ghost Lap receipt metadata; existing global lock and replica guard retained. No generic Timeline change.
- **Why not leave unchanged:** Existing process-crash tests need not exercise a live old process after lock-session loss; local expectedVersion alone has no cross-process meaning unless linked to durable ownership/terminal state.

### F06 — Remove the instruction to fork a currently recording solo attempt

**P1 · HIGH confidence · 11 task 11.3 and design “Solo lifecycle”.**

- **Text/evidence:** `.kiro/specs/spec-11-ghostlap-solo-history-branches/design.md:25` says an active attempt should pause and create a new child, then says to use original-only sources initially. `src/Marrow.Timeline/Internal/TimelineRules.cs:78–88` forbids forking a Recording history. `src/GhostLap/Solo/SoloBranches.cs:128–132,149–182` forks a Complete original. A partial original cannot be completed merely to make it forkable (`Solo/TickRecorder.cs:154–169`). Requirements R11.04/R11.05 concern challenging/restoring an original (`requirements.md:20–21`), so live-attempt branching is not needed to satisfy them.
- **Failure inference:** Kiro tries to complete a partial run, creates an unsupported child-of-recording path, or silently branches the original's state while labeling it the active attempt's state. Each changes the intended provenance or violates the frozen lifecycle.
- **Minimal correction:** In Phase 1, seek/fork uses a completed verified original only. During an active attempt, “rewrite” explicitly opens that original's replay at the selected valid tick; any child is sourced from the original, with that source labeled. Retain the current attempt independently. Do not fork Recording histories or promise inherited live-attempt state. Completed child viewing remains supported; nested fork support stays deferred as already planned.
- **Acceptance adjustment:** Active-attempt navigation produces a child whose parent ID, fork-state bytes and score match the explicitly chosen verified original; direct Recording-source fork remains Conflict. No partial run is promoted to an original.
- **Rework avoided:** MEDIUM. **Amendment effort:** SMALL, revise one paragraph and one existing test. **Compatibility/ADR:** no contract change or ADR; clarification selects the already-supported original-only behavior.
- **Why not leave unchanged:** Two adjacent instructions name different possible sources, one of which the implemented engine cannot fork.

### F07 — Make ranking projection monotonic under reordered events and rebuild races

**P1 · HIGH confidence · 12 tasks 12.3/12.4.**

- **Text/evidence:** `.kiro/specs/spec-12-ghostlap-social-ranking/design.md:23,27` defines transactional Elo, unique effects and a durable outbox/cache rebuild, but not application order or cache staleness. R12.08 (`requirements.md:24`) requires authoritative rebuild. `docs/architecture/07-realtime-room-authority.md`, “Outbox delivery guarantee”, explicitly disclaims aggregate ordering; `src/Marrow.Infrastructure/Messaging/RedisStreamInboxConsumer.cs:158–178` mixes reclaimed/fresh messages and dedupes by event identity only.
- **Failure inference:** Two matches change one player's rating to successive values. Applying their distinct cache events in reverse order can leave Redis at the older value, even though each event ran exactly once. A rebuild can likewise overwrite a newer live update with an older read. Elo effects themselves depend on application order; treating match end time as guaranteed transport order yields an unauditable pre-match snapshot.
- **Minimal correction:** At the existing locked rating transaction, record the authoritative rating-application order and a monotonic per-player rating revision. Phase 1 may use transaction application order; do not introduce a chronological reorder coordinator. Cache messages carry absolute rating+revision, and atomic cache application/rebuild rejects older revisions. Keep inbox+all match effects+cache outbox in the same PG transaction. Preserve the already-defined formula and uniqueness keys.
- **Acceptance adjustment:** Deliver two distinct cache updates in reverse order with duplicates, clear Redis, race an older rebuild snapshot with a newer update, and verify cache equals PG at the highest revision. Audit each Elo effect's actual pre-update snapshot/order; all n participants still update atomically. Delivery permutations need not yield identical Elo values when the audited transaction order differs.
- **Rework avoided:** MEDIUM. **Amendment effort:** SMALL. **Compatibility/ADR:** additive domain projection metadata; provisional formula unchanged, no shared messaging-order promise or ADR needed.
- **Why not leave unchanged:** Duplicate-event tests prove idempotency, not correctness after reordered different events; a plain sorted-set assignment can pass them and remain stale.

### F08 — Make sound optional presentation work, as requested

**P2 · HIGH confidence · 13 task 13.5; R13.07 and sound-design subsection.**

- **Text/evidence:** `.kiro/specs/spec-13-ghostlap-complete-browser/design.md:15,23` requires working volume/settings and an `AudioCueRouter`; `requirements.md:23` makes audio/VFX cues mandatory; `tasks.md:35–39` includes audio UX. The audit's explicit scope says audio/sound design is deferred. `docs/architecture/05-coverage-readiness.md`, external-input row “Final bespoke game art/licensing and soundtrack”, also permits semantic placeholders and later soundtrack work.
- **Failure inference:** A capable agent can spend time building required fallback WebAudio/routing/settings to close the spec, despite the user's presentation deferral. This is scope friction, not a correctness or security defect.
- **Minimal correction:** Mandatory semantic **visual** cues and reduced-motion accessibility remain. Simple gesture-unlocked optional audio/mute may be added within 13.5 if cheap; no audio subsystem or sound-production work blocks Phase 1. Omit nonfunctional volume controls when no audio exists. Record richer sound design under Phase 2 presentation.
- **Acceptance adjustment:** Core journeys pass muted and with audio absent; material states remain visible. If optional audio ships, verify user-gesture startup and mute without making it a new acceptance dependency.
- **Rework avoided:** LOW. **Amendment effort:** SMALL. **Compatibility/ADR:** none; bounded presentation scope clarification.
- **Why not leave unchanged:** The currently mandatory wording conflicts with this audit's explicit user instruction. No new task is necessary.

## F. Proposed specification amendments — approval-ready

These are proposed text only. **No edits below have been applied.** References use exact repository-relative paths. Additions belong to existing tasks; no new numbered implementation task is proposed.

### F01 — Additive finance contract and action handoff

- **Target:** `.kiro/specs/spec-07-revenant-ledger-operations/design.md`, §“Coupling to V1 and Worker”, line 23; `tasks.md`, 7.2/7.4/7.5.
- **Proposed wording:**
  > Preserve the frozen Spec-05 reducer, schemas, policies and historical hashes. In 7.2 define an additive Revenant history version for new runs containing ordered payment-intent and externally committed review/return facts, with pinned inference-evidence identity for Spec 08. Record canonical external facts and their stable run-local order with the live transaction/outbox. Project them idempotently through Timeline; source verification waits for catch-up and live-outcome parity. New sources and branches use the new version key from creation: Timeline v1 forks do not switch schema/reducer keys. Existing V1 runs remain supported within V1. Document compatibility in a focused domain ADR. 7.4 produces review facts; 7.5 proves crash/retry catch-up and cold replay including authorized release/return. No ledger capability is given to replay.
- **Companion references:** Add to `.kiro/specs/spec-08-revenant-intelligence-liquidity/tasks.md`, 8.2/8.5: “Consume Spec 07's additive version/evidence contract; do not modify frozen V1 policy IDs or reducer behavior.” Add to `.kiro/specs/spec-09-revenant-counterfactual-ui/tasks.md`, 9.1: “Consume the 07/08 versioned action/evidence stream; apply analyst-continuation-v1 to those recorded facts, preserving V1 historical support.”
- **Necessity/order:** Eliminates the incompatible context-only assumption. Contract decided in 7.2, ML filled by 08, consumed by 09; no new prerequisite cycle.

### F02 — Canonical inference boundary

- **Target:** `.kiro/specs/spec-08-revenant-intelligence-liquidity/design.md`, §“Replay model pinning”, line 22; `tasks.md`, 8.2 input and 8.5 verification.
- **Replace tolerance alternative with:**
  > Pin canonical normalized inference evidence by feature hash, model checksum and normalization/runtime version. Record original evidence and produce branch evidence from the branch's causal features outside the pure transition, then freeze it before verification. Replay consumes those exact values without predictor I/O. Numerical tolerances are evaluation-only and never substitute for equal Timeline bytes/hashes; unavailable required evidence is incompatible. Verify cold replay without a predictor, mismatched feature rejection and policy-threshold boundary cases. Agree this evidence shape before 8.2 integration.
- **Necessity/order:** Chooses an existing design option compatible with hashing; no new service. Reuses F01's version plan.

### F03 — Product composition ownership and verification

- **Targets / existing tasks:**
  - `.kiro/specs/spec-07-revenant-ledger-operations/tasks.md`, 7.2: add “Own production Revenant API/LiveOps composition, Timeline registration and matching generated contracts; implement domain workflow authorization rather than applying the generic owner-only helper to multi-staff review.”
  - Same file, 7.4: add “Implement the validated staff-tenant mapping/claim-to-actor path and fresh membership revalidation, including hub revalidation; prove two real tenant-bearing sessions and pooled RLS isolation, not only fixed actor stubs.”
  - Same file, 7.5: add “Close Spec-03/05 handoff rows in docs/architecture/02-spec-map.md: domain role/login grants, runtime DI, domain outbox/inbox/relay recovery and scoped retention/operator procedures. Prove runtime-login operation. Scope the slice-only no-driver test to pure code while retaining ABI/hash and architecture boundaries.”
  - `.kiro/specs/spec-09-revenant-counterfactual-ui/tasks.md`, 9.1/9.5: add “Register the restricted Replay mode with the production engine/domain versions and grants; run the finance journey against actual product host registrations.”
  - `.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/tasks.md`, 10.1/10.2: add “Define typed command ingress to the sole Realtime room owner; API and Realtime processes must not create separate in-memory room authorities. Provision required domain grants to the Realtime login and compose membership/stream policy.”
  - `.kiro/specs/spec-11-ghostlap-solo-history-branches/tasks.md`, 11.2/11.3: add “Own production solo/history/branch endpoints, authoritative solo loop composition, generated contracts and API/authority runtime grants, including ghostlap_runtime in migrate.sh role management. Reuse SoloOriginals, SoloBranches and their durable identities; restart/resume a recording attempt from its committed tick with one loop per attempt. Inspect the test-only bridge as a reference, never a runtime dependency. Extend existing solo_originals/solo_branches metadata through a catalog link.”
  - Same file, 11.5: add “Verify cold restart/resume and dual-domain policy composition with real guest sessions/runtime logins. Keep /__e2e and fixture assemblies absent; revise fixture-isolation assertions to allow intended production routes.”
  - `.kiro/specs/spec-12-ghostlap-social-ranking/tasks.md`, 12.3: add “Close the existing messaging recovery/retention and Worker/Realtime grant handoffs assigned to Spec 12 in docs/architecture/02-spec-map.md; reuse shared mechanisms.”
  - `.kiro/specs/spec-13-ghostlap-complete-browser/tasks.md`, 13.6: add “Run acceptance against actual product API/Realtime/Worker registrations and runtime grants. Record C4's two-app browser smoke using Spec 09's finance gate and Ghost Lap's journeys together; B2 remains separately pending until performed. Retain a bounded CI command for the new domain integration/browser suites. Public HTTPS/judge access remains the separately owned late deployment gate.”
- **Shared verification wording:** “Compose authorization by domain/resource kind without replacing the other app's policy; verify staff workflow, guest privacy, unknown-kind denial and revoked membership across API and hub.”
- **Necessity/order:** Makes already-assigned work discoverable and testable. Contracts arrive before dependent UI tasks; no new feature, host project, gate service or task dependency. Only C4 acceptance consumes Spec 09 evidence; early Ghost Lap UI work need not wait on finance.

### F04 — Participation after forfeit

- **Target:** `.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/design.md`, §“Disconnect policy v1”, line 19; `tasks.md`, 10.2/10.3.
- **Proposed wording:**
  > During disconnect grace the slot remains a neutral-input participant. At forfeit, retain stable slot/roster identity, freeze its authoritative score/stat breakdown and exclude it from movement, pickup, contact and Burst source/target participation. Pin this multiplayer extension to a new rules identity, reusing the existing simulation phases; preserve ghost-v1 solo histories/codecs. Verify future-candle overlap and pending auto-Burst after forfeit, with live-player outcomes and result breakdown consistent. Do not implement a second simulation.
- **Necessity/order:** Existing game state has no inactive-slot semantics. Decide before 10.2; reconnect and finalization consume the same rule. Version compatibility decision recorded by Spec 10.

### F05 — Durable authority/terminal decision

- **Target:** `.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/design.md`, room authority/result subsections, lines 17/23; `tasks.md`, 10.2/10.4.
- **Proposed wording:**
  > Retain the one-instance guard and no live-room takeover. A durable process generation identifies each match-start receipt. Replacement startup retires unfinished predecessor receipts; result finalization validates the current generation and atomically changes the receipt to a terminal result together with result/outbox. Abort competes on that same terminal decision; stale generations cannot commit. Preserve prior committed results. Detected guard loss cancels room loops/finalizers. Record this narrow terminal-fencing decision in an ADR. Test advisory-lock-session loss with the old process/DB pool still alive, delayed probe and a replacement racing finalization/abort. This does not promise elimination of the documented stale-stream window or distributed failover.
- **Necessity/order:** 10.2 receipt contains the authority identity; 10.4 consumes it. No extra persistent tick data.

### F06 — Original-only rewrite source

- **Target:** `.kiro/specs/spec-11-ghostlap-solo-history-branches/design.md`, §“Solo lifecycle”, line 25; `tasks.md`, 11.3 verification.
- **Replace active-attempt sentence with:**
  > Phase 1 forks only completed verified originals. During an active attempt, rewrite navigation opens that original's replay and labels it as the fork source; a new child inherits the selected original tick, never the recording attempt's divergent state. Preserve the active attempt separately. Do not complete a partial original or fork a Recording history. Verify the returned parent ID and fork-state bytes against the chosen verified original.
- **Necessity/order:** Resolves contradictory source semantics using existing engine behavior. No new dependency or nested-branch feature.

### F07 — Audited rating order and revisioned cache

- **Target:** `.kiro/specs/spec-12-ghostlap-social-ranking/design.md`, §“Ranking formula v1, idempotency”, line 23; `tasks.md`, 12.3/12.4 verification.
- **Proposed wording:**
  > Record rating-application order/pre-update snapshots and a monotonic per-player revision in the existing locked rating transaction, together with inbox, all match effects and cache outbox. Phase 1 uses audited transaction application order; it does not infer chronological match order from transport. Cache events contain absolute rating and revision; atomic application/rebuild ignores older revisions. Test reversed distinct updates, duplicates and a rebuild racing a newer update; Redis must converge to PostgreSQL's highest revision. Preserve the current formula and per-match uniqueness.
- **Necessity/order:** No transport-order dependency or new consumer platform. Revision produced in 12.3 and used in 12.4 rebuild.

### F08 — Optional audio

- **Target:** `.kiro/specs/spec-13-ghostlap-complete-browser/requirements.md`, R13.07; `design.md`, lines 15/23; `tasks.md`, 13.5.
- **Proposed wording:**
  > Server semantic events require readable visual cues and reduced-motion support. Audio is optional Phase-1 presentation polish: simple gesture-unlocked cues and mute may be added within 13.5, but sound routing/production does not block completion. Omit inactive volume controls when audio is absent. All core journeys work with audio absent or muted; richer sound design belongs to Phase 2. If audio is supplied, verify gesture startup and mute.
- **Necessity/order:** Aligns mandatory wording with the user's deliberate deferral; no backend or task-order effect.

## G. Leave-unchanged register

| Evaluated area | Decision and reason |
|---|---|
| Marrow lifecycle, journal and hashes | **NO CHANGE RECOMMENDED.** Existing engine already handles canonical steps, segment contexts, complete checkpoints, exact verification and independent suffixes. Finance domain evolution must adapt to it. |
| Historical version resolution | **NO CHANGE RECOMMENDED.** Keep old registrations and fail incompatible versions. Do not reinterpret V1 history or change ForkAsync to solve a domain-specific extension. |
| Modular monolith / three host binaries | **NO CHANGE RECOMMENDED.** Composition gaps need explicit wiring, not microservices or a new universal application framework. Separate Worker process modes remain sufficient. |
| Spec 07 DB accounting | **NO CHANGE RECOMMENDED.** Requirements/design already reject unbalanced, empty, one-legged, zero-value and unlinked groups and atomically link Settled to finalized posting. Ordered locks, bounded recognized retries, append-only compensation and run-scoped idempotency address real risks. |
| Run namespaces and two fresh runs | **NO CHANGE RECOMMENDED.** R07.12, R09.12 and 9.5 already protect both durable histories and initial per-run balances. No TRUNCATE/reset convenience is needed. |
| Four-eyes and RLS invariants | **NO CHANGE RECOMMENDED** to the stated guarantees. Existing 7.4/R07.13 checks are good; F03 supplies the real actor/composition handoff, not a replacement security model. |
| ML evaluation/provenance | **NO CHANGE RECOMMENDED.** Spec 08 already excludes truth predictors, requires group/chronological splits, prevalence/confusion metrics, causal series/backtests and qualified explanations. No quality threshold or MLOps service should be invented before measurement. |
| Financial metric truth | **NO CHANGE RECOMMENDED.** Existing code and Spec 09 distinguish pending/deferred from terminal never-settled prevention, use 250/10 USD minor units and disclose synthetic assumptions. Settled-then-returned fraud is not prevented principal. |
| Replay role and UI/source seal | **NO CHANGE RECOMMENDED.** Actual write-denial, new-child permission and before/after source proof are meaningful requirements; a branch hash differing from source is expected. Preserve the separate restricted process credential and absent live-writer DI. |
| Solo save/catalog boundary | **NO CHANGE RECOMMENDED.** Verified source then idempotent catalog transaction is sufficient. No cross-schema FK, two-phase commit or recovery daemon is justified. Existing original/branch stores remain recording authority. |
| Ghost noninterference / exact restore | **NO CHANGE RECOMMENDED.** Existing independent worlds, full state including input latch/remainder and aligned ticks provide the right foundation. A second local replay/game engine would add risk. |
| MP failure policy and deployment size | **NO CHANGE RECOMMENDED.** No permanent MP input archive or room-state recovery is required. Keep one replica, neutral/grace/forfeit and truthful aborted/unrated outcomes. F05 narrows durable terminal ownership only. |
| Social/share/privacy scope | **NO CHANGE RECOMMENDED.** Bounded room chat, casual Elo, server-authorized results, opaque hashed share tokens, revocation, no-referrer/no-store and real proxy-log tests are adequate for this project. |
| Shared browser control / design assets | **NO CHANGE RECOMMENDED.** Both apps reuse the existing scrubber with domain adapters. Specs 09/13 explicitly load the supplied design guidance narrowly, allow geometric assets and preserve responsive/accessible authoritative UI. |
| B2 and public deployment ownership | **NO CHANGE RECOMMENDED.** B2 is explicitly assigned to 05+06 and pending. Public HTTPS/judge access already belongs to the late deployment/submission gate in `docs/architecture/05-coverage-readiness.md`; do not invent an unowned deployment feature or force it into every app spec. |

## H. Deferred and out-of-scope concerns

- **Scale-out/hot room recovery:** beyond the one-Realtime deployment. A future multi-replica requirement needs the already-required room lease/fencing/affinity ADR; F05 does not authorize it. No consensus service or multiplayer journal now.
- **Scoring mix, art/layout alignment and exact game feel:** `docs/kiro-log.md:128` accepts V1 coefficients/layout and assigns human playtesting to 13, with a later version proposal if warranted. Do not change existing `ghost-v1` or `arena-v1` to match illustrative design coordinates.
- **Audio production and bespoke assets:** F08 keeps these in Phase 2 presentation. Existing semantic visuals, mute-if-present and accessibility suffice.
- **Storage compression, quotas and orphan cleanup:** measure in 11.5, then decide retention explicitly. The existing orphan-child retry test and immutable source references make an unreachable orphan a storage concern, not permission to delete journal rows. No hidden solo expiry or background reconciler is needed now.
- **PaySim availability, selected trainer/API, model quality and calibrated forecast bands:** implementation measurements owned by 08, with its lawful fixture/fallback and honest limitations. No internet/API selection investigation was necessary for this audit, and no dataset suitability or inference throughput is claimed.
- **Public hosting, external URLs, credentials and judge staff access:** owned by the existing late deployment/submission gate after 13. Actual public HTTPS and external browser smoke remain required there, not completed by local fixture tests. No infrastructure credentials were inspected.
- **Staff account recovery/extra login controls:** the existing Spec-03 deferral register asks 07/09 to decide when accounts extend beyond dev seeding. Do not add an account-management project solely for the hackathon's controlled demo identities.
- **Messaging operations already assigned to 07/12:** trimming, poison/dead-letter requeue, restricted grants and measured fsync throughput are implementation obligations at those owners, not new findings or Phase 2 deferrals. F03 points the existing tasks at the register. Redis-volume loss remains outside the supported automatic-delivery guarantee; follow the existing bounded operator recovery plan rather than promising disaster-proof delivery.
- **No speculative hypotheses carried as defects:** no banking-core expansion, anti-smurf platform, global social archive, assumed performance target achievement or unverified current test failure is recommended.

## I. Recommended implementation readiness decision

**PROCEED AFTER TARGETED AMENDMENTS.**

1. Review and approve F01–F07 before implementing the affected behavior. Apply F08 if the stated audio deferral remains the intended Phase 1 scope. This report does not apply approvals or edits.
2. **7.1 can begin safely** with its existing ledger invariants. **7.2 and full 7.5 parity wait for F01**; 7.4's real authorization proof needs F03. Keep 08 and 09 after their existing earlier exit gates; settle F02 before 8.2 integration/8.5, and use F01's output in 9.1.
3. **10.1 can begin safely** against completed 03/06 interfaces while following F03 composition ownership. **10.2/10.3 wait for F04's participation/version decision; 10.2 receipt and 10.4 finalization wait for F05.** No multi-replica deployment is authorized.
4. **11.1 can begin safely** by extending existing solo records/catalog. Complete F03/F06 before 11.2/11.3 and their production exit proof. **12.1/12.2 can proceed after 10/11 gates; 12.3/12.4 need F07.** Spec 13 follows its existing dependencies, consuming F03's product contracts and F08's presentation scope.
5. **B2 remains PENDING.** Perform the existing joint engine/scrubber/nonmutation demonstration as its own outstanding integration gate; do not rewrite completed individual exits or treat this audit as execution evidence. C4 after 13 must show both apps working together; public access remains a later separately owned deployment gate.

No foundational rebuild or new numbered implementation task is required. The amended specifications should remain compact: one finance version subsection, a few exact invariants and references/tests attached to existing tasks.

## J. Compact approval checklist

1. **F01 [P1]** — Additive finance action/model-capable history version, durable ordered action handoff, preserved V1 compatibility and focused ADR.
2. **F02 [P1]** — Exact persisted canonical inference evidence; no tolerance-based Timeline verification.
3. **F03 [P1]** — Explicit product-host/tenant/policy/grant/contracts/resume handoffs and real-composition acceptance at existing owner tasks.
4. **F04 [P1]** — Versioned multiplayer inactive participation and frozen post-forfeit breakdown using the existing simulation.
5. **F05 [P1]** — Durable authority generation and atomic result/abort fence, including silent lock-loss test; keep one replica/no takeover.
6. **F06 [P1]** — Fork only a labeled completed verified original; remove the recording-attempt fork ambiguity.
7. **F07 [P1]** — Audited rating-application order and monotonic absolute cache revisions across reordered delivery/rebuild.
8. **F08 [P2]** — Optional bounded audio in 13.5; richer sound design deferred to Phase 2.
