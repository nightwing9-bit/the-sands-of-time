# Spec 12 design — Bounded social features and PostgreSQL-truthful competitive ranking

## 0. Preconditions, authority boundaries and critical corrections

This is **new planned work** depending on real, tested Spec 03 identity, Spec 10 immutable result/placement/ranking-policy descriptor and Spec 11 owner-verified solo catalog. Inspect actual code, schema, host topology, Redis version/config, outbox/inbox contracts, DB role grants and `docs/architecture/{01-master,02-spec-map,03-contracts}.md` before editing. **Do not alter Marrow public contracts, canonical solo history, or certified Revenant.** Spec 13 presentation is excluded. No AI, full multiplayer replay, matchmaking, social graph or permanent global chat archive.

**Correctness-critical decisions over original Spec 12:**

1. A **match-level unique rating application** is the business idempotency unit, **never** `(matchId, playerId, algorithmVersion)`.
2. Pairwise actual `S_ij` comes from immutable **Spec 10 competitive placement**, not raw score independent of forfeiture.
3. The rules/result carry an immutable `ratingAlgorithmId` selected **when the match starts**; delayed events after deployment updates cannot select whichever version happens to be current. Initial `elo-pair-v1`; Spec 10 owns serialization of this opaque ranking-policy identity, Spec 12 owns its meaning and application. The application receipt pins the same ID and result digest.
4. First-time rating rows require race-safe **creation before sorted locks**; same-match effects and cache events commit together.
5. Redeemed replay sessions remain bound to the originating grant and must honor **future** revoke/expiry checks.
6. Redis state is expendable; rebuild and stream retention are not assumed reliable merely from consumer pending metadata.

If the Spec 10 finalized event/result lacks required identity/placement/eligibility/digest/policy fields, **fix the Spec 10-owned contract before rolling out the rating worker**, not by guessing placement in Spec 12. `MatchFinalizedV1` may remain the external event name if no existing consumers require a breaking change, but its **actual typed payload** must be coordinated with Spec 10 before implementation. Treat requirements as contracts to implement, not claims of existing runtime capability.

## 1. Room chat, presence and spectator access

A server-derived room participant/spectator principal may send authorized lobby/result chat and limited emotes as configured; no social input is processed in the tick reducer. Initial caps: 280 Unicode-normalized plain-text characters, five chat messages per 10s per user, a versioned list of eight emote IDs, bounded emote rate, last 50 messages per room in ephemeral storage. Normalize text/whitespace, filter control characters, escape on output, cap wire size, no raw HTML or script. Each room uses an ordered message sequence from its social lane and server timestamp; no claim of global ordering or durable full archive. Messages received during illegal room lifecycle stages are typed rejects. Host/admin mute or bounded room-level abuse controls may be used without a new moderation platform.

Presence and room authorization derive from actual Spec 10 membership, role and lifecycle, not Redis ephemeral counts or user-posted slot IDs. Only a verified competitor with the current **controller epoch** may send gameplay commands; spectators may subscribe to authorized snapshots/results and social channels only. Enforce at hub and application layer, even if UI is bypassed. Invite codes grant room discoverability/permissioned join by policy, never private historical replay access.

Use Spec 10's **bounded, coalesced, asynchronous** snapshot fan-out for spectators, with configurable per-room cap (initial target 8 concurrent spectators/room) and time/bandwidth limits measured under multi-room tests. Spectator join starts from a full current snapshot and later generation/tick sequence updates; slow watchers cannot accumulate unbounded snapshots or stall the authoritative loop. Distinguish snapshots (replaceable) from membership/terminal decisions (refetchable/reliable). No watched player or spectator becomes a physics entity merely from presence.

## 2. Owner-scoped, read-only solo replay sharing

`ghostlap.replay_share_grant`: random token digest (never plaintext), grant ID, owner principal, **one** verified eligible completed solo original/attempt timeline reference, creation/expiry/revoke state, read-only scope, active link count. Use cryptographically secure random token with >=128-bit entropy (prefer 256-bit), hash for lookup, constant-time check where applicable, bounded issue/redeem rate and maximum five active links per history initially. Token is a bearer capability **only** for the intended historical replay reads. No owner-wide original listing; no `BranchFrom`, `CreateHistory`, control, source mutation, raw auth tokens or competing live match access. Unrecognized, expired and revoked bearer links get uniformly nonrevealing errors.

**Recommended low-leak redemption:** put the bearer token in a URL fragment on a first-party, no-third-party bootstrap page, submit it in a protected POST body to redeem, immediately remove fragment from browser URL, obtain a short-lived HttpOnly/Secure/SameSite scoped read-only session and navigate to clean replay URL. A URL fragment is not in normal HTTP request target/Referer; the bootstrap must not run third-party analytics/error telemetry or leak the token in client logs. If using a path/query token route instead, fronting reverse proxy/CDN **must explicitly scrub or disable access logging/tracing for the raw path before any request log**, and same first-party resource/no-referrer/no-store rules apply. This is a transport choice, **not** a reason to develop unrelated frontend visual design.

Every token bootstrap/redemption and protected replay response uses `Cache-Control: no-store`, `Referrer-Policy: no-referrer`, no-index and strict third-party resource/cross-origin controls (CSP where configured), with actual proxy, application, analytics and exception redaction. Do not echo token in page HTML, redirect `Location`, events or error messages. Test the deployed route with actual browser/headers/proxy logs, not just a mocked React handler.

**Revocation-after-redemption semantics:** a short replay session stores its originating `grantId` and limited history/scope/expiry. Every subsequent **protected data fetch** or authorized stream initiation checks the current grant not revoked/expired (or an equivalently strong immediately invalidated binding). Revocation prevents all future data retrieval and new seek requests; already delivered bytes are not retractable. A history owner retains private access. Explicit share permissions do not modify the underlying Timeline ownership or original verification state.

## 3. Immutable result input, ranking eligibility and algorithm identity

Consume only the **PostgreSQL committed** `MatchResult` and its transactionally created `MatchFinalizedV1` outbox event. Before rating, verify that the message's match ID, immutable result digest, typed version, sealed roster/eligibility, placement and `ratingAlgorithmId` agree with canonical result. Do not compute or trust scoring from a hub/client payload. Duplicate event delivery is expected, conflicting duplicate digest for one match is a corruption/security alert. **Aborted** receipts have no finalized result/outbox. A finalized but `ratedEligible=false` result is a valid **unrated** result; record consumption/reason idempotently without effects. History originals/branches/challenges can never create rating application receipts.

Spec 10 competition order is **authoritative**: nonforfeiting finishers first, then forfeits; higher score within each status class ranks above lower; equal scores within the same class share competitive placement (slot only for stable display). For `n=2,3,4` distinct started eligible principals:

```
E_ij = 1 / (1 + 10^((R_j - R_i)/400))
S_ij = 1 if rank_i outranks rank_j; 0 if worse; 0.5 if tied
Delta_i = K/(n-1) * SUM_{j != i}(S_ij - E_ij), K = 24
newRating_i = oldRating_i + Delta_i
```

Initial rating = 1000. Compute **all** pair expectations from the **same transaction's locked pre-match ratings**. Use deterministic calculation/rounding with fixed persisted `numeric` precision (initial policy: six decimal places, document C# math/rounding conversion and audit both unrounded and applied deltas). Algebraically raw pair contributions are zero-sum; bounded fixed precision may cause tiny residual, record it honestly rather than silently inventing points or pretending integer conservation. Tie/forfeit examples (including a forfeited high-score player ranked below lower-score finisher) must match canonical placement. No per-player hidden bonus, score-per-distance rerating or artificial slot tiebreak. A future K factor, algorithm or season change is a new policy identity; never rerun old live effects by virtue of changing defaults.

**Eligibility:** match receipt committed, `MatchResult` truly finalized, at least two distinct *server-verified guest principals*, immutable `ratedEligible` under sealed match policy and supported `ratingAlgorithmId`. Matches where every participant forfeits are **unrated**; one nonforfeiting finisher plus eligible forfeits can be rated. A lost authority/service interruption is **not** manufactured as player forfeit. Guest accounts can be created by the same human; label standings **casual competitive** and apply modest rate/invite protections, not false anti-smurf guarantees.

## 4. One atomic PostgreSQL rating application

**Suggested GhostLap-owned tables/constraints** (adjust private names to real migrations):

- `rating_account(playerId PK, rating numeric(18,6), revision bigint, eligibleMatchCount, updatedUtc, ...)`.
- `rating_match_application(matchId PK, resultDigest, algorithmId, eligibilityDecision, appliedAt, ... )` **unique by matchId alone**. Stores algorithm/version and effect count; an unrated finalized result can have one no-effects receipt to pin outcome.
- `rating_effect(matchId,playerId,preRating,delta,postRating,preRevision,postRevision,competitivePlacement,applicationIdentity, ... UNIQUE(matchId,playerId))`.
- existing transactional inbox and cache outbox **in the same DB transaction** through the owning messaging port; outbox record identity unique per effect/revision. No shared Marrow schema rewrite.

Apply with the following transaction protocol:

1. Read immutable final result/evidence and validate eligible and supported sealed algorithm. Begin transaction. Attempt insert/find **match-level unique receipt**; duplicate matching receipt returns the recorded decision/effects, never invokes formula again. A conflicting digest/version results in a visible corruption error and no effects.
2. For eligible match, **insert missing rating_account rows** for all distinct participant IDs (`INSERT ... ON CONFLICT DO NOTHING`) in a deterministic order, then acquire each participant row lock individually in sorted stable player-ID order (`SELECT ... FOR UPDATE`); do not rely on `ORDER BY` alone or an absent-row lock. Other rating writers use identical lock order. Bounded DB deadlock/serialization retries restart the **whole** transaction; do not double-apply partially committed effects.
3. Read all locked pre-ratings/revisions from the same transaction; compute all expected/actual pair scores and deltas from that one snapshot; write each new absolute rating and `revision+1`, per-player effects, match receipt/pinned algorithm/eligibility, inbox and per-player outbox **atomically**. Audit actual revisions/transaction application relationships, not a fictitious global chronological order. If any participant effect fails, rollback **all**.
4. Commit before acknowledging rating applied. On SQL-commit ACK loss, look up the authoritative match receipt; don't recalculate. Delivery to Redis is at-least-once; end-user profile may indicate `RatingPending` until receipt exists. A repeated `MatchFinalizedV1` after upgrading default algorithm must see the same receipt and unchanged player revisions.

If the result has no nonforfeiting player or otherwise unrated, commit one idempotent `Unrated` receipt/reason with **no** participant effects or rating outbox and never reinterpret it after policy upgrades. Never use `UNIQUE(matchId,playerId,algorithmVersion)` as live business dedup. Retroactive re-rating, if ever built, belongs to a distinct versioned projection/season and not this live ledger.

**Concurrency example:** M1 and M2 overlap on player P and one result message arrives before the other; the first transaction to obtain P's row lock applies against its snapshot, the second then sees P's revised rating. Record the actual pre-state and applied revisions, and accept that different legitimate serialization orders may give different Elo within bounded formula behavior. No global Kafka-like ordering is required. Players with disjoint match rosters can be processed concurrently.

## 5. Redis as a safe disposable projection

Redis sorted-set(s) for standings and accompanying per-player revision/value/hash are **derived**, never the primary rating source. Each outbox payload contains player ID, absolute rating, new per-player revision, eligibility and projection identity. A **single atomic Redis operation** (Lua or supported transaction) compares stored revision and then updates both rating metadata and sorted-set score. A lower revision cannot overwrite newer. Equal revision plus matching value is idempotent; if index entry is missing, repair it. Equal revision with a different value is a **consistency alarm** to reconcile from PostgreSQL, not silent success. Initial zero/missing revision may be populated from authoritative snapshot; ensure partial key loss does not permanently leave a player absent from rankings.

When Redis is cleared or rebuilt: keep the API honest (`rebuilding` status and bounded PostgreSQL fallback for realistic hackathon scale); bulk-read authoritative Postgres accounts/revisions and **merge** through the same atomic nonregressing operation while live events flow. Do not drop/replace a live newer score using a slower older scan. Treat rebuild ready only after a complete scan and outbox catch-up/reconciliation. Avoid a naive `DEL` of a live sorted set mid-rebuild without a generation-safe cutover. Current project scale does not justify a globally ordered coordinator; choose the simplest existing-port rebuild proven to converge. Test missing ZSET with surviving revision hash, surviving ZSET with missing hash, duplicate and reverse deliveries, racing 2 results and a replayed old update.

**Stream recovery:** inspect deployed Redis version and actual Marrow stream topology. Generic `XTRIM MAXLEN` can discard pending/unread payload even if PEL references remain. Redis 8.2 supports `ACKED` retention when appropriate for all relevant groups; otherwise use consumer-safe watermark trimming or durable PostgreSQL outbox replay/reconciliation consistent with existing messaging contracts. Prove pending claim/retry, poison/dead-letter replay via business receipts, bounded retention, Redis volume-loss recovery and measured persistence (`appendfsync always` if actually configured). Do **not** modify Revenant streams blindly or assume Redis is an authoritative rating ledger. Realtime/Worker grants must be tested using actual runtime logins.

## 6. Profiles and security/runtime tests

Profiles and leaderboard show actual match breakdowns and ranking status (`Rated`, `RatingPending`, `Unrated`, `TemporarilyUnavailable`) with post-rating and effect audit. No fake “rating +20” on event receipt before SQL commit. Public view includes only intended participant/standing fields; never accidental owner-private solo source, link token or foreign history list. Rematch uses a **fresh Spec 10 match ID**, not a second rating effect for the old one. Rank queries can fall back to PostgreSQL for small-scale correctness during Redis loss; explicitly degrade if too costly, never quietly return zero ratings.

**Mandatory adversarial tests:**

- 2/3/4 player pairwise analytical cases, shared scores, all forfeits unrated, high-score forfeiter below normal finisher, starting 1000, documented numeric precision; no solo/branch rating.
- Replay exact same `matchId` under different outbox IDs, after worker restart, after `elo-pair-v2` default switch and after SQL ACK loss: receipt/effects/revisions stay singular; same match with altered payload digest fails audit.
- M1/M2 with shared player including no existing rating row, both lock serialization orders and duplicate events, rolled-back participant error; one consistent pre-state per transaction and no deadlocks left unresolved.
- Redis two revisions reversed, equal-revision index repair, mismatched-value alert, partial/full volume loss during live rebuild, pending consumer trim/claim and poison replay; verified Postgres values and revisions win.
- Forged spectator gameplay call, room flood/injection, unauthorized private history reads, token enumeration, raw token leaks in real proxy/tracing/browser/caches, revoked link **after** clean-URL redemption and subsequent replay seek/read denied.
- Combined target-host test with active 4P rooms, solo replay/recording/seek, spectator/chat pressure and rating Redis/Worker backlog; verify Spec 10 tick/queue/overload boundaries and no direct DB/stream waits in critical simulation. Actual Ghost Lap/Marrow/Revenant runtime permission and DI coexistence, plus match receipt/access security; production contains no test-only bridge/`/__e2e` endpoint.

**External technology assumptions to verify against deployment:** PostgreSQL session/row locking semantics; Redis `XTRIM` default `KEEPREF` versus Redis 8.2 `ACKED`; SignalR invocation-scoped hubs and independent background send path. These are design constraints, not proof of installed versions or passing tests.
