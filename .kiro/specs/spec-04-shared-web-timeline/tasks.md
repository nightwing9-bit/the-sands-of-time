# Spec 04 implementation tasks — Typed web integration and common Timeline scrubber

**Execute tasks in listed order.** Dependencies: spec-02, spec-03. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [x] **4.1 Implement typed API and hub clients**
  - **Precondition/input & inspect:** OpenAPI generator config, `web/packages/contracts`, host endpoints. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** repeatable generated contract script and gap/reconnect transport layer.
  - **Forbidden changes:** manual divergent copy of DTO or promiscuous `any`. Do not change frozen shared public contracts without ADR.
  - **Verification:** CI stale generation check + TS strict type tests; exhaustive Spec-02 Timeline typed-error mapping and permission-denial checks.
  - **Done when:** both app shells share typed clients.

- [x] **4.2 Build controlled shared scrubber**
  - **Precondition/input & inspect:** `web/packages/timeline-ui` and shared adapter interface. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** seek/play/pause/markers/fork/read-only/error/keyboard behavior.
  - **Forbidden changes:** game or bank rule logic in shared UI. Do not change frozen shared public contracts without ADR.
  - **Verification:** component tests seek race, final position, >2^53 labels.
  - **Done when:** same component exported once.

- [x] **4.3 Implement thin app adapters** _(fast Playwright suite uses browser-edge stubs; the backend observation is the exit-gate proof `tests/E2E/real-backend.spec.ts`, see docs/kiro-log.md)_
  - **Precondition/input & inspect:** `web/apps/revenant` and `ghost-lap` shell integrations. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** two tiny adapters with distinct labels calling test endpoint or real backend.
  - **Forbidden changes:** copying scrubber source, fake branch result user experience. Do not change frozen shared public contracts without ADR.
  - **Verification:** Playwright verify requests and verified state.
  - **Done when:** both apps mount same package.

- [x] **4.4 Instrument browser evidence**
  - **Precondition/input & inspect:** Playwright configs, existing app startup, Chrome MCP config. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** named short smoke scripts, console/network failure trace.
  - **Forbidden changes:** claim Chrome connection without checking, save secrets in traces. Do not change frozen shared public contracts without ADR.
  - **Verification:** actual Playwright smoke, reduced-motion and keyboard test.
  - **Done when:** browser route and failure observations recorded.

- [x] **4.5 Gate UI correctness**
  - **Precondition/input & inspect:** contract versions, README, tests/E2E. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** compatibility check and source-package import verification.
  - **Forbidden changes:** long design polish or unknown skill file recreation. Do not change frozen shared public contracts without ADR.
  - **Verification:** inspect bundled imports, stale DTO failure test.
  - **Done when:** common scrubber verified for two adapters.

## Spec exit verification (blocking)

Two live browser shells import one working Timeline scrubber and typed clients; controlled seek/fork/error behavior observed against backend; actual graphics/domain overlays deferred.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.

**Exit gate status (2026-11-10): SPEC 04 EXIT GATE: PASSED** against the test-only Timeline host (`tests/E2E/backend`), with no `page.route` in `tests/E2E/real-backend.spec.ts`. Commands, counts, defects, ownership register and the items deferred to Specs 05/06/09/10/11/13 are in `docs/kiro-log.md` (entry "spec-04 / exit verification"). Remote CI is pending until the change is pushed.
