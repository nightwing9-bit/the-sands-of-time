# Spec 10 implementation tasks — Authoritative multiplayer and fast, honest matches

**All unchecked; none claimed implemented.** Execute in order, respecting dependencies Spec 03/06. Each task is a real implementation/test package, not an AI prompt. Inspect actual frozen upstream contracts and named files before editing; adapt private Ghost Lap code without rewiring Marrow or certified Revenant. Log actual commands, evidence, commits, environment and blockers in `docs/kiro-log.md`; **no observed evidence = not done**.

- [ ] **10.0 Contract and game-rules preflight (BLOCKER for 10.2)**
  - **Inspect:** `docs/architecture/{01-master,02-spec-map,03-contracts}.md`, Spec 03 identity/hub contracts, Spec 06 canonical `ghost-v1` GameState/codec/reducer, input-latch/edge rules, phase ordering, wall/pickup/player collision, score distance calculation, rules/layout hashes, actual goldens/migrations and Realtime deployment.
  - **Produce:** compatibility matrix showing what exists, what must be added only within Ghost Lap; a sealed MP rules profile defining any originally-open wall/Burst/candle/score/terminal phase decisions and initial opaque `ratingAlgorithmId` handoff to Spec 12; max-speed safety hypothesis and input-window profile. Identify the exact owner of login/ingress and the existing runtime grant path. Where upstream evidence is absent or contradictory, mark BLOCKED rather than invent an API.
  - **Verify:** run existing golden `ghost-v1` tests/hash assertions unchanged; inspect/measure collision at max considered speed and retained input under loss. Confirm no shared ABI, Timeline, Revenant or historical byte change.
  - **Done when:** factual evidence supports a versioned compatible MP extension; every gameplay-significant unresolved rule has a documented decision before MP runs.

- [ ] **10.1 Room/lobby, controller and start admission**
  - **Produce:** genuine guest-bound invitation/roster/ready/host lifecycle, separate bounded spectators, one controlling-connection epoch/competitor, safe host transfer, countdown cancel/duplicate start idempotency; typed API/hub commands that route to the only Realtime owner. Sealed seed/rules/layout/spawn/`ratingAlgorithmId` roster config; fair 2/3/4 sockets and startup validation.
  - **Verify:** transition matrix including concurrent joins/leave/host changes, duplicate countdown requests, 2 tabs, foreign identity, expired room codes, snapshot permissions and no competitor admission after countdown.
  - **Done when:** illegal roster cannot start; old controller cannot submit gameplay after takeover; the lobby remains recoverable after cancelled countdown.

- [ ] **10.2 Single-authority tick loop, inputs and physics safety**
  - **Produce:** durable receipt-before-Running, dedicated session-held advisory guard, monitored one-Realtime process generation, startup predecessor retirement, one serial mailbox per room, 30 Hz/2700-tick monotonic scheduler with **bounded** catch-up and overload fail-safe, per-slot authenticated admitted-input/one-shot Burst, canonical MP reducer extension, deterministic phases, applied-input acknowledgements. Queue/DB/transport are off the simulation critical path. Preserve six-tick connected hold and immediate disconnect neutral. Record focused authority/terminal ADR and gameplay version identity.
  - **Forbidden:** two tick authorities, game logic in Hub methods, magic clock-based score, changes to `ghost-v1` codec/hashes, MP full input archive or synchronous SQL/network work inside reducer.
  - **Verify:** 2/3/4 sockets affect same world; same applied inputs reproduce same hashes; target-tick edge/candle/contact/Burst ordering; extreme speed, diagonals, wall-slide and narrow pickup; scoring excludes corrections; scheduler 700-ms-stall and persistent overrun behavior; dedicated guard connection loss and old process cancellation. Test target host registrations and least-privilege grants, preserving Revenant.
  - **Done when:** one real authority produces correct fast gameplay without tunnel/phantom score, and delayed ticks have defined safe outcomes.

- [ ] **10.3 Snapshots, network quality, reconnect and forfeit**
  - **Produce:** immutable authoritative state snapshots at initially every two ticks (configurable), event IDs, applied-sequence ack, monotonic generation/sequence filters, resync and bounded recent event tail; asynchronous bounded fan-out/drop-stale-snapshot policy; controller epoch handoff and 450-tick grace/forfeit semantics. No effect/score for forfeited slot as actor/target.
  - **Verify:** 50/150/300ms RTT + jitter/loss, stale/duplicate/reordered input, refresh/controller takeover, old connection spoof, key-release during six-tick hold, tab suspension; overlit auto-Burst before/after forfeit; boundary 450/451; slow spectators and saturated outbound buffers do not stall authoritative tick. Spectator gameplay calls denied in both layers.
  - **Done when:** lost packets do not create repeated Burst, client can recover from a snapshot, and disconnected/forfeited behavior stays deterministic.

- [ ] **10.4 Transactional terminal results and rematch**
  - **Produce:** `match_session` receipt/generation and `MatchResult`/`MatchFinalizedV1` outbox uniqueness; terminal/replacement serialized under same locked receipt row; honest pending/provisional outcome and recovery; exact per-player stats, finishers-before-forfeits, genuine tied placements, immutable rating eligibility and carried-through start-pinned `ratingAlgorithmId`; rematch fresh identity/seed/state.
  - **Verify:** guard-loss race with old process's ordinary DB pool alive, both commit orderings; before/after SQL result commit and lost ACK, DB down at start/finish, old receipt aborted while new epoch starts, duplicate/concurrent finalize and payload conflict, unrated all-forfeit and rated one-finisher cases, tie/forfeit placement, frozen algorithm identity across delayed finalization and fresh rematch. `Aborted` never emits rankable result; committed winner survives lost acknowledgement.
  - **Done when:** exactly one truthful durable terminal decision and outbox per finalized match, no double result, no fake winner after crash.

- [ ] **10.5 Competitive fairness and multicurrent-room load proof**
  - **Produce:** deterministic seeded fairness/collision property fixtures, measured target-host multi-room scheduling/fan-out test, explicit admission limits and performance configuration, representative score-incentive playtests. Record adjusted *new MP versioned* tuning only if proven necessary.
  - **Verify:** at least four 4P rooms + two spectators each (or an explicit blocking target-host capacity result); p50/p95/p99 tick/start-lateness/GC/queue/drop/byte/snapshot size, overload recovery and no unbounded backlog; adversarial high-speed wall/player/pickup crossing, 2/3/4 reachable layouts, corner-farming vs contesting. Preserve old solo deterministic fixtures.
  - **Done when:** game meets documented measured performance targets and competitive incentives are defensible, not inferred from a single empty room.

- [ ] **10.6 Full real-client and cross-domain exit gate**
  - **Produce:** run reproducible 2/3/4-client full 90-s sessions, spectator denial, reconnect, actual results/rematch; operational instructions for single-replica guard and observed overload behavior. Confirm typed clients correspond to implemented contracts.
  - **Verify:** exact command/evidence logs, production DI/API/Realtime roles and Revenant coexistence, unknown resource/actor denial, no test-only bridges/routes in production, final persisted result and outbox. Any missing real-environment check is a blocker, not an inferred pass.
  - **Done when:** R10.01–R10.20 are evidenced and no architectural compatibility blocker remains.

## Blocking exit certificate (write evidence; don't pre-fill)

Record actual tests and hashes; final implementation commit; matched immutable rules/layout IDs and 06 goldens; socket recordings/results; fault-injection SQL assertions; multi-room tick/latency profile and host hardware; privacy/policy observations; gameplay balance observations; known limitations. Missing proof keeps Spec 10 incomplete. Do not start Spec 12's production rating application until Spec 10's terminal result/placement contract is verified.
