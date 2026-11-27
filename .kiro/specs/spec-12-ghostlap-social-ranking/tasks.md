# Spec 12 tasks — Minimal results and spectator integration handoff

**All unchecked in this replacement.** Spec 10 multiplayer gameplay, result/outbox, spectator permissions and rematch are already implemented; do not rebuild them. Spec 11 is independent and **does not wait for Spec 12**. This directory name is legacy; room chat, history sharing, Elo, Redis ranks and social features have been explicitly deferred.

**Cost constraint:** maximum value from **no new production changes** when existing Spec 10 contracts already satisfy the browser. Begin with a brief source inventory and only the mapping changes actually needed; focused smoke checks verify the relevant contracts. Reuse recorded Spec 10 evidence instead of lengthy multiplayer suites. No new database/Redis/Worker work unless a real required result-display field is provably inaccessible.

- [x] **12.0 Inspect existing multiplayer result, spectator and rematch contracts (short, code-first gate)**

  - **Inspect actual code:** Review the existing Spec 10 `MatchResult`, `MatchFinalizedV1` producer/outbox, result retrieval authorization, lifecycle/status types, standings/score fields, guest/player identities, 2/3/4-player match support, four-player spectator hub, authoritative frames/events, resync and rematch responses. Verify how new matches seal `ghost-mp-v2` with `arena-v1`. Read relevant Spec 10 ADRs and completed backend acceptance evidence. Use real implementation names; do not guess interfaces or DTO fields.

  - **Map the minimum browser handoff:** Produce a compact source-grounded mapping for:
    1. **Match results:** final scores, placements, winners, ties, forfeits, available scoring breakdowns, actual pending/finalized/aborted or equivalent status semantics, and correct guest-visible identity.
    2. **Spectators:** read-only entry/authorization, live authoritative snapshots, relevant game-event cues, cursor/resync behavior and terminal result.
    3. **Rematch:** real new match identity, seed/profile, players, readiness and response contract without incorrectly reusing the old match.
    4. **Visual fidelity:** authoritative tick/event identifiers needed for the future scoreboard, winner presentation, candle/Burst effects and spectators.

  - **Identify only genuine gaps:** Compare existing typed contracts against these minimum screen needs. Distinguish a truly missing backend field or owner-access path from a field already available but not yet consumed by the browser. Record the exact existing producer/consumer and the smallest potential 12.1 change, if any. Do not invent additional scoring data or duplicate existing match results.

  - **Preserve completed architecture:** `MatchFinalizedV1`, `ratedEligible` and `ratingAlgorithmId` remain existing durable metadata; they do not mean Elo ratings have been calculated or published. Preserve the outbox contract without implementing a rating consumer. No chat, network emotes, social sharing, Redis leaderboard, Elo ledger, new results schema, gameplay physics changes or multiplayer redesign.

  - **Verify from existing evidence only:** Reference the previously completed Spec 10 two-, three- and four-player rounds, four-player rematch, spectator denial/resync, finalized-result uniqueness and authorization tests. Confirm coverage from committed test source/reports. **Do not rerun them.** No PostgreSQL containers, long game sessions, migrations, builds, benchmarks or broad test commands in Task 12.0.

  - **Kiro deliverable:** A concise mapping in the existing `kiro-log.md` Task 12.0 entry, identifying actual source files/types, reusable contracts and a short list of concrete missing items, if any. State clearly whether 12.1 requires production code or can be a no-op. No new lengthy design report or ADR unless a genuine frozen-contract incompatibility is discovered.

  - **Done when:** The future Spec 13 multiplayer results, spectator and rematch screens have an evidenced, minimal integration path, with no invented contracts or unnecessary work.

  **Stop after inspection. Do not implement 12.1. Do not run tests. Reserve subsequent integration/regression verification for the focused acceptance stage.**

- [x] **12.1 Confirm complete multiplayer presentation contracts — NO PRODUCTION CHANGES**

  - **Decision:** Task 12.0 established that the existing Spec 10 contracts already provide all essential multiplayer results, standings, spectator frames, rematch identities and gameplay event data. Therefore **Task 12.1 requires zero production implementation**. Do not create new DTOs, API routes, database queries, migrations or persistence services.

  - **Reuse existing results:** `GhostLapHub.GetMatchResult(matchId)` returns `MatchOutcomeView`, including the actual `finalized` / `aborted` / `pending` decision. `MatchPlayerResultView` already supplies authoritative placements, ties, forfeits, total score, movement/pickup/penalty breakdowns, gameplay counters and room-scoped identity. Preserve existing access control and immutable `MatchFinalizedV1` results.

  - **Reuse existing spectators and rematches:** `JoinRoom(AsSpectator)` provides read-only `SnapshotFrame` updates, `Resync` and room-authorized result retrieval. Spectators cannot control gameplay. `Rematch` returns the room to the lobby; the next match start creates a fresh match ID, seed and sealed configuration using `ghost-mp-v2` / `arena-v1`.

  - **Record the Spec 13 handoff:** The browser must call `GetMatchResult` when the match reaches a settled state, distinguish pending from finalized/aborted outcomes, and retrieve a spectator's result before leaving the room. Use existing `MatchEvent.Key`, `Snapshot.Tick`, `Sequence`, `IsFinal` and event/candle identities for correct visual presentation and resync. Do not expose roster-private results publicly.

  - **Preserve scope:** `ratedEligible` and `ratingAlgorithmId` remain metadata, not evidence of calculated Elo. Do not implement chat, social emotes, rating consumers, Redis leaderboards, public result sharing, duplicate standings calculations or another match-result store. Do not change multiplayer mechanics, room admission, authoritative results or frozen profiles.

  - **Kiro verification:** Inspect the existing Task 12.0 mapping and reference the already committed Spec 10 results, spectator, forfeit, tie and rematch tests. **Do not execute those tests again.** No PostgreSQL Testcontainers, builds, integration runs, load tests or migration commands.

  - **Deliverable:** Mark Task 12.1 complete as an intentional **NO-OP**, with a brief decision and exact reusable contracts in `kiro-log.md`. No new ADR or separate architecture report.

  - **Verification boundary:** Any final focused regression or real-host contract smoke belongs to Task 12.2. Do not execute it here.

  - **Done when:** The existing authoritative multiplayer contracts are explicitly accepted as sufficient for Spec 13, with zero unnecessary production changes and no duplicated backend tests.

  **Stop after documenting the NO-OP decision. Do not start Task 12.2.**

- [x] **12.2 Final multiplayer results/spectator handoff and minimal acceptance — close Spec 12**

  **Objective:** Confirm that the existing, certified Spec 10 multiplayer contracts are sufficient for the beautiful Spec 13 results, spectator and rematch presentation. Produce a concise, source-grounded browser handoff without implementing additional multiplayer infrastructure.

  **Scope:** Documentation and at most one short delegated real-host smoke. No new gameplay, ranking, social or persistence implementation.

  ### 1. Finalize the actual result contract

  - Reuse `GhostLapHub.GetMatchResult(matchId)` and the existing `MatchOutcomeView` / `MatchPlayerResultView` types.
  - Document the real public result decisions: `finalized`, `aborted` and `pending`. Do not invent a separate public `unverified` status unless the actual contract exposes it.
  - Document exact competitive placements, shared ranks for ties, forfeits below finishers, authoritative final scores, movement/pickup/penalty breakdowns, pickup/Burst/contact counters and forfeit ticks.
  - Results identify members through room-scoped `MemberId` and `Slot`. Do not introduce public profiles or expose unnecessary guest identity.
  - Results remain derived from the existing authoritative, immutable Spec 10 source. No duplicated score or standings calculation.
  - `MatchFinalizedV1`, `ratedEligible` and `ratingAlgorithmId` remain backend metadata. Do not imply Elo was calculated or applied.

  ### 2. Finalize spectator and rematch contracts

  - Document the actual `GhostLapHub.JoinRoom(JoinRoomRequest)` call with `AsSpectator = true`; it is not a separate `JoinRoom(AsSpectator)` method.
  - Spectators receive authoritative `SnapshotFrame` updates with `CanControl = false`.
  - Preserve spectator `Resync`, terminal match-result retrieval and refusal of gameplay actions with `spectator_forbidden`.
  - Record the existing result-access limitation: spectators must retrieve the settled result while they retain authorized room membership; leaving the room does not grant permanent result access.
  - Document `Rematch` accurately: it returns the existing room to lobby state, while the **next successful match start** creates a new `MatchId`, seed and sealed configuration digest.
  - Preserve `ghost-mp-v2` / `arena-v1` for newly started multiplayer matches, without changing frozen `ghost-mp-v1` or solo `ghost-v1`.
  - The browser must not carry old match snapshots, scores, event cursors or one-shot effects into a new rematch.

  ### 3. Preserve the essential Spec 13 gameplay-feel handoff

  Document existing production contracts and client responsibilities without implementing browser features here.

  - Heartbeat/client liveness: preserve the documented approximately five-second ping and 15-second timeout expectations, subject to the actual host configuration.
  - Use authoritative frame sequence, tick, `MatchEvent.Key`, candle identity and resync cursor/generation for deterministic visual presentation.
  - Reconcile missing/coalesced frames and ensure candle pickups, Soulfire changes, manual Burst, automatic Burst, contacts and terminal cues are displayed once, in correct event order.
  - Respect authoritative server state while using future client-side interpolation and bounded prediction for responsive movement.
  - Preserve actual one-shot manual Burst press semantics; do not silently discard legitimate presses as ordinary movement replacement.
  - Use existing snapshot/resync and input-admission contracts instead of building a new networking subsystem.
  - Once match status settles, the browser must call `GetMatchResult`. Display `pending` honestly and perform bounded retry where appropriate; do not assume a finalized result is automatically pushed.
  - Clearly separate **already implemented backend capabilities** from **unimplemented browser behavior**. PixiJS, animation, audio, browser input responsiveness, physical-device controls and WSS deployment acceptance remain Spec 13 work.

  ### 4. Kiro — documentation only, no test execution

  - Read the Task 12.0 inspection and Task 12.1 NO-OP decisions in `kiro-log.md`, plus the relevant committed Spec 10 acceptance/handoff evidence.
  - Produce one short final handoff section in `kiro-log.md` or the existing relevant architecture handoff document. Prefer extending an existing document to creating a new lengthy report.
  - Identify the actual hub methods, typed DTOs, result lifecycle, spectator permissions, rematch identity and event/resync fields.
  - State explicitly that no missing presentation-contract field or backend access path has been demonstrated.
  - Reference the previously completed real 2/3/4-player games, four-player rematch, spectator/resync and finalized-result tests **without rerunning them**.
  - Do not run builds, PostgreSQL Testcontainers, migrations, integration tests, long games, benchmarks or command-line regression suites.
  - Do not modify production code or Spec 13.
  - Record any remaining focused smoke as **pending**, not passed.

  **Kiro stops after the handoff.**

  ### 5. Minimal independent acceptance

  Verify the final technical behavior against existing source, contracts and recorded test evidence.

  - Read the actual current hub/DTO implementations and the final Kiro handoff.
  - Reuse existing Spec 10 fixtures and tests. Verify from source that `MatchOutcomeView` accurately exposes finalized/tied/forfeited scores and placements, and that pending/aborted outcomes are not misrepresented as finalized.
  - Perform at most **one focused, short real-host contract smoke** if the existing certificates do not already cover the necessary exposed API/hub behavior.
  - Where that smoke is needed, check authorized result retrieval, spectator join/read-only denial, `Resync`, and a rematch's fresh identity after the next match starts. Use existing deterministic fixtures or bounded harness execution; a complete 90-second match is unnecessary.
  - Confirm foreign/unauthorized match-result retrieval remains non-disclosing.
  - Check that a rematch does not reuse the old authoritative result, game events, generation or frame identity.
  - Verify the existing `MatchFinalizedV1` producer and immutable result source remain unchanged.
  - Use the minimum focused tests needed for any previously uncovered contract. Do not repeat already certified scenarios simply to increase test counts.
  - If no genuine defect is reproduced, make **zero production changes**.
  - If a real presentation-contract defect is reproduced, fix only the narrow defect, add a targeted regression and preserve all frozen physics and result semantics.
  - Produce a concise exit note and checkpoint the relevant Spec 12 decisions/evidence on the existing Ghost Lap feature branch if safe. Preserve unrelated working-tree edits.
  - Report actual CI status; zero GitHub Actions runs does not mean green CI.

  ### 6. Strict exclusions

  Do not implement:

  - Room chat or social messages.
  - Network/social emotes; existing gameplay-driven facial expressions must remain available to Spec 13.
  - Elo/MMR calculations or rating consumers.
  - Redis leaderboards or ranking projections.
  - Public sharing links or public match-history access.
  - New result/score database tables.
  - Another multiplayer authority or simulation engine.
  - New matchmaking, lobby or room infrastructure.
  - Repeated four-room load, fairness, crash-recovery or full-duration certification matrices.
  - Browser rendering, local prediction, sprite animation or audio.

  Preserve all real 2/3/4-player gameplay, four-player spectators, rematches, movement, candles, Soulfire, Bursts, contacts, results and forfeits.

  ### 7. Completion criteria

  **Kiro implementation handoff complete when:**

  - The exact existing results, spectator and rematch contracts are documented for Spec 13.
  - Pending/finalized/aborted statuses are accurate.
  - Results expose authentic match-local placements and scoring.
  - Spectator viewing, control denial and result-read permissions are understood.
  - Rematch identity and event/reset semantics are clearly defined.
  - No unnecessary backend implementation is introduced.
  - Any independent technical verification still pending is recorded honestly.

  **Spec 12 fully accepted when independent verification confirms:**

  - The existing contracts are usable without backend redesign.
  - Authoritative results and spectator authorization remain correct.
  - New rematches cannot mix with old match results or frames.
  - No actual result-contract regression or required missing field remains.
  - The final Spec 13 handoff contains the necessary client timing, input, event and resync responsibilities.
  - No social, rating or leaderboard infrastructure was introduced.

  **Final objective:** Close Spec 12 with the smallest amount of new work, preserving the completed multiplayer backend and giving Spec 13 everything it needs to create a polished four-player game.

  **Do not allow Spec 12 to become another lengthy backend development phase.**
## Exit record (short, honest)

State whether production code changed at all. Link/quote real Spec 10 tests and any focused 12.x test, identify which current endpoint provides finalized results, what statuses to render, how to distinguish rematches and how spectators receive terminal state. Keep browser/Pixi rendering, actual keyboard feel, effects/audio, public TLS/WSS and any future global leaderboard explicitly pending separate work. Record commit only when a real diff exists. **No chat/sharing/Elo/Redis deliverables and no hidden dependency from Spec 11 or the final visual work on deferred social features.**
