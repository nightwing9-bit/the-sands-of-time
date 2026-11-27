# Spec 12 design — Reuse real Spec 10 results; do not build a social backend

**Status:** planned narrow handoff, not a new engine. The historical directory keeps “social-ranking” in its name for replacement compatibility; its original feature set is intentionally deferred. Spec 11's verified private solo histories do not depend on this module.

## 0. Decision and source of truth

The multiplayer backend is already certified for the hackathon scope:

- `ghost-mp-v2` on existing `arena-v1` powers new matches; `ghost-mp-v1` and solo `ghost-v1` histories and goldens remain pinned.
- 2/3/4 real-client full 2700-tick rounds have evidence; four-player match and rematch have real persisted results.
- Spec 10 has canonical match receipt, immutable `MatchResult`, one unique `MatchFinalizedV1` outbox event and honest `Pending/Unverified/Aborted` handling.
- Live spectators have a bounded, denied-to-control snapshot/event path and resync.

**Nothing in this spec should recompute authoritative rank, implement another rating system, duplicate result persistence or update gameplay rules.** A “rank” on the results screen is simply the existing match-local competitive placement; it does not indicate a global Elo rating. Retain `ratingAlgorithmId` and `ratedEligible` in the stored result for compatibility/provenance without consuming the outbox or telling the player a rating was awarded.

Existing `MatchFinalizedV1` delivery to Redis/Worker has not been established; the registered relay is not proof of delivery. This phase neither configures nor requires a rating consumer. Do not start a queue to consume events and discard them as though applied. An existing outbox event may simply remain durable for future work.

## 1. Product-facing flow

```text
Authorized guest opens room/result
         |
         +-- live room -> Spec10 snapshots/events/ack/resync
         |     - four simultaneous players, full rules and actual event IDs
         |     - spectator read-only current state, bounded fan-out
         |
         +-- terminal -> actual receipt/result lifecycle
         |     - Finalized: placement + score components + ties/forfeits
         |     - Pending/Unverified/Aborted: truthful distinct UI states
         |
         +-- authorized rematch -> existing fresh Spec10 room/match
               - tick0/seed/new ID; no prior-match state or duplicated result
```

This is a **read-contract integration**, not a new application service. Before coding, inventory actual existing API/hub endpoints and typed DTOs. If they suffice for the future frontend, record their names and field mapping and do not touch production source.

## 2. Minimal presentation contract

Read directly from actual Spec 10 response types; avoid inventing implementation method names. The eventual UI needs, at most:

| Existing source fact | Needed presentation meaning |
|---|---|
| `matchId`, sealed rules/layout version, lifecycle/generation | prevent old-match frames/results being treated as the rematch |
| authenticated member slot/character ID and competitor/spectator role | four-player scene identity and read-only spectator restrictions |
| final authoritative player score and score components | valid results presentation and breakdown (not invented UI score) |
| competitive placement, tie and forfeit state | winner/placement order independent of display-slot ordering |
| finalization status and result digest | real Finalized vs Pending/Unverified/Aborted outcome |
| snapshots/events with event IDs, ticks and recipient sequence | gameplay effects, 0–4 charge, Overlit/Burst, contacts and candle pickups |
| rematch command and resulting fresh match identity | new session without old state leakage |

If necessary, add **one thin owner/roster-authorized read projection** that maps existing persisted result fields; it must not copy/rerank results or persist a second projection. If the required fields are already exposed, the appropriate change is **no code**.

A future frontend may show winner badges, full podium, scores, candles, movement and Burst information using these contracts. Do not specify layout, typography, animations, effects, assets or game audio here: the user is preparing those separately for the visual spec.

### Result truth and access

- A finalized unrated match is a real completed game with valid standings. Do not show “Rating +X” just because `ratedEligible=true`; that bit records future rating eligibility only.
- If transaction/authority failed, UI should show the existing honest status. An aborted match has no rankable winner or `MatchFinalizedV1` result.
- Spectator authority is determined at the service/hub layer. An observer may watch the actual live room and its permitted terminal state, but that does not imply a permanent public result URL or a right to view every past match by guessed ID.
- Keep the committed result's stable ID/digest and typed versions. Rematches are **separate matches**, not a new application of an old result.
- Existing shared guest principal and resource-kind authorizers are the only allowed security truth. Do not create public history routes, guest social profiles, share tokens or new identity infrastructure.

## 3. Execution boundaries and cost control

A single short port-inspection pass establishes where the result and spectator contracts already exist, followed by **only a small gap patch if necessary**. Then a focused smoke check reuses the already-passed Spec 10 evidence for:

- authentic four-player final result, tie and forfeit ordering;
- roster-visible result status and foreign-user denial;
- spectator cannot submit gameplay or control a player;
- fresh rematch and no mixed-match snapshots/events;
- preserved outbox/result immutability and no solo rating effects.

Do **not** implement a new Worker, migration, Redis projection, chat hub, time-ordered social history, revocable share link or public leaderboard. Do **not** rerun multiroom load/fairness matrices or crash recovery to “complete” Spec 12. A failed narrow result/authorization smoke test is worth fixing because it affects the actual game; theoretical global rating-order corner cases are not.

**Verification plan:** Inspect and document result contracts and quick focused tests. A separate verification pass covers any necessary repetitive integration/regression checks and a Git checkpoint. If no genuine gap exists, end Spec 12 with the documented evidence—not implementation for its own sake.

### Recorded browser/deployment handoff (NOT work in Spec 12)

The completed Spec 10 backend certificate has several **mandatory later client checks**, not reasons to expand this spec: production browser SignalR keepalive must respect the documented Ghost Lap server heartbeat (client ping at most 5 seconds and client timeout consistent with the server's 15-second timeout); initial client tick estimation must avoid systematic `future_tick` refusals and re-anchor on acknowledgements/refusals; input action IDs, including a same-tick Burst press/release, must survive retry without double firing; resync must apply snapshot `(match,generation,lifecycle,sequence)` and event-cursor rules without stale/duplicate effects. The eventual PixiJS renderer must use local prediction/reconciliation, remote interpolation and true authoritative candle/contact/Burst events rather than fabricate visual outcomes. Public HTTPS/WSS reverse proxy, cookie/origin topology, browser tab suspension and WAN behavior **remain unverified** until real target deployment/client tests. Record these as explicit handoff items, not as completed Spec 12 tests.

## 4. Deferred product backlog: not 12.x work

The earlier social/rating design is parked: bounded room chat/emotes; presence messaging beyond existing connection/ready status; public history shares and revoke tokens; Elo and guest rating ledger; Redis ZSET rankings, Worker outbox/inbox and stream recovery; replay sharing URLs; social anti-abuse and multiroom load of those systems. It may be revisited **only after** the core rewind/branch demo and full visual game are excellent and the user requests it.

No planned Spec 12 work may modify Spec 11 source ownership or create a read-only sharing exception. Solo originals and child histories remain private to their owner. No change to Marrow, Revenant or the complete Spec 10 gameplay engine.

## 5. Exit and handoff

Produce a concise mapping from actual result/spectator/rematch fields to future browser consumption, links to previously executed Spec 10 evidence, any real short tests required for missing fields/permissions and an explicit list of visual/browser limitations. A valid exit may be **zero production files changed**. Browser scene quality, real browser input feel, event/FX/audio synchronization and public TLS/WSS target environment remain later presentation/deployment gates and must not be falsely certified by this document.
