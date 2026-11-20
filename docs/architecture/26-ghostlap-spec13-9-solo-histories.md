# Spec 13.9: Solo Original, durable save and My Histories

Technical integration is running locally against production Hosts.Api and Hosts.Realtime. Human playability review remains pending.

## Play locally

Open [Solo](http://localhost:5174/?screen=solo) in a desktop browser. Choose a head, select **Begin run**, move with WASD or arrows, and press Space at 4/4 Soulfire. A run starts immediately and reaches tick 2700 after about 90 seconds. Saving starts automatically. Once **Saved · verified** appears, select **My Histories**, then select the Original. The [My Histories URL](http://localhost:5174/?screen=histories) also opens the private catalog directly. Refresh in the same browser profile to retain the guest session and look up the same Original.

### Restart the verified setup

From the repository root with Docker Desktop running, use the existing Compose stack and its existing volumes. The git-ignored local overlay `.env.ghostlap-solo` supplies the local Solo runtime password, the previously missing local ledger login passwords, and host ports 8181/8182. Keep this file and the base `.env` to reuse the current database config and shared guest signing secret.

```sh
cd /Users/rroy/Desktop/the-sands-of-time-phase1
docker compose --env-file .env --env-file .env.ghostlap-solo -f deploy/compose/docker-compose.yml up -d api realtime
cd web
VITE_API_ORIGIN=http://localhost:8181 VITE_REALTIME_ORIGIN=http://localhost:8182 npm run dev -w @sands/ghost-lap
```

Add `--build` to the Compose command after changing host source. Compose starts PostgreSQL on 55432 and Redis on 56379 and runs the existing migration job before the hosts. The browser uses Vite on 5174, Hosts.Api on 8181 for guest bootstrap, and Hosts.Realtime on 8182 for hubs and owner history. Ports 8181/8182 let this setup coexist with the previous day-play fixture on 8081/8082. Guests created on that fixture use its separate identity store. For the Compose setup, keep the same browser profile and the same origin, `http://localhost:5174`.

The previous fixture launcher explicitly passed an empty `ConnectionStrings__GhostLapSolo`. Multiplayer negotiation answered 401 without credentials, while the Solo negotiate and Originals routes answered 404 both directly and through Vite. Compose provisions `sands_realtime_solo` under the existing `ghostlap_runtime` and `marrow_timeline_runtime` roles. PostgreSQL has the existing Original and branch tables. The Vite proxy retains the browser Host and Origin. Authenticated negotiation works with the existing guest CSRF header. The shared production browser hub client uses direct WebSocket negotiation with the same guest cookie as the owner HTTP requests.

## Original browser contract

| Source | Integration |
|---|---|
| `StartRun` / `SoloRunStarted` | The host seals the run ID, selected mascot, rules/layout and tick rate. Canonical player slot 0 is mapped to the selected mascot in the presentation, including one-shot events. |
| `SoloRunRegistry` / `SoloMapping.ToOriginalFrame` | Every live projection is built from a committed `GameState` through `WorldFrame.Of` and `ChallengeMapping.ToWire`. The frame includes absolute tick, fixed-point position and distance, score parts, charge/deadline, pickup and Burst counts, held input, latch, ack and ordered reducer events. |
| Additive Original `SoloFrame` fields | `visibleCandles: [{id,x,y,phase}]` uses the pinned server candle plan and committed active IDs. Coordinates are arena world units. Phase is `telegraph` for `TelegraphTick <= tick < ActivateTick` or `active` for an uncollected active ID. Future, expired and collected candles are excluded. |
| Latest-wins delivery | `seq` counts pushed Original frames. `missed: [{tick,events}]` carries skipped event-bearing ticks in order, bounded to 64. `missedTruncated` signals an overflow. The client consumes the latest full state, identifies effects by run/tick/index and marks cue history incomplete after a lost frame or overflow. The terminal frame is flushed ahead of `RunEnded(final)`. There is no live replay request or reconnect for an interrupted Original. |
| `StreamInputs` / `InputRejected` | A single production stream carries monotonic sequence, estimated server tick, vector and held Burst request. Refusals carry the server tick and typed classes. The transport retries a refused Burst edge within its existing bounds and waits for the next commit when inputs are frozen. The shared keyboard binding neutralizes on blur or hidden state, and the transport resends neutral until acknowledged. |
| `FinishRun(operationKey)` | The server saves at completion. The browser asks for verification, retries the same key on `SavePending`, and offers **Retry save** if confirmation remains pending. It labels a result **Saved · verified** only with the terminal scored Original. After reload, even a key marked playing can be checked if the browser missed the terminal notification. Unfinished runs remain interrupted. |
| `GET /api/ghostlap/solo/originals` and `/{id}` | The same guest owns the list and verified detail. The list has no score, so the browser verifies recent rows sequentially and verifies older rows upon selection. The browser accepts a full-width Int64 seed as metadata and never uses the parsed number to recreate the world. |

The presentation remains the existing `PlayFrame` / `PlaySession` / `PlayScene` path. Original/child inspect and trace contracts stay available for later work.

## Verification

- **Production browser run** — headless Chromium with a disposable guest, via Vite and the two Production Kestrel hosts. Guest bootstrap, authenticated Solo negotiation and empty owner library all returned 200. Selected Yellow diamond (mascot 2), received 2,700 authoritative WebSocket frames, and reached tick 2700 in 90.941 seconds. Active and telegraph positions matched the delivered candle identities. Arrows and WASD changed the server's position. Blur produced an acknowledged neutral input and stopped movement.
- **Soulfire and scoring** — four real candle pickups, an Overlit state in the wire feed, then a Space-triggered `BurstManual` at T195. The saved score was 199 (99 movement + 100 pickup − 0 penalty). The automated driver pressed Space before it captured the Overlit HUD text. The saved screen rendered the selected head and world candles. Human review of motion, HUD and sound remains pending.
- **Durable save** — `FinishRun` returned `Saved` for `01a1f32c-4bba-7828-8cd0-ef539063d413`. The owner detail HTTP response matched the hub result. The row and verified detail appeared after clicking **My Histories** and again after refresh. PostgreSQL recorded a single `Complete` row at T2700 with the matching final-state hash. The healthy run did not enter a prolonged pending/retry state. Focused tests cover those responses.
- **Focus checks** — frontend typecheck, 33 focused Solo/shared play/multiplayer tests and one production Vite build passed. Production Realtime published successfully, and four focused host projection/delivery tests passed. A separate Chromium guest opened `/hubs/ghostlap` and created a real multiplayer room. No multiplayer round or expanded Spec 11 matrix was run.
- **Diagnostics** — the full browser driver's final blanket console assertion counted the expected initial `401 /auth/guest/me` before guest creation. A fresh setup-only check isolated that response and confirmed the follow-up calls returned 200. Chromium reported software WebGL readback warnings. Realtime logged both save calls as `Saved` for the same run, with no run failure. Existing container startup warnings about optional Kerberos and local data-protection keys did not affect readiness.

## Closure status (final)
- **Human review: APPROVED.** The user played a real 90-second Solo Original and confirmed movement, candles, Soulfire/Burst, expressions and overall presentation. The run ended as **Saved · verified** with score **2390**, and My Histories kept it after a refresh. No visual or gameplay changes were requested.
- **Codex real-host integration: VERIFIED.** Production Solo hub, PostgreSQL-backed Original save, owner-history retrieval, refresh persistence and the authoritative candle/event projection. Evidence is the "Spec 13.9 production integration" section above and the matching `docs/kiro-log.md` entry. Not rerun for this closure.
- **Handoff for 13.10:** a genuine saved Original now exists for replay and timeline development. Replay, Ghost Challenge and Rewind & Fork are **not yet browser-certified**. My Histories still shows them as unavailable, and nothing here should be read as claiming otherwise.

## Questions for the user (answered: review approved)

1. Does choosing a head and pressing Begin feel like the first act of the story, or is a short beat (a countdown on your side) wanted? (The server cannot hold the start.)
2. When the run ends, does the hourglass panel (sand still flowing while saving, still when saved, underlined verified score) feel rewarding, and is "My Histories / Another run / Home" the obvious next step?
3. In My Histories, is a short list with a detail pane enough for now, with Replay / Challenge / Fork visibly "not in this build yet"?
