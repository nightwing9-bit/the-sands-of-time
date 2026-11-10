# Spec 12 implementation tasks — Ghost Lap V2: chat, spectator, sharing and rankings

**Execute tasks in listed order.** Dependencies: spec-03, spec-10, spec-11. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **12.1 Room chat/emote and presence**
  - **Precondition/input & inspect:** spec-03 transport, 10 lobby states. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** bounded sanitized chat, emotes, membership/rate limits.
  - **Forbidden changes:** social state in tick reducer or unbounded message history. Do not change frozen shared public contracts without ADR.
  - **Verification:** two-client chat ordering and flood/injection tests.
  - **Done when:** members exchange real messages safely.

- [ ] **12.2 Spectator access and history share**
  - **Precondition/input & inspect:** spec-10 room snapshot, spec-11 solo ownership. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** read-only spectator handlers, hashed revocable share token service and no-referrer/no-store token-route policy with application/proxy redaction (no third-party token-route subresources).
  - **Forbidden changes:** replay token grants branch, private list exposure. Do not change frozen shared public contracts without ADR.
  - **Verification:** forged spectator control, revoke/expire, response headers, browser Referer, proxy/app-log redaction and cache tests.
  - **Done when:** watchers can see but cannot alter state.

- [ ] **12.3 Durable rating ledger**
  - **Precondition/input & inspect:** MatchFinalizedV1, Postgres rating tables, outbox/inbox. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** pairwise formula/algorithm version, unique per-match effects, sorted-set cache projection.
  - **Forbidden changes:** rating from solo or counting duplicate delivery. Do not change frozen shared public contracts without ADR.
  - **Verification:** 2/3/4/tie/forfeit and duplicate relay tests.
  - **Done when:** one match yields one set of durable rating effects.

- [ ] **12.4 Profile/standings/rematch integration**
  - **Precondition/input & inspect:** profile read API, spec-10 new match ID. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** eligible leaderboard, actor history, results and rematch endpoints.
  - **Forbidden changes:** Redis as only authority or fake rematch UI. Do not change frozen shared public contracts without ADR.
  - **Verification:** clear Redis/rebuild, cross-player result owner checks.
  - **Done when:** rank standings recover with durable values.

- [ ] **12.5 End-to-end privacy/social gate**
  - **Precondition/input & inspect:** web contracts and real hub transport. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** browser/API cases across guest, spectator and shared replay link.
  - **Forbidden changes:** over-sharing login tokens in URL or logs. Do not change frozen shared public contracts without ADR.
  - **Verification:** multi-client chat+view+revoke, no spectator score changes; raw token absent from access logs/traces/caches under token-route smoke.
  - **Done when:** social workflow fully functional for spec-13 UI.

## Spec exit verification (blocking)

Multiple guests chat, spectate and share/revoke solo replay safely; live matches update transparent durable ranking once and redis cache rebuilds reliably.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
