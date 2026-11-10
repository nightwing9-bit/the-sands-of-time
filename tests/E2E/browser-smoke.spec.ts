// Task 4.4: short browser smoke for both app shells with a console/network failure trace.
// Like adapters.spec.ts, /meta and /timeline are answered by page.route (browser-edge test double); nothing here observes
// a backend (real-backend.spec.ts does). The trace stores only method, path (no query), status and console text; no headers, bodies or cookies.
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";

const TIMELINE = "11111111-2222-3333-4444-555555555555";
const CONTRACTS = { apiContract: "1.0.0", realtimeSchemaVersion: 1, timelineErrorCodes: [] as string[] };
const DESCRIPTOR = { sourceId: "smoke-src", readOnly: false, bounds: { start: "0", end: "100" }, markers: [{ position: "50", label: "Mid" }] };

interface Trace { app: string; console: string[]; pageErrors: string[]; requestFailures: string[]; badResponses: string[] }

function attachTrace(page: Page, app: string): Trace {
  const trace: Trace = { app, console: [], pageErrors: [], requestFailures: [], badResponses: [] };
  const path = (url: string) => new URL(url).pathname;
  page.on("console", (m) => { if (m.type() === "error" || m.type() === "warning") trace.console.push(`${m.type()}: ${m.text().slice(0, 300)}`); });
  page.on("pageerror", (e) => trace.pageErrors.push(e.message.slice(0, 300)));
  page.on("requestfailed", (r) => trace.requestFailures.push(`${r.method()} ${path(r.url())} ${r.failure()?.errorText ?? ""}`));
  page.on("response", (r) => { if (r.status() >= 400) trace.badResponses.push(`${r.request().method()} ${path(r.url())} ${r.status()}`); });
  return trace;
}

function saveTrace(trace: Trace, name: string): void {
  mkdirSync("test-results", { recursive: true });
  writeFileSync(`test-results/browser-trace-${trace.app}-${name}.json`, JSON.stringify(trace, null, 2));
}

async function stub(page: Page, restoreStatus = 200): Promise<string[]> {
  const restores: string[] = [];
  await page.route("**/meta/contracts", (route) => route.fulfill({ json: CONTRACTS }));
  await page.route(`**/timeline/${TIMELINE}**`, (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/restore")) {
      const position = url.searchParams.get("position")!;
      restores.push(position);
      if (restoreStatus !== 200) return route.fulfill({ status: restoreStatus, json: { code: "Unavailable" } });
      return route.fulfill({ json: { position, state: { businessTime: "2026-03-04T09:30:00Z", tick: 1, stateHash: "h" + position } } });
    }
    return route.fulfill({ json: DESCRIPTOR });
  });
  return restores;
}

const apps = [
  { name: "revenant", url: `http://localhost:5175/?timeline=${TIMELINE}` },
  { name: "ghost-lap", url: `http://localhost:5174/?timeline=${TIMELINE}` },
];

for (const app of apps) {
  test.describe(`smoke ${app.name}`, () => {
    test("loads with no console errors, page errors or failed requests", async ({ page }) => {
      const trace = attachTrace(page, app.name);
      await stub(page);
      await page.goto(app.url);
      await expect(page.getByTestId("timeline-scrubber")).toBeVisible();
      saveTrace(trace, "load");
      expect(trace.pageErrors).toEqual([]);
      expect(trace.console).toEqual([]);
      // StrictMode aborts the first descriptor fetch in dev, so only non-abort failures count.
      expect(trace.requestFailures.filter((f) => !f.includes("ERR_ABORTED"))).toEqual([]);
      expect(trace.badResponses).toEqual([]);
    });

    test("keyboard: Tab reaches the slider; arrows, Home and End seek", async ({ page }) => {
      const restores = await stub(page);
      await page.goto(app.url);
      const slider = page.getByRole("slider", { name: "Timeline position" });
      await expect(slider).toBeVisible();
      for (let i = 0; i < 6 && !(await slider.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press("Tab");
      await expect(slider).toBeFocused();
      await page.keyboard.press("ArrowRight");
      await expect.poll(() => restores.at(-1)).toBe("1");
      await page.keyboard.press("End");
      await expect.poll(() => restores.at(-1)).toBe("100");
      await page.keyboard.press("Home");
      await expect.poll(() => restores.at(-1)).toBe("0");
    });

    test("reduced motion: prefers-reduced-motion is reflected by the scrubber", async ({ page }) => {
      await stub(page);
      await page.emulateMedia({ reducedMotion: "reduce" });
      await page.goto(app.url);
      await expect(page.getByTestId("timeline-scrubber")).toHaveAttribute("data-reduced-motion", "true");
      await page.emulateMedia({ reducedMotion: "no-preference" });
      await expect(page.getByTestId("timeline-scrubber")).toHaveAttribute("data-reduced-motion", "false");
    });

    test("failure trace: a 503 on restore shows an alert and is recorded, with no verified state", async ({ page }) => {
      const trace = attachTrace(page, app.name);
      const restores = await stub(page, 503);
      await page.goto(app.url);
      await page.getByRole("slider", { name: "Timeline position" }).focus();
      await page.keyboard.press("ArrowRight");
      await expect(page.getByTestId("timeline-error")).toBeVisible();
      await expect(page.getByTestId("verified-state")).toHaveText("No position verified by the server yet.");
      saveTrace(trace, "restore-503");
      expect(restores[0]).toBe("1");
      expect(trace.badResponses).toContain("GET /timeline/" + TIMELINE + "/restore 503");
      expect(trace.pageErrors).toEqual([]);
    });
  });
}
