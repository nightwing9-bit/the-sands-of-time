# Kiro Execution Readiness

## Can Spec 01 begin?

**Not safely as the package stands; yes after a short architecture-only preflight.**

Before Kiro scaffolds task 1.1, the documents must place shared actor/realtime/outbox abstractions in a dependency-free project or explicitly in `Marrow.Kernel` (R-01), and name one migration executor while preserving domain-owned migrations (the first sentence of R-02). Those decisions affect initial project references and host startup ownership. They are small corrections, not a reason to regenerate the specs.

After that preflight, Spec 01 can begin. The incomplete Timeline API/context resolver must be corrected before Spec 02, not before solution/CI/Compose scaffolding.

## Native spec compatibility

The 13 folders use the current native `requirements.md` / `design.md` / `tasks.md` structure and contain substantive EARS-style requirements, design decisions and bounded tasks. Current official Kiro documentation confirms the structure and steering/hook/MCP locations: [Feature Specs](https://kiro.dev/docs/specs/feature-specs/), [Steering](https://kiro.dev/docs/steering/), [Hooks](https://kiro.dev/docs/hooks/), and [MCP configuration](https://kiro.dev/docs/mcp/configuration/).

**Do not regenerate requirements, designs or tasks.** Apply targeted edits from `audit/minimal-remediation-plan.md`, then execute one spec and one dependent task at a time as the workflow already requires.

## Required preparation before the first Kiro session

1. Apply R-01 to `01-master.md` and Specs 01/03; update the scaffold/project-reference list and architecture-test ownership together.
2. Apply the migration-executor portion of R-02. Do not create domain schemas in Spec 01.
3. Record these as architecture corrections, not fabricated implementation activity. Do not append passing evidence to `docs/kiro-log.md` until commands actually run.
4. Confirm the local Kiro surface and account/model/credit status. The package's 4,000-credit table is a planning ceiling, not proof of available balance or future pricing.
5. Start only Spec 01. Do not pre-enable hooks, Chrome MCP, or UI design context.

## Frontend design input status

Both inputs are present and were audited:

- Revenant: `design-inputs/skills1 (1).md`
- Ghost Lap: `design-inputs/skills2 (1).md`

The current package status is inaccurate: `README.md:28-30` says contents were not supplied, while `docs/workflow/02-steering-and-assets.md:14-18` and Specs 09/13 expect `web/apps/revenant/design/Skills1.md` and `web/apps/ghost-lap/design/Skills2.md`.

Before the relevant UI spec, either place an unmodified copy at the expected app path or update the consuming spec to the actual path. Do not load either file during backend/Timeline work. First add the compatibility decision in R-15 because the inputs conflict with locked authority on Revenant visual direction, offline Ghost behavior and hash comparison semantics. Final art/media assets are not present; Spec 13's geometric/semantic fallbacks make that a polish limitation, not an architecture blocker.

## Steering status and context hazards

Six steering files exist. `product.md`, `structure.md` and `tech.md` are short and always loaded; this is a reasonable default context. `timeline-correctness.md`, `finance-integrity.md` and `game-authority.md` are conditionally loaded, which is the right credit strategy.

Two finance/game `fileMatch` lists omit the very frontend trees they are supposed to govern. Add the narrow globs in R-16. Do not respond by making those files always-loaded.

Context hazards to avoid:

- Re-injecting the 881-line original context into every task instead of loading the active spec, its owned paths and named contracts.
- Loading both design inputs during backend work.
- Letting one long session span multiple specs after its assumptions become stale.
- Asking Kiro to reconsider architecture that the corrected master contract already freezes.
- Running parallel agents against the same Timeline contract, migration, DbContext, game rules or settlement files.
- Treating design-input presentation statements as server/game/finance authority.

## MCP status

`.kiro/settings/mcp.json` is valid JSON and uses the documented workspace location.

- **Context7:** configured at the vendor endpoint, `disabled: false`, `autoApprove: []` (`mcp.json:3-6`). Configuration does not prove a successful connection. Use it only for targeted version-sensitive .NET/EF Core/ML.NET/PixiJS/SignalR questions. Core engineering does not depend on it; disabling it except when needed would be a reasonable operational preference, not a blocker.
- **Chrome DevTools MCP:** configured with `npx -y chrome-devtools-mcp@latest --headless --isolated`, `disabled: true`, `autoApprove: []` (`mcp.json:8-17`). The command/flags are vendor-plausible. Leave it disabled until a browser app exists in Spec 04, smoke-test the actual environment, then pin the resolved package version for reproducibility. Do not expose auth tokens or production data.

No MCP connection or invocation has been validated by this planning audit. MCP unavailability must degrade to ordinary documentation/browser tooling, not halt the core build.

## Hook status

All three JSON files exist, parse successfully, use `version: "v1"`, and are truly inactive:

| Hook | Trigger | Command | Current state | Earliest enable point |
|---|---|---|---|---|
| Marrow Contract Guardian | `PostFileSave` with a narrow matcher | `scripts/checks/contract-guardian.sh` | `enabled: false`; script absent | After Spec 01 creates/tests the script and Specs 02/03 contracts exist |
| Architecture & Quality Gate | `PostTaskExecution` | `scripts/checks/spec-gate.sh` | `enabled: false`; script absent | After local Kiro surface confirms trigger support and a real bounded suite exists |
| Browser Evidence Capture | `PostTaskExecution` | `scripts/checks/browser-evidence.sh` | `enabled: false`; script absent | Specs 09/13 UI milestones after Playwright paths exist |

No hook can currently execute unexpectedly. The absent scripts are intentionally assigned to Spec 01, so no hook command should be run during this audit. Before enabling any hook: run its script manually, verify timeout/exit behavior, confirm the local Kiro trigger surface, and ensure it does not edit files, recursively invoke Kiro or call an AI service. Keep full Testcontainers/Playwright suites out of `PostFileSave`.

## CI status

`.github/workflows/ci.yml` is absent. This is expected: Spec 01 task 1.4 creates it. The staged strategy in `docs/workflow/03-kiro-tooling-ci.md:23-29` is proportionate:

- cheap restore/build/typecheck/lint/unit/architecture checks from Spec 01;
- PostgreSQL/Redis integration and migration tests at relevant changes/basket gates;
- deterministic replay/finance/game property suites at risk-appropriate gates;
- Playwright smoke in 04, Revenant E2E in 09, and multiplayer/spectator/mobile Ghost E2E in 13;
- actual performance measurement rather than ungrounded assertions.

Add the new remediation acceptance tests at their owning specs. Do not run full integration/browser suites on every save or require evidence files before the software that produces them exists.

## Credit readiness

The allocation is coherent: 3,450 credits across four baskets plus 550 contingency equals 4,000, and the strongest reasoning is reserved for irreversible contracts, ledger SQL/causality and authoritative replay/networking (`docs/workflow/04-credit-strategy.md:5-14`). It correctly treats amounts as ceilings, not usage predictions.

Immediately avoidable credit-expensive workflows:

- regenerating the 39 usable native documents;
- starting Spec 02 before R-03/R-04 and then rewriting every adapter;
- creating a second Ghost codec in Spec 11;
- debugging UI behavior while its design file path/authority is unresolved;
- using premium reasoning for routine styling/DTO wiring after contracts are frozen;
- repeatedly running global suites or invoking MCP for routine local facts;
- enabling AI or heavy shell hooks on every save;
- spending the 550 reserve on premature polish rather than integration/demo defects.

Recalibrate after Specs 01, 02, 05 and 06 using actual credits, corrections, test outcomes and elapsed time. Do not invent a fixed model-to-credit conversion.

## Items that can wait for their owning spec

- Timeline lifecycle/context details: must be fixed immediately after 01 and before 02.
- B2 cross-app gate placement and game input/score state: before 06 completion.
- Ledger group completeness/RLS/LiveOps grants: before 07 DB tasks exit.
- Replay privileges, continuation policy, metric finality and scenario-run identity: before 09 replay/E2E.
- Realtime crash outcome: before 10 finalization exit.
- Save reconciliation and share-token leakage: before 11/12 exits.
- Design input placement, frontend steering and responsive tests: just before 09/13 UI work.
- Public HTTPS/judge access: late, but mandatory before submission.
- Manifest repair: optional; never a start gate.

## Actionable implementation-start gate

Spec 01 may start only when all boxes below are true:

- [ ] Shared public actor/realtime/outbox contracts have one legal dependency-free owner and Spec 01 scaffolds it.
- [ ] One migration executor is named; Spec 01 does not claim later domain migrations.
- [ ] The active work is only Spec 01 and its first incomplete task.
- [ ] Hooks remain disabled and no absent script is invoked.
- [ ] No MCP/server connection or CI result is claimed without observed evidence.
- [ ] Frontend design inputs are not injected into backend context.
- [ ] The first basket gate and credit log will record actual commands/results only.

When these are satisfied, implementation can begin without reopening the architecture. Before advancing to Spec 02, complete R-03 and R-04 and re-check every Timeline producer/consumer signature together.
