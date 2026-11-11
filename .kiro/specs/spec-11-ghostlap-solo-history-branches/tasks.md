# Spec 11 implementation tasks — Ghost Lap V2: persistent solo histories, exact ghosts and branching

**Execute tasks in listed order.** Dependencies: spec-02, spec-03, spec-06. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **11.1 Link existing canonical codec to immutable solo catalog schema**
  - **Precondition/input & inspect:** ghostlap tables, spec-02 Timeline API, Spec-06 **existing finalized** input/state codec and full GameState. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** GhostLap owner/catalog metadata schema and references to already verified Marrow source ID; **reuse** Spec-06 codec/fixtures unchanged (do not implement another codec).
  - **Forbidden changes:** partial position-only checkpoint or silent record update. Do not change frozen shared public contracts without ADR.
  - **Verification:** real DB catalog migration, cold reload and Spec-06 codec-fixture/hash compatibility checks; no second codec in project.
  - **Done when:** full original survives reload.

- [ ] **11.2 Original save and history listing**
  - **Precondition/input & inspect:** guest actor, solo completion semantics, REST endpoints. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** idempotent verified-source-to-catalog transaction, original/attempt queries and pin metadata; retry links orphaned verified source by same operation key. Own production solo/history endpoints, existing Timeline/domain-store and authoritative solo-loop composition, REST/OpenAPI/generated frontend contracts and API/authority runtime grants, including `ghostlap_runtime` and Timeline login mapping in migration-role management. Reuse `SoloOriginals`, `SoloBranches` and existing verified/branch metadata through catalog links; test-only bridge is reference only, never a production dependency, and solo recording does not require multiplayer rooms.
  - **Forbidden changes:** success before commit, new original from branch by default. Do not change frozen shared public contracts without ADR.
  - **Verification:** double save/retry/not-found, failed catalog commit, lost success response and owner-owned verified-but-unlisted source recovery; no false receipt tests.
  - **Done when:** owner sees reliable original list.

- [ ] **11.3 Challenge/ghost + branch completion**
  - **Precondition/input & inspect:** Timeline engine, source codec, independent GameState instances. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** same seed/spawn, read-only trace, any-valid-T fork from a completed verified original and child suffix persistence. Own production branch endpoints/generated contracts and durable solo-loop restoration after restart from the last committed persisted state/tick; concurrent resumes yield one authoritative loop per attempt. Preserve an active attempt independently while navigating a selected original; label source/tick, disable fork without an eligible original, and never auto-complete partial histories or introduce nested forks.
  - **Forbidden changes:** ghost inserted into living entities or mutable source reuse. Do not change frozen shared public contracts without ADR.
  - **Verification:** T=1140 charge/consumed same + siblings independent. While a divergent attempt is Recording, choose a completed original and verify child parent identity, fork-state bytes and hash against that selected original; direct Recording-source fork returns Conflict.
  - **Done when:** repeated challenge/forks behave truly.

- [ ] **11.4 Score alignment and viewer API**
  - **Precondition/input & inspect:** score sample/verified replay, shared adapter APIs. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** source/current aligned tick curves and explicit branching errors.
  - **Forbidden changes:** score from Pixi coordinates or past final score during live. Do not change frozen shared public contracts without ADR.
  - **Verification:** T0/final tick and arbitrary positions, curve delta assertions.
  - **Done when:** source and current comparison reconciles to backend.

- [ ] **11.5 Ownership/version/performance gate**
  - **Precondition/input & inspect:** real Postgres history, guest privacy and rules migration. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** incompatible notice, permission enforcement and checkpoint cadence measurement.
  - **Forbidden changes:** silent old-rules reinterpretation or unmeasured compression claim. Do not change frozen shared public contracts without ADR.
  - **Verification:** two owners, version mismatch, storage size sampling and no alternate game codec; original and branch hash/score fixtures agree with Spec 06. Cold-restart actual product registrations, reopen a completed original and race resumes of a recording child at its last committed tick: one loop, durable identity preserved. Through real sessions/runtime logins prove Revenant and Ghost Lap policies coexist without registration overwrite, valid distinct-staff workflow access, cross-tenant denial, private guest histories, unknown domain/resource denial and revoked membership enforcement at API/relevant hub paths. Keep fixture assemblies and `/__e2e` routes absent from production; narrowly adapt old blanket route-isolation assertions to permit intended production domain routes/contracts.
  - **Done when:** history verified and denied readers blocked.

## Spec exit verification (blocking)

Owner records full solo once, refreshes, repeatedly challenges, rewinds nonzero-state tick, creates multiple independent saved children, inspects true tick-aligned scores; hashes/privacy/version tests pass.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
