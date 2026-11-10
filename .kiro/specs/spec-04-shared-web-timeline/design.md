# Spec 04 design — Typed web integration and common Timeline scrubber

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-02, spec-03. **This spec owns:** `web/packages/contracts`, `web/packages/timeline-ui`, Vite app shells, `tests/E2E` baseline. **Do not implement:** No substantive finance/game visualization, asset design reconstruction, score generation in React, final styling.

**Consumes:** Public contracts from spec-02, spec-03; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

Build `TimelineScrubber` as a controlled component consuming `TimelineAdapter` defined in `docs/architecture/03-contracts.md`. It owns seek positioning, keyboard stepping, drag, play/pause timer and confirmed-state status. A seek request carries latest-operation ID, cancels obsolete in-flight requests; only newest verified response may paint current state. UI position is a decimal-string that parses with `BigInt` for comparisons and small-step increments. `getLabel` comes from domain adapter (`00:42/01:30` for game or business date/time for bank). Domains supply `renderPreview(state)`, `renderComparison`, event markers, branch options, and source/branch authority. Scrubber only provides buttons and interactions; **never** calls reducers or generates fake metrics. Domain API validates all positions and permissions regardless of disabled UI.

Shared typed clients: OpenAPI -> TS REST DTOs with deterministic regeneration; handwritten but schema-checked discriminated typed SignalR messages where OpenAPI is insufficient. Decouple transport envelope sequence from game tick and finance event position. A `/meta/contracts` version handshake (or equivalent) reports host contract versions to app shells; incompatible major versions block operations with intelligible message. Map all Spec-02 `TimelineErrorCode` variants to typed browser errors, including incompatible version, corrupt history, conflict, forbidden and cancellation; browser UI never treats a failed verification as an available branch. Use TanStack Query for fetching where appropriate; no giant global Zustand store. Compose app shells with distinct routes and neutral design tokens; specific production layout belongs 09/13. Playwright tests mount **two fake domain adapters only at component level**; integration E2E calls actual Timeline test endpoints from spec-02, including slow/forbidden responses. Do not create pretend user-facing branch success.

The two original design inputs already reside at `design-inputs/skills1 (1).md` and `design-inputs/skills2 (1).md`; only Specs 09/13 need their full contents. Do not load them in this shared-core spec or allow design instructions to override server authority. Chrome DevTools MCP may be enabled *after* this spec has a bootable browser and Node/Chrome prerequisites have been verified.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Seek cancellation: slow prior response must never overwrite newest preview
- Decimal positions >2^53 retain exact precision
- Unauthorized server preview cannot be bypassed by UI controls
- Both apps import sole compiled package; no second scrubber copy
- Keyboard/touch/focus/reduced-motion Playwright checks

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Economical for component behavior; strong for shared adapter/version semantics.
