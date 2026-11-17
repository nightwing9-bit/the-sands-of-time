# Spec 12 implementation tasks — Read-only sharing, bounded social and correct rating

**All unchecked / not implemented.** Dependencies Spec 03, verified Spec 10 result/receipt/placement contract and verified Spec 11 history catalog/ownership. Use real infrastructure tests; keep Marrow/Revenant frozen. Before writing code, inspect the actual outbox/inbox, Redis version, DB schema/roles, guest identity, typed contracts, Realtime/Worker DI and deployed proxy logging configuration. Log real commands, evidence, commits and blockers in `docs/kiro-log.md`.

- [ ] **12.0 Cross-spec rating/share contract preflight (BLOCKER)**
  - **Inspect:** Spec 10 `MatchResult`, `MatchFinalizedV1` and result policy fields; ensure `competitivePlacement`, tie/forfeit, digest, `ratedEligible` and sealed `ratingAlgorithmId` at start are immutable. Inspect Spec 11 verified owner/source and per-read authorization, and existing messaging/Worker grants/Redis topology.
  - **Produce:** exact typed event validation and profile states, supported `elo-pair-v1` policy, committed-result verification owner, token redemption security path and concrete Redis trim policy for actual version. Coordinate any contract gap with the **producer** Spec 10/11 before implementation; no invented frozen API.
  - **Verify:** 2/3/4 result fixtures include forfeit score-inversion and tied players; old finalized events don't get a new algorithm on delayed processing; current production proxy can suppress token logs.
  - **Done when:** no ambiguous result/eligibility/identity input remains before ledger or sharing code.

- [ ] **12.1 Ephemeral room chat, emotes and authoritative presence**
  - **Produce:** room-authorized bounded plain-text chat (280 chars, five/10s initial), eight enumerated emotes, server timestamp/room sequence, bounded last 50 and rate limits, lifecycle/role-based send privileges, safe normalized/escaped content and fan-out independent of tick reducer.
  - **Verify:** real two-client order, independent room isolation, chat/emote flood, injection/oversize, participant revoke and spectator read/send policy; slow social receiver doesn't block input/world simulation.
  - **Done when:** real messages and presence are safe, bounded and separated from authority.

- [ ] **12.2 Read-only live spectator and revocable history share**
  - **Produce:** capped live spectator admission using Spec 10 snapshot delivery/resync, service+hub gameplay denial; `replay_share_grant` hashed high-entropy owner/single-history/scope/expiry/revoke, read-only short session bound to grant, no token in stored plaintext or telemetry. Implement secure token bootstrap (prefer fragment+POST clean-URL) or proven proxy redaction route, with no-store/no-referrer/no-index/first-party restrictions and per-fetch grant revalidation.
  - **Verify:** spectator forged controls at every boundary, 8-watchers initial cap/slow watcher isolation, foreign original/branch owner enumeration, replay token cannot fork, expiry/revoke before and **after** redemption, cache/analytics/browser Referer/application/proxy/access logs not containing raw secret under actual deployed stack.
  - **Done when:** genuine spectators watch safely, grants can be revoked for all future reads and private sources remain private.

- [ ] **12.3 Exactly-once multiway rating application and cache outbox**
  - **Produce:** match-level receipt `UNIQUE(matchId)` pins immutable result digest + sealed algorithm, participant effects `UNIQUE(matchId,playerId)`; rated/unrated decisions, first-account idempotent upsert before sorted per-player `FOR UPDATE` locks, pairwise placement-based Elo and deterministic documented fixed precision; all participants + per-player revisions + inbox/outbox in **one** Postgres transaction. Include bounded deadlock/serialization retries and a typed `RatingPending` state. Runtime Worker grants limited to owned tables/ports.
  - **Forbidden:** original `(matchId,playerId,algorithmVersion)` business uniqueness, raw-score pair outcome ignoring forfeit placement, source/branch rank effects, a global delivery-order coordinator or Redis-only ratings.
  - **Verify:** 2/3/4, true ties, high-score forfeiter, all forfeits unrated, one normal finisher, first-time player concurrent M1/M2, rollback whole group, lost SQL ACK, outbox duplicate under new message ID, same match after algorithm default change and conflicting digest. Record per-player pre-state/applied revision and exactly one live receipt, no second rating.
  - **Done when:** real committed results yield at most one atomic auditable effect per player/match under concurrent retries.

- [ ] **12.4 Redis revision projection, stream safety and profile/rematch integration**
  - **Produce:** atomic per-player revision+absolute-rating update of ZSET and metadata, missing-member equal-revision repair, mismatched revision/value alert and reconcile, monotonic merge rebuild from PostgreSQL while live events continue. Inspect/deploy safe version-specific Redis stream trimming/pending claim, bounded poison/retry and recovery from lost volume using existing Postgres outbox/inbox mechanisms. Real profiles/standings/result/rematch APIs show true pending/unrated states and can truthfully fall back when Redis fails; no retroactive rating after terminal abort.
  - **Verify:** reversed two distinct revisions, duplicate update, partial/full Redis deletion during live new event, pending stream payload retention/trimming under actual version, poison recovery, Worker/Realtime logins, rebuild post-Restart. `RatingPending` eventually becomes actual applied receipt; Redis clear does not erase ledger. Rematch rates only its **new** match ID.
  - **Done when:** standings are accurate projections that survive expected loss and event reordering without rating duplication.

- [ ] **12.5 End-to-end social/privacy/rating assurance**
  - **Produce:** reproducible real-client suite connecting room host/players/spectators, sharing completed private solo source, revoking grant, completing eligible MP results and observing actual rating changes and recovery. Record exact operator recovery steps and known casual guest/abuse limitations.
  - **Verify:** tests across actual browser + API/hub/proxy/DB/Redis; adversarial token/identity/permission checks; no spectator affects score, no share session can issue branch, no raw bearer in logs; no solo effect; real 2/3/4 rating outcomes, redelivery after policy switch; combined 4P multiplayer rooms plus live solo record/seek and chat/spectator/Worker backlog do not exceed Spec 10 measured tick/queue budgets; Realtime/Worker/Marrow/Revenant DI and DB grants coexist.
  - **Done when:** requirements R12.01–R12.19 have observed proof and no correctness/privacy blocker remains.

## Spec exit certificate (not pre-filled)

Log actual transaction IDs/receipts (no secrets), real match result hashes and repeated-event tests, two-order overlapping match rating traces and initial account creation, Redis loss/trim/rebuild evidence with server version, revoked-grant-after-redeem browser trace, proxy/log token leak check, multi-client chat/spectator observations, cross-domain authorization tests and commit ID. **Never claim passed tests or successful certification without execution.**
