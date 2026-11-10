# Spec 09 implementation tasks — Revenant V2: isolated counterfactual, five functional screens and audit

**Execute tasks in listed order.** Dependencies: spec-04, spec-05, spec-07, spec-08. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **9.1 Implement isolated branch job store/Worker**
  - **Precondition/input & inspect:** spec-02 Timeline, pure finance from 05, ledger from 07, ML from 08. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** two explicit process configurations of same Worker binary (`LiveOps`/`Replay`), restricted replay DB role, no live writers in replay DI, versioned async run lifecycle and result schema, pinned `analyst-continuation-v1`.
  - **Forbidden changes:** injecting live ledger writer or copying v7 decisions as v8 inputs. Do not change frozen shared public contracts without ADR.
  - **Verification:** direct SQL replay-role live-write **denial** and permitted **new Timeline child append plus** branch-result write in real Postgres (verified original source still immutable), DI graph, post-fork action/precondition fixture and source immutability tests.
  - **Done when:** zero real ledger writes possible by design.

- [ ] **9.2 Finalize metrics/curve alignment**
  - **Precondition/input & inspect:** actual v7/v8 scenario and forecast outputs. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** horizon/time-bucket rules, cost formula with terminal prevented versus deferred separate, result provenance and human-action continuation evidence.
  - **Forbidden changes:** hardcoded ghost lines/financial numbers. Do not change frozen shared public contracts without ADR.
  - **Verification:** synthetic truth case matrix where a held fraud settles after horizon (must NOT count prevented), valid terminal rejection counts; invariant metric tests and explicit cost units.
  - **Done when:** metrics reconcile to source/alternate outcome data.

- [ ] **9.3 Implement five complete React screens**
  - **Precondition/input & inspect:** `web/apps/revenant`, existing `design-inputs/skills1 (1).md` on-demand, locked dark-default design authority, typed clients. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** working navigation, queries, actions, charts/error/accessibility.
  - **Forbidden changes:** unknown asset hallucination, duplicate scrubber. Do not change frozen shared public contracts without ADR.
  - **Verification:** Playwright normal+invalid actions, all five screen routes; phone/tablet Held Queue, chart/table fallback, navigation and touch seek.
  - **Done when:** UI reflects real backend data.

- [ ] **9.4 Wire shared scrubber and audit**
  - **Precondition/input & inspect:** shared Timeline package, financial adapter and audit verifier. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** seek/fork/compare job state, hash details, export if supported.
  - **Forbidden changes:** UI-only green audit indicators or confirmed success before commit. Do not change frozen shared public contracts without ADR.
  - **Verification:** Playwright actual 09:00 branch and source untouched.
  - **Done when:** same shared control is used in both apps.

- [ ] **9.5 B3 integrated security/performance gate**
  - **Precondition/input & inspect:** worker/ledger/model/tests, six-bank fixed scenario. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** authorized create-fresh-scenario-run operation and end-to-end proof with two run namespaces, actual speed/worker logs and deterministic cancellation.
  - **Forbidden changes:** unverified 100× marketing claim. Do not change frozen shared public contracts without ADR.
  - **Verification:** 2 staff four-eyes, corrupt model, concurrent replay, hash check, two equivalent equal-seed runs with distinct bank/account state and identical seeded opening balances, without deletion/key collision, DB denial and post-fork analyst-action case.
  - **Done when:** five-screen finance product works and is verifiable.

## Spec exit verification (blocking)

Five connected screens, real Friday Ring v7/v8 and audit verified; no branch live-ledger writing; test proves source immutable and computed liquidity/metric delta.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
