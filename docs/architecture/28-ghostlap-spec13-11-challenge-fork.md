# Spec 13.11: Ghost Challenge and the cinematic historical Fork

**CREATIVE IMPLEMENTATION DONE · focused gate passed (tsc, 131 unit tests, vite build, bundle graph) · run end to end against the real local stack · HUMAN REVIEW PENDING · TECHNICAL ACCEPTANCE PENDING.**
One named dependency limits what you will see: the server does not send candle positions in Challenge/Fork frames yet (see "Dependency" below). Everything else in the journey is real.

## Try it

Vite on 5174 serves this code with hot reload (Compose hosts on 8181/8182, as in docs 26 and 27).

1. [Solo](http://localhost:5174/?screen=solo): record an Original (90 s) and wait for **Saved · verified**.
2. **My Histories** → select the Original → **Replay & choose a fork point**.
3. Wait for the history to load, click or key the rail to **T1140** (Home, then Shift+Right 38 times), press **Fork here**.
4. Watch the rewind, the preparing card, the CRT and smoke, the night world. Press **Begin from T1140** when you are ready. Play to the end (about 52 s). Press **Compare with the original**.
5. Also try: **Ghost Challenge** (My Histories row or the replay page, starts at T0), a Fork at **T2699** (one tick left), and T2700 (Fork stays disabled with its reason).

## What changed

| Area | Files |
|---|---|
| Wire: strict parsers for Prepared / Started / Frame / Finish / Result, all checked against what was asked for | `src/race/raceWire.ts` |
| Pure rules: rewind montage, the cross gate, start check, save interpretation, key, remembered claim | `src/race/raceModel.ts` |
| Transport: prepare, begin, frames, input, finish, cancel (Solo's input handling, anchored on the fork tick) | `src/race/RaceTransport.ts` |
| The journey screen: rewind, preparing, CRT + smoke, night reveal, Begin, race HUD, result | `src/race/RaceScreen.tsx`, `src/styles/race.css` |
| Wiring | `GhostLapApp.tsx` (race screen, dock held for a Fork's rewind), `screens.ts`, `presentation/types.ts`, `ReplayScreen.tsx` (Fork here, Ghost Challenge), `HistoriesScreen.tsx` (Ghost Challenge for a verified original), `HistoryDock.tsx` (`locked`), `playTypes.ts` (`PlayFrame.race`) |

Reused unchanged: `PlaySession`, `PlayScene` (night mode, ghost beneath the head), `HistoryScene`, `SceneTransition` (CRT + smoke), `AudioDirector` (`transition` then `night`), the history cache and loader, `SoloTransport` constants. No Marrow, rules, scoring, collision, Burst, record, ghost replay, authorization or fork-tick change. No backend change. No new dependency, asset or sound.

## The arc, and what each step really is

| Step | Truth behind it |
|---|---|
| Fork here (T) | Offered only for a server-confirmed Original, T0..T2699, whose recorded state at T is loaded. T2700 and saved futures say why not. |
| Rewind (about 1 s) | The recorded states of the Original's own history, newest to T, at integer ticks. It starts from the end of the unbroken run of states that are loaded (it never skips a gap or interpolates), brakes into T and is exactly T at the end. The dock stays, locked; its hourglass sand runs back. Reduced motion skips it. A rewind under 3 ticks is skipped. |
| Preparing | `PrepareFork(OriginalId, ForkTick)` runs from the click, in parallel with the rewind. The player sees the recorded frame at T and an indeterminate "Preparing history" card until the server answers Ready. Failure shows the server's reason with Try again / Back. |
| Cross to night | `mayCross` requires the verified Ready reply **and** the recorded state at T **and** a finished rewind. CRT + smoke play, the day scene is swapped for the night `PlayScene` under full cover, the dock leaves, the arena re-fits. Audio goes to `transition` (silent) here. |
| Ready | The world at T: the head and the cyan ghost on the same recorded state, no forced separation. The card names T and the first tick that is theirs (T+1), says the clock cannot be paused, and has one button. |
| Begin | `StartChallenge`. The server clock starts with this reply. The first authoritative state is drawn and keys are live in the same step. `ghost_main_theme.wav` and wind → wind → knock start here (night is only accepted after `transition`). |
| Start check | The server's first frames are compared with the recording the player inspected: ghost state hash, position, charge, score, pickups. The HUD shows "✓ same position, charge, score and state hash", or names what differs. Seen on the real host for T1140 and for T0. |
| Race | Living head in its natural colour; the ghost is the server's `ChallengeFrame.ghost`, never offset, never interacting; its events never become living events or sounds. HUD: score, Soulfire, original score **at the same tick**, signed Δ. Events from the Start reply belong to the recorded past and are not replayed. |
| Result | `FinishFork` for a Fork, `FinishChallenge` for a Challenge, by the durable key, idempotent, same-key retries. "Saved · verified" only for a Completed answer whose kind, original, minted key, fork tick and full round match the race played. Then **Compare with the original** (real replay of `/children/{id}`), My Histories, Race again. |
| Leaving | Before Begin: nothing was started, the server is told to drop the prepared session. During the race: confirmation; an unfinished race is never saved. |

The server mints the stored key as `fork.<key>` or `challenge.<key>` (`SoloBranchKinds.KeyFor`). The browser accepts exactly that for the kind it asked for, so a Fork can never be resolved as a Challenge.

## Evidence (real local stack, headless Chromium, disposable guest, no page.route)

- Original recorded and saved: score 1721, `01a1f379-ca81-7719-a528-a3ac7eb37ef7`.
- **Fork at T1140:** rewind T2700 → T1140; prepare, cross and ready; Begin → first state matched the recording (hash, position, charge, score); audio went `menu` → `transition` (silent) → `night` (`ghostMainTheme`, wind step 0) 247 ms after Begin; real-time race (T1195 about 1.8 s after Begin); saved **Fork #01a1f37b**, your future **1041**, original **1721**, Δ **−680**. Compare: at T1140 original 710 = future 710 (±0), at T2700 1721 / 1041 / −680, equal to the race's own numbers.
- **Fork at T2699:** no rewind, prepare, night, Begin, one tick, saved **Fork #01a1f37d** (1721 / 1721, ±0). **T2700:** Fork disabled: "Nothing is left to change at T2700."
- **Ghost Challenge at T0:** first state matched the recording at T0; a full 90 s race; saved **Challenge #01a1f37e**, 1663 vs 1721, Δ −58.
- Only console error: the expected first `401 /auth/guest/me` before the guest exists. 54 history requests in the whole run, 44 of them trace chunks (cached and shared with the replay screen).
- Screenshots (not committed evidence of feel): `TestResults/ghostlap-race/`.

## Dependency (blocks full playability; creative work did not need to stop)

**Candle positions in Challenge/Fork frames.** `ChallengeMapping.ToWire(WorldFrame, bool)` (`src/Hosts.Realtime/GhostLap/Solo/SoloChallengeRegistry.cs`, used for `ChallengeStarted.Living` and `ChallengeFrameView.Living`) carries `activeCandleIds` but no coordinates. `SoloMapping.ToOriginalFrame(state, plan)` (`SoloRunRegistry.cs`, lines 17-31) already adds `VisibleCandles` for Original frames from the pinned plan. Without the same projection for challenge frames, the night race has **no candles drawn**, so a living player cannot see what to collect; movement, Burst, ghost and scores are live.
- Required: the same additive `VisibleCandles` projection on `ChallengeStarted.Living` and `ChallengeFrameView.Living`, built from the plan pinned for the original's seed and layout, in arena world units, `active` or `telegraph` only.
- Smallest success check: in one Fork and one Challenge, every `living.visibleCandles` id set equals the active ids plus the telegraphed ones, and `ActiveCandleIds` after a living pickup no longer contains that id.
- Browser side is ready: `living.visibleCandles` is read when present (`delivery` becomes "rich", the "no candle positions" notes disappear, `PlayScene` draws them with the night light). Needs a contract-compatibility check (additive) and, if the team wants one, an ADR.
- Same gap, saved histories (13.10 dependency): trace frames list candle ids without coordinates, so a replay cannot draw them.

## Decisions

1. **Own transport, Solo's constants.** `RaceTransport` copies `SoloTransport`'s input handling (it is private there) rather than changing the approved Solo path. A later consolidation would extract one input pump; not done now because it would touch the approved Solo run.
2. **The night reveal reuses the real `PlayScene` and the real transport.** The reveal is a recorded-state `PlayFrame` (same identity as the race) so there is no remount, halo flash or jump at Begin.
3. **Cards sit in the side rail away from the head** at the chosen tick (a head near the west wall had been hidden behind the first, left-only version).
4. **Prepare runs during the rewind** instead of after it; the wait after a 1 s rewind is an honest "Preparing history" on the recorded frame, not a fake animation.
5. **Fork here is unavailable while viewing plays** ("Pause on the moment you want"), so the label and the tick can never differ.

## Defects and not done

1. Candle positions (above).
2. `PrepareFork` took about 8 to 11 s on the first call for a history on this machine and under 1 s when the server already held the trace. The wait is shown honestly; no timer or smoke covers it.
3. The CRT + smoke crossing took 5 to 7.5 s in headless software WebGL (the transition clamps each frame to 50 ms, so a slow renderer stretches it). Its nominal length is the 13.6 value. Not measured on real hardware. No FPS, memory, Safari, high-DPI, screen-reader or full keyboard-only pass was run.
4. Not covered by the real run: a refusal path (preparation busy, challenge already active), a dropped connection mid-race, and recovery of a save by key after a refresh (`PendingRace` is written and the finish call exists; the Histories-screen recovery note is not built).
5. The dev "Preview fixtures" strip overlaps the top clock in screenshots; development only.
6. Codex validation of fork/hash/identity/auth was not run; the Start check above shows hash equality on two real starts, which is not a certification.

## Questions for the user

1. **Emotional transformation.** From pressing Fork here to the night world: does the rewind through your own run make "I am going back" land, and does the held "Preparing history" beat feel like tension or like waiting? Should the rewind start later so it ends nearer to Ready?
2. **Control handoff.** After the smoke clears the world waits for you and the clock starts only on Begin. Is that clear, or do you want a short on-screen cue at the first moment of control (and is "Begin from T1140" the right wording)?
3. **Ghost clarity and realness.** When the cyan ghost sits exactly under your head at T and then pulls away only as your choices differ, does it read as "the same world, a different future"? Is the original's score and Δ beside your own the right amount of comparison during the race?

## Final backend handoff · 19 November 2026

**CANDLE BLOCKER CLEARED · HUMAN REVIEW PENDING.** This check closes the candle dependency recorded above. The full earlier 13.11 journey and the frozen backend behavior use the existing [Spec 11 acceptance evidence](16-ghostlap-spec11-final-acceptance.md) and the browser run documented above.

### Files and contracts

| Files | Change |
|---|---|
| `src/Hosts.Realtime/GhostLap/Solo/SoloChallengeRegistry.cs`, `src/Hosts.Realtime/GhostLap/Solo/SoloRunRegistry.cs` | One Solo visibility projection serves Original frames, `ChallengeStarted.Living` and every `ChallengeFrameView.Living`. The live race holds the pinned living context's plan and maps each committed world's own candle IDs. The ghost's projection uses the same plan with the Original's recorded IDs. |
| `src/GhostLap/Solo/SoloOriginals.Trace.cs`, `src/GhostLap/Solo/SoloChallenges.Children.cs`, `src/Hosts.Realtime/GhostLap/Solo/GhostLapSoloHistoryRoutes.cs`, `src/GhostLap.Contracts/Solo/GhostLapSoloContracts.cs` | Original inspect and Original/child trace add `visibleCandles: [{id,x,y,phase}]` to `SoloGhostFrame` under `solo-history/1`. Each authorized response builds the `ghost-v1` / `arena-v1` plan once from the Original's exact Int64 seed, then uses the restored frame's IDs and absolute tick. Coordinates are arena world units. Phases are `active` and `telegraph`. Seed metadata on the internal trace chunk is in memory only. |
| `web/apps/ghost-lap/src/history/historyWire.ts`, `web/apps/ghost-lap/src/history/HistoryScene.ts`, `web/apps/ghost-lap/src/history/ReplayScreen.tsx`, `web/apps/ghost-lap/src/race/RaceTransport.ts` | History parsing accepts and validates the additive field. Replay draws the viewed history's cached candles through the existing CandleField. The night reveal uses the recorded start frame, then the existing live consumer takes over on Begin. The live parser already supports rich living frames. Older basic payloads still parse. |
| `tests/Integration/GhostLap/GhostLapSoloHistoryHostTests.cs`, `tests/Integration/GhostLap/GhostLapDeferredEventTests.cs`, `tests/Integration/GhostLap/GhostLapForkAcceptanceTests.cs`, `web/apps/ghost-lap/test/history.test.ts`, `web/apps/ghost-lap/test/race.test.ts` | Extended the existing projection, ownership, sender, parser and recorded-reveal checks. Updated the Spec 11 acceptance test's mapper call to the new signature. |
| `docs/architecture/28-ghostlap-spec13-11-challenge-fork.md`, `docs/kiro-log.md` | Recorded the focused verification and local review handoff. |

### Verified on the rebuilt local Compose host

- **Real Chromium and production Solo hub.** Ghost Challenge from T0 delivered 12 visible candles on start and 11 at T28. Fork from T1140 delivered 11 at start and 12 at T1168. Both starts matched the recorded tick, position, charge, score, ghost state hash and candle array. The sampled live IDs, coordinates and telegraph phases matched the pinned history plan. Screenshots showed flames with their existing night light pools, telegraph rings, and the ghost beneath the living head at overlap. The missing-position notes were absent at reveal and during play.
- **Living and ghost separation.** At T53 the recorded ghost collected a candle that stayed active for the living Challenge. A keyboard-driven living pickup of `c-0008` at T67 removed it in the same authoritative frame.
- **Saved replays.** Original `01a1f348-2f96-7931-b389-e135fa088bf8` and saved Fork `01a1f34c-2673-7a0b-9e69-db10d2acf6de` came from the existing local database. Every shared-prefix state hash and candle array matched through T1140. The saved candle sets first differed at T1182, with identical coordinates for a given candle ID. Both replays visibly drew the shared candles and telegraph at T34. Each initial replay load made 10 trace requests, then 24 key seeks made zero additional trace requests per replay.
- **Short Fork and refresh.** T2699 started from the recorded candle state and completed at T2700. Two fresh `FinishFork` connections after reload returned the same saved Fork, source, fork tick and signed delta. The catalog contained one row for the key. `FinishChallenge` on that key returned `not_found`. A separate reload during the T1140 Fork left child detail at 404 and a fresh `FinishFork` returned `not_complete`, with no result. This exercises the server lookup after refresh. Automatic recovery in the Histories UI remains a gap.
- **Evidence.** Screenshots and machine-readable starts are in ignored `TestResults/ghostlap-candles/`. Reused the earlier disposable guest and saved histories. The only newly completed browser Fork ran one tick. Zero full-length races were run for this handoff.

### Local review

The Compose hosts and Vite are running. Open [Solo](http://localhost:5174/?screen=solo) or [My Histories](http://localhost:5174/?screen=histories) in the same browser profile used for your saved Original. Choose **Ghost Challenge** from a verified Original in My Histories. For a Fork, open **Replay & choose a fork point**, seek to T1140 or any point through T2699, select **Fork here**, then **Begin** after the night reveal. Use **Saved Futures** to open a saved Fork and scrub its shared past and new future.

From the repository root, the restart commands use the existing local overlay and volumes.

```sh
cd /Users/rroy/Desktop/the-sands-of-time-phase1
docker compose --env-file .env --env-file .env.ghostlap-solo -f deploy/compose/docker-compose.yml up -d --build api realtime
cd web
VITE_API_ORIGIN=http://localhost:8181 VITE_REALTIME_ORIGIN=http://localhost:8182 npm run dev -w @sands/ghost-lap
```

### Focused correctness gate

- **Verified now.** 34 selected Release Solo domain tests passed (Challenge, Fork, trace, save and history catalog). These cover exact canonical state and hash at T, an immutable source prefix, first choice at T+1, T0 and T2699 validity, T2700 refusal, child kind and signed score delta, operation-key idempotency, and foreign/unknown key refusal.
- **Verified now.** Seven selected Release host integration tests passed (Solo history, Solo hub smoke and deferred sender). The HTTP checks include exact ID/phase/coordinate projection, inspect/trace parity, Fork prefix parity, and indistinguishable foreign and unknown responses for Original and child detail and trace. The hub checks include start/live delivery, input refusals, foreign/unknown preparation and interruption. The sender check exercises independent living and ghost candle sets through a real reducer pickup.
- **Verified now.** 47 focused history/race browser unit tests, frontend typecheck and production Vite build passed. Release Realtime build passed with zero warnings and errors. Compose Realtime readiness returned 200. The selected domain build reported three pre-existing xUnit1031 warnings from the multiplayer instrumentation tests.
- **Covered by prior evidence.** Full real-time Challenge and Fork completion, cold restart, extended authentication checks and parallel multiplayer are recorded above and in the Spec 11 acceptance. This handoff's short real-host run exercised the existing refresh-to-key lookup and interruption, plus the T2699 one-tick save.
- **Outside this smoke.** The preparation-busy and already-active paths were not driven in Chromium. Human playability review, Safari, high-DPI, hardware rendering performance and accessibility review remain pending. The existing development fixture strip still overlaps the race clock in the Vite screenshots.

## Final acceptance — human PASS and Codex handoff (for 13.12 agents)

**Status: 13.11 ACCEPTED. HUMAN REVIEW: PASS** (user played the full Fork and Ghost Challenge experience). Verified by the user: movement, WASD/arrows, smoothing, historical cyan ghost, genuine divergence, candles, night presentation, expressions, Burst, same-tick score comparison, verified saving, Saved Futures and replay. No visual or gameplay changes were requested.

### Codex fixes now part of 13.11
- **Candle positions.** Authoritative `visibleCandles` projection for Challenge/Fork starts and live frames, Original inspect and Original/child traces (additive, `solo-history/1`); the replay renderer uses the selected history's cached candles. This cleared the earlier backend dependency (see the final backend handoff above).
- **Fork input-sequence bug.** The inherited acknowledged input sequence from the source at T must initialize `RaceTransport`. It previously restarted at zero. Codex verified real-host Fork and Challenge movement, authoritative stops, candle behaviour and historical correctness after the fix.

### LOCKED (approved behaviour; 13.12 must not alter without a new user decision)
- Fork/Challenge journey: prepare, ~1 s authentic rewind to exactly T, honest "Preparing history" until verified Ready, `mayCross` gate, CRT + smoke only under real Ready, press-Begin handoff, `StartChallenge` only on Begin.
- Night theme + wind→wind→knock only after Begin succeeds; audio menu → transition → night.
- Ghost drawn at exact overlap beneath the head, no offset; ghost events never become living events or sounds.
- Start check against the inspected recording; reply must match the ask; `fork.<key>` / `challenge.<key>` prefixes; `FinishFork` for Fork, `FinishChallenge` for Challenge.
- Fork inherits full source state at T, living decisions from T+1, both end at T2700; T2700 Fork disabled with reason; Challenge from T0 with no dock.
- Cards in the side rail away from the head; the same-tick score/Δ comparison; the Fork keeping the locked dock during rewind.
- `RaceTransport` input handling mirroring Solo, now seeded with the inherited acknowledged sequence.

### Not claimed
Not performed or not passed here: Safari, high-DPI, FPS/memory, accessibility/screen reader, preparation-busy and challenge-already-active refusal paths in Chromium, mid-race disconnect, automatic save recovery in the Histories UI (server key lookup after refresh was exercised; the UI note is still a gap), cold-restart/release gate, CI. These remain for 13.12. The development fixture strip overlapping the race clock is dev-only.
