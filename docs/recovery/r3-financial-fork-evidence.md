# R3 financial fork evidence

## Early identity checkpoint

- Verified R2 implementation ancestor `0e042125d5103e36673d1054fa4e42f90ec045a5`, starting branch HEAD `7dcae197c609a02c4dac0252fcba231370453ac2`.
- Local SDK `10.0.404`. Model ZIP SHA-256 `c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de`. Model manifest file SHA-256 `a96b3d5f364cf543c071c1be17ca7a292b181272cdb6b934a15671b8ba574d5a`. Both hashes match R1.
- The Release solution build completed with zero warnings and zero errors. Local Revenant non-Docker tests passed 171 cases, including the V4 context pin and opening-state test.
- The candidate adds the branch execution coordinator, binding persistence and migration `0010_v4_branch_execution.sql`. It extends the V4 seal store, upgrades the R2 migration regression fixture and adds an executed-test requirement to the existing CI workflow.
- The PostgreSQL test fixes the source and child execution UUIDs at `00000000-0000-0000-0000-000000010001` and `00000000-0000-0000-0000-000000010002`. It reports the actual Timeline IDs, state hash and prefix digest when run. The native runs below record the identity fixture. Financial execution still requires its own proof.

The identity test covers a source that seals through 09:05, a real fork at the 09:00 seal, and the first child suffix at sequence 3. Its assertions cover canonical bytes and hash, child prefix ownership, invalid bindings, retry and cold seal replay. The financial counterfactual, financial invariants and two-worker tests are pending.

### First native run

[Draft PR #3](https://github.com/nightwing9-bit/the-sands-of-time/pull/3) tested head `cd2e9df096b89945e5b690bdb3adcbac8077c309` at merge SHA `3e9c3dfde758d381c8ced37c1346b2fcf572d661` on Linux x86_64. All 84 Revenant PostgreSQL cases passed, including `Verified_source_forks_byte_identically_and_the_child_appends_only_its_suffix`, the five R2 scheduler cases and the required R1 model cases. The whole workflow failed because the new binding foreign key crossed module schemas. The identity integration suite enforces zero such foreign keys. Commit `af683010a8c43d2756f1b76bb35ade030c5cf0f3` removed the cross-schema foreign key and retained the Marrow origin trigger. [First native run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43171771552) · [Revenant TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43171771552/artifacts/13922582148).


### Corrected identity run

All seven jobs passed for head `af683010a8c43d2756f1b76bb35ade030c5cf0f3` at merge SHA `1d5e1eb9fd49e21dfc3721492e3f5f4f854ef06c` on Linux x86_64. The native job executed 107 Integration, 61 Marrow PostgreSQL and 84 Revenant PostgreSQL cases. The identity fixture executed once. The migration applied on fresh and upgrade paths. The R1 model tests and all five R2 V4 scheduler cases passed. [Corrected native run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43172167357) · [Revenant TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43172167357/artifacts/13922567807).

The source Timeline was `01a1ce91-2ba6-7f77-b875-942c6b7e646a` and the child was `01a1ce91-2c55-74ff-b7ef-7ec87b956c2c`. The source verified at sequence 3, and the child inherited sequence 2 at 09:00 before appending its own seal at sequence 3. The shared state hash was `adcbfd70dcfc3f84c1e9a1b3bc177dbb2a17ec5ab6b63d21cafc3d54603ed304`. The shared post-seal digest was `fb46f298ff54fb959b01c6391fd7ee14bc9941e1de8cbf16fe58295732727c75`.

### Financial implementation run

[Native run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43174392760) tested head `5d24741a0676797e7b32997b6689e886af4024c7` at merge SHA `57ffb14ef28b60981412e65c36ea431cbd2d3381`. All seven jobs passed. The Revenant TRX recorded 86 executed and passed cases with zero skipped, including the three R3 PostgreSQL tests. [Revenant TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43174392760/artifacts/13924470019).

The financial fixture forked at 09:00, position 7. The source had settled a USD 1,000 payment and held a different USD 10,000 payment by then. The child inherited the checkpoint accounts, the active hold and case, and the settlement reference. The SQL child had zero inherited fact rows and zero inherited debit or credit entries.

The common post-fork payment was USD 10,000 to a new beneficiary. V7 cleared and settled it. V8 held it and opened a case. The payer in each branch had USD 29,000 available at the point of comparison. The branches differed in how much money was posted or reserved.

| Branch | Payer posted | Payer reserved | Payer available | Payee posted | Payment |
| --- | ---: | ---: | ---: | ---: | --- |
| Source v7 | 3,900,000 | 1,000,000 | 2,900,000 | 6,100,000 | Settled |
| Child v8 | 4,900,000 | 2,000,000 | 2,900,000 | 5,100,000 | Held, case open |

Amounts are USD minor units. The source reserve belongs to the separate inherited payment. The child reserve includes both the inherited payment and the post-fork hold. A subsequent authorized source return restored its separate transfer amount. The child re-evaluated the source's rejection request against its newer case version and cancelled it as stale. It enforced distinct four-eyes approval before settling the inherited held payment.

### Changed files

| Area | Files |
| --- | --- |
| Migration | `scripts/db/migrations/revenant/0010_v4_branch_execution.sql` |
| Scheduler and Timeline | `V4BranchExecution.cs`, `V4FinanceDispatch.cs`, `V4SimulationStore.cs`, `V4SimulationStore.Binding.cs`, `ObservedFinanceV4.cs` |
| Ledger | `PaymentLedgerStore.cs`, `PaymentLedgerStore.Cases.cs`, `PaymentLedgerStore.Closure.cs`, `PaymentLedgerStore.Screening.cs`, `PaymentLedgerStore.Settlement.cs`, `PaymentLedgerStore.V4.cs`, `PaymentLedgerStore.V4Opening.cs` |
| Test fixtures | `LedgerDatabase.cs`, `V4BranchExecutionPostgresTests.cs`, `V4SimulationPostgresTests.cs`, `ObservedFinanceV4Tests.cs` |
| Workflow and reports | `.github/workflows/ci.yml`, `r3-branch-execution-contract.md`, `r3-financial-fork-evidence.md` |

### Final native verification

[Final native run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43175063776) tested implementation `927704d5c7fef3e00673860ec73b81c60b1cb8ef` at merge SHA `f2532bba4515bdda549bb8864f3dc397baf288c6` on GitHub-hosted Linux x86_64 with PostgreSQL 18.6. All seven jobs passed. The native job passed 107 Integration cases, 61 Marrow PostgreSQL cases and 86 Revenant PostgreSQL cases. All 86 discovered Revenant cases executed and passed. There were zero skips. [TRX artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43175063776/artifacts/13924580156).

The three R3 cases executed once each.

- `Verified_source_forks_byte_identically_and_the_child_appends_only_its_suffix`
- `Financial_fork_preserves_the_opening_and_posts_or_reserves_under_the_actual_policy`
- `Reconciliation_rejects_conflicting_bytes_already_written_at_the_SQL_position`

The five R2 scheduler cases and the required R1 model and admission cases executed and passed. The upgrade case wrote an R2 seal and resolved command under 0009, applied 0010, verified the existing V2/V3 and R2 rows, and continued the R2 scheduler. The legacy V3 inference evidence guard, V4 root barrier, duplicate seal, tenant and role restrictions passed.

| Identity | Source | Child |
| --- | --- | --- |
| Canonical lineage | `00000000-0000-0000-0000-000000010011` | `00000000-0000-0000-0000-000000010011` |
| Physical execution | `00000000-0000-0000-0000-000000010011` | `00000000-0000-0000-0000-000000010012` |
| Timeline | `01a1cea7-3977-754a-9372-5ba32f06b22d` | `01a1cea7-3d88-773d-90fc-82298e2c2605` |
| Final sequence | 16 | 17 |

Both canonical checkpoint byte sequences matched at the 09:00 seal, sequence 7. State SHA-256 `57898bb28ff5b9d2d26760bdd374cbd093eaf177f99c2d3c87bcba36f96b7546`. Inherited post-seal digest `7106b49d1164aa87e94e519f94cf60814e739c0e38fcee374741e13b17b32b39`. The opening payer posted, reserved and available amounts were 4,900,000, 1,000,000 and 3,900,000. The payee amounts were 5,100,000, 0 and 5,100,000. The test checked each account against the canonical opening and verified separate physical case IDs.

Source and child both wrote intake and screening at 09:00 as facts 8 and 9. The source wrote settlement as fact 10. The child refused settlement of the held payment. Its duplicate intake command cancelled with `intent-already-recorded`. Review and the compensating return in the source were facts 11 and 15. The child wrote authorized review at 10 and 11, and settled the reviewed inherited payment at 12. It cancelled the source analyst's stale request and refused a second approval by the same actor. At 09:00:00.002 two workers processed competing holds of 1,450,001 minor units each. The first held and the second rejected without reservation or case. Both runs sealed the interval at 09:05. The full command journals and effective times are in the financial case's TRX output.

An injected outbox failure rolled back intake and left the same child command pending. Retrying created one payment and one fact. An injected registration failure after Marrow fork left a pending reservation with no scheduler. Retry reused the same child Timeline, and a changed child ID under the same key failed. SQL-to-Marrow reconciliation recovered with SQL ahead and with the first append already committed. It refused different bytes at the same position. Cold replay restored both histories without a predictor and conserved posted principal.

| Source fingerprint | Before and after child continuation |
| --- | --- |
| SQL, including facts, journal, accounting, cases, audit and outbox | `55ce77e4bc6c92173a01012bb0c5782f6fb592ae30d20bb300a1fda86d0fef25` |
| Marrow final state | `7290ac94af0b3f7c709801331253cc66486108d31a93a512864e0c8e41011785` |
| Marrow record | `96698071e756420c177351ef884ea6f0aacf369ac21464b8ec965528cd1d2689` |

The child final state hash was `abfa3d5ad68670d7f9a7690e15eda4d81fb4f738d9d21f9a67f73daf9745910c`. Tenant isolation rejected a wrong-tenant execution and reservation. The writer could not forge the fork hash, prefix, lineage or Timeline. The replay login could not mutate the binding or opening. Both frozen model artifact hashes still match R1. Local Docker and PlacementOps were outside this work.

R3 is ready for R4 architecture review through the internal interfaces in the branch execution contract. Dispatch and reconciliation currently fold the committed prefix. The verified fixture is a rules-only v7-to-v8 fork. General continuation orchestration, model inference, public APIs and R4 dataset work are outside R3.
