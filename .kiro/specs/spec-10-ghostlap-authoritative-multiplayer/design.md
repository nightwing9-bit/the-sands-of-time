# Spec 10 design — Ghost Lap V2: authoritative 2–4-player multiplayer and rooms

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-03, spec-06. **This spec owns:** `src/GhostLap` room/session loop, `src/Hosts.Realtime`, multiplayer API and contracts, `tests/GhostLap/Multiplayer`. **Do not implement:** No permanent full multiplayer input archive, no ghost as live rival, no multi-replica tick ownership without fencing, no client-written score.

**Consumes:** Public contracts from spec-03, spec-06; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Room authority and lifecycle

`Room`: Created → Lobby → Countdown → Running → Finished → Finalized, or **Aborted/Unrated** after lost authority without committed result. Only host may request start and only when 2–4 distinct ready competitors; during Countdown a dropout cancels back to Lobby, not partial/ghost players. Lobby join is closed to competitors when Countdown begins, late guests may spectate only if room visibility allows. Host leaving Lobby transfers host to earliest joined connected competitor; if empty, room expires. Exact 2P, 3P, 4P spawn socket selection comes from the authored `GameRules`/layout committed in 06; start verifies fair geometry rather than random 3-of-4 corner selection. Set a concrete 3-second countdown initial value in session config and propagate snapshot of server start tick.

`IActiveRoomOwner` lives within **one** Realtime host process; each active room maps to one serialized event loop and one atomic lifecycle version. Before claiming a ranked match has started, commit a **minimal** durable `match_session(matchId,roster,startedUtc,terminalStatus)` receipt (no tick/input archive) sufficient to distinguish an authorized aborted match from a nonexistent ID after a crash. If this small commit fails, refuse start and keep lobby recoverable. The host must refuse startup if configured >1 Realtime replicas until a new owner-fencing design exists. For connected players with no fresh accepted packet, apply Spec-06 `ghost-v1` **effective input**: keep the last legal direction for up to six ticks, then neutral; disconnect makes it neutral immediately. `requestBurst` is edge-triggered and cannot repeat on held/lost input; stale/late sequences cannot reset the timeout. The state/snapshot and relevant adapter retain accepted sequence, latch state and scoring remainder; do not create a separate multiplayer reducer. All inputs arrive in per-slot bounded queue `(targetTick,sequence)`; reject duplicates, invalid joystick vectors, Burst too frequent, >configured future window and stale beyond last authoritative accepted window. Tick step is fixed by server monotonic scheduler; game state updates run sequentially through exact spec-06 pure reducer; slow tick cannot produce extra score and publishes health drift metrics. Snapshot is a read-only projection and contains current tick/room/version/server sequence; nonauthoritative client interpolation never affects score. Redis SignalR transport/backplane is optional notification layer only.

**Disconnect policy v1:** client disconnect results neutral input (0 vector, no Burst) until reconnection. Grace 450 ticks (~15 seconds at 30Hz). After grace, mark forfeited: no later score accrual; placed below non-forfeiting finishers, tied forfeits ordered by their last authoritative score then stable slot ID. If <2 connected players remain, let round finish at time but no further score from forfeited clients; schedule result finalization exactly once. Rejoined within grace receives full state snapshot with outstanding sequence and resumes; after forfeit reconnect is spectator-only for same match. Replay of old commands cannot reinstate forfeit. A match may still be labelled ranked if 2+ distinct eligible guests started; abuse eligibility and sanctions are part of spec-12's rating policy.

### Final result contract

`MatchResult(matchId,roomId,seed,rulesVersion,layoutVersion,startUtc,endUtc,roster[{playerId,slot,score,distancePoints,pickupPoints,penalties,charge/bursts/hits,forfeited,placement}], finalizedVersion)` plus outbox event `MatchFinalizedV1`. Postgres unique matchId and idempotent worker `finalizeMatch(matchId,expectedVersion)` ensure one durable result. **Failure v1:** while DB is unavailable, room returns `PendingFinalization` with bounded retry, never `Finalized`. After process loss/restart, if an authorized result lookup has no committed match result and no active room capable of retrying, return `Aborted/Unrated` for that match; do **not** emit `MatchFinalizedV1` or rating effect. If result committed before response loss, re-query it by `matchId` and show it once. Use the already-committed minimal start receipt to distinguish authorized `Aborted/Unrated` from an unknown ID; **no full tick history or recovery coordinator**. When PostgreSQL is restored, opportunistically mark the durable start receipt `Aborted` if no valid result was committed; until then serve a truthful provisional unrated outcome. Clients see a clear error/rematch, not an ambiguous permanent spinner. Tie: equal total score gives shared placement unless explicit forfeit ordering applies; displaying arbitrary winner forbidden. Results from 2–4 real separate browser clients; no permanent archived network input stream. Use Testcontainers for room result and retry, network integration harness with 2–4 sockets for actual commands and spectator denial.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Fake position/pickup/score/hit payload never alter authoritative game state
- Tick input stable ordering across clients and same-tick collision/candle tie-break
- No room ever has 2 concurrent active loops
- Reconnect within 450 ticks; forfeit after limit without continued score
- Spectator requests rejected even with forged membership payload
- Result and outbox exactly one business effect despite repeated finish; DB outage/process loss before commit leads to pending then Aborted/Unrated, committed result survives lost reply
- 2/3/4 remote-client tests with one shared arena and consistent total-score winner

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for room ownership/tick semantics; economical for lobby endpoint plumbing.
