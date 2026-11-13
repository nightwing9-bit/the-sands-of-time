# R7 to R8 advisory handoff

## Series reconstruction available for integration

Read physical execution history through `V4BranchExecution.ReadProjectionHistoryAsync`, project it with `V4ObservationProjector.Project`, and call `AccountOutgoingSeries` or `BankAvailableSeries` at a verified completed seal. For a later isolated replay store, use the same `V4ProjectionHistory` contract with verified genesis, ordered segment facts, pinned segment contexts and fork metadata. Carry the physical execution ID separately from the canonical series digest and origin seal. Compare series bytes at the inherited checkpoint. Build each post-fork prefix from its own suffix. Identical accepted intakes may yield identical anomaly counts even if policy changes reservations and settlement.

Keep the fitted model ZIP immutable and create independent observation state for each execution and subject. Forecast from the latest completed observed balance, using opening balance as reference and only a pinned model scale. Use future targets for offline evaluation. Leave recorded R5/R6 Screening evidence and financial authority with the existing finance path.

## Pending integration

R8 readiness requires qualified and pinned model ZIPs with reference outputs, the selected model and warning policies, advisory evidence DTOs and persistence, typed availability failures, actual published Worker inference and cold reconstruction evidence. The native transfer fixture will exercise branch parity and a changed reservation. Its experimental ZIPs carry no deployment authorization.

## Worker interface implemented for qualification

`Hosts.Worker --v4-advisory <execution-uuid> <completed-boundary-ms> [account-id]` scores an account when supplied and all six banks otherwise. The command returns a typed status, subject, completed origin, model identity, input digest and evidence digest. Available rows include the observed count and native score or the eight forecast steps and both interval variants. Failures return their status without an output. The finance dispatcher remains independent.

The loader expects one applicable `ObservedModelPin` from the run context. It validates the manifest SHA-256 embedded in the model ID and the ZIP SHA-256 before creating a Linux AMD64 inference engine. Each invocation replays the requested prefix independently. Available evidence is stored in `revenant.v4_advisory_evidence` using tenant RLS and an immutable execution, subject, seal, schema, model and policy key. The same digest can be written again. A different digest for the same key fails.

The command and its native test compile. Artifact promotion and the published Worker test depend on qualifying models. The transfer ZIPs and pilot ZIPs are research artifacts.
