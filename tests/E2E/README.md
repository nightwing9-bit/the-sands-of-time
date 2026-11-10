# tests/E2E

Two Playwright suites against the two Vite app shells, plus the test-only backend they can run against.

| Suite | Command (from `tests/E2E`) | What it proves |
|---|---|---|
| Stubbed | `npm run test:stubbed` | Browser behaviour with `/meta` and `/timeline` answered by `page.route` at the network edge: keyboard, reduced motion, smoke trace, requests sent. Fast. **Does not observe any backend.** |
| Real backend | `npm run test:real` | Spec 04 exit gate. No `page.route`. Real `Hosts.Api` composition + real Marrow Timeline engine + PostgreSQL 18 (Testcontainers), both shells proxied to it. Seek, fork, idempotent fork retry, denied access, typed failures, stale seek. |

| Revenant slice | `npm run test:revenant` | Spec 05 task 5.4. No `page.route`. The same real host plus the real Revenant finance engine: start a Friday Ring run, scrub to 09:00, choose policy v8, fork, compare. Every value on screen is compared with the API JSON; state hashes with PostgreSQL; the source history is shown unchanged; stranger/anonymous are denied. |
## Real-backend host (`backend/`)
`E2E.Backend` runs the real `Hosts.Api` `Program` through `WebApplicationFactory` over Kestrel on `127.0.0.1:18081` and adds a fixture Timeline HTTP surface (`GET /timeline/{id}`, `GET /timeline/{id}/restore?position=`, `POST /timeline/{id}/fork`, and token-protected `/__e2e/*` control routes). It also serves the Revenant slice routes (`/revenant/runs`, `/revenant/runs/{id}/restore|branches`, `/revenant/branches/{id}/comparison`, `RevenantSliceEndpoints.cs`) over the real `FinanceTimelineAdapter`; these are a test-host bridge, not the production Revenant API. It is **not** a Revenant or Ghost Lap API and no product host references it:
- the fixture routes are mapped only inside this assembly (path-scoped branch in front of the real pipeline) and startup fails in `Production`;
- identity is the real guest session; authorization is the Spec 03 owner-match policy over the owner persisted in the history manifest (not-owner, unknown id and non-fixture history answer identically);
- `/__e2e/provision|facts|log` need the per-run `X-E2E-Fixture-Token` and expose nothing secret;
- `tests/Integration/Timeline/TimelineSurfaceIsolationTests.cs` and the architecture test `No_product_assembly_references_a_test_only_assembly` prove the product API serves none of it.

Needs Docker, the .NET SDK from `global.json` and `npm ci` in `web/` and here. The host ends itself after `E2E_BACKEND_MAX_SECONDS` (1500 in the config). Evidence JSON (browser requests, host request log, PostgreSQL facts) is written to `test-results/real-backend-*.json`.
