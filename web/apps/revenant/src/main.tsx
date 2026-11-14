import { lazy, Suspense, StrictMode, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { createMarrowTransport, describeFailure, describeHandshake, type ApiResult, type HandshakeVerdict, type TimelinePosition, type TransportFailure } from "@sands/contracts";
import { TimelineScrubber, type VerifiedPreview } from "@sands/timeline-ui";
import { createRevenantAdapter, type RevenantTimelineDescriptor, type RevenantPreview } from "./revenantTimelineAdapter.ts";
import { formatBusinessTime } from "./revenantTimelineAdapter.ts";
import { createTestHostHttpWire } from "./revenantTimelineWire.ts";
import { FridaySlice } from "./slice/FridaySlice.tsx";
import { createSliceHttpWire } from "./slice/sliceWire.ts";
// Same-origin: Vite (dev) or the reverse proxy (deployed) forwards /meta, /auth, /timeline and /hubs to the API and Realtime hosts.
// Both shells build their typed clients from this one @sands/contracts factory and mount the one @sands/timeline-ui scrubber.
const transport = createMarrowTransport({ apiBaseUrl: "", realtimeBaseUrl: "" });
const wire = createTestHostHttpWire();
const sliceWire = createSliceHttpWire();
const R8 = lazy(() => import("./r8/RevenantR8.tsx").then(m => ({ default: m.RevenantR8 })));
const useR8 = new URLSearchParams(window.location.search).get("view") === "r8";
const timelineId = new URLSearchParams(window.location.search).get("timeline");

type Loaded = { kind: "loading" } | { kind: "failed"; failure: TransportFailure } | { kind: "ready"; descriptor: RevenantTimelineDescriptor };

function TimelinePanel({ id }: { id: string }) {
  const [loaded, setLoaded] = useState<Loaded>({ kind: "loading" });
  useEffect(() => {
    const controller = new AbortController();
    void wire.describe(id, controller.signal).then(
      (descriptor) => !controller.signal.aborted && setLoaded({ kind: "ready", descriptor }),
      (error: { failure?: TransportFailure }) => !controller.signal.aborted && setLoaded({ kind: "failed", failure: error.failure ?? { kind: "server", status: 0 } }),
    );
    return () => controller.abort();
  }, [id]);
  if (loaded.kind === "loading") return <p role="status">Loading timeline…</p>;
  if (loaded.kind === "failed") return <p role="alert" data-testid="timeline-load-error" data-kind={loaded.failure.kind}>{describeFailure(loaded.failure)}</p>;
  return <Mounted id={id} descriptor={loaded.descriptor} />;
}

function Mounted({ id, descriptor }: { id: string; descriptor: RevenantTimelineDescriptor }) {
  const adapter = useMemo(() => createRevenantAdapter({ timelineId: id, descriptor, wire }), [id, descriptor]);
  // The parent owns only server-verified values: nothing is shown as verified until the server answered.
  const [position, setPosition] = useState<TimelinePosition>(descriptor.bounds.start);
  const [preview, setPreview] = useState<VerifiedPreview<RevenantPreview> | null>(null);
  const [branchId, setBranchId] = useState<string | null>(null);
  return (
    <>
      <TimelineScrubber
        adapter={adapter}
        position={position}
        onVerified={(p) => { setPosition(p.position); setPreview(p); }}
        onForked={(result) => setBranchId(result.branchId)}
      />
      {preview ? <p data-testid="verified-state">Verified business time: {formatBusinessTime(preview.state.businessTime) ?? preview.state.businessTime}</p> : <p data-testid="verified-state">No position verified by the server yet.</p>}
      {branchId ? <p data-testid="fork-result" role="status">Server created branch {branchId}.</p> : null}
    </>
  );
}

function Shell() {
  const [handshake, setHandshake] = useState<ApiResult<HandshakeVerdict> | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void transport.negotiate(controller.signal).then((result) => {
      if (!controller.signal.aborted) setHandshake(result);
    });
    return () => controller.abort();
  }, []);
  const compatible = handshake?.ok === true && handshake.value.compatible;
  return (
    <main data-testid="app-shell">
      <h1>Revenant</h1>
      <p data-testid="contract-status" role="status">{describeHandshake(handshake)}</p>
      {/* ?timeline=<id> keeps the generic history inspector (spec 04). Without it, the Friday Ring V1 slice (spec 05, task 5.4). */}
      {!compatible ? null : timelineId ? <TimelinePanel id={timelineId} /> : <FridaySlice transport={transport} wire={sliceWire} />}
    </main>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {useR8 ? <Suspense fallback={<p role="status">Loading Revenant…</p>}><R8 /></Suspense> : <Shell />}
  </StrictMode>,
);
