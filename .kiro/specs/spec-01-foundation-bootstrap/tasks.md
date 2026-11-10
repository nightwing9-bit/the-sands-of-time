# Spec 01 implementation tasks — Repository, hosts, persistence and CI

**Execute tasks in listed order.** Dependencies: none (greenfield starting point). Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **1.1 Scaffold solution and two browser apps**
  - **Precondition/input & inspect:** root tree, supplied `.kiro/` and architecture contract map. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** projects/workspaces with actual entrypoints and package/SDK lockfiles; scaffold `Marrow.Kernel` as the sole dependency-free owner of pure `IActorContext`, realtime envelope metadata and outbox/inbox contract records/ports. No implementation dependencies in Kernel.
  - **Forbidden changes:** new conceptual frameworks or per-layer empty project explosions. Do not change frozen shared public contracts without ADR.
  - **Verification:** `dotnet build` + `npm ci` + `npm run typecheck`.
  - **Done when:** all projects compile and shells render.

- [ ] **1.2 Configure Postgres, Redis and host DI**
  - **Precondition/input & inspect:** `deploy/compose`, host Program.cs, health and config. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** Postgres/Redis provisioning, `scripts/db/migrate.sh` one-shot runner and a Compose `migrate` job that completes before hosts start; three runnable hosts and readiness checks. This spec does **not** create Timeline/Identity/Revenant/GhostLap domain migrations. The executor applies only migrations supplied by their owning specs and fails visibly on errors; host startup does not run migrations.
  - **Forbidden changes:** cross-schema FKs, ad hoc global singletons or two match authorities. Do not change frozen shared public contracts without ADR.
  - **Verification:** Compose clean boot with no domain migrations yet, single executor invocation + ordered host boot, failure stops startup; stop/recover Redis.
  - **Done when:** fresh environment starts and failures are truthful.

- [ ] **1.3 Enforce module architecture**
  - **Precondition/input & inspect:** `.csproj` refs, contract assembly names, `tests/Architecture`. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** NetArchTest forbidden-edge and render-dependency rules: Kernel → nothing app/framework-specific; Timeline → Kernel only; both domains may consume Kernel/Timeline but not shared Infrastructure or each other; hosts compose implementations.
  - **Forbidden changes:** self-reported module checks without compiled assembly assertions. Do not change frozen shared public contracts without ADR.
  - **Verification:** inject prohibited temporary reference to verify test fails, remove afterward.
  - **Done when:** architecture gate rejects violations.

- [ ] **1.4 Add CI and deterministic hook scripts**
  - **Precondition/input & inspect:** `.github/workflows`, `scripts/checks`, `.kiro/hooks/*.json`. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** real fast jobs, invocation contracts, doc for intended task gate marker.
  - **Forbidden changes:** hook activation before scripts/tests exist; full Testcontainers every save. Do not change frozen shared public contracts without ADR.
  - **Verification:** run scripts, YAML/JSON validation, sample skipped status.
  - **Done when:** scripts act safely; hook files remain initially disabled.

- [ ] **1.5 Repo hygiene and handoff**
  - **Precondition/input & inspect:** README, license decision, gitignore, evidence log. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** step-by-step local setup, actual LICENSE, secrets-example template, first evidence procedure.
  - **Forbidden changes:** claiming Kiro runs or passing tests without execution. Do not change frozen shared public contracts without ADR.
  - **Verification:** clean-clone smoke and git status verify `.kiro/` tracked.
  - **Done when:** setup repeated by separate clean directory.

## Spec exit verification (blocking)

Clean-clone Compose+both UI shells+all three hosts start; targeted CI is green; forbidden dependency test is shown failing when injected; no hook executes nonexistent tests.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
