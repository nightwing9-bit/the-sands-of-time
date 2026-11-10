// HTTP wire between Revenant's Timeline adapter and the TEST-ONLY Timeline host (tests/E2E/backend, spec 04 exit gate).
// This is NOT a production Revenant API. Hosts.Api serves no Timeline route and the OpenAPI document lists none; the domain-owned
// endpoints, their authorization and DTOs arrive with the Specs 05/09, which replace createTestHostHttpWire only. The adapter and the
// shell depend on the RevenantTimelineWire interface below, never on these routes.
// The three routes (GET /timeline/{id}, GET /timeline/{id}/restore?position=, POST /timeline/{id}/fork) are served by the test host
// over the real Timeline engine and PostgreSQL. tests/E2E/real-backend.spec.ts calls them unmocked; adapters.spec.ts and
// browser-smoke.spec.ts exercise this same code against browser-edge stubs (fast, no backend), and say so.
// Cookie-authenticated mutations echo the identity module's double-submit CSRF token, as the typed REST client does.
import { CSRF_HEADER, classifyHttpFailure, classifyThrown, currentCsrfToken, parsePosition, type TimelinePosition, type TransportFailure } from "@sands/contracts";
import type { RevenantPreview, RevenantTimelineDescriptor } from "./revenantTimelineAdapter.ts";

/** What the adapter needs from a backend. Failures are thrown as `{ failure: TransportFailure }`. */
export interface RevenantTimelineWire {
  describe(timelineId: string, signal?: AbortSignal): Promise<RevenantTimelineDescriptor>;
  restore(timelineId: string, position: TimelinePosition, signal?: AbortSignal): Promise<{ position: string; state: RevenantPreview }>;
  fork(timelineId: string, position: TimelinePosition, operationKey: string, options: unknown): Promise<{ branchId: string }>;
}

export function csrfHeader(token: string | null): Record<string, string> {
  return token ? { [CSRF_HEADER]: token } : {};
}
export function fail(failure: TransportFailure): never {
  throw { failure };
}
export async function call(doFetch: typeof fetch, url: string, init: RequestInit): Promise<unknown> {
  let response: Response;
  try {
    response = await doFetch(url, { ...init, credentials: "include", cache: "no-store" });
  } catch (error) {
    return fail(classifyThrown(error, init.signal ?? undefined));
  }
  const text = await response.text();
  let body: unknown;
  try {
    body = text.length > 0 ? JSON.parse(text) : undefined;
  } catch {
    return response.ok ? fail({ kind: "malformed", message: "The response was not valid JSON." }) : fail(classifyHttpFailure(response.status, undefined, response.headers));
  }
  if (!response.ok) return fail(classifyHttpFailure(response.status, body, response.headers));
  return body;
}

export function createTestHostHttpWire(
  doFetch: typeof fetch = (...args) => fetch(...args),
  getCsrfToken: () => string | null = currentCsrfToken,
): RevenantTimelineWire {
  const json = { Accept: "application/json" };
  return {
    async describe(timelineId, signal) {
      const body = (await call(doFetch, `/timeline/${encodeURIComponent(timelineId)}`, { method: "GET", headers: json, signal })) as Partial<RevenantTimelineDescriptor> | undefined;
      const b = body?.bounds;
      if (!body || typeof body.sourceId !== "string" || typeof body.readOnly !== "boolean" || !b || parsePosition(b.start) === null || parsePosition(b.end) === null || !Array.isArray(body.markers)) {
        return fail({ kind: "malformed", message: "The timeline description was not recognised." });
      }
      return { sourceId: body.sourceId, branchId: body.branchId, bounds: b, readOnly: body.readOnly, markers: body.markers };
    },
    async restore(timelineId, position, signal) {
      const body = (await call(doFetch, `/timeline/${encodeURIComponent(timelineId)}/restore?position=${encodeURIComponent(position)}`, { method: "GET", headers: json, signal })) as { position?: unknown; state?: Partial<RevenantPreview> } | undefined;
      const businessTime = body?.state?.businessTime;
      if (typeof body?.position !== "string" || typeof businessTime !== "string") return fail({ kind: "malformed", message: "The preview was not recognised." });
      return { position: body.position, state: { businessTime, stateHash: body.state?.stateHash } };
    },
    async fork(timelineId, position, operationKey, options) {
      const body = (await call(doFetch, `/timeline/${encodeURIComponent(timelineId)}/fork`, {
        method: "POST",
        headers: { ...json, "Content-Type": "application/json", "Idempotency-Key": operationKey, ...csrfHeader(getCsrfToken()) },
        body: JSON.stringify({ position, operationKey, options: options ?? null }),
      })) as { branchId?: unknown } | undefined;
      if (typeof body?.branchId !== "string") return fail({ kind: "malformed", message: "The server did not confirm a branch." });
      return { branchId: body.branchId };
    },
  };
}
