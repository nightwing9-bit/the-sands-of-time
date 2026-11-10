import { randomBytes } from "node:crypto";
import { defineConfig } from "@playwright/test";
// GHOST LAP GREYBOX SUITE (ghostlap-greybox.spec.ts): Spec 06 task 6.5. Same bounded servers as playwright.revenant.config.ts but only the
// Ghost Lap shell is started. No page.route anywhere: the greybox talks to the real Ghost Lap solo recording, tick loop, restore, fork and
// aligned comparison over the real Marrow Timeline and PostgreSQL.
// Servers started and torn down by Playwright:
//   1. tests/E2E/backend, the test-only host: the real Hosts.Api composition over Kestrel on loopback, real PostgreSQL 18 in a
//      Testcontainers container migrated by scripts/db/migrate.sh, the real Marrow Timeline engine and the real GhostLap.Domain.
//      It ends itself after E2E_BACKEND_MAX_SECONDS even if teardown is missed.
//   2. the Ghost Lap Vite shell, with its /meta, /auth, /timeline and /ghost-lap proxies pointed at that host (same-origin for the
//      browser, which the Secure __Host- cookies need). Never reused: a dev server started elsewhere would proxy somewhere else.
// A full solo round is 2700 ticks at the server's 30 Hz, so the journey test plays about 90 s of real time.
const BACKEND_PORT = Number(process.env.E2E_BACKEND_PORT ?? 18081);
const FIXTURE_TOKEN = process.env.E2E_FIXTURE_TOKEN ?? randomBytes(24).toString("hex");
process.env.E2E_FIXTURE_TOKEN = FIXTURE_TOKEN;
process.env.E2E_BACKEND_PORT = String(BACKEND_PORT);
const viteEnv = { VITE_API_ORIGIN: `http://127.0.0.1:${BACKEND_PORT}` };
export default defineConfig({
  testDir: ".",
  testMatch: "ghostlap-greybox.spec.ts",
  workers: 1,
  timeout: 300_000,
  expect: { timeout: 10_000 },
  reporter: [["list"]],
  use: { trace: "retain-on-failure", viewport: { width: 1280, height: 900 } },
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
    { command: "npm run dev -w @sands/ghost-lap", cwd: "../../web", url: "http://localhost:5174", reuseExistingServer: false, timeout: 60_000, env: viteEnv },
  ],
});
