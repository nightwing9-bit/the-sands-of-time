# Spec 12 — Ghost Lap V2: safe social play, shareable histories and trustworthy ranking

**Basket:** 4 | **Status:** replacement specification, NOT implemented | **Dependencies:** Spec 03 identity/transport, Spec 10 immutable authoritative `MatchResult`/`MatchFinalizedV1`, Spec 11 owner-verified solo catalog | **Owned paths:** `src/GhostLap` messaging/presence/share/rating, its domain migrations and Worker/runtime integration, `src/Hosts.Realtime` handlers, `tests/GhostLap/Social`.

## Outcome and boundaries

Bounded lobby chat/emotes, authorized live read-only spectators, private-by-default owner-revocable solo replay links, real results/profiles/rematch, and **one auditable PostgreSQL rating application per eligible multiplayer match**, resilient to concurrency, duplicate delivery and Redis loss. This is a casual, guest-based ranking—not verified individual identity or professional anti-smurf protection. **No solo/ghost/fork rating, no MP history archive, no social network/matchmaking expansion.** Shared Marrow ports and completed Revenant remain frozen. Spec 13 visual/UI design is outside this scope.

## EARS-style requirements

**R12.01 — Bounded room messaging.** WHEN an authorized member sends chat/emote, THE SYSTEM SHALL validate server-derived room membership, allowed room lifecycle, normalized 280-character plain text or enumerated emote, rate/size limits, server timestamp and stable per-room ordering. It SHALL escape output and bound retained messages (initially 50 per room) without blocking the authoritative game loop.

**R12.02 — Presence and spectator role.** WHEN a permitted spectator joins a running room, THE SYSTEM SHALL send authorized current state/results without taking a competitor slot. Spectator membership shall have configured admission/bandwidth limits and be checked at hub and service boundaries for **every** gameplay/ready/controller command. A slow observer must not stall players or the tick loop.

**R12.03 — Safe owner sharing.** WHEN the verified owner shares an eligible completed solo original/attempt, THE SYSTEM SHALL generate cryptographically unpredictable, stored-hash-only, history-scoped read-only replay token, private by default, owner-revocable, expiring per configured policy, with bounded active tokens. It SHALL not expose an owner-wide list or grant new history/branch input privileges.

**R12.04 — Revocation after redemption.** WHEN the owner revokes or a grant expires, THE SYSTEM SHALL deny **future protected reads**, including those using a previously redeemed clean-URL read-only session tied to that grant. No share-derived session may outlive the grant authorization; already delivered bytes cannot be retroactively withdrawn.

**R12.05 — Token transport secrecy.** WHEN a browser visits a tokenized replay URL, THE SYSTEM SHALL use no-referrer/no-store/no-index controls and an authenticated read-only redemption flow or equivalently safe route; **raw bearer token never appears** in app/proxy/CDN logs, traces, metrics, analytics, third-party Referer or caches. Token routes shall not load third-party resources before secret removal.

**R12.06 — Private resource authorization.** WHEN a caller guesses a Timeline/catalog/share ID or sends a forged role, THE SYSTEM SHALL enforce server-side owner or scope-limited valid replay grant for each read. Share authorization cannot list another user's private originals, mutate source, initiate branch or become a regular player/owner identity.

**R12.07 — Exactly one live rating application.** WHEN a genuine eligible Spec 10 `MatchResult` finalizes, THE SYSTEM SHALL create **at most one live rating application per `matchId`**, irrespective of `algorithmVersion`, worker restart, event replay or delivery order. A match-level unique receipt pins algorithm identity and immutable result digest; participant effects are unique on `(matchId,playerId)`. Conflicting same-ID payloads are rejected and audited; unrated/aborted matches cannot later gain effects through redelivery.

**R12.08 — True competitive outcome.** WHEN calculating pairwise 2/3/4-player rating, THE SYSTEM SHALL derive win/tie/loss from Spec 10's **authoritative competitive placements**, including finisher-above-forfeit precedence and shared ties, **not** from a fresh comparison of raw score that contradicts placement. Stable display slot ordering shall never manufacture rating wins.

**R12.09 — Defined rating formula and versions.** WHEN a rated eligible result is first applied, THE SYSTEM SHALL use its sealed, supported `ratingAlgorithmId` (initial `elo-pair-v1`: starting 1000, K=24, pairwise Elo expectation, normalize by `n−1`) and record pre-update ratings, formula version, rounding/precision, actual atomic effects and per-player revisions. Later default algorithm changes cannot reinterpret an already played match or rerate a finalized receipt.

**R12.10 — Concurrent atomic application.** WHEN matches with overlapping/new guest players apply concurrently, THE SYSTEM SHALL ensure race-safe initial rating row creation, deterministic player-ID row-lock ordering, one coherent pre-match snapshot for all participants and an atomic PostgreSQL transaction for match receipt, all effects, rating changes, inbox and projection outbox. Deadlocks/serialization failures shall retry boundedly without partial effects.

**R12.11 — Source-of-truth and revisions.** WHEN multiple match events apply in any legitimate PostgreSQL transaction ordering, THE SYSTEM SHALL record actual application data and monotonically increasing revision **per player**; cache events carry absolute rating + revision. Do not claim global chronological order that is not guaranteed by delivery or DB sequence assignment.

**R12.12 — Redis convergence.** WHEN Redis events duplicate/reorder, keys partially disappear or a cache rebuild races live updates, THE SYSTEM SHALL atomically apply only nonregressing per-player revisions, repair missing index entries even on equal revision when PostgreSQL-consistent, detect mismatched same-revision values, and rebuild standings from authoritative PostgreSQL. Redis must never be the sole rating ledger.

**R12.13 — Durable messaging recovery.** WHEN Redis streams contain pending/unread items, consumer crashes, trim, poison messages or volume loss, THE SYSTEM SHALL use deployed-version-correct acknowledgment/retention policy and existing PostgreSQL outbox/inbox recovery, rather than treating trimmed pending references as recoverable payload. Actual Worker and Realtime roles must be least-privilege and work alongside Revenant.

**R12.14 — Eligibility and honest abuse boundaries.** WHEN processing a result, THE SYSTEM SHALL require committed Spec 10 result/outbox, a valid ranked policy version, at least two distinct verified guest principals and the result's immutable `ratedEligible` decision. A match with zero nonforfeiting finishers is unrated; a match with one normal finisher may rank forfeits according to the recorded rules. Network/service abort never counts as player forfeit. No claim that guest identity prevents multi-account abuse.

**R12.15 — Profiles, rankings and rematch.** WHEN a guest views results/standings/profile, THE SYSTEM SHALL show real persisted breakdown, current eligible rating and actual effect (or explicit unrated/pending state), not derive an invented score from Redis alone. Rematch uses Spec 10's newly committed match identity; profile views must never leak a different owner's private solo history.

**R12.16 — Honest degraded operation.** WHEN Redis is unavailable or rebuilding, THE SYSTEM SHALL serve coherent PostgreSQL fallback for supported small-scale reads or an explicit temporary unavailable response; it SHALL never silently present empty/zero standings as true, erase ratings, or lose durable rating effects. Chat/spectator pressure does not interfere with simulation.

**R12.17 — Security and anti-abuse.** WHEN malicious clients flood messaging, enumerate sharing tokens, spoof match-result events, attempt role escalation or submit cross-user history IDs, THE SYSTEM SHALL authenticate, authorize, rate-limit, return typed failures, and audit relevant security actions without logging tokens or private replay inputs.

**R12.18 — Preservation and verification.** WHEN integrated into existing hosts, THE SYSTEM SHALL reuse existing typed ports, migrations and outbox/inbox interfaces without changing Marrow's public Timeline behavior or certified Revenant. All ranking/security/stream correctness requires real transactional/transport tests, not only a mock or present endpoint.

**R12.19 — Combined-runtime load safety.** WHEN multiplayer, solo recording/seek, spectator/chat fan-out and rating/outbox consumers run together, THE SYSTEM SHALL preserve the measured Spec 10 tick-availability and bounded-memory guarantees; worker/database/stream pressure must not block the simulation owner.

## Blocking exit gate

Multiple real guest clients chat/emote, spectate safely, create/redeem/revoke solo replay grants (including post-redemption revocation) and see authentic durable results/rating. 2/3/4 player win/tie/forfeit formula; **same match replay across algorithm upgrades cannot rate twice**; two matches involving the same new guest serialize without missing-row races; Redis loss/reorder/rebuild and pending stream recovery converge; raw token not observed in actual app/proxy/cache/log/browser paths; no solo rating. Actual commands and evidence recorded; unrun checks remain blockers.
