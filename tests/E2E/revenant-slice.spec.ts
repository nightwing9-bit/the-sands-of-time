// Spec 05 task 5.4: the Revenant V1 slice in a real browser against a REAL backend.
//   - No page.route, no stubbed answer (the first test asserts this about this file's own source).
//   - The host is the TEST-ONLY one in tests/E2E/backend: the real Hosts.Api composition (guest identity, CSRF), the real Marrow Timeline
//     engine, the real Revenant FinanceTimelineAdapter/FinanceReducer and PostgreSQL 18. It is not a production Revenant API.
//   - Every number asserted on screen is compared with the JSON the server returned to the browser, and the state hashes are compared
//     with the hashes stored in PostgreSQL (read through the token-protected fixture routes). The expected strings are formatted here,
//     independently of the app's formatter, from the API values; no expected balance or count is written into this file.
// Run: npm run test:revenant (from tests/E2E). Needs Docker for PostgreSQL and the .NET SDK from global.json.
import { expect, test, type Page, type Response } from "@playwright/test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const BACKEND = `http://127.0.0.1:${process.env.E2E_BACKEND_PORT ?? 18081}`;
const TOKEN = process.env.E2E_FIXTURE_TOKEN ?? "";
const APP = "http://localhost:5175/";

// ---- Independent formatting (integer arithmetic) ---------------------------------------------------------------------------------
const money = (minor: number, currency = "USD") =>
  `${minor < 0 ? "-" : ""}${currency} ${Math.trunc(Math.abs(minor) / 100).toLocaleString("en-US")}.${String(Math.abs(minor) % 100).padStart(2, "0")}`;

// ---- Server-side facts (Node side, straight to the host) ---------------------------------------------------------------------------
async function control<T = any>(path: string): Promise<T> {
  const response = await fetch(BACKEND + path, { headers: { "X-E2E-Fixture-Token": TOKEN } });
  if (!response.ok) throw new Error(`fixture control ${path} -> ${response.status}`);
  return (await response.json()) as T;
}
const facts = (id: string, position?: number | string) => control(`/__e2e/facts/${id}${position === undefined ? "" : `?position=${position}`}`);

/** The same request the browser makes, repeated from Node with the browser's own session cookies (independent read of the API). */
async function apiGet(page: Page, path: string): Promise<{ status: number; body: any }> {
  const cookies = await page.context().cookies();
  const response = await fetch(BACKEND + path, { headers: { Cookie: cookies.map((c) => `${c.name}=${c.value}`).join("; "), Accept: "application/json" } });
  return { status: response.status, body: await response.json().catch(() => null) };
}

async function openApp(page: Page): Promise<void> {
  await page.goto(APP);
  await expect(page.getByTestId("contract-status")).toHaveText("Server contract 1.0.0 is compatible.");
}

/** Clicks Start and returns the run description and the opening (position 0) restore, as the server answered the browser. */
async function startRun(page: Page, seed?: string): Promise<{ run: any; opening: any }> {
  if (seed !== undefined) await page.getByTestId("seed-input").fill(seed);
  const started = page.waitForResponse((r) => r.request().method() === "POST" && new URL(r.url()).pathname === "/revenant/runs");
  const opened = page.waitForResponse((r) => new URL(r.url()).pathname.endsWith("/restore") && new URL(r.url()).searchParams.get("position") === "0");
  await page.getByRole("button", { name: /Start (another )?synthetic Friday Ring run/ }).click();
  const startResponse = await started;
  expect(startResponse.status()).toBe(200);
  const run = await startResponse.json();
  const opening = await (await opened).json();
  await expect(page.getByTestId("banks-panel")).toBeVisible();
  return { run, opening };
}

async function expectBanksMatch(page: Page, state: any): Promise<void> {
  await expect(page.getByTestId("banks-panel")).toHaveAttribute("data-state-hash", state.stateHash);
  expect(state.banks).toHaveLength(6);
  for (const bank of state.banks) {
    const row = page.getByTestId(`bank-${bank.bankId}`);
    await expect(row).toContainText(bank.name);
    await expect(row.locator('[data-field="posted"]')).toHaveText(money(bank.postedMinor, state.currency));
    await expect(row.locator('[data-field="reserved"]')).toHaveText(money(bank.reservedMinor, state.currency));
    await expect(row.locator('[data-field="available"]')).toHaveText(money(bank.availableMinor, state.currency));
  }
  const totals = page.getByTestId("bank-totals");
  await expect(totals.locator('[data-field="posted"]')).toHaveText(money(state.totalPostedMinor, state.currency));
  await expect(totals.locator('[data-field="reserved"]')).toHaveText(money(state.totalReservedMinor, state.currency));
}

test.beforeAll(async () => {
  expect(TOKEN.length, "E2E_FIXTURE_TOKEN comes from playwright.revenant.config.ts; run with npm run test:revenant").toBeGreaterThan(15);
  const info = await (await fetch(`${BACKEND}/__e2e/info`)).json();
  expect(info.notAProductionApi).toBe(true);
});

test("this suite never stubs the network: no route interception exists in its source", () => {
  const source = readFileSync(fileURLToPath(import.meta.url), "utf8");
  const interception = new RegExp(["\\.(route|routeFromHAR|unroute|routeWebSocket)\\(", "\\.fulfill\\(", "setOffline\\("].join("|"));
  expect(source.match(interception)).toBeNull();
});

test("Friday Ring click path: start, scrub to 09:00, pick policy v8, fork; every value is the API's, the source is untouched", async ({ browser }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  const pageErrors: string[] = [];
  page.on("pageerror", (e) => pageErrors.push(e.message.slice(0, 300)));
  await openApp(page);

  // 1. Start: a real guest session, then a real recorded run (seed 42 is the UI's default text, not a displayed result).
  const { run, opening } = await startRun(page);
  expect(run.labelling).toMatchObject({ synthetic: true, projectionOnly: true, ledgerPosting: "none" });
  expect(run.sourcePolicyId).toBe("policy-v7");
  expect(run.historyStatus).toBe("Verified");
  const truth = page.getByTestId("truth-banner");
  await expect(truth).toHaveAttribute("data-synthetic", "true");
  await expect(truth).toHaveAttribute("data-projection-only", "true");
  await expect(truth).toHaveAttribute("data-ledger-posting", "none");
  await expect(truth).toContainText("SYNTHETIC DATA");
  await expect(page.getByTestId("ledger-posting")).toHaveText("NO LEDGER POSTING");
  await expect(page.getByTestId("run-meta")).toHaveAttribute("data-timeline-id", run.timelineId);

  // Six banks: names and opening balances are the restore-at-0 answer; money is conserved against the run's own opening balances.
  await expectBanksMatch(page, opening.state);
  const openingSum = run.banks.reduce((sum: number, b: any) => sum + b.openingMinor, 0);
  expect(opening.state.totalPostedMinor).toBe(openingSum);
  expect(opening.state.totalReservedMinor).toBe(0);
  expect(run.banks.map((b: any) => b.bankId)).toHaveLength(6);

  // Source events: one row per recorded intent, each showing the server's amount and synthetic label.
  const rows = page.locator('tr[data-testid^="event-"]');
  await expect(rows).toHaveCount(run.events.length);
  expect(String(run.events.length)).toBe(run.bounds.end);
  const firstRing = run.events.find((e: any) => e.syntheticLabel === "synthetic-fraud-mule-ring");
  await expect(page.getByTestId(`event-${firstRing.position}`)).toContainText(money(firstRing.amountMinor, firstRing.currency));
  await expect(page.getByTestId(`event-${firstRing.position}`)).toContainText("synthetic label: mule ring");
  await expect(page.getByTestId("event-1")).toHaveAttribute("data-status", "NotReached");

  // 2. Scrub (the shared scrubber) to the server-supplied 09:00 marker.
  const nineAm = run.markers.find((m: any) => m.label === "09:00 business time");
  expect(nineAm, "the server supplies a 09:00 marker").toBeTruthy();
  const forkPosition = Number(nineAm.position);
  const restoreAt = page.waitForResponse((r) => new URL(r.url()).pathname.endsWith("/restore") && new URL(r.url()).searchParams.get("position") === nineAm.position);
  await page.getByRole("button", { name: "Marker: 09:00 business time" }).click();
  const restored = await (await restoreAt).json();
  await expect(page.getByTestId("verified-position")).toHaveAttribute("data-position", nineAm.position);
  await expectBanksMatch(page, restored.state);
  expect(restored.state.totalPostedMinor).toBe(openingSum);
  // Causal effect of the applied payments: at least one bank moved and the screen shows exactly the moved values.
  expect(restored.state.banks.some((b: any, i: number) => b.postedMinor !== opening.state.banks[i].postedMinor)).toBe(true);
  for (const payment of restored.state.payments) {
    await expect(page.getByTestId(`event-${payment.position}`)).toHaveAttribute("data-status", payment.status);
  }
  expect(restored.state.payments).toHaveLength(forkPosition);
  await expect(page.getByTestId(`event-${forkPosition + 1}`)).toHaveAttribute("data-status", "NotReached");
  // The same state read independently from Node (same session) and from PostgreSQL's stored hash for that position.
  const independent = await apiGet(page, `/revenant/runs/${run.timelineId}/restore?position=${nineAm.position}`);
  expect(independent.status).toBe(200);
  expect(independent.body.state).toEqual(restored.state);
  const stored = await facts(run.timelineId, forkPosition);
  expect(restored.state.stateHash).toBe(stored.stateHashAt);

  // 3. Policy selector: v7 and v8 offered from the server's policy list; choose v8.
  const policyIds = run.policies.map((p: any) => p.policyId);
  expect(policyIds).toEqual(["policy-v7", "policy-v8"]);
  await expect(page.getByTestId("policy-selector").getByRole("radio")).toHaveCount(policyIds.length);
  await page.getByTestId("policy-option-policy-v8").getByRole("radio").check();
  await expect(page.getByTestId("fork-help")).toContainText("policy-v8");

  // 4. Fork: the browser sends the verified position and the chosen policy with an idempotency key.
  const sourceBefore = await facts(run.timelineId);
  expect(sourceBefore.children).toEqual([]);
  const forkRequest = page.waitForRequest((r) => r.method() === "POST" && new URL(r.url()).pathname === `/revenant/runs/${run.timelineId}/branches`);
  const comparisonResponse = page.waitForResponse((r) => new URL(r.url()).pathname.startsWith("/revenant/branches/") && new URL(r.url()).pathname.endsWith("/comparison"));
  await page.getByRole("button", { name: "Fork from here" }).click();
  const request = await forkRequest;
  const posted = request.postDataJSON() as { position: unknown; operationKey: string; options: { policyId: string } };
  expect(posted.position).toBe(nineAm.position);
  expect(posted.options).toEqual({ policyId: "policy-v8" });
  expect(request.headers()["idempotency-key"]).toBe(posted.operationKey);
  expect((request.headers()["x-csrf-token"] ?? "").length).toBeGreaterThan(20);
  const branched = await (await request.response())!.json();
  expect(branched).toMatchObject({ sourceId: run.timelineId, forkPosition: nineAm.position, policyId: "policy-v8", status: "Verified" });
  const comparison = await (await comparisonResponse).json();
  expect(comparison.branchId).toBe(branched.branchId);

  // 5. The comparison on screen is the API's comparison.
  const panel = page.getByTestId("comparison-panel");
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute("data-branch-id", branched.branchId);
  await expect(panel).toHaveAttribute("data-branch-status", "Verified");
  await expect(page.getByTestId("comparison-headline")).toContainText(`ORIGINAL · ${comparison.sourcePolicyId}`);
  await expect(page.getByTestId("comparison-headline")).toContainText(`ALTERNATE · ${comparison.branchPolicyId}`);
  expect(comparison.branchPolicyId).toBe("policy-v8");
  expect(comparison.forkPosition).toBe(nineAm.position);
  const first = comparison.aligned[0];
  const last = comparison.aligned[comparison.aligned.length - 1];
  expect(first.position).toBe(nineAm.position);
  expect(last.position).toBe(run.bounds.end);
  // Shared verified prefix at the fork; divergence after it.
  expect(first.original.stateHash).toBe(first.alternate.stateHash);
  expect(last.original.stateHash).not.toBe(last.alternate.stateHash);
  await expect(page.getByTestId("prefix-check")).toHaveAttribute("data-shared", "true");
  for (const bank of last.original.banks) {
    const alt = last.alternate.banks.find((b: any) => b.bankId === bank.bankId);
    const cells = page.getByTestId(`cmp-bank-${bank.bankId}`).locator("td");
    await expect(cells.nth(1)).toHaveText(money(bank.availableMinor));
    await expect(cells.nth(3)).toHaveText(money(alt.availableMinor));
  }
  // Computed causal difference: v8 holds payments v7 cleared, so reserved liquidity differs and total posted money is conserved.
  expect(last.original.totalReservedMinor).not.toBe(last.alternate.totalReservedMinor);
  expect(last.alternate.totalReservedMinor).toBeGreaterThan(last.original.totalReservedMinor);
  expect(last.original.totalPostedMinor).toBe(openingSum);
  expect(last.alternate.totalPostedMinor).toBe(openingSum);
  expect(comparison.differences.length).toBeGreaterThanOrEqual(1);
  expect(comparison.differences.length).toBe(last.metrics.decisionDifferenceCount);
  await expect(page.getByTestId("difference-count")).toHaveAttribute("data-count", String(comparison.differences.length));
  for (const d of comparison.differences) {
    await expect(page.getByTestId(`difference-${d.position}`)).toContainText(money(d.amountMinor, d.currency));
  }
  // The trajectory plots the API's numbers only: one table row per aligned position, with the API's values.
  for (const a of comparison.aligned) {
    const row = page.getByTestId("trajectory-table").locator(`tr[data-position="${a.position}"]`);
    await expect(row.locator("td").nth(1)).toHaveText(money(a.original.totalReservedMinor));
    await expect(row.locator("td").nth(2)).toHaveText(money(a.alternate.totalReservedMinor));
  }

  // 6. Truth labels: pending fraud is deferred, never counted as prevented (R05.11), and the metrics are the API's.
  const m = last.metrics;
  await expect(page.getByTestId("metrics")).toHaveAttribute("data-cost-model", m.costModel);
  await expect(page.getByTestId("metric-fraud-prevented").locator("b")).toHaveText(String(m.fraudPreventedCount));
  await expect(page.getByTestId("metric-fraud-deferred").locator("b")).toHaveText(String(m.fraudDeferredAtHorizonCount));
  await expect(page.getByTestId("metric-legit-delayed").locator("b")).toHaveText(String(m.legitimateDelayedCount));
  await expect(page.getByTestId("metric-false-positive-cost").locator("b")).toHaveText(money(m.falsePositiveCostMinor));
  await expect(page.getByTestId("metric-delay-cost").locator("b")).toHaveText(money(m.incrementalDelayCostMinor));
  await expect(page.getByTestId("metric-net").locator("b")).toHaveText(money(m.netEffectMinor));
  // Cost model v1 is stated in USD minor units (250 and 10 per minute), so these are cents on screen. Seed 42 forked at 09:00
  // (position 30) has these exact figures in an independent reference calculation, not copied from this implementation's output.
  expect({ seed: run.scenario.seed, fork: nineAm.position }).toEqual({ seed: 42, fork: "30" });
  {
    expect({ delayed: m.legitimateDelayedCount, fp: m.falsePositiveCostMinor, delay: m.incrementalDelayCostMinor, net: m.netEffectMinor, deferred: m.fraudDeferredAtHorizonCount, prevented: m.fraudPreventedCount })
      .toEqual({ delayed: 1, fp: 250, delay: 360, net: -610, deferred: 10, prevented: 0 });
    await expect(page.getByTestId("metric-false-positive-cost").locator("b")).toHaveText("USD 2.50");
    await expect(page.getByTestId("metric-delay-cost").locator("b")).toHaveText("USD 3.60");
    await expect(page.getByTestId("metric-net").locator("b")).toHaveText("-USD 6.10");
  }
  expect(m.fraudDeferredAtHorizonCount).toBeGreaterThanOrEqual(1);
  expect(last.alternate.counters.fraudPendingHold).toBeGreaterThanOrEqual(m.fraudDeferredAtHorizonCount);
  expect(m.fraudPreventedCount).toBe(last.alternate.counters.fraudTerminallyStopped - last.original.counters.fraudTerminallyStopped);
  await expect(page.getByTestId("metric-fraud-deferred")).toContainText("deferred, not prevented");
  await expect(page.getByTestId("metrics-caveat")).toContainText("not real-world fraud findings");
  await expect(page.getByTestId("metrics-caveat")).toContainText("A held payment is pending, not prevented");
  await expect(page.getByText(/ledger (entries )?posted|posted to the ledger/i)).toHaveCount(0);
  // The synthetic / projection-only banner is still on screen next to the comparison, not only at the start.
  await expect(page.getByTestId("truth-banner")).toBeVisible();
  await expect(page.getByTestId("truth-banner")).toContainText("SYNTHETIC DATA");
  await expect(page.getByTestId("ledger-posting")).toHaveText("NO LEDGER POSTING");

  // 7. PostgreSQL: one child at the fork position with the browser's key; the source rows did not move; hashes are the stored ones.
  const sourceAfter = await facts(run.timelineId);
  expect(sourceAfter.history).toEqual(sourceBefore.history);
  expect(sourceAfter.children).toHaveLength(1);
  expect(sourceAfter.children[0]).toMatchObject({ id: branched.branchId, operationKey: posted.operationKey, forkPosition: nineAm.position, lastPosition: run.bounds.end, status: "Verified" });
  expect(sourceAfter.history.ownStepRows).toBe(Number(run.bounds.end));
  expect(comparison.sourceCheck).toEqual({ hashWhenRecorded: run.sourceFinalStateHash, hashNow: run.sourceFinalStateHash, unchanged: true });
  await expect(page.getByTestId("source-check")).toHaveAttribute("data-unchanged", "true");
  expect((await facts(run.timelineId, run.bounds.end)).stateHashAt).toBe(comparison.sourceFinalStateHash);
  expect((await facts(branched.branchId, run.bounds.end)).stateHashAt).toBe(comparison.branchFinalStateHash);
  expect((await facts(branched.branchId, run.bounds.end)).stateHashAt).toBe(last.alternate.stateHash);
  // The branch shares the source prefix by reference: it owns steps only after the fork.
  expect((await facts(branched.branchId)).history.ownStepRows).toBe(Number(run.bounds.end) - forkPosition);
  expect(pageErrors).toEqual([]);
  await context.close();
});

test("a different seed changes what the screen shows, and what it shows is again exactly the API's answer", async ({ browser }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  await openApp(page);
  const a = await startRun(page, "42");
  await expectBanksMatch(page, a.opening.state);
  const b = await startRun(page, "7");
  expect(b.run.scenario.seed).toBe(7);
  expect(b.run.timelineId).not.toBe(a.run.timelineId);
  expect(b.run.runId).not.toBe(a.run.runId);
  expect(b.run.scenario.factsHash).not.toBe(a.run.scenario.factsHash);
  await expectBanksMatch(page, b.opening.state);
  await expect(page.getByTestId("run-meta")).toHaveAttribute("data-timeline-id", b.run.timelineId);
  const changed = b.run.events.filter((e: any, i: number) => e.amountMinor !== a.run.events[i].amountMinor || e.sourceAccountId !== a.run.events[i].sourceAccountId);
  expect(changed.length, "the seed changes the recorded intents").toBeGreaterThan(0);
  for (const e of changed.slice(0, 5)) {
    await expect(page.getByTestId(`event-${e.position}`)).toContainText(money(e.amountMinor, e.currency));
  }
  // The earlier run is untouched by the later one.
  const earlier = await apiGet(page, `/revenant/runs/${a.run.timelineId}`);
  expect(earlier.body.sourceFinalStateHash).toBe(a.run.sourceFinalStateHash);
  await context.close();
});

test("only the owner can read or branch a run; a stranger and a missing session get typed denials, and nothing is created", async ({ browser }) => {
  const owner = await browser.newContext();
  const ownerPage = await owner.newPage();
  await openApp(ownerPage);
  const { run } = await startRun(ownerPage);

  const strangerContext = await browser.newContext();
  const stranger = await strangerContext.newPage();
  await openApp(stranger);
  expect(await stranger.evaluate(async () => (await fetch("/auth/guest/start", { method: "POST", credentials: "include" })).status)).toBe(200);
  const probe = (page: Page, path: string, init?: RequestInit) =>
    page.evaluate(async ([p, i]) => {
      const csrf = document.cookie.split("; ").find((c) => c.startsWith("__Host-sands_csrf_guest="))?.split("=").slice(1).join("=") ?? "";
      const r = await fetch(p as string, { credentials: "include", ...(i as RequestInit), headers: { "X-CSRF-Token": csrf, ...((i as RequestInit | undefined)?.headers ?? {}) } });
      return r.status;
    }, [path, init] as const);
  expect(await probe(stranger, `/revenant/runs/${run.timelineId}`)).toBe(403);
  expect(await probe(stranger, `/revenant/runs/${run.timelineId}/restore?position=1`)).toBe(403);
  const key = "stranger-branch-attempt";
  expect(
    await probe(stranger, `/revenant/runs/${run.timelineId}/branches`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "Idempotency-Key": key },
      body: JSON.stringify({ position: "5", operationKey: key, options: { policyId: "policy-v8" } }),
    }),
  ).toBe(403);
  const anonymousContext = await browser.newContext();
  const anonymous = await anonymousContext.newPage();
  await openApp(anonymous);
  expect(await probe(anonymous, `/revenant/runs/${run.timelineId}/restore?position=1`)).toBe(401);
  expect((await facts(run.timelineId)).children).toEqual([]);
  // Owner side input validation: unknown policy and a non-canonical position are typed 400s and create nothing.
  expect(await probe(ownerPage, `/revenant/runs/${run.timelineId}/restore?position=01`)).toBe(400);
  expect(
    await probe(ownerPage, `/revenant/runs/${run.timelineId}/branches`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "Idempotency-Key": "bad-policy" },
      body: JSON.stringify({ position: "5", operationKey: "bad-policy", options: { policyId: "policy-v9" } }),
    }),
  ).toBe(400);
  expect((await facts(run.timelineId)).children).toEqual([]);
  await Promise.all([owner.close(), strangerContext.close(), anonymousContext.close()]);
});
