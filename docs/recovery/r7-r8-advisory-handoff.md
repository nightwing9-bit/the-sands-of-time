# R7 to R8 advisory handoff

## Series reconstruction available for integration

Read physical execution history through `V4BranchExecution.ReadProjectionHistoryAsync`, project it with `V4ObservationProjector.Project`, and call `AccountOutgoingSeries` or `BankAvailableSeries` at a verified completed seal. For a later isolated replay store, use the same `V4ProjectionHistory` contract with verified genesis, ordered segment facts, pinned segment contexts and fork metadata. Carry the physical execution ID separately from the canonical series digest and origin seal. Compare series bytes at the inherited checkpoint. Build each post-fork prefix from its own suffix. Identical accepted intakes may yield identical anomaly counts even if policy changes reservations and settlement.

Keep the fitted model ZIP immutable and create independent observation state for each execution and subject. Forecast from the latest completed observed balance, using opening balance as reference and only a pinned model scale. Use future targets for offline evaluation. Leave recorded R5/R6 Screening evidence and financial authority with the existing finance path.

## Pending integration

R8 readiness requires qualified and pinned model ZIPs with reference outputs, the selected model and warning policies, advisory evidence DTOs and persistence, typed availability failures, actual published Worker inference and cold reconstruction evidence. The native transfer fixture will exercise branch parity and a changed reservation. Its experimental ZIPs carry no deployment authorization.
