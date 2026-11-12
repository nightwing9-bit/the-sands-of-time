# R4 observation and dataset contract

R4 executes exogenous instructions through the existing V4 rules-only v7 root,
scheduler and financial ledger. PostgreSQL and the verified Marrow Timeline
remain the financial authority. Observation rows and exports are derived,
versioned records. R4 fits no model.

## Scenario and generation

- Scenario: `revenant-behavioral-episodes/1`; generator:
  `revenant-episode-generator/1`. The catalog is BANK-A Aster Bank, BANK-B
  Bramble Trust, BANK-C Cinder Savings, BANK-D Dune Credit, BANK-E Ember Bank,
  BANK-F Fjord Union.
- The manifest pins seed, origin, duration, each logical account and bank,
  USD opening capital, and policy and command contracts. Initial reserves
  are zero. `BehavioralEpisodeScenario.Create` accepts six population counts
  (2–1,000 each); the current cohort planner generates 3–6 accounts per bank.
  Account keys are seed-scoped, with one logical customer per account.
- The format-1 `revenant-behavioral-episode-manifest/1` wrapper hashes the
  existing canonical manifest content as a byte field, then the generator
  version, duration Int64, four v7 policy reference strings, review version
  and counted command kinds (PaymentIntent, ReviewDecision, ReturnInstruction,
  Screening, Settlement). The context pins this exact hash. This wrapper
  does not change the legacy manifest encoder or V4 context wire format.
- Source metadata uses `revenant-features/1`: reported new beneficiary and
  sanctions-hit flags. The legacy `SyntheticTruth` value is fixed to
  `Legitimate` for generated financial instructions. Hidden truth resides in
  the offline sidecars. No device, network, or identity signals are invented.
- `V4EpisodeConfig.Roots` assigns whole root groups before planning. The
  root identity is SHA-256 of canonical `independent-root-split/1`, master
  seed Int64, split byte (Train=1, Validation=2, Calibration=3, FinalTest=4)
  and zero-based ordinal Int32; the first eight digest bytes form the signed
  root seed. The generator derives named SplitMix64 streams by hashing
  generator version, root key and stream domain. Population, ordinary
  schedules, account behavior, campaigns, counterparties, amounts, timing,
  and campaign identity consume separate streams.
- `V4EpisodePlanner.Plan` returns a financial plan and separate annotation
  object. The planner schedules recurring payments, household bills,
  low-value everyday activity, lawful returns, commercial bursts and large
  occasional transfers. It varies ring routes, account deviations,
  dispersal, coordinated bursts, and beneficiary abuse; legitimate
  lookalikes can be large, novel, or busy. The executor receives only the
  financial plan and observes actual V4 outcomes. Inapplicable attempts
  remain in the attempt stream. A cleared payment can still fail to post
  for lack of available funds.
- Qualification is 24 independent roots (12/4/4/4), each 72 simulated
  hours, with master seed 937041261. The pilot is two independent 48-hour
  roots. The export rejects duplicate group keys, logical accounts,
  campaign keys, and normalized behavior fingerprints. Descendants are
  projected with the same lineage but are not included as additional
  independent roots in this cohort. Any future descendant export must stay
  with its root group.

## Seal and observation semantics

`V4BranchExecution.ReadProjectionHistoryAsync` reads the committed physical
execution, its verified genesis and context, and any certified ancestor
prefixes up to their fork boundaries. The `V4ObservationProjector` folds
facts in sequence order using each segment's own context. An inherited
opening is not a new settlement. A child's historical measurements can
match its ancestor's while the physical owner and projection digest differ.

A five-minute seal ending at `b` covers `[b - 300000, b)` in authoritative
effective Unix milliseconds. The V4 seal is sequenced before any financial
fact at `b`. There is no coverage before genesis. Every materialized row
carries the physical execution UUID, interval, seal fact sequence,
**post-seal** prefix SHA-256, explicit coverage and the row's schema. A
projection digest hashes the canonical row separately. A seal can be
committed before its projection is present.

- **`bank-observation/1`** has six rows per seal. Demand is attributed
  to the sending bank. It records intake count and principal; distinct
  sending and receiving accounts; screening count, cleared, held,
  rejected and pending; settled gross inbound/outbound, net and count;
  compensating gross inbound/outbound and count; new, released or
  consumed, and active reserves; posted, reserved and available principal.
  Each bank counts a settlement or return once when it participates.
  Same-bank payments contribute to both gross directions and zero net.
  A rejected screening creates no active reserve. Insufficient settlement
  with no posting contributes no settlement or compensating return.
  Bank boundaries reconcile to the previous balance plus signed posted
  flows and reservation changes. System net is zero for both internal
  settlement and returns.
- **`account-activity-observation/1`** materializes only accounts
  touched by activity in the interval. The row contains bank and account,
  balance, last successful settlement time, elapsed time since most
  recent account event, and activity windows. The `AccountAsOf` query
  reconstructs a covered zero from genesis or the latest sparse balance
  and the ordered event prefix. An unknown account, unsealed boundary or
  the origin has no completed observation; an existing seal with no events
  supports a genuine zero. `ReadAccountActivityAsync` reads only physical
  sparse rows; use `AccountAsOf` to distinguish those cases.
- The 5-, 30-, 60-minute and 24-hour windows have inclusive starts. For
  completed account observations the seal boundary is exclusive. A fraud
  snapshot includes same-time activity at lower fact positions. A window
  whose start precedes origin is `NotEnoughHistory` and reports only the
  observed portion. A covered zero is `Complete` with zero counts/volumes.
  Window order: duration Int64, coverage byte, counts and minor-unit
  amounts for settled inbound, settled outbound, submitted, held,
  compensated return inbound and outbound (count/amount pairs); then
  distinct counterparties, distinct submitted beneficiaries, new and
  familiar beneficiary counts. Beneficiary novelty uses preceding eligible
  submitted intents in this run, with no claim about prehistory.
- **`fraud-asof-observation/1`** is one row per committed Screening. Its
  proof names the execution, logical intent, screening effective time,
  exact cutoff fact sequence, last completed seal boundary (origin
  sentinel until the first seal), and canonical digest at the cutoff.
  The cutoff is immediately before Screening, and the candidate's own
  intake is excluded from historical aggregates, including novelty. The
  whitelist is exactly `V4FraudInputs`: amount, currency, source posted/
  reserved/available, amount-to-available ppm (null at zero available;
  capped at Int64 max), the two observed source flags, previously seen
  beneficiary and counterparty flags, time since last outgoing submission
  or posting, UTC minute of day and day of week (Sunday 0), and the four
  windows above. The feature writer has no truth, campaign, split,
  rule outcome, eventual case, return, final balance, or raw identity
  predictor. The candidate and episode keys belong to the outer row.
- **`bank-eight-horizon-target/1`** uses only later bank rows from the
  same execution. At origin `t` (a completed seal), entry `h` is
  `[t+(h-1)·300000, t+h·300000)`, for 1–8. Each entry records an explicit
  presence bit, then actual settled inbound/outbound/net/count, return
  inbound/outbound/count, and future posted/reserved/available. Missing
  entries contain no financial values. The full eligibility bit requires
  all eight. Targets and target coverage must not enter origin features.

Migration 0011 creates exactly two derived tables with forced tenant RLS
and insert/read privileges for the ledger writer; the replay role only
reads. `MaterializeBanksAsync` and `MaterializeAccountsAsync` accept an
exact retry, compare stored bytes and metadata on conflict, and fail
without overwrite on disagreement. Materialization does not move a
financial cursor or change a seal. All physical records use canonical
format 1 with explicit tags, field order, signed Int64 minor units and time,
and strict hash, schema and end-of-input checks on decode.

## Canonical exports and offline truth

`V4DatasetExporter` emits eight separately hashed streams per split:
`fraud-asof`, `bank`, `account`, `targets`, `attempt-outcomes`,
`payment-truth`, `anomaly-truth`, and `campaign-truth`. Full table files
are optional; hashing always covers the exact export bytes. Each stream
starts with canonical Int32 1, the string `revenant-dataset-stream/1`,
and stream name, followed by a big-endian Int32 byte length and canonical
record for each row in deterministic root/fact or boundary/entity order.
Strings and byte arrays have big-endian Int32 lengths, Booleans use one
byte, and no monetary field uses floating point.

- The predictor row is canonical format 1, `dataset-fraud-row/1`, source
  schema `fraud-asof-observation/1`, logical episode and intent keys,
  cutoff sequence, screening time, last seal boundary, then
  `V4FraudInputs.Write` in the field order above. Optional Int64 values
  have a presence byte and carry a value only when present. A consumer
  must never treat the envelope as predictors.
- `dataset-bank-row/1`, `dataset-account-row/1`, and
  `dataset-target-row/1` start with the source schema and logical episode,
  interval start, boundary, fact sequence, coverage byte, then the
  corresponding typed record's `WriteContent` fields. Operational UUIDs
  and run-dependent prefix/digests stay in persisted audit records;
  normalized exports retain logical keys and values across physical runs.
- `episode-truth/1` distinguishes payment (tag 1), account anomaly (2),
  and campaign (3). Payments join on episode/intent; malicious intent,
  family, campaign and hard-negative annotation are separate from real
  lifecycle outcomes. The attempt stream records all planned attempts
  including unscored attempts and whether settlement actually posted.
  `malicious && posted` is realized fraudulent settlement, with any later
  return recorded separately. A v7/v8 rule decision is never a fraud
  label. Anomaly truth joins on episode/account/interval and has explicit
  anomaly and non-anomaly annotations; an unlabeled interval is not
  automatically negative.
- `dataset-manifest/1` pins the source Git revision, versions, master
  seed, origin, duration, bank catalog, root group keys, opening and
  normalized behavior hashes, and per-split table counts/bytes/digests.
  The aggregate model and truth digests hash separate ordered lists of
  stream names and SHA-256 digests. `manifest.bin` is canonical;
  `manifest.json` and `sample.json` are derived. The published manifest
  SHA-256 is the hash of `manifest.bin`. Runtime, process memory and CI
  identifiers live in evidence, outside dataset identity.

The field order after the typed row envelope is:

| Stream | Body order |
| --- | --- |
| `bank` | Bank key; posted, reserved, available; intent count, submitted principal, distinct senders, distinct receivers, screened, cleared, held, rejected, pending; settled in, settled out, settlement count; return in, return out, return count; reserve additions, reserve releases, net settled. |
| `account` | Account key, bank key; posted, reserved, available; four activity windows; optional last successful activity, optional elapsed time since activity. |
| `targets` | Bank key, full-coverage Boolean; eight entries of future boundary and coverage Boolean, with covered entries carrying settled in/out/net/count, return in/out/count, posted/reserved/available. |
| `attempt-outcomes` | Intent key, intake status string, optional screening/settlement/return status strings, screened Boolean, posted Boolean. |
| `payment-truth` | Truth tag 1, episode key, intent key, malicious Boolean, hard-negative Boolean, and for malicious rows attack-family byte and campaign key. |
| `anomaly-truth` | Truth tag 2, episode key, account key, interval start, boundary, anomaly-family byte, anomaly Boolean. |
| `campaign-truth` | Truth tag 3, episode key, campaign key, attack-family byte, behavior fingerprint, Int32 count and intent keys in episode order. |

The model-streams digest covers the separate targets file as part of
dataset integrity. A consumer must never merge that file into input
columns for its origin.

## Consumer handoff and regeneration

R5 can use `V4ObservationProjector.FraudAsOf` and
`V4FraudAsOfObservation.Decode` for typed rows, or the logical
`fraud-asof` stream for training. Join the offline payment truth only
after reading the feature records. Fit on Train, choose model and features
on Validation, set threshold/review budget on Calibration, and keep
FinalTest truth unopened for tuning. Use coverage and optional fields
explicitly. See the qualification evidence for counts and hashes.

R7 can use `V4ObservationProjector.Banks`, `Accounts`, `AccountAsOf`,
and `V4BankTargets.Build`, or their corresponding canonical streams.
Evaluate chronologically within each root at real completed seals, require
the feature window's stated warmup and all eight target flags for full
horizon evaluation, and never use data from later positions as inputs.
Read sparse account and anomaly truth by logical account and boundary.
Use settled flows, reserve changes and posted/available snapshots as
separate measures. R4 does not select an anomaly or forecasting model.

To reproduce on the authorized native Linux x64 runner with disposable
Testcontainers, check out the implementation SHA recorded in the
qualification evidence. Use the existing CI command with explicit mode:

```bash
R4_DATASET_MODE=qualification \
R4_CODE_REVISION="$(git rev-parse HEAD)" \
R4_DATASET_OUTPUT="$(mktemp -d)" R4_WRITE_TABLES=1 \
dotnet test tests/Revenant/Revenant.Tests.csproj -c Release \
  --filter 'Category=R4Cohort&FullyQualifiedName~Generate_native_R4_cohort'
```

Omit `R4_WRITE_TABLES` to retain only compact evidence and compute the same
hashes. The ordinary pull-request workflow runs only the smoke and regression
gates. The existing CI workflow's R4 branch-only edit trigger can explicitly
select `pilot` or `qualification`; full raw data is not uploaded.
