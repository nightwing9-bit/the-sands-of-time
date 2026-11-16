// Shared by the Milestone 2 browser tests and local visual preview.
const origin = 1_789_203_300_000;
export const runId = "22222222-3333-4444-5555-666666666666";
export const jobId = "99999999-8888-7777-6666-555555555555";
const banks = ["BANK-A", "BANK-B", "BANK-C", "BANK-D", "BANK-E", "BANK-F"];
const opening = banks.map((bankId, i) => ({ bankId, postedMinor: String(2_500_000 + i * 310_000), reservedMinor: "0", availableMinor: String(2_500_000 + i * 310_000) }));
const seals = Array.from({ length: 24 }, (_, i) => ({ kind: "ObservationSeal", position: String((i + 1) * 12), atMs: origin + (i + 1) * 300_000, intentId: null }));
const boundaryMs = seals.at(-1)!.atMs;
const risk = (ppm: number | null) => ({ scorePpm: ppm, thresholdPpm: 650_000, modelId: "r5-fastforest-pinned-2026-11", policy: "policy-v7", reason: "HoldForReview",
  evidenceSha256: "4c218e9b7d0a11f2c3b4a5968778695a4b3c2d1e0f9a8b7c6d5e4f3a2b1c9e12",
  observations: ppm === null ? [] : [{ name: "Base rule", value: "VelocityReview" }, { name: "Review slots available", value: "2" },
    { name: "Prior causal fact", value: "41" }, { name: "Feature vector digest", value: "a18b5c43f00e9d8c7b6a5f4e3d2c1b0a99887766" }] });
const payments = [
  { intentId: "P-001", sourceBank: "BANK-A", destinationBank: "BANK-D", amountMinor: "150000", status: "Held", reason: "HoldForReview", acceptedAtMs: origin + 2_100_000, risk: risk(912_400) },
  { intentId: "P-002", sourceBank: "BANK-B", destinationBank: "BANK-C", amountMinor: "150000", status: "Settled", reason: "Allow", acceptedAtMs: origin + 2_400_000, risk: risk(120_000) },
  { intentId: "P-003", sourceBank: "BANK-F", destinationBank: "BANK-A", amountMinor: "150000", status: "Held", reason: "HoldForReview", acceptedAtMs: origin + 3_000_000, risk: risk(701_000) },
  { intentId: "P-004", sourceBank: "BANK-E", destinationBank: "BANK-B", amountMinor: "150000", status: "Returned", reason: "Sanctions", acceptedAtMs: origin + 3_600_000, risk: risk(null) },
];
export const cases = [
  { caseId: "c0ffee00-0000-4000-8000-000000000001", intentId: "P-001", version: 1, status: "Open", requiredApprovals: 2, approvalCount: 0, amountMinor: "150000", risk: risk(912_400) },
  { caseId: "c0ffee00-0000-4000-8000-000000000003", intentId: "P-003", version: 2, status: "AwaitingSecondApproval", requiredApprovals: 2, approvalCount: 1, amountMinor: "150000", risk: risk(701_000) },
];
const balanceAt = (bankId: string, i: number, shift = 0n) => {
  const base = BigInt(opening.find(b => b.bankId === bankId)!.postedMinor);
  const drift = BigInt(((i * 37 + bankId.charCodeAt(5) * 11) % 9) * 4_000);
  const reserved = (bankId === "BANK-A" && i >= 7) || (bankId === "BANK-F" && i >= 10) ? 150_000n : 0n;
  const posted = base - drift + shift;
  return { bankId, postedMinor: String(posted), reservedMinor: String(reserved), availableMinor: String(posted - reserved) };
};
const current = banks.map(b => balanceAt(b, seals.length - 1));
export const snapshot = (status: "Active" | "Verified") => ({ runId, timelineId: runId, status, boundaryMs, opening, balances: current, payments, cases,
  anomalies: [], anomalyStatus: "InsufficientHistory", lastPosition: String(seals.length * 12 + 4),
  markers: [...seals, { kind: "Screening", position: "86", atMs: origin + 2_100_000, intentId: "P-001" }, { kind: "Screening", position: "122", atMs: origin + 3_000_000, intentId: "P-003" }]
    .sort((a, b) => Number(a.position) - Number(b.position)) });
export const liquidity = { method: "LiquidityPersistenceBaseline/1", uncertainty: "Uncalibrated", anomalyStatus: "InsufficientHistory",
  actual: seals.flatMap((s, i) => banks.map(b => ({ atMs: s.atMs, balance: balanceAt(b, i) }))),
  projection: banks.flatMap(b => Array.from({ length: 8 }, (_, i) => ({ bankId: b, atMs: boundaryMs + (i + 1) * 300_000, availableMinor: balanceAt(b, seals.length - 1).availableMinor }))) };
export const fork = seals[6]!;
const headlines = [["Fraud prevented", "0", "150000", "150000", "USD minor units"], ["Legitimate payments delayed", "0", "1", "1", "payments"],
  ["False-positive cost", "0", "250", "250", "USD minor units"], ["Net financial effect", "0", "149600", "149600", "USD minor units"]]
  .map(([label, source, alternative, delta, unit]) => ({ label, source, alternative, delta, unit }));
const verified = { sourcePolicy: "policy-v7", alternativePolicy: "policy-v8", mode: "PolicyOnly", costPolicy: "counterfactual-cost-v1", continuationPolicy: "exogenous-intents-v1",
  model: "r5", horizonMs: boundaryMs, childTimelineId: "77777777-aaaa-4bbb-8ccc-dddddddddddd", childRecordHash: "cd8401b3aa00f1e2d3c4b5a69788796a5b4c3d2e1f0a9b8c7d6e5f4a3b2c772c",
  childStateHash: "87d5e1a9", sourceRecordHash: "0238d7ab00000000000000000000000000000000000000000000000000071e", sourceStateHash: "state-src", evidenceDigest: "611daf50",
  headlines, breakdown: headlines, actions: [],
  payments: [{ intentId: "P-001", principalMinor: "150000", sourceStatus: "Settled", alternativeStatus: "Returned", sourceCause: "Released", alternativeCause: "PolicyHold",
    sourceEvidence: "aa", alternativeEvidence: "bb", delayMinutes: "0", delayCostMinor: "0", sourceSettlementMs: null, alternativeSettlementMs: null }],
  balances: seals.slice(6).flatMap((s, k) => banks.map(b => ({ atMs: s.atMs, bankId: b, source: balanceAt(b, k + 6),
    alternative: balanceAt(b, k + 6, b === "BANK-A" && k > 1 ? BigInt(k) * 9_000n : b === "BANK-D" && k > 1 ? -BigInt(k) * 9_000n : 0n) }))) };
export const audit = { timelineId: runId, status: "Verified", ledgerVerification: "Verified", stateHash: "state-src", recordHash: verified.sourceRecordHash,
  manifestHash: "manifest", policy: "policy-v7", model: "r5", lastPosition: String(seals.length * 12 + 4), ledgerHeadHash: "9f3c28b14c7aa71e0000000000000000aa71e",
  recentEvents: Array.from({ length: 14 }, (_, i) => ({ sequence: 1842 - i, event: ["LedgerCheckpoint", "PaymentReleased", "ReviewApproved", "PaymentHeld", "ScreeningRecorded", "PaymentAccepted"][i % 6],
    actor: i % 3 === 0 ? "system" : i % 3 === 1 ? "r8.browser.operator" : "policy-v7", subject: i % 6 === 0 ? "" : `P-00${(i % 4) + 1}`, subjectVersion: i % 2 ? i : null,
    evidenceSha256: i % 5 === 0 ? null : (0x3b91c8ad + i).toString(16) + "00ff11ee22dd33cc44bb55aa" })) };

export const staffLogin = { accessToken: "fixture-token" };
export const staffProfile = { kind: "staff", subject: "staff-1", roles: ["supervisor"], claims: [] };
export const run = { runId, seed: "42", manifestHash: "m" };

export function timelinePreview(position: string) {
  return { position, boundaryMs: seals.find(s => s.position === position)?.atMs ?? origin,
    stateHash: "fixture-state-" + position, status: "Verified", balances: current };
}

export function replay(status: "Pending" | "Verified" | "Failed" | "Cancelled" | null) {
  return { jobId, status, failure: status === "Failed" ? "fixture-failure" : null,
    verified: status === "Verified" ? verified : null };
}
