import { createHmac } from "node:crypto";
import { expect, test } from "@playwright/test";

function totp(secret: string) {
  let buffer = 0, bits = 0;
  const bytes: number[] = [];
  for (const char of secret.toUpperCase().replace(/[^A-Z2-7]/g, "")) {
    buffer = (buffer << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".indexOf(char);
    bits += 5;
    if (bits >= 8) { bytes.push((buffer >>> (bits - 8)) & 255); bits -= 8; }
  }
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now()/30_000)));
  const hash = createHmac("sha1", Buffer.from(bytes)).update(counter).digest();
  return String((hash.readUInt32BE(hash.at(-1)! & 15) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

test("staff reviews and verifies a replay through all five real Revenant screens on a phone", async ({page}) => {
  await page.goto("/?view=r8");
  const login = page.waitForResponse(r => r.url().endsWith("/auth/staff/login") && r.request().method() === "POST");
  await page.getByLabel("Username").fill("r8.browser.operator");
  await page.getByLabel("Password").fill(process.env.E2E_R8_PASSWORD!);
  await page.getByRole("button",{name:"Sign in"}).click();
  const loginResponse = await login;
  expect(loginResponse.ok()).toBeTruthy();
  const {accessToken} = await loginResponse.json();
  const auth = {Authorization:`Bearer ${accessToken}`};
  const enrollment = await page.request.post("/auth/staff/mfa/enroll", {headers:auth,data:{currentPassword:process.env.E2E_R8_PASSWORD}});
  expect(enrollment.ok()).toBeTruthy();
  const {sharedKey} = await enrollment.json();
  const confirm = await page.request.post("/auth/staff/mfa/confirm", {headers:auth,data:{code:totp(sharedKey)}});
  expect(confirm.ok()).toBeTruthy();

  page.setDefaultTimeout(10_000);
  const nav = page.getByRole("navigation",{name:"Revenant screens"});
  const launch = page.waitForResponse(r => r.request().method()==="POST"&&r.url().endsWith("/revenant/r8/runs"));
  await page.getByRole("button",{name:"Launch fresh scenario"}).click();
  const launchResponse = await launch;
  expect(launchResponse.status(), await launchResponse.text()).toBe(200);
  await expect(page.getByRole("img",{name:"Committed payment flows among the six banks"})).toBeVisible({timeout:40_000});
  await expect(page.getByText("Current balances")).toBeVisible();
  const runId = await page.getByLabel("Scenario",{exact:true}).inputValue();
  const readRun = await page.request.get(`/revenant/r8/runs/${runId}`, {headers:auth});
  expect(readRun.ok()).toBeTruthy();
  const before = await readRun.json();
  const [held] = before.cases;
  expect(held.status).toBe("Open");
  const released = await page.request.post(`/revenant/r8/runs/${runId}/cases/${held.intentId}`, {
    headers:{...auth,"Idempotency-Key":"independent-release"}, data:{action:"Approve",expectedVersion:held.version},
  });
  expect(released.status(), await released.text()).toBe(200);
  const decisionRow = page.getByRole("row").filter({hasText:held.intentId});
  await expect(decisionRow.getByRole("cell", {name:"Settled",exact:true})).toBeVisible();
  await nav.getByText("Held Queue").click();
  await expect(page.getByText("Recorded score:",{exact:false})).toBeVisible();
  await expect(page.getByRole("cell",{name:"Released",exact:true})).toBeVisible();

  await nav.getByText("Liquidity Fan").click();
  await expect(page.getByText("statistical baseline, uncalibrated",{exact:false})).toBeVisible();
  await page.getByText("Liquidity data table").click();
  await expect(page.getByRole("columnheader",{name:"Available",exact:true})).toBeVisible();
  await nav.getByText("Rewind Mode").click();
  await page.getByRole("button",{name:"Close and verify source"}).click();
  await expect(page.getByRole("button",{name:"Run sandbox replay"})).toBeVisible();
  const slider = page.getByRole("slider",{name:"Timeline position"});
  await slider.focus();
  await slider.press("Home");
  await expect(page.getByTestId("r8-preview")).toContainText("Verified event 0");
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
  await nav.getByRole("link",{name:/Audit$/}).click();
  await expect(page.getByText("Original history unchanged")).toBeVisible();

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
