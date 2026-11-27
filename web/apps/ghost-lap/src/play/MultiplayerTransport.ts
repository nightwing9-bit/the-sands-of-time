import { createAuthenticatedHubConnection, createMarrowTransport, HubInputSubject } from "@sands/contracts";
import { ARENA_V1_RENDER } from "../arena/arenaV1Descriptor.ts";
import type { ActorSlot, Charge, ControlIntent } from "../presentation/types.ts";
import type { ConnectionState, InputAnswerView, PlayFrame, PlayEventKind, PlayTransport } from "./playTypes.ts";
import { NEUTRAL } from "./keyboardIntent.ts";
import { failureText, HubCallError, type GameplayInput, type JoinedRoom, type MatchOutcomeWire, type RoomCommandResult, type RoomView, type SnapshotFrame } from "./multiplayerWire.ts";

const kinds: Record<string, PlayEventKind> = { candle_collected: "CandleCollected", overlit_started: "OverlitStarted", contact_drain: "ContactDrain",
  burst_manual: "BurstManual", burst_auto: "BurstAuto", burst_hit: "BurstHit", overlit_cancelled: "OverlitCancelled", round_finished: "RoundFinished" };
export function multiplayerFrame(f: SnapshotFrame, at = performance.now(), roster: { slot: number; memberId: string }[] = []): PlayFrame {
  const s = f.snapshot, identity = `${s.matchId}:${s.generationId}`;
  return { identity, tick: s.tick, snapshotSequence: s.sequence, tickRate: 30, roundTicks: 2700, final: s.isFinal, receivedAtMs: at,
    players: s.players.map(p => ({ ...p, memberId: roster.find(m => m.slot === p.slot)?.memberId, slot: p.slot as ActorSlot, charge: p.charge as Charge, x: p.xFixed / 1024, y: p.yFixed / 1024,
      overlit: p.charge === 4, overlitDeadlineTick: p.charge === 4 ? p.overlitDeadlineTick : null, appliedSeq: p.lastSequence,
      graceEndsTick: p.status === "grace" ? p.graceEndsTick ?? null : null })),
    you: { slot: f.you.slot as ActorSlot | -1, canControl: f.you.canControl }, candles: f.visibleCandles ?? [],
    events: f.events.items.map(e => ({ ...e, key: `${identity}:${e.key}`, raw: e.kind, kind: kinds[e.kind] ?? "Unknown", candleId: e.candleId ?? "" })),
    eventsComplete: !f.events.truncated };
}

export class MultiplayerTransport implements PlayTransport {
  get label() { return `PRODUCTION HUB · ${this.latest?.you.canControl ? `P${this.latest.you.slot + 1} controlling` : this.joined?.role === "spectator" ? "spectator · read only" : "awaiting control"}`; }
  readonly production = true;
  readonly hub: ReturnType<typeof createAuthenticatedHubConnection>;
  private frames = new Set<(f: PlayFrame) => void>();
  private connections = new Set<(s: ConnectionState) => void>();
  private answers = new Set<(a: InputAnswerView) => void>();
  private stream: HubInputSubject<GameplayInput> | null = null;
  private pending = new Map<number, { sentAtMs: number; burstRequested: boolean }>();
  private current: ControlIntent = NEUTRAL;
  private latest: SnapshotFrame | null = null;
  private seq = -1;
  private lifecycleFloor = 0;
  private at = 0;
  private disposed = false;
  /** Set when this class itself stops the hub after a failure, so the close it causes cannot overwrite the real reason. */
  private closing = false;
  private locked = false;
  private fingerprint = "";
  private refreshingRound = false;
  private retired = new Set<string>();
  private timer?: ReturnType<typeof setInterval>;
  private roomTimer?: ReturnType<typeof setInterval>;
  private pollingRoom = false;
  joined: JoinedRoom | null = null;
  onRoom: (room: JoinedRoom) => void = () => {};
  /** The room no longer exists for this member (closed, expired, or this member was removed). Reported once. */
  onRoomClosed: (reason: string) => void = () => {};
  private roomClosedReported = false;
  private readonly setup: { code?: string; spectator?: boolean; pollRoom?: boolean; baseUrl?: string };
  /**
   * `pollRoom` keeps the room view fresh by itself (the lobby, countdown and rematch are driven by it; the hub pushes no room events).
   * `baseUrl` is empty (same origin, the Vite proxy or the reverse proxy) everywhere except a unit test, which has no origin.
   */
  constructor(setup: { code?: string; spectator?: boolean; pollRoom?: boolean; baseUrl?: string } = {}) {
    this.setup = setup;
    this.hub = createAuthenticatedHubConnection({ baseUrl: setup.baseUrl ?? "", hubPath: "/hubs/ghostlap", keepAliveMs: 5000, serverTimeoutMs: 15000, reconnectDelaysMs: [0, 1000, 3000, 5000] });
    this.hub.on("Snapshot", (f: SnapshotFrame) => this.receive(f));
    this.hub.on("InputsApplied", (ack: { matchId: string; controllerEpoch: number; lastAppliedSeq: number }) => {
      if (ack.matchId !== this.latest?.snapshot.matchId || ack.controllerEpoch !== this.latest.you.controllerEpoch) return;
      this.seq = Math.max(this.seq, ack.lastAppliedSeq);
      this.answerThrough(ack.lastAppliedSeq);
    });
    this.hub.on("InputRejected", (r: { seq: number; code: string; sequenceFloor?: number }) => {
      this.seq = Math.max(this.seq, r.sequenceFloor ?? -1);
      this.answer(r.seq, false, r.code); // Never replay a rejected Burst edge.
    });
    this.hub.on("ControllerSuperseded", () => this.loseControl("Controller superseded by another session"));
    this.hub.on("RoomClosed", () => { this.loseControl("Room closed"); this.state({ kind: "ended", reason: "Room closed" }); this.reportRoomClosed("The room was closed."); });
    this.hub.on("MatchControl", (n: { matchId: string; generationId: string; lifecycleVersion: number; code: string }) => {
      if (n.matchId !== this.latest?.snapshot.matchId || n.generationId !== this.latest.snapshot.generationId) return;
      if (n.lifecycleVersion < this.lifecycleFloor) return;
      this.lifecycleFloor = n.lifecycleVersion;
      if (n.code === "forfeited") this.loseControl("Slot forfeited");
      if (n.code === "control_overflow") void this.resync().catch(e => this.fail(e));
    });
    this.hub.onreconnecting(() => { this.current = NEUTRAL; this.stream = null; this.locked = true; this.state({ kind: "degraded", reason: "Reconnecting; input released", sinceMs: this.at || null }); });
    this.hub.onreconnected(() => { void this.recover().catch(e => this.fail(e)); });
    this.hub.onclose(() => { if (!this.disposed && !this.closing) this.fail("Connection closed after bounded reconnection attempts"); });
  }
  onFrame(l: (f: PlayFrame) => void) { this.frames.add(l); return () => { this.frames.delete(l); }; }
  onConnection(l: (s: ConnectionState) => void) { this.connections.add(l); return () => { this.connections.delete(l); }; }
  onInputAnswer(l: (a: InputAnswerView) => void) { this.answers.add(l); return () => { this.answers.delete(l); }; }
  private state(s: ConnectionState) { for (const l of this.connections) l(s); }
  private fail(e: unknown) { this.locked = true; this.current = NEUTRAL; this.state({ kind: "failed", reason: failureText(e) }); }
  private reportRoomClosed(reason: string) { if (this.roomClosedReported || this.disposed) return; this.roomClosedReported = true; this.onRoomClosed(reason); }
  async call<T>(method: string, ...args: unknown[]): Promise<T> {
    const r = await this.hub.invoke<{ ok: boolean; code: string | null; value: T }>(method, ...args);
    if (!r.ok) throw new HubCallError(method, r.code ?? "unknown");
    return r.value;
  }
  async start(signal: AbortSignal) {
    signal.addEventListener("abort", () => this.dispose(), { once: true });
    try {
      const http = createMarrowTransport({ apiBaseUrl: "", realtimeBaseUrl: "" });
      const check = await http.negotiate(signal);
      if (!check.ok || !check.value.compatible) throw new Error("API contract unavailable/incompatible");
      const me = await http.api.get("/auth/guest/me");
      if (!me.ok) { const guest = await http.api.post("/auth/guest/start"); if (!guest.ok) throw new Error("Guest authentication failed"); }
      if (this.disposed || signal.aborted) return;
      await this.hub.start();
      if (this.disposed) { await this.hub.stop(); return; }
      const arena = await this.call<{ layoutId: string; layoutFingerprint: string; width: number; height: number; obstacles: typeof ARENA_V1_RENDER.obstacles }>("GetArena");
      if (arena.layoutId !== "arena-v1" || arena.width !== 1600 || arena.height !== 900 || JSON.stringify(arena.obstacles) !== JSON.stringify(ARENA_V1_RENDER.obstacles)) throw new Error("Arena layout mismatch; refusing play");
      this.fingerprint = arena.layoutFingerprint;
      this.joined = await this.call<JoinedRoom>(this.setup.code ? "JoinRoom" : "CreateRoom", this.setup.code
        ? { requestId: crypto.randomUUID(), code: this.setup.code, asSpectator: !!this.setup.spectator } : { requestId: crypto.randomUUID() });
      if (this.disposed) return;
      this.onRoom(this.joined);
      this.openStream();
      this.timer = setInterval(() => this.send(), 90);
      if (this.setup.pollRoom) this.roomTimer = setInterval(() => void this.pollRoom(), 600);
      // A spectator/rejoining controller may enter an already-running match.
      await this.refreshRoom();
      if (this.joined.view.matchStatus === "running") await this.resync();
    } catch (e) {
      if (this.disposed) return;
      this.closing = true;
      clearInterval(this.timer); clearInterval(this.roomTimer);
      this.fail(e);
      await this.hub.stop();
    }
  }
  private openStream() {
    const stream = new HubInputSubject<GameplayInput>(); this.stream = stream;
    void this.hub.invoke<{ ok: boolean; code: string | null; value: { endReason: string } | null }>("StreamInputs", stream)
      .then(reply => { if (!this.disposed && this.stream === stream) this.fail(`Input stream ended: ${reply.code ?? reply.value?.endReason ?? "unknown"}`); })
      .catch(e => { if (!this.disposed && this.stream === stream) this.fail(e); });
  }
  /** Newest room view wins (the version is the lifecycle order); a slow reply can never roll the lobby back. */
  private adopt(view: RoomView) {
    if (!this.joined || view.roomId !== this.joined.view.roomId || view.version < this.joined.view.version) return;
    this.joined.view = view;
    this.onRoom(this.joined);
  }
  async refreshRoom() {
    if (!this.joined) return;
    this.adopt(await this.call<RoomView>("GetRoom", this.joined.view.roomId));
  }
  private async pollRoom() {
    if (this.pollingRoom || this.disposed || !this.joined) return;
    this.pollingRoom = true;
    try { await this.refreshRoom(); }
    catch (e) { if (e instanceof HubCallError && e.code === "room_unavailable") this.reportRoomClosed(failureText(e)); /* anything else is transient: the next poll retries */ }
    finally { this.pollingRoom = false; }
  }
  private async roomCommand(method: "SetReady" | "StartCountdown" | "CancelCountdown" | "Rematch", extra: object = {}) {
    if (!this.joined) throw new HubCallError(method, "room_unavailable");
    const result = await this.call<RoomCommandResult>(method, { requestId: crypto.randomUUID(), roomId: this.joined.view.roomId, ...extra });
    this.adopt(result.view);
    return result;
  }
  /** Kept for the dev day-play screen. The journey screen uses the explicit methods below. */
  async command(method: "SetReady" | "StartCountdown") { await this.roomCommand(method, method === "SetReady" ? { ready: true } : {}); }
  setReady(ready: boolean) { return this.roomCommand("SetReady", { ready }); }
  startCountdown() { return this.roomCommand("StartCountdown"); }
  cancelCountdown() { return this.roomCommand("CancelCountdown"); }
  /** Competitor-only, after a settled match. The server returns the room to its lobby; the next start seals a new match id, seed and generation. */
  async rematch() {
    const result = await this.roomCommand("Rematch");
    if (this.latest) this.retired.add(`${this.latest.snapshot.matchId}:${this.latest.snapshot.generationId}`); // a late frame of the old match is never drawn again
    this.latest = null; this.pending.clear(); this.current = NEUTRAL; this.locked = true; this.lifecycleFloor = 0;
    return result;
  }
  /** Durable result of a match this member may read (sealed roster, or current room member). */
  getResult(matchId: string) { return this.call<MatchOutcomeWire>("GetMatchResult", matchId); }
  get matchId(): string | null { return this.latest?.snapshot.matchId ?? null; }
  async resync() { this.receive(await this.call<SnapshotFrame>("Resync")); }
  private async recover() {
    if (!this.joined || this.disposed) return;
    this.pending.clear(); this.current = NEUTRAL;
    this.joined = await this.call<JoinedRoom>("JoinRoom", { requestId: crypto.randomUUID(), code: this.joined.roomCode, asSpectator: this.joined.role === "spectator" });
    this.onRoom(this.joined); this.openStream(); await this.refreshRoom();
    if (this.joined.view.matchStatus === "running") await this.resync();
  }
  private receive(f: SnapshotFrame) {
    if (this.disposed) return;
    const s = f.snapshot, previous = this.latest;
    if (previous?.snapshot.matchId === s.matchId && f.lifecycleVersion < this.lifecycleFloor) return;
    const id = `${s.matchId}:${s.generationId}`;
    if (this.retired.has(id)) return;
    if (!this.joined) return; // Join may push before its invocation reply; the explicit Resync recovers that baseline.
    const same = !!previous && previous.snapshot.matchId === s.matchId && previous.snapshot.generationId === s.generationId;
    if (same && (s.sequence < previous!.snapshot.sequence || s.tick < previous!.snapshot.tick || f.lifecycleVersion < previous!.lifecycleVersion)) return;
    if (same && s.sequence === previous!.snapshot.sequence && !f.isResync) return;
    const round = this.joined.view.round;
    if (!round || round.matchId !== s.matchId || round.authorityGeneration !== s.generationId) {
      // The polled room view is older than this match (a rematch just started, or Join pushed before its reply): read it once, then decide.
      if (!this.refreshingRound) {
        this.refreshingRound = true;
        void this.refreshRoom().then(() => {
          this.refreshingRound = false;
          const fresh = this.joined?.view.round;
          if (fresh && fresh.matchId === s.matchId && fresh.authorityGeneration === s.generationId) this.receive(f);
          else if (fresh) this.fail("Match/layout identity mismatch"); // the room names a DIFFERENT sealed round than this frame: refuse it
          // no sealed round at all: the room is back in its lobby and this is a late frame of a finished match; nothing to draw
        }).catch(e => { this.refreshingRound = false; this.fail(e); });
      }
      return;
    }
    if (s.layoutId !== "arena-v1" || s.rulesId !== "ghost-mp-v2" || round.layoutFingerprint !== this.fingerprint) { this.fail("Match/layout identity mismatch"); return; }
    if (!same) {
      if (previous) this.retired.add(`${previous.snapshot.matchId}:${previous.snapshot.generationId}`);
      this.pending.clear(); this.seq = -1; this.current = NEUTRAL; this.lifecycleFloor = 0;
    }
    this.lifecycleFloor = Math.max(this.lifecycleFloor, f.lifecycleVersion);
    this.latest = f; this.at = performance.now(); this.seq = Math.max(this.seq, f.you.appliedSeq);
    this.locked = !f.you.canControl || f.matchStatus !== "running";
    this.answerThrough(f.you.appliedSeq);
    if (this.locked) this.current = NEUTRAL;
    for (const l of this.frames) l(multiplayerFrame(f, this.at, round.slots));
    this.state(s.isFinal || f.matchStatus !== "running" ? { kind: "ended", reason: `Match ${f.matchStatus}` } : { kind: "live" });
  }
  private loseControl(reason: string) {
    this.setIntent(NEUTRAL, "neutral"); this.locked = true;
    if (this.latest) { this.latest = { ...this.latest, you: { ...this.latest.you, canControl: false } }; for (const l of this.frames) l(multiplayerFrame(this.latest, performance.now(), this.joined?.view.round?.slots)); }
    this.state({ kind: "degraded", reason, sinceMs: this.at || null });
  }
  setIntent(intent: ControlIntent, _reason: "change" | "neutral") { this.current = this.locked ? NEUTRAL : intent; this.send(); }
  private send() {
    const f = this.latest;
    if (this.disposed || this.locked || !f?.you.canControl || f.snapshot.isFinal || !this.stream || performance.now() - this.at > 500) return;
    const seq = ++this.seq;
    this.pending.set(seq, { sentAtMs: performance.now(), burstRequested: this.current.requestBurst });
    if (this.pending.size > 128) this.pending.delete(this.pending.keys().next().value!);
    this.stream.next({ ...this.current, seq, matchId: f.snapshot.matchId, controllerEpoch: f.you.controllerEpoch,
      clientTick: f.snapshot.tick + 1 + Math.floor((performance.now() - this.at) * 30 / 1000) });
  }
  private answerThrough(seq: number) { for (const n of this.pending.keys()) if (n <= seq) this.answer(n, true, null); }
  private answer(seq: number, accepted: boolean, rejection: string | null) {
    const p = this.pending.get(seq); if (!p) return; this.pending.delete(seq);
    for (const l of this.answers) l({ seq, accepted, rejection, ...p, answeredAtMs: performance.now() });
  }
  dispose() {
    if (this.disposed) return;
    this.setIntent(NEUTRAL, "neutral"); this.disposed = true; clearInterval(this.timer); clearInterval(this.roomTimer);
    this.stream?.complete(); this.stream = null;
    void (async () => { try { if (this.joined) await this.call("LeaveRoom", { requestId: crypto.randomUUID(), roomId: this.joined.view.roomId }); } catch { /* Disconnect still neutralizes server control. */ } finally { await this.hub.stop(); } })();
    this.frames.clear(); this.connections.clear(); this.answers.clear(); this.pending.clear();
  }
}
