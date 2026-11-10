# Spec 01 design — Repository, hosts, persistence and CI

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** none (greenfield starting point). **This spec owns:** `src/Hosts.*`, `src/Marrow.Kernel`, `deploy/`, `tests/Architecture`, `.github/workflows/ci.yml`, `scripts/checks/`. **Do not implement:** No payment/game business rules, ML model, replay engine, multi-replica realtime sessions, cloud IaC, final art.

**Consumes:** Public contracts from the frozen architecture and repo structure; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Composition and slices

Use one `.slnx` (or conventional .sln) and .NET 10 SDK pin. Create actual Kernel, Timeline, Infrastructure, each small domain Contracts, two domain, and three host projects only when used by compile or integration tests. **Freeze and scaffold the existing `Marrow.Kernel` as the dependency-free owner of pure actor (`IActorContext`), realtime envelope metadata and outbox/inbox contract records/ports from `03-contracts.md`; implementations belong later in Infrastructure.** No new shared `Marrow.Contracts` assembly is needed. Domains may reference Kernel/Timeline and their domain Contracts; they must not import shared Infrastructure or the other domain. API wires REST/auth and app controllers; Realtime wires typed SignalR and room hosting **later**; Worker wires one background pipeline registration. For greenfield initial minimal logic is health/root endpoints, not fake business success. The two Vite apps are distinct entrypoints; `web/packages/contracts` and `timeline-ui` workspaces are created but Timeline UI implementation belongs to 04. Hosts share DI registrations and configuration; no cross-domain references in shared projects. Include Dockerfiles with distinct host entrypoints, local HTTPS developer allowance with reverse proxy for later real deployment. Set Realtime replicas=1 by default with explicit comment warning against increasing it.

Database baseline uses a single PostgreSQL service; **each owning spec creates only its own migrations** (`marrow_timeline` in 02, `marrow_identity` in 03, `revenant` and `ghostlap` later). Spec 01 owns the database provisioning and **one explicit migration executor**: `scripts/db/migrate.sh`, invoked by a one-shot Compose `migrate` job prior to all three hosts, and by the same command in CI/manual local setup. It executes only migrations that exist, in a stable documented order, and returns nonzero on failure. Hosts do not run migrations automatically or race each other at startup. No cross-schema FKs. Redis is not the primary source for balances, history or rating. Use connection pooling, health checks, structured logging with trace identifiers and no secrets. Use local `.env.example` with fake placeholders, `.gitignore` excludes `.env` but **not `.kiro/`**. Repository tracks SPDX-compatible chosen license after user selection; license choice is a concrete operator action, not permission to invent one.

### Fast gate scripts and CI

`scripts/checks/contract-guardian.sh`: check contracts build+NetArchTest/TS types without modifying code; `spec-gate.sh`: reads `scripts/checks/.active-gate` (tracked sample but runtime state gitignored) and runs allow-listed task suites only; `browser-evidence.sh`: reads named browser route and executes Playwright when available, else reports 'not provisioned' and exits nonzero when invoked as a required gate. They must not recursively invoke Kiro. Hooks initially **disabled**. Install real GitHub Actions matrix including `dotnet restore/build/test` and `npm ci/typecheck`; keep integration suites as explicit gate workflow or jobs when Testcontainers arrive. Contract lint should prevent direct app↔app references and game-domain references to rendering and finance domain references to game. Architecture suite should inspect actual compiled assemblies rather than directory names only.

### Local acceptance scenario

On a clean checkout, `docker compose up --build` starts healthful Postgres/Redis plus host processes and serves both Vite app shells (development script may run Vite outside Compose only if one documented path fully works); `dotnet test` and frontend typecheck pass. Stop Redis then check readiness, restart and recover. No live gameplay/payment claims from health endpoints.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Architecture test: direct Revenant→GhostLap and vice versa must fail
- Architecture test: Kernel references no Infrastructure/Timeline/domain/ASP.NET/EF/SignalR assemblies; Timeline references Kernel only; domains do not reference shared Infrastructure. Deliberately introduced forbidden edge fails.
- One-shot migration-runner dry/clean boot (including before domain migrations exist), host ordering and health readiness; Redis outage recovery
- CI green on true clean checkout (not generated log output)
- Secrets scan and root `.kiro` tracking verification

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Economical for workspace wiring; strong review for schema/host authority choices.
