# Spec 11 tasks — Build the Marrow proof, one playable vertical slice at a time

**Task status is tracked below.** Spec 10 backend is complete; active new multiplayer games use `ghost-mp-v2`, and solo history remains pinned to Spec 06 `ghost-v1`. **Do not change either profile, Marrow, Revenant or their pinned hashes.** Spec 12 is not a dependency. The eventual renderer/audio/art implementation is separate and may begin planning in parallel.

**Credit rule for every task:** Kiro implements/inspects only the named product behavior and runs quick focused tests. Codex Medium handles repeated PostgreSQL/Testcontainers, cold-restart, long-running integration and repository checkpoints. Do not make Kiro re-execute large suites or rebuild completed Spec 10. Reuse existing ports, persistence and tests before creating new ones. Capture short reproducible evidence in `docs/kiro-log.md`. Do not call a deferred verification gate passed.

- [x] **11.0 Inspect actual frozen ports and choose the minimum integration (short gate)**
  - **Inspect:** Spec 02 Timeline source/verify/restore/branch and manifests; Spec 06 `ghost-v1` codec/reducer, goldens, `SoloOriginals`/`SoloBranches`, existing solo host/API and schema; Spec 03 principal/cookie and actual DI/DB grants. Read relevant existing ADRs; do not substitute design guesses for code.
  - **Decide:** smallest supported real original-recording path (existing live solo owner vs bounded played-input capture with canonical **server-side** verification), verified-source/catalog owner identity, safe retry of completed save, read trace and exact fork port. An interrupted **unfinished** run may restart; no live writer generation/resume system is required. Avoid duplicate logs, catalogs and new schema if existing services work.
  - **Produce:** compact concrete API/data-flow mapping (with real names, which existing services get reused, any *genuinely missing* adapter) and 1–2 golden/restore smoke checks. If essential actual canonical branch/verification capability is missing, document the specific incompatibility and minimum compatible path before modifying frozen Marrow.
  - **Done when:** subsequent tasks have an evidenced minimal implementation path, with no invented `Timeline` interfaces. **Stop after that—not a multi-day research report.**Task 11.0 is strictly a short, source-grounded integration gate. Inspect the actual implementation, identify reusable ports and missing adapters, and produce a concise handoff for Tasks 11.1–11.5. Do not implement Task 11.1, redesign Marrow, add schemas, or execute extensive tests. Stop when the concrete integration path is established.

- [x] **11.1 Record and durably save ONE genuine solo original end-to-end**
  - **Produce:** a real `ghost-v1` 30Hz 2700-tick solo run with its actual movement/candles/charge/Overlit/manual+auto Burst rules; canonical inputs/checkpoints/replay via existing Spec 06/Timeline services; verified final hash; owner-bound saved identity and minimal metadata *only if needed*. API exposes honest `Saved` / `SavePending` / failed outcome; stable operation key protects retries. Do not trust client-reported score/final hash or add long-lived crash-resumable solo writers.
  - **Verify (Kiro focused):** full original final reducer hash, one saved owner-visible record, identical retry returns the same identity, conflicting retry denied, interrupted partial run cannot be listed as original; solo `ghost-v1` goldens preserved. **Delegate to Codex:** real PostgreSQL cold reload, one lost-response/ambiguous-save scenario, real owner and foreign-user reads.
  - **Done when:** a completed real original can be saved and retrieved after refresh with exact versions/hash; no false `Saved`. This is the first product milestone—do not proceed to complicated fork infrastructure before it works.
  - **Codex verification passed.** A full 2,700-tick original saved through the production Realtime hub, survived a cold host restart, and replayed to the same hash from PostgreSQL. The owner recovered the same save after a lost reply and a retryable `SavePending`. See the Task 11.1 checkpoint in [the Kiro log](../../../docs/kiro-log.md#spec-11-task-111-codex-verification-checkpoint).

- [x] **11.2 Exact historical seek and ghost trace**
  - **Produce:** verified owner-only source inspection and bounded tick-indexed trace using the actual frozen checkpoint/restore/reducer. Include the needed position, Soulfire/Overlit, true source scores, candle/Burst/action cues and state/hash identity. Keep historic ghost **read-only** and all visuals logically separate from living gameplay. One bounded trace projection/chunking as actually needed; no default new score-sample table/cache.
  - **Verify:** full-state restore and source hash at T=0,1140,2699,2700; a nontrivial T1140 source with movement remainder, collected candles and charge/Overlit where reachable; deterministic event/score samples; unsupported codec and foreign-owner refusal; trace never substitutes final score for historical score. **Delegate:** one measured cold trace/seek and source replay after host restart.
  - **Done when:** the real timeline can scrub to any integer tick and faithfully show the original past with no invented historical data.**Task 11.2 — Implementation clarifications**1. **Use the real canonical history, not reconstructed visual guesses.** Historical position, score, Soulfire, Overlit state, candle status and Burst/action cues must be derived from the verified `ghost-v1` source and deterministic replay transitions. Use the state at the requested tick, not final-run totals or interpolated positions presented as authoritative history. Keep tick 0 and tick 2700 boundary semantics explicit. Do not implement PixiJS rendering, animation, ghost-player collision or branch creation in this task.2. **Keep trace retrieval efficient without creating a second history system.** Reuse the existing checkpoint/restore/compare ports and measure one actual full-run cold trace. Do not automatically perform thousands of independent PostgreSQL restore round trips for a full trace if a bounded sequential replay or existing chunked projection is available. Use bounded tick ranges, stable ordering and payload limits. Add a new Marrow read-range port only if measurement establishes a real blocker and existing frozen-compatible ports cannot support the required trace.3. **Protect source identity and correctness.** Every returned trace chunk must belong to the requested verified original and pinned `ghost-v1` rules/codec identity. Reject unsupported versions and unauthorized sources. Keep historical score and events tick-aligned, including the requested T=0, 1140, 2699 and 2700 checks. Use a reproducible nontrivial fixture that genuinely exercises charge, collected candles and Overlit behavior. Kiro runs focused tests; delegate cold-restart trace, full-range performance measurement and repeated chunk/authorization regressions to Codex Medium.

- [x] **11.3 Race the original ghost in a fresh full challenge**

  - **Produce — historical preparation:** Authenticate and verify the selected completed `ghost-v1` original. Prefetch its full bounded, immutable T0–T2700 trace using the existing Task 11.2 services before starting challenge gameplay. Maintain an explicit `Preparing` → `Ready` → `Running` lifecycle with truthful failure/cancellation handling. Reuse an already prepared trace only when original ID, pinned rules/codecs and verified final hash match. Bound active-session memory and release buffers when no longer needed. No database cache, duplicate input archive or new Marrow port.

  - **Produce — complete gameplay:** Start the living challenge from the original's exact tick-zero seed, layout, mascot configuration and candle schedule. Preserve all existing `ghost-v1` movement, wall collision, score, candle, Soulfire 0–4, manual Burst and 240-tick automatic Burst rules. The living player has independent movement, candle collection, Soulfire, timers, actions and scoring. The historical ghost uses only verified read-only source frames; it never collides, collects, attacks, receives damage or changes the living world.

  - **Produce — smooth ghost playback:** Index the prepared historical trace directly at the authoritative logical tick. Define T0 as initial state and Tn as the state after n transitions. Source position, expressions, charge, Overlit, candle cues, Burst cues and historical scores remain correctly aligned with the living tick. Do not invoke PostgreSQL, `RestoreAsync`, `TraceAsync`, `InspectAsync` or `CompareAsync` from the live 30 Hz gameplay tick loop. Preserve the complete event data needed for later PixiJS animation and sound without implementing browser rendering now.

  - **Produce — real independent challenge result:** Record the living player's canonical inputs and checkpoints using existing Spec 06/Timeline services. Completing the full 2,700 ticks produces a verified, owner-bound child of the source at fork tick 0, semantically identified as a `Challenge`. It must never appear as a new original. Reuse existing durable child metadata where possible; do not create new persistence infrastructure solely for classification. Preserve the exact original source hash and recorded future.

  - **Verify — Kiro focused:** Correct preparation readiness, cancellation and retry; no partial challenge starts; source/child state isolation; identical initial candle opportunities with independent collections; exact source ghost positions and scores at T0, T1140 and T2700; no off-by-one ghost playback; normal movement/charge/Burst behavior; independent living score divergence; verified child identity; original hash unchanged. Confirm through focused instrumentation/tests that zero history-restoration calls occur after gameplay starts. Run short deterministic full-round tests without realtime waiting.

  - **Delegate — Codex Medium:** One real-host authenticated full 90-second challenge and saved-child cold replay; measured initial trace preparation time and resource use; one representative contention test while a living solo session is advancing; owner/foreign challenge authorization; repeated challenge/retry and source immutability regressions. Reuse existing Spec 11.1–11.2 evidence and avoid duplicate long rounds.

  - **Performance boundary:** Challenge admission requires a verified prepared trace, but does not require any artificial backend loading delay. The later browser presentation may use a cinematic rewind/preparation sequence. Once running, the ghost must be driven by local frame indexing, not history database reads. If trace preparation fails, the challenge must not silently start with incomplete or invented history.

  - **Done when:** An authenticated guest can prepare an exact saved original, race its fully non-interactive historical ghost through a complete 2,700-tick challenge, finish with a genuine independently verified child result, and preserve the original untouched. Full realtime, cold replay and preparation-contention verification may remain pending Codex evidence after Kiro's implementation; do not claim those delegated checks passed prematurely.

- [x] **11.4 Fork at T and save genuinely independent futures — Marrow's central proof**

  - **Produce — exact historical fork:** Select an owned, completed, verified `ghost-v1` original. Use the existing frozen Timeline restore/branch services and `SoloBranches` to fork at any integer tick T where 0 ≤ T ≤ 2699. T0 is the initial state; T2700 is inspect-only. Verify the actual source state/hash at T and preserve the canonical original prefix through T. The child's new inputs and transitions begin strictly at T+1.

  - **Produce — complete state continuity:** Inherit the full canonical state at T, not merely the player's position or score. This includes seed/layout, absolute tick, movement remainder, score components, candle schedule and consumption, Soulfire stage/charge, Overlit deadline, pending actions, and all other reducer state. Existing events through T must not be applied again. The child continues the remaining **2700 − T** transitions, ending at absolute tick 2700 without resetting the clock, score or arena.

  - **Produce — independent future and original ghost:** The living child uses the unchanged `ghost-v1` reducer and may collect different candles, move differently, use Burst differently and produce a different score. Continue displaying the verified original future as a separate, non-interactive historical ghost indexed by the same absolute tick, using the Task 11.3 prepared trace where applicable. Neither world can modify the other. Do not restore historical ghost frames from PostgreSQL inside the living tick loop.

  - **Produce — durable sibling branches:** Persist the completed child through the existing canonical Timeline/`SoloBranches` path with owner, original parent, fork tick, pinned rules/codecs, verified final state/hash, and explicit `Fork` classification using existing metadata. Keep `Fork` distinct from Task 11.3 `Challenge`. A second operation at the same source/T may create a distinct sibling; retries of the same operation must not create duplicate children. No nested child forks, unfinished-run cross-process recovery, extra branch database or new Marrow port.

  - **Produce — minimum integration:** Reuse the existing challenge preparation, authoritative input execution, completion, ownership and bounded trace-buffer facilities wherever compatible. Adapt only what differs for a nonzero starting tick and `Fork` classification. Do not build another 30 Hz runner, replay engine or caching infrastructure. Preserve exact source identity and fail closed for unfinished, foreign, corrupt, unsupported or incompatible originals.

  - **Verify — Kiro focused:** Deterministic source forks at T0, T1140 and T2699. Prove T2699 requires only one remaining transition and T2700 is not forkable. At T1140, prove byte-exact restored canonical state and matching source history through T, including nonzero movement remainder, historical candle consumption, current score, Soulfire/Overlit and action-boundary cases. Verify the first new transition is T1141; no reset, duplicated source event or off-by-one scoring. Produce two sibling forks from T1140 with different legal inputs, different independently verified futures and an unchanged original. Check aligned historical scores using existing comparison ports. Test duplicate operation-key behavior and owner/version refusal.

  - **Delegate — Codex Medium:** Independently verify source plus both sibling histories against real PostgreSQL after a cold host restart; check canonical prefix provenance, hashes, parent tick, full-state continuity, sibling independence, owner isolation and durable retry identity. If Task 11.4 exposes the realtime fork flow, perform one authenticated, real-time T1140→T2700 continuation (1,560 ticks) and cold replay; otherwise leave final public transport acceptance to Task 11.5. Reuse existing 11.1–11.3 evidence. Do not rerun another full original, four-room multiplayer benchmarks or large recovery matrices.

  - **Done when:** A guest can select a genuine past tick, restore its exact complete state, make new decisions starting at T+1, save a verified independent future and create a distinct sibling without changing the original or its recorded future. Kiro must report delegated real-host/cold-replay verification as pending until Codex actually passes it.**Scope boundary:** This is the actual deterministic fork implementation, not the PixiJS rewind animation, browser timeline interface, social sharing or a new Timeline architecture.
- [x] **11.5 Complete the authenticated solo timeline journey, same-tick comparison and renderer-ready host contracts**

  **Objective:** Connect the already-verified Spec 11 recording, history inspection, ghost challenge, arbitrary-tick fork and comparison capabilities through the existing production API/Realtime hosts. Finish with a truthful, private, persistent end-to-end journey that the future PixiJS client can consume directly.

  **Keep implementation minimal.** The canonical gameplay and Timeline capabilities already exist and passed Tasks 11.1–11.4. This task is an integration and presentation-data boundary, not another engine-development phase.

  ### 1. Inspect and reuse the real existing contracts

  - Inspect the current `GhostLapSoloHub`, `GhostLapSoloContracts`, `SoloChallengeRegistry`, `SoloChallenges`, `SoloOriginals`, `SoloBranches`, existing DI/auth/role configuration and the Codex Task 11.2–11.4 handoff reports.
  - Reuse `SoloOriginals.ListAsync`, `OpenAsync`, `InspectAsync`, `TraceAsync`; existing `SoloBranches` restore/compare/persistence facilities; and the working Challenge lifecycle.
  - Reuse `SoloChallenges.BeginFork`, `PrepareAsync`, `StartAsync`, input submission, tick commit, completion, `ResolveForkAsync`, and the existing owner-scoped persistence.
  - Confirm actual method signatures and typed response DTOs before implementing transport adapters. Do not invent Timeline APIs or duplicate authoritative game simulation.
  - Make only genuinely missing API/Realtime integration changes.

  ### 2. Expose the complete authenticated ghost Challenge and Fork flow

  - Extend the existing `/hubs/ghostlap-solo` hub and `SoloChallengeRegistry` to support arbitrary-tick `Fork` through the same preparation, start, input streaming, finish, cancellation and recovery lifecycle as `Challenge`.
  - The selected original and fork tick must be supplied explicitly. `Challenge` remains a fork at T0, classified as `Challenge`; arbitrary-tick `Fork`, including T0, remains classified as `Fork`.
  - Preserve the existing absolute-tick contract: at fork tick T, living state exactly equals the verified source state at T; new living transitions begin at T+1 and finish at T2700. T2700 is inspect-only.
  - Carry `Kind`, `ForkTick`, source identity, verified final source hash, owner-bound operation identity, seed, mascot and pinned reducer/layout/codec versions through preparation, start, live and finish responses where relevant.
  - Reuse existing Challenge transport methods where compatible. Add the minimum necessary Fork entry point and typed mappings; do not create another hub, runner or matchmaking system.
  - Support honest `Completed`/`Saved`, `Pending`/`Unavailable`, `Failed`, `Conflict` and cancellation outcomes using existing domain semantics. Never return success for a child whose canonical completion has not been verified.
  - Route post-refresh/restart Fork resolution to `ResolveForkAsync` and Challenge resolution to the existing Challenge resolver. Identical retries must return the same durable child.
  - Never start gameplay before the full verified historical trace is prepared.

  ### 3. Expose owner-only history inspection and bounded trace retrieval

  - Add minimal authenticated inspect and trace endpoints under the existing solo-original resource. Use the actual `SoloOriginals.InspectAsync` and `TraceAsync` services.
  - Inspect accepts an owned original ID and exact integer tick T0–T2700.
  - Trace accepts an owned original ID, start tick, bounded count **1–300**, and contract version; returns ordered frames and a nullable continuation tick.
  - Every response carries sufficient pinned source identity to prevent mixing frames from different originals or versions: original ID, final hash, reducer/input/state/context codec versions, layout and terminal tick.
  - Preserve actual historical position, Soulfire/Overlit, historical score/components, active candle IDs, state hash, ordered gameplay cues and exact tick semantics.
  - Support loading the complete 2,701-state trace through bounded consecutive chunks. Do not require a server request for each slider movement once frames are loaded.
  - Use existing verification and owner authorization. Never expose public raw Timeline reads, unverified histories or a debug route that bypasses guest ownership.
  - Do not add another per-tick score table, input archive, Redis cache or new Marrow range-read port by default.

  ### 4. Provide a minimal owner-facing Challenge/Fork child catalog

  - Keep the existing original library strictly limited to completed Originals.
  - Expose an authenticated, bounded/paginated list of the owner's **completed saved children**, containing Challenge and Fork classifications, child identity, source original ID, fork tick, pinned versions, final score/hash and completed status.
  - Provide the minimum owner-safe child detail/selection needed for replay, comparison and refresh recovery.
  - Reuse existing `solo_branches` metadata and read-only queries; add only genuinely missing SELECT methods. No new schema, social-sharing links or general-purpose history catalog infrastructure.
  - Pending/incomplete children must not be misrepresented as completed saved histories.
  - Foreign and unknown IDs remain indistinguishable to unauthorized callers.

  ### 5. Deliver genuine same-tick comparison

  - Reuse the existing canonical `SoloBranches.CompareAsync` / aligned comparison services and actual historical state/score data.
  - For any valid comparison tick t, report **source score at t**, **child score at t**, and **signed delta = child score at t − source score at t**.
  - Include available authentic score breakdowns, including resolved-distance and candle-derived scoring, without inventing estimates.
  - The original's final score must never replace its historical score at earlier ticks.
  - A Fork child and source must agree through fork tick T. Differences may begin only after T; equal scores at some later ticks do not imply identical states.
  - Support bounded, stable, tick-indexed comparison data suitable for future line graphs, rewind markers, score overlays and result screens. Do not reconstruct an entire history on every visual frame.
  - Keep source, child and sibling histories immutable throughout comparison.
  - Do not compute comparisons by modifying or replaying the living game state.

  ### 6. Finalize authoritative visual-data contracts

  The future renderer must be able to represent the actual game beautifully without creating fake gameplay events.

  - Preserve same-tick `Living` and `Ghost` frame pairing with authoritative tick, score and signed delta.
  - Ensure the browser-facing living frame exposes the relevant already-existing `WorldFrame.Events` and active candle identifiers, as well as movement, Soulfire/Overlit and Burst-related state. The historical ghost already exposes ordered original events and must remain read-only.
  - Preserve authentic candle collection/expiration, Soulfire stage changes, manual Burst, automatic Burst, terminal cues and their correct tick ordering.
  - Expose the necessary fixed-point score/distance components where required for accurate scoreboard and timeline presentation, without indiscriminately exporting internal reducer fields.
  - Carry sufficient identity and event sequencing so the client can interpolate visual movement and render each authoritative one-shot effect exactly once, including after resync.
  - Preserve original/living separation: ghost visual events never damage, collect, collide or change living gameplay.
  - Keep a concise typed-contract handoff for Spec 13 explaining tick meanings, fixed-point units, authoritative events, loading readiness, source identity and client interpolation boundaries.
  - **Do not implement PixiJS, sprites, shaders, animation, cinematic rewind, sound, client-side prediction or browser UI in Task 11.5.**

  ### 7. Protect loading and 30 Hz gameplay responsiveness

  - Retain the Task 11.3–11.4 model: fully prepare the historical trace once; reuse the bounded verified active-session buffer; drive ghost playback by direct absolute-tick indexing.
  - A full trace may take approximately 5–6 seconds to prepare on the previously measured local setup. This is accepted. Do not add an artificial server-side delay for animation purposes.
  - When preparation is incomplete, failed or cancelled, communicate truthful state and never launch an incomplete ghost.
  - During live gameplay, issue **zero historical ghost restore, inspect, trace or compare calls** in the authoritative tick loop. Normal canonical living recording I/O remains permitted.
  - Preserve all existing solo 30 Hz mechanics, full 2,700-tick challenge and absolute-tick fork continuation.
  - Do not change the frozen reducer or tick cadence speculatively to improve measured network delivery intervals.

  **Specific transport handoff concern from Task 11.3:** the real-host run completed 2,700 ticks in 89.304 seconds, had a worst observed frame-delivery gap of 151.72 ms and rejected 20 of 2,699 submitted inputs. These measurements do not prove slow simulation, but need one targeted follow-up:
  - Distinguish authoritative scheduler/tick timing from SignalR delivery intervals.
  - Preserve or expose existing input-admission outcomes/reasons sufficiently to distinguish benign late/duplicate movement input from failed one-shot manual Burst input.
  - Ensure the host has the tick numbering, state and event data needed for bounded browser interpolation and local control prediction in Spec 13.
  - Do not add a large networking subsystem, change deterministic input rules or perform broad WAN/load benchmarks in Kiro.

  ### 8. Maintain production authentication, privacy and version compatibility

  - Use the real authenticated guest principal, `IActorContext` and existing owner-resource authorization. Never accept caller-supplied owner identity as authority.
  - Verify actual production API/Realtime service composition, solo runtime role and database grants. Do not silently fall back to privileged migration credentials.
  - Enforce ownership for original lists, inspect/trace, child catalogs/details, Challenge/Fork preparation, resolution and comparisons.
  - Preserve non-disclosing foreign/unknown ID behavior, correct authentication rejection, stable typed errors and refusal of corrupt, unverified or unsupported-version histories.
  - Maintain the separate frozen `ghost-v1` solo profile and `ghost-mp-v1`/`ghost-mp-v2` multiplayer behavior.
  - No Spec 12 chat, rating, sharing, Redis leaderboard or social dependency.
  - Do not alter Marrow's frozen contracts or Revenant's production code.

  ### 9. Kiro implementation verification — focused only

  Kiro owns production integration and a small set of targeted deterministic checks.

  Verify:

  - Private owner routes actually resolve through production-like DI and owner authorization.
  - A bounded trace returns correct T0/T1140/T2700 history and continuation boundaries.
  - Complete source version/final-hash identity is stable across chunks.
  - The owner can discover a completed Challenge and Fork child without contaminating the Originals list.
  - A Fork prepared at T1140 begins new gameplay at T1141 with correct kind/tick in the wire contract.
  - Existing Challenge T0 behavior remains unchanged.
  - Same-tick comparison uses true historical scores; source and child agree at the fork boundary and may diverge afterward.
  - Living and ghost frames expose real event/candle/score data with correct tick alignment.
  - Invalid/foreign/incompatible requests fail closed.
  - Focused frozen solo/multiplayer golden and architecture checks pass.

  Use existing PostgreSQL Testcontainers and short deterministic fixtures. Run the minimum necessary tests and one affected Release build.

  **Do not spend Kiro credits on a 90-second real-time original, full real-time Challenge, multi-room multiplayer benchmark, repeated long traces, extensive restart matrices or browser acceptance.**

  Stop after implementing the actual host contracts and passing focused checks. Record any delegated checks as pending, never as passed.

  ### 10. Delegate to Codex Medium — final real-host verification

  Codex owns the slower, repetitive and command-line-heavy acceptance after Kiro finishes.

  Required independent gate:

  - Run an authenticated real Release Kestrel/SignalR **Fork at T1140 through T2700** (1,560 living transitions) using an owned saved original.
  - Verify exact same-tick ghost/living frames, event order, input admissions and signed score delta throughout selected sample ticks.
  - Finish the Fork, save its verified child, restart the actual serving host against the same PostgreSQL database, then retrieve it through the owner-facing catalog/resolve API and independently replay its canonical history.
  - Retrieve original inspect/trace chunks and confirm exact source identity, version pinning, chunk continuity and a correct score comparison after restart.
  - Test two real guest cookies: owner access, foreign/unknown denial, revoked-session refusal, corrupted-source and unsupported-version rejection through the public contracts.
  - Run one representative concurrency smoke with a live solo record/seek or fork flow alongside **one** live multiplayer room. Observe 30 Hz progression and gross resource contention; reuse Spec 10's existing four-room certification instead of repeating it.
  - Measure authoritative timer cadence separately from SignalR delivery p50/p95/p99/max. Classify relevant input rejections and confirm manual Burst inputs/effects are not silently lost due to transport or client sequencing.
  - Confirm there are no historical ghost restores in the living tick loop and no new Timeline/Marrow or multiplayer regressions.
  - Reuse existing 11.1–11.4 fixtures and certification. Do not play another 90-second original or Challenge solely to repeat already-proven history correctness.
  - Run focused integration/architecture/regression commands, write one concise final Spec 11 evidence report, and checkpoint only relevant changes on the existing feature branch if acceptance passes.

  **Presentation limitations:** Actual PixiJS interpolation, 60 FPS browser rendering, cinematic five-second rewind effects, audio, touch controls and visual polish remain Spec 13 acceptance items. Codex must report these as untested, not pretend the backend tests certify them.

  ### 11. Completion standard

  **Kiro implementation done when:**
  - Real authenticated hosts expose the complete private Original → Challenge/Fork → saved child → inspect/trace/compare journey.
  - An owned original can be selected and preloaded.
  - A Challenge starts at T0; a Fork starts from full source state T and accepts its first new transition at T+1.
  - Living and historical ghost data are accurate, independent and renderer-ready.
  - Completed Challenge/Fork children are owner-discoverable and durably resolvable.
  - Every score delta is aligned to the same absolute tick.
  - Loading is isolated from live gameplay and no history reconstruction occurs during living ticks.
  - Focused integration, security and architecture checks pass.
  - No unnecessary gameplay, persistence or social system was added.

  **Full Task 11.5 accepted only after Codex verifies** the real authenticated host journey, an actual T1140→T2700 Fork, saved-child cold recovery, privacy/version enforcement, comparison integrity and representative one-room coexistence.

  **Final Spec 11 outcome:** A player can preserve a genuine original, encounter the exact ghost of their past, choose a historical tick, make different decisions and retrieve two truthful, independently verified futures. All necessary game data is available to the future high-quality renderer without requiring a new history engine.

  **Explicit exclusions:** No Spec 13 edits, no PixiJS/audio implementation, no social/rating system, no Redis history cache, no new Timeline port by default, no nested forks, no unfinished-run crash-resume machinery and no multi-day certification campaign.
## Exit certificate — deliberately focused

Record *real evidence*: verified original and two child IDs/hashes, pinned solo `ghost-v1` version, one complete 2700-tick source, T0/1140/2699/2700 state/hash checks, child inputs starting T+1, same-tick score delta, immutable source, real cold reload and foreign-owner denial, actual save status and reasonable observed seek/trace timing. Record test commands, artifacts, untested browser aspects and commit SHA. **Do not run** four-room performance suites, fairness reseeding, comprehensive process-kill matrices or Redis/worker testing for Spec 11. **Do not accept** a forged timeline, missing full-state restore, cross-world ghost effects, false save success or a rewritten historical hash.
