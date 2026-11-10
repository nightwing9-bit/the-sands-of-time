# Minimal Remediation Plan

This is a patch plan for the existing architecture, not a replacement design. Preserve the 13 spec directories and edit only the documents named for each correction. P1 corrections are mandatory before the stated gate; P2 corrections may wait until their owning spec. P3 cleanup must not delay implementation.

## Gate summary

| Gate | Required corrections | May proceed afterward |
|---|---|---|
| Before first Kiro implementation session | R-01, the executor sentence in R-02 | Spec 01 |
| Before Spec 02 | R-03, R-04 | Timeline implementation and all later consumers |
| Before Specs 05/06 cross-app exit | R-05 | Parallel 05/06 slices and B2 proof |
| Before Spec 06 codec/reducer freeze | R-12, Spec 06 portion of R-13 | Deterministic game implementation |
| Before Spec 07 exit | R-06, R-11, LiveOps side of R-09 | Ledger/security claims |
| Before Spec 09 exit | R-07, R-08, R-09, R-10, Revenant portion of R-15/R-16 | Financial hero demo |
| Before Specs 10–12 exit | R-14, R-17 and relevant R-12/R-13 | Robust multiplayer/social claims |
| Before Spec 13/submission exit | Ghost portion of R-15, R-18 | Public demo completion |

## Corrections

### R-01 — Place dependency-free shared contracts

- **Severity / priority:** P1; first.
- **Files to update:** `docs/architecture/01-master.md`; `.kiro/specs/spec-01-foundation-bootstrap/design.md`; `.kiro/specs/spec-01-foundation-bootstrap/tasks.md`; `.kiro/specs/spec-03-identity-realtime-outbox/design.md`; relevant architecture steering/test ownership text.
- **Smallest change:** Choose exactly one option: place pure actor/realtime/outbox records/interfaces explicitly in `Marrow.Kernel`, or add one small dependency-free `Marrow.Contracts`/application-abstractions project to the scaffold. State that Infrastructure implements these abstractions and domains cannot reference Infrastructure.
- **Update together:** legal dependency graph, Spec 01 project list/references, Spec 03 file ownership and architecture tests.
- **Acceptance:** A written project-reference graph can place every public Spec 03 abstraction without a domain → Infrastructure reference or cycle.
- **Must implementation wait?** **Yes.** Spec 01 task 1.1 must wait; no other redesign is needed.

### R-02 — Clarify migration and outbox instantiation ownership

- **Severity / priority:** P2; first-session scaffold clarification.
- **Files to update:** `.kiro/specs/spec-01-foundation-bootstrap/design.md`; `.kiro/specs/spec-03-identity-realtime-outbox/design.md`; `.kiro/specs/spec-03-identity-realtime-outbox/tasks.md`; where necessary Specs 07, 10, 11 and 12 migration/outbox tasks.
- **Smallest change:** Spec 01 provisions PostgreSQL and one migration-runner mechanism and names one executor. Spec 03 proves the reusable outbox/inbox with an Identity/test-owned transaction. Each later domain owns its schema, UoW and outbox instantiation.
- **Update together:** host startup responsibility and CI migration command.
- **Acceptance:** No host races startup migrations, no early spec writes another module's schema, and later domain transactions have explicit atomic outbox tests.
- **Must implementation wait?** Only the executor/ownership sentence must precede Spec 01; domain instantiation details may wait.

### R-03 — Complete the Timeline v1 public contract

- **Severity / priority:** P1; before Spec 02.
- **Files to update:** `docs/architecture/03-contracts.md`; `.kiro/specs/spec-02-timeline-journal-simulation/{requirements,design,tasks}.md`; `.kiro/specs/spec-04-shared-web-timeline/design.md`; affected references in Specs 05/06/09/11.
- **Smallest change:** Add source create, append, complete/verify and aligned compare operations; make source/branch identities addressable through one history ID or explicit mapping; wrap all operations in a typed result/error union. Freeze position 0, first record/tick, inclusive restore range, final position and `T+1` continuation semantics.
- **Update together:** Browser error mapping and Spec 02 contract tests.
- **Acceptance:** A client can create and complete a source, fork it, restore/replay the returned child, compare aligned histories, and exhaustively handle promised errors without calling an unspecified store API.
- **Must implementation wait?** Spec 02 and all Timeline consumers must wait. Corrected Spec 01 need not.

### R-04 — Make pinned transition context durably resolvable

- **Severity / priority:** P1; before Spec 02.
- **Files to update:** `docs/architecture/03-contracts.md`; Spec 02 requirements/design/tasks; adapter/context sections in Specs 05/06/11.
- **Smallest change:** Define an immutable context codec/resolver. Store canonical bytes or a durable content-addressed reference plus schema version and hash before acknowledging history/branch creation. Never read current configuration during replay.
- **Update together:** manifest shape, compatibility error and restart test fixtures.
- **Acceptance:** After process restart, a history replays with the exact original context and matching hash, or fails with `IncompatibleVersion` while preserving bytes.
- **Must implementation wait?** Spec 02 finalization must wait.

### R-05 — Move the cross-app proof to the actual B2 gate

- **Severity / priority:** P1; before Spec 06 completion.
- **Files to update:** `.kiro/specs/spec-06-ghostlap-v1-slice/requirements.md`; `tasks.md`; `docs/architecture/02-spec-map.md`; `docs/workflow/01-execution.md` if its basket checklist mirrors the gate.
- **Smallest change:** Remove “Revenant V1 already exists” from Spec 06's local exit/task and run the proof after both 05 and 06 at B2. Alternatively declare 05 → 06, but that sacrifices safe parallelism.
- **Update together:** B2 acceptance must assert the same compiled Timeline implementation is used by both adapters.
- **Acceptance:** 05 and 06 can complete independently; B2 cannot pass until finance and game record/restore/fork/compare through the same package.
- **Must implementation wait?** No for 01–05 or the independent 06 kernel; yes before 06 is declared complete/B2 begins.

### R-06 — Enforce posting-group completeness, not only equality

- **Severity / priority:** P1; before Spec 07 exit.
- **Files to update:** `.kiro/specs/spec-07-revenant-ledger-operations/{requirements,design,tasks}.md`; financial integrity steering if it describes the invariant.
- **Smallest change:** Require finalized groups to contain expected non-zero legs in one currency with a settlement link; update payment to `Settled` only with that finalized group in the same transaction.
- **Update together:** DB constraint/trigger strategy and integration fixtures.
- **Acceptance:** Empty, one-leg, zero-value and missing-leg commits fail; one valid balanced group settles exactly once; duplicate settlement remains idempotent.
- **Must implementation wait?** Spec 07's DB migration/settlement task must wait; earlier specs need not.

### R-07 — Define terminal versus deferred fraud outcome metrics

- **Severity / priority:** P1; before Spec 09 comparison/UI.
- **Files to update:** Spec 05 design metric wording; `.kiro/specs/spec-09-revenant-counterfactual-ui/{requirements,design,tasks}.md`; any demo script/coverage row that names “fraud prevented.”
- **Smallest change:** Count principal as prevented only after terminal rejection/return or a complete final scenario horizon. Label nonsettlement at a partial horizon as deferred and exclude it from avoided-exposure/net-effect totals.
- **Update together:** typed comparison DTO labels and UI copy.
- **Acceptance:** A fraudulent payment held at comparison time but later approved never appears as prevented; metric fixtures state horizon/finality.
- **Must implementation wait?** Only Spec 09 metric implementation and demo claims.

### R-08 — Select a versioned post-fork human-action policy

- **Severity / priority:** P1; before Spec 09 replay.
- **Files to update:** `docs/architecture/01-master.md` decision reference; Spec 09 requirements/design/tasks; Friday Ring scenario documentation/fixtures.
- **Smallest change:** Choose one deterministic rule, e.g. replay recorded action only when stable case/payment preconditions still hold, apply a documented deterministic fallback for newly held cases, and fail unresolved actions. Persist its version in replay context.
- **Update together:** continuation validation and comparison explanation.
- **Acceptance:** A fixture with an original post-fork approval and a changed v8 hold set has one repeatable, explainable result; unresolved action cannot silently disappear.
- **Must implementation wait?** Spec 09 replay must wait.

### R-09 — Enforce Replay/LiveOps isolation with process mode and DB role

- **Severity / priority:** P1; before Spec 09 nonmutation claim.
- **Files to update:** `docs/architecture/01-master.md` physical Worker map; Spec 07 Worker/grant sections; `.kiro/specs/spec-09-revenant-counterfactual-ui/{design,tasks}.md`; deployment configuration plan.
- **Smallest change:** Use the same Worker executable in explicit `LiveOps` and `Replay` modes. Replay registers no settlement writer and uses a DB credential unable to mutate live ledger/payment tables, while allowed to read source and write counterfactual results.
- **Update together:** DI graph test, PostgreSQL grants and secret/config names.
- **Acceptance:** Replay startup has the expected registrations, and a direct attempted live-ledger write fails at PostgreSQL while counterfactual output succeeds.
- **Must implementation wait?** Specs 07/09 worker/grant tasks must respect the decision; unrelated work may proceed.

### R-10 — Add append-only-safe scenario run identity

- **Severity / priority:** P1; before repeatable demo E2E.
- **Files to update:** Spec 05 seed/scenario design; Spec 07 idempotency/tenant or run-key design; Spec 09 E2E setup/tasks.
- **Smallest change:** Introduce `ScenarioRunId` (or equivalent demo tenant/run namespace) and an authorized create-fresh-run command. Include it in idempotency/business keys; never reset by deleting or rewriting prior ledger records.
- **Update together:** seed receipts, navigation/selection of current run and cleanup policy.
- **Acceptance:** Two consecutive judge runs produce equivalent scenario facts under distinct namespaces, both remain auditable, and no duplicate/final-state collision occurs.
- **Must implementation wait?** The final Spec 09 E2E/demo gate must wait; pure seed work need not.

### R-11 — Freeze executable RLS semantics

- **Severity / priority:** P2; before Spec 07 RLS claim.
- **Files to update:** Spec 07 requirements/design/tasks and deployment DB-role notes.
- **Smallest change:** Separate migration owner/runtime roles, apply `FORCE ROW LEVEL SECURITY` where claimed, set tenant context transaction-locally, and prohibit table-owner/BYPASSRLS runtime credentials.
- **Update together:** connection pooling and background-worker tenant selection.
- **Acceptance:** Pooled A→B→A requests cannot read/write another tenant and no tenant setting survives transaction reuse.
- **Must implementation wait?** Only RLS-dependent Spec 07 tasks/exit.

### R-12 — Complete canonical game input and score state

- **Severity / priority:** P2; before Spec 06 codec/reducer freeze.
- **Files to update:** `docs/architecture/03-contracts.md` game contract; Spec 06 design/tasks; Spec 10 input design/tasks; Spec 11 codec/test references.
- **Smallest change:** Define highest accepted sequence, age/window, hold-last versus neutral, Burst edge and late rejection. If latched, persist effective input/last sequence. Add accumulated fixed-point authoritative distance or remainder plus score components to canonical state.
- **Update together:** codecs and packet-loss/checkpoint fixtures.
- **Acceptance:** Packet loss and restore during active movement are deterministic; restore at a 1023/1024 distance boundary produces the same next score as uninterrupted play.
- **Must implementation wait?** The first final game codec/reducer must wait; foundation work may proceed.

### R-13 — Remove codec duplication and define save finalization

- **Severity / priority:** P2; before Specs 06/11 persistence is frozen.
- **Files to update:** Specs 06 and 11 design/tasks; `docs/architecture/01-master.md` save semantics if necessary.
- **Smallest change:** Make Spec 06 owner of the final Ghost codec/Timeline record shape. Change Spec 11 task 11.1 to reuse/verify it and add catalog/privacy/idempotency. Define idempotent `Pending → Verified/Failed` finalization and reconciliation across `marrow_timeline` and `ghostlap`; return “saved” only when verified.
- **Update together:** failure injection and orphan reconciliation tests.
- **Acceptance:** No second codec implementation exists; a crash between schema writes converges to Verified or Failed without a false save receipt.
- **Must implementation wait?** Correct ownership before Spec 06 codec completion; finalization may be implemented in 11.

### R-14 — Define realtime finalization failure outcome

- **Severity / priority:** P2; before Spec 10 robust exit.
- **Files to update:** `.kiro/specs/spec-10-ghostlap-authoritative-multiplayer/{requirements,design,tasks}.md`; result/rematch UI expectations in Spec 13 if `Aborted/Unrated` is chosen.
- **Smallest change:** Either persist a minimal terminal-finalization intent before room disposal, or define restart recovery as `Aborted/Unrated` with visible rematch. Do not add multiplayer history.
- **Update together:** rating event emission and unique-result logic.
- **Acceptance:** Crash before result commit and DB outage at finish produce one documented outcome, never a phantom rating or permanently ambiguous match.
- **Must implementation wait?** Only Spec 10 finalization/result tasks.

### R-15 — Put design inputs at the consumer boundary and resolve authority conflicts

- **Severity / priority:** P2; before Specs 09/13 UI work.
- **Files to update:** `README.md`; `docs/workflow/02-steering-and-assets.md`; Specs 09/13 design references; optionally place unmodified copies at `web/apps/revenant/design/Skills1.md` and `web/apps/ghost-lap/design/Skills2.md` when those app directories are created; inventory text.
- **Smallest change:** Correct the false “not supplied” status and use one exact path per asset. Add a short compatibility note: locked Revenant visual authority wins; offline Ghost mode is deferred/unavailable; original pre/post verification hashes may match, source and divergent branch hashes may not.
- **Update together:** UI tasks must classify design files as presentation input, not server/game/finance authority.
- **Acceptance:** A fresh UI session finds the intended file without loading it during backend work and cannot implement the three known contradictions.
- **Must implementation wait?** Specs 09/13 frontend tasks only.

### R-16 — Load frontend invariants and test Revenant responsiveness

- **Severity / priority:** P2; before frontend exits.
- **Files to update:** `.kiro/steering/finance-integrity.md`; `.kiro/steering/game-authority.md`; Spec 09 tasks; relevant E2E path matches.
- **Smallest change:** Add `web/apps/revenant/**/*` and `web/apps/ghost-lap/**/*` (plus narrowly relevant E2E) to conditional `fileMatch`. Add a small phone/tablet viewport matrix for navigation, Held Queue, chart/table fallback and touch Timeline seek.
- **Update together:** Keep steering conditional; do not make design assets always-loaded.
- **Acceptance:** Frontend sessions receive only their domain invariants, and Revenant's promised mobile interaction passes targeted E2E.
- **Must implementation wait?** Only Specs 09/13 UI execution.

### R-17 — Harden capability share-token transport

- **Severity / priority:** P2; before Spec 12 share release.
- **Files to update:** `.kiro/specs/spec-12-ghostlap-social-ranking/{requirements,design,tasks}.md`; token-route behavior in Spec 13.
- **Smallest change:** Require `Referrer-Policy: no-referrer`, `Cache-Control: no-store`, no third-party subresources on token routes and raw-token redaction from logs/traces/errors.
- **Update together:** reverse proxy/access-log configuration and browser test.
- **Acceptance:** Valid revoke/expiry still work and the raw token is absent from request logs, telemetry, Referer and caches.
- **Must implementation wait?** Share-link tasks only.

### R-18 — Add one public HTTPS and judge-access exit gate

- **Severity / priority:** P1; late but mandatory before submission.
- **Files to update:** `docs/architecture/05-coverage-readiness.md`; Spec 13 requirements/tasks or one clearly linked deployment/submission checklist; workflow/demo checklist.
- **Smallest change:** Add single-instance production Compose, TLS reverse proxy, Revenant/Ghost routes, production secret validation, staff/MFA instructions and external browser smoke. No fleet/IaC platform is required.
- **Update together:** source-map/error hygiene, health checks and actual public URLs/evidence.
- **Acceptance:** A machine outside the deployment network reaches both HTTPS apps, a judge follows access instructions, and one Revenant plus one Ghost critical journey passes.
- **Must implementation wait?** No early spec waits; submission/final completion must.

## Optional cleanup

### R-19 — Repair manifest accounting

- **Severity / priority:** P3; opportunistic.
- **Files to update:** `MANIFEST.json`, `README.md` inventory text.
- **Smallest change:** Define manifest scope, remove the self-hash/self-entry or exclude it explicitly, include the context and design inputs, then regenerate count/hashes after authorized architecture corrections.
- **Acceptance:** Count equals scoped files and every listed hash is stable/verifiable.
- **Must implementation wait?** No.

## Explicit non-remediations

Do **not** regenerate the 39 Kiro documents, split the modular monolith into services, add distributed room coordination, introduce a full event-sourcing framework, archive all multiplayer ticks, build production-bank infrastructure, or reconstruct missing final art. None is needed to close the findings and each would consume scarce credits without improving the promised proof.
