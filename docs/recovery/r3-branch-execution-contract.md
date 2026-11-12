# R3 branch execution contract

## Identity checkpoint

A root shares its canonical lineage and SQL execution UUID. A child keeps that lineage in its state, canonical inputs and input IDs. It owns a different SQL execution UUID and a different Marrow Timeline ID.

Migration `0010_v4_branch_execution.sql` stores the tenant, execution, lineage and Timeline IDs, the source execution and Timeline IDs, fork sequence and boundary, 32-byte state and prefix hashes, the pinned context hash, the operation key and seal-only readiness. The binding is immutable and tenant-scoped. The insert guards check the source's closed SQL cursor and authentic seal, the verified Marrow source, and the child's Marrow origin and checkpoint. The replay role can read the binding in its tenant.

The child cursor starts at the fork position `k`. The child's scheduler clock and watermark start at the fork boundary and retain the scenario origin. The first physical suffix fact is `k+1`. It uses the digest after source seal `k`. Further facts use the immediately preceding sequence. The child creates its own command digest from its physical execution UUID. V2/V3 cursor counting and the R2 unbound root remain on their original zero base.

`V4BranchExecution` opens the root and calls the existing Marrow fork. The Marrow rules hash covers the V4 schemas, scheduler, observation contract and cadence. The v7 and v8 rules-only policies are pinned in their respective V4 contexts. Fork validation allows that policy change and preserves the rest of the context and the opening state.

The seal transaction locks the scheduler before the cursor. Completion closes the SQL cursor, reads the SQL seal history in a repeatable-read transaction and appends each original seal to Marrow with its deterministic input ID as the operation key. It verifies each stored SQL digest. Forking requires a closed SQL history and a Verified Timeline at the same position. Resubmitting the same operation verifies the prior Marrow receipts and returns the same binding. Marrow creation and SQL registration use separate transactions. If SQL registration rolls back, retrying the same request reuses the existing Marrow Timeline.

## Remaining R3 work

Both bound executions currently admit seals only. Physical child account and obligation materialization, durable reservation of the complete fork request, the transactional payment bridge and financial fact reconciliation are pending. Independent exogenous continuation, derived opportunities and human request applicability are pending. R4 interfaces depend on completing and verifying those stages.
