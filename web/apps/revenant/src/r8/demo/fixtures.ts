// Prepared R8 scenario shared by Demo Mode, the local preview and Milestone 2 browser tests.
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

// Browser Demo Mode uses presentation fixtures independently of the contract preview above.
const preparedDate = "2026-09-12";
const preparedTime = (clock: string) => Date.parse(`${preparedDate}T${clock.length === 5 ? clock + ":00" : clock}Z`);
const minute = 60_000;
const usd = (amount: number) => String(Math.round(amount * 100));
const millions = (amount: number) => usd(amount * 1_000_000);
const preparedBoundary = preparedTime("09:41:07");
const preparedBalance = (bankId: string, availableMinor: string) =>
  ({ bankId, postedMinor: availableMinor, reservedMinor: "0", availableMinor });

const bankPositions = [4_821_673, 2_961_405, 3_202_690, 3_671_284, 2_409_180, 1_339_760];
const preparedBalances = banks.map((b, i) => preparedBalance(b, usd(bankPositions[i]!)));
const preparedOpening = preparedBalances.map(b => b.bankId === "BANK-F" ? preparedBalance(b.bankId, millions(1.83)) : b);

const preparedHeld = [
  ["RV-HLD-2041", "BANK-A", "BANK-D", 1_841_673, 94, "12m", "09:29:12", "Circular transaction pattern", "Northstar Settlement Ltd", "Ostera Trading Group"],
  ["RV-HLD-2040", "BANK-F", "BANK-B", 692_418, 87, "18m", "09:23:31", "Unusual transaction velocity", "Corvane Treasury", "Halden Custody"],
  ["RV-HLD-2039", "BANK-D", "BANK-E", 415_927, 81, "21m", "09:20:06", "Circular transaction pattern", "Ostera Holdings", "Tessell Capital"],
  ["RV-HLD-2037", "BANK-B", "BANK-A", 270_684, 72, "27m", "09:14:27", "Destination velocity exceeded", "Halden Custody", "Meridian Trust"],
  ["RV-HLD-2035", "BANK-C", "BANK-F", 84_219, 68, "32m", "09:09:50", "Policy threshold exceeded", "Quillon Financial", "Corvane Treasury"],
  ["RV-HLD-2032", "BANK-E", "BANK-D", 112_763, 57, "38m", "09:03:23", "Unusual route concentration", "Tessell Capital", "Ostera Holdings"],
  ["RV-HLD-2030", "BANK-A", "BANK-F", 59_381, 44, "43m", "08:58:13", "Velocity review", "Meridian Trust", "Corvane Treasury"],
] as const;
const primaryCaseId = preparedHeld[0][0];
const primaryIndicators = [
  { name: "Circular route detection", value: "94%" },
  { name: "Transaction velocity", value: "88%" },
  { name: "Counterparty concentration", value: "76%" },
  { name: "Unusual settlement time", value: "62%" },
];
const preparedPayments = preparedHeld.map(([intentId, sourceBank, destinationBank, amount, score, , at, reason]) => ({
  intentId, sourceBank, destinationBank, amountMinor: usd(amount), status: "Held", reason, acceptedAtMs: preparedTime(at),
  risk: { ...risk(score * 10_000), modelId: "Prepared scenario", reason, observations: intentId === primaryCaseId ? primaryIndicators : [] },
}));
const preparedCases = preparedPayments.map(p => ({
  caseId: p.intentId, intentId: p.intentId, version: 1, status: "Open", requiredApprovals: 2, approvalCount: 0, amountMinor: p.amountMinor, risk: p.risk,
}));
export const heldDetails = new Map(preparedHeld.map(([id, , , , , age, at, , sender, beneficiary]) => {
  const accepted = preparedTime(at);
  const history = id === primaryCaseId ? { accepted: accepted - 2000, screened: accepted - 1000 } : undefined;
  return [String(id), { age, sender, beneficiary, history }];
}));

const preparedActivity = [
  ["09:39", "BANK-A", "BANK-D", 186_427, "Released"],
  ["09:36", "BANK-C", "BANK-F", 88_214, "Held"],
  ["09:34", "BANK-D", "BANK-F", 412_731, "Routed"],
  ["09:31", "BANK-B", "BANK-C", 275_619, "Released"],
  ["09:29", "BANK-F", "BANK-A", 95_342, "Held"],
] as const;
const activityPayments = preparedActivity.map(([at, sourceBank, destinationBank, amount, status]) => ({
  intentId: `RV-ACT-${at.replace(":", "")}`, sourceBank, destinationBank, amountMinor: usd(amount),
  status, reason: "Prepared activity", risk: null, acceptedAtMs: preparedTime(at),
}));

export const preparedTower = {
  activity: activityPayments,
  volumeMinor: usd(18_416_732), transferCount: 173, releaseRate: "92.4%", priorReleaseRate: "89.1%",
  heading: "Money is moving across six banks; one ring is asking for attention.",
  alerts: [
    { key: "ring", tone: "high", title: "Circular route detected", sub: "MRD → OST → CRV → MRD", at: preparedTime("09:34"), intent: null },
    { key: "floor", tone: "warn", title: "Corvane liquidity floor risk", sub: "Forecast below minimum", at: preparedTime("09:31"), intent: null },
    { key: primaryCaseId, tone: "high", title: "Held payment requires approval", sub: primaryCaseId, at: preparedTime("09:29"), intent: primaryCaseId },
  ],
  trendOpening: opening,
  trends: liquidity.actual.map(x => ({ ...x, atMs: x.atMs - boundaryMs + preparedBoundary })),
};

const observed = [1.83, 1.80, 1.76, 1.72, 1.69, 1.63, 1.58, 1.56, 1.48, 1.45, 1.41, 1.37, 1.33976];
const observedStart = preparedTime("09:00");
const observedEnd = preparedTime("09:40");
const forecastTimes = ["09:40", "10:00", "10:22", "11:00", "12:00", "14:00", "16:00", "18:00"].map(preparedTime);
const forecastPoints = (amounts: number[]) => amounts.map((amount, i) => ({ at: forecastTimes[i]!, minor: millions(amount) }));
export const preparedForecast = {
  bank: "BANK-F", thresholdMinor: usd(1_000_000), crossing: preparedTime("10:22"), horizon: preparedTime("18:00"),
  median: forecastPoints([1.33976, 1.20, 1.00, 0.92, 0.82, 0.70, 0.61, 0.54]),
  inner: {
    lower: forecastPoints([1.33976, 1.12, 0.88, 0.75, 0.62, 0.45, 0.31, 0.22]),
    upper: forecastPoints([1.33976, 1.28, 1.12, 1.09, 1.06, 1.01, 0.96, 0.90]),
  },
  outer: {
    lower: forecastPoints([1.33976, 1.02, 0.75, 0.55, 0.33, 0.14, 0.07, 0.03]),
    upper: forecastPoints([1.33976, 1.38, 1.24, 1.30, 1.40, 1.50, 1.62, 1.73]),
  },
};
const preparedLiquidity = {
  method: "Prepared scenario", uncertainty: "Illustrative 50% and 80% bands", anomalyStatus: "Prepared",
  actual: observed.flatMap((amount, i) => preparedBalances.map(balance => ({
    atMs: observedStart + i * (observedEnd - observedStart) / (observed.length - 1),
    balance: balance.bankId === preparedForecast.bank ? preparedBalance(balance.bankId, millions(amount)) : balance,
  }))),
  projection: preparedForecast.median.slice(1).map(p => ({ atMs: p.at, bankId: preparedForecast.bank, availableMinor: p.minor })),
};

const rewindOrigin = preparedTime("06:00");
const original = [15.4, 16.1, 16.8, 17.2, 18.5, 17.9, 18.7, 20.0, 19.3, 19.9, 20.7, 20.2, 21.4, 22.1, 21.8, 23.0, 22.3, 23.6, 24.1];
const alternate = [15.4, 16.1, 16.8, 17.2, 18.5, 17.9, 18.7, 20.0, 19.3, 19.9, 22.1, 23.4, 24.6, 26.0, 27.2, 28.1, 29.5, 30.0, 31.2];
const rewindSeals = original.map((_, i) => ({ kind: "ObservationSeal", position: String(i * 12), atMs: rewindOrigin + i * 20 * minute, intentId: null }));
const defaultForkIndex = 9;
export const preparedRewind = {
  bank: "NETWORK", fork: rewindSeals[defaultForkIndex]!, horizon: rewindSeals.at(-1)!.atMs,
};
const rewindMarkers = [...rewindSeals, ...preparedPayments.slice(0, 2).map(p => ({
  kind: "Screening", position: String(Math.round((p.acceptedAtMs - rewindOrigin) / (20 * minute) * 12)),
  atMs: p.acceptedAtMs, intentId: p.intentId,
}))].sort((a, b) => Number(a.position) - Number(b.position));

const networkPosition = bankPositions.reduce((total, amount) => total + amount, 0);
// Keep the existing bank selector usable with an illustrative share of the network curve for each bank.
function rewindBalances(amount: number) {
  return [preparedBalance(preparedRewind.bank, millions(amount)), ...banks.map((b, i) =>
    preparedBalance(b, usd(Math.round(amount * 1_000_000 * bankPositions[i]! / networkPosition))))];
}
export const preparedHistory = rewindSeals.flatMap((s, i) => rewindBalances(original[i]!).map(balance => ({ atMs: s.atMs, balance })));

const preparedHeadlines = [
  ["Fraud prevented", usd(2_463_921), "USD minor units"],
  ["False-positive cost", usd(181_436), "USD minor units"],
  ["Legitimate payments delayed", "14", "payments"],
  ["Net financial effect", usd(1_923_487), "USD minor units"],
].map(([label, value, unit]) => ({ label: label!, source: "0", alternative: value!, delta: value!, unit: unit! }));
const originalHead = "9f3c28b14c7aa71e";
const scenarioHead = "cd8401b33e8a772c";
export const preparedAuditDetails = {
  originalHead, scenarioHead, chainLength: 1842, verifiedAt: "09:41:07.204 UTC", scenario: "s-0007", policies: "v7 → v8",
};
const preparedEvents = [
  ["09:41:07.204", "Ledger checkpoint", "System", "ORIGINAL", "9f3c28b1"],
  ["09:39:11.018", "Payment released", "Analyst A.K.", "ORIGINAL", "7d30ac62"],
  ["09:35:52.482", "Fraud confirmed", "Risk engine", "ORIGINAL", "3b91c8ad"],
  ["09:33:06.100", "Simulated hold", "Policy v8", "SCENARIO", "cd8401b3"],
  ["09:30:17.620", "Rule evaluation", "Policy v8", "SCENARIO", "20f8ce14"],
  ["09:29:12.387", "Payment held", "Policy v7", "ORIGINAL", "4c218e9b"],
  ["09:24:10.004", "Scenario fork created", "Analyst A.K.", "SCENARIO", "87d5e1a9"],
  ["09:20:36.235", "Payment routed", "System", "ORIGINAL", "611daf50"],
  ["09:12:44.850", "Policy evaluation", "Policy v7", "ORIGINAL", "a18b5c43"],
  ["09:00:00.000", "Ledger checkpoint", "System", "ORIGINAL", "0238d7ab"],
] as const;
const preparedAudit = {
  ...audit, recordHash: originalHead, ledgerHeadHash: originalHead, lastPosition: String(preparedAuditDetails.chainLength),
  recentEvents: preparedEvents.map(([timestamp, event, actor, source, evidenceSha256], i) => ({
    sequence: preparedAuditDetails.chainLength - i, timestamp, event, actor, source, evidenceSha256,
    subject: event === "Payment held" ? primaryCaseId : "", subjectVersion: null,
  })),
};

export const preparedScenario = {
  cases: preparedCases, liquidity: preparedLiquidity, audit: preparedAudit,
  snapshot(status: "Active" | "Verified", reviews: typeof cases = preparedCases) {
    const reviewedPayments = preparedPayments.map(p => {
      const status = reviews.find(c => c.intentId === p.intentId)?.status;
      return { ...p, status: status === "Released" ? "Settled" : status === "Rejected" ? "Returned" : "Held" };
    });
    return {
      ...snapshot(status), boundaryMs: preparedBoundary, opening: preparedOpening, balances: preparedBalances,
      payments: [...reviewedPayments, ...activityPayments], cases: reviews,
      markers: rewindMarkers, lastPosition: rewindSeals.at(-1)!.position,
    };
  },
  timelinePreview(position: string) {
    const seal = rewindSeals.find(s => s.position === position);
    const atMs = seal?.atMs ?? rewindOrigin + Number(position) / 12 * 20 * minute;
    return {
      ...timelinePreview(position), boundaryMs: atMs,
      balances: preparedHistory.filter(p => p.atMs === atMs).map(p => p.balance),
    };
  },
  replay(status: Parameters<typeof replay>[0], position = preparedRewind.fork.position) {
    if (status !== "Verified") return replay(status);
    const index = rewindSeals.findIndex(s => s.position === position);
    const forkIndex = index < 0 ? defaultForkIndex : index;
    return {
      jobId, status, failure: null,
      verified: {
        ...verified, horizonMs: preparedRewind.horizon, sourceRecordHash: originalHead, childRecordHash: scenarioHead,
        headlines: preparedHeadlines, breakdown: preparedHeadlines,
        payments: verified.payments.map(p => ({ ...p, intentId: primaryCaseId, principalMinor: preparedPayments[0]!.amountMinor })),
        balances: rewindSeals.flatMap((s, i) => {
          const sample = Math.min(defaultForkIndex + i - forkIndex, alternate.length - 1);
          const delta = i <= forkIndex ? 0 : alternate[sample]! - original[sample]!;
          const options = rewindBalances(original[i]! + delta);
          return rewindBalances(original[i]!).map((source, j) => ({
            atMs: s.atMs, bankId: source.bankId, source, alternative: options[j]!,
          }));
        }),
      },
    };
  },
};
