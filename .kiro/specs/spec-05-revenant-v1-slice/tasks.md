# Spec 05 implementation tasks — Revenant V1: seeded six-bank causal vertical slice

**Execute tasks in listed order.** Dependencies: spec-02, spec-03, spec-04. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [x] **5.1 Define scenario/versioned exogenous input codec**
  - **Precondition/input & inspect:** `Revenant.Contracts`, Timeline adapter, sources baseline. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** six-bank seeded manifest, stable labels, immutable intent stream and run-scoped `ScenarioRunId`/intent/bank/account state and deterministic starting balances (no destructive reset).
  - **Forbidden changes:** derived settlement results as branch inputs. Do not change frozen shared public contracts without ADR.
  - **Verification:** same seed/version same canonical exogenous facts for two distinct run IDs (ignoring namespace), different seed known variation; different run IDs never collide.
  - **Done when:** Friday Ring has reproducible inputs.

- [x] **5.2 Implement pure payments and policy kernel**
  - **Precondition/input & inspect:** `src/Revenant` domain, payment transitions, Money. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** v7/v8 config, hold/reserve and causal balance reducer.
  - **Forbidden changes:** DB writes inside reducer, floats or precomputed graph. Do not change frozen shared public contracts without ADR.
  - **Verification:** legal statuses, balances conserved, fraud ring classification checks.
  - **Done when:** pure projection produces actual financial differences.

- [x] **5.3 Bind adapter to one Marrow Timeline**
  - **Precondition/input & inspect:** spec-02 contracts, snapshot codec, domain context. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** real recorded source, restore/fork/continue/compare and source immutability.
  - **Forbidden changes:** new finance-specific replay engine or external time sorting. Do not change frozen shared public contracts without ADR.
  - **Verification:** replay hash and source pre/post byte comparison.
  - **Done when:** both policy branches use one engine.

- [x] **5.4 Implement thin Revenant slice UI**
  - **Precondition/input & inspect:** `web/apps/revenant`, shared UI package, API. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** six banks, source events, position select, v7/v8 and comparison.
  - **Forbidden changes:** false production ledger badges, fake chart numbers. Do not change frozen shared public contracts without ADR.
  - **Verification:** Playwright Friday Ring click path and real API state check.
  - **Done when:** result is visibly causal and labelled synthetic.

- [x] **5.5 Confirm integration handoff to 07/08/09**
  - **Precondition/input & inspect:** public finance kernel APIs, test fixtures, metrics. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** frozen contract tests and same source/branch hash report.
  - **Forbidden changes:** code that 07 would need to discard. Do not change frozen shared public contracts without ADR.
  - **Verification:** contract tests + bank B2 part gate; pending fraud excluded from terminal prevention and avoided-exposure fixture.
  - **Done when:** later specs consume pure domain reducer unchanged.

## Spec exit verification (blocking)

A genuine six-bank v7/v8 branch produces distinct, computed hold/settlement/liquidity states and synthetic metrics through Marrow; source unchanged; V1 browser path works; no claim of DB posting before 07.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
