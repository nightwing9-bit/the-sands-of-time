import { randomBytes } from "node:crypto";
import { defineConfig } from "@playwright/test";
// REAL-BACKEND SUITE (real-backend.spec.ts): the Spec 04 exit-gate proof. Three bounded servers are started and torn down by Playwright:
//   1. tests/E2E/backend, the test-only Timeline host: the real Hosts.Api composition over Kestrel on loopback, real PostgreSQL 18 in a
//      Testcontainers container migrated by scripts/db/migrate.sh, and the real Marrow Timeline engine. It ends itself after
//      E2E_BACKEND_MAX_SECONDS even if teardown is missed.
//   2. and 3. both Vite app shells, with their /meta, /auth and /timeline proxies pointed at that host (same-origin for the browser,
//      which the Secure __Host- cookies need). Never reused: a dev server started elsewhere would proxy somewhere else.
// No test in this suite calls page.route to answer a /meta, /auth or /timeline request.
const BACKEND_PORT = Number(process.env.E2E_BACKEND_PORT ?? 18081);
const FIXTURE_TOKEN = process.env.E2E_FIXTURE_TOKEN ?? randomBytes(24).toString("hex");
// Workers inherit this, so the spec can authenticate to the fixture control routes with the same per-run secret.
process.env.E2E_FIXTURE_TOKEN = FIXTURE_TOKEN;
process.env.E2E_BACKEND_PORT = String(BACKEND_PORT);
const viteEnv = { VITE_API_ORIGIN: `http://127.0.0.1:${BACKEND_PORT}` };
export default defineConfig({
  testDir: ".",
  testMatch: "real-backend.spec.ts",
  workers: 1,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [["list"]],
  use: { trace: "retain-on-failure" },
  webServer: [
    {
      command: "dotnet build backend/E2E.Backend.csproj --configuration Release --nologo --verbosity quiet && dotnet backend/bin/Release/net10.0/E2E.Backend.dll",
      cwd: ".",
      url: `http://127.0.0.1:${BACKEND_PORT}/__e2e/info`,
      timeout: 240_000,
      reuseExistingServer: false,
      stdout: "pipe",
      stderr: "pipe",
      gracefulShutdown: { signal: "SIGTERM", timeout: 20_000 },
      env: { E2E_FIXTURE_TOKEN: FIXTURE_TOKEN, E2E_BACKEND_PORT: String(BACKEND_PORT), E2E_BACKEND_MAX_SECONDS: "1500", ASPNETCORE_ENVIRONMENT: "Testing" },
    },
    { command: "npm run dev -w @sands/revenant", cwd: "../../web", url: "http://localhost:5175", reuseExistingServer: false, timeout: 60_000, env: viteEnv },
    { command: "npm run dev -w @sands/ghost-lap", cwd: "../../web", url: "http://localhost:5174", reuseExistingServer: false, timeout: 60_000, env: viteEnv },
  ],
});
