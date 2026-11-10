# Spec 06 design — Ghost Lap V1: deterministic greybox and first solo ghost

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-02, spec-03, spec-04. **This spec owns:** `src/GhostLap`, `src/GhostLap.Contracts`, `web/apps/ghost-lap` greybox, `tests/GhostLap`. **Do not implement:** No network rooms with remote players yet (10), durable share UI (11/12), final art (13), AI adversaries, multiplayer history archives.

**Consumes:** Public contracts from spec-02, spec-03, spec-04; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### GameRules v1 (versioned defaults, not permanent balance claims)

Use `GameRules` hash `ghost-v1` storing 30Hz tick rate and 2700 tick round (initial targets), width:height 1600×900 integer world, standard equal collider/speed across **four selectable mascot IDs** (provisional placeholders distinguish identity), one authored wall layout `arena-v1` with several short obstacles, 2P opposite sockets, 3P authored balanced triangle, 4P four corners. All domain positions/velocity are deterministic fixed-point integer units (e.g. 1/1024 world unit). Movement directions quantized to cardinal or normalized diagonal by integer lookup; no OS-frame deltas/trig floats in authoritative simulation. Use wall-slide/stop deterministic tie-break. **No browser sprite movement earns score.** Movement points: explicit `floor(totalAuthoritativeDistanceFixed / configuredUnitFactor)`; initial testable coefficient, tune only with rules-version change. Candles: precompute schedule with stable IDs from seed/layout, activation ≤tick<expiry, a ~210-tick lifetime and average 8–12 simultaneously as initial tuning goal. Spawn telegraph precedes activation, stable reachable spawn points and no wall overlap; 2/3/4P opportunity distances tested. Pickup +25 points initial, charge min(4,charge+1); charge=4 starts ~240-tick Overlit deadline. While full pickup still scores but doesn't reset deadline. Contact when unequal loses 1 weaker charge, no score, once per pair per 45 ticks; losing charge from four cancels Overlit and future return to 4 re-arms. Burst radius initial 160 world units, no wall occlusion in `ghost-v1` (document clearly), target loses 2 charge and 90 points clamped at zero, **no attacker reward**. No knockouts, stuns, AI, bombs or combat in solo ghost.

**Input and score replay contract (`ghost-v1`):** The server accepts strictly newer per-slot sequence within its permitted tick window; a connected player's last accepted movement persists for no more than **six ticks** without update, then becomes neutral. Disconnect/blur neutralizes immediately. `requestBurst` is edge-triggered, never repeatedly re-fired on held input. Materialize the actual **effective** input at every server tick (including neutral/no-input ticks) in solo history. Canonical `GameState` and the single **Spec 06-owned** versioned input/state codec include last accepted sequence, held-direction expiry, Burst latch if needed, and cumulative post-collision fixed-point distance (or equivalent remainder) plus movement/pickup/penalty score components. Do not derive fractional distance from floored points. One checkpoint and uninterrupted run must earn the same next point at a unit boundary.

**Stable per-tick resolution:** (1) validate input/tick/seq and sort by owner slot+seq, apply latest permitted input; (2) deterministic movement/wall & distance-score; (3) expire/activate candles by ID; (4) pickups by candle ID and stable player slot; (5) eligible contact sorted pair of slots; (6) explicit Burst in slot order then due auto-Burst, at most one per player/tick; (7) finalize after all effects when terminal tick. Client `requestBurst` and expiry on same tick generate one Burst; if contact cancelled Overlit earlier in tick, that owner cannot burst. Score floor/clamp and fixed arithmetic tests. A solo Burst still triggers/consumes charge and visual event even without opponents; no fabricated game enemy.

### V1 game host and historical proof

`GhostLap.Domain` rule engine is pure, independent of API/Pixi. For V1, a single C# server-driven solo session endpoint/SignalR stream orchestrates ticks; it never trusts browser positions. Persist solo histories through the Marrow journal with guest owner and full authoritative checkpoints (initial cadence 60 ticks, adjust after measuring). A history original is complete only after 2700 ticks, verified hashes, and successful DB commit; fail/retry visible. Source trace is either verified sampled state or deterministic replay of past inputs and provides own position/score/Burst cues. For the challenge, a parallel *read-only* source projection and current mutable world use the same **pinned** candle schedule but distinct consumed-ID sets. At tick0 overlap exactly; after fork at T current resumes T+1, ghost remains source. Final tick is inspect-only fork disabled. No persistent multiplayer replay requirement.

Basic PixiJS placeholder renderer can be implemented with circles and rectangles, while semantics stay in C# and React. One side-by-side score delta from actual backend state, keyboard/Space input. Spec-13 owns complete mobile/visual/browser polish; 06 must still have a genuinely playable greybox. Reuse the exact `web/packages/timeline-ui` from 04.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Identical seed/inputs gives identical final state hash across two replays
- At T with charge>0, cooldown active, consumed candle: restore all equals source
- Same candle ID collectible once per world; ghost collection cannot consume current candle
- Same-tick manual+expiry Burst exactly once; status 0≤charge≤4
- Input spoof/teleport claim has no authoritative score effect
- Candle schedule independent from branch pickups and same tick deterministic priority
- Spec 06 local proof: canonical codec save/restore, independent ghost and genuine greybox user flow; joint shared engine+scrubber proof happens only at B2 after 05+06

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for deterministic world rules and branch fidelity; economical for placeholder renderer.
