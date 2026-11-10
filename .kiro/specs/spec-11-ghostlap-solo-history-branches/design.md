# Spec 11 design — Ghost Lap V2: persistent solo histories, exact ghosts and branching

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-02, spec-03, spec-06. **This spec owns:** `src/GhostLap` solo application/persistence/catalog (consumes Spec-06 codec), `ghostlap` schema, `web/apps/ghost-lap` domain adapters, `tests/GhostLap/Replay`. **Do not implement:** No multiplayer replay archives, AI/simulated opponent, branchable MP sessions, ghost interaction with present world.

**Consumes:** Public contracts from spec-02, spec-03, spec-06; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Durable storage and canonical replay

Tables in `ghostlap`: `solo_history` (original vs child attempt type, owner, source/root/parent/fork IDs, seed/rules/layout/character/spawn, state hash, completion, visibility, pin), `solo_score_sample` verified time-aligned score trace (derived from reducer, not sprite interpolation), `solo_save_idempotency`, plus reference to the **sole authoritative** `marrow_timeline` manifest ID **by scalar identity only, not cross-schema FK**. Canonical per-tick input bytes and complete checkpoints live **only in the Marrow Timeline journal/checkpoint tables**, through the **single Spec-06-owned canonical GhostLap input/state codec/adapter**; no second copy in the game schema. The original/source records in Marrow are append-only; own metadata can be append-only with explicit pin/visibility action records as permitted. Originals are retained by default across guest reauth; if storage becomes too large, quota/retention is an ADR/user decision **not a hidden silent expiration**. Child branches store prefix ref and only suffix inputs/checkpoints; never clone and rewrite original.

Full source checkpoints **as implemented by Spec 06** include position/velocity, charge/Overlit deadline, pair cooldown, score component counters **and fixed-point distance remainder**, last accepted/effective input/sequence and Burst edge where needed, collected/active/scheduled candle identity, layout/seed/rules, pending auto-Burst and any domain RNG state. Restore source at T using nearest checkpoint then exact reducer fold; verify hash at T even for empty ticks. History manifest refers to pinned `ghost-v1` reducer and deterministic codec; if an old reducer/codec is removed, return incompatible and allow metadata view but not fake rewind. Branch start inherits original T state; after fork new inputs only at T+1 onward. Ghost path and old score always from original verified source, unaffected by current-world candles or Burst. Time-aligned score points are canonical integer ticks; source past score at T used, not source final score.

### Verified source → catalog finalization (no distributed coordinator)

Spec 06 has already saved and marked the owner-tagged 2700-tick Marrow source `Verified`. Spec 11 does **not** write another Timeline log or recreate the codec: it checks the verified manifest, actor and hashes through the public Timeline port, then commits the `ghostlap.solo_history` catalog link plus save idempotency record in **one GhostLap-schema PostgreSQL transaction** with uniqueness on `(ownerId,timelineId)` and the operation key. Only after that commit does the API return “History saved.” If the catalog transaction fails, the source remains intact and discoverable by its ID; retry safely links the same verified source, does not mint a new original, and never declares success prematurely. A verified-but-unlisted source can be recovered on a retry/owner-owned recovery query without a background reconciliation daemon. For children, use the same catalog-link semantics after verified completion. Test failure injection at verification, catalog commit and acknowledgement loss. No cross-schema FK and no two-phase/distributed transaction coordinator.

### Solo lifecycle

A nonexisting history shows `Create Your First History`. Only a **full newly played** solo run can become a new original. The verified completion save responds with owner-ID tied receipt; after refresh guest can challenge it infinitely. Challenge tick0 starts same location, overlap preserved; a separate ghost layer renders read-only old path. Branch is permitted from completed source while viewer paused; **during active solo attempt** the initial UI policy is to pause and create a *new child* preserving running attempt separately; no mutating source or ambiguous rewind in-place. Final tick seek allowed, branch disabled. At finish child is saved as attempt with `type=Branch` and compared to source; attempts are visible but are not automatically new eligible original sources. Subsequent forks from children require explicit supported source chain, initially use original only for simplicity (not a scope loss: multiple branches from original supported).

### Exact source branch proof

Generate a golden deterministic 2700-tick run with nonzero charge/score and collected candle at T=1140. `Restore(T)` matches original state hash including collection/cooldowns; two branches diverge after 1140 but both keep original hash unchanged, and future candle opportunity IDs and tick positions are identical even if consumption differs. Assert source ghost may cross a living candle and living collector still gets it once. Include T=0, T=2699, T=2700 (inspect-only), source unavailable, version mismatch, save retry, illegal owner and repeated challenge. Real Postgres reload tested. Use same `Marrow.Timeline` source and `TimelineScrubber` frontend component, no new local replay engine.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Exact branch-prefix equality through T and old source unchanged after siblings
- Full state at T (including charge/Overlit/cooldowns/collection) hash equals historical
- Original 2700-tick verified Timeline source is linked once to catalog despite retries; failed catalog commit stays unlisted until idempotent retry, no false “saved” receipt
- Current and ghost world's candle consumption independent
- Historical score at tick T is read from source trace, not final
- T=2700 branch denied but seek permitted; missing reducer fails closed
- Guest cannot read/branch private foreign history

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for durable replay correctness and privacy; economical for history listing component.
