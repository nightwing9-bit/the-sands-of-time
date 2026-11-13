# R7 SSA series and model contract

## Causal inputs

`V4ObservationProjector.Project` verifies the V4 fact fold and applies each segment under its own pinned context. Both series builders take a subject and an as-of boundary. A completed seal supplies its physical execution, fact sequence and canonical prefix digest. Rebuilding the same prefix yields the same canonical series bytes. Those bytes encode schema, subject, unit, genesis boundary, requested boundary, coverage, optional reference, and each observed boundary, fact sequence, prefix digest and value. The physical execution ID is retained on the series and each seal. It is excluded from the series hash so an inherited prefix can have the same hash across a source and child.

- `AccountOutgoingSeries(accountId, throughBoundaryMs)` produces `account-outgoing-velocity-series/1`. It counts one committed `PaymentIntent` per accepted intake in `[boundary - 300000, boundary)`. A later Screening rejection still leaves the intake counted. At the exact seal time the next interval begins. Valid accounts come from genesis. Empty completed intervals have zero intents, and opening or future unsealed origins report `UnobservedOrigin`. An unknown account reports `UnknownSubject`.
- `BankAvailableSeries(bankId, throughBoundaryMs)` produces `bank-available-balance-series/1`. It uses reconciled V4 bank observations in canonical bank order, exactly six per completed seal. Each USD minor-unit value is checked posted less reserved. The bank's opening available balance is a normalization reference. The model-visible input contains observations through the requested seal only. `V4BankTargets` supplies the eight future five-minute values separately for backtesting, with null for an unobserved horizon.

`AdvisoryBalanceNormalization` uses the `available-balance-opening-centered/1` transform. It subtracts the reference using `Int128`, divides by the pinned positive minor-unit scale and converts to `Single`. Reconstruction rounds the scaled delta to even, checks addition and narrowing, and rejects nonfinite output. Predicted values and interval endpoints may be negative. The transfer fixture uses scale 1,024 and records the observed maximum round-trip error.

## Initial Linux AMD64 gate

ML.NET TimeSeries 5.0.1 uses the existing Linux AMD64 OpenMP runtime setup. The ordinary platform probe continues to verify the published Worker platform. The R7 case fits one contiguous account history and one contiguous bank history, saves both models, and loads a fresh engine from each frozen ZIP for each independent subject and reconstruction. The experiment consumes 49 observations before its first reported origin, including the current completed bucket. The preceding 48 buckets also feed a trailing empirical p95 anomaly baseline. The forecast experiment compares each fully covered origin with persistence, records nominal interval coverage, checks updating and forecast-only call parity, and tests repeat forecasts from the same state. These are transfer settings and exploratory diagnostics.

| Model | Transfer settings |
| --- | --- |
| `DetectSpikeBySsa` | Positive side, confidence 95, training window 32, seasonality 8, p-value history 16 |
| `ForecastBySsa` | Window 8, series length 32, train size 96, eight horizons, non-adaptive, nominal 95% |

The fitted forecaster can emit a path before seeing any serving observations. The experiment records its first horizon before and after warmup. Transferability and the production warmup remain open until the native evidence and longitudinal pilot are evaluated. A production model manifest, calibrated intervals, alert policy, and Worker advisory service are pending.

## Advisory serving and failure states

The Worker resolves exact model pins from the run context. Artifacts use `Intelligence/Artifacts/revenant-ssa/{anomaly|forecast}/{manifest-sha256}/` with a manifest and ZIP. The ZIP SHA-256 is checked against both context and manifest. The invocation creates a separate ML.NET engine per subject, consumes only the supplied verified completed prefix and records the output at that origin. An unavailable origin or subject is explicit. Artifact and platform failures produce `ArtifactMissing`, `ArtifactInvalid`, or `RuntimeUnavailable`. An account or bank that lacks the pinned history reports `InsufficientHistory`.

Available predictions are canonicalized in `anomaly-evidence/1` and `liquidity-forecast-evidence/1` and stored in the advisory projection. Evidence binds physical execution, lineage, subject, origin seal, input digest, model, policy and result. Predictions do not enter Screening. The serving contract currently implements genesis-centered inference. A corrected method can be promoted only with the same inference transformation in the Worker and the evaluation.
