# Spec 13.0 — frontend contract map

Audit: 18 November 2026, Asia/Kolkata; source checkpoint `5806c136775db1eb598e1ea50c8ec738ccc4c0df`, branch `codex/ghostlap-spec10-0-3`.

**PASS WITH LATER BLOCKERS.** Opus can begin 13.1 in the existing app. Geometry/plan delivery and original-run frame completeness must be resolved before real gameplay acceptance; character and effect-source gaps are in [asset audit](18-ghostlap-spec13-approved-asset-audit.md). **B1 NOT STARTED; 13.1–13.12 NOT STARTED.** No production code, assets, physics or dependencies changed; no application build, database or game tests run.

Planning-location caveat: the tracked Spec 13 `requirements.md`, `design.md`, `tasks.md` are already deleted in this working tree. The replacement documents actually exist at `.kiro/specs/spec-13-ghostlap-complete-browser/.kiro/specs/spec-13-ghostlap-complete-browser/`. Those three documents, the adjacent replacement README, the entire installed `design-inputs/ghost-lap-frontend-director/SKILL.md`, README, refs 00–12, inventory script and actual `characters.md` were read. No directory repair is included in this audit.

## A1. Verified frontend application

| Fact | Actual source / result |
|---|---|
| Application | `web/apps/ghost-lap`; entry `src/main.tsx`, `index.html`; established React shell, not a new app |
| Workspace | `web/package.json`: npm `apps/*`, `packages/*`; `web/package-lock.json` lockfile v3; no repository-root package.json |
| Locked versions | React/ReactDOM **19.3.1**, TypeScript **5.9.3**, Vite **7.3.8**, React plugin **5.2.0**, Pixi **8.22.0**, SignalR **10.0.11** (`@sands/contracts`) |
| Declared versions | React `^19.0.0`, TS `^5.9.0`, Vite `^7.0.0`, plugin `^5.0.0`; Pixi exact `8.22.0`; Node `^22.18.0 || ^24.0.0` |
| TypeScript | `web/tsconfig.base.json`, app `tsconfig.json`: strict, ES2022, bundler resolution, react-jsx, noEmit, noUncheckedIndexedAccess |
| Routing | `main.tsx`: `?timeline=`; greybox `GhostLapGreybox.tsx`: `?original=`, `?branch=`, local pushState/popstate; no router dependency |
| Styling | Plain `greybox/greybox.css`; no approved frost/theme/font system implemented |
| Scripts | App `dev` = vite, `typecheck` = tsc, `build` = tsc + vite; workspace typecheck/build and `check:bundles` |
| Static serving | Vite default public-directory convention; no approved asset registry/public runtime copies found in app. `public/game-assets/` is a later proposed output |
| Tests | `tests/E2E/ghostlap-greybox.spec.ts`, `playwright.ghostlap.config.ts`, `adapters.spec.ts`, `browser-smoke.spec.ts`; separate E2E package Playwright **1.65.0**; shared timeline Vitest locked **5.1.0**; backend `tests/GhostLap`, `tests/Integration/GhostLapSolo*` |

| Capability | Classification | Reuse / limitation |
|---|---|---|
| Genuine Ghost Lap screen | PARTIAL | `GhostLapGreybox` is real code, but `/ghost-lap` wire is explicitly **test-host-only**, not current production solo/multiplayer |
| Game UI | PARTIAL | Existing score/aligned readouts, errors, replay/start/branch screens and shared `TimelineScrubber`; no premium primitives/lobby/results/history library |
| Realtime | PARTIAL | `@sands/contracts`: `createMarrowTransport`, `RealtimeLink`, `createRealtimeConnection`; shared `/hubs/realtime`, **not** game-hub DTO clients |
| Guest state | EXISTING AND REUSABLE | `GhostLapGreybox.tsx` private `ensureGuest`: GET `/auth/guest/me`, then POST `/auth/guest/start`; memoized promise; shared CSRF/HTTP helpers |
| Asset loading | ABSENT | No approved asset manifest/preloader; Pixi dependency alone is not an asset pipeline |
| Canvas | EXISTING AND REUSABLE | `greybox/ArenaView.tsx` exported `ArenaView`: imperative Pixi Application/effect cleanup/ticker; primitive Graphics greybox, no production scene/characters/snow |
| Input | PARTIAL | `liveSession.ts`: `useKeyboardIntents`, `useLiveFrames`, `failureOf`; polling/test-host wire, not production input stream |
| Results/history | PARTIAL | `createOriginalAdapter`, `createGhostLapAdapter`, `createTestHostHttpWire`, generic shared scrubber; no current private production catalogs/children/result adapter |

Skill types (`ScenePresentationState`, `SemanticVisualCue`, `WallRenderer`) and proposed source modules are **not existing production interfaces**. `pixi-filters`, `@pixi/sound`, approved Fontsource families and `@pixi/react` are absent from the lockfile; no install/upgrade recommendation or action is part of 13.0.

## A2. Authoritative world mapping

Sources: `src/GhostLap/Rules/{GameRulesV1,ArenaV1,CandlePlan}.cs`, `src/GhostLap/Simulation/Movement.cs`, `src/GhostLap/Multiplayer/{MpRulesProfile,MpSimulation,RoundSealing}.cs`.

* **1600 × 900 world units**, fixed-point scale **1024 = 1 unit** (`FixedShift=10`). Renderer conversion: `XFixed / 1024`, `YFixed / 1024`, then one contain/letterbox transform. Origin top left, X right/Y down, consistent with existing keyboard/renderer convention. `FixedRect` is half-open; touching edges are not overlap.
* Every head has the same **48 × 48 axis-aligned square collider**, half extent 24, independent of visual silhouette. Centre stays in `[24,1576] × [24,876]`; `Movement.ResolveAxis` resolves X then Y against world bounds and `ArenaV1.Obstacles`.
* **Nine static authored obstacles**, world-unit `(x0,y0,x1,y1)`: `(400,200,560,240)`, `(1040,200,1200,240)`, `(400,660,560,700)`, `(1040,660,1200,700)`, `(300,380,340,520)`, `(1260,380,1300,520)`, `(760,410,840,490)`, `(780,160,820,280)`, `(780,620,820,740)`. Same maze for solo and multiplayer; only spawn sockets vary.
* Spawns: solo `(200,450)`; 2P `(200,450),(1400,450)`; 3P `(800,80),(400,780),(1200,780)`; 4P `(120,120),(1480,120),(120,780),(1480,780)`.
* `ArenaV1.CandleSites`: 12 sites `(300,120),(250,300),(480,320),(640,200),(640,340),(400,450),(620,450),(250,600),(480,580),(640,700),(560,780),(700,600)`, plus point mirrors `(1600-x,900-y)` in the same order. Candle collider 24 × 24, half extent 12.
* `CandlePlan.Create(seed)` supplies `CandleSpec(Id,SiteIndex,Site,TelegraphTick,ActivateTick,ExpireTick)`. IDs `c-0000` etc are **not site indices**. Activation inclusive, expiry exclusive; telegraph 30 ticks, lifetime 210. Plan varies by seed; obstacles do not. Active membership comes from server `ActiveCandleIds`, collection cue from `CandleId`; expiry can be derived only from the verified plan and tick. No separate spawn/expire event is exposed.
* Solo identity `ghost-v1 / arena-v1`; new room starts explicitly seal `ghost-mp-v2 / arena-v1` in `ActiveRoomOwner.StartCountdown`. `MpRulesProfile.LayoutFingerprint` hashes geometry/sites/spawns. Legacy `ghost-mp-v1` support is in `MpRulesProfile.Supports`, `MpRules.StepWithPhases` (`Legacy` versus `All`) and versioned state encoding in `MpSimulation.cs`; do not rewrite it.

**Geometry is established in source, but no current production browser read DTO supplies obstacles or candle-plan coordinates.** `MatchSnapshot` and solo history/live DTOs carry IDs only; sealed round carries spawn coordinates, seed and layout fingerprint, not maze/plan. `greybox/types.ts` `Arena` and `/ghost-lap/arena` belong to the test fixture (`tests/E2E/backend/GhostLapSoloEndpoints.cs`). They are not a production access path. B1 must propose the smallest read projection of existing `ArenaV1` + `CandlePlan` (or a source-verified generated client configuration with appropriate identity checks). No endpoint is created/approved here. **BLOCKER for 13.2 geometry integration and 13.4/13.5 candles**; never use snow pixels or 18px tiles as collision data.

## A3. Players, state, controls and cues

`RoundSealing.Seal` orders competitors by a room-secret/round-ordinal MAC and assigns **zero-based** slots. Lobby order is not final slot order. `JoinedRoom.MemberId` identifies the caller; `RoomMemberView.MemberId/Slot`, `SealedRoundView.Slots`, and `SnapshotFrame.You.Slot` establish assignment. `MpMatchState.Initial` uses `GameContext.Create(seed,count)`, assigning mascot ID equal to slot. There is no server color/shape dictionary: **visual** mapping is defined by actual `characters.md` / skill ref 04: slot 0 → P1 orange circle, 1 → P2 pink square, 2 → P3 yellow diamond, 3 → P4 green squircle.

**CHARACTER SELECTION NOT IMPLEMENTED — do not expose a fake selector in multiplayer.** No mutable room mascot method exists. Solo is different: `StartSoloRequest(OperationKey,MascotId)` accepts mascot 0–3 at run creation; Challenge/Fork inherits source mascot and cannot change it. Existing greybox start UI already has a test-host mascot choice. Preserve identity rather than assuming every solo slot-0 actor must be P1.

| Presentation need | Actual authoritative field / supported derivation |
|---|---|
| Position, facing | MP `PlayerSnapshot.XFixed/YFixed`, `MoveX/MoveY`; Challenge/history held vector `HeldMoveX/Y`. Facing/gaze is presentation from effective direction; no facial-expression field |
| Soulfire | `Charge` 0–4; initial `GameState.Initial` leaves Charge **0**, despite older character prose saying 1. Visual 0/1 same frown/no halo; numeric state distinct |
| Overlit | MP `Charge==4` plus `OverlitDeadlineTick`; Challenge/trace also `Overlit`. Deadline set at full charge +240 ticks; no invented timer authority |
| Score | `MovementPoints`, `PickupPoints`, `PenaltyPoints`, `TotalScore`, pickup/Burst/hit counters; no frontend award calculation |
| Input | MP `GameplayInput(MatchId,ControllerEpoch,ClientTick,Seq,MoveX,MoveY,RequestBurst)`; solo `SoloInputItem(Seq,ClientTick,MoveX,MoveY,RequestBurst)`; slot/owner inferred server-side |
| Admission | `InputAdmission.Admit`: vector components −1/0/1, increasing sequence, client tick in next−4..next+2; accepted input applies at **next server tick**, not claimed tick. Hold six ticks; diagonal per-axis **7241 fixed**, cardinal **10240 fixed** |
| Burst | `RequestBurst` is held pressed state; reducer's `BurstLatch` detects **rising edge**. Requires actual full charge; admission is not proof of a Burst. Use authoritative `BurstManual`/`BurstAuto` cue |
| Contact/hit | `ContactDrain`: Slot is weaker victim, OtherSlot stronger, Value actual charge loss. `BurstHit`: Slot victim, OtherSlot attacker, Value actual score penalty. No generic collision/health/damage animation event |
| Outcome | MP durable `MatchPlayerResultView.CompetitivePlacement/Outcome`; ties share rank, finishers above forfeits. Solo final reducer `FinalResult.Winners`; live original wire does not publish that object |
| Time | absolute Tick, terminal 2700; nominal remaining seconds `(2700-Tick)/30` is a valid display derivation, not a browser deadline |

Established keys in **existing greybox** `liveSession.ts`: WASD **and** arrow keys, Space Burst. Movement is resent every 90ms, frame polling gap 30ms, blur/hidden invokes test-host neutral. These are client precedent, not production multiplayer keyboard wiring. Production has no dedicated blur-neutral hub method: B1/B2 must send ordered neutral/released input over its real stream and obey control/admission. Don't copy the test `/neutral` call.

**Important source discrepancy:** `SoloRunRegistry.PushFrame` (line 452 at audited checkpoint) constructs only `SoloFrame(Tick,XFixed,YFixed,Charge,OverlitDeadlineTick,TotalScore,Final)`. All trailing declared fields (`Events`, `ActiveCandleIds`, held input, score parts, `AckedInputSeq`, etc.) remain defaults, including `Overlit=false` even at charge 4. Original pushes also lack `Seq/Pacing/Missed/MissedTruncated`. Challenge/Fork uses `ChallengeMapping.ToWire` and fills the rich fields correctly. B1 must resolve original live projection/event delivery; the DTO definition and Spec 11 Challenge acceptance do **not** prove Original completeness. A client must not treat default `AckedInputSeq=-1` as a real applied-sequence floor.

## A4. Multiplayer contract

Actual source: `src/Hosts.Realtime/GhostLap/GhostLapHub.cs`; DTOs `src/GhostLap.Contracts/Multiplayer/{GhostLapMultiplayerContracts,GhostLapSnapshotContracts,GhostLapResultContracts}.cs`; authority `ActiveRoomOwner.cs`, `MatchFeed.cs`, `MatchResults.cs`. Hub **`/hubs/ghostlap`**, WebSockets only. All calls require live guest session policy `ghostlap.realtime.connect`, not staff. Replies are `GhostLapReply<T>(Ok,Code,Value)`.

| Action / consumer | Actual method / request | Reply or event | Additional authorization |
|---|---|---|---|
| Create / setup | `CreateRoom(CreateRoomRequest(RequestId))` | `JoinedRoom(View,MemberId,Role,ControllerEpoch,RoomCode)` | live guest, room limits |
| Join / lobby | `JoinRoom(JoinRoomRequest(RequestId,Code,AsSpectator))` | `JoinedRoom` | eligible invite, capacity/role rules |
| Ready / lobby | `SetReady(SetReadyRequest(RequestId,RoomId,Ready))` | `RoomCommandResult` | competitor controller |
| Start / countdown | `StartCountdown(RoomCommand)` | `RoomCommandResult` | host/controller, 2–4 ready connected competitors; optional ExpectedVersion |
| Cancel / host | `CancelCountdown(RoomCommand)` | `RoomCommandResult` | host/controller, valid phase |
| Transfer / host | `TransferHost(TransferHostRequest)` | `RoomCommandResult` | valid host/target/version |
| Room / lobby | `GetRoom(string roomId)` | `RoomView` | current membership; lifecycle version |
| Room subscription | shared `/hubs/realtime`, stream ID `ghostlap-room:{roomId:N}` | room lifecycle projection | membership, shared supervised stream contract |
| Play / input | **one** `StreamInputs(IAsyncEnumerable<GameplayInput>)` invocation/connection | `InputStreamSummary`; `InputRejected(InputRejection)`, `InputsApplied(InputAck)`, `ControllerSuperseded()` | bound controlling connection/epoch/match; spectators refused |
| World / renderer | server `Snapshot(SnapshotFrame)` | `MatchSnapshot`, `You`, `Events`, lifecycle | room recipients; per-recipient latest wins |
| Recovery / viewer | `Resync()` | `SnapshotFrame` with `IsResync=true` | current member, including spectator |
| Result / results | `GetMatchResult(string matchId)` | `MatchOutcomeView`, player rows | sealed roster subject **or current room member**; foreign/unknown `result_not_found` |
| Rematch / results | `Rematch(RoomCommand)` | `RoomCommandResult`, notice `rematch_lobby` | competitor controller after settled outcome; clears ready flags |
| Leave / navigation | `LeaveRoom(RoomCommand)` | `RoomCommandResult`; server `RoomClosed()` | spectator may leave; competitor leave during `start_admitted` refuses `wrong_phase` (or `start_committing`) |

No explicit `Forfeit()` method. Disconnect neutralizes the competitor; **450 authoritative ticks** grace then forfeit. Later connections cannot restore control to forfeited slots. `MatchControl(MatchControlNotice)` includes `forfeited` / `control_overflow`. `You.CanControl` is false for spectators, superseded tabs and forfeited slots; enforce it, not merely Role.

Lifecycle: `lobby → countdown → start_admitted`; `RoomView.MatchStatus` distinguishes `running`, `pending_finalization`, `aborting`, `finalized`, `aborted`, `unverified`. Only result `Decision=finalized|aborted|pending` is durable read semantics; `unverified` is not a result decision. Retrieve result after terminal indication; bounded backoff on pending/unavailable is frontend work, not a prescribed server retry interval. Rematch returns to lobby; **next start** seals new MatchId/seed/ConfigDigest/ordinal. Reset old cue/frame ownership. A roster competitor retains result read after room membership loss via roster authorization; a spectator does **not** retain it after leaving. No public result directory/route.

## A5. Solo, history, Challenge and Fork

Source: `src/Hosts.Realtime/GhostLap/Solo/{GhostLapSoloHub,GhostLapSoloRegistration,GhostLapSoloHistoryRoutes,SoloRunRegistry,SoloChallengeRegistry}.cs`, `src/GhostLap.Contracts/Solo/GhostLapSoloContracts.cs`, `src/GhostLap/Solo/{SoloOriginals*,SoloChallenges*,SoloBranches,SoloGhostTrace}.cs`.

All routes below live on **Hosts.Realtime**, guest-owner scoped; identity never comes from body/query. Solo registration is conditional on configured `ConnectionStrings:GhostLapSolo` (name only; no credential inspected/reported). Hub **`/hubs/ghostlap-solo`**. Invocation replies use `GhostLapReply<T>`.

| Action / consumer | Method or HTTP route / request | Actual response/event |
|---|---|---|
| Original / day play | `StartRun(StartSoloRequest(OperationKey,MascotId))` | `SoloRunStarted`: RunId, server seed, mascot, pinned rules/layout/rate/round; starts authoritative runner immediately |
| Original input | `StreamInputs(IAsyncEnumerable<SoloInputItem>)` | `InputStreamSummary`; `Frame(SoloFrame)`, `InputRejected(SoloInputRejected)`, `RunEnded(SoloRunEnded)` |
| Original save/retry | `FinishRun(string operationKey)` | `SoloSaveView`: `Saved` only with Original; `SavePending` same-key retry; `Failed` code |
| Originals / library | GET `/api/ghostlap/solo/originals`, `/{id}` | `SoloOriginalView[]`, verified detail `SoloOriginalView`; list capped at 100 (`SoloOriginals.MaxListLimit`), no after/limit query contract |
| Exact inspect | GET `/api/ghostlap/solo/originals/{id}/inspect/{tick}?v=` | `SoloInspectView`, one verified `SoloGhostFrame` |
| Original replay | GET `/api/ghostlap/solo/originals/{id}/trace?from=&count=&v=` | `SoloTraceView`, Source identity, Frames, NextTick |
| Challenge prepare | `PrepareChallenge(PrepareChallengeRequest(OriginalId))` | `ChallengePrepared`, **Ready only after full verified source trace**; T0 |
| Fork prepare | `PrepareFork(PrepareForkRequest(OriginalId,ForkTick))` | `ChallengePrepared`, Kind Fork, exact T, FirstInputTick T+1; T0–2699 only |
| Start prepared race | `StartChallenge(StartChallengeRequest(OperationKey))` | `ChallengeStarted`: ChallengeId, OriginalId, seed/mascot, Kind/ForkTick, Living/Ghost at T, hashes/versions |
| Race input / night | `StreamChallengeInputs(IAsyncEnumerable<SoloInputItem>)` | `ChallengeFrame(ChallengeFrameView)`, `ChallengeEnded(SoloRunEnded)`, `InputRejected` |
| Cancel race | `CancelChallenge()` | `bool`; unfinished setup/running work discarded, never a saved result |
| Finish / recover child | `FinishChallenge(key)` **or** `FinishFork(key)` by kind | `ChallengeFinishView`: Completed + verified `ChallengeResultView`, Pending same-key retry, Failed code |
| Saved children | GET `/api/ghostlap/solo/children?source=&after=&limit=` | `SoloChildPageView(Items,NextAfter)`; limit 1–25 (default 10), newest first, completed Challenge/Fork only |
| Child detail | GET `/api/ghostlap/solo/children/{id}` | verified `ChallengeResultView` |
| Child replay | GET `/api/ghostlap/solo/children/{id}/trace?from=&count=&v=` | `SoloChildTraceView`: both IDs/final hashes, Kind, ForkTick, versions, Frames/NextTick, inherited prefix included |
| Same-tick/curve | GET `/api/ghostlap/solo/children/{id}/comparison?from=&count=&stride=&v=` | `SoloComparisonView`, points at from+i×stride; each contains parts/hashes/Delta/AfterFork |

Trace count 1–300, tick 0–2700; comparison count/stride 1–300; version `v=solo-history/1` (optional omission accepted, other values refused). Ten trace chunks cover 2701 states. IDs in HTTP reads must be D-form UUIDs. `NextTick`/`NextAfter` null means end. Shared read limiter has four slots. HTTP failures: 400 invalid bounds/version contract, 401 guest absent, 404 foreign/unknown/unfinished, 409 verification/version failure, 429 capacity, 503 unavailable. Hub owner's unfinished preparation can say `not_complete`; do not flatten all hub failures to HTTP 404.

Original identity fields: Id, OperationKey, Seed, MascotId, RulesVersion/LayoutVersion, DomainId/ReducerVersion/InputSchemaVersion/StateSchemaVersion/ContextSchemaVersion, FinalTick/FinalStateHash/LastRecordHash, nullable Score (verified detail). Child catalog adds SourceId, Kind, ForkTick, Verified, Status; result adds child/source final **state and record** hashes, LivingScore/SourceScore/Delta. `SoloPinnedVersions` pins `ghost-lap / ghost-v1 / ghost-input/1 / ghost-state/1 / ghost-context/1`, rules ghost-v1/layout arena-v1 (`src/GhostLap/Timeline/GhostLapTimelineVersions.cs`). Cache by resource type + IDs + hashes + versions, not list index.

Fork restores complete canonical state at T, not just position/score; first new transition T+1, terminal absolute T2700. Challenge at T0 and Fork(T0) remain distinct child kinds. No nested child forks. Comparison `AfterFork=false` through T; signed Delta = child−source at the same absolute tick. Equal scores after T do not prove equal states. Never use source final score in a live same-tick HUD.

Durable owner-scoped operation keys resolve completed originals/children after lost response, refresh and serving-process restart; they do **not** resume an unfinished live runner. Disconnect abandons original and cancels Challenge/Fork in the hub disconnect path. Preparation/Ready is connection-bound; no live solo `Resync` endpoint or generic resume method exists. Original key allows 1–100 allowed characters; child start key 1–90 (letters/digits/`-_.`). In-memory pending-save retention is 10 minutes (`SoloRunRegistry.PendingSaveLifetime`, `SoloChallengeRegistry.PendingLifetime`); durable completed reads remain independent of that retention. Ready race should start only once scene/control can show T; Start advances real time, not an animation timer. Original also starts immediately on StartRun.

## A6. Timing, identity and network recovery

| Stream | Proven fields / dedup / limits |
|---|---|
| Multiplayer | `Snapshot.MatchId`, `GenerationId`, `Tick`, `Sequence`, `IsFinal`; `LifecycleVersion`, `MatchStatus`, `IsResync`; effective MoveX/Y and LastSequence. `MatchEvent.Key = Tick*1024+Index`, scoped by **MatchId + GenerationId**. `EventBatch.FromTick/ThroughTick/Truncated` describes retained delivery, not arbitrary missing snapshot count |
| Challenge/Fork living | scope by `ChallengeStarted.ChallengeId`, event Tick + **index in Events**; `Seq` is pushed-frame order, **not** event identity. `Living.AckedInputSeq` is applied reducer sequence; held vector/latch are authoritative |
| Historical ghost | scope by OriginalId + hash/versions, tick/index, separate from living child; ghost cues must not trigger living pickup/hit audio |
| Deferred race cues | consume `Missed` oldest first, each tick's ordered Living/Ghost arrays, then current arrays; dedup across both paths. Buffer bounded **64 event-bearing ticks**, explicit `MissedTruncated`; it cannot restore missing living cues from an unfinished child via a nonexistent live-resync API |
| Original live | Tick and initial RunId association; only seven fields populated. No reliable event identity/ack/missed stream is currently delivered; **do not generalize Challenge contract to Original** |
| Archived trace | real `SoloGhostFrame.Tick`, `Events[i]`, `StateHash`, IDs/final hashes/versions; stable local dedup. User-initiated historical playback reset is a presentation policy, distinct from duplicate network delivery |

`MultiplayerOptions` defaults: simulation 30Hz; snapshot every **2 ticks ≈15Hz**, plus final/presence/forfeit; event retention **300 ticks**; sender timeout **3000ms**; reliable-control capacity 64; server keepalive **5s**, client timeout **15s**, actually wired with `AddHubOptions<GhostLapHub>` in `GhostLapRealtimeRegistration.cs`. Browser must configure its own timely ping (existing shared builder does not set these game values). These are source defaults, not measured deployed/browser settings; don't assume the MP hub-specific options apply to solo. Session access is periodically revalidated for long streams (Realtime access-revalidation setting) and re-read on commands.

`SoloTickClock` uses monotonic 30Hz deadlines; stalled commits can lengthen a round without skipping logical transitions. Challenge `FramePacing(CommitElapsedMs,ScheduleLagMs,SendElapsedMs,Coalesced)` is server-round-relative; browser arrival time is a separate local clock. No MP server timestamp is in `MatchSnapshot`. MP `InputAck(MatchId,ControllerEpoch,Tick,LastAppliedSeq)`, recipient `AppliedSeq` and rejection `SequenceFloor/Tick` allow sequence recovery; controller epochs are not authority generations. Solo refusals expose `NextServerTick`, `Class=benign_movement|burst_rejected|refused`, `BurstRequested`; rejected at-risk Burst presses are not silently coalescible movement. No new event UUIDs or guessed acknowledgements.

These fields support later tick-based visual buffering, effective-direction presentation, sequence reconciliation and 30Hz-to-display rendering; **no interpolation algorithm is implemented/designed here**. Score, charge, candles and outcomes stay authoritative. Snapshot gaps are normal; truncate/control overflow requires truthful baseline recovery, not replaying invented cues. Reconnect MP must restore membership/control binding and obtain a valid new-generation baseline; active match state is in-process, not an archived replay. Solo unfinished disconnect is interruption, not recoverable play.

Prior evidence read, **not rerun**: [host contracts](15-ghostlap-solo-host-contracts.md), [Spec 11 acceptance](16-ghostlap-spec11-final-acceptance.md), earlier 11.3/11.4 architecture reports and focused source tests. Spec 11 measured a 1560-tick T1140 Fork, post-restart child/source traces, bounded deferred sender cues, corrected solo clock and four-player overlap. Corrected local client max gap ~93ms, MP about 15Hz; none proves browser FPS, WAN latency, WSS/TLS cookies, audio codecs, physical-key response or original-live completeness. Those remain B2/B4/B5 gates.

## A7. Minimal B1 seam for agreement during 13.1

**Interface map only; B1 NOT STARTED.** Retain `web/apps/ghost-lap` and shared contract/error/CSRF/handshake code. `createRealtimeConnection` is fixed to `/hubs/realtime`; do not claim it already opens game hubs. `greyboxWire.ts` and `ghostLapTimelineWire.ts` are test-host wires; use their validation/error idioms, not their production URLs. Proposed transport files should be agreed with Opus before edits; no state-management library choice or directory reorganization.

| Codex bridge responsibility | Opus read/consume/own |
|---|---|
| Guest-cookie HTTP, CSRF, handshake, host routing; explicit availability/error states | accessible screen/navigation/pending/error presentation |
| Typed copies/parsers of actual MP/solo DTOs; stream lifecycle, membership/controller, server methods | experience coordinator; never infer authorization from a button |
| source-backed world/plan read projection proposal; exact layout/hash identity | one world-to-pixel transform and actual wall/candle art |
| ordered immutable authoritative frames, run/match/generation identities and applied-input/refusal reports | interpolation/local visual anticipation/reconciliation policy; render ticker |
| authentic per-tick ordered cue envelopes, Missed/truncation signals | expressions, pure Burst, audio one-shots and dedup consumption |
| bounded owner trace/curve reads, durable keys and typed save/recovery status (B3 details later) | left-hourglass history UI, cached historical viewing, rewind/night choreography |

**PROPOSED SEMANTIC TYPES**, not source DTOs: readonly frame envelope containing actual identity, absolute tick, parsed raw authoritative state and local `receivedAtClientMonotonicMs`; cue envelope containing same identity + tick/index + actual event kind/payload; explicit transport/control/save states. Local arrival timestamp is a diagnostic derivation, not server time. Preserve distinction between source ghost and living child, MP generation and controller epoch, original and rich Challenge frame shapes. Agree ownership and missing-field behavior before implementation. No speculative endpoint approval is implied.

Auth integration: `src/Marrow.Infrastructure/Identity/IdentityEndpoints.cs` issues/resumes HttpOnly Secure `__Host-sands_guest`, companion readable CSRF cookie, SameSite=Lax/path `/`; `GuestSessionLifetime` default 90 days, server revocation/expiry still authoritative. Shared `api-client.ts` uses credentials; `csrf.ts` supplies `X-CSRF-Token`. Hub uses guest cookies and valid Origin, not a staff token. Extra game origins are controlled separately by `GhostLap:Multiplayer:AllowedOrigins`; identity write origins by `Identity:AllowedOrigins`. No browser deployment is verified. Vite app uses empty-base same-origin transport, port **5174**, API target **8081**, realtime **8082**, `VITE_API_ORIGIN`/`VITE_REALTIME_ORIGIN`, `changeOrigin:false`. **Missing proxy:** `/api/ghostlap/solo/*` is not forwarded by current Vite config; it belongs on Realtime, not API. B1 must address this narrowly with actual target/proxy configuration. Do not read/report secret values, alter DB roles or duplicate history endpoints.

## A8. Blockers and exact follow-up

| Missing fact/boundary; inspected evidence | Classification / affected task | Owner / smallest resolution |
|---|---|---|
| Replacement spec nested; canonical tracked paths deleted; README + git status | NONBLOCKING VERIFY for 13.1 tooling | repository owner normalizes separately; audit references actual nested copy, no restoration here |
| No production maze/candle plan projection; Rules constants, outbound DTOs, registrations, test fixture inspected | **BLOCKER 13.2 integration / 13.4–13.5** | B1 propose narrow read adapter over existing constants/seeded plan; geometry itself fully known |
| Original `PushFrame` fills seven fields; no deferred cues/ack/active IDs despite optional DTO fields | **BLOCKER 13.4 original / 13.5–13.7 / 13.9** | B1 source-grounded compatible projection/delivery fix proposal; focused verification later, frozen simulation unchanged |
| Test-only browser wire; no game-hub clients; missing Realtime HTTP proxy | **BLOCKER real-play gate 13.4**, not 13.1 | B1 implement transport after agreed 13.1 boundary |
| Exact orange-circle source, gaze/pupil split and special face parts; actual PNG/XML inventory | **BLOCKER full 13.3 acceptance** | asset owner + Opus resolve exact mapping/approved derived composition; see asset report |
| Effect frame grid/order/FPS not documented, large sheets | **BLOCKER animation 13.5/13.6** | asset owner supply exporter manifest or establish frame boundaries/order from source, then derived packaging; no guessed slicing |
| MP choice unsupported; solo mascot accepted; source roster sealing | NONBLOCKING confirmed limitation, 13.1/13.8 | Opus display assigned MP character; no fake selector |
| Deployed ping/origin/session/WSS/browser timing; only defaults + prior local evidence | NONBLOCKING VERIFY for foundation, required 13.4/B2/B4/B5 | Codex checks actual browser later; no 60FPS claim |
| FX/audio raw-distribution rights absent; local pack licenses/ffprobe inspected | **BLOCKER public release**, not UI foundation | asset owner supplies provenance/licenses; B5 verifies |

Git/worktree: unrelated Spec 08/11/12 edits, deleted Spec 13 paths, untracked design inputs/recovery documents and pre-existing log changes preserved. `gh run list --branch codex/ghostlap-spec10-0-3 --limit 5 --json ...` returned **[]**; workflow is PR/main-push triggered. Zero runs is **not green CI**. Documentation left uncommitted because replacement checklist is inside an untracked replacement file and log already contains unrelated edits; this audit does not stage the full replacement/deletions by accident. Scoped document/checklist/log checks passed; repository-wide `git diff --check` reports only pre-existing trailing whitespace in unrelated Spec 08/11/12 requirements, left untouched.
