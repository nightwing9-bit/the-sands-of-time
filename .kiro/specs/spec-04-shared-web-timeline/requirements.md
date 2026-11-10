# Spec 04 — Typed web integration and common Timeline scrubber

**Basket:** 1 | **Dependencies:** spec-02, spec-03 | **Owner paths:** `web/packages/contracts`, `web/packages/timeline-ui`, Vite app shells, `tests/E2E` baseline

## User value and outcome

One accessible functional React Timeline control with two domain adapters on real typed HTTP/SignalR contracts, ready to mount real finance and game reducers.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No substantive finance/game visualization, asset design reconstruction, score generation in React, final styling.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R04.01 — Single package.** WHEN either app displays a Timeline, THE SYSTEM SHALL import the same `web/packages/timeline-ui` component package, never clone it.
**R04.02 — Seek correctness.** WHEN a user drags/keyboard seeks to logical position, THE SYSTEM SHALL request actual versioned server preview and render confirmed position, not invent local state.
**R04.03 — Fork safety.** WHEN selected position is invalid, end tick, read-only or pending, THE SYSTEM SHALL disable action and display grounded reason rather than issue mutation.
**R04.04 — Transport recovery.** WHEN a REST/hub request is stale, cancelled, denied or disconnected, THE SYSTEM SHALL show explicit loading/cancelled/permission/offline state and recover after snapshot fetch.
**R04.05 — Big position precision.** WHEN a long logical event offset exceeds JavaScript safe integer, THE SYSTEM SHALL round-trip it using a decimal string without float precision loss.
**R04.06 — Domain adapter independence.** WHEN a tick or event-time adapter mounts, THE SYSTEM SHALL use its labels/markers/branch options and never import the other app DTOs.
**R04.07 — Accessibility.** WHEN keyboard and touch/assistive tech navigate Timeline, THE SYSTEM SHALL provide focus, arrow step, play/pause and reduced-motion-safe feedback.
**R04.08 — Contract freshness.** WHEN OpenAPI schema/Hub DTO changes, THE SYSTEM SHALL regenerate and type-check once, with CI detecting stale client output.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

Two live browser shells import one working Timeline scrubber and typed clients; controlled seek/fork/error behavior observed against backend; actual graphics/domain overlays deferred.
