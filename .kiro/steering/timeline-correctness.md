---
inclusion: fileMatch
fileMatchPattern:
  - "src/Marrow.Timeline/**/*.cs"
  - "src/Marrow.Kernel/**/*.cs"
  - "tests/Marrow/**/*"
  - "web/packages/timeline-ui/**/*"
---
# Shared Timeline local guardrails

TimelinePosition is nonnegative logical long, decimal-string on wire. Game tick steps include empty ticks; bank steps hold causally ordered exogenous facts. Complete versioned snapshots, canonical hashes, append-only journal, idempotent fork, read-only parent prefix and independent suffix. Reducers have no clock/network/DB side effects. Missing or incompatible version/hash fails closed. Branches cannot be called verified without replay verification. Domain-specific outcome comparisons are adapters, not Timeline rules. Refer to `docs/architecture/03-contracts.md` only when altering public contracts.
