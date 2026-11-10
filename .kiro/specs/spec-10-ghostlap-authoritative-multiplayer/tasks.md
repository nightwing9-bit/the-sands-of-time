# Spec 10 implementation tasks — Ghost Lap V2: authoritative 2–4-player multiplayer and rooms

**Execute tasks in listed order.** Dependencies: spec-03, spec-06. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **10.1 Implement lobby/roster lifecycle**
  - **Precondition/input & inspect:** GhostLap guest/session contracts and realtime host. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** create/join/link, 2–4 slots, ready/host/transition logic, spectator role.
  - **Forbidden changes:** client-side slot authority or game lobby in Redis alone. Do not change frozen shared public contracts without ADR.
  - **Verification:** unit transition matrix and API membership tests.
  - **Done when:** room cannot start invalid roster.

- [ ] **10.2 Implement authoritative per-room tick loop**
  - **Precondition/input & inspect:** spec-06 reducer/rules, Realtime host, input validation. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** minimal durable ranked match-start identity receipt before declaring Running; single process loop, tick scheduling, accepted input queues, server snapshots; reuse Spec-06 six-tick hold/neutral effective-input and one-shot Burst semantics.
  - **Forbidden changes:** game logic in Hub methods or Pixi, two owners. Do not change frozen shared public contracts without ADR.
  - **Verification:** same-seed 2/3/4 socket integration plus spoofed, missing, late and duplicate Burst input tests; no extra score after checkpoint.
  - **Done when:** real remote players influence one world.

- [ ] **10.3 Reconnect/forfeit and snapshot recovery**
  - **Precondition/input & inspect:** Identity session, hub sequence, room lifecycle. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** 450-tick grace, neutral input, rejoin, forfeit and spectator conversion.
  - **Forbidden changes:** unbounded disconnected active player scoring. Do not change frozen shared public contracts without ADR.
  - **Verification:** jitter/reconnect stale input, forced neutral on disconnect, accepted-sequence resume and duplicate join tests.
  - **Done when:** state stable through refresh/outage.

- [ ] **10.4 Finalize and rematch**
  - **Precondition/input & inspect:** `ghostlap.match_result`, outbox, room session version. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** idempotent result, full breakdown, bounded PendingFinalization then Aborted/Unrated on missing durable result after crash, next-match reset; never emit rating event for aborted match.
  - **Forbidden changes:** rating effect in gameplay loop or MP full histories. Do not change frozen shared public contracts without ADR.
  - **Verification:** DB outage before start-receipt commit (must not start), DB outage just before result commit, process crash before/after result commit, pending/aborted versus valid existing-result lookup, no phantom rating, rematch state reset.
  - **Done when:** one result/outbox event per completed match.

- [ ] **10.5 Real multi-client gate**
  - **Precondition/input & inspect:** Playwright websocket harness and authoritative trace. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** 2,3,4 actual remote users, spectator snapshots/denials.
  - **Forbidden changes:** simulated fake extra player or score from pixels. Do not change frozen shared public contracts without ADR.
  - **Verification:** real clients move, collect, Burst, compare server-state.
  - **Done when:** multiplayer is demonstrably genuine.

## Spec exit verification (blocking)

Actual 2–4 browser/socket clients play 90s authoritative session through result/rematch; spectator denied control; reconnect and single-match-result tests pass.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
