---
inclusion: always
---
# Repository and execution boundaries

`src/Marrow.Kernel`, `Marrow.Timeline`, `Marrow.Infrastructure`: reusable. `src/Revenant` + `.Contracts` and `src/GhostLap` + `.Contracts`: mutually isolated domains. `src/Hosts.*` host composition only. `web/packages/timeline-ui`: sole shared scrubber; `web/apps/{revenant,ghost-lap}` independent domain visuals. `tests/{Architecture,Marrow,Revenant,GhostLap,Integration,E2E}`; `scripts/checks`; `docs/kiro-log.md`. Implement the **active** `.kiro/specs/spec-NN-*/tasks.md`, one dependent task at a time. Log genuine tests and significant decisions. Contract changes require a compatibility check and potentially an ADR. No full-context ingestion or arbitrary new service/project hierarchies.
