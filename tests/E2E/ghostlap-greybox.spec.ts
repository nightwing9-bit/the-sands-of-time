// Spec 06 task 6.5: the Ghost Lap V1 greybox in a real browser against a REAL backend.
//   - No page.route, no stubbed answer (the first test asserts this about this file's own source).
//   - The host is the TEST-ONLY one in tests/E2E/backend: the real Hosts.Api composition (guest identity, CSRF), the real Marrow Timeline
//     engine, the real GhostLap.Domain (SoloOriginals, SoloBranches, the ghost-v1 reducer) and PostgreSQL 18. Not a production API.
//   - The test plays with the keyboard only (arrow keys), steering by the server's own world readout. Expected values are never written
//     into this file: on-screen numbers are compared with the server's JSON and with the hashes stored in PostgreSQL.
// Run: npm run test:ghostlap (from tests/E2E). Needs Docker for PostgreSQL and the .NET SDK from global.json.
import { expect, test, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const BACKEND = `http://127.0.0.1:${process.env.E2E_BACKEND_PORT ?? 18081}`;
const TOKEN = process.env.E2E_FIXTURE_TOKEN ?? "";
const APP = "http://localhost:5174/";

async function control<T = any>(path: string): Promise<T> {
  const response = await fetch(BACKEND + path, { headers: { "X-E2E-Fixture-Token": TOKEN } });
  if (!response.ok) throw new Error(`fixture control ${path} -> ${response.status}`);
  return (await response.json()) as T;
}
const facts = (id: string, position?: number) => control(`/__e2e/facts/${id}${position === undefined ? "" : `?position=${position}`}`);

/** Same-origin call from inside the page (browser cookies + the double-submit CSRF header), outside the app's own code. */
async function pageFetch(page: Page, method: string, path: string, body?: unknown, key?: string): Promise<{ status: number; body: any }> {
  return page.evaluate(async ({ method, path, body, key }) => {
    const csrf = document.cookie.split("; ").find((c) => c.startsWith("__Host-sands_csrf_guest="))?.split("=").slice(1).join("=") ?? "";
    const headers: Record<string, string> = { Accept: "application/json", "Content-Type": "application/json", "X-CSRF-Token": decodeURIComponent(csrf) };
    if (key) headers["Idempotency-Key"] = key;
    const r = await fetch(path, { method, headers, credentials: "include", body: body === undefined ? undefined : JSON.stringify(body) });
    return { status: r.status, body: await r.json().catch(() => null) };
  }, { method, path, body, key });
}

type Readout = { tick: number; x: number; y: number; charge: number; score: number; pickups: number; collected: number; candles: { x: number; y: number }[]; hash: string };
async function readout(page: Page, testId: string): Promise<Readout | null> {
  return page.evaluate((id) => {
    const el = document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
    if (!el) return null;
    const n = (k: string) => Number(el.dataset[k]);
    const candles = (el.dataset.candles ?? "").split(";").filter(Boolean).map((c) => { const [, x, y] = c.split(":"); return { x: Number(x), y: Number(y) }; });
    return { tick: n("tick"), x: n("x"), y: n("y"), charge: n("charge"), score: n("score"), pickups: n("pickups"), collected: n("collected"), candles, hash: el.dataset.stateHash ?? "" };
  }, testId);
}

/** Holds exactly the arrow keys for (dx, dy); releases the others. */
async function steer(page: Page, held: Set<string>, dx: number, dy: number) {
  const want = new Set<string>();
  if (dx > 0) want.add("ArrowRight");
  if (dx < 0) want.add("ArrowLeft");
  if (dy > 0) want.add("ArrowDown");
  if (dy < 0) want.add("ArrowUp");
  for (const k of [...held]) if (!want.has(k)) { await page.keyboard.up(k); held.delete(k); }
  for (const k of want) if (!held.has(k)) { await page.keyboard.down(k); held.add(k); }
}

test.beforeAll(async () => {
  expect(TOKEN.length, "E2E_FIXTURE_TOKEN comes from playwright.ghostlap.config.ts; run with npm run test:ghostlap").toBeGreaterThan(15);
  const info = await (await fetch(`${BACKEND}/__e2e/info`)).json();
  expect(info.notAProductionApi).toBe(true);
});

test("this suite never stubs the network: no route interception exists in its source", () => {
  const source = readFileSync(fileURLToPath(import.meta.url), "utf8");
  const interception = new RegExp(["\\.(route|routeFromHAR|unroute|routeWebSocket)\\(", "\\.fulfill\\(", "setOffline\\("].join("|"));
  expect(source.match(interception)).toBeNull();
});

test("greybox journey: play a full solo run by keyboard, reload the original, rewind to a charged tick, fork, inert ghost and aligned delta", async ({ browser }) => {
  test.setTimeout(300_000);
  const context = await browser.newContext();
  const page = await context.newPage();
  const pageErrors: string[] = [];
  page.on("pageerror", (e) => pageErrors.push(e.message.slice(0, 300)));
  await page.goto(APP);
  await expect(page.getByTestId("contract-status")).toHaveText("Server contract 1.0.0 is compatible.");
  await expect(page.getByTestId("start-view")).toBeVisible();

  // 1. Play: the server starts the recording and its 30 Hz tick loop; the browser only sends arrow-key intents.
  const started = page.waitForResponse((r) => r.request().method() === "POST" && new URL(r.url()).pathname === "/ghost-lap/solo/sessions");
  await page.getByRole("button", { name: "Start solo run" }).click();
  const startedResponse = await started;
  expect(startedResponse.status()).toBe(200);
  const first = await startedResponse.json();
  expect(first.kind).toBe("original");
  expect(first.world.tick).toBe(0);
  const sessionId: string = first.sessionId;
  await expect(page.getByTestId("live-view")).toHaveAttribute("data-status", "Running");
  await expect(page.getByTestId("arena")).toHaveAttribute("data-renderer", "pixi");
  await expect(page.locator('[data-testid="arena"] canvas')).toBeVisible();

  // An untrusted vector outside -1..1 is a typed 400 and moves nothing.
  const spoof = await pageFetch(page, "POST", `/ghost-lap/sessions/${sessionId}/inputs`, { sequence: 1_000_000, clientTick: 1, moveX: 40, moveY: 0, requestBurst: false });
  expect(spoof.status).toBe(400);
  expect(spoof.body.code).toBe("ghostlap_invalid_request");

  const held = new Set<string>();
  let last = { x: 0, y: 0, at: Date.now() };
  let detourUntil = 0;
  let detour = { dx: 1, dy: 1 };
  let checkedHud = false;
  while (await page.getByTestId("live-view").count()) {
    const r = await readout(page, "live-readout");
    if (!r) break;
    if (!checkedHud && r.tick > 300) {
      // The HUD and the readout are the same server frame: score text equals the frame's authoritative total.
      const frame = await pageFetch(page, "GET", `/ghost-lap/sessions/${sessionId}/frame`);
      expect(frame.status).toBe(200);
      expect(frame.body.world.tick).toBeGreaterThanOrEqual(r.tick);
      await expect(page.getByTestId("hud-score")).toHaveText(/^Score \d+$/);
      checkedHud = true;
    }
    const now = Date.now();
    if (now - last.at > 400) {
      if (Math.hypot(r.x - last.x, r.y - last.y) < 3 && held.size > 0) {
        detour = { dx: Math.random() < 0.5 ? -1 : 1, dy: Math.random() < 0.5 ? -1 : 1 };
        detourUntil = now + 600;
      }
      last = { x: r.x, y: r.y, at: now };
    }
    if (now < detourUntil) {
      await steer(page, held, detour.dx, detour.dy);
    } else {
      const target = r.candles.sort((a, b) => Math.hypot(a.x - r.x, a.y - r.y) - Math.hypot(b.x - r.x, b.y - r.y))[0];
      if (target) {
        const dx = Math.abs(target.x - r.x) > 6 ? Math.sign(target.x - r.x) : 0;
        const dy = Math.abs(target.y - r.y) > 6 ? Math.sign(target.y - r.y) : 0;
        await steer(page, held, dx, dy);
      } else {
        await steer(page, held, 0, 0);
      }
    }
    await page.waitForTimeout(60);
  }
  await steer(page, held, 0, 0);

  // 2. Only a completed, replay-verified original opens the replay view.
  await expect(page.getByTestId("replay-view")).toBeVisible({ timeout: 30_000 });
  const originalId = await page.getByTestId("replay-view").getAttribute("data-original-id");
  expect(originalId).toBe(sessionId);
  expect(new URL(page.url()).searchParams.get("original")).toBe(originalId);
  const stamp = page.getByTestId("original-stamp");
  await expect(stamp).toContainText("ORIGINAL RUN PRESERVED · Verified");
  const finalHash = await stamp.getAttribute("data-final-hash");
  const before = await facts(originalId!);
  expect(before.history).toMatchObject({ domainId: "ghost-lap", status: "Verified", lastPosition: "2700", parentId: null });
  expect(before.history.ownStepRows).toBe(2700); // every tick, empty ones included
  expect(before.history.lastRecordHash).toMatch(/^[0-9a-f]{64}$/);

  // Reload: the same guest reopens the original, re-verified from PostgreSQL with the same final hash.
  await page.reload();
  await expect(page.getByTestId("replay-view")).toHaveAttribute("data-original-id", originalId!);
  await expect(page.getByTestId("original-stamp")).toHaveAttribute("data-final-hash", finalHash!);
  const opened = await pageFetch(page, "GET", `/ghost-lap/originals/${originalId}`);
  expect(opened.status).toBe(200);
  expect(opened.body.finalStateHash).toBe(finalHash);
  expect(opened.body.markers.length).toBeGreaterThan(0); // the keyboard run collected candles

  // 3. Rewind with the shared scrubber. The final tick is inspect-only: blocked in the UI and refused by the server.
  const slider = page.getByRole("slider", { name: "Timeline position" });
  await slider.focus();
  await page.keyboard.press("End");
  await expect(page.getByTestId("replay-readout")).toHaveAttribute("data-tick", "2700");
  await expect(page.getByTestId("fork-reason")).toHaveAttribute("data-reason", "end_of_timeline");
  const finalFork = await pageFetch(page, "POST", `/ghost-lap/originals/${originalId}/branches`, { position: "2700", operationKey: "final-fork" }, "final-fork");
  expect(finalFork.status).toBe(409);
  expect(finalFork.body.code).toBe("ghostlap_fork_not_allowed");

  // Seek to the tick of the first collected candle: charge > 0 and a consumed candle, restored by the server.
  const firstMarker = opened.body.markers[0];
  const forkTick = Number(firstMarker.position);
  await page.getByRole("button", { name: `Marker: ${firstMarker.label}` }).click();
  const replay = page.getByTestId("replay-readout");
  await expect(replay).toHaveAttribute("data-tick", String(forkTick));
  const atT = (await readout(page, "replay-readout"))!;
  expect(atT.charge).toBeGreaterThan(0);
  expect(atT.collected).toBeGreaterThan(0);
  expect(atT.pickups).toBeGreaterThan(0);
  expect(atT.hash).toBe((await facts(originalId!, forkTick)).stateHashAt); // the restore is the stored, hashed state
  await expect(page.getByTestId("hud-charge")).toHaveAttribute("data-charge", String(atT.charge));

  // 4. Fork ("Fork from here") at T: the branch inherits exactly the source state at T.
  const branched = page.waitForResponse((r) => r.request().method() === "POST" && new URL(r.url()).pathname.endsWith("/branches"));
  await page.getByRole("button", { name: "Fork from here" }).click();
  const branchResponse = await branched;
  expect(branchResponse.status()).toBe(200);
  const { branchId, forkPosition } = await branchResponse.json();
  expect(forkPosition).toBe(String(forkTick));
  const challenge = page.getByTestId("challenge-view");
  await expect(challenge).toHaveAttribute("data-branch-id", branchId);
  await expect(challenge).toHaveAttribute("data-source-id", originalId!);
  await expect(challenge).toHaveAttribute("data-fork-tick", String(forkTick));
  await expect(challenge).toHaveAttribute("data-status", "Ready");
  const inherited = (await readout(page, "live-readout"))!;
  expect(inherited).toMatchObject({ tick: forkTick, x: atT.x, y: atT.y, charge: atT.charge, score: atT.score, pickups: atT.pickups, collected: atT.collected });
  await expect(page.getByTestId("compare-hud")).toHaveAttribute("data-delta", "0"); // same state at T

  // 5. Start the rewrite and stay still: the ghost keeps its original trace, the living world keeps its own candles and score.
  await page.getByRole("button", { name: "Start rewrite" }).click();
  await expect(challenge).toHaveAttribute("data-status", "Running");
  // The restored state may still hold the vector accepted just before T for up to six ticks (ghost-v1 hold); let it expire.
  await expect.poll(async () => (await readout(page, "live-readout"))!.tick, { timeout: 5_000 }).toBeGreaterThan(forkTick + 10);
  const settled = (await readout(page, "live-readout"))!;
  const ghostStart = (await readout(page, "ghost-readout"))!;
  let sawNegative = false;
  let ghostMoved = false;
  for (let i = 0; i < 40; i++) {
    await page.waitForTimeout(100);
    const sample = await page.evaluate(() => {
      const hud = document.querySelector('[data-testid="compare-hud"]') as HTMLElement;
      const live = document.querySelector('[data-testid="live-readout"]') as HTMLElement;
      const ghost = document.querySelector('[data-testid="ghost-readout"]') as HTMLElement;
      return { hud: { ...hud.dataset }, live: { ...live.dataset }, ghost: { ...ghost.dataset } };
    });
    // Aligned: ghost, current and delta are for one and the same tick, and nothing is called final before the final tick.
    expect(sample.hud.tick).toBe(sample.live.tick);
    expect(sample.ghost.tick).toBe(sample.live.tick);
    expect(Number(sample.hud.delta)).toBe(Number(sample.hud.currentScore) - Number(sample.hud.ghostScore));
    expect(Number(sample.hud.currentScore)).toBe(Number(sample.live.score));
    expect(Number(sample.hud.ghostScore)).toBe(Number(sample.ghost.score));
    expect(sample.hud.final).toBe("false");
    // Inert ghost: whatever the ghost does, the idle living world collects nothing and does not move.
    expect(Number(sample.live.pickups)).toBe(settled.pickups);
    expect(Number(sample.live.collected)).toBe(settled.collected);
    expect(Number(sample.live.x)).toBe(settled.x);
    expect(Number(sample.live.y)).toBe(settled.y);
    if (Number(sample.hud.delta) < 0) sawNegative = true;
    if (Math.hypot(Number(sample.ghost.x) - ghostStart.x, Number(sample.ghost.y) - ghostStart.y) > 20) ghostMoved = true;
  }
  expect(ghostMoved, "the source ghost follows its recorded trace").toBe(true);
  expect(sawNegative, "an idle branch falls behind the moving original at the same tick").toBe(true);
  await expect(page.getByTestId("delta-pill")).toHaveText(/BEHIND$/);

  // The branch is playable: an arrow key moves the living player on the server.
  const beforeMove = (await readout(page, "live-readout"))!;
  await page.keyboard.down("ArrowRight");
  await page.waitForTimeout(500);
  await page.keyboard.up("ArrowRight");
  await page.waitForTimeout(200);
  const afterMove = (await readout(page, "live-readout"))!;
  expect(afterMove.tick).toBeGreaterThan(beforeMove.tick);
  expect(afterMove.x !== beforeMove.x || afterMove.y !== beforeMove.y).toBe(true);

  // 6. The source history is untouched by the branch; the branch is its own Timeline child at T.
  const after = await facts(originalId!);
  expect(after.history).toEqual(before.history);
  const child = after.children.find((c: any) => c.id === branchId);
  expect(child).toMatchObject({ forkPosition: String(forkTick), status: "Recording" });
  const reopened = await pageFetch(page, "GET", `/ghost-lap/originals/${originalId}`);
  expect(reopened.body.finalStateHash).toBe(finalHash);
  expect(pageErrors).toEqual([]);
  const end = (await readout(page, "live-readout"))!;
  console.log(`GHOSTLAP_OBSERVED original=${originalId} finalHash=${finalHash} pickupMarkers=${opened.body.markers.length} finalScore=${opened.body.finalScore} ` +
    `forkTick=${forkTick} chargeAtT=${atT.charge} collectedAtT=${atT.collected} scoreAtT=${atT.score} branch=${branchId} ` +
    `branchTick=${end.tick} lastDelta=${await page.getByTestId("compare-hud").getAttribute("data-delta")}`);
  await context.close();
});
