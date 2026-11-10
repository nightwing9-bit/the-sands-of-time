# Spec 13 — Ghost Lap V2: complete PixiJS browser game, mobile and journeys

**Basket:** 4 | **Dependencies:** spec-04, spec-06, spec-10, spec-11, spec-12 | **Owner paths:** `web/apps/ghost-lap` React routes/PixiJS scene/input/audio/UI and `tests/E2E` Playwright browser journeys

## User value and outcome

Complete connected functional Ghost Lap desktop/mobile product with all requested user routes and authoritative game state, independent ghost visuals, proper input/network/error handling and measured browser evidence.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No final bespoke art pass required in Phase 1, no multiplayer ghost mode, no AI opponent, no renderer-owned game rules.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R13.01 — All screens.** WHEN user navigates product, THE SYSTEM SHALL provide functional Start, Lobby, Live Arena, Results, Ghost History, Ghost Challenge, Replay/Timeline, Branch Comparison, Leaderboard/Profile and Watch/Spectator.
**R13.02 — Whole arena.** WHEN browser changes size/orientation, THE SYSTEM SHALL keep authored 16:9 whole-world geometry visible without distortion, use landscape request/letterboxing on mobile.
**R13.03 — Real controls.** WHEN desktop keyboard or mobile thumbstick/Burst is used, THE SYSTEM SHALL send normalized bounded input to server and reflect authoritative events, avoid client score/physics authority.
**R13.04 — Pixi layers.** WHEN source ghost and current player overlap, THE SYSTEM SHALL render ghost on separate translucent historic layer with no current-world hit/pickup semantics and keep living player legible.
**R13.05 — Real playback.** WHEN user selects original/replay tick, THE SYSTEM SHALL use the common Timeline scrubber for verified history seek and actual branch creation, not a cosmetic rewind.
**R13.06 — Network behavior.** WHEN network jitters, reconnects or browser refresh, THE SYSTEM SHALL reconcile locally predicted input if used, interpolate remote visuals, show disconnect/forfeit/retry states without phantom scores.
**R13.07 — Semantic effects.** WHEN server emits candle/charge/Overlit/Burst events, THE SYSTEM SHALL trigger replaceable audio/VFX cues without altering server state; honor mute/reduced-motion.
**R13.08 — UX completion.** WHEN list is empty, history incompatible, link denied or save fails, THE SYSTEM SHALL provide intentional loading/error/empty states and working navigation to recovery.
**R13.09 — E2E credibility.** WHEN real browsers execute demo journey, THE SYSTEM SHALL verify create original→refresh→challenge→fork→compare plus actual multi-client room/spectator/rating and mobile controls.
**R13.10 — Visual assets.** WHEN provided `design-inputs/skills2 (1).md` is available, THE SYSTEM SHALL use its compatible visual guidance with server authority unchanged; when genuinely missing, keep working semantic placeholders without fabricating content.
**R13.11 — Trustworthy disconnect/share states.** WHEN the host cannot serve a saved ghost or a multiplayer result is aborted/unrated, THE SYSTEM SHALL present a truthful unavailable/aborted status and a rematch/retry path, not silently implement offline client authority or display a phantom rating.
**R13.12 — Secure read-only share route.** WHEN a permitted solo replay token is opened, THE SYSTEM SHALL honor server share policy without leaking the token through browser referrers, third-party subresources, caching or errors.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

A real browser can complete multi-client invite→90s match→spectate→results/rematch and solo create→refresh→challenge→rewind→branch→compare, with phone controls, readable entire arena, authority and privacy tests.
