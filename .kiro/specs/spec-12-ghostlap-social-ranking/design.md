# Spec 12 design — Ghost Lap V2: chat, spectator, sharing and rankings

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-03, spec-10, spec-11. **This spec owns:** `src/GhostLap` messaging/standing/share application, domain migrations, `src/Hosts.Realtime` handlers, `tests/GhostLap/Social`. **Do not implement:** No rating from solo rewrites, no public enumeration of private ghost history, no mass social network features or AI matchmaking.

**Consumes:** Public contracts from spec-03, spec-10, spec-11; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Messaging and presence

Use shared Marrow typed hub envelopes/identity/rate limit, but GhostLap owns chat semantics, room authorization and moderation restrictions. v1 chat: 280-char max normalized plain text, server timestamp, escaped rendering, rate e.g. 5 messages/10 seconds/player (versioned policy), bounded 50 recent messages per room. Allowed small emote enum (8 IDs) with event rate bound and no user-supplied arbitrary HTML/scripts; no expensive global chat archive. Deletion/moderation may use coarse admin mute and report/no-op display rules as justified; never let chat impact fixed tick loop. Presence uses authoritative current membership snapshots, not only Redis ephemeral counts. Invite codes are random, short-lived, rate-limited lookup; their existence does not authorize private historical replay.

### Spectator and share

Live spectator receives read-only snapshots from spec-10 room but is not inserted into player slots, collisions or rankings; server validates role at every gameplay command. Shared solo replay links use 128-bit+ cryptographic opaque token stored hashed; include owner/history/created/expiry/revoked flags; default private, read-only `GET` with token, never exposes raw input owner secrets or grants branch/write rights. Owner may revoke. Share-token routes respond with `Referrer-Policy: no-referrer` and `Cache-Control: no-store` (and equivalent no-index/canonical controls); do not load third-party subresources while the raw token is in the URL. Serve self-hosted assets on the token route or redeem the token server-side into a short-lived, **read-only** session then redirect to a clean URL. Exclude/redact the raw token in application, tracing, exception, CDN and reverse-proxy access logs (the proxy needs an explicit sanitized/log-off route policy; hiding it only in React is insufficient). No token is passed in third-party error telemetry or analytics. Share acceptance tests inspect real headers, browser Referer behavior and configured server/proxy logs. A public sharing option is **explicit** opt-in and only for verified solo originals/attempts as policy; no full MP archive. Limits: 5 active links per history initial, versioned config; retention/quota later measured. Rate-limit token enumeration and no cached public list.

### Ranking formula v1, idempotency

Provisional multi-way pairwise Elo for 2–4 real players (no AI). Starting rating 1000; for each opponent j expected `E_ij=1/(1+10^((R_j-R_i)/400))`; actual `S_ij=1` win by total score, 0 loss, 0.5 tie; forfeited players place below nonforfeiting as spec-10. `Δ_i=(K/(n−1)) Σ_j(S_ij−E_ij)`, K=24 provisional, round only after computing all deltas from same pre-match snapshot. Apply all player rating effects in **one Postgres transaction** with `UNIQUE(matchId,playerId,algorithmVersion)`, use deterministic player-ID lock ordering to avoid deadlocks and bounded retries; receipt/leaderboard cache emitted via durable outbox. Need all n deltas sum approximately zero before final integer rounding; explicitly distribute remainder deterministically if integer rating and demand exact conservation, or store fixed numeric precision with documented rounding. This is a provisional rating algorithm for testing and human fairness tuning; version change requires separate algorithm ID, no silent retroactive rewrite. Guest eligibility: minimum two distinct verified guest principals, completed room result; multiple accounts/same human abuse detection limited, label **casual rating**, not secure anti-smurf claims. Disconnections/forfeits produce outcome as above; rematch is a distinct matchId and may legitimately rate again.

Authoritative rating updates occur in PostgreSQL. Within the existing locked transaction, record the actual rating-application order, pre-update snapshots and a monotonic revision for each affected player, atomically with inbox receipt, **all** participant effects and cache outbox. Use audited PostgreSQL transaction application order, not an assumed chronological delivery order; distinct legitimate transaction orders need not yield identical Elo. Cache events contain absolute rating values plus revision. Atomic Redis application refuses an older revision replacing a newer one; rebuild uses the same nonregressing revision rule while live events continue. Redis remains reconstructible from PostgreSQL; no global event-ordering guarantee or reorder coordinator is introduced. The provisional formula above remains unchanged.

### Tests

Actual room lobby chat across separate clients, permission and injection payload testing; spectator forbidden calls through hub even if client UI hacked. Share token only read, expiry/revoke, no enumeration; raw token absent from app/proxy logs, telemetry, Referer and caches on a real browser route. Duplicate `MatchFinalizedV1` deliveries and cache rebuild yield exactly one rating business effect. Multi-way 2/3/4P expectation/ties and forfeit cases asserted under chosen formula, no solo match rating.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- One finalized match changes each eligible rating at most once after redelivery
- No solo original, branch or ghost challenge changes ranked Elo
- Pairwise 2/3/4 participant formula and ties/forfeit consistent
- Replay share token does not authorize branch; revoked/expired denied
- Spectator cannot submit input even by calling hub API manually
- Chat flood/XSS attempt bounded and escaped; cache rebuild exact

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for multi-way rating and idempotency/privacy; economical for chat UI logic.
