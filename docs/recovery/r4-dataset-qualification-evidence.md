# R4 dataset qualification evidence

R4 started from verified R3 implementation
`927704d5c7fef3e00673860ec73b81c60b1cb8ef` and preserved its local
documentation-only descendant `91b9706558cd2aeba340e60cc183d9865e2b2c2e`.
The [stacked draft PR #4](https://github.com/nightwing9-bit/the-sands-of-time/pull/4)
targets the R3 branch. Main and the R1–R3 branches were not changed.

The frozen model ZIP SHA-256 matched
`c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de`.
Friday Ring and V4 historical canonical goldens passed. No historical migration,
Kiro record, Ghost Lap code, or V1–V3 codec was changed. No local Docker or
PlacementOps resources were used. The final local Release build on SDK
10.0.404 passed with zero warnings or errors. The filtered solution suite
passed 365 cases, including 179 Revenant cases, with zero skips.

## Opening and native projection checkpoints

The first native opening ran [here](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43201298938)
on Linux x86_64, testing head `71c55351ecece49f44b763f8e28325b9b46d8a1d`
at merge SHA `cf4e01944c9631b3908ea676bfe61cae631ed0f0`. It passed 107
Integration, 61 Marrow and all 87 Revenant integration cases with zero
skips. The new gate executed exactly once; the [TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43201298938/artifacts/13934134813)
reports 0.612 seconds for the case. A separate Worker Compose health
job could not pull the PostgreSQL image because Docker Hub's token
endpoint returned EOF. The native database job passed. The subsequent
projection workflow passed the Worker job as well.

The root pinned manifest
`sha256:c90b3a0dfbc6addb09e824898330841385be82d702118e299731cde06443595b`.
It opened BANK-A–F with 2/3/4/2/3/4 funded USD accounts, 18 total,
for two simulated hours. PostgreSQL committed four intakes, four
screenings, two settlements, one held reservation, one insufficient
screening rejection, and one authorized compensating return. It
committed 24 seals and 35 total facts. Posted and reserved amounts
matched the expected value for each account; available equaled posted
minus reserved throughout. The final verified V4 prefix was
`4556b920b33b4a69e4d082f0b48f6d1858c0a94a008f9629c602136c760ec87c`.

The first bank run exposed a stale R2→R3 upgrade assertion that expected
one applied migration. The fixture now requires both named migrations
and their exact count. The [projection run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43204340944)
tested head `0f8917f6207d44c7c2449cdeba3b485121d95a52` at merge
`442a059f5ca96f7b3e61d50e4d847de9487607a8`; all seven jobs passed.
The [87-case Revenant TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43204340944/artifacts/13936297865)
records the opening and projection case at 1.267 seconds. It materialized
144 bank rows and five sparse account rows and read four typed as-of
rows. Its first/last bank row digests were
`c5cd4b8667668322a390f77dd515501dc66f60fba2119f82b37e12b8e052dffa`
and `5b233c3f9fd1286820032fd94916560a9fe9fe00fca001cfd7bc53a81c24bf54`.
Exact retries were idempotent, conflicting bank bytes were refused, and
the replay reader could not see another tenant's rows. Online and
offline digests matched for the first seal. The child retained inherited
values under physical execution `00000000-0000-0000-0000-000000040003`
and excluded the source's later return, hold and settlement from its
suffix.

The focused pure cases prove covered zero versus insufficient history,
strict schema and byte validation, exact pre-Screening position and
candidate exclusion, same-timestamp future suffix invariance, and
financial/feature invariance after offline truth annotations change.
The horizon test measures a real later settlement and return, and leaves
missing tail entries uncovered. The split test assigns 24 roots before
planning and detects overlapping normalized root and campaign
fingerprints and logical accounts.

## Generated smoke and tested implementation

The implementation head is `73e1fa4d4f172ca32bb81a325353a487ac1aa006`.
The native [ordinary smoke run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43205868026)
tested merge `aff492633b153c27626e0c4ec3578dfcc0f65e6d`; all seven
jobs passed. The [TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43205868026/artifacts/13936973539)
contains 88 executed and passed Revenant integration cases (zero skipped),
including both named R4 gates and the required R1–R3 cases. The ordinary
native job also passed 107 Integration and 61 Marrow PostgreSQL cases.
The actual workflow guard accepted the original TRX. On local copies it
rejected both removal of the opening case (even with consistent counters)
and marking the case skipped.

`V4DatasetPostgresTests.Generated_smoke_reproduces_semantic_exports_under_different_execution_ids`
ran two real physical executions of the same two-hour logical root, with
different execution UUIDs and different operational prefix hashes. Both
produced semantic manifest
`48d51fc2b7cebc879de49bfc2bfaf09aea6d0047a34934744580391ac06532e0`
under the fixture revision. Each produced 43 screenings, 41 settlements,
two holds, one compensating return, 144 bank rows and 58 sparse account
rows. The two executions took 4.478 and 3.343 seconds; the complete test
took 7.943 seconds. Flipping only a malicious-intent annotation changed
the truth hash and preserved the model-visible hash. No cohort was run
by the ordinary workflow.

## 48-hour native pilot

The [pilot run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43206415370)
tested the same implementation and merge SHAs as ordinary smoke. All
seven jobs passed. The [pilot artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43206415370/artifacts/13939126140)
contains the one executed and passed cohort test, the manifest, sample
and measured performance. The canonical binary was independently hashed
and matched manifest
`b90039b01342bd0037bbd3f1168846df0481a4d3953e10f968f1a5a9f8a09ce9`.

The 48-hour Train and Validation roots had 30 and 31 funded accounts
across six banks each. They committed 1,566 facts, including 1,152 seals,
and produced 6,912 bank rows, 204 sparse account rows, 138 as-of rows,
6,912 target rows (6,816 fully covered), 138 payment labels and 106
anomaly labels (34 positive). All 138 attempts reached Screening:
125 posted settlements, 3 holds, 1 screening rejection and 9
insufficient-funds settlement outcomes without a posting. There were
four compensating returns of posted transfers. Truth contained 95
legitimate and 43 malicious attempts, with 38 hard negatives and all
five attack families. The canonical streams represented 8,607,121 bytes.

| Split | Roots | Legitimate | Malicious | Hard negatives | Model streams SHA-256 | Truth SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| Train | 1 | 46 | 21 | 19 | `66674707007e8bfd423603b166ccf7fac7a4df9abadb627a5ec3239bda2b4b27` | `3f50f982267b28e742f8718016a6a2e65d5c00e2dac2f42718e80ffb404afda1` |
| Validation | 1 | 49 | 22 | 19 | `3280f0aef449b741ad385f408e18eab95a357320bea6cc348175c7c993cafc7e` | `26a5acf42e4b4236d24f48fca31ac705e72f81a2c6a4db91c504d83285260896` |

The pilot took 33.09 seconds, including root execution, verified replay,
projection, materialization, and export. Roots took 15.45–16.21 seconds
each; pure projection took 16.6–39.8 ms per root. Peak test-process
working set was 183.2 MiB (excludes PostgreSQL and other processes).
Mean command dispatch time in successive 200-fact bands was
23.76, 21.23, 22.84 and 23.68 ms. The measured variation did not justify
a replay checkpoint optimization.

## 72-hour native qualification

The [qualification run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43207060325) tested head
`73e1fa4d4f172ca32bb81a325353a487ac1aa006` at merge
`aff492633b153c27626e0c4ec3578dfcc0f65e6d` on GitHub-hosted Linux x86_64
and PostgreSQL 18.6. The [cohort artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43207060325/artifacts/13939063651)
contains one executed and passed cohort case, canonical manifest, compact
sample and timing measurements. The [Revenant regression TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43207060325/artifacts/13940655254)
contains the ordinary integration results for the same run.

The independently verified manifest SHA-256 is
`a40cfb31cab319541a339d5c07a7068b6fda4717718a8012af5a999a83e793df`. The
[retained manifest](r4-qualified-dataset-manifest.json) lists every
logical root, opening and normalized behavior hash, plus all eight
stream counts, byte sizes and hashes per split. It represents
154,535,875 bytes of canonical streams; the CI artifact retains only
compact evidence.

All 24 independent roots completed 72 hours and 864 seals across six
banks each. There were 661 accounts (22–34
per root), 27,414 committed facts, 20,736 seals,
124,416 bank rows, 3,381 sparse account rows and
2,219 pre-screening rows. Of 2,219
attempts, 2,219 were admitted and 2,219
screened. There were 2,057 posted settlements,
39 holds, 12 screening rejections,
111 unposted insufficient-funds settlement
outcomes, and 72 compensating returns. Of the malicious
attempts, 525 originally posted. Bank flows reconciled
to posted and reserved balances at every seal and conserved system
principal. The checked SQL ledger transitions also enforce nonnegative
available funds.

The exporter rejected group, logical account/customer and campaign
identity overlap and duplicate normalized root/campaign fingerprints.
All 120 campaigns are present. There are 1,690
legitimate and 529 malicious labels, including
647 hard negatives, and 411 positive
and 1,266 negative account-interval annotations.

| Split | Roots | Bank rows | Account rows | As-of rows | Legitimate | Malicious | Hard negatives | Positive/negative anomalies | Full targets |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Train | 12 | 62,208 | 1,700 | 1,106 | 842 | 264 | 322 | 204/632 | 61,632 |
| Validation | 4 | 20,736 | 571 | 377 | 288 | 89 | 109 | 71/218 | 20,544 |
| Calibration | 4 | 20,736 | 552 | 367 | 279 | 88 | 110 | 68/204 | 20,544 |
| FinalTest | 4 | 20,736 | 558 | 369 | 281 | 88 | 106 | 68/212 | 20,544 |

| Split | Ring | Takeover | Dispersal | Coordinated burst | Beneficiary abuse | Model streams SHA-256 | Truth SHA-256 |
| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| Train | 72 | 24 | 60 | 72 | 36 | `e4964abd2f81ed31990a39a7574006d27cc43ccccd2092e9a14f05a365604e51` | `dc07b1c5ce498b14743f95eda4e65745a1aced407e0eeb2742b89d528c69e055` |
| Validation | 24 | 8 | 21 | 24 | 12 | `71f6233bb0505dfdfb543163e64340fb37aecfbf5090ca83ddf0c5eac0e665a1` | `1844a18579360efbb980d4f5692c4e09a7d6708dff6aa3838b7c88d102fe35e2` |
| Calibration | 24 | 8 | 20 | 24 | 12 | `e190fb7ae4cd71464d8c468a9d612466184516d760ab5dd84284a26f86db4a8d` | `4c8521bfed3bc427391d03068b73e00fcba87a32bc3b7a1e95fcbf3059535651` |
| FinalTest | 24 | 8 | 20 | 24 | 12 | `4bc67dec664f688a096ea48f0879be729e4f23b36a2deaa38809058fa3627d88` | `7aae730562dd67c6ff73c4c8c3f39e30627f63bc201d88a2493c5df2148a225d` |

The cohort produced 124,416 target vectors: 123,264 have all eight real
future seals and 1,152 are partial or empty at the episode tails. Missing
entries contain no financial values. The data source can reproduce the
dataset without the raw files; the separate native smoke verified exact
semantic hash reproducibility across distinct physical executions.

Qualification took 539.46 seconds end to end in the test body. Root
execution and verified replay took 21.19–23.23
seconds per root; pure projection took 8.7–42.0
ms per root. Peak test-process working set was 196.1 MiB, excluding
PostgreSQL and other processes. No replay optimization was needed.

| Prior facts | Dispatch commands | Mean time per command (ms) |
| --- | ---: | ---: |
| 0–199 | 888 | 18.86 |
| 200–399 | 817 | 19.66 |
| 400–599 | 1,045 | 21.45 |
| 600–799 | 991 | 22.73 |
| 800–999 | 1,617 | 24.68 |
| 1000–1199 | 1,320 | 25.83 |

All seven jobs passed for the tested implementation. The native
qualification workflow executed 107 Integration, 61 Marrow PostgreSQL,
88 Revenant PostgreSQL regression cases and one separate cohort case
with no skips. The observed counts reconcile: 20,736 seals plus 2,219
intakes, 2,219 screenings, 2,168 settlement-attempt facts and 72
compensating returns make 27,414 total facts.

## Acceptance gates

| Gate | Status | Executed proof |
| --- | --- | --- |
| R4-01 | VERIFIED PASS | R3 implementation ancestor and documentation child retained; protected scope and frozen model hash checked; legacy goldens and native regressions passed. |
| R4-02 | VERIFIED PASS | Real enhanced six-bank v7 opening, manifest and account assertions in the native opening TRX. |
| R4-03 | VERIFIED PASS | Two native 48-hour roots and 24 native 72-hour roots completed and verified through the legal finance executor. |
| R4-04 | VERIFIED PASS | Two independently executed copies of the generated two-hour root produced identical semantic manifest bytes by hash under different operational IDs. The full cohort was executed once. |
| R4-05 | VERIFIED PASS | 144 real smoke bank rows and exactly 124,416 qualified rows, six per complete seal. |
| R4-06 | VERIFIED PASS | Sparse persistence and cold read verified; pure tests covered 5/30/60-minute and 24-hour windows; 3,381 qualified sparse rows. |
| R4-07 | VERIFIED PASS | Real hold increased reserve without settled outflow; returns and posted settlements were measured independently. |
| R4-08 | VERIFIED PASS | Actual account balances, same-bank cancellation, global settled and return conservation and per-bank reservation reconciliation passed. |
| R4-09 | VERIFIED PASS | Pure queries verified a covered zero at a real seal, prehistory warmup and missing-boundary absence. |
| R4-10 | VERIFIED PASS | Pure and native pre-screening cases verified exact prior fact position, prior balances and the explicit feature whitelist. |
| R4-11 | VERIFIED PASS | Byte-identical earlier features after later same-time facts; native child preserved the inherited prefix without the source continuation. |
| R4-12 | VERIFIED PASS | Pure annotation flip left command, fact, bank, account and feature bytes unchanged; native re-annotation changed only offline truth hashes. |
| R4-13 | VERIFIED PASS | Qualified independent root assignment was 12/4/4/4, verified before planning and by the canonical manifest. |
| R4-14 | VERIFIED PASS | All five adversarial families and legitimate lookalikes in every split; 647 hard negatives and 411 positive anomaly intervals. |
| R4-15 | VERIFIED PASS | Pure cohort and actual exporter checked distinct roots, account/customer identities, campaign keys and normalized fingerprints. |
| R4-16 | VERIFIED PASS | Measured later flows in the pure horizon test and 123,264 full vectors in qualification; 1,152 partial/empty tails explicitly masked. |
| R4-17 | VERIFIED PASS | Strict projection hash/schema/bytes checks, native materialization conflict rejection, repeat semantic hash and independent canonical manifest SHA check. |
| R4-18 | VERIFIED PASS | Full 24-root native run completed in 539.46 seconds; pilot and qualification command-band metrics supported leaving R3 replay unchanged. |
| R4-19 | VERIFIED PASS | All seven jobs passed on the tested head; both named R4 native regression cases, the cohort case, and the R1–R3 guards executed. |
| R4-20 | VERIFIED PASS | Exact R5/R7 contracts, measured qualification evidence, compact manifest, and unstaged local recovery entry. |

The qualified population is synthetic and rules-only. It supports model
research under these scenario and truth definitions. It does not measure
fraud detection accuracy or support claims about production prevalence.
