# Spec 01 — Repository, hosts, persistence and CI

**Basket:** 1 | **Dependencies:** none (greenfield starting point) | **Owner paths:** `src/Hosts.*`, `src/Marrow.Kernel`, `deploy/`, `tests/Architecture`, `.github/workflows/ci.yml`, `scripts/checks/`

## User value and outcome

A runnable, observable, structurally enforced .NET 10/React monorepo with real Postgres and Redis local environment and separate host compositions.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No payment/game business rules, ML model, replay engine, multi-replica realtime sessions, cloud IaC, final art.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R01.01 — Clean bootstrap.** WHEN the repo is cloned fresh, THE SYSTEM SHALL the documented setup build API, Realtime and Worker configurations, both Vite shells, Postgres and Redis through a single Compose flow without secret defaults.
**R01.02 — Explicit boundaries.** WHEN a source module is compiled, THE SYSTEM SHALL Marrow libraries remain free of application-domain dependencies, and Revenant and GhostLap internals remain mutually isolated.
**R01.03 — Schema ownership.** WHEN a migration is applied, THE SYSTEM SHALL each module own its schema and its migration history, with no cross-schema foreign keys or unauthorized direct writes.
**R01.04 — Process health.** WHEN a dependency fails or is unavailable, THE SYSTEM SHALL affected readiness show unhealthy while liveness remain truthful and startup retries are bounded.
**R01.05 — Repeatable CI.** WHEN a pull request changes code, THE SYSTEM SHALL restore, compile, typed JS checks, architecture boundary checks and cheap unit tests run in GitHub Actions.
**R01.06 — Tracked hackathon artifacts.** WHEN project initializes, THE SYSTEM SHALL root `.kiro/`, a chosen actual OSS LICENSE, README, no tracked secret, and evidence log are present.
**R01.07 — Tool script safety.** WHEN a hook is configured before its scripts exist, THE SYSTEM SHALL all three hooks stay disabled; deterministic gate scripts skip unavailable suites clearly, not print false passes.
**R01.08 — API contracts.** WHEN an endpoint DTO changes, THE SYSTEM SHALL OpenAPI schema and typed frontend generation have one documented script path and fail on stale generated clients.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

Clean-clone Compose+both UI shells+all three hosts start; targeted CI is green; forbidden dependency test is shown failing when injected; no hook executes nonexistent tests.
