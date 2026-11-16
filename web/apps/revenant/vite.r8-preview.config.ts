import { defineConfig, type Connect } from "vite";
import react from "@vitejs/plugin-react";
import {
  audit, cases, jobId, liquidity, replay, run, runId, snapshot, staffLogin, staffProfile, timelinePreview,
} from "../../../tests/E2E/revenant-r8-m2.fixtures.ts";

const runsPath = "/revenant/r8/runs";
const runPath = `${runsPath}/${runId}`;
const host = "127.0.0.1";

// One local fixture scenario. All writes are in memory and launching resets it.
function fixtureApi(): Connect.NextHandleFunction {
  let status: "Active" | "Verified" = "Active";
  let reviews = cases.slice();
  let readyAt: number | null = null;
  let cancelled = false;
  const current = () => ({ ...snapshot(status), cases: reviews });

  return async (req, res, next) => {
    const path = req.url?.split("?", 1)[0] ?? "";
    const send = (value: unknown, code = 200) => {
      res.writeHead(code, { "Content-Type": "application/json", "Cache-Control": "no-store" });
      res.end(JSON.stringify(value));
    };
    if (!/^\/(auth|revenant|timeline|meta|hubs)(\/|$)/.test(path)) return next();
    if (req.headers.origin && req.headers.origin !== `http://${req.headers.host}`) {
      return send({ title: "Use the local preview origin" }, 403);
    }

    switch (`${req.method} ${path}`) {
      case "POST /auth/staff/login": return send(staffLogin);
      case "GET /auth/staff/me": return send(staffProfile);
      case "POST /auth/staff/logout": return send(null);
      case `GET ${runsPath}`: return send([run]);
      case `POST ${runsPath}`:
        status = "Active";
        reviews = cases.slice();
        readyAt = null;
        cancelled = false;
        return send(run);
      case `GET ${runPath}`: return send(current());
      case `GET ${runPath}/liquidity`: return send(liquidity);
      case `GET ${runPath}/audit`: return send(audit);
      case `POST ${runPath}/close`:
        status = "Verified";
        return send(current());
      case `POST ${runPath}/counterfactual`:
        readyAt = Date.now() + 2500;
        cancelled = false;
        return send(replay("Pending"));
      case `GET ${runPath}/counterfactual/${jobId}`:
        if (readyAt === null) return send({ title: "Replay has not started" }, 404);
        return send(replay(cancelled ? "Cancelled" : Date.now() >= readyAt ? "Verified" : "Pending"));
      case `POST ${runPath}/counterfactual/${jobId}/cancel`:
        cancelled = true;
        return send(replay("Cancelled"));
    }

    const position = path.startsWith(`${runPath}/timeline/`) ? path.slice(`${runPath}/timeline/`.length) : "";
    if (req.method === "GET" && /^\d+$/.test(position)) return send(timelinePreview(position));

    if (req.method === "POST" && path.startsWith(`${runPath}/cases/`)) {
      let body = "";
      try {
        for await (const chunk of req) body += chunk;
        const { action, expectedVersion } = JSON.parse(body);
        const review = reviews.find(c => path === `${runPath}/cases/${c.intentId}`);
        if (!review) return send({ title: "No response fixture for this case" }, 404);
        if (expectedVersion !== review.version || (action !== "Approve" && action !== "Reject")) {
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

export default defineConfig(({ command, isPreview }) => {
  if (command !== "serve" || isPreview) throw new Error("The R8 fixture preview requires the Vite development server.");
  return {
    cacheDir: "../../node_modules/.vite/revenant-r8-preview",
    define: { "import.meta.env.R8_PREVIEW_TOKEN": JSON.stringify(staffLogin.accessToken) },
    plugins: [react(), {
      name: "r8-visual-preview",
      configureServer(server) {
        if (server.config.server.host !== host) throw new Error("The R8 fixture preview is loopback-only.");
        server.middlewares.use(fixtureApi());
      },
    }],
    server: { host, port: 5173, strictPort: true, cors: false },
  };
});
