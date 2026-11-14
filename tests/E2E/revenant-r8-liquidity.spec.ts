import { expect, test } from "@playwright/test";

const origin = 1_780_000_000_000;
const runId = "11111111-2222-3333-4444-555555555555";
const balance = (bankId: string, postedMinor: string, reservedMinor: string, availableMinor: string) =>
  ({ bankId, postedMinor, reservedMinor, availableMinor });
const opening = [
  balance("BANK-A", "120009", "20000", "100009"),
  balance("BANK-B", "300001", "0", "300001"),
  balance("BANK-D", "0", "0", "0"),
  balance("BANK-E", "100000", "1", "100000"),
  balance("BANK-F", "9223372036854775808", "0", "9223372036854775808"),
];
const position = "9007199254740993";

test.beforeEach(async ({ page }) => {
  await page.route("**/auth/staff/login", route => route.fulfill({ json: { accessToken: "fixture-token" } }));
  await page.route("**/revenant/r8/runs", route => route.fulfill({ json: [] }));
  await page.route(`**/revenant/r8/runs/${runId}`, route => route.fulfill({ json: {
    runId, timelineId: runId, status: "Verified", boundaryMs: origin + 300_000,
    opening, balances: [], payments: [], cases: [], anomalies: [], lastPosition: "9007199254740994",
    markers: [{ kind: "ObservationSeal", position, atMs: origin, intentId: null }],
  } }));
});

async function login(page: import("@playwright/test").Page, screen: string, job?: string) {
  await page.goto(`http://localhost:5175/?view=r8&run=${runId}${job ? "&job=" + job : ""}#${screen}`);
  await page.getByLabel("Username").fill("operator");
  await page.getByLabel("Password").fill("fixture");
  await page.getByRole("button", { name: "Sign in" }).click();
}

test("the selected bank uses its own opening buffer without changing the chart scale", async ({ page }) => {
  await page.route("**/liquidity", route => route.fulfill({ json: {
    method: "LiquidityPersistenceBaseline/1", uncertainty: "Uncalibrated", anomalyStatus: "InsufficientHistory",
    actual: [
      { atMs: origin, balance: balance("BANK-A", "15000", "8000", "7000") },
      { atMs: origin, balance: balance("BANK-B", "90000", "45000", "45000") },
    ],
    projection: Array.from({ length: 8 }, (_, i) => ({
      bankId: "BANK-A", atMs: origin + (i + 1) * 300_000, availableMinor: "7000",
    })),
  } }));
  await login(page, "liquidity");
  const bank = page.getByRole("combobox", { name: "Bank" });
  const note = page.locator("[data-reference-status]");
  const line = page.getByTestId("r8-buffer-line");
  await expect(note).toContainText("Illustrative operating buffer — 10% of opening available balance");
  await expect(note).toContainText("$100.00");
  await expect(line).toBeVisible();
  await expect(line.locator("line")).toHaveAttribute("y1", "137");
  await expect(page.getByText("LiquidityPersistenceBaseline/1 · statistical baseline, Uncalibrated", { exact: false })).toBeVisible();
  await page.getByText("Liquidity data table").click();
  await expect(page.getByText("Operating buffer reference: $100.00")).toBeVisible();
  const projection = page.getByRole("table").filter({ hasText: "Available baseline" });
  await expect(projection.getByRole("cell", { name: "$70.00", exact: true })).toHaveCount(8);

  await bank.selectOption("BANK-B");
  await expect(note).toContainText("$300.00 · below visible chart range");
  await expect(line).toHaveCount(0);
  await expect(page.getByText("Operating buffer reference: $300.00")).toBeVisible();
  for (const unavailable of ["BANK-C", "BANK-D", "BANK-E", "BANK-F"]) {
    await bank.selectOption(unavailable);
    await expect(note).toHaveAttribute("data-reference-status", "ReferenceUnavailable");
    await expect(page.getByText("Operating buffer reference: Unavailable")).toBeVisible();
    await expect(line).toHaveCount(0);
  }
  await bank.selectOption("BANK-A");
  await expect(line).toBeVisible();
  await expect(note).toContainText("$100.00");
});

test("the shared scrubber seeks and submits an exact decimal-string position", async ({ page }) => {
  await page.route(`**/timeline/${position}`, route => route.fulfill({ json: {
    position, boundaryMs: origin, stateHash: "fixture-state", status: "Verified", balances: [],
  } }));
  await page.route("**/counterfactual", route => {
    expect(route.request().postDataJSON().forkPosition).toBe(position);
    return route.fulfill({ json: { jobId: runId, status: "Pending", failure: null, verified: null } });
  });
  await page.route(`**/counterfactual/${runId}`, route => route.fulfill({ json: {
    jobId: runId, status: "Pending", failure: null, verified: null,
  } }));
  await login(page, "rewind");
  await page.getByRole("button", { name: /Marker:.*completed seal/ }).click();
  await expect(page.getByTestId("r8-preview")).toContainText(`Verified event ${position}`);
  await page.getByRole("button", { name: "Run sandbox replay" }).click();
  await expect(page.getByTestId("r8-job-status")).toContainText("Replay Pending");
});

test("source and alternative use the same opening buffer", async ({ page }) => {
  await page.route(`**/counterfactual/${runId}`, route => route.fulfill({ json: {
    jobId: runId, status: "Verified", failure: null,
    verified: {
      sourcePolicy: "policy-v7", alternativePolicy: "policy-v8", mode: "PolicyOnly",
      costPolicy: "counterfactual-cost-v1", childTimelineId: runId, headlines: [], breakdown: [], payments: [],
      balances: [{ atMs: origin, bankId: "BANK-A", source: balance("BANK-A", "15000", "3000", "12000"),
        alternative: balance("BANK-A", "15000", "7000", "8000") }],
    },
  } }));
  await page.goto(`http://localhost:5175/?view=r8&run=${runId}&job=${runId}#rewind`);
  await page.getByLabel("Username").fill("operator");
  await page.getByLabel("Password").fill("fixture");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByTestId("r8-buffer-line")).toHaveCount(1);
  await page.getByText("Aligned balance data").click();
  await expect(page.getByText("Shared operating buffer reference: $100.00")).toBeVisible();
  await expect(page.getByRole("cell", { name: "$120.00", exact: true })).toBeVisible();
  await expect(page.getByRole("cell", { name: "$80.00", exact: true })).toBeVisible();
});

test.describe("tablet with keyboard and touch", () => {
  test.use({ viewport: { width: 820, height: 1180 }, hasTouch: true });

  test("all screens open, exact seeks work and only verified jobs show KPI data", async ({ page }) => {
    const headlines = [
      ["Fraud prevented", "150000", "USD minor units"],
      ["Legitimate payments delayed", "1", "count"],
      ["False-positive intervention cost", "250", "USD minor units"],
      ["Net financial position", "149600", "USD minor units"],
    ].map(([label, delta, unit]) => ({ label, delta, unit, source: "0", alternative: delta }));
    const verified = {
      sourcePolicy: "policy-v7", alternativePolicy: "policy-v8", mode: "PolicyAndSandboxIntervention",
      costPolicy: "counterfactual-cost-v1", childTimelineId: runId, headlines, breakdown: headlines, payments: [],
      balances: [{ atMs: origin, bankId: "BANK-A", source: balance("BANK-A", "15000", "3000", "12000"),
        alternative: balance("BANK-A", "15000", "7000", "8000") }],
    };
    let status = "Pending";
    await page.route(`**/timeline/${position}`, route => route.fulfill({ json: {
      position, boundaryMs: origin, stateHash: "fixture-state", status: "Verified", balances: [],
    } }));
    await page.route("**/liquidity", route => route.fulfill({ json: {
      method: "LiquidityPersistenceBaseline/1", uncertainty: "Uncalibrated", anomalyStatus: "InsufficientHistory",
      actual: [{ atMs: origin, balance: opening[0] }], projection: [],
    } }));
    await page.route("**/audit", route => route.fulfill({ json: {
      timelineId: runId, status: "Verified", ledgerVerification: "Verified", stateHash: "fixture-state",
      recordHash: "fixture-record", manifestHash: "fixture-manifest", policy: "policy-v7",
      model: "fixture-model", ledgerHeadHash: "fixture-ledger", recentEvents: [],
    } }));
    await page.route("**/counterfactual", route => {
      expect(route.request().postDataJSON().forkPosition).toBe(position);
      return route.fulfill({ json: { jobId: runId, status, failure: null, verified: null } });
    });
    await page.route(`**/counterfactual/${runId}`, route => route.fulfill({ json: {
      jobId: runId, status, failure: status === "Failed" ? "fixture-failure" : null, verified,
    } }));
    await login(page, "tower");
    const nav = page.getByRole("navigation", { name: "Revenant screens" });
    for (const name of ["Control Tower", "Held Queue", "Liquidity Fan", "Audit", "Rewind Mode"]) {
      const link = nav.getByRole("link", { name: new RegExp(name) });
      await link.tap();
      await expect(link).toHaveAttribute("aria-current", "page");
      await expect(page.getByRole("region", { name, exact: true })).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(820);
    }
    const marker = page.getByRole("button", { name: /Marker:.*completed seal/ });
    await marker.focus();
    await marker.press("Enter");
    await expect(page.getByTestId("r8-preview")).toContainText(`Verified event ${position}`);
    const touchSeek = page.waitForRequest(r => r.url().endsWith(`/timeline/${position}`));
    await marker.tap();
    await touchSeek;
    await expect(page.getByTestId("r8-preview")).toContainText(`Verified event ${position}`);
    await page.getByRole("button", { name: "Run sandbox replay" }).tap();
    await expect(page.getByTestId("r8-job-status")).toContainText("Replay Pending");
    await expect(page.getByTestId("r8-kpi")).toHaveCount(0);
    status = "Failed";
    await expect(page.getByTestId("r8-job-status")).toContainText("Replay Failed");
    await expect(page.getByTestId("r8-kpi")).toHaveCount(0);
    status = "Verified";
    await page.reload();
    await login(page, "rewind", runId);
    await expect(page.getByTestId("r8-kpi")).toHaveCount(4);
    await expect(page.getByTestId("r8-kpi").filter({ hasText: "Net financial position" })).toContainText("+$1,496.00");
    await expect(page.getByRole("img", { name: "BANK-A source and completed alternative balances" })).toBeVisible();
    await page.getByText("Aligned balance data").tap();
    await expect(page.getByRole("cell", { name: "$120.00", exact: true })).toBeVisible();
    await expect(page.getByRole("cell", { name: "$80.00", exact: true })).toBeVisible();
  });
});
