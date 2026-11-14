import { defineConfig } from "@playwright/test";
// FAST STUBBED SUITE (adapters.spec.ts, browser-smoke.spec.ts). Starts both Vite app shells; the browser is the only real part.
// /meta and /timeline are answered per test with page.route, so these tests prove browser behaviour and the requests sent, not
// backend behaviour. The real-backend proof for the Spec 04 exit gate is real-backend.spec.ts, run with playwright.real.config.ts
// (npm run test:real): no page.route, a real Hosts.Api composition, the real Timeline engine and PostgreSQL.
export default defineConfig({
  testDir: ".",
  testMatch: "*.spec.ts",
  testIgnore: ["real-backend.spec.ts", "revenant-slice.spec.ts", "ghostlap-greybox.spec.ts", "revenant-r8.spec.ts"],
  workers: 1,
  reporter: [["list"]],
  use: { trace: "retain-on-failure" },
  webServer: [
    { command: "npm run dev -w @sands/revenant", cwd: "../../web", url: "http://localhost:5175", reuseExistingServer: true, timeout: 60_000 },
    { command: "npm run dev -w @sands/ghost-lap", cwd: "../../web", url: "http://localhost:5174", reuseExistingServer: true, timeout: 60_000 },
  ],
});
