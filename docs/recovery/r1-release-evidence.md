# R1 release evidence

**R1 FULL PASS — VERIFIED** · 12 November 2026

## Candidate and provenance

- Base and `main`: `2fde4a57f2f229743bc43d8a41e6bc989d433d09`.
- Branch: `codex/r1-v3-hardening-runtime-20261112`.
- Implementation candidate: `bcb32c7ad356f9595916d7f80b6475f61c288baa`. The unpublished identity correction preserved the message, parent and exact source tree `3d86fb608aa21916828fdaf66f00e2e6340649a2`. GitHub attributes both author and committer to the authenticated account.
- [Draft PR #1](https://github.com/nightwing9-bit/the-sands-of-time/pull/1) · [Successful pull-request CI run 43150815709](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43150815709). GitHub reported the candidate as the run head. The PR merge test commit `b32fd977b48ef3a770291899e50d21968e15b387` has the same tree and the expected base and candidate parents. The PR remains a draft.
- Frozen ZIP SHA-256: `c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de` (29,417 bytes). Manifest file SHA-256: `a96b3d5f364cf543c071c1be17ca7a292b181272cdb6b934a15671b8ba574d5a`. Embedded manifest seal: `00431188ea8ac78cfa948ef2da78383f659b5c1ada5dec78e89171ac646c658a`.

## Change inventory

| File | Purpose and R1 gates | Evidence and compatibility |
| --- | --- | --- |
| `src/Revenant/Payments/PaymentLedgerStore.cs` | V3-only intake feature guard and finance family on the locked cursor. R1-02–06 | PostgreSQL admission and replay assertions. Existing accepted duplicate resolution and V2 intake remain intact. |
| `src/Revenant/Payments/PaymentLedgerStore.Screening.cs` | Rebuild features at the locked screening boundary, return typed blocked outcomes and skip blocked runs for the current pass. R1-06–07, 09 | PostgreSQL proves independent runs advance and a locked earlier V3 intent cannot be bypassed. V3 evidence encoding and fact ordering remain intact. |
| `src/Revenant/Payments/ScreeningWorker.cs` | Report blocked inputs and pause between affected passes. R1-07, 09–10 | Hosted artifact tests plus locked-head store tests. Model-required decisions retain their existing authority. |
| `src/Revenant/Intelligence/RiskModelScorer.cs` | Direct typed artifact reads, required manifest field check and scoped loader exceptions. R1-08–10, 14 | Native loader tests and live recovery. Existing pin, normalization and reference decisions pass. |
| `src/Revenant/Revenant.csproj` | Exposes internal reader injection to the test assembly. R1-08, 19 | Release build and architecture checks. Existing domain package boundaries remain intact. |
| `src/Hosts.Worker/Hosts.Worker.csproj` | Worker-only ML.NET TimeSeries 5.0.1 reference. R1-15–19 | Native published image and architecture checks. |
| `src/Hosts.Worker/Program.cs` | Routes the exact diagnostic argument to self-test before ordinary startup. R1-15–18 | Published self-test and normal startup both executed. |
| `src/Hosts.Worker/MlRuntimeSelfTest.cs` | Verify actual native SSA, model parity, package versions and mapped libraries. R1-14–17 | Actual runtime JSON and exit 0. |
| `src/Hosts.Worker/MlRuntimeSsa.cs` | Shared deterministic fit, save, reload and predict workloads. R1-15–16 | Native SDK probes and published Worker both pass. Synthetic inputs remain separate from financial models. |
| `deploy/docker/Worker.Dockerfile` | Ubuntu/glibc Linux x64 publish, OpenMP runtime and alias, and `wget`. R1-15–18 | Linux AMD64 image proof and actual Compose health probe. Runs as `app`. |
| `deploy/compose/docker-compose.yml` | Pins only the Worker to Linux AMD64. R1-17–19 | Isolated Compose smoke exercised the existing migration and health contract. |
| `.github/workflows/ci.yml` | Native image gate, isolated healthy and degraded smoke, explicit Revenant integration selection, strict TRX coverage and retained evidence. R1-02–18 | Hosted run completed all seven jobs. |
| `tests/Revenant/Intelligence/RiskModelTests.cs` | Fault injection, disappearing files and exact artifact restoration. R1-08, 10, 14 | Native focused and CI unit passes. |
| `tests/Revenant/Ledger/LedgerDatabase.cs` | Unique database per existing disposable ledger fixture. R1-02–13 | Real PostgreSQL suite passed with fixture-owned endpoints and migrations. |
| `tests/Revenant/Payments/ColdModelReplay.cs` | Launches a predictor-free child process and compares canonical PostgreSQL-backed replay. R1-13 | Actual child PID, matching fingerprints and live ledger write-denial checks. |
| `tests/Revenant/Payments/ModelAwareScreeningTests.cs` | Exact generated evidence, live FR-0041, hosted artifact recovery and cold replay. R1-09–13, 18 | Four PostgreSQL results passed. Controlled transactions remain outside the generated cohort. |
| `tests/Revenant/Payments/ModelInputAdmissionTests.cs` | Numeric boundary, rejected write counts, replay and cross-run locked-head tests. R1-02–07 | Both PostgreSQL tests passed. |
| `tests/Revenant/Platform/MlPlatformProbe.cs` | Uses shared SSA workloads with existing test and output contracts. R1-15–17 | Three native SDK probe tests and one local ARM64 FastTree probe passed. |
| `tests/Revenant/Revenant.Tests.csproj` | Test-only child executable and linked shared SSA source. R1-13, 15–17 | Discovery, unit suite, actual child replay and native probes passed. |
| `tests/Revenant/Timeline/ModelLedgerReducerTests.cs` | Same-input FR-0041 reference regression for both lawful snapshots. R1-12, 14 | Pure fold and verified loader match exact hashes and PPM. |
| `tests/Revenant/Timeline/RevenantTimelineDatabase.cs` | Unique database per existing disposable Timeline fixture. R1-01, 19 | Existing real PostgreSQL historical replay tests passed. |

## PostgreSQL safety and artifact behavior

The [integration job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43150815709/job/130959265820) executed 78 Revenant tests. All 78 passed with zero failures and zero skips. The TRX coverage guard found both `ModelInputAdmissionTests` cases, the generated-evidence/cold-replay case and all three artifact-outage theory cases. The job also passed 107 integration-project and 61 Marrow PostgreSQL cases. It used disposable Testcontainers on a GitHub-hosted Linux x86_64 runner.

- V3 admits exactly 9,007,199,254,740,992 minor units when other requirements hold and rejects the next integer with `InvalidRequest`. Two rejected attempts left the payment, transition, command, fact, audit, outbox and cursor unchanged. An accepted same-input retry replays the same payment and fact. A conflicting duplicate fails. A V2 payment at the larger amount still enters and receives a rules-only screening outcome.
- The high-opening-balance fixture receives `SourcePostedBalanceInvalid` and writes no intake effects. At screening, an already-queued incompatible balance is checked again under the source-account and cursor locks. The test observed a typed block, no effects, and independent V2 and V3 progress. An incompatible legacy amount likewise stayed Initiated, and a later same-run intent did not overtake its locked head. Existing closure correctly rejects the non-quiescent legacy run.
- A naturally crossing balance was outside the normal Friday Ring fixture's range. The run opens with 66,000,000 minor units total and its balanced postings conserve that total. The PostgreSQL fixture seeded an out-of-envelope source and a legacy queue item to exercise the explicit blocked path. A permanent legacy incompatibility can still block its own run under frozen V3 lifecycle rules.
- Unit coverage exercised missing ZIP and manifest, malformed and hash-invalid manifests, corrupted ZIP, unsupported metadata, injected I/O and permission failures for both reads, and disappearance at read time. Hosted outage cases observed `ManifestMissing`, `ArtifactChecksumMismatch`, and `ArtifactIoUnavailable`. V2 progressed during each outage. V3 stayed Initiated with no added screening fact, transition, audit or outbox event and no case, hold or posting. Restoring the exact pin allowed the same Worker to screen V3 successfully.

## Generated screening evidence and FR-0041

The [Revenant TRX artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43150815709/artifacts/13913786328) contains 90 decoded persisted Screening rows for 90 distinct generated IDs. The integration test checked intent identity, truth category, fact boundary, policy, model, threshold, same-input PPM and final decision for every row. The generated cohort contains 60 legitimate and 30 ring-labeled intents. All 90 went Clear → Clear. Three separate controls exercised ML-only hold, clear settlement and sanctions hold. The generated cohort produced zero learned holds.

| Score (PPM) | Legitimate | Ring |
| ---: | ---: | ---: |
| 2,653 | 15 | 0 |
| 3,650 | 35 | 0 |
| 3,704 | 5 | 0 |
| 5,124 | 3 | 0 |
| 8,284 | 1 | 0 |
| 10,054 | 0 | 8 |
| 12,410 | 0 | 1 |
| 12,692 | 1 | 0 |
| 18,524 | 0 | 19 |
| 22,739 | 0 | 2 |
| **Total** | **60** | **30** |

The historical Kiro histogram listed 89 entries while describing 90 decisions. Every published bucket agrees with the new persisted evidence. The omitted bucket is the single 12,410 PPM ring row, `FR-0037`, with amount 125,349 and posted balance 1,000,000 at screening. The new run has its Screening fact at position 77. This explains the aggregate discrepancy. The cause of the omission in the earlier report is unknown. The historical log remains intact.

| FR-0041 snapshot | Amount | Source posted | Feature hash | PPM |
| --- | ---: | ---: | --- | ---: |
| Persisted live, screening before settlement | 143,381 | 1,000,000 | `a36f27bb806a02c8648134bd5390dca3050d6b5da445746fc692f9cccab77bd3` | 18,524 |
| Sequential pure fold after earlier settlements | 143,381 | 314,052 | `cfdccf6c46af98439c353d90a55269de33e6994fbc4b282e7ee0adc1a313cb8f` | 253,652 |

The persisted row binds as-of fact 84 to Screening fact 85 and matches a rescore of its exact input. A fresh verified loader reproduces both reference results for their respective inputs. The different lawful posted balances explain the different scores.

## Separate-process replay

The PostgreSQL integration test closed and verified the V3 source, then launched `dotnet Revenant.Tests.dll --r1-cold-replay` in an isolated directory. Child PID 18798 differed from the parent. The model ZIP and manifest were absent from the child directory and its DI composition had no predictor. The child used the restricted projection/replay login, verified table and column write privileges were absent on the checked authoritative ledger tables, and reconciled from persisted facts and evidence. The parent compared replay position, exact canonical state bytes, state and record hashes, source fingerprint and canonical inputs before and after execution.

| Replay measurement | Observed result |
| --- | --- |
| Final position | 280 |
| Final state SHA-256 | `fa4bc0accbb21cfe4beec830e6b35e1d96a3c5836e8236ecdaea3ceade9339ac` |
| Final record chain SHA-256 | `953772803bbcf6de3c762278c245c165a1e73bff6bf19612f9e51468d8fdf830` |
| Source fingerprint | `2498094c1351ff1a6542c722187ff7fd93a0602ee4a46712ebb8238045973b0f` |
| Canonical input SHA-256 | `65e273577c0409db25d2d865844da5764a051998c830cb6fedc3e784e4d4241c` |

## Actual published Worker

The [native Worker job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43150815709/job/130959265406) built from the repository root and ran `--ml-runtime-selftest` through the image entrypoint on GitHub-hosted Ubuntu Linux x86_64. The separate SDK probe also passed all three tests. The [Worker artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43150815709/artifacts/13914265780) retains the image identity, self-test, probe TRX and normal startup output.

| Item | Executed result |
| --- | --- |
| SDK base | `mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e` |
| ASP.NET base | `mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c` |
| Published image | `sands-worker-ml:ci`, `sha256:a066bc7924c5d10f78c5b3a06338fdf3ebdc8aee833b7fd19894ac17986a621a` |
| Runtime | Ubuntu 24.04.5 LTS, glibc 2.39, `linux-x64`, .NET and ASP.NET Core 10.0.14 |
| ML native and managed dependencies | ML.NET, TimeSeries and FastTree 5.0.1. Loaded mappings include `libMklImports.so` and `libomp.so.5`. |
| Anomaly | Real `DetectSpikeBySsa` fit, saved 1,328-byte ZIP, reloaded and predicted. Zero alerts in 40 calm observations and alert 1 for the positive spike. |
| Forecast | Real `ForecastBySsa` fit, saved 1,283-byte ZIP, reloaded and returned eight finite horizons with enclosing bounds. Forecast: 20.158646, 23.84359, 25.384487, 23.862343, 20.172329, 16.489395, 14.969887, 16.495565. |
| FastTree | Verified pinned ZIP and manifest, all six reference scores and threshold decisions with `probability-ppm/1`. Self-test exited 0. |
| Normal startup | Same image started as nonroot `app`, used the Compose `wget` command and became healthy. Liveness 200 and readiness 200 with dependencies. After Redis stopped, readiness became 503 while liveness stayed 200. Zero Worker restarts and exit 0 on normal stop. |

## CI and historical integrity

All seven jobs passed for the implementation candidate. The [build and unit job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43150815709/job/130959265702) passed warnings-as-errors build and 11 dedicated architecture tests. Its subsequent solution test step passed 352 executions, including 166 Revenant, 103 Marrow, 68 Ghost Lap, 11 architecture and four integration-project unit tests. Web, browser E2E, contracts and script-lint jobs also passed.

Local SDK 10.0.403 Release build passed with zero warnings and errors. Focused local TRX reports recorded 136 Revenant tests, 11 architecture tests and one ARM64 FastTree probe, all passing with zero skips. Discovery selected 78 PostgreSQL cases. Those cases and the authoritative published Worker proof ran on GitHub-hosted infrastructure.

The historical V1/V2/V3 reducer, fact, state and context code, migrations, model policy, threshold (257,796 PPM), quantization and artifact bytes stayed frozen. The pure compatibility, PostgreSQL replay and architecture checks passed. Generic Marrow Timeline, Ghost Lap and original Kiro records remained intact. R1 retains the established classifier and its known limitations. PlacementOps was outside the verification.

## Acceptance

| Gate | Status | Evidence |
| --- | --- | --- |
| R1-01 | VERIFIED PASS | Frozen code and hashes audited; historical local and PostgreSQL replay passed. |
| R1-02 | VERIFIED PASS | 2^53+1 rejected for V3 in real PostgreSQL. |
| R1-03 | VERIFIED PASS | Exact 2^53 accepted for V3. |
| R1-04 | VERIFIED PASS | V2 retained its range and rules-only outcome. |
| R1-05 | VERIFIED PASS | Duplicate precedence and rejected-intake no-effect assertions passed. |
| R1-06 | VERIFIED PASS | High source rejected on intake; legacy incompatible source explicitly blocked at screening without effects. |
| R1-07 | VERIFIED PASS | Independent V2/V3 progress and same-run locked-head order passed. |
| R1-08 | VERIFIED PASS | Typed filesystem, checksum and manifest regressions passed. |
| R1-09 | VERIFIED PASS | Three hosted failure cases left mandatory V3 screening effects absent. |
| R1-10 | VERIFIED PASS | Same Worker recovered after verified artifact restoration. |
| R1-11 | VERIFIED PASS | 90/90 persisted rows, 60/30 labels, and omitted FR-0037 bucket reconciled. |
| R1-12 | VERIFIED PASS | Live FR-0041 same-input parity and both lawful snapshots verified. |
| R1-13 | VERIFIED PASS | Separate child replay matched canonical bytes and hashes without a predictor or ledger write privileges. |
| R1-14 | VERIFIED PASS | Frozen pin, manifest, ZIP and exact PPM parity verified locally and in the published Worker. |
| R1-15 | VERIFIED PASS | Published AMD64 anomaly fit/save/reload/predict passed. |
| R1-16 | VERIFIED PASS | Published AMD64 eight-horizon forecast fit/save/reload/predict passed. |
| R1-17 | VERIFIED PASS | Native hosted CI succeeded for the implementation head and identical checkout tree. |
| R1-18 | VERIFIED PASS | Nonroot startup, actual `wget` probe, ready 200/503 and normal stop observed. |
| R1-19 | VERIFIED PASS | Frozen boundaries and architecture tests passed. |
| R1-20 | VERIFIED PASS | This report and the local append-only Codex recovery log record the observed evidence. |

**R1 FULL PASS — VERIFIED.** The legacy run-local block and zero learned holds in the generated cohort are recorded limitations. New finance lifecycle, model training and production anomaly/forecast designs remain outside R1. The draft PR is ready for R2 architecture review.
