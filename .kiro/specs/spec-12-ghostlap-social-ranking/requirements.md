# Spec 12 — Ghost Lap: minimal results and spectator handoff (hackathon edition)

**Status:** deliberately reduced replacement specification; NOT YET REVIEWED/IMPLEMENTED as this scoped handoff.  
**Folder note:** the existing directory name `spec-12-ghostlap-social-ranking` is retained for safe in-place replacement; **the specification no longer requires social, sharing or rating features**.  
**Prerequisites:** completed Spec 10 authoritative `ghost-mp-v2` gameplay, immutable `MatchResult`, `MatchFinalizedV1`, real rematches and spectator access. **Spec 11 is not a prerequisite**; solo source ownership/history is not part of this spec.  
**Purpose:** make existing real multiplayer results and spectators readily consumable by the future beautiful browser game, with **almost no new backend code**.

## 1. Product scope and exclusions

Ghost Lap is a fast 90-second **four-player showcase** (also supports 2P/3P), not a chat or social-network product. The player should see an accurate, attractive end-of-round winner/placement and score breakdown and be able to rematch. A permitted spectator should watch the same authoritative game read-only. **The physics and gameplay from Spec 10 remain complete and untouched**: responsive movement and collisions, rapid candles, charge stages 0–4, stronger/weaker contact, manual radial Burst, eight-second Overlit auto-Burst, deterministic event resolution and full 4P support.

**Not in hackathon scope:** room/lobby messaging, network emotes, chat presence history, public/private history-sharing URLs, share/revocation tokens, public profiles, Elo/ladder/MMR, Redis leaderboard, ranking Worker/inbox, cross-player identity verification, social feed, matchmaking, result-sharing service or full multiplayer replay archive. Existing Spec 10 spectator delivery and result/outbox persistence remain; **do not delete or rework already-working infrastructure**. In particular the outbox is allowed to have no configured rating consumer in this phase. Do not claim Redis delivery/rating occurred.

**What “ranking” means in the hackathon:** match-local **competitive placement** (winner, ties, forfeits, score components) from Spec 10's immutable result. There is no new persistent global player rating. A simple personal-best/win-count view is **optional after the visual prototype**, only if explicitly requested and feasible from already-authorized persisted results; it is not a 12.x acceptance obligation.

## 2. Requirements (EARS-style)

**R12.01 — Truthful match standings.** WHEN a completed multiplayer match is shown, THE SYSTEM SHALL use the existing immutable committed Spec 10 result for final competitive placements, real ties, finisher/forfeit order and authoritative score components. Never fabricate totals from snapshots, client arithmetic, a planned Elo formula or an unconsumed outbox event. All four slots can appear correctly.

**R12.02 — Honest lifecycle display.** WHEN a result is not durably committed, THE SYSTEM SHALL distinguish `Pending`, `Unverified` or `Aborted` from a finalized outcome per actual Spec 10 lifecycle. No fake winner, fake rated points or generic “Saved” success on a failed transaction. An unrated finalized match still has real match standings; `ratedEligible`/`ratingAlgorithmId` are retained provenance, **not a promise that ratings were applied**.

**R12.03 — Safe roster reads.** WHEN a match result or current room state is requested, THE SYSTEM SHALL use existing verified guest/roster/room authorization and resource-kind checks. Knowing a match ID alone does not grant private result retrieval. Do not leak another guest's solo history, token or owner records. Use only existing authorization integration unless a demonstrated omission requires a small correction.

**R12.04 — Existing spectator contract.** WHEN a permitted spectator is connected, THE SYSTEM SHALL preserve Spec 10's bounded, read-only snapshots/events/resync and appropriate terminal match state without occupying a competitor slot. Spectators cannot control, ready, Burst, move, change team/slot or initiate an unauthorized rematch. Slow spectators must not block play. No new spectator infrastructure or chat is required.

**R12.05 — Genuine rematch.** WHEN a player initiates an authorized rematch, THE SYSTEM SHALL reuse Spec 10's actual fresh-match lifecycle: new ID, seed, tick-zero state, receipt and independent final result. The previous result remains unchanged; the user interface must not display previous snapshots/events as the new match. No new ranking effects are introduced.

**R12.06 — Small typed browser handoff.** WHEN the later game UI consumes results, THE SYSTEM SHALL expose or document the **existing** typed status/result/placement/score/spectator/rematch contracts, including what is authoritative, versions and stable event/snapshot IDs. Add a small read-only projection only if a specific indispensable display field is actually missing; do not build duplicate result tables or a new ratings API.

**R12.07 — Preservation and minimum verification.** WHEN this scoped specification exits, THE SYSTEM SHALL demonstrate that existing 4P results, authorized spectator terminal view, foreign/unknown actor denial and fresh rematch still work using **reused Spec 10 evidence** plus at most focused gap tests. Marrow, Revenant, `ghost-v1`, historical `ghost-mp-v1`, active `ghost-mp-v2`, result/outbox uniqueness and gameplay semantics remain unchanged.

## 3. Exit boundary

**The smallest success is allowed to be mostly documentation:** if Spec 10 already provides every necessary result/spectator contract, Spec 12 can close with a short verified mapping and a narrow smoke check, **zero new production code and zero new migrations**. The later polished browser owns the actual results screen, animations, HUD and game audio.

**Do not run** rating-consumer, Redis stream/loss, share-token security, chat flood, replay-link and full four-room load suites. They test features explicitly removed from this phase. Never mark unavailable browser visuals or a nonexisting Elo ledger as implemented.

## 4. Superseded earlier Spec 12 scope (explicit, not silently forgotten)

The previous R12.01–R12.19 social/ranking requirements are **superseded for the hackathon**. Message chat/emotes, share grants, token redemption/revocation, pairwise Elo, rating revisions/inbox, Redis standings/recovery and combined Worker/social load move to an optional **post-hackathon product backlog**, not required dependency gates. Already completed Spec 10 results, spectator permissions and transactional outbox survive. **The later Spec 13 plan must not treat the old social/rating tasks as prerequisites.**
