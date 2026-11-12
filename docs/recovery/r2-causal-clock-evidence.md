# R2 causal clock evidence

## Candidate

- R1 base `aa7755d8713313af8364a281f43f027d11be0483`.
- R2 implementation commit `b95ced88094a18136876424d8660b968f8b36adf`.
- The initial publication candidate is the evidence commit on top of the implementation commit. Resolve it with `git log --diff-filter=A -1 --format=%H -- docs/recovery/r2-causal-clock-evidence.md`.
- R2 head branch `codex/r2-v4-causal-clock-20261112`. Intended draft base `codex/r1-v3-hardening-runtime-20261112`. R1 PR #1 was observed open as a draft at the exact baseline.
- Native run URL, job IDs and artifacts are pending approval and execution. The current evidence applies to the local source candidate.

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
| `dotnet test tests/Revenant/Revenant.Tests.csproj -c Release --no-build --filter 'Category=Integration' --list-tests` | 83 discovered including all 5 R2 integration cases. Execution pending. |
| PostgreSQL 18.6 parser via pglast | 40 SQL statements and 8 function/PL blocks parsed. Database execution pending. |

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

## Database and native proof to collect

Migration SHA-256 `6d226e8c79841d9f5698a00433199d63d02f9564ce99b0fb4ffd97069804b9ce`.

`finance_fact_payment_or_seal` distinguishes the V4 control fact and mandatory payment IDs for ordinary facts. `finance_fact_one_v4_seal_per_boundary` gives the boundary a database identity. `finance_fact_v4_shape` validates family, cursor, boundary, unresolved commands, canonical bytes and digest. `run_cursor_v4_matches_seal` is a deferred converse check for the cursor, seal and watermark. `v4_scheduler_guard`, `v4_command_guard` and `v4_command_progress` validate journal progression and update the rolling command digest. The new tenant tables use forced RLS under `revenant.tenant_id`. The existing finance foreign key, payment uniqueness indexes, evidence shape and append-only, cursor progression and closed-run guards remain in effect.

The CI integration job already selects `Category=Integration`. It now requires all five named R2 cases to pass in the uploaded Revenant TRX alongside the existing R1 requirements. The pending database cases cover:

1. Exact boundary dispatch, blocked dependency recovery, retained payloads, terminal digest reconciliation and fresh Marrow engine replay.
2. Competing writer connections under a deterministic barrier, one committed seal, and independent run progress.
3. Deferred commit failure, rollback, and command and seal retries after acknowledgment loss.
4. Wrong-tenant and replay-role denial, canonical seal shape, invalid progression, and signed origin.
5. Real migration runner upgrade with open and closed V2/V3 runs, exact old row and fact fingerprints, the V3 evidence guard, and identical V4 bytes from fresh and upgraded schemas.

The R2 scheduler executes test-owned control commands without financial effects. Production V4 ledger execution, shared-transaction adaptation of existing handlers, branch identity binding, and financial close belong to R3. The [V4 time contract](r2-v4-time-contract.md) records those interfaces.
