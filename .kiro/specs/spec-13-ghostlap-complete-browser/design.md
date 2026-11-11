# Spec 13 design — Ghost Lap V2: complete PixiJS browser game, mobile and journeys

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-04, spec-06, spec-10, spec-11, spec-12. **This spec owns:** `web/apps/ghost-lap` React routes/PixiJS scene/input/audio/UI and `tests/E2E` Playwright browser journeys. **Do not implement:** No final bespoke art pass required in Phase 1, no multiplayer ghost mode, no AI opponent, no renderer-owned game rules.

**Consumes:** Public contracts from spec-04, spec-06, spec-10, spec-11, spec-12; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Full application routes and state

Define `Start/Menu`, `Lobby/:code`, `Match/:id`, `Results/:id`, `History`, `Ghost/:sourceId`, `Replay/:sourceId`, `Compare/:branchId`, `Leaderboard/Profile`, `Watch/:roomId` and permissioned `Watch/replay/:shareToken` (token entry receives `no-referrer`/`no-store`, no third-party subresources until token is removed/redeemed, and server/proxy logs redact token). The game screen view model must be built from **typed authoritative** `GameSnapshot` plus semantic events; remove any local-only mock mode from public routes. **No offline self-authoritative Ghost/Timeline engine**: when server replay/authority is unavailable, show an intentional retry/unavailable state rather than pretending to create verified history locally. A missing durable multiplayer result after process loss is `Aborted/Unrated`, never a fabricated winner/rating. Show four selectable head-like mascot identities using distinct placeholder heads/eyes/labels; all have equal hitboxes/movement. Expose basic how-to-play, readable controls and reduced-animation settings; show working mute only if optional audio is present, and omit nonfunctional volume settings when absent. At least 2–4 live browser clients must actually join same room; no fake players. Event receipts and actual save/reconnect status determine toast content.

### PixiJS and React boundary

`PixiArenaScene`: authored 16:9 world under fixed camera; background/walls, candle telegraph/active layers, read-only historical ghost trail, living mascots, effects, debug overlay. React draws HUD, buttons, lobby/history and Timeline/score charts. Renderer only consumes authoritative positions/candles/scores/rules events; use interpolation for remote clients, optionally local input prediction **visual-only** followed by correction with sequence acknowledgment. No Pixi loop can mutate domain charge/score or create candles. Load an asset manifest with `CharacterAppearanceId`, candle, wall, effect IDs; fallback simple geometric art if file unavailable. Need working desktop keyboard (WASD/arrows + Space) and mobile landscape left thumbstick/right large Burst, pointer cancel/blur releases inputs to neutral to avoid stuck movement. Other mascots show 0–4 charge halo; Overlit telegraph readily legible; a ghost tints the same source mascot and stays behind living overlap.

### Accessibility, network, sound and mobile

Responsive letterbox to world without stretching colliders; landscape overlay for portrait with accessible continue explanation and room join possible. Never hide timer, current/past score, Soulfire status or mobile controls under chat. Critical server semantic events require readable visual cues; reduced-motion and applicable accessibility remain mandatory. Phase 1 audio is optional polish: simple user-gesture-unlocked sounds and mute may be included only if cheap and without jeopardizing core acceptance. A sound engine, soundtrack production, full audio routing and extensive settings UI are not required; richer sound design is deferred to Phase 2. Core journeys work with audio absent or muted, and reduced motion never hides material status (timer/charge/Burst readiness). If audio is present, verify user-gesture initialization and mute. Keyboard navigation/focus covers menus, history, branch dialogs; ARIA labels and visible focus. Error handling includes empty history, expired share, incompatible rules, failed save, reconnect during lobby/round, after-forfeit read-only state, browser network gap recovery, unauthorized branch, final tick branch disabled and mobile viewport loss. Client must not use requestAnimationFrame delta to compute score.

### Browser evidence gates

Playwright multi-context tests: real two/four guest sessions create invitation, ready/start, collect, Burst, third spectator denied submitting command, finish/result/rating/rematch. Solo: create first 90s history, save+reload, choose original, source/current start overlap, rewrite tick T with nonzero charge/consumed candle, compare tick-aligned score and source hash. Use controlled accelerated test clock only **as a test-specific deterministic server harness**, never make production client locally skip ticks; then smoke real-time duration separately. Test desktop keyboard, mobile touch and 16:9 scaling, reduced motion and audio absent/muted (gesture initialization/mute if audio exists), reconnect, network errors, Aborted/Unrated match result, server-unavailable ghost and token-route Referer/cache/log protections. Measure frame times/phone FPS/payload/30Hz loop actuals on named devices; **30Hz target not automatically achieved**. Give an explicit manual human game-feel gate: 2/3/4P fairness, solo fun despite no opponent, camping exploit, first-five-seconds clarity, scoring mix; record findings and revision proposal instead of quietly changing rule constants.

### Design inputs

The original visual design input **exists** at `design-inputs/skills2 (1).md`; read it only during Ghost Lap frontend tasks and apply its compatible arena/HUD/typography guidance. Its offline playable ghost sentence **does not** override server-authoritative history/branching—show disconnected/unavailable instead. Its illustrative `1,420 → 1,578 (+158)` values must come from actual computed histories, not hardcoded. An integrity seal compares the original source's hash before/after; a divergent child hash is **not expected to equal** the source. If the file is genuinely unavailable in a checkout, keep functional placeholders; final haunted slate/stone art/audio belongs later refinement backlog and is not invented here.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Both Ghost modes and all listed screens have real API-backed actions
- 2/3/4 real browser clients see one authoritative state and consistent results
- Ghost underlayer does not change living score/pickups and source trace exact
- Mobile pointer cancel/keyboard blur results neutral input and no phantom movement
- Reduced motion, audio absent/muted, portrait and tiny viewport preserve core information; optional audio verifies user-gesture initialization and mute
- Replay source persists through refresh, fork T works, final T disabled
- Record actual FPS/latency/tick health rather than assert targets as achieved

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong review for integration/security and precise replay UI; economical for scenes/routes after contracts freeze.
