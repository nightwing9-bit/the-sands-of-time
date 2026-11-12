# R2 causal clock evidence

**R2 partial. Native CI passed. Distinct finance-run identity at the R3 handoff remains an architecture blocker.**

## Candidate

- R1 base `aa7755d8713313af8364a281f43f027d11be0483`.
- R2 implementation commit `b95ced88094a18136876424d8660b968f8b36adf`.
- Published and tested PR head `0e042125d5103e36673d1054fa4e42f90ec045a5` on `codex/r2-v4-causal-clock-20261112`. [Draft PR #2](https://github.com/nightwing9-bit/the-sands-of-time/pull/2).
- GitHub tested synthetic merge `dedaf9b5d21c21aec251050b51454ba3370ec0ac`. Its parents are the R1 base and R2 head above, and its tree `2c49d6b3a7dd70747f6e661a395c243bdd653d3c` matches the exact R2 head tree. The integration job checkout log records this merge SHA.
- The PR base is `codex/r1-v3-hardening-runtime-20261112`. Remote R1 remained at `aa7755d8713313af8364a281f43f027d11be0483` and remote main at `2fde4a57f2f229743bc43d8a41e6bc989d433d09` after the run. This evidence update is local. [R1 draft PR #1](https://github.com/nightwing9-bit/the-sands-of-time/pull/1).

## Changed paths

| Area | Paths |
| --- | --- |
| Timeline | `src/Revenant/Timeline/ObservedFinanceV4.cs`, `src/Revenant/Timeline/ObservedFinanceV4Codecs.cs` |
| Scheduler | `src/Revenant/Payments/V4SimulationScheduler.cs`, `src/Revenant/Payments/V4SimulationStore.cs` |
| Migration | `scripts/db/migrations/revenant/0009_v4_causal_scheduler.sql` |
| Tests | `tests/Revenant/Timeline/ObservedFinanceV4Tests.cs`, `tests/Revenant/Payments/V4SimulationPostgresTests.cs`, `tests/Revenant/Ledger/LedgerDatabase.cs` |
| CI | `.github/workflows/ci.yml` |
| Documentation | `docs/recovery/r2-v4-time-contract.md`, `docs/recovery/r2-causal-clock-evidence.md` |

The original migration files, frozen V1/V2/V3 contracts and goldens, original Kiro records, Ghost Lap, Marrow, Worker image and model files match R1. Model ZIP SHA-256 `c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de`. Model manifest file SHA-256 `a96b3d5f364cf543c071c1be17ca7a292b181272cdb6b934a15671b8ba574d5a`. PlacementOps and local Docker were outside this work.

## Local verification

| Check | Observed result |
| --- | --- |
| `dotnet build TheSandsOfTime.slnx -c Release --no-restore -warnaserror` with SDK 10.0.403 | Passed with zero warnings and errors |
| `dotnet test tests/Revenant/Revenant.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ObservedFinanceV4Tests'` | 4 passed, 0 failed, 0 skipped |
| `dotnet test TheSandsOfTime.slnx -c Release --no-build --filter 'Category!=Integration&Category!=E2E&Category!=MlSsaX64&Category!=OfflineTraining'` | 356 passed, 0 failed, 0 skipped. Revenant 170, architecture 11, Marrow 103, Ghost Lap 68, integration project non-service cases 4. Includes the four focused V4 cases. |
| `dotnet test tests/Revenant/Revenant.Tests.csproj -c Release --no-build --filter 'Category=Integration' --list-tests` | 83 discovered including all 5 R2 integration cases. Native counts matched discovery. |
| PostgreSQL 18.6 parser via pglast | 40 SQL statements and 8 function/PL blocks parsed. Native migration results appear below. |

The initial pure run exposed a property/type name collision at compile time and rejection of the legacy `sha256:` manifest notation. The corrected context uses the catalog review policy explicitly, validates the manifest string form, and stores the raw 32-byte digest canonically. The initial broad test selection also picked up two Linux-only SSA probes and the offline training entry point. All three failed their entry requirements. The CI fast filter produced the passing run above. An optional static parser install under Python 3.9 could not resolve its build requirements. The existing Python 3.14 accepted the packaged parser and completed the static check.

## Boundary and fixture evidence

The pure reducer rejected a payment at 09:00 before the seal, then accepted this sequence.

| UTC effective time | Finance sequence | Fact |
| --- | ---: | --- |
| 08:55:00.000 | 1 | Seal for the first interval |
| 08:59:59.999 | 2 | Payment intent |
| 09:00:00.000 | 3 | Seal for `[08:55, 09:00)`, certifying preceding fact sequence 2 |
| 09:00:00.000 | 4 | Next payment intent |

A second 09:00 payment was accepted at sequence 5. Applying sequence 3 changed the wrapped ledger's position alone. The idle test replayed 144 consecutive seals under both pinned suffix policies with identical state bytes.

| Canonical fixture | SHA-256 |
| --- | --- |
| V4 context | `a902cb16638eebffbd49b114dce2eac20387d23a85789966b90e3d4280d530ea` |
| V4 state | `61b8376d350ea8566547e97332c6d3c17451976e17ea544fe425b83dd7e6ae6a` |
| Observation seal payload | `cb5e296e79d74135d65f9a42c44b43d47d75d6e5bcb9cad4e3cfd2502a21253f` |

## Native CI

The GitHub-hosted integration runner reported Ubuntu 24.04, Linux x86_64, RID `linux-x64` and SDK 10.0.404. PostgreSQL used the `postgres:18.6-alpine3.24` Testcontainers fixture. Run attempt 1 completed successfully. [CI run 43159700298](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298).

| Job | Result |
| --- | --- |
| .NET restore, build and test | [Passed, 130987700742](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298/job/130987700742) |
| Linux AMD64 FastTree, SSA and Worker | [Passed, 130987700595](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298/job/130987700595) |
| .NET Testcontainers integration | [Passed, 130987700679](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298/job/130987700679) |
| Web typecheck, tests and build | [Passed, 130987700398](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298/job/130987700398) |
| OpenAPI/TypeScript contracts | [Passed, 130987701085](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298/job/130987701085) |
| Playwright E2E | [Passed, 130987700601](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298/job/130987700601) |
| Script lint | [Passed, 130987700794](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298/job/130987700794) |

The downloaded Revenant TRX contains 83 distinct results with 83 executed, 83 passed, zero failures and zero unexecuted. Comparing test names with the R1 TRX found all 78 R1 cases and exactly five new R2 cases. The workflow's coverage guard independently confirmed each R2 case and all six named R1 model cases, including the V3 cold replay and three artifact-outage cases. The same job passed 107 integration-project cases and 61 Marrow PostgreSQL cases. [R2 TRX artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43159700298/artifacts/13917592069) · [R1 comparison TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43150815709/artifacts/13913786328).

## Database and replay proofs

Migration SHA-256 `6d226e8c79841d9f5698a00433199d63d02f9564ce99b0fb4ffd97069804b9ce`.

`finance_fact_payment_or_seal` distinguishes the V4 control fact and mandatory payment IDs for ordinary facts. `finance_fact_one_v4_seal_per_boundary` gives the boundary a database identity. `finance_fact_v4_shape` validates family, cursor, boundary, unresolved commands, canonical bytes and digest. `run_cursor_v4_matches_seal` is a deferred converse check for the cursor, seal and watermark. `v4_scheduler_guard`, `v4_command_guard` and `v4_command_progress` validate journal progression and update the rolling command digest. The new tenant tables use forced RLS under `revenant.tenant_id`. The existing finance foreign key, payment uniqueness indexes, evidence shape and append-only, cursor progression and closed-run guards remain in effect.

All five tests in [V4SimulationPostgresTests.cs](../../tests/Revenant/Payments/V4SimulationPostgresTests.cs) passed. The native results verify these assertions.

| Case | Verified result |
| --- | --- |
| Boundary, blocking, retry and digest | The 08:55 seal committed at fact sequence 1. A command due at 08:59:59.999 blocked the 09:00 seal until explicit retry. Equal-due commands then resolved in command sequence order. The 09:00 seal committed at fact sequence 2 while the command due at 09:00 was pending. That command completed next at effective 09:00. Future payloads remained in the journal and outside the seal prefix. Matching admission replay returned the stored result, and a conflicting payload was rejected. |
| Competing dispatchers | An in-flight command held the run lock. Two concurrent seal callers waited while another run committed its own seal. After release, both callers obtained identical receipts and the contended run had one finance fact. |
| Commit failure and lost acknowledgment | The injected deferred commit error left zero facts and the cursor and watermark at their original position. A thrown handler left its command pending. After successful execution and seal, fresh retries returned the committed identities with one command and one seal. |
| Tenant and SQL guards | Wrong-tenant reads and updates saw zero rows, wrong-tenant inserts failed, and replay-role mutation attempts were denied. SQL rejected early execution, duplicate seals, seals referencing a payment, and incomplete cursor/seal/watermark writes. A signed origin advanced from −300,000 ms to a seal at 0. |
| Fresh install, upgrade and determinism | The real migration runner applied 0009 to both a fresh fixture and an isolated R1 schema. The upgrade preserved exact row and fact fingerprints for open and closed V2/V3 runs. Both historical families rejected fake seals and V3 rejected screening without inference evidence. Fixed schedules produced identical seal envelopes and rebuilt state bytes across fresh and upgraded databases. |

The 09:00 PostgreSQL proof uses a test control command with no ledger effect. The pure test above verifies the equivalent position ordering with real payment-intent inputs. The native boundary case independently recomputed the command digest from persisted journal entries, rebuilt canonical finance state through a replay-role connection, and verified the same state bytes and state hash in a fresh Marrow engine instance without a predictor.

## R3 compatibility review

Marrow can give a child a different **Timeline ID** and preserve the exact source prefix. Fork creation restores the verified source state at the fork position, serializes it unchanged with the same state codec, checks its original hash, and records the parent and fork position. Full replay folds ancestor segments under their own pinned contexts and verifies the child's base-state hash before applying its suffix. The V4 state codec stores the rolling finance-prefix digest in those checkpoint bytes. [Fork creation](../../src/Marrow.Timeline/Internal/PersistentTimelineEngine.cs#L242) · [Ancestor replay](../../src/Marrow.Timeline/Internal/TimelineReconstructor.cs#L157) · [V4 state codec](../../src/Revenant/Timeline/ObservedFinanceV4Codecs.cs#L131).

A child with a different **finance run ID** has no usable binding to that inherited prefix in R2. The inherited ledger bytes include the source run UUID. Rewriting the UUID at the fork changes the verified checkpoint bytes. Keeping the inherited UUID causes the first child-owned V4 fact to fail the input-ID check or `RunMismatch`. PostgreSQL independently initializes V4 schedulers at genesis and validates seal payloads and predecessor digests against the same SQL run ID. The contract names child identity binding as R3 work and supplies no transition or mapping. Treat this as an architecture blocker for the requested distinct-run continuation. [Ledger state codec](../../src/Revenant/Timeline/FinanceLedgerCodecs.cs#L88) · [V4 transition](../../src/Revenant/Timeline/ObservedFinanceV4.cs#L227) · [Reducer run check](../../src/Revenant/Timeline/ObservedFinanceV4.cs#L150) · [SQL genesis and seal checks](../../scripts/db/migrations/revenant/0009_v4_causal_scheduler.sql#L110).

## Acceptance

| Gate | Status | Evidence |
| --- | --- | --- |
| R2-01 | VERIFIED PASS | Verified R1 ancestry, isolated draft and identical tested merge tree |
| R2-02–03 | VERIFIED PASS | Pinned V4 identities, strict codecs and fixed fixtures |
| R2-04–06 | VERIFIED PASS | Pure and native time, position and exact barrier checks |
| R2-07–10 | VERIFIED PASS | Native journal, idempotency, explicit blocked retry and independent run |
| R2-11–14 | VERIFIED PASS | Native atomicity, uniqueness, failure/retry and competing dispatchers |
| R2-15–16 | VERIFIED PASS | Native command digest audit and predictor-free V4 replay |
| R2-17–18 | VERIFIED PASS | Exact R1 case coverage, fresh/upgrade migration and SQL role enforcement |
| R2-19 | VERIFIED PASS | All seven jobs passed on the verified candidate tree |
| R2-20 | PARTIAL | Contract, evidence and local log are complete. The distinct finance-run handoff remains unresolved. |
