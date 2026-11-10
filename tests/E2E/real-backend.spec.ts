// Spec 04 exit gate, browser half: both app shells in a real browser against a REAL backend.
//   - Nothing in this file calls page.route (or route.fulfill/continue). Every /meta, /auth and /timeline answer comes from the
//     test-only Timeline host (tests/E2E/backend): the real Hosts.Api composition, the real Marrow Timeline engine and PostgreSQL 18.
//   - The host is test-only. Its Timeline routes are NOT a Revenant or Ghost Lap API (see revenantTimelineWire.ts); the product API
//     serves none of them (tests/Integration/Timeline/TimelineSurfaceIsolationTests.cs).
//   - Identity is the real guest session. The owner of every history is the verified guest subject that provisioned it.
//   - Independent checks read PostgreSQL through the host's token-protected fixture routes (state hashes stored in the steps table,
//     children rows, the source manifest before and after a fork) and the host's request log (completed versus aborted).
// Run: npm run test:real (from tests/E2E). Needs Docker for PostgreSQL and the .NET SDK from global.json.
import { expect, test, type Browser, type Page, type Request, type Response } from "@playwright/test";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
const BACKEND = `http://127.0.0.1:${process.env.E2E_BACKEND_PORT ?? 18081}`;
const TOKEN = process.env.E2E_FIXTURE_TOKEN ?? "";
const UUID = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
interface Marker { label: string; position: number }
interface AppCase {
  name: "revenant" | "ghost-lap";
  base: string;
  domain: "revenant" | "ghost-lap";
  wireModule: string;
  steps: number;
  slow: Marker;
  fast: Marker;
  fork: Marker;
  /** The text the shell shows for a server-verified position, written out by hand from the fixture's documented formula. */
  verified: Record<number, string>;
  label: (position: number) => string;
  endLabel: string;
  markerCount: number;
}
const apps: AppCase[] = [
  {
    name: "revenant",
    base: "http://localhost:5175/",
    domain: "revenant",
    wireModule: "/src/revenantTimelineWire.ts",
    steps: 24,
    slow: { label: "Payroll batch", position: 5 },
    fast: { label: "Month end", position: 20 },
    fork: { label: "Settlement cut-off", position: 12 },
    // Event p closes at 2026-03-02T09:00Z + p*(p+7) minutes.
    verified: {
      0: "Verified business time: 2 Mar 2026, 09:00 UTC",
      5: "Verified business time: 2 Mar 2026, 10:00 UTC",
      12: "Verified business time: 2 Mar 2026, 12:48 UTC",
      20: "Verified business time: 2 Mar 2026, 18:00 UTC",
      24: "Verified business time: 2 Mar 2026, 21:24 UTC",
    },
    label: (p) => ({ 5: "Event 5 · 2 Mar 2026, 10:00 UTC", 12: "Event 12 · 2 Mar 2026, 12:48 UTC", 20: "Event 20 · 2 Mar 2026, 18:00 UTC" })[p] ?? `Event ${p}`,
    endLabel: "Event 24",
    markerCount: 3,
  },
  {
    name: "ghost-lap",
    base: "http://localhost:5174/",
    domain: "ghost-lap",
    wireModule: "/src/ghostLapTimelineWire.ts",
    steps: 60,
    slow: { label: "Candle", position: 30 },
    fast: { label: "Burst window", position: 45 },
    fork: { label: "Candle", position: 30 },
    // The tick world records its own tick, which equals the position in this fixture.
    verified: { 0: "Verified tick: 0", 30: "Verified tick: 30", 45: "Verified tick: 45", 60: "Verified tick: 60" },
    label: (p) => ({ 30: "Tick 30 · 00:01", 45: "Tick 45 · 00:01", 60: "Tick 60 · 00:02" })[p] ?? `Tick ${p}`,
    endLabel: "Tick 60 · 00:02",
    markerCount: 2,
  },
];
// ---- Fixture control (Node side, direct to the host with the per-run token) ---------------------------------------------------
async function control<T = any>(path: string): Promise<T> {
  const response = await fetch(BACKEND + path, { headers: { "X-E2E-Fixture-Token": TOKEN } });
  if (!response.ok) throw new Error(`fixture control ${path} -> ${response.status}`);
  return (await response.json()) as T;
}
const facts = (id: string, position?: number) => control(`/__e2e/facts/${id}${position === undefined ? "" : `?position=${position}`}`);
const serverLog = (id: string) => control<{ method: string; path: string; query: string; status: number | null; outcome: string }[]>(`/__e2e/log?timeline=${id}`);
async function waitForLog(id: string, predicate: (e: { method: string; path: string; query: string; status: number | null; outcome: string }) => boolean) {
  await expect.poll(async () => (await serverLog(id)).some(predicate), { timeout: 8_000, message: "host request log" }).toBe(true);
  return (await serverLog(id)).filter(predicate);
}
// ---- Browser helpers -------------------------------------------------------------------------------------------------------
interface Seen { method: string; path: string; status: number | null; failed?: string }
function record(page: Page): { seen: Seen[]; pageErrors: string[] } {
  const seen: Seen[] = [];
  const pageErrors: string[] = [];
  const relevant = (url: string) => /^\/(timeline|meta|auth|__e2e)/.test(new URL(url).pathname);
  page.on("response", (r) => { if (relevant(r.url())) seen.push({ method: r.request().method(), path: new URL(r.url()).pathname + new URL(r.url()).search, status: r.status() }); });
  page.on("requestfailed", (r) => { if (relevant(r.url())) seen.push({ method: r.method(), path: new URL(r.url()).pathname + new URL(r.url()).search, status: null, failed: r.failure()?.errorText }); });
  page.on("pageerror", (e) => pageErrors.push(e.message.slice(0, 300)));
  return { seen, pageErrors };
}
function saveEvidence(name: string, data: unknown): void {
  mkdirSync("test-results", { recursive: true });
  writeFileSync(`test-results/real-backend-${name}.json`, JSON.stringify(data, null, 2));
}
/** Opens the shell and starts the real remembered-guest session (cookie plus CSRF cookie) through the app origin. */
async function startGuest(page: Page, app: AppCase): Promise<void> {
  await page.goto(app.base);
  await expect(page.getByTestId("contract-status")).toHaveText("Server contract 1.0.0 is compatible.");
  const status = await page.evaluate(async () => (await fetch("/auth/guest/start", { method: "POST", credentials: "include" })).status);
  expect(status).toBe(200);
}
/**
 * Provisions a seeded history owned by the browser's own guest session. The control call goes straight to the host (the app's dev
 * proxy only forwards /meta, /auth, /timeline) carrying that session's cookies, its CSRF token and the per-run fixture token, so the
 * host derives the owner from the verified guest subject, exactly as a domain handler would.
 */
async function provision(page: Page, body: Record<string, unknown>): Promise<string> {
  const cookies = await page.context().cookies();
  const csrf = cookies.find((c) => c.name === "__Host-sands_csrf_guest")?.value ?? "";
  expect(cookies.some((c) => c.name === "__Host-sands_guest" && c.httpOnly && c.secure), "the browser holds the real guest session cookie").toBe(true);
  const response = await fetch(`${BACKEND}/__e2e/provision`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Cookie: cookies.map((c) => `${c.name}=${c.value}`).join("; "),
      Origin: BACKEND,
      "X-CSRF-Token": csrf,
      "X-E2E-Fixture-Token": TOKEN,
    },
    body: JSON.stringify(body),
  });
  expect(response.status).toBe(200);
  return ((await response.json()) as { timelineId: string }).timelineId;
}
/** Source of a page-side call to the app's own wire module (served by Vite). Returns the thrown `failure`, or null on success. */
function wireRestore(module: string, timeline: string, position: string): string {
  return `(async () => {
    const wire = (await import(${JSON.stringify(module)})).createTestHostHttpWire();
    try { await wire.restore(${JSON.stringify(timeline)}, ${JSON.stringify(position)}); return null; }
    catch (error) { return error && error.failure ? error.failure : String(error); }
  })()`;
}
const open = async (page: Page, app: AppCase, id: string) => {
  await page.goto(`${app.base}?timeline=${id}`);
};
const slider = (page: Page) => page.getByRole("slider", { name: "Timeline position" });
const marker = (page: Page, m: Marker) => page.getByRole("button", { name: `Marker: ${m.label}` });
const restoreResponse = (page: Page, position: number | string) =>
  page.waitForResponse((r) => new URL(r.url()).pathname.endsWith("/restore") && new URL(r.url()).searchParams.get("position") === String(position));
async function seekByMarker(page: Page, m: Marker): Promise<Response> {
  const [response] = await Promise.all([restoreResponse(page, m.position), marker(page, m).click()]);
  return response;
}
async function newGuestPage(browser: Browser, app: AppCase) {
  const context = await browser.newContext();
  const page = await context.newPage();
  const trace = record(page);
  await startGuest(page, app);
  return { context, page, trace };
}
test.beforeAll(async () => {
  expect(TOKEN.length, "E2E_FIXTURE_TOKEN comes from playwright.real.config.ts; run with npm run test:real").toBeGreaterThan(15);
  const info = await (await fetch(`${BACKEND}/__e2e/info`)).json();
  expect(info.fixture).toBe("sands-e2e-timeline-host");
  expect(info.notAProductionApi).toBe(true);
});
test("this suite never stubs the network: no route interception exists in its source", () => {
  const source = readFileSync(fileURLToPath(import.meta.url), "utf8");
  const interception = new RegExp(["\\.(route|routeFromHAR|unroute|routeWebSocket)\\(", "\\.fulfill\\(", "setOffline\\("].join("|"));
  expect(source.match(interception)).toBeNull();
});
for (const app of apps) {
  test.describe(`real backend · ${app.name}`, () => {
    test("mounts the one shared scrubber from a real describe over the real contract handshake", async ({ browser }) => {
      const { context, page, trace } = await newGuestPage(browser, app);
      const id = await provision(page, { domain: app.domain });
      await open(page, app, id);
      const scrubber = page.getByTestId("timeline-scrubber");
      await expect(scrubber).toBeVisible();
      await expect(scrubber).toHaveAttribute("data-source-id", id);
      await expect(scrubber).not.toHaveAttribute("data-branch-id", /.+/);
      await expect(scrubber).toHaveAttribute("data-read-only", "false");
      await expect(page.getByTestId("timeline-label")).toContainText(`/ ${app.endLabel}`);
      await expect(page.getByRole("button", { name: /^Marker: / })).toHaveCount(app.markerCount);
      await expect(page.getByTestId("verified-state")).toHaveText("No position verified by the server yet.");
      const described = await waitForLog(id, (e) => e.method === "GET" && e.path === `/timeline/${id}` && e.outcome === "completed");
      expect(described.every((e) => e.status === 200)).toBe(true);
      expect(trace.seen.some((s) => s.path === "/meta/contracts" && s.status === 200)).toBe(true);
      expect(trace.pageErrors).toEqual([]);
      saveEvidence(`${app.name}-mount`, { timeline: id, browser: trace.seen, host: await serverLog(id), steps: app.steps });
      await context.close();
    });
    test("a seek shows the state the server verified, and its hash is the one stored in PostgreSQL", async ({ browser }) => {
      const { context, page, trace } = await newGuestPage(browser, app);
      const id = await provision(page, { domain: app.domain });
      await open(page, app, id);
      await expect(page.getByTestId("timeline-scrubber")).toBeVisible();
      const checked: Record<string, string> = {};
      // Marker click, End and Home each issue a real GET /restore; the position on the wire is the exact decimal string.
      const byMarker = await seekByMarker(page, app.fork);
      await expect(page.getByTestId("verified-state")).toHaveText(app.verified[app.fork.position]!);
      await expect(page.getByTestId("timeline-label")).toContainText(app.label(app.fork.position));
      const body = await byMarker.json();
      expect(body.position).toBe(String(app.fork.position));
      expect(body.historyStatus).toBe("Verified");
      const stored = await facts(id, app.fork.position);
      expect(body.state.stateHash).toBe(stored.stateHashAt);
      checked[String(app.fork.position)] = stored.stateHashAt;
      await slider(page).focus();
      const [endResponse] = await Promise.all([restoreResponse(page, app.steps), page.keyboard.press("End")]);
      await expect(page.getByTestId("verified-state")).toHaveText(app.verified[app.steps]!);
      expect((await endResponse.json()).state.stateHash).toBe((await facts(id, app.steps)).stateHashAt);
      const [homeResponse] = await Promise.all([restoreResponse(page, 0), page.keyboard.press("Home")]);
      await expect(page.getByTestId("verified-state")).toHaveText(app.verified[0]!);
      const home = await homeResponse.json();
      expect(home.hashOrigin).toBe("StoredCheckpoint");
      expect(home.state.stateHash).toBe((await facts(id, 0)).stateHashAt);
      expect(trace.pageErrors).toEqual([]);
      saveEvidence(`${app.name}-seek`, { timeline: id, storedHashesChecked: checked, browser: trace.seen, host: await serverLog(id) });
      await context.close();
    });
    test("a position above 2^53 reaches the server as an exact decimal string; the server reads it exactly and refuses it as typed InvalidPosition", async ({ browser }) => {
      const { context, page } = await newGuestPage(browser, app);
      const id = await provision(page, { domain: app.domain });
      await open(page, app, id);
      await expect(page.getByTestId("timeline-scrubber")).toBeVisible();
      // What a JavaScript number would have sent instead.
      expect(String(BigInt(Number("9007199254740993")))).toBe("9007199254740992");
      // The app's own wire module, in the page, against the host. (The UI cannot offer a position beyond the history's real last
      // position: a history of more than 2^53 steps cannot exist, so exactness here is shown at the wire.)
      const [response, failure] = await Promise.all([
        page.waitForResponse((r) => r.url().includes("position=9007199254740993")),
        page.evaluate(wireRestore(app.wireModule, id, "9007199254740993")),
      ]);
      expect(new URL(response.url()).search).toBe("?position=9007199254740993");
      expect(response.status()).toBe(400);
      const problem = await response.json();
      expect(problem).toMatchObject({ timelineError: "InvalidPosition", requestedPosition: "9007199254740993" });
      expect(failure).toMatchObject({ kind: "timeline", code: "InvalidPosition" });
      // Beyond int64 the server refuses without pretending to have parsed it.
      const [beyond] = await Promise.all([
        page.waitForResponse((r) => r.url().includes("position=9223372036854775808")),
        page.evaluate(wireRestore(app.wireModule, id, "9223372036854775808")),
      ]);
      expect(beyond.status()).toBe(400);
      expect((await beyond.json()).requestedPosition).toBeUndefined();
      await context.close();
    });
    test("denied access, an ended session and unreadable histories give the right UI state and never a verified state", async ({ browser }) => {
      const owner = await newGuestPage(browser, app);
      const id = await provision(owner.page, { domain: app.domain });
      // A different guest session: real identity, not the owner.
      const stranger = await newGuestPage(browser, app);
      await open(stranger.page, app, id);
      await expect(stranger.page.getByTestId("timeline-load-error")).toHaveAttribute("data-kind", "denied");
      await expect(stranger.page.getByTestId("timeline-load-error")).toHaveText("You do not have permission to do that.");
      await expect(stranger.page.getByTestId("timeline-scrubber")).toHaveCount(0);
      await expect(stranger.page.getByTestId("verified-state")).toHaveCount(0);
      // No session at all.
      const anonymousContext = await browser.newContext();
      const anonymous = await anonymousContext.newPage();
      await open(anonymous, app, id);
      await expect(anonymous.getByTestId("timeline-load-error")).toHaveAttribute("data-kind", "denied");
      await expect(anonymous.getByTestId("timeline-load-error")).toHaveText("Your session is not active. Sign in again.");
      await expect(anonymous.getByTestId("timeline-scrubber")).toHaveCount(0);
      // The owner is unaffected, and the session ending later denies the next seek without painting anything.
      await open(owner.page, app, id);
      await expect(owner.page.getByTestId("timeline-scrubber")).toBeVisible();
      const signedOut = await owner.page.evaluate(async () => {
        const csrf = document.cookie.split("; ").find((c) => c.startsWith("__Host-sands_csrf_guest="))?.split("=").slice(1).join("=") ?? "";
        return (await fetch("/auth/guest/signout", { method: "POST", credentials: "include", headers: { "X-CSRF-Token": csrf } })).status;
      });
      expect(signedOut).toBe(204);
      const [denied] = await Promise.all([restoreResponse(owner.page, app.fast.position), marker(owner.page, app.fast).click()]);
      expect(denied.status()).toBe(401);
      await expect(owner.page.getByTestId("timeline-error")).toHaveAttribute("data-kind", "denied");
      await expect(owner.page.getByTestId("verified-state")).toHaveText("No position verified by the server yet.");
      await expect(owner.page.getByTestId("fork-result")).toHaveCount(0);
      const entries = await serverLog(id);
      expect(entries.filter((e) => e.status === 403).length).toBeGreaterThanOrEqual(1);
      expect(entries.filter((e) => e.status === 401).length).toBeGreaterThanOrEqual(2);
      expect(entries.some((e) => e.path.endsWith("/restore") && e.status === 200)).toBe(false);
      expect((await facts(id)).children).toEqual([]);
      saveEvidence(`${app.name}-denied`, { timeline: id, stranger: stranger.trace.seen, anonymous: "401 session_inactive", owner: owner.trace.seen, host: entries });
      await Promise.all([owner.context.close(), stranger.context.close(), anonymousContext.close()]);
    });
    test("an incompatible history and a corrupt stored checkpoint are typed failures in the UI; the next valid seek recovers", async ({ browser }) => {
      const { context, page, trace } = await newGuestPage(browser, app);
      const incompatible = await provision(page, { domain: app.domain, scenario: "incompatible" });
      await open(page, app, incompatible);
      await expect(page.getByTestId("timeline-load-error")).toHaveAttribute("data-kind", "timeline");
      await expect(page.getByTestId("timeline-load-error")).toHaveText("This history was recorded with rules or data formats this version cannot read.");
      await expect(page.getByTestId("timeline-scrubber")).toHaveCount(0);
      expect((await serverLog(incompatible)).some((e) => e.path === `/timeline/${incompatible}` && e.status === 422)).toBe(true);
      const corrupt = await provision(page, { domain: app.domain, scenario: "corrupt-checkpoint" });
      await open(page, app, corrupt);
      await expect(page.getByTestId("timeline-scrubber")).toBeVisible();
      const altered: Marker = { label: "Altered checkpoint", position: 8 };
      const failed = await seekByMarker(page, altered);
      expect(failed.status()).toBe(500);
      expect(await failed.json()).toMatchObject({ timelineError: "CorruptHistory", requestedPosition: "8" });
      await expect(page.getByTestId("timeline-error")).toHaveAttribute("data-kind", "timeline");
      await expect(page.getByTestId("timeline-error")).toHaveText("This history failed verification and cannot be shown or branched.");
      await expect(page.getByTestId("verified-state")).toHaveText("No position verified by the server yet.");
      // A position that does not read the altered checkpoint verifies again and clears the error.
      await seekByMarker(page, app.fork);
      await expect(page.getByTestId("verified-state")).toHaveText(app.verified[app.fork.position]!);
      await expect(page.getByTestId("timeline-error")).toHaveCount(0);
      const log = await serverLog(corrupt);
      expect(log.some((e) => e.query === "?position=8" && e.status === 500)).toBe(true);
      expect(log.some((e) => e.query === `?position=${app.fork.position}` && e.status === 200)).toBe(true);
      expect(trace.pageErrors).toEqual([]);
      saveEvidence(`${app.name}-typed-failures`, { incompatible, corrupt, browser: trace.seen, host: log });
      await context.close();
    });
    test("fork reaches the real engine, persists an independent child exactly once, and the child restores from PostgreSQL", async ({ browser }) => {
      const { context, page, trace } = await newGuestPage(browser, app);
      const id = await provision(page, { domain: app.domain });
      await open(page, app, id);
      const seek = await seekByMarker(page, app.fork);
      const sourceHash = (await seek.json()).state.stateHash as string;
      const before = await facts(id);
      expect(before.children).toEqual([]);
      const forkRequest = page.waitForRequest((r: Request) => r.method() === "POST" && r.url().endsWith(`/timeline/${id}/fork`));
      await page.getByRole("button", { name: "Fork from here" }).click();
      const request = await forkRequest;
      const posted = request.postDataJSON() as { position: unknown; operationKey: string };
      expect(posted.position).toBe(String(app.fork.position));
      expect(typeof posted.position).toBe("string");
      expect(request.headers()["idempotency-key"]).toBe(posted.operationKey);
      expect((request.headers()["x-csrf-token"] ?? "").length).toBeGreaterThan(20);
      await expect(page.getByTestId("fork-result")).toHaveText(new RegExp(`^Server created branch ${UUID}\\.$`));
      const branchId = (await page.getByTestId("fork-result").innerText()).match(new RegExp(UUID))![0];
      const forkedResponse = await (await request.response())!.json();
      expect(forkedResponse.branchId).toBe(branchId);
      // PostgreSQL: one child, with the key and position the browser sent, and the source row did not move.
      const after = await facts(id);
      expect(after.children).toHaveLength(1);
      expect(after.children[0]).toMatchObject({ id: branchId, operationKey: posted.operationKey, forkPosition: String(app.fork.position), lastPosition: String(app.fork.position), status: "Recording" });
      expect(after.history).toEqual(before.history);
      expect((await facts(branchId, app.fork.position)).stateHashAt).toBe(sourceHash);
      // The child, opened in the browser, is its own history over the source's verified prefix.
      await open(page, app, branchId);
      const scrubber = page.getByTestId("timeline-scrubber");
      await expect(scrubber).toHaveAttribute("data-branch-id", branchId);
      await expect(scrubber).toHaveAttribute("data-source-id", id);
      await slider(page).focus();
      const [childResponse] = await Promise.all([restoreResponse(page, app.fork.position), page.keyboard.press("End")]);
      expect((await childResponse.json()).state.stateHash).toBe(sourceHash);
      await expect(page.getByTestId("verified-state")).toHaveText(app.verified[app.fork.position]!);
      // The Recording child is not a Verified fork source: the engine's Conflict is shown, and no grandchild exists.
      const [home] = await Promise.all([restoreResponse(page, 0), page.keyboard.press("Home")]);
      expect(home.status()).toBe(200);
      await page.getByRole("button", { name: "Fork from here" }).click();
      await expect(page.getByTestId("timeline-error")).toHaveAttribute("data-kind", "timeline");
      await expect(page.getByTestId("timeline-error")).toHaveText("The timeline changed or the request conflicts with an earlier one. Reload and try again.");
      await expect(page.getByTestId("fork-result")).toHaveCount(0);
      expect((await facts(branchId)).children).toEqual([]);
      expect((await facts(id)).children).toHaveLength(1);
      expect(trace.pageErrors).toEqual([]);
      saveEvidence(`${app.name}-fork`, { source: id, child: branchId, key: posted.operationKey, hostFactsAfter: after, browser: trace.seen, host: await serverLog(id) });
      await context.close();
    });
    test("a fork whose answer is lost is retried with the same key and still yields exactly one child", async ({ browser }) => {
      const { context, page, trace } = await newGuestPage(browser, app);
      const id = await provision(page, { domain: app.domain, dropFirstForkResponse: true });
      await open(page, app, id);
      await seekByMarker(page, app.fork);
      const keys: string[] = [];
      page.on("request", (r) => { if (r.method() === "POST" && r.url().endsWith(`/timeline/${id}/fork`)) keys.push(r.headers()["idempotency-key"] ?? ""); });
      const fork = page.getByRole("button", { name: "Fork from here" });
      await fork.click();
      // The host committed the child and then cut the connection: the browser sees a network failure and shows no branch.
      // (Through the Vite dev proxy a cut upstream connection reaches the browser as an empty 500; direct it is a network error.)
      await expect(page.getByTestId("timeline-error")).toHaveAttribute("data-kind", /^(offline|server)$/);
      await expect(page.getByTestId("fork-result")).toHaveCount(0);
      const afterFirst = await facts(id);
      expect(afterFirst.children).toHaveLength(1);
      await expect(fork).toBeEnabled();
      await fork.click();
      await expect(page.getByTestId("fork-result")).toHaveText(new RegExp(`^Server created branch ${UUID}\\.$`));
      expect(keys).toHaveLength(2);
      expect(keys[1]).toBe(keys[0]);
      expect(keys[0]).not.toBe("");
      const branchId = (await page.getByTestId("fork-result").innerText()).match(new RegExp(UUID))![0];
      const afterRetry = await facts(id);
      expect(afterRetry.children).toHaveLength(1);
      expect(afterRetry.children[0].id).toBe(branchId);
      expect(afterRetry.children[0].id).toBe(afterFirst.children[0].id);
      const forkAttempts = trace.seen.filter((s) => s.method === "POST" && s.path.endsWith("/fork"));
      expect(forkAttempts.map((s) => s.status === 200)).toEqual([false, true]);
      saveEvidence(`${app.name}-fork-retry`, { source: id, key: keys[0], child: branchId, childrenAfterFirstAttempt: afterFirst.children.length, childrenAfterRetry: afterRetry.children.length, browser: trace.seen });
      await context.close();
    });
    test("a slow earlier seek never overwrites a newer verified result", async ({ browser }) => {
      const { context, page, trace } = await newGuestPage(browser, app);
      const id = await provision(page, { domain: app.domain, slowRestoreMs: { [String(app.slow.position)]: 2500 } });
      await open(page, app, id);
      await expect(page.getByTestId("timeline-scrubber")).toBeVisible();
      await marker(page, app.slow).click();
      await expect(page.getByTestId("timeline-scrubber")).toHaveAttribute("data-view-state", "loading");
      const [fast] = await Promise.all([restoreResponse(page, app.fast.position), marker(page, app.fast).click()]);
      expect(fast.status()).toBe(200);
      await expect(page.getByTestId("verified-state")).toHaveText(app.verified[app.fast.position]!);
      // The superseded request is cancelled in the browser and observed as aborted by the host; nothing arrives to overwrite the display.
      await expect.poll(() => trace.seen.some((s) => s.failed === "net::ERR_ABORTED" && s.path.endsWith(`?position=${app.slow.position}`))).toBe(true);
      const slowEntries = await waitForLog(id, (e) => e.query === `?position=${app.slow.position}`);
      expect(slowEntries.every((e) => e.outcome === "aborted")).toBe(true);
      await page.waitForTimeout(2800);
      await expect(page.getByTestId("verified-state")).toHaveText(app.verified[app.fast.position]!);
      await expect(page.getByTestId("timeline-label")).toContainText(app.label(app.fast.position));
      await expect(page.getByTestId("timeline-error")).toHaveCount(0);
      expect((await serverLog(id)).filter((e) => e.query === `?position=${app.fast.position}`).every((e) => e.outcome === "completed" && e.status === 200)).toBe(true);
      saveEvidence(`${app.name}-stale-seek`, { timeline: id, browser: trace.seen, host: await serverLog(id) });
      await context.close();
    });
  });
}
