# Independent Phase 1 Architecture Verdict

## Final verdict

**READY AFTER SPECIFIED FIXES**

The package is fundamentally viable, unusually complete for a pre-implementation hackathon architecture, and does not require a replacement architecture or regeneration of its 13 Kiro specs. The modular-monolith shape, three-host deployment, shared Marrow Timeline concept, finance/game separation, authoritative game reducer, and incremental integration baskets are proportionate to the promised product.

It is not ready for unconditional implementation exactly as delivered. One P1 decision must be resolved before Spec 01 scaffolds project references: the dependency-free ownership/location of shared actor, realtime, outbox and related contracts. Two further Timeline P1 corrections must be frozen before Spec 02: a complete callable lifecycle/typed-error API and durable recovery of the pinned transition context. Later P1 gates are local to Specs 06, 07, 09 and the deployment exit. None requires a fundamental redesign.

There are **no P0 findings**. The prioritized evidence and correction set are in `audit/findings.md` and `audit/minimal-remediation-plan.md`.

## Audit basis and inventory

The review used `MARROW_FINAL_PROJECT_CONTEXT_UPDATED.md` as the product baseline and read all delivered architecture, workflow, steering, hook, MCP, spec and frontend-design artifacts. Three independent read-only specialist passes covered .NET/dependencies/tooling, deterministic Timeline/game systems, and finance/security/product behavior.

Verified inventory:

- 13 actual spec directories, each with substantive `requirements.md`, `design.md` and `tasks.md` (39 native Kiro documents).
- Six steering files, three syntactically valid hook JSON files, and one syntactically valid `.kiro/settings/mcp.json`.
- All three hooks are explicitly `enabled: false`; their future scripts are assigned to Spec 01 and do not exist yet.
- `.github/workflows/ci.yml` does not exist; Spec 01 task 1.4 explicitly owns its creation. This is expected, not a missing Phase 1 artifact.
- The two design inputs do exist as `design-inputs/skills1 (1).md` and `design-inputs/skills2 (1).md`. README/workflow text saying they were unavailable is stale, and the files are not at the paths Specs 09 and 13 expect.
- All relative Markdown links resolve. Planned `src/`, `web/`, `tests/` and workflow files are intentionally future-generated.
- The manifest is not a reliable complete inventory: it claims 63 files excluding itself, contains 63 entries including itself, omits the authoritative context and two design inputs, and embeds a necessarily stale self-hash. All listed non-self hashes checked correctly.

## Readiness by concern

| Concern | Assessment | Gate |
|---|---|---|
| Architecture integrity | Lean, coherent modular monolith; no circular spec DAG. Shared abstraction ownership is presently incompatible with the declared project graph. | Correct before Spec 01 task 1.1. |
| Requirement coverage | The original product is substantially covered. Consequential omissions are public deployment/judge access, repeatable demo-run lifecycle, and precise financial metric/continuation semantics. | Correct before the owning late specs and submission. |
| Dependency readiness | Direct dependency headers form an acyclic graph and normally introduce producers before consumers. Spec 06 nevertheless requires a Spec 05 proof while the map permits them in parallel. | Move the proof to basket gate B2 or add dependency 05 → 06. |
| Timeline correctness | The generic model works for finance events and game ticks without sharing clocks or domain payloads. The public contract cannot yet create/append/compare histories, recover a returned branch, return its promised typed errors, or recover pinned `TContext` after restart. | Freeze before Spec 02. |
| Financial correctness | Payment lifecycle, append-only audit, concurrency, ledger, ML and causal projections are well assigned. Posting completeness, replay database privileges, human continuation and outcome-finality definitions need correction. | Gate Specs 07 and 09. |
| Game/realtime readiness | Fixed-point reducer, scheduled RNG, same-tick ordering, inert ghost, room serialization, reconnect, spectators and rating idempotency are sound. Effective-input behavior, score remainder state and crash-at-finalization need local clarification. | Gate the relevant Spec 06/10 codec and loop work. |
| UI/demo readiness | Both hero journeys have real state/persistence owners, not cosmetic mocks. Design files exist but conflict with some product authority and are at unexpected paths. Responsive Revenant acceptance and a public HTTPS exit are missing. | Resolve before Specs 09/13 and submission. |
| Tooling/CI readiness | Native spec, steering, hook and MCP shapes match current Kiro conventions. Hooks are safely disabled; MCP configuration is merely configured, not connection-proven; CI is correctly future-owned. | No regeneration. Add frontend steering globs; pin MCP package after smoke. |
| Kiro credit efficiency | The 3,450 + 550 allocation is arithmetically and strategically coherent. The main avoidable cost is implementing before the small contract gates, or regenerating already substantive specs. | Use the remediation gates and one-spec-at-a-time workflow. |

## Genuine blockers and safe progress

### Before the first implementation session

1. Assign dependency-free ownership for `IActorContext`, realtime envelopes, outbox/inbox primitives and other shared low-level contracts. Either place them explicitly in `Marrow.Kernel` or add one small contracts/abstractions project and include it in Spec 01's scaffold. Infrastructure must implement these contracts; domains must not reference Infrastructure.
2. Clarify that Spec 01 provisions PostgreSQL and one migration-runner mechanism, while domain specs own their migrations. Name the single migration executor so multiple hosts do not race startup migrations.

After those bounded document corrections, Spec 01 can begin. No application code, infrastructure, package install or spec regeneration should precede them.

### Before Spec 02

Freeze a complete Timeline v1 contract: source creation/append/completion, one usable history identity or branch mapping, aligned comparison coordination, typed result/errors, exact position-zero/final-position semantics, and a durable versioned `TContext` codec/resolver. This is the highest leverage credit-protection action.

### Before later owning specs

The other P1s may wait behind their natural gates: Spec 06 cross-app proof placement; Spec 07 posting-group completeness; Spec 09 continuation, metric finality, replay DB privilege isolation and repeatable run identity; and a late public HTTPS/judge-access gate. P2 corrections should be applied with the affected spec rather than stopping unrelated backend work.

## Answers to the 17 non-negotiable questions

1. **Does the package faithfully cover the original product?** Substantially yes, but not completely. It preserves the real shared Timeline, financial causality, immutable originals, authoritative multiplayer and full browser journeys. Public deployment/judge access, append-only-safe repeatable demo runs, and several exact financial semantics lack sufficient implementation ownership.

2. **Are all 13 specs necessary, appropriately sized and validly ordered?** Yes in overall decomposition; the graph is acyclic and the baskets are sensible. Spec 06's local gate illegally depends on parallel Spec 05, and Spec 11 duplicates part of Spec 06's codec/persistence ownership. Those are bounded corrections, not reasons to merge or regenerate specs.

3. **Does each spec have enough local instruction and prerequisites?** Most do. Specs 01–03 need shared-contract/migration ownership clarified; Specs 06, 07, 09 and 11 need the specific corrections above. Without them Kiro would invent architecture at implementation time.

4. **Are frozen shared interfaces precise enough?** No. The Timeline interface is the largest defect: it omits create/append/compare, has inconsistent branch/history identity, promises typed failures its signatures cannot return, leaves position-zero semantics under-specified, and cannot reconstruct pinned `TContext` after restart.

5. **Is one Marrow Timeline genuinely suitable for both domains?** Yes conceptually. Logical positions, immutable prefix plus independent suffix, generic state/input/context, snapshots and domain adapters avoid clock or payload leakage. Finance can append business events while Ghost Lap records every tick. The incomplete public API must be corrected before this promise is executable.

6. **Can Ghost Lap reconstruct and branch while the ghost remains unchanged?** The planned reducer, canonical snapshots, deterministic schedule, independent living/historical worlds and Spec 11 tests support it. It becomes reliable only after the context resolver, effective-input state and fixed-point distance remainder are frozen in the canonical state/codec.

7. **Can Revenant produce a real nonmutating counterfactual?** The causal path is real rather than chart-only, but the current plan does not yet prove it. PostgreSQL must deny the replay mode live-ledger writes, post-fork human decisions need a versioned policy, and “fraud prevented” must not include merely delayed payments.

8. **Is multiplayer correctly integrated yet separate from solo history?** Yes architecturally. Spec 10 reuses the pure game reducer in one serialized room loop and does not require multiplayer histories. Crash/finalization and missing-input behavior need local operational definitions.

9. **Do tests cover damaging failures proportionately?** Largely yes: replay invariants, corruption, concurrency, double settlement, ghost inertness, spectators and outbox/rating duplicates are strong. Add targeted tests for context recovery, empty/incomplete posting groups, packet loss/effective input, DB privilege denial, post-fork analyst actions, crash-before-result commit and public external smoke. Do not add a heavyweight test platform.

10. **Is the architecture lean enough for a hackathon?** Yes. A modular monolith, three processes, one PostgreSQL instance, one Realtime replica and one shared UI component are proportionate. The remediation does not require microservices, event sourcing frameworks, distributed consensus or production-bank infrastructure.

11. **Are steering, MCP, hooks and CI proportionate and credit-efficient?** Mostly. The native file shapes are current; disabled hooks cannot run; CI is correctly deferred to Spec 01; MCP auto-approval is empty. Add missing frontend `fileMatch` globs, treat MCP connection as unproven, and enable hooks only after their scripts pass manually.

12. **Are frontend skills correctly incorporated?** Not yet. They exist, but the package says they do not and Specs 09/13 expect different paths. Copy them unmodified to the expected future paths or update the specs; load them only for their UI work. Resolve their conflicts with locked product authority first.

13. **Will the signature demo be real and integrated early?** Yes if corrected. Specs 02, 05 and 06 support the real cross-app proof, and basket gate B2 is the right early milestone. Move Spec 06's premature bank proof to B2. Add repeatable run IDs and a final public deployment smoke.

14. **Can the first Kiro engineer begin Spec 01 without finding a missing decision?** Not quite. Shared abstraction placement and migration execution ownership affect the initial scaffold and must be documented first. Once those small corrections are made, Spec 01 is ready.

15. **Are 4,000 credits protected?** The allocation and reserve are credible. Protection depends on not regenerating the 39 usable documents, freezing Timeline/contracts before consumers, loading conditional context only, running one spec/task gate at a time, and avoiding repeated implementation against unresolved P1 contracts.

16. **Which changes are necessary versus optional?** Every P1 in `audit/findings.md` is a required gate before its stated spec. P2 items are required to substantiate the affected reliability/claim but can wait until their owning spec. P3 manifest cleanup and convenience refinements should not delay implementation.

17. **What cannot be validated before implementation?** Actual PostgreSQL trigger/RLS/grant behavior, transaction races, outbox recovery, deterministic hashes across runtime versions, ML.NET API feasibility and model quality, forecast calibration, realtime jitter/crash behavior, browser/mobile performance, public TLS routing, real MCP connectivity, CI execution and the timed three-minute demo all require implemented tests and measurements. They must remain claims-to-prove, not current facts.

## Bottom line

This package should be repaired, not replaced. Make the two first-session ownership clarifications, freeze the Timeline contract before Spec 02, and enforce each later correction at its owning gate. With those changes, it is a focused and credit-conscious basis for Kiro implementation.
