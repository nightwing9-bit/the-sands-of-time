import { createHash, createHmac } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { expect, test, type Page } from "@playwright/test";

const WINDOW_MS = 30_000;
function totp(secret: string, window: number) {
  let buffer = 0, bits = 0;
  const bytes: number[] = [];
  for (const char of secret.toUpperCase().replace(/[^A-Z2-7]/g, "")) {
    buffer = (buffer << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".indexOf(char);
    bits += 5;
    if (bits >= 8) { bytes.push((buffer >>> (bits - 8)) & 255); bits -= 8; }
  }
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(window));
  const hash = createHmac("sha1", Buffer.from(bytes)).update(counter).digest();
  return String((hash.readUInt32BE(hash.at(-1)! & 15) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

async function browserLogin(page: Page, username: string, code?: string) {
  await page.goto("/?view=r8");
  await page.getByLabel("Username").fill(username);
  await page.getByLabel("Password").fill(process.env.E2E_R8_PASSWORD!);
  if (code) await page.getByLabel("MFA code").fill(code);
  const login = page.waitForResponse(r => r.url().endsWith("/auth/staff/login") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Sign in" }).click();
  const response = await login;
  expect(response.status(), "staff browser login").toBe(200);
  const { accessToken } = await response.json();
  return { Authorization: `Bearer ${accessToken}` };
}

async function enrollMfa(page: Page, auth: { Authorization: string }) {
  const enrollment = await page.request.post("/auth/staff/mfa/enroll", {
    headers: auth, data: { currentPassword: process.env.E2E_R8_PASSWORD },
  });
  expect(enrollment.status(), "staff MFA enrollment").toBe(200);
  const { sharedKey } = await enrollment.json() as { sharedKey: string };
  const window = Math.floor(Date.now() / WINDOW_MS);
  const code = totp(sharedKey, window);
  const confirm = await page.request.post("/auth/staff/mfa/confirm", { headers: auth, data: { code } });
  expect(confirm.status(), "staff MFA confirmation").toBe(200);
  return { sharedKey, window, code };
}

async function freshTotp({ sharedKey, window, code }: Awaited<ReturnType<typeof enrollMfa>>) {
  const deadline = Date.now() + 2 * WINDOW_MS + 1_000;
  while (Date.now() < deadline) {
    const now = Date.now();
    const current = Math.floor(now / WINDOW_MS);
    const candidate = totp(sharedKey, current);
    if (current > window && candidate !== code) return candidate;
    await new Promise(resolve => setTimeout(resolve, (current + 1) * WINDOW_MS - now + 100));
  }
  throw new Error("No unused staff TOTP code became available within two windows.");
}

test("staff reviews and verifies a replay through all five real Revenant screens on a laptop", async ({page}) => {
  const auth = await browserLogin(page, "r8.browser.operator");
  await enrollMfa(page, auth);

  page.setDefaultTimeout(10_000);
  const nav = page.getByRole("navigation",{name:"Revenant screens"});
  const launch = page.waitForResponse(r => r.request().method()==="POST"&&r.url().endsWith("/revenant/r8/runs"));
  await page.getByRole("button",{name:"Launch fresh scenario"}).click();
  const launchResponse = await launch;
  expect(launchResponse.status(), await launchResponse.text()).toBe(200);
  await expect(page.getByRole("img",{name:"Committed payment flows among the six banks"})).toBeVisible({timeout:40_000});
  await expect(page.getByText("Current balances")).toBeVisible();
  const { runId, seed } = await launchResponse.json();
  await expect(page.getByLabel("Scenario",{exact:true})).toHaveValue(runId);
  const readRun = await page.request.get(`/revenant/r8/runs/${runId}`, {headers:auth});
  expect(readRun.ok()).toBeTruthy();
  const before = await readRun.json();
  expectReconciled(before.opening);
  expectReconciled(before.balances);
  const [held] = before.cases;
  expect(held.status).toBe("Open");
  const paymentBefore = before.payments.find((payment: Pay) => payment.intentId === held.intentId);
  expect(paymentBefore.status).toBe("Held");

  await nav.getByText("Held Queue").click();
  await page.getByRole("button", { name: new RegExp(`Select case ${held.intentId},`) }).click();
  await page.getByRole("button", { name: "Approve", exact: true }).click();
  const approved = page.waitForResponse(r => r.request().method() === "POST" && r.url().endsWith(`/cases/${held.intentId}`));
  await page.getByRole("dialog").getByRole("button", { name: "Approve and release" }).click();
  const released = await approved;
  expect(released.status()).toBe(200);
  const reviewAfter: Cs = await released.json();
  expect(reviewAfter).toMatchObject({ status: "Released", version: held.version + 1, approvalCount: held.approvalCount + 1 });
  const readAfter = await page.request.get(`/revenant/r8/runs/${runId}`, { headers: auth });
  expect(readAfter.status()).toBe(200);
  const after: Snapshot = await readAfter.json();
  const paymentAfter = after.payments.find(payment => payment.intentId === held.intentId);
  expect(paymentAfter?.status).toBe("Settled");
  expectReconciled(after.balances);
  expectSettlement(before.balances, after.balances, paymentBefore);
  await nav.getByText("Control Tower").click();
  const decisionRow = page.getByRole("row").filter({hasText:held.intentId});
  await expect(decisionRow.getByRole("cell", {name:"Settled",exact:true})).toBeVisible();
  await nav.getByText("Held Queue").click();
  await expect(page.getByText("Recorded score:",{exact:false})).toBeVisible();
  await expect(page.getByRole("cell",{name:"Released",exact:true})).toBeVisible();

  const observations = page.waitForResponse(r => r.request().method() === "GET" && r.url().endsWith(`/runs/${runId}/liquidity`));
  await nav.getByText("Liquidity Fan").click();
  const observed = await observations;
  expect(observed.status()).toBe(200);
  const liquidity = await observed.json();
  const observedAt = after.markers.filter(marker => marker.kind === "ObservationSeal").at(-1)!.atMs;
  expectReconciled(liquidity.actual.filter((row: { atMs: number }) => row.atMs === observedAt)
    .map((row: { balance: Bal }) => row.balance));
  await expect(page.getByText("statistical baseline, uncalibrated",{exact:false})).toBeVisible();
  await page.getByText("Liquidity data table").click();
  await expect(page.getByRole("columnheader",{name:"Available",exact:true})).toBeVisible();
  await nav.getByText("Rewind Mode").click();
  const closing = page.waitForResponse(r=>r.request().method()==="POST"&&r.url().endsWith("/close"));
  await page.getByRole("button",{name:"Close and verify source"}).click();
  const closeResponse = await closing;
  expect(closeResponse.status()).toBe(200);
  const closed = await closeResponse.json();
  const closedAuditResponse = await page.request.get(`/revenant/r8/runs/${runId}/audit`, { headers: auth });
  expect(closedAuditResponse.status()).toBe(200);
  const closedAudit: Audit = await closedAuditResponse.json();
  expect(closedAudit.status).toBe("Verified");
  expect(closedAudit.ledgerVerification).toBe("Verified");
  const sealKinds = new Set(closed.markers.map((m:{kind:string})=>m.kind));
  for (const kind of sealKinds) expect(["PaymentIntent","ReviewDecision","ReturnInstruction","Screening","Settlement","ObservationSeal"]).toContain(kind);
  await expect(page.getByRole("button",{name:/Marker: .* completed seal/}).first()).toBeVisible();
  const dockButtons = await page.getByRole("button",{name:/Marker: .* completed seal/}).count();
  expect(dockButtons).toBeGreaterThan(0);
  expect(dockButtons).toBeLessThanOrEqual(closed.markers.filter((m:{kind:string})=>m.kind==="ObservationSeal").length);
  await expect(page.getByRole("button",{name:"Run sandbox replay"})).toBeVisible();
  const slider = page.getByRole("slider",{name:"Timeline position"});
  await slider.focus();
  await slider.press("Home");
  await expect(page.getByTestId("r8-preview")).toContainText("Verified event 0");
  // The final event is the horizon seal, not a completed seal before the horizon: seeking there must not enable a fork.
  await slider.press("End");
  await expect(page.getByTestId("r8-preview")).toContainText(`Verified event ${closed.lastPosition}`);
  await expect(page.getByRole("button",{name:"Run sandbox replay"})).toBeDisabled();
  await expect(page.getByText(/NOT A COMPLETED OBSERVATION SEAL/)).toBeVisible();
  await slider.press("Home");
  await page.getByRole("button",{name:/Marker: 08:55 UTC completed seal/}).tap({timeout:6000});
  await expect(page.getByTestId("r8-preview")).toContainText("Verified event 288");
  await page.getByLabel("Comparison mode").selectOption("intervention");
  await page.getByRole("combobox",{name:"Payment",exact:true}).selectOption("P-001");
  await expect(page.getByRole("button",{name:"Run sandbox replay"})).toBeEnabled();
  const submitted = page.waitForResponse(r=>r.request().method()==="POST"&&r.url().endsWith("/counterfactual"));
  await page.getByRole("button",{name:"Run sandbox replay"}).click();
  const {jobId} = await (await submitted).json();
  await expect(page.getByTestId("r8-job-status")).toHaveText(/Replay Verified/);
  const result = await page.request.get(`/revenant/r8/runs/${runId}/counterfactual/${jobId}`,{headers:auth});
  const {verified} = await result.json();
  await expect(page.getByTestId("r8-kpi")).toHaveCount(4);
  expect(verified.headlines.map((x:{delta:string})=>x.delta)).toEqual(["150000","1","250","149600"]);
  console.log("R8 verified evidence", JSON.stringify({source:verified.sourceRecordHash, child:verified.childRecordHash,
    branch:verified.childTimelineId, digest:verified.evidenceDigest}));
  expect(await page.getByTestId("r8-kpi").evaluateAll(nodes=>nodes.map(n=>n.getAttribute("data-delta"))))
    .toEqual(verified.headlines.map((x:{delta:string})=>x.delta));
  await expect(page.getByRole("img",{name:/source and completed alternative balances/})).toBeVisible();
  // verified.balances: source and alternative sides carry the row's bank; the inherited prefix up to the fork seal is identical, and the
  // alternative diverges after it. The chart plots these rows as given (source -> "Source history", alternative -> "Verified sandbox").
  const forkSeal = closed.markers.find((m:{position:string})=>m.position==="288");
  expect(forkSeal?.kind).toBe("ObservationSeal");
  expect(forkSeal.atMs).toBeLessThan(closed.boundaryMs);
  type Row = {atMs:number;bankId:string;source:{bankId:string;postedMinor:string;reservedMinor:string;availableMinor:string};alternative:{bankId:string;postedMinor:string;reservedMinor:string;availableMinor:string}};
  const rows: Row[] = verified.balances;
  expect(rows.length).toBeGreaterThan(0);
  for (const row of rows) {
    expect(row.source.bankId).toBe(row.bankId); expect(row.alternative.bankId).toBe(row.bankId);
    for (const side of [row.source,row.alternative]) expect(BigInt(side.postedMinor)-BigInt(side.reservedMinor)).toBe(BigInt(side.availableMinor));
    if (row.atMs <= forkSeal.atMs) expect(row.alternative).toEqual(row.source);
  }
  expect(rows.some(r=>r.atMs>forkSeal.atMs&&JSON.stringify(r.source)!==JSON.stringify(r.alternative))).toBeTruthy();
  await page.getByText("Aligned balance data").click();
  await expect(page.getByRole("heading",{name:/source vs executed child/i})).toBeVisible();
  await nav.getByRole("link",{name:/Audit$/}).click();
  await expect(page.getByText("Original history unchanged")).toBeVisible();
  const auditRead = await page.request.get(`/revenant/r8/runs/${runId}/audit`,{headers:auth});
  const auditBody = await auditRead.json();
  expect(auditBody.status).toBe("Verified");
  expect(auditBody.ledgerVerification).toBe("Verified");
  expect(auditRead.status()).toBe(200);
  expect(auditBody.timelineId).toBe(closed.timelineId);
  expect(auditBody.recordHash).toBe(closedAudit.recordHash);
  expect(auditBody.recordHash).toBe(verified.sourceRecordHash);
  expect(auditBody.stateHash).toBe(closedAudit.stateHash);
  expect(auditBody.stateHash).toBe(verified.sourceStateHash);
  expect(auditBody.ledgerHeadHash).toBe(closedAudit.ledgerHeadHash);
  expect(verified.childTimelineId).not.toBe(auditBody.timelineId);
  expect(verified.childRecordHash).not.toBe(auditBody.recordHash);
  await page.getByText("Child evidence", { exact: true }).click();
  await expect(page.getByLabel("Sandbox verification")).toContainText(verified.childTimelineId);
  await expect(page.getByLabel("Sandbox verification")).toContainText(verified.childRecordHash);
  await expect(page.getByLabel("Sandbox verification")).toContainText(verified.childStateHash);
  console.log("R8 source audit evidence", JSON.stringify({
    sourceTimeline: auditBody.timelineId, sourceState: auditBody.stateHash,
    childState: verified.childStateHash, timeline: auditBody.status,
    ledger: auditBody.ledgerVerification, ledgerHead: auditBody.ledgerHeadHash,
  }));
  await expect(page.getByLabel("Chain integrity")).toContainText("Ledger audit chain");
  await expect(page.getByLabel("Chain integrity").getByText("● VERIFIED",{exact:true})).toBeVisible();

  // Use the payment-day seal. The warmup day also contains a 09:00 seal.
  const paymentDay = new Date(paymentBefore.acceptedAtMs).toISOString().slice(0, 10);
  const nineIso = `${paymentDay}T09:00:00.000Z`;
  const nine: Marker[] = closed.markers.filter((m: Marker) => m.kind === "ObservationSeal" && m.atMs === Date.parse(nineIso));
  expect(nine).toHaveLength(1);
  const [exact] = nine;
  expect(exact.position).toMatch(/^[1-9][0-9]*$/);
  expect(exact.atMs).toBeLessThan(closed.boundaryMs);

  await nav.getByText("Rewind Mode").click();
  const seek = page.waitForResponse(r => r.request().method() === "GET" && r.url().endsWith(`/timeline/${exact.position}`));
  await page.getByRole("button", { name: /Marker: 09:00 UTC completed seal/ }).tap();
  const seekResponse = await seek;
  expect(seekResponse.status()).toBe(200);
  const preview = await seekResponse.json();
  expect(preview).toMatchObject({ position: exact.position, boundaryMs: exact.atMs, status: "Verified" });
  expectReconciled(preview.balances);
  await expect(page.getByTestId("r8-preview")).toContainText(`Verified event ${exact.position}`);
  await page.getByLabel("Comparison mode").selectOption("policy");
  const exactSubmission = page.waitForResponse(r => r.request().method() === "POST" && r.url().endsWith("/counterfactual"));
  await page.getByRole("button", { name: "Run sandbox replay" }).click();
  const exactResponse = await exactSubmission;
  expect(exactResponse.status()).toBe(200);
  expect(exactResponse.request().postDataJSON()).toEqual({
    forkPosition: exact.position, horizonMs: closed.boundaryMs, policy: "policy-v8", interventions: null,
  });
  const exactJob = await exactResponse.json();
  const exactJobUrl = `/revenant/r8/runs/${runId}/counterfactual/${exactJob.jobId}`;
  await expect.poll(async () => {
    const read = await page.request.get(exactJobUrl, { headers: auth });
    expect(read.status()).toBe(200);
    return (await read.json()).status;
  }).toBe("Verified");
  const exactRead = await page.request.get(exactJobUrl, { headers: auth });
  const exactProof = (await exactRead.json()).verified;
  expect(exactProof).toMatchObject({
    sourceRecordHash: closedAudit.recordHash, sourceStateHash: closedAudit.stateHash, alternativePolicy: "policy-v8",
    horizonMs: closed.boundaryMs,
  });
  expect(new Set([exactProof.childTimelineId, verified.childTimelineId, closed.timelineId]).size).toBe(3);
  expect(exactProof.childRecordHash).toMatch(/^[0-9a-f]{64}$/);
  expect(exactProof.childStateHash).toMatch(/^[0-9a-f]{64}$/);
  const exactRows: Row[] = exactProof.balances;
  expect(exactRows.some(row => row.atMs > exact.atMs)).toBeTruthy();
  expectReconciled(exactRows.filter(row => row.atMs === exact.atMs).map(row => row.source));
  for (const row of exactRows) {
    for (const side of [row.source, row.alternative]) {
      expect(side.bankId).toBe(row.bankId);
      expect(BigInt(side.postedMinor) - BigInt(side.reservedMinor)).toBe(BigInt(side.availableMinor));
    }
    if (row.atMs <= exact.atMs) expect(row.alternative).toEqual(row.source);
  }
  expect(exactProof.headlines).toHaveLength(4);
  expect(exactProof.headlines.every((item: { delta: string }) => item.delta === "0")).toBeTruthy();
  const exactAudit = await page.request.get(`/revenant/r8/runs/${runId}/audit`, { headers: auth });
  expect(exactAudit.status()).toBe(200);
  expect(await exactAudit.json()).toEqual(closedAudit);

  const opening = [...before.opening].sort((a: Bal, b: Bal) => a.bankId.localeCompare(b.bankId));
  const openingDigest = createHash("sha256").update(JSON.stringify(opening)).digest("hex");
  const evidence = {
    runId, seed, bankIds: opening.map((balance: Bal) => balance.bankId), opening, openingDigest,
    instructions: before.payments.map(({ intentId, sourceBank, destinationBank, amountMinor, acceptedAtMs, status }: Pay) =>
      ({ intentId, sourceBank, destinationBank, amountMinor, acceptedAtMs, status })),
    decision: { intentId: held.intentId, reviewBefore: held.status, reviewAfter: reviewAfter.status,
      paymentBefore: paymentBefore.status, paymentAfter: paymentAfter!.status },
    balances: { before: before.balances, after: after.balances }, liquidityAtMs: observedAt,
    source: { timelineId: auditBody.timelineId, stateHash: auditBody.stateHash, recordHash: auditBody.recordHash,
      timelineVerdict: auditBody.status, ledgerVerdict: auditBody.ledgerVerification, ledgerHeadHash: auditBody.ledgerHeadHash },
    replay: { fork: forkSeal, jobId, childTimelineId: verified.childTimelineId, childStateHash: verified.childStateHash,
      childRecordHash: verified.childRecordHash, headlines: verified.headlines, breakdown: verified.breakdown },
    exact0900: { fork: exact, atUtc: nineIso, horizonMs: closed.boundaryMs, previewStateHash: preview.stateHash,
      jobId: exactJob.jobId, childTimelineId: exactProof.childTimelineId, childRecordHash: exactProof.childRecordHash,
      childStateHash: exactProof.childStateHash, headlines: exactProof.headlines },
  };
  await writeFile(test.info().outputPath("acceptance.json"), JSON.stringify(evidence, null, 2) + "\n");

  const invalid = await page.request.post(`/revenant/r8/runs/${runId}/counterfactual`,
    {headers:{...auth,"Idempotency-Key":"bad-position"},data:{forkPosition:"289",horizonMs:verified.horizonMs,policy:"policy-v8",interventions:null}});
  expect(invalid.ok()).toBeTruthy();
  const bad = await invalid.json();
  await expect.poll(async()=>{
    const failure = await page.request.get(`/revenant/r8/runs/${runId}/counterfactual/${bad.jobId}`,{headers:auth});
    const job = await failure.json();
    return [job.status,job.verified];
  }).toEqual(["Failed",null]);

  const retryLink = `/?view=r8&run=${runId}&job=${bad.jobId}#rewind`;
  await page.goto(retryLink);
  await expect(page.getByTestId("r8-job-status")).toContainText("Replay Failed");
  await expect(page.getByTestId("r8-kpi")).toHaveCount(0);

  const next = page.waitForResponse(r=>r.request().method()==="POST"&&r.url().endsWith("/revenant/r8/runs"));
  await page.getByRole("button",{name:"Launch fresh scenario"}).click();
  const second = await (await next).json();
  expect(second.runId).not.toBe(runId);
  await expect.poll(()=>page.getByLabel("Scenario",{exact:true}).inputValue(),{timeout:35_000}).not.toBe(runId);
});

type Bal = { bankId: string; postedMinor: string; reservedMinor: string; availableMinor: string };
type Pay = { intentId: string; sourceBank: string; destinationBank: string; amountMinor: string; acceptedAtMs: number; status: string };
type Cs = { caseId: string; intentId: string; version: number; status: string; requiredApprovals: number; approvalCount: number; amountMinor: string };
type Marker = { kind: string; position: string; atMs: number };
type Snapshot = { balances: Bal[]; payments: Pay[]; cases: Cs[]; markers: Marker[] };
type AuditEvent = { sequence: number; event: string; actor: string; subject: string; evidenceSha256: string | null };
type Audit = { timelineId: string; status: string; ledgerVerification: string; ledgerHeadHash: string; recordHash: string; stateHash: string; recentEvents: AuditEvent[] };

const usd = (minor: string) => {
  const amount = BigInt(minor);
  return "$" + (amount / 100n).toLocaleString("en-US") + "." + String(amount % 100n).padStart(2, "0");
};
const bank = (list: Bal[], id: string) => list.find(b => b.bankId === id)!;
const big = (b: Bal) => ({ posted: BigInt(b.postedMinor), reserved: BigInt(b.reservedMinor), available: BigInt(b.availableMinor) });
const observedTime = (atMs: number) => new Date(atMs).toISOString().slice(11, 16) + " UTC";

function expectReconciled(balances: Bal[]) {
  expect(balances).toHaveLength(6);
  expect(new Set(balances.map(balance => balance.bankId)).size).toBe(6);
  for (const balance of balances) {
    const { posted, reserved, available } = big(balance);
    expect(posted - reserved, balance.bankId).toBe(available);
  }
}

function expectSettlement(before: Bal[], after: Bal[], pay: Pay) {
  const principal = BigInt(pay.amountMinor);
  for (const balance of before) {
    const from = big(balance);
    const to = big(bank(after, balance.bankId));
    const source = balance.bankId === pay.sourceBank;
    const destination = balance.bankId === pay.destinationBank;
    const postedDelta = source ? -principal : destination ? principal : 0n;
    const reservedDelta = source ? -principal : 0n;
    expect(to.posted - from.posted, balance.bankId).toBe(postedDelta);
    expect(to.reserved - from.reserved, balance.bankId).toBe(reservedDelta);
    expect(to.available - from.available, balance.bankId).toBe(postedDelta - reservedDelta);
  }
}

test("staff decides a held payment through the real approval dialog and the ledger reconciles", async ({ page, browser }) => {
  page.setDefaultTimeout(10_000);
  const username = "r8.browser.approver";
  const bootstrap = await browser.newPage({ baseURL: test.info().project.use.baseURL });
  const enrolled = await browserLogin(bootstrap, username)
    .then(auth => enrollMfa(bootstrap, auth))
    .finally(() => bootstrap.close());
  const auth = await browserLogin(page, username, await freshTotp(enrolled));
  const nav = page.getByRole("navigation",{name:"Revenant screens"});
  // Role chip: informational; must equal a role really returned by /auth/staff/me.
  const me = await page.request.get("/auth/staff/me",{headers:auth});
  expect(me.status()).toBe(200);
  const actor = await me.json() as { subject: string; roles: string[]; claims: string[] };
  expect(actor.roles).toContain("operator");
  expect(actor.claims).toContain("mfa");
  await expect(page.locator(".r8-role")).toHaveText("Operator");
  await expect(page.locator(".r8-role")).toHaveAttribute("title", actor.subject);

  const launch = page.waitForResponse(r => r.request().method()==="POST"&&r.url().endsWith("/revenant/r8/runs"));
  await page.getByRole("button",{name:"Launch fresh scenario"}).click();
  expect((await launch).status()).toBe(200);
  await expect(page.getByRole("img",{name:"Committed payment flows among the six banks"})).toBeVisible({timeout:40_000});
  const runId = await page.getByLabel("Scenario",{exact:true}).inputValue();
  const read = async (): Promise<Snapshot> => {
    const response = await page.request.get(`/revenant/r8/runs/${runId}`, { headers: auth });
    expect(response.status()).toBe(200);
    return response.json();
  };
  const readAudit = async (): Promise<Audit> => {
    const response = await page.request.get(`/revenant/r8/runs/${runId}/audit`, { headers: auth });
    expect(response.status()).toBe(200);
    const audit: Audit = await response.json();
    expect(audit.ledgerVerification).toBe("Verified");
    expect(audit.status).toBe("Recording");
    return audit;
  };
  const before = await read();
  const held = before.cases.find(c=>c.status==="Open")!;
  expect(held, "an Open case").toBeTruthy();
  const pay = before.payments.find(p=>p.intentId===held.intentId)!;
  const principal = BigInt(pay.amountMinor);
  expect(BigInt(held.amountMinor)).toBe(principal);
  expect(pay.status).toBe("Held");
  expect([held.approvalCount, held.requiredApprovals, principal]).toEqual([0, 1, 150_000n]);
  expect(before.cases.every(c => c.requiredApprovals === 1)).toBeTruthy();
  expectReconciled(before.balances);
  const auditBefore = await readAudit();
  const isReview = (event: AuditEvent) => event.event === "fact.reviewdecision";
  expect(auditBefore.recentEvents.filter(isReview)).toHaveLength(0);

  const decisions: string[] = [];
  page.on("request", r => { if (r.method()==="POST" && r.url().includes(`/runs/${runId}/cases/`)) decisions.push(r.url()); });
  await nav.getByText("Held Queue").click();
  await page.getByRole("button",{name:new RegExp(`Select case ${held.intentId},`)}).click();
  const approve = page.getByRole("button",{name:"Approve",exact:true});
  await approve.click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(usd(held.amountMinor));
  await expect(dialog).toContainText(`(${pay.sourceBank})`);
  await expect(dialog).toContainText(`(${pay.destinationBank})`);
  await expect(dialog).toContainText(`${held.approvalCount} of ${held.requiredApprovals} approvals · ${held.status} · v${held.version}`);
  await expect(dialog).toContainText(`Release ${usd(held.amountMinor)} for settlement?`);
  await expect(dialog).toContainText("release and settlement attempt");

  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toHaveCount(0);
  expect(decisions).toHaveLength(0);
  const afterCancel = await read();
  expect(afterCancel.cases.find(c => c.intentId === held.intentId)).toEqual(held);
  expectReconciled(afterCancel.balances);
  const auditAfterCancel = await readAudit();
  expect(auditAfterCancel.recentEvents.filter(isReview)).toHaveLength(0);

  // Confirm through the primary action: exactly one versioned, idempotent POST.
  await approve.click();
  const decision = page.waitForResponse(r=>r.request().method()==="POST"&&r.url().includes(`/runs/${runId}/cases/${held.intentId}`));
  await page.getByRole("dialog").getByRole("button",{ name: "Approve and release" }).click();
  const res = await decision;
  expect(res.status(), await res.text()).toBe(200);
  expect(res.request().postDataJSON()).toEqual({action:"Approve",expectedVersion:held.version});
  expect(res.request().headers()["idempotency-key"]).toBeTruthy();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const receipt = await res.json() as Cs;
  expect(receipt.approvalCount).toBe(held.approvalCount+1);
  expect(receipt.status).toBe("Released");
  expect(receipt.version).toBe(held.version + 1);
  expect(decisions).toHaveLength(1);

  // Refreshed UI and ledger agree with the server.
  const after = await read();
  const caseAfter = after.cases.find(c=>c.intentId===held.intentId)!;
  expect(caseAfter.approvalCount).toBe(held.approvalCount+1);
  expect(caseAfter.status).toBe(receipt.status);
  const row = page.getByRole("button",{name:new RegExp(`Select case ${held.intentId},`)}).locator("xpath=ancestor::tr");
  await expect(row).toContainText(`${caseAfter.approvalCount}/${caseAfter.requiredApprovals}`);
  await expect(row).toContainText("Released");
  expect(caseAfter.version).toBe(receipt.version);
  expectReconciled(after.balances);
  const payAfter = after.payments.find(p => p.intentId === held.intentId)!;
  expect(payAfter.status).toBe("Settled");

  // Create has completed its seeded dispatches. Verify the isolated review/settlement boundary
  // before attributing the bank deltas to the selected payment.
  const priorPosition = BigInt(afterCancel.markers.at(-1)!.position);
  expect(after.markers.filter(marker => BigInt(marker.position) > priorPosition).map(marker => marker.kind))
    .toEqual(["ReviewDecision", "Settlement"]);
  expect(after.payments.filter(p => p.intentId !== held.intentId))
    .toEqual(afterCancel.payments.filter(p => p.intentId !== held.intentId));
  expectSettlement(afterCancel.balances, after.balances, pay);
  await expect(approve).toBeDisabled();
  expect(decisions).toHaveLength(1);

  const liquidityPath = `/revenant/r8/runs/${runId}/liquidity`;
  const liquidityResponse = page.waitForResponse(r => r.request().method() === "GET" && r.url().endsWith(liquidityPath));
  await nav.getByText("Liquidity Fan").click();
  const liquidity = await liquidityResponse;
  expect(liquidity.status()).toBe(200);
  const liq = await liquidity.json() as {
    actual: { atMs: number; balance: Bal }[];
    projection: { bankId: string; atMs: number; availableMinor: string }[];
    method: string;
    uncertainty: string;
  };
  const latest = liq.actual.filter(x => x.balance.bankId === pay.sourceBank).at(-1)!;
  expect(latest.atMs).toBe(after.markers.filter(m => m.kind === "ObservationSeal").at(-1)!.atMs);
  expectReconciled(liq.actual.filter(x => x.atMs === latest.atMs).map(x => x.balance));
  await page.getByLabel("Bank", { exact: true }).selectOption(pay.sourceBank);
  await expect(page.getByLabel("Bank", { exact: true })).toHaveValue(pay.sourceBank);
  await expect(page.getByRole("img", { name: `${pay.sourceBank} posted, reserved, available and projected available liquidity` })).toBeVisible();
  const tiles = page.locator(".r8-fan-tile");
  const amounts = [latest.balance.postedMinor, latest.balance.reservedMinor, latest.balance.availableMinor];
  await expect(tiles.locator(".r8-figure")).toHaveText(amounts.map(amount => usd(amount) + " USD"));
  await expect(tiles.locator(".r8-muted")).toHaveText(amounts.map(() => `observed ${observedTime(latest.atMs)}`));
  const projection = liq.projection.filter(x => x.bankId === pay.sourceBank);
  expect(projection).toEqual(Array.from({ length: 8 }, (_, i) => ({
    bankId: pay.sourceBank, atMs: latest.atMs + (i + 1) * 300_000, availableMinor: latest.balance.availableMinor,
  })));
  expect(liq.method).toBe("LiquidityPersistenceBaseline/1");
  expect(liq.uncertainty).toBe("Uncalibrated");
  await page.getByText("Liquidity data table").click();
  await expect(page.getByRole("table", { name: `${pay.sourceBank} persistence baseline` }).locator("tbody tr")).toHaveCount(8);
  await expect(page.getByText(`${liq.method} · statistical baseline, ${liq.uncertainty}`, { exact: false })).toBeVisible();
  await expect(page.getByText("Illustrative operating buffer — 10% of opening available balance")).toBeVisible();
  await expect(page.getByText("R8-SyntheticBufferReference/1", { exact: true })).toBeVisible();
  await expect(page.getByText(/\d+ ?% (confidence|interval)/i)).toHaveCount(0);

  const auditResponse = page.waitForResponse(r => r.request().method() === "GET" && r.url().endsWith(`/runs/${runId}/audit`));
  await nav.getByRole("link", { name: /Audit$/ }).click();
  const response = await auditResponse;
  expect(response.status()).toBe(200);
  const audit: Audit = await response.json();
  expect(audit.ledgerVerification).toBe("Verified");
  // This journey leaves the source open. The closed Replay journey checks the combined Verified badge.
  expect(audit.status).toBe("Recording");
  expect(audit.ledgerHeadHash).not.toBe(auditAfterCancel.ledgerHeadHash);
  const priorSequence = auditAfterCancel.recentEvents.at(-1)!.sequence;
  const appended = audit.recentEvents.filter(event => event.sequence > priorSequence);
  expect(appended.map(event => event.event)).toEqual(["fact.reviewdecision", "fact.settlement"]);
  expect(appended[0]).toMatchObject({ actor: actor.subject, subject: `fact:${priorPosition + 1n}` });
  const integrity = page.getByLabel("Chain integrity");
  await expect(integrity.locator("dl > div").filter({ hasText: "Original timeline" }).locator("dd")).toHaveText(audit.status);
  await expect(integrity.locator("dl > div").filter({ hasText: "Ledger audit chain" }).locator("dd")).toHaveText(audit.ledgerVerification);
  await expect(integrity.getByTitle("Ledger audit head")).toHaveText(audit.ledgerHeadHash);
  await expect(page.getByText(`Showing ${audit.recentEvents.length} of the ${audit.recentEvents.length}`, { exact: false })).toBeVisible();
  await expect(page.getByText("Original history unchanged")).toHaveCount(0);
  await expect(page.getByText("No verified sandbox in view", { exact: false })).toBeVisible();
  console.log("R8 approval evidence", JSON.stringify({
    intent: pay.intentId, principalMinor: pay.amountMinor, status: payAfter.status, caseVersion: receipt.version,
    source: { before: bank(afterCancel.balances, pay.sourceBank), after: bank(after.balances, pay.sourceBank) },
    destination: { before: bank(afterCancel.balances, pay.destinationBank), after: bank(after.balances, pay.destinationBank) },
    liquidity: latest, ledgerVerification: audit.ledgerVerification, ledgerHead: audit.ledgerHeadHash,
  }));
});
