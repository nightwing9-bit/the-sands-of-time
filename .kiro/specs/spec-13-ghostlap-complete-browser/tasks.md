# Spec 13 implementation tasks — Ghost Lap V2: complete PixiJS browser game, mobile and journeys

**Execute tasks in listed order.** Dependencies: spec-04, spec-06, spec-10, spec-11, spec-12. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **13.1 Complete React routes and real service state**
  - **Precondition/input & inspect:** typed contract clients, spec-10/11/12 services, shared UI package. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** all ten real navigation pages, error/empty/loading and settings.
  - **Forbidden changes:** public mock buttons, inaccessible branch results. Do not change frozen shared public contracts without ADR.
  - **Verification:** Playwright each route and core action coverage.
  - **Done when:** all screens connected and correct.

- [ ] **13.2 Implement Pixi scene and semantic renderer**
  - **Precondition/input & inspect:** GameSnapshot, GameRules, view model and asset IDs. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** whole 16:9, walls/candles/ghost/current layers, charge/Burst cues.
  - **Forbidden changes:** game scoring or collision computed in renderer. Do not change frozen shared public contracts without ADR.
  - **Verification:** viewport and actual snapshot comparison tests.
  - **Done when:** scene matches server, ghost inert.

- [ ] **13.3 Implement keyboard/touch and reconnect**
  - **Precondition/input & inspect:** hub input contract, auth session and UI control mapper. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** WASD/arrows/Space, mobile joystick/Burst, cancel/blur, snapshot recovery.
  - **Forbidden changes:** sending trusted position/score or stuck input. Do not change frozen shared public contracts without ADR.
  - **Verification:** Playwright mobile and jitter/refresh/forfeit tests; aborted/unrated result and server-unavailable replay show truthful state.
  - **Done when:** remote players see correct server results.

- [ ] **13.4 Wire actual solo timeline/comparison**
  - **Precondition/input & inspect:** spec-11 verified history API, timeline-ui. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** history/seek/rewrite/compare routes, aligned curves and stable ghost cues.
  - **Forbidden changes:** copied scrubber or invented score curves. Do not change frozen shared public contracts without ADR.
  - **Verification:** refresh/replay T0/T1140/Tfinal and source-hash checks.
  - **Done when:** full solo hero path clickable.

- [ ] **13.5 Social/rank/spectate and audio UX**
  - **Precondition/input & inspect:** spec-12 rating/chat/share, event cue registry. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** invite/chat/emotes/watch/profile/rank/rematch and mandatory semantic visual cues/reduced motion/accessibility. Phase 1 audio is optional polish: simple gesture-unlocked sounds and mute only if cheap and safe for core acceptance; no required sound engine, soundtrack, full audio routing or extensive settings UI. Omit nonfunctional volume controls when audio is absent; richer sound design is deferred to Phase 2.
  - **Forbidden changes:** sound as event source, public private histories. Do not change frozen shared public contracts without ADR.
  - **Verification:** multi-context Playwright, share-token no-referrer/no-store/log checks and permission tests. Core journeys work without audio or muted, with critical states visually legible and reduced-motion/accessibility intact; if optional audio exists, verify user-gesture startup and mute.
  - **Done when:** full social workflow connected.

- [ ] **13.6 Browser/mobile/performance gate**
  - **Precondition/input & inspect:** existing `design-inputs/skills2 (1).md` on-demand, fixtures, browser capture script; server/replay authority is frozen. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** trace-backed device tests, manual human fairness records, asset manifest fallback. Use actual configured API, Realtime and Worker product registrations/runtime grants, not only fixture hosts; retain a bounded CI command for domain integration/browser suites. Record C4's cross-app browser proof with Spec 09's finance journey and Ghost Lap journeys together; this consumes 09 evidence only at C4, without blocking earlier Ghost Lap UI tasks. The separately pending B2 joint gate remains **PENDING** until performed and is not satisfied by C4. Public HTTPS/judge access stays in its existing later deployment/submission gate.
  - **Forbidden changes:** claims of achieved tick/FPS without measurement. Do not change frozen shared public contracts without ADR.
  - **Verification:** real-time 90s run, mobile layout, source/branch E2E; no offline client-authoritative game or hardcoded demo numbers.
  - **Done when:** entire app usable without final commissioned art.

## Spec exit verification (blocking)

A real browser can complete multi-client invite→90s match→spectate→results/rematch and solo create→refresh→challenge→rewind→branch→compare, with phone controls, readable entire arena, authority and privacy tests.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
