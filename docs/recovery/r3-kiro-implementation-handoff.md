# R3 Kiro implementation handoff

Agent: Kiro (Default coding agent). This file records Kiro work only. It supersedes the `CODEX-ML-R3` log entry requirement.

## Verdict

`R3 PARTIAL`: Checkpoint 0 (baseline and architecture audit) is complete. **No source, SQL, test or spec file was changed.** No R3 acceptance gate other than R3-01 is verified.

## Why work stopped

| Blocker | Evidence |
|---|---|
| .NET SDK mismatch | `global.json` pins `10.0.402` with `rollForward: latestFeature`. Only `10.0.401` is installed in `/usr/local/share/dotnet/sdk`. `dotnet --version` fails, so nothing can be built or tested locally. |
| No native PostgreSQL | `psql`, `postgres` and `pg_ctl` are absent. Local Docker is prohibited. Marrow has no in-memory `ITimelineStore` (only `PostgresTimelineStore`), so a real `ForkAsync` can run only in CI. |

Writing unbuilt financial and SQL code against the R2 triggers would have left the tree unbuildable and unverifiable, so none was written. To continue, install SDK 10.0.402 or newer (without changing `global.json`). The PostgreSQL gates still need CI, which needs push approval.

## Gate R3-01: VERIFIED PASS

- Workspace: `/Users/rroy/Desktop/the-sands-of-time-phase1`; remote `nightwing9-bit/the-sands-of-time`.
- Starting HEAD: `7dcae197c609a02c4dac0252fcba231370453ac2` on `codex/r2-v4-causal-clock-20261112`.
- `git merge-base --is-ancestor 0e042125… HEAD` succeeded.
- `git diff --stat 0e04212..7dcae19` shows one file, `docs/recovery/r2-causal-clock-evidence.md`, so it is documentation-only.
- Branch `codex/r3-v4-financial-fork-20261112` was created from that HEAD. Nothing is committed on it.
- Untracked files preserved and not to be staged: `SPEC_07-13_ARCHITECTURE_AUDIT.md`, `docs/codex-ml-recovery-log.md` (private), `docs/recovery/r1-resumption-checkpoint.md`, `docs/recovery/spec08-architecture-inspection.md`.
- Not done: model ZIP and manifest hash re-check (needs no build, left for the continuing agent).
- Latest migration is `0009_v4_causal_scheduler.sql`, so `0010_v4_branch_execution.sql` is free.

## Audit findings (from reading code, not from running it)

1. **Identity.** `ObservedFinanceState.RunId` is `Ledger.RunId`. `ObservedFinanceReducer.Apply` rejects `input.RunId != state.RunId`. `ObservedFinanceTransition` also requires `InputId == FinanceLedgerTransition.InputId(previous.RunId, position)`. A child that keeps the inherited lineage ID A in its inputs passes both checks unchanged, so no reducer change is needed for identity. Physical ownership (execution B) must live in a new SQL binding table, as the assignment recommends.
2. **v8 is supported.** `PolicyCatalog.Resolve` resolves `policy-v7` and `policy-v8`. `ObservedFinanceContext` accepts any catalog policy. The existing test `Twelve_idle_hours_replay_identically_under_either_suffix_policy` already applies one state under a v8 context. No V4-only policy adapter is needed.
3. **RulesHash.** There is no V4 Marrow opening code and no V4 `ForkAsync` call anywhere in `src/` or `tests/`, so the "policy hash in the family rule slot" trap has not been created yet. R3 must define the V4 `NewHistory.RulesHash` as a family-level constant, not derived from the suffix policy. Marrow `ForkAsync` copies `RulesHash`, `SeedHash`, owner and version key from the source and swaps only the context.
4. **Fork preconditions.** Marrow requires a Verified (completed) source. The existing Ghost Lap tests show the shape: complete the source, then fork, with the operation key giving idempotency.
5. **SQL blocks R3 transitions.** `0009` deliberately limits V4:
   - `guard_v4_finance_fact` accepts only `ObservationSeal` and recomputes the seal bytes from `fact_seq - 1`.
   - `check_v4_cursor` (deferred) requires `last_intent_sequence = 0` and that the last fact is a seal.
   - `check_fact_matches_cursor` (from `0002`) requires `count(finance_fact) = cursor.last_fact_seq`, which makes a child starting at `k+1` impossible without a V4 branch exemption.
   - The `0002` cursor update guard requires `last_fact_seq = old + 1`; a child cursor must be created at `k`.
   - `guard_v4_scheduler` genesis requires `last_fact_seq = 0` and the initial command digest from `run_id`; a child needs a separate genesis rule.
   - `guard_v4_finance_fact` seeds the first prefix from `uuid_send(run_id)` at `fact_seq = 1`; the child's prefix must continue from the inherited digest.
   - All of these are replaced via `CREATE OR REPLACE` in `0010`. `0009` stays unmodified.
6. **Reducer is seal plus PaymentIntent only.** Screening, ReviewDecision, Settlement and ReturnInstruction currently return `IllegalStep`. They must delegate to `FinanceLedgerReducer.Apply` (V2), as the PaymentIntent path already does. The V2 `LedgerFinanceContext` pins the policy via `FromPins`, so v8 works there too.
7. **Handler seam.** `V4SimulationStore` takes a test-supplied `testHandler` and commits the command outcome in its own transaction. R3 needs a handler that runs inside that transaction. `PaymentLedgerStore` methods open their own connections, so shared-transaction primitives must be extracted for Screening, Settlement, Cases and Returns (not yet inspected line by line).
8. **Genesis digest.** `V4SimulationScheduler.InitialCommandDigest(runId)` seeds from the run ID. A child's scheduler is its own journal, so seeding from execution B is acceptable. The inherited fact-prefix digest must come from the checkpoint state, not from B.

## Continuation plan for Codex or any later agent

0. Install SDK 10.0.402 or newer. Run `dotnet build -c Release -warnaserror`. Confirm the model ZIP and manifest hashes match the R1 values.
1. **Checkpoint 1 (stop gate).**
   - Add `0010_v4_branch_execution.sql`: table `revenant.v4_branch_execution` with the fields in section 6.2 of the assignment. Use tenant RLS, a UNIQUE `(tenant_id, fork_operation_key)`, a composite FK to the source run and an immutability trigger. Grant the writer insert and select, grant the replay reader select only, and revoke from PUBLIC.
   - Define the V4 Marrow opening with a constant family `RulesHash`.
   - Write the identity test (assignment section 7) over real PostgreSQL: complete a source that has reached a seal, call `ForkAsync` with a v8 context, then assert equal state bytes and hash, distinct timeline IDs, and the lineage/execution mapping. Also test cross-tenant and altered-hash rejection.
2. Checkpoint 2: branch genesis in `0010`. The child cursor starts at `k`. Relax `check_fact_matches_cursor` and `guard_v4_*` only for V4 rows that have a binding. Materialize the child opening snapshot from the Marrow state at `k`, never from current source SQL.
3. Checkpoints 3 to 4: extend the V4 reducer and SQL guard to the remaining fact kinds. Add the in-transaction handler with the lock order scheduler, accounts, cursor.
4. Checkpoints 5 to 7: counterfactual, concurrency, regression. Checkpoints 8 to 10: commit, ask before the first push, CI, evidence.
5. Remaining deliverables not yet created: `docs/recovery/r3-financial-fork-evidence.md`, `docs/recovery/r3-branch-execution-contract.md`, and the local append to `docs/codex-ml-recovery-log.md`.

## Gate status

| Gate | Status |
|---|---|
| R3-01 | VERIFIED PASS |
| R3-02 to R3-20 | BLOCKED (toolchain) / not started |

## Confirmations

- No `.kiro/**`, `docs/kiro-log.md`, ADR, historical migration, Ghost Lap, V1 to V3 code or model files were changed.
- Local Docker and PlacementOps were not used. Nothing was pushed or merged.
- The private recovery log was not touched.
- R4 readiness: NOT READY (R3 incomplete).
