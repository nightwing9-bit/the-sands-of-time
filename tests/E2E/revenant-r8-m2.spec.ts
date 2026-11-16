import { expect, test, type Page } from "@playwright/test";
import { audit, cases, fork, jobId, liquidity, replay, run, runId, snapshot, staffLogin, staffProfile, timelinePreview } from "./revenant-r8-m2.fixtures.ts";

// STUBBED Milestone 2 checks for the light-teal R8 React views. Every API response below is a page.route fixture: these tests
// prove the browser views, their requests and their layout, not the backend journey (that is revenant-r8.spec.ts).
// Set R8_SHOTS=<dir> to also write reference screenshots of all nine states at both laptop sizes.

async function stub(page: Page, opts: { status?: "Active" | "Verified"; job?: "Pending" | "Verified" | "Failed" | null; denseSeals?: boolean } = {}) {
  const state = { status: opts.status ?? "Verified", job: opts.job ?? null, reviews: [] as unknown[] };
  const denseSeals = ["288", "302", "303", "304"].map((position, i) => ({
    kind: "ObservationSeal", position, atMs: Date.parse("2026-09-13T08:55:00Z") + i * 300_000, intentId: null,
  }));
  await page.route("**/auth/staff/login", r => r.fulfill({ json: staffLogin }));
  await page.route("**/auth/staff/me", r => r.fulfill({ json: staffProfile }));
  await page.route("**/revenant/r8/runs", r => r.fulfill({ json: [run] }));
  await page.route(`**/revenant/r8/runs/${runId}`, r => {
    const result = snapshot(state.status);
    if (opts.denseSeals) {
      result.markers = [...result.markers.filter(marker => Number(marker.position) < 288), ...denseSeals];
      result.lastPosition = denseSeals.at(-1)!.position;
      result.boundaryMs = denseSeals.at(-1)!.atMs;
    }
    return r.fulfill({ json: result });
  });
  await page.route("**/liquidity", r => r.fulfill({ json: liquidity }));
  await page.route("**/audit", r => r.fulfill({ json: audit }));
  await page.route("**/timeline/*", r => {
    const preview = timelinePreview(r.request().url().split("/").at(-1)!);
    if (opts.denseSeals) preview.boundaryMs = denseSeals.find(seal => seal.position === preview.position)?.atMs ?? preview.boundaryMs;
    return r.fulfill({ json: preview });
  });
  await page.route("**/counterfactual", r => { state.job = "Pending"; return r.fulfill({ json: replay(state.job) }); });
  await page.route(`**/counterfactual/${jobId}`, r => r.fulfill({ json: replay(state.job) }));
  return state;
}
async function open(page: Page, screen: string, query = "") {
  await page.goto(`http://localhost:5175/?view=r8&run=${runId}${query}#${screen}`);
  await page.getByLabel("Username").fill("supervisor");
  await page.getByLabel("Password").fill("fixture");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("navigation", { name: "Revenant screens" })).toBeVisible();
}
async function fits(page: Page) {
  const box = await page.evaluate(() => ({ w: document.documentElement.scrollWidth, h: document.documentElement.scrollHeight, vw: innerWidth, vh: innerHeight }));
  expect(box.w, "no horizontal document overflow").toBeLessThanOrEqual(box.vw);
  expect(box.h, "no vertical document overflow").toBeLessThanOrEqual(box.vh);
}
test.use({ viewport: { width: 1440, height: 900 } });
const shots = process.env.R8_SHOTS;
async function shot(page: Page, name: string) {
  if (!shots) return;
  await page.waitForTimeout(400); // let the 0.2 s dialog/scrim entrance settle
  await page.screenshot({ path: `${shots}/${page.viewportSize()!.width}-${name}.png` });
}

test("sign-in exposes only the real staff form before authentication", async ({ page }) => {
  await stub(page);
  await page.goto(`http://localhost:5175/?view=r8&run=${runId}#tower`);
  await expect(page.getByRole("navigation", { name: "Revenant screens" })).toHaveCount(0);
  await expect(page.getByRole("link")).toHaveCount(0);
  for (const label of ["Username", "Password", "MFA code"]) await expect(page.getByLabel(label)).toBeVisible();
  await expect(page.getByText(/SSO|work email/i)).toHaveCount(0);
  await fits(page);
});

test("the approval dialog explains a first of two approvals, traps focus and sends the versioned idempotent request", async ({ page }) => {
  await stub(page, { status: "Active" });
  const bodies: { body: unknown; key: string | null }[] = [];
  let fail = true;
  await page.route("**/cases/P-001", r => {
    bodies.push({ body: r.request().postDataJSON(), key: r.request().headers()["idempotency-key"] ?? null });
    if (fail) { fail = false; return r.fulfill({ status: 403, json: { code: "mfa_required", status: 403, title: "MFA required" } }); }
    return r.fulfill({ json: { ...cases[0], status: "AwaitingSecondApproval", approvalCount: 1, version: 2 } });
  });
  await open(page, "held");
  await expect(page.getByText("Recorded score:", { exact: false })).toBeVisible();
  const approve = page.getByRole("button", { name: "Approve", exact: true });
  await approve.click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText("Record approval 1 of 2");
  await expect(dialog).toContainText("payment stays held");
  await expect(dialog).not.toContainText("attempts settlement");
  await expect(dialog.getByRole("textbox")).toHaveCount(0); // no rejection note: the API cannot persist one
  await expect(page.getByRole("button", { name: "Cancel" })).toBeFocused();
  await page.keyboard.press("Shift+Tab");
  expect(await dialog.evaluate(d => d.contains(document.activeElement))).toBeTruthy();
  await page.keyboard.press("Escape");
  await expect(dialog).toHaveCount(0);
  await expect(approve).toBeFocused();
  await approve.click();
  await page.getByRole("button", { name: /Record approval 1 of 2/ }).click();
  await expect(dialog.getByRole("alert")).toContainText("fresh MFA code");
  await page.getByRole("button", { name: /Retry · Record approval 1 of 2/ }).click();
  await expect(dialog).toHaveCount(0);
  expect(bodies.map(b => b.body)).toEqual([{ action: "Approve", expectedVersion: 1 }, { action: "Approve", expectedVersion: 1 }]);
  expect(bodies[0]!.key).toBeTruthy();
  expect(bodies[1]!.key).toBe(bodies[0]!.key);
  await shot(page, "02-held-queue");
});

test("final approval and rejection describe their real consequences", async ({ page }) => {
  await stub(page, { status: "Active" });
  await open(page, "held");
  await page.getByRole("button", { name: /Select case P-003/ }).click();
  await page.getByRole("button", { name: "Approve", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("Release $1,500.00 for settlement?");
  await expect(page.getByRole("dialog")).toContainText("attempts settlement");
  await expect(page.getByRole("dialog")).not.toContainText("stays held");
  await page.getByRole("button", { name: "Close" }).click();
  await page.getByRole("button", { name: "Reject", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("terminal");
  await expect(page.getByRole("button", { name: /Reject payment/ })).toBeEnabled();
});

test("rewind moves from original to fork to verified comparison without early KPIs", async ({ page }) => {
  const state = await stub(page);
  await open(page, "rewind");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("without changing a single entry");
  await expect(page.getByRole("img", { name: /source history available balance/ })).toBeVisible();
  await shot(page, "05-rewind-original");
  await page.getByRole("button", { name: /Marker: .*completed seal/ }).first().click();
  await expect(page.getByTestId("r8-preview")).toContainText("Verified event");
  const markerTime = await page.getByTestId("r8-preview").innerText();
  expect(markerTime).toMatch(/Verified event \d+/);
  await expect(page.getByRole("heading", { level: 1 })).toContainText("A decision point at");
  await expect(page.getByText("FORK FIXED · AWAITING REPLAY")).toBeVisible();
  await expect(page.getByTestId("r8-kpi")).toHaveCount(0);
  await shot(page, "06-rewind-fork");
  const submitted = page.waitForRequest(r => r.method() === "POST" && r.url().endsWith("/counterfactual"));
  await page.getByRole("button", { name: "Run sandbox replay" }).click();
  const body = (await submitted).postDataJSON();
  expect(body.policy).toBe("policy-v8");
  expect(body.interventions).toBeNull();
  await expect(page.getByTestId("r8-job-status")).toContainText("Replay Pending");
  await expect(page.getByRole("button", { name: "Cancel replay" })).toBeVisible();
  await expect(page.getByTestId("r8-kpi")).toHaveCount(0);
  state.job = "Verified";
  await expect(page.getByTestId("r8-job-status")).toContainText("Replay Verified");
  await expect(page.getByTestId("r8-kpi")).toHaveCount(4);
  expect(await page.getByTestId("r8-kpi").evaluateAll(n => n.map(x => x.getAttribute("data-delta")))).toEqual(["150000", "1", "250", "149600"]);
  await expect(page.getByTestId("r8-kpi").first()).toContainText("+$1,500.00");
  await expect(page.getByRole("img", { name: "BANK-A source and completed alternative balances" })).toBeVisible();
  await expect(page.getByText(/CHILD 77777777/)).toBeVisible();
  await expect(page.locator(".r8-wedge")).toHaveCount(1);
  await expect(page.getByText("✓ ORIGINAL ARCHIVE UNCHANGED")).toBeVisible();
  await shot(page, "07-rewind-comparison");
  await page.getByRole("navigation", { name: "Revenant screens" }).getByRole("link", { name: /Audit$/ }).click();
  await expect(page.getByText("Original history unchanged")).toBeVisible();
});

test("a failed replay shows no comparison metrics", async ({ page }) => {
  await stub(page, { job: "Failed" });
  await open(page, "rewind", `&job=${jobId}`);
  await expect(page.getByTestId("r8-job-status")).toContainText("Replay Failed");
  await expect(page.getByTestId("r8-kpi")).toHaveCount(0);
  await expect(page.getByText(/CHILD 77777777/)).toHaveCount(0);
});

for (const viewport of [{ width: 1440, height: 900 }, { width: 1366, height: 768 }]) {
  test(`adjacent Rewind seals accept independent mouse clicks at ${viewport.width}×${viewport.height}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await stub(page, { denseSeals: true });
    await open(page, "rewind");
    const nine = page.getByRole("button", { name: "Marker: 09:00 UTC completed seal" });
    const horizon = page.getByRole("button", { name: "Marker: 09:10 UTC completed seal" });
    const markers = page.getByRole("button", { name: /Marker: .*completed seal/ });
    await expect(nine).toBeVisible();
    await expect(horizon).toBeVisible();
    const bounds = [];
    for (const marker of await markers.all()) {
      bounds.push((await marker.boundingBox())!);
      expect(await marker.evaluate(button => {
        const box = button.getBoundingClientRect();
        const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
        return hit === button;
      }), await marker.getAttribute("aria-label")).toBe(true);
    }
    bounds.sort((a, b) => a.x - b.x);
    for (const [i, box] of bounds.slice(1).entries()) {
      expect(box.x).toBeGreaterThanOrEqual(bounds[i]!.x + bounds[i]!.width);
    }
    const select = async (marker: typeof nine, position: string) => {
      const [response] = await Promise.all([
        page.waitForResponse(r => r.request().method() === "GET" && r.url().endsWith(`/timeline/${position}`)),
        marker.click(),
      ]);
      expect(await response.json()).toMatchObject({ position, status: "Verified" });
      await expect(page.getByTestId("r8-preview")).toContainText(`Verified event ${position} ·`);
      await expect(page.getByRole("slider", { name: "Timeline position" })).toHaveAttribute("data-position", position);
    };
    const replayButton = page.getByRole("button", { name: "Run sandbox replay" });
    await select(nine, "302");
    await expect(page.getByText("Fork 09:00:00", { exact: true })).toBeVisible();
    await expect(page.locator(".r8-dock-ticks .is-active")).toHaveText("09:00");
    await expect(replayButton).toBeEnabled();
    await select(horizon, "304");
    await expect(page.locator(".r8-dock-ticks .is-active")).toHaveText("09:10");
    await expect(page.getByText("No fork set", { exact: true })).toBeVisible();
    await expect(replayButton).toBeDisabled();
    await select(nine, "302");
    await expect(page.getByText("Fork 09:00:00", { exact: true })).toBeVisible();
    await expect(replayButton).toBeEnabled();
    await expect(page.locator(".r8-dock")).toBeInViewport({ ratio: 1 });
    await fits(page);
    if (shots) await page.locator(".r8-dock").screenshot({ path: `${shots}/${viewport.width}-dense-dock.png` });
    const replayRequest = page.waitForRequest(r => r.method() === "POST" && r.url().endsWith("/counterfactual"));
    await replayButton.click();
    expect((await replayRequest).postDataJSON()).toMatchObject({ forkPosition: "302", horizonMs: Date.parse("2026-09-13T09:10:00Z") });
  });

  test(`all workspaces fit ${viewport.width}×${viewport.height} without document scrolling`, async ({ page }) => {
    await page.setViewportSize(viewport);
    const state = await stub(page, { status: "Active", job: "Verified" });
    await page.goto(`http://localhost:5175/?view=r8&run=${runId}#tower`);
    await expect(page.getByLabel("Username")).toBeVisible();
    await shot(page, "09-sign-in");
    await fits(page);
    await page.getByLabel("Username").fill("supervisor");
    await page.getByLabel("Password").fill("fixture");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("img", { name: "Committed payment flows among the six banks" })).toBeVisible();
    await shot(page, "01-control-tower");
    await fits(page);
    const nav = page.getByRole("navigation", { name: "Revenant screens" });
    for (const [name, key] of [["Held Queue", "02-held-queue"], ["Liquidity Fan", "04-liquidity-fan"], ["Audit", "08-audit"]] as const) {
      await nav.getByRole("link", { name: new RegExp(name) }).click();
      await expect(page.getByRole("region", { name, exact: true })).toBeVisible();
      if (name === "Liquidity Fan") await expect(page.getByRole("img", { name: /projected available liquidity/ })).toBeVisible();
      if (name === "Audit") await expect(page.getByRole("cell", { name: "01842" })).toBeVisible();
      await shot(page, key);
      await fits(page);
      for (const chart of await page.locator(".r8-chart-box svg").all()) expect(Number(await chart.getAttribute("height"))).toBeGreaterThan(80);
    }
    await nav.getByRole("link", { name: /Held Queue/ }).click();
    await page.getByRole("button", { name: "Approve", exact: true }).click();
    const dialog = page.getByRole("dialog");
    const box = (await dialog.boundingBox())!;
    expect(box.y).toBeGreaterThanOrEqual(0);
    expect(box.y + box.height).toBeLessThanOrEqual(viewport.height);
    await expect(page.getByRole("button", { name: /Record approval 1 of 2/ })).toBeInViewport();
    await shot(page, "03-payment-approval");
    await fits(page);
    await page.keyboard.press("Escape");
    state.status = "Verified";
    await page.goto(`http://localhost:5175/?view=r8&run=${runId}&job=${jobId}&fork=${fork.position}#rewind`);
    await page.getByLabel("Username").fill("supervisor");
    await page.getByLabel("Password").fill("fixture");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByTestId("r8-kpi")).toHaveCount(4);
    await expect(page.getByRole("button", { name: /Marker: .*completed seal/ }).first()).toBeInViewport();
    expect(Number(await page.locator(".r8-chart-box svg").first().getAttribute("height"))).toBeGreaterThan(160);
    await shot(page, "07-rewind-comparison");
    await fits(page);
  });
}
