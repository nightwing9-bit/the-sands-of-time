# Spec 12 — Ghost Lap V2: chat, spectator, sharing and rankings

**Basket:** 4 | **Dependencies:** spec-03, spec-10, spec-11 | **Owner paths:** `src/GhostLap` messaging/standing/share application, domain migrations, `src/Hosts.Realtime` handlers, `tests/GhostLap/Social`

## User value and outcome

Complete bounded real lobby chat/emotes, spectators/read-only share, rematch/profile/eligible multiplayer ranking with exactly-once durable rating effects.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No rating from solo rewrites, no public enumeration of private ghost history, no mass social network features or AI matchmaking.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R12.01 — Lobby social.** WHEN a room member sends allowed chat/emote, THE SYSTEM SHALL broadcast with identity, time, ordering, length/rate limits and clear permission controls.
**R12.02 — Spectator live.** WHEN a permitted watcher joins active room, THE SYSTEM SHALL receive snapshots/results and deny all participant commands independently of UI hiding controls.
**R12.03 — History sharing.** WHEN owner creates a replay share link, THE SYSTEM SHALL issue unguessable revocable, optionally expiring token to read-only solo replay, private by default.
**R12.04 — Share revocation.** WHEN owner revokes token or link expires, THE SYSTEM SHALL future read attempts fail with denial while owner retains source.
**R12.05 — Public privacy.** WHEN unauthorized user enumerates history via list/ID, THE SYSTEM SHALL expose no private source without owner or valid share token.
**R12.06 — Rating eligibility.** WHEN a valid live multiplayer match finalizes, THE SYSTEM SHALL update ranking from exactly one eligible result; never include solo/ghost/branch sessions.
**R12.07 — Multiplayer rating.** WHEN 2–4 valid competitors finish or forfeit, THE SYSTEM SHALL use a defined multi-way rating formula with transparent tie handling and durable per-player effect audit.
**R12.08 — Cache consistency.** WHEN Redis sorted-set cache clears or replays, THE SYSTEM SHALL rebuild from authoritative Postgres rating values, no duplicate score gain.
**R12.09 — Profile/rematch.** WHEN finished users inspect results/profile or rematch, THE SYSTEM SHALL display actual breakdown, rating change and real invitation/new match state.
**R12.10 — Abuse safety.** WHEN guest chat flood or fake rating messages arrive, THE SYSTEM SHALL rate-limit, authorize and reject unverified result/rating commands.
**R12.11 — Share-token transport privacy.** WHEN a user opens a tokenized read-only replay route, THE SYSTEM SHALL prevent raw share-token leakage in app/proxy logs, traces, third-party Referer and caches, and retain expiry/revocation enforcement.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

Multiple guests chat, spectate and share/revoke solo replay safely; live matches update transparent durable ranking once and redis cache rebuilds reliably.
