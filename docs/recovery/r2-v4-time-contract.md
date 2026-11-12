# R2 V4 finance time contract

## Identity and canonical bytes

V4 registers a new Marrow Timeline shape in the existing Revenant finance domain. Its reducer, input, state and context IDs are `revenant-finance-reducer/4`, `revenant-finance-fact/4`, `revenant-finance-state/4` and `revenant-finance-context/4`. Its scheduler is `sim-scheduler/1`, its interval contract is `five-minute-seal/1`, and its seal payload schema is `observation-seal/1`. A run's family is fixed at opening.

All V4 encodings use Marrow canonical format 1. Integers are big-endian, UUIDs are 16 big-endian bytes, and strings and blobs have signed Int32 length prefixes. Encoded digests are 32-byte blobs. The existing v7/v8 policy reference fields retain their catalog string representations.

| Encoding | Field order after Int32 format |
| --- | --- |
| Context | Finance family, scenario version, manifest SHA-256, scheduler ID, interval ID, origin Int64, cadence Int64, base policy ID/config hash/model hash/feature hash, review version, risk mode byte, risk feature schema, optional required model ID/artifact SHA-256/normalization ID/threshold Int32, anomaly pins, liquidity pins |
| State | Length-prefixed frozen V2 ledger state, last effective Int64, last sealed boundary Int64, rolling prefix SHA-256 |
| Input | Run UUID, fact sequence Int64, effective Int64, kind byte, payload schema, payload bytes, evidence-present boolean, then evidence schema and SHA-256 if present |
| Seal payload | `observation-seal/1`, run UUID, boundary Int64, interval start Int64, preceding fact sequence Int64, preceding fact-prefix SHA-256, resolved-command SHA-256 |

Risk tags are `ModelRequired=1` and `NotRequired=2`. Required pins include a nonempty feature schema and model identity, a 32-byte artifact hash and a threshold in 1–1,000,000 under `probability-ppm/1`. Rules-only pins use the empty risk feature field and omit the model fields. Each advisory list has a count followed by model ID and artifact digest pairs in strict ordinal ID order. The codec validates pins offline. Friday Ring is the implemented opening-state adapter. The context format supports other versioned scenario manifests.

Input tags 1–5 name `PaymentIntent`, `ReviewDecision`, `ReturnInstruction`, `Screening` and `Settlement`. Tag 6 names `ObservationSeal`. The pure R2 adapter handles payment intent and seal. Payment intent delegates to the existing ledger reducer for the test fixture. Its inherited intake clock is separate from V4 effective time. Other finance operations fail explicitly in R2.

## Clock and command journal

All authoritative times are checked signed Unix epoch milliseconds. Requested time is scenario or customer input. Admitted time is the locked scheduler's simulated clock. Due time is the earliest legal execution time and must be at least the admitted time. Effective time is assigned on committed execution. R2 dispatches at due time. A blocked attempt advances the scheduler's admission clock to its due time and leaves its effective time unset. The watermark moves only when a seal commits.

At an aligned origin, the simulated clock, last sealed boundary and exclusive watermark equal the origin. Coverage begins there. The first seal ends at origin plus 300,000 ms. Fact order is `(effective_time_ms, fact_seq)` with nondecreasing time and gap-free finance positions. A seal at `b` closes `[b - 300000, b)`. Commands are selected by `(due_time_ms, command_seq)`. A due time at or beyond the next seal must wait. At 09:00, the seal comes before any command effective at 09:00.

`AdmitAsync` assigns the immutable command sequence under the run lock and stores the caller's versioned canonical payload bytes and SHA-256. Each future handler owns canonical validation of its command payload schema. Admission retries by run and key compare kind, class, schema, request and due times, and bytes. A matching retry returns the stored command and its latest result. A conflicting retry fails.

Commands are `Exogenous`, `ConditionalHumanRequest` or `DerivedOpportunity`. `Pending` can become `Completed`, `Cancelled`, `FailedPermanent` or `RetryableBlocked`. The first three are terminal. Only `Completed` carries effective time. A blocked command keeps its sequence and due time, prevents later work from overtaking it, and can return to `Pending` only through explicit retry. Unsupported handlers leave the command unresolved. Outcome evidence hashes use SHA-256 over the canonical string `revenant-v4-command-outcome/1`, status name, outcome code and evidence blob. R2 checks scheduler close eligibility after all work and its last bucket are resolved. R3 owns the financial close transition.

## Transaction and verification

Set the transaction-local tenant, then lock that tenant's scheduler row. Effectful R3 handlers must take existing payment/case and ordered account locks before the existing finance cursor. Seal-only operations lock the cursor immediately after checking the head. Both paths revalidate status and the time barrier under the run lock. A blocked head stops advancement for that run. Different runs lock different scheduler rows.

A seal checks the unresolved head, preceding fact and position, appends to `finance_fact`, updates the existing cursor and advances the scheduler watermark in one transaction. An explicit seal retry uses `(run_id, boundary_ms)` and returns the existing canonical fact. Missing or non-next boundaries fail without changes. Each dispatch call processes at most one command or seal.

The fact-prefix genesis hash is SHA-256 over the canonical string `revenant-v4-fact-prefix/1` and the run UUID. Each step hashes that domain string, preceding digest blob, fact sequence, effective time, kind byte and SHA-256 of the canonical payload. The seal stores the preceding digest and sequence. The reducer checks them before advancing the inherited ledger's `LastFact` and folding the seal. It changes no balance, reservation, case, payment or legacy intake field. The chain seed is independent of suffix policy.

The command digest starts with SHA-256 over the canonical string `revenant-v4-command-completion/1` and the run UUID. Each terminal transition hashes that domain string, preceding digest blob, command sequence, payload hash blob, due time, terminal status string and outcome hash blob. SQL maintains the rolling digest in the terminal command transaction. The audit recomputation orders terminal journal rows by due time and sequence, using only rows with due time below the requested boundary. An unresolved row below the boundary makes reconciliation fail.

Marrow replay reads the pinned context and canonical fact inputs. It verifies the fact-prefix hash without consulting the scheduler or a predictor. The SQL rebuild takes pinned context and reads canonical finance rows under a consistent snapshot. Command digest reconciliation is a separate journal audit.

## PostgreSQL compatibility

Migration `0009_v4_causal_scheduler.sql` adds two forced-RLS tables with tenant-bearing foreign keys, a V4 family allowance, effective-time and rolling-prefix columns, and a unique partial seal index on the existing fact table. Writer privileges cover admission, allowed status changes and scheduler progression. Replay has read access in the current tenant. `RV011` reports V4 invariants.

The payment-or-seal constraint keeps the payment ID mandatory for ordinary finance facts. The existing payment foreign key, intake/screening/settlement uniqueness indexes, evidence pair and V3 evidence trigger, append-only triggers, gap-free trigger, and cursor close/monotonic update guard remain active. The legacy nonnegative cursor insert rule moves into a family-aware guard so that a V4 origin can be signed. A deferred V4 check ties cursor position and time to the canonical seal and watermark, including the case where an update omitted one of those writes. R2's durable V4 write path supports seals only. Its internal run initializer and no-financial-effect dispatcher handler are exercised by tests without host registration.

## R3 and observation projections

The future 09:00 fork ends at the seal of `[08:55, 09:00)`. The child begins from that verified coverage boundary and preserves the original origin and inherited fact chain. The child owns its scheduler and due journal. Carry forward eligible exogenous command payloads, re-authorize conditional human requests and recompute derived opportunities under the new policy. Preserve source facts, hashes and ledger state.

R3 can reuse `ObservedFinanceTimelineVersions.V4Components`, the canonical envelopes, `ObservedFinanceReducer`, the scheduler row lock, the command journal and the seal transaction. It must supply an adapter that runs legal existing ledger operations on the same `NpgsqlTransaction`, after the scheduler lock and before terminal completion. Existing store entry points open their own transactions. R3 must expose shared-transaction internals, bind recorded financial inputs and cursor effective time to the dispatched command, and extend the V4 SQL fact guard and durable rebuild for those inputs. It must also bind the child's SQL and ledger identity to the inherited Marrow prefix at the fork point while preserving source input IDs, canonical bytes and digest. R2 verifies a policy-independent prefix with a fixed source identity. Child identity binding, effective-time financial intake, close and actual branch divergence require R3 implementation and proof.

A seal certifies financial-time coverage and due-command resolution. Advisory projection readiness requires its own versioned output keyed by run, boundary, seal position, prefix digest and projection schema. Missing projection data conveys no zero-activity claim.
