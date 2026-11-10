// Task 4.3 (FAST STUBBED SUITE): both app shells mount the one shared scrubber through their own thin adapter, and every number they
// show came from a request the test observed. The /timeline answers below are a test double at the browser's network edge (page.route):
// they prove the requests the shells send and how they render answers, NOT backend behaviour. Seek/fork/error against the real Timeline
// engine and PostgreSQL is proven by real-backend.spec.ts (npm run test:real), which uses no page.route.
import { expect, test, type Page, type Request } from "@playwright/test";

const TIMELINE = "11111111-2222-3333-4444-555555555555";
const CONTRACTS = { apiContract: "1.0.0", realtimeSchemaVersion: 1, timelineErrorCodes: [] as string[] };

interface Seen { restores: string[]; forks: Request[]; descriptor: number }

async function stubBackend(page: Page, opts: { descriptor: object; restore: (position: string) => { status: number; body: unknown }; branchId?: string }): Promise<Seen> {
  const seen: Seen = { restores: [], forks: [], descriptor: 0 };
  await page.route("**/meta/contracts", (route) => route.fulfill({ json: CONTRACTS }));
  await page.route(`**/timeline/${TIMELINE}**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname.endsWith("/restore")) {
      const position = url.searchParams.get("position")!;
      seen.restores.push(position);
      const { status, body } = opts.restore(position);
      return route.fulfill({ status, json: body });
    }
    if (url.pathname.endsWith("/fork")) {
      seen.forks.push(request);
      return route.fulfill({ json: { branchId: opts.branchId ?? "branch-from-server" } });
    }
    seen.descriptor += 1;
    return route.fulfill({ json: opts.descriptor });
  });
  return seen;
}

const apps = [
  {
    name: "revenant",
    url: `http://localhost:5175/?timeline=${TIMELINE}`,
    descriptor: { sourceId: "ledger-src", readOnly: false, bounds: { start: "0", end: "9007199254740993" }, markers: [{ position: "5", label: "Payroll" }] },
    restore: (position: string) => ({ status: 200, body: { position, state: { businessTime: "2026-03-04T09:30:00Z", stateHash: "h" + position } } }),
    expectLabel: /Event 1 · 4 Mar 2026, 09:30 UTC/,
    expectState: /Verified business time: 4 Mar 2026, 09:30 UTC/,
  },
  {
    name: "ghost-lap",
    url: `http://localhost:5174/?timeline=${TIMELINE}`,
    descriptor: { sourceId: "ghost-src", readOnly: false, bounds: { start: "0", end: "2700" }, markers: [{ position: "90", label: "Candle" }] },
    restore: (position: string) => ({ status: 200, body: { position, state: { tick: Number(position) * 30, stateHash: "h" + position } } }),
    // Position 1 is 1 tick; the server-reported state tick (30) is deliberately different so the two are not conflated.
    expectLabel: /Tick 1 · 00:00/,
    expectState: /Verified tick: 30/,
  },
];

for (const app of apps) {
  test.describe(app.name, () => {
    test("mounts the shared scrubber and renders only server-verified state after a keyboard seek", async ({ page }) => {
      const seen = await stubBackend(page, { descriptor: app.descriptor, restore: app.restore });
      await page.goto(app.url);
      const scrubber = page.getByTestId("timeline-scrubber");
      await expect(scrubber).toBeVisible();
      await expect(scrubber).toHaveAttribute("data-source-id", app.descriptor.sourceId);
      await expect(page.getByTestId("verified-state")).toHaveText("No position verified by the server yet.");
      expect(seen.restores).toEqual([]);

      await page.getByRole("slider", { name: "Timeline position" }).focus();
      await page.keyboard.press("ArrowRight");
      await expect(page.getByTestId("verified-state")).toHaveText(app.expectState);
      await expect(page.getByTestId("timeline-label")).toHaveText(app.expectLabel);
      expect(seen.restores).toEqual(["1"]);
      // The dev shells run React StrictMode, which mounts effects twice, so the descriptor GET may be issued 1 or 2 times
      // (the first is aborted). Asserting an exact count was flaky (observed 2 on reruns); at least one is the real requirement.
      expect(seen.descriptor).toBeGreaterThanOrEqual(1);
      expect(seen.descriptor).toBeLessThanOrEqual(2);
    });

    test("fork sends the verified position with an idempotency key and shows the server branch id", async ({ page }) => {
      const seen = await stubBackend(page, { descriptor: app.descriptor, restore: app.restore, branchId: "branch-42" });
      await page.goto(app.url);
      await page.getByRole("slider", { name: "Timeline position" }).focus();
      await page.keyboard.press("ArrowRight");
      await expect(page.getByTestId("verified-state")).toHaveText(app.expectState);
      await page.getByRole("button", { name: "Fork from here" }).click();
      await expect(page.getByTestId("fork-result")).toHaveText("Server created branch branch-42.");
      expect(seen.forks).toHaveLength(1);
      const fork = seen.forks[0]!;
      expect(fork.method()).toBe("POST");
      const body = fork.postDataJSON() as { position: string; operationKey: string };
      expect(body.position).toBe("1");
      expect(body.operationKey).toBeTruthy();
      expect(fork.headers()["idempotency-key"]).toBe(body.operationKey);
    });

    test("a denied preview shows the permission state and never renders a verified state or a branch", async ({ page }) => {
      const seen = await stubBackend(page, { descriptor: app.descriptor, restore: () => ({ status: 403, body: { code: "Forbidden" } }) });
      await page.goto(app.url);
      await page.getByRole("slider", { name: "Timeline position" }).focus();
      await page.keyboard.press("ArrowRight");
      await expect(page.getByTestId("timeline-error")).toHaveAttribute("data-kind", "denied");
      await expect(page.getByTestId("verified-state")).toHaveText("No position verified by the server yet.");
      await expect(page.getByTestId("fork-result")).toHaveCount(0);
      expect(seen.restores).toEqual(["1"]);
      expect(seen.forks).toHaveLength(0);
    });
  });
}

test("revenant big position keeps exact decimal precision on the wire", async ({ page }) => {
  const seen = await stubBackend(page, { descriptor: apps[0]!.descriptor, restore: apps[0]!.restore });
  await page.goto(apps[0]!.url);
  await page.getByRole("slider", { name: "Timeline position" }).focus();
  await page.keyboard.press("End");
  await expect.poll(() => seen.restores).toEqual(["9007199254740993"]);
});
