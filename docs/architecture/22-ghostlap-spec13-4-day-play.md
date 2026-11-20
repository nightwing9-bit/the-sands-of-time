# Spec 13.4: connect and play one smooth, authoritative daytime game

**CREATIVE IMPLEMENTATION DONE · HUMAN REVIEW PENDING · MULTIPLAYER TECHNICAL PLAYABILITY GATE MET (19 November 2026).**

The integration addendum below supersedes the original blocked status and transport/geometry handoff items. The original implementation notes are retained as history; production Solo remains separate.
Existing `web/apps/ghost-lap` only. No Marrow, rules, simulation, scoring, collision, Burst, record, replay, authorization or fork change. No mobile controls. No invented assets.

## What exists now

| Piece | File | Notes |
|---|---|---|
| Transport seam (presentation types, not DTOs) | `src/play/playTypes.ts` | `PlayTransport`, `PlayFrame`, `PlayEvent`, `ConnectionState`, `InputAnswerView`. The production adapter must implement exactly this. |
| Keyboard | `src/play/keyboardIntent.ts` | WASD/arrows + Space. Burst press and release are separate reported changes. Neutral on blur, hidden tab, dispose, `CanControl=false`. Ctrl/Meta/Alt ignored. |
| Smoothing (pure) | `src/play/visualSmoothing.ts` | `RemoteTrack`: interpolates others in the past (1.5 × observed gap, 66 to 200 ms), holds at the newest snapshot, never extrapolates, holds across a discontinuity. `LocalSmoother`: eases toward the newest AUTHORITATIVE position only (no lead, no prediction), snaps beyond 80 units. Presets Raw / Light (35 ms) / Smooth (70 ms). |
| Session | `src/play/PlaySession.ts` | Orders frames (stale ignored, new identity resets), dedupes events by key, queues cues for the victim only, Burst feedback `requested → admitted → confirmed` (only the server's own `BurstManual` event confirms) or `refused` / `not_confirmed` with the real charge. Stall watchdog (700 ms) flips the HUD to "Connection trouble" even if the transport says live. |
| Scene | `src/play/PlayScene.ts` | One ticker callback in the one Pixi world. `CharacterView` from 13.3. Candles are **placeholder vector markers** (13.5 owns the art). Optional "Server position" outline = newest authoritative 48×48 collider. |
| HUD | `src/screens/PlayScreen.tsx` + `styles/screens.css` | Timer (derived from tick), score + parts, Soulfire n/4, Overlit with derived seconds, event feed, Burst line, connection pill, banner, transport label. Edge zones only. |
| Dev-only bridge | `src/play/testHostBridge.ts` | `PlayTransport` over the existing test-host `/ghost-lap/*` routes. Real ghost-v1 authority. Not in the production bundle (verified: no `TEST-HOST BRIDGE` string in `dist`). |
| Route | `?screen=dayPlay` (DEV only) or the "Day play" preview button | Screen id `dayPlay` in `app/screens.ts`. |

## Honest status on screen

The start card always says **PLAYABILITY BLOCKED for the real game** (no production adapter). In DEV it adds the test-host bridge start button. While running, the HUD source tag reads "TEST-HOST BRIDGE (dev only, HTTP polling, real ghost-v1 authority, not the production hub)". `eventsComplete=false` on the bridge shows "Event log may skip ticks".

## Try it

1. Host (needs Docker for PostgreSQL), from `tests/E2E`:
   `dotnet build backend/E2E.Backend.csproj --configuration Release --nologo --verbosity quiet`
   `E2E_FIXTURE_TOKEN=local-dev-play-token-1234567890 E2E_BACKEND_PORT=8081 E2E_BACKEND_MAX_SECONDS=7200 ASPNETCORE_ENVIRONMENT=Testing dotnet backend/bin/Release/net10.0/E2E.Backend.dll`
   Port 8081 is the Vite default API target, so the already running dev server needs no restart. The host ends itself after `E2E_BACKEND_MAX_SECONDS`.
2. Vite (hot reload): `npm run dev -w @sands/ghost-lap` from `web/` (port 5174; reuse the running one).
3. Open `http://localhost:5174/?screen=dayPlay` (optional `&seed=7&mascot=0..3`), press **Start test-host run**. Move with WASD/arrows, Burst with Space (needs 4/4).

The "Feel review (dev)" panel shows measured input RTT, key→server, key→drawn, frame gaps and refusals, and has Raw/Light/Smooth, head size 64/56/48 and the Server position outline. It is a dev tool and collapses.

## Questions for the user (judge by playing)

1. **Steering and direction changes near walls:** which smoothing (Raw / Light / Smooth) feels best, and does any of them feel late or floaty?
2. **Head size 64 against the 48×48 collider:** does the head ever look like it passes through, or stops short of, a wall? Turn on "Server position" to compare; is 56 or 48 better?
3. **HUD truth and readability:** can you read timer, score, Soulfire and the Burst line while playing, and does anything claim more than the server said (for example the Burst line before the Burst resolves)?

Record the answers and one or two targeted tweaks here. **HUMAN REVIEW PENDING; no approval is claimed.**

## Measurements actually taken (not FPS claims)

* Host cadence without any browser rendering (`/tmp` node script, 8 s, polling the real host): 241 ticks seen (expected about 240), per-tick spacing mean 33.1 ms, p95 41 ms, max 50 ms, no gap above 100 ms, poll round trip mean 2.1 ms.
* Same host driven end to end through `TestHostBridge` + `PlaySession` in Node by a steering bot: 4 candles collected, Overlit started at tick 113, Space sent, `BurstManual` event received at tick 114/115, session reported `confirmed`; key→server 25 ms, key→drawn 28 ms.
* Headless Chromium with **software GL** at 1600×900 showed frame gaps up to about 360 ms and key→drawn 130 to 400 ms. The host cadence above is smooth, so this comes from software rendering and says nothing about a real GPU browser. **No real-browser feel or FPS number exists yet; the user's HUD readout is the first.**
* Real refusals seen: `commit_pending` (benign, documented as "client resubmits"; count shown in the HUD) and occasional `late_tick`. A refused Burst press is re-sent up to 4 times within 400 ms by the bridge (`TRANSIENT_REFUSALS`), then reported as refused. Before this fix one real Space press was refused with `commit_pending` and silently lost.

## Decisions

* **No prediction.** The local head eases toward the newest authoritative position and never leads it. Adding limited prediction is only justified if the user reports lag that the readout confirms; none was added on theory.
* **Event identity for solo:** `${runId}:${tick}:${index}` (13.0 map A6). Multiplayer adapters must supply `MatchEvent.Key` scoped by MatchId + GenerationId.
* **Release face length is PROVISIONAL:** 450 ms (`RELEASE_VISUAL_MS`) from the authoritative Burst cue. The server defines none; 13.3's open-ended rule would otherwise leave the scream on screen until the next charge change. Needs a human verdict or a backend decision.
* **Bridge refuses mismatched geometry:** the host's `/ghost-lap/arena` obstacles must equal `ARENA_V1_RENDER`, otherwise the run is not started.
* Test-host `/neutral` is used for blur; production must send an ordered neutral input over its stream (13.0 map A3).

## External handoff for the main agent (not Opus work)

| # | Missing contract / implementation | Source evidence | Required behaviour | Smallest success check | Creative slice can continue? |
|---|---|---|---|---|---|
| 1 | **Production hub adapter** implementing `PlayTransport` for `/hubs/ghostlap` (multiplayer, richer feed first) and later `/hubs/ghostlap-solo` | No browser reference to either hub (grep of `web/`); `GhostLapHub`, `GhostLapSoloHub`; contract map 17 A4/A5/A6 | Guest cookie + CSRF + Origin, SignalR game-hub connection with 5 s keepalive / 15 s timeout, client-to-server `StreamInputs`, `Snapshot`, `InputsApplied`, `InputRejected`, `ControllerSuperseded`, `Resync`; frames carry real MatchId + GenerationId, `Snapshot.Tick`, `MatchEvent.Key`, `You.CanControl`, applied sequence | Two guests create/join/ready/start a room; one browser tab renders its own slot and the other as a remote interpolated head with real `appliedSeq` and event keys; a third tab as spectator shows `canControl=false`; blur sends a neutral input | Yes (done against the bridge) |
| 2 | **Source-backed arena and candle plan delivery** | 13.0 map A2: `MatchSnapshot`/solo DTOs carry candle IDs only; `/ghost-lap/arena` and candle coordinates are test-host only | A read projection (or verified generated config) of `ArenaV1` obstacles and `CandlePlan.Create(seed)` coordinates keyed by layout identity and seed; adapter resolves `ActiveCandleIds` to positions and telegraph phase | For a sealed round seed, the client plan equals the C# plan for all candle IDs; layout fingerprint mismatch refuses to draw | Yes (bridge reads the test-host values; `ARENA_V1_RENDER` is still a static fixture) |
| 3 | **Richer Solo Original frame projection** | `SoloRunRegistry.PushFrame` fills 7 fields; events, `ActiveCandleIds`, held input, score parts, `AckedInputSeq` stay default | Original live frames carry what Challenge frames carry (events with tick+index, candles, applied sequence) | An Original run shows candle pickups and Burst events in the HUD with no polling gap | Yes (use #1 multiplayer first) |
| 4 | **Vite proxy `/api/ghostlap/solo/*` → Realtime** | `vite.config.ts` has no such route | Forward to `VITE_REALTIME_ORIGIN` | Not needed for 13.4 | Yes |
| 5 | **Release face length** | `GameEvent` has no duration | Decide a real window or accept "until charge drops" | n/a | Yes (450 ms provisional) |

## Checks actually run

`npx tsc -p tsconfig.json` pass. `npm test -w @sands/ghost-lap` 21 pass (10 existing + 11 new: keys, remote interpolation, local smoothing, session ordering/dedupe/identity reset, cue targeting, Burst stages, not-confirmed/refused, CanControl and stall, bridge mapping + descriptor check, bridge input ordering, Burst retry). `npm run build -w @sands/ghost-lap` and `npm run check:bundles` pass; the bridge is absent from `dist`. Headless Chromium against the real test host: start, move, wall stop, candle placeholders, Space at 0/4 reports "No Burst: charge 0/4", blur sends neutral, no console error except the expected 401 of `/auth/guest/me` before the guest session exists.

**Not run:** real GPU/FPS, Firefox/Safari, multiplayer, existing Playwright suites, production hub, any audio. No CI claim.


## Production integration addendum — 19 November 2026 (Codex)

**DEV PLAY: PLAYABLE. PRODUCTION MULTIPLAYER PLAY: PLAYABLE on the real Realtime host. Spec 13.4 is not fully accepted: human Mac visual/movement review remains pending.** No Spec 13.5, source-art, physics, scoring, collision, timeline or Marrow change.

### Character crash

Reproduced in the actual `http://localhost:5174/?screen=dayPlay` application by switching Character study → Day play repeatedly. Browser debugger captured `TypeError: Cannot set properties of null (setting 'autoGenerateMipmaps')`, at `CharacterTextures.load`, `CharacterAssets.ts`, on the source mipmap assignment. Pixi 8.22's asynchronous `Assets.unload` destroys the cached texture before cache eviction finishes; immediate `Assets.load` could return that same texture with `source=null`. Ref counting alone did not serialize this zero-user transition. The loader now waits for outstanding unload before loading again. No exception suppression, placeholder, new texture API, asset modification or Pixi upgrade. Orange derivation, four heads/expressions, 64/56/48 display sizes and 48×48 colliders are preserved.

Repeated browser transitions no longer throw. DEV test-host smoke: approved head visible, authoritative x moved **200 → 250**, release held x=250/y=450 across subsequent frames; no page exception. Screenshot: `/tmp/ghost134-dev.png` (software rendering, no GPU/FPS/feel claim).

### Production transport and authority

- Client: `web/apps/ghost-lap/src/play/MultiplayerTransport.ts`; exact wire shapes in `multiplayerWire.ts`. Reuses `PlayTransport`, `PlaySession`, `PlayScene`, keyboard and existing smoothing. `PlayScreen.tsx` adds plainly identified integration room controls. The production build accepts `?screen=dayPlay`; the DEV bridge is still separately gated and absent from the production bundle.
- Authentication: existing `createMarrowTransport` contract handshake, GET `/auth/guest/me`, POST `/auth/guest/start`, signed guest/CSRF cookies and existing CSRF helper. Hub uses the shared authenticated WebSocket builder extended in `web/packages/contracts/src/signalr.ts`, same-origin `/hubs/ghostlap`, credentials and server Origin enforcement. Distinct people require separate cookie stores (e.g. regular + private browser or separate profiles); two tabs in one profile intentionally represent one guest/controller identity.
- Methods: `GetArena`, `CreateRoom`, `JoinRoom` (including `asSpectator=true`), `GetRoom`, `SetReady`, `StartCountdown`, one client upload `StreamInputs`, `Resync`, `LeaveRoom`. Host/controller/readiness checks remain entirely server-owned. Room polling is a small pre-game integration control, not the polished Spec 13.8 lobby. In an active round `LeaveRoom` may refuse `wrong_phase`; disposal still closes the socket and uses real disconnect/grace semantics.
- Events: `Snapshot`, `InputsApplied`, `InputRejected`, `ControllerSuperseded`, `MatchControl`, `RoomClosed`. Input sequences rise above recipient applied floors; held inputs refresh every 90 ms, each press/release is separately streamed. Blur/hidden/control loss/disposal release input. No HTTP `commit_pending` retry policy is copied. Acks prove applied input, never manual Burst; only mapped `burst_manual` confirms it. Backend processes all same-tick inputs in sequence, retaining a real rising edge even when press/release share a tick.
- Identity: `${MatchId}:${GenerationId}` for frames, `${MatchId}:${GenerationId}:${MatchEvent.Key}` for events. Backend slots select the four fixed characters; positions divide `XFixed/YFixed` by 1024. Scores/components, charge, Overlit/deadline and tick timer come from authority. `PlayFrame.snapshotSequence` and `PlayerFrame.memberId` (opaque sealed-roster identity) are optional/additive; spectator slot is explicitly -1. Stale tick/sequence/lifecycle and retired match generations are rejected. Same-tick control loss and newly retained events remain consumable without replaying deduped cues.
- Recovery: 5 s keepalive / 15 s timeout agrees with `GhostLapRealtimeRegistration` and defaults. Bounded reconnect delays 0/1/3/5 s, neutralized intent, same-room rejoin, new controller epoch/applied sequence floor and Resync before restored play. Failed recovery stays failed. Superseded/forfeited/spectator recipients cannot send gameplay. Resync was exercised; automatic network reconnect and supersession were not separately exercised.

### Arena and candle projection

`GhostLapHub.GetArena` returns typed `ArenaPresentationView` from `GhostLapPresentation.cs`: actual `ArenaV1` nine rectangles/world dimensions plus `MpRulesProfile.LayoutFingerprint`. Before playing, the client requires exact render-descriptor equality and a fingerprint match against the sealed round. No geometry service or client collision simulation.

`SnapshotFrame.VisibleCandles` is an additive projection on pushed **and Resync** frames. It reads the runtime's immutable `CandlePlan` but publishes only candles in that snapshot's `ActiveCandleIds`, plus those with `TelegraphTick <= snapshot.Tick < ActivateTick`. Coordinates are actual `CandleSpec.Site / 1024`, not ID-derived site indices. Collected/expired candles are excluded; future locations/times and the whole plan are never added to the payload. Existing sealed-round seed behavior is unchanged. Final candle artwork remains 13.5.

### Focused evidence

Real **production Hosts.Realtime entry point**, Production environment, real Kestrel/WebSockets and migrated isolated PostgreSQL, sharing the genuine API's guest secret. The optional E2E launcher provisions a separate non-superuser Realtime login with identity + multiplayer runtime roles; it does not fake hub methods, admission, scheduler, snapshots or auth. Redis is configured but not required by this direct multiplayer slice; Redis health readiness is not claimed.

Three isolated Chromium cookie contexts: A created, B joined, both readied, authorized host started; both received matching identity and different server slots. A moved, B observed the same fixed-point position through the existing remote renderer; applied acks arrived. Blur sent an ordered neutral and positions stopped. Third guest joined as spectator (`slot=-1`, `canControl=false`), received frames and sent no stream items after movement/Space keys. Resync returned `isResync=true`. No blocked warning remained during real play.

Latest bounded run: **T696 (~23.2 s active, including 15 s disconnect grace for the event check)**; match `95fa13d1dba1a4d66f1234b3afd2bc97`, generation `01a1efed6b61750095a0b8a190b56e32`; A's sealed slot was **1**, B's **0** (not join-order assignment); A movement YFixed **460800 → 665600**, same on B; **252 applied ack messages**, blur-stop verified. Visible candle `c-0000` at **(300,120)** in an actual frame. B disconnected; server emitted `forfeited`, tick **675**, index **0**, authoritative key **691200**. It maps to Unknown/raw `forfeited`, preserving identity without inventing a visual cue. No browser page exception. Evidence `/tmp/ghost134-production-result.json`, `/tmp/ghost134-production.png`. Earlier short smoke also passed; a collection-seeking probe did not reach a candle in its bounded window, so no production pickup/manual-Burst claim is made.

### Reproduce / current URLs

From repository root (Docker running):

```sh
dotnet build src/Hosts.Realtime/Hosts.Realtime.csproj -c Release --nologo -v quiet
dotnet build tests/E2E/backend/E2E.Backend.csproj -c Release --nologo -v quiet
cd tests/E2E
E2E_FIXTURE_TOKEN=local-dev-play-token-1234567890 E2E_BACKEND_PORT=8081 E2E_GHOSTLAP_REALTIME_PORT=8082 E2E_BACKEND_MAX_SECONDS=7200 ASPNETCORE_ENVIRONMENT=Testing dotnet backend/bin/Release/net10.0/E2E.Backend.dll
```

In another terminal, from `web/`: `npm run dev -w @sands/ghost-lap`. Reuse an existing listener rather than starting duplicate ports. The optional paired-host flag adds Realtime; omit it for the old DEV-only fixture. Ctrl-C stops the paired host/container; the fixture also expires after two hours. The launcher resolves `src/Hosts.Realtime/bin/Release/net10.0/Hosts.Realtime.dll`; build that first. Fresh browser cookie stores are simplest after a fixture restart because its database/guest secret are ephemeral.

Both URLs are **http://localhost:5174/?screen=dayPlay**. DEV: **Start test-host run**. Production: A **Create real room**, B enters code and **Join real room**, both **Ready**, A **Start countdown**. Third distinct cookie store: code → **Watch as spectator**. WASD/arrows move; Space presses/releases Burst (requires real 4/4). The source tag identifies TEST-HOST vs PRODUCTION HUB and controlling/read-only state. DEV feel panel retains smoothing/head-size/truth controls plus production Resync.

Checks: frontend typecheck; **23 frontend unit tests**; production build; bundle graph; explicit absence of TEST-HOST BRIDGE from production JS; source descriptor check (nine exact rectangles); Release Realtime + E2E fixture builds; **2 focused projection tests** (geometry/fingerprint, multiple-tick candle visibility/identity/no future or collected candle leak); actual browser crash reproduction/fix, DEV movement/release and production smoke above.

Not run: full 90 s 2/3/4-player matrices, broad Playwright/load/fairness/DB restart certification, match finish/rematch/results, production 4/4 manual Burst or pickup collection, forced automatic reconnect/controller takeover, Firefox/Safari, audio or real-GPU FPS/feel. No CI or human approval claim.

Remaining separate work: rich `SoloRunRegistry.PushFrame`, production `/hubs/ghostlap-solo` Original transport, Solo history proxy/catalog/save/history journeys; Challenge/Fork integration remains separate. No unrelated checklist task is marked complete. Existing dirty working tree preserved; no staging or commit.


Files touched in this integration (including narrow edits to previously untracked user frontend files):

- `src/GhostLap.Contracts/Multiplayer/GhostLapSnapshotContracts.cs`
- `src/Hosts.Realtime/GhostLap/{GhostLapHub.cs,GhostLapMatchHosting.cs,GhostLapPresentation.cs}`
- `tests/E2E/backend/{E2eBackendEntry.cs,E2eDatabase.cs,GhostLapRealtimeProcess.cs}`
- `tests/Integration/GhostLap/GhostLapPresentationTests.cs`
- `web/packages/contracts/src/signalr.ts`
- `web/apps/ghost-lap/src/characters/CharacterAssets.ts`
- `web/apps/ghost-lap/src/play/{playTypes.ts,keyboardIntent.ts,PlaySession.ts,multiplayerWire.ts,MultiplayerTransport.ts}`
- `web/apps/ghost-lap/src/screens/PlayScreen.tsx`
- `web/apps/ghost-lap/src/app/GhostLapApp.tsx`
- `web/apps/ghost-lap/src/arena/arenaV1Descriptor.ts` and `src/stage/WorldLayer.tsx` (comments describing runtime verification)
- `web/apps/ghost-lap/test/{play.test.ts,multiplayer.test.ts}`
- This handoff and `docs/kiro-log.md`.

## Final human acceptance — Spec 13.4 CLOSED

**Human and technical acceptance: PASS.** This supersedes the "human review pending" wording above; Codex's technical findings stand unchanged.

Human-verified: approved daytime arena, 64-unit characters and the full expression set in live play; WASD/arrows, diagonals, Space/Burst, wall contact and responsive movement; Raw/Light/Smooth all work (Light and Smooth both acceptable, no prediction system needed now); HUD, score, Soulfire, Overlit and feedback readable for this stage. The human played a full two-window Chrome/Incognito multiplayer match: independent controls, synchronized movement and scores, responsive characters, correct contact expressions. The read-only spectator was verified by Codex only, not human-tested. Automatic reconnection was not tested. Solo production integration is not done.

**Instruction to future task agents: REUSE, do not recreate.** Use the existing `MultiplayerTransport.ts` (production transport), the `PlayTransport`/`PlayFrame` presentation seam, the existing smoothing, controls, arena projection and character implementation.

**Sizes:** 64 is the approved gameplay visual size; 56 and 48 are fallbacks. Backend colliders remain 48x48.

**Remaining later work, not part of 13.4:** richer production Solo Original frames, Solo hub/history integration, final candle artwork, effects, audio, polished multiplayer journey and final UX refinements stay in their respective later tasks.
