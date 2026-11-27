import {
  audit, cases, jobId, liquidity, preparedScenario, replay, run, runId, snapshot, staffLogin, staffProfile, timelinePreview,
} from "./fixtures.ts";

const runsPath = "/revenant/r8/runs";
const runPath = `${runsPath}/${runId}`;

// One local fixture scenario. All writes are in memory and launching resets it.
export function createDemoFetch(staff = staffProfile, prepared = false): typeof fetch {
  const scenario = prepared ? preparedScenario : { audit, cases, liquidity, replay, snapshot, timelinePreview };
  let status: "Active" | "Verified" = "Active";
  let reviews: (typeof cases)[number][] = scenario.cases.slice();
  let readyAt: number | null = null;
  let forkPosition: string | undefined;
  let cancelled = false;
  const current = () => ({ ...scenario.snapshot(status, reviews), cases: reviews });

  return async (input, init) => {
    const request = new Request(typeof input === "string" ? new URL(input, "http://revenant.demo.invalid") : input, init);
    request.signal.throwIfAborted();
    const path = new URL(request.url).pathname;
    const send = (value: unknown, code = 200) => Response.json(value, { status: code });

    switch (`${request.method} ${path}`) {
      case "POST /auth/staff/login": return send(staffLogin);
      case "GET /auth/staff/me": return send(staff);
      case "POST /auth/staff/logout": return send(null);
      case `GET ${runsPath}`: return send([run]);
      case `POST ${runsPath}`:
        status = "Active";
        reviews = scenario.cases.slice();
        readyAt = null;
        forkPosition = undefined;
        cancelled = false;
        return send(run);
      case `GET ${runPath}`: return send(current());
      case `GET ${runPath}/liquidity`: return send(scenario.liquidity);
      case `GET ${runPath}/audit`: return send(scenario.audit);
      case `POST ${runPath}/close`:
        status = "Verified";
        return send(current());
      case `POST ${runPath}/counterfactual`:
        if (prepared) ({ forkPosition } = await request.json());
        readyAt = Date.now() + 2500;
        cancelled = false;
        return send(scenario.replay("Pending"));
      case `GET ${runPath}/counterfactual/${jobId}`:
        if (readyAt === null) return send({ title: "Replay has not started" }, 404);
        return send(scenario.replay(cancelled ? "Cancelled" : Date.now() >= readyAt ? "Verified" : "Pending", forkPosition));
      case `POST ${runPath}/counterfactual/${jobId}/cancel`:
        cancelled = true;
        return send(scenario.replay("Cancelled"));
    }

    const position = path.startsWith(`${runPath}/timeline/`) ? path.slice(`${runPath}/timeline/`.length) : "";
    if (request.method === "GET" && /^\d+$/.test(position)) return send(scenario.timelinePreview(position));

    if (request.method === "POST" && path.startsWith(`${runPath}/cases/`)) {
      try {
        const { action, expectedVersion } = await request.json();
        const review = reviews.find(c => path === `${runPath}/cases/${c.intentId}`);
        if (!review) return send({ title: "No response fixture for this case" }, 404);
        if (expectedVersion !== review.version || !["Open", "AwaitingSecondApproval"].includes(review.status) || (action !== "Approve" && action !== "Reject")) {
          return send({ title: "Invalid fixture decision" }, 409);
        }
        const approvalCount = review.approvalCount + Number(action === "Approve");
        const receipt = { ...review, approvalCount, version: review.version + 1,
          status: action === "Reject" ? "Rejected" : approvalCount >= review.requiredApprovals ? "Released" : "AwaitingSecondApproval" };
        reviews = reviews.map(c => c === review ? receipt : c);
        return send(receipt);
      } catch {
        return send({ title: "Invalid fixture request" }, 400);
      }
    }
    return send({ title: "No response fixture for this request" }, 404);
  };
}
