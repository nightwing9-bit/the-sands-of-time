# ADR-P1-22 — Ghost Lap multiplayer rules profile `ghost-mp-v1`
- **Status:** Accepted at Spec 10 task 10.0 (preflight). Not implemented. Items marked *measure* are pinned now and may change only through a new rules version after the named measurement.
- **Scope:** gameplay rules of live 2–4-player Ghost Lap matches. Not in scope: room authority, generation fencing and terminal commit (the Spec 10 task 10.2 ADR), rating formulas (Spec 12), and anything about solo histories.
- **Evidence:** `docs/architecture/08-ghostlap-multiplayer-preflight.md`, `tests/GhostLap/Multiplayer/PreflightSpeedSafetyTests.cs`.

## Context
Spec 06 defined one deterministic reducer, `ghost-v1` / `arena-v1`, that already accepts 1 to 4 players and pins phase order and same-tick tie-breaks. It has no notion of an inactive or forfeited participant: a neutralized slot still collects, contacts and auto-Bursts. `GameContextCodec` accepts only `ghost-v1`, so the multiplayer extension cannot reuse that identity without changing historical bytes.

## Decision
1. Multiplayer uses rules id **`ghost-mp-v1`** on layout **`arena-v1`**, with every `GameRulesV1` constant unchanged. Its hashed state schema is `ghost-mp-state/1`. `ghost-v1` codecs, version key, fixtures and hashes are not modified. MP state is not archived in the Marrow Timeline.
2. **One simulation.** The ghost-v1 phase code becomes the shared implementation with a participation mask. `ghost-v1` runs with every slot active and must keep its golden pins byte-identical. `ghost-mp-v1` adds phase 0 (apply forfeits scheduled for this tick) before phases 1 to 7.
3. **Inherited rules:** no Burst wall occlusion (ADR-P1-09); players pass through each other and contact is an end-of-tick overlap; pickup and contact use end-of-tick overlap; distance is post-collision displacement only; no attacker reward; penalties clamp at zero.
4. **Same-tick priority** keeps ghost-v1 slot order (contested candle to the lowest slot; Bursts resolved in slot order, so a lower slot's Burst can cancel a higher slot's Overlit). To keep this from being a chosen advantage, slots and their sockets are assigned to players by a permutation derived from the sealed seed at countdown. *Measure* contested-tie frequency and its score effect in task 10.5.
5. **Disconnect and forfeit.** On a verified disconnect the room applies the existing server neutral input on the next tick D. Ticks D..D+449 are grace (450 ticks, ADR-P1-11) and the slot is a normal target and collector with neutral input. A controller takeover processed before tick D+450 is resolved keeps the slot. Otherwise the slot is forfeited at tick F = D+450, applied in phase 0. A forfeit that would fall after tick 2700 does not happen.
6. **Forfeited slot** from tick F: admits no input, does not move, cannot collect, is skipped by every contact pair (no drain, no cooldown change), cannot fire a manual or automatic Burst and cannot be hit. Its Overlit deadline is cleared without a Burst and its charge is kept only as a frozen stat (the MP codec invariant is "deadline nonzero exactly when charge is 4 and the slot is not forfeited"). Score and stats are frozen. A reconnect after F is spectator-only for that match.
7. **Burst latch across reconnect:** `Neutralize` releases the latch, so a key held through a reconnect gives one rising edge on the first accepted input. Accepted.
8. **Terminal:** tick 2700 resolves every phase, including a last-tick auto-Burst, then finishes. Slots not forfeited by tick 2700 are finishers, including slots still in grace.
9. **Placement:** finishers rank above forfeits; within a class a higher total ranks first; equal totals in a class share a placement; slot order is display-only. Eligibility policy **`mp-eligibility-v1`**: rated only if at least one slot finished and at least two distinct authenticated guest principals started; a service-side abort is unrated and is never a forfeit.
10. **Rating handoff:** the countdown seals the opaque **`ratingAlgorithmId = "elo-pair-v1"`**. It is copied unchanged to the start receipt, the `MatchResult` and `MatchFinalizedV1`. Ghost Lap never interprets it; Spec 12 owns its meaning.
11. **Score coefficients unchanged.** Movement points are capped at 1687 per round at full speed. *Measure* empty-corner running against contested collecting in 2/3/4-player runs in task 10.5.
12. **Network profile** `ghost-mp-net-v0` is a hypothesis only (preflight §5). It is frozen with measured values in task 10.3 and sealed into the countdown config from then on.

## Consequences
- The ghost-v1 reducer is refactored without behaviour change; the existing golden tests are the acceptance check for that refactor.
- `MatchResult` needs placement class, forfeit tick and eligibility fields that `GameResult` lacks.
- Any later change to items 3, 4, 6 or 11 is `ghost-mp-v2`, never an edit of `ghost-mp-v1` once a match has been finalized under it.

## Rejected alternatives
- Implementing forfeit with neutral input only: the measured ghost-v1 behaviour keeps such a slot collecting and auto-Bursting.
- Rotating or simultaneous same-tick priority now: a rule change without the measurement R10.19 requires.
- Swept pickup or contact sampling now: the measured gap is a corner graze below one diagonal axis step, and a sampling change would alter rules without playtest evidence.
