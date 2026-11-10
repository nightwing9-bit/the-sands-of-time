# Spec-by-Spec Review

Statuses describe the document as delivered, not the feasibility of its product area.

- **PASS:** executable within normal implementation discretion.
- **PASS WITH CHANGES:** sound scope; bounded correction should be made before its affected task/exit.
- **REQUIRES CORRECTION:** a P1 or scaffold-affecting contradiction must be resolved before this spec is implemented/completed.
- **INCOMPLETE:** absent or too insubstantial to execute. No spec is in this category.

All 13 directories contain substantive requirements, design and tasks. Their declared dependency graph is acyclic. The decomposition is appropriate; deleting or regenerating a spec would cost more than correcting the identified boundaries.

## 01 — `spec-01-foundation-bootstrap`

- **Responsibility:** solution/project scaffold, legal references, baseline hosts/web workspaces, local Compose, scripts, CI and initial quality checks.
- **Dependencies:** none; correct for a bootstrap spec.
- **Contract consistency:** Project graph is well motivated, but shared low-level contracts later owned by Spec 03 have no legal dependency-free location. This affects task 1.1, not merely later code (`docs/architecture/01-master.md:37-47`; Spec 03 `design.md:5,15-23`).
- **Requirement completeness:** Covers the intended empty-repository start. Clarify one migration executor and that this spec provisions DB services/mechanism rather than owning domain migrations.
- **Tests:** Smoke, architecture checks and CI bootstrap are proportionate. CI absence today is intentional because task 1.4 creates it.
- **Executability / credits:** Do not let Kiro invent a contracts assembly mid-scaffold. A short planning correction is cheaper than reference churn.
- **Status:** **REQUIRES CORRECTION** — resolve ARC-01 and the migration-runner sentence before task 1.1.

## 02 — `spec-02-timeline-journal-simulation`

- **Responsibility:** the shared immutable journal, snapshots, deterministic restore/replay/fork/continue/compare, hashing and concurrency behavior.
- **Dependencies:** 01 only; valid once the scaffold is corrected.
- **Contract consistency:** The requirements are strong, but the frozen interface has no create/append/compare, inconsistent branch/history addressing, no typed-result shape, ambiguous position-zero mechanics, and no durable `TContext` recovery (`docs/architecture/03-contracts.md:11-37`).
- **Requirement completeness:** Corruption, cancellation, repeated replay, immutable prefixes and concurrent forks are covered. Context artifact retention/recovery is missing.
- **Tests:** Excellent adversarial intent, including corrupt checkpoints and concurrent identical forks. Add restart/context-resolve, exact first/final position and branch-addressability tests.
- **Executability / credits:** This is the most expensive place to improvise. Freeze the corrected API before code; do not regenerate the spec.
- **Status:** **REQUIRES CORRECTION** — TL-01 and TL-02 block its implementation exit.

## 03 — `spec-03-identity-realtime-outbox`

- **Responsibility:** actor identity/session/staff authority, realtime envelopes, idempotent inbox/outbox patterns and transport foundation.
- **Dependencies:** 01; valid in sequence.
- **Contract consistency:** `IActorContext` and shared envelopes/mechanics are assigned under Infrastructure even though later domains cannot legally reference it. “Per module” outbox proof arrives before most domain modules own UoWs/migrations.
- **Requirement completeness:** Guest/staff distinctions, idempotency and transport failures are substantively covered. It should prove the reusable pattern using an Identity/test-owned transaction, not pre-own future schemas.
- **Tests:** Duplicate delivery and authorization coverage are appropriate. Add the architecture dependency test after contract placement is corrected.
- **Executability / credits:** Bounded changes to ownership language avoid a new assembly appearing during Specs 05–12.
- **Status:** **REQUIRES CORRECTION** — update with ARC-01/PERSIST-01 before implementation.

## 04 — `spec-04-shared-web-timeline`

- **Responsibility:** one reusable React Timeline interaction, typed browser adapter, accessibility and domain-specific projections.
- **Dependencies:** 02 and 03; correct.
- **Contract consistency:** The component boundary is appropriately generic and does not leak finance/game concepts. Its error mapping and compare interaction depend on the missing Spec 02 result/compare contract.
- **Requirement completeness:** Seek/fork/compare, cancellation, accessibility and adapter separation are covered.
- **Tests:** Component/adapter tests are proportionate. Add exhaustive typed-error mapping once TL-01 is frozen.
- **Executability / credits:** No redesign; consume the corrected API rather than inventing browser-only semantics.
- **Status:** **PASS WITH CHANGES** — executable after corrected Specs 02/03.

## 05 — `spec-05-revenant-v1-slice`

- **Responsibility:** deterministic Friday Ring facts, v7/v8 pure policy transitions, finance Timeline adapter and first real cross-app foundation proof.
- **Dependencies:** 02, 03 and 04; valid.
- **Contract consistency:** Finance state/input/context stay in the adapter, which is the right reuse boundary. It relies on Timeline operations missing from the frozen API.
- **Requirement completeness:** Seed determinism, causal decisions and original immutability are covered. The early “fraud held/rejected before settlement” metric must not later be treated as terminal prevention.
- **Tests:** Pure transition, replay and adapter tests are strong. Add a metric fixture distinguishing held/deferred from terminally prevented.
- **Executability / credits:** Scope is a sensible early thin slice. Keep ML, ledger and UI out of it as planned.
- **Status:** **PASS WITH CHANGES** — consume TL fixes and align FIN-02 terminology.

## 06 — `spec-06-ghostlap-v1-slice`

- **Responsibility:** deterministic fixed-tick game kernel, complete canonical `GameState`, full solo run, persistence, branching and first game adapter.
- **Dependencies:** Declares 02, 03, 04; those are sufficient for its own slice. Its bank-proof exit/task also requires parallel Spec 05, contradicting the declared graph.
- **Contract consistency:** Fixed-point movement, precomputed schedules, stable same-tick order and separate worlds are strong. Connected missing-input/latch state and fractional distance score state are not fully frozen.
- **Requirement completeness:** The full 2,700-step original, refresh and branch are real. It already owns the final codec/persistence proof that Spec 11 later appears to reimplement.
- **Tests:** Good deterministic/property coverage. Add packet-loss/effective-input snapshot and non-divisible distance-boundary restore tests.
- **Executability / credits:** Preserve parallel 05/06 work by moving their joint proof to B2. Declare codec ownership now to avoid paying twice.
- **Status:** **REQUIRES CORRECTION** — DAG-01 is P1; GAME-01/02 and PERSIST-02 are bounded additions.

## 07 — `spec-07-revenant-ledger-operations`

- **Responsibility:** durable payment/hold/approval/settlement state, append-only double-entry ledger, tenant/security authority and transactional outbox.
- **Dependencies:** 03 and 05; valid.
- **Contract consistency:** Transaction and idempotency boundaries are generally credible. The DB equality trigger does not prove a posting group is complete, and Worker modes/DB roles are not separated.
- **Requirement completeness:** Lifecycle, concurrent staff action, duplicate initiation/settlement, corrections and source audit are covered. RLS runtime semantics need explicit roles and transaction-local tenant context.
- **Tests:** Strong concurrency/conservation intent. Add empty/one-leg/zero/missing-leg groups, replay-role write denial and pooled A→B→A RLS tests.
- **Executability / credits:** These are small SQL/security contract fixes with high payoff; no production-banking platform is needed.
- **Status:** **REQUIRES CORRECTION** — FIN-01 blocks the exit; SEC-01/02 must be settled before claims are made.

## 08 — `spec-08-revenant-intelligence-liquidity`

- **Responsibility:** ML.NET classifier/anomaly/forecast components, versioned model evidence, grounded explanations and liquidity projections driven by actual outcomes.
- **Dependencies:** 05 and 07; correct.
- **Contract consistency:** Model/version ownership and causal connection to payments are clear; it does not displace ledger authority.
- **Requirement completeness:** Synthetic-data limitations, train/evaluation separation, fallback behavior and grounded explanations are appropriately bounded for a hackathon.
- **Tests:** Planned leakage checks, deterministic version pinning, explanation assertions and projection integration are proportionate. Real quality/calibration remains implementation evidence.
- **Executability / credits:** A sensible higher-reasoning allocation; avoid prematurely tuning models before the causal pipeline works.
- **Status:** **PASS**.

## 09 — `spec-09-revenant-counterfactual-ui`

- **Responsibility:** isolated v8 continuation, financial comparison metrics, all five functional Revenant screens and browser proof of immutable originals.
- **Dependencies:** 04, 07 and 08; valid.
- **Contract consistency:** It correctly separates counterfactual output from the real ledger, but DI exclusion alone does not enforce that boundary. Human-decision continuation and terminal outcome semantics are still placeholders.
- **Requirement completeness:** The real hero journey exists. Repeatable append-only-safe scenario runs, public deployment ownership and mobile acceptance are absent; Skills1 is at an unexpected path and has authority conflicts.
- **Tests:** Cancellation/source-hash verification and five-screen E2E are good. Add DB denial, post-fork analyst action, held-vs-prevented metrics, fresh-run idempotency and phone/tablet paths.
- **Executability / credits:** Resolve semantics before UI/chart work; otherwise impressive visuals will encode false metrics and consume the reserve.
- **Status:** **REQUIRES CORRECTION** — FIN-02/03, SEC-01 and PROD-01 block the signature financial proof.

## 10 — `spec-10-ghostlap-authoritative-multiplayer`

- **Responsibility:** 2–4 player lobby/match, serialized server loop, input validation, reconnect/forfeit, authoritative result and rematch.
- **Dependencies:** 03 and 06; correct. It reuses the pure reducer and remains separate from solo history.
- **Contract consistency:** Single-room authority and input windows are sound. Empty/late connected-input behavior is missing, and terminal crash/DB outage has no defined durable outcome.
- **Requirement completeness:** Invitations, real participants, reconnect, results and server authority are covered without overreaching into permanent multiplayer replay.
- **Tests:** Add packet-loss semantics and crash-before-result/DB-unavailable finalization. Existing duplicate-result and input-authority tests are valuable.
- **Executability / credits:** One replica and one serialized loop per room are lean. Do not introduce distributed room coordination for Phase 1.
- **Status:** **PASS WITH CHANGES** — GAME-01 and RT-01 are local gates.

## 11 — `spec-11-ghostlap-solo-history-branches`

- **Responsibility:** durable history catalog, reload/challenge/seek/fork, immutable ghost playback, independent child history and aligned comparison.
- **Dependencies:** 04 and 06; correct.
- **Contract consistency:** The complete journey is coherent, including T=0, penultimate/final ticks and sibling forks. It duplicates Spec 06 codec/record implementation and lacks an explicit cross-schema save-finalization protocol.
- **Requirement completeness:** Rule incompatibility fails closed, original remains immutable, historical ghost is inert, and final position is inspect-only. Canonical score remainder/effective-input state must be inherited from Spec 06.
- **Tests:** Strong adversarial matrix. Add catalog reconciliation/orphan recovery and explicitly reuse the Spec 06 codec fixtures.
- **Executability / credits:** Narrow task 11.1 to extension/verification rather than reimplementation.
- **Status:** **PASS WITH CHANGES** — PERSIST-02 and GAME-02 corrections before persistence freeze.

## 12 — `spec-12-ghostlap-social-ranking`

- **Responsibility:** chat/emotes, spectators, capability shares, ranking/rating and duplicate-safe event processing.
- **Dependencies:** 03, 10 and 11; valid.
- **Contract consistency:** Spectator and rating authority are correctly server-side; outbox/ranking idempotency is coherent after Spec 03 ownership correction.
- **Requirement completeness:** Social/competitive requirements are covered. URL share-token transport lacks leakage controls even though hash/revoke/expiry are present.
- **Tests:** Duplicate ranking event and spectator-input denial are strong. Add raw-token absence from logs/referrers/caches.
- **Executability / credits:** Correctly deferred after core match/history; no need for a separate social service.
- **Status:** **PASS WITH CHANGES** — SEC-03 before exposing shares.

## 13 — `spec-13-ghostlap-complete-browser`

- **Responsibility:** complete React/Pixi browser experience, ten routes, mobile controls, failure states, full journeys and demo evidence.
- **Dependencies:** 04, 10, 11 and 12; correct.
- **Contract consistency:** Renderer remains a server-state consumer; semantic fallbacks prevent absent final media from blocking functionality. Skills2's offline claim must not override server authority.
- **Requirement completeness:** The requested browser journey is broad and concrete. Skills2 is not at the expected path; a public HTTPS/judge-access exit is still ownerless.
- **Tests:** Accelerated deterministic E2E plus a separate real-time smoke is a sound balance. Add external deployed smoke and token-route leakage coverage inherited from Spec 12.
- **Executability / credits:** Keep visual polish behind functional route/state acceptance. Do not spend credits fabricating absent final art before the fallback UI works.
- **Status:** **REQUIRES CORRECTION** — assign DEPLOY-01 and resolve UI-01/02 before final completion.

## Overall spec conclusion

No spec is empty, unnecessary or fundamentally mis-sized. The native documents can be consumed directly after targeted edits. The critical sequence is:

1. repair shared ownership before 01;
2. freeze the complete Timeline/context contract before 02;
3. preserve the intended 05/06 parallelism with a real B2 proof;
4. enforce local finance/game/security gates at their owning specs;
5. make deployment and judge access an explicit final acceptance gate.
