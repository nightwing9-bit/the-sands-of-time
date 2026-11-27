# Spec 11 design — Small integration, exact Marrow history

**Implementation status: PLANNED.** This document describes required behaviors and preferred composition, **not claims about methods already exported by the repository**. The very first task inspects the actual Spec 02/03/06 contracts and records any necessary adaptation. Favor the simplest proven path over new infrastructure. `ghost-v1` solo remains frozen; Spec 10 multiplayer's new default is `ghost-mp-v2` and is *not* used to decode solo history.

## 0. Engineering strategy and boundaries

The architecture is deliberately two-layered:

1. **Canonical history and physics:** existing Spec 06 `ghost-v1` deterministic reducer, state/input codec, checkpoint/replay/branch support and **frozen Marrow Timeline**. One authoritative state interpretation.
2. **Ghost Lap product adapter:** owner authentication, a minimal record/save/catalog integration where existing Spec 06 services do not already suffice, bounded verified trace/score reads and typed challenge/fork operations. Existing API/Realtime hosts; no new independent infrastructure.

No duplicated solo reducer in TypeScript, no clone of canonical per-tick journal, no multiplayer archive, no live solo process-generation fencing, no background repair daemon, no distributed two-phase commit and no new rating/share/messaging service. Browser visuals (sprites, Pixi filters, SFX, pause/rewind animation, smoothing) are intentionally separate; this spec supplies **verified frame/tick data and action semantics** to render them accurately.

### One-time port inventory — 11.0, timeboxed

Inspect source and tests (not design assumptions):

| Need | Actual evidence to find | Preferred handling |
|---|---|---|
| `ghost-v1` initial state, 30Hz reducer, complete codec and goldens | Spec 06 public services and deterministic fixture hashes | **Reuse unchanged**; verify no extra codec |
| Original creation/append/verification and final manifest | Spec 02/06 exported Timeline ports and persistence | Reuse existing transaction/record workflow; no invented method names |
| `Restore(source,T)`/checkpoint fold and source hash | Existing source/branch services and tests | Reuse; detect unsupported T or corrupt source honestly |
| Branch with exact parent prefix/suffix | Existing public branch port and metadata | Reuse; if absent, identify smallest *allowed* compatible adapter, do not fabricate a fork |
| Owner catalog/`SoloOriginals`/`SoloBranches` | Existing schema, migrations and resource authorization | Extend only missing metadata; no duplicate tables |
| Stable save retry identity | Existing operation/source ID or preassigned ID accepted by port | Match owner+stable save key to same canonical identity; ambiguous save stays pending rather than creating a second source |
| Web/host composition | Actual API/Realtime config, cookies, DI and roles | Reuse existing host and deny foreign history IDs |

**Stop only for an actual missing indispensable canonical restore/branch contract**; report exact API evidence and the minimal compatible decision. Do not block on optional mid-run resume, cross-process takeover or Redis because none is required. If a save retry mechanism is unsupported, preserve ambiguous status and identify the smallest Ghost-Lap-owned, port-compatible resolution rather than silently minting another history.

## 1. Minimal end-to-end data flow

```text
Authenticated solo player
    |
    +-- play real 2700-tick `ghost-v1` run (existing Spec 06 runtime)
    |      canonical input order + seed/rules/spawn + actual 30Hz transitions
    |
    +-- completed original -> existing Timeline writer/verify -> immutable source ID
    |      owner-bound operation key -> durable verified catalog link -> Saved
    |
    +-- list/inspect owned verified original
    |      Timeline checkpoints + frozen codec -> exact source states/events/scores
    |
    +-- Challenge at tick0 --------------------------+
    |                                                |
    +-- Fork source at selected T -> Restore+hash -> branch prefix, inputs T+1..
                                                     |
                                  new living world + read-only source ghost
                                                     |
                          verified child + source/current same-tick comparison
```

**Solo recording can use whichever route the actual Spec 06 implementation supports:** a bounded server-owned live session *or* capture of real 2700-tick accepted inputs with server-side canonical replay/verification before save. Both must employ the actual frozen reducer, not accept a client-reported path/score or create a second simulation with different rules. Keep the live control protocol as small as possible. A single Realtime host already exists; use it if needed, but **do not automatically import the Spec 10 multiplayer room/advisory-lock architecture into solo recording**.

### Active-attempt lifecycle — intentionally minimal

`Ready -> Playing -> Finishing/Verifying -> Saved` or `Failed/Pending`. An unfinished `Playing` attempt interrupted by shutdown, disconnect or navigation is `Interrupted`/discarded, with an explicit **Restart run** action; it is not a completed or corrupted original. There is no silent clock progression after disconnect. A current unfinished challenge/fork may be discarded after an explicit UI action; the saved original and previous verified children remain untouched. No mandatory crash-resume, competing reconnect controllers, append-by-append durable acknowledgement or live writer generation. If the existing Spec 06 session naturally supports pause/reconnect, it may be reused without making this a new obligation.

Keep **the completed-save boundary strict**: a failure at `Verify`/catalog linking cannot become a green `Saved` state. Treat uncertain commit/HTTP acknowledgement using owner+operation idempotency and an authoritative read of the existing verified source/catalog. Use bounded retries; on genuinely unresolvable ambiguity, return a typed `SavePending` error rather than creating another source. No progress-beyond-last-verified-tick claim.

## 2. Source ownership and data model (reuse first)

**Required data**, regardless of which existing schema owns it:

| Field | Purpose |
|---|---|
| owner principal | Private history/command authorization, never supplied as truth by client |
| stable save operation/key | Safe duplicate HTTP request/response handling |
| canonical Timeline source/branch ID | Exact replay source; scalar reference if cross-schema |
| kind: Original/Challenge/Fork | Avoid presenting a completed child as an original |
| original/root ID; parent ID; optional fork tick | Provenance and source selection |
| seed, solo rules/codec/layout, spawn/character | Exact historical interpretation and visual mapping |
| finalTick (2700), canonical hash and authoritative score components | Verified completion and score/trace comparison |
| state: Pending/Verified/Saved/Interrupted/Failed/Incompatible | Honest owner-visible lifecycle |

Prefer the implemented Spec 06 source/catalog or `SoloOriginals`/`SoloBranches`. Add **one small Ghost Lap-owned metadata/unique-key table only if a concrete field/ownership gap exists**, with a forward-only migration and real runtime grants. Avoid a second set of `solo_history`, `solo_score_sample`, `solo_recording_intent` tables simply because the earlier plan proposed them. No cross-schema FK or direct write into Marrow-owned tables.

**Saved iff:** `source manifest is verified` AND `owner-bound visible link is durably committed`. Stable operation-key replay returns that same link/hash. Conflicting request against a used key fails. A client-provided source ID can be used to locate, but never authorize, a history. Completed source metadata is never silently expired; if quota limits creation, refuse a new attempt rather than deleting saved originals.

**No outbox/Redis needed for solo:** verification and metadata creation are a small owner action, not a message-processing pipeline. Source snapshots, checkpoints and inputs reside where Timeline/Spec 06 already store them; optional precomputed trace may be an ephemeral cache only if measurement establishes need.

## 3. Canonical time, replay and fork semantics

`ghost-v1` initial world is **tick 0**, and each transition applies one canonical action for `tick = 1..2700`; `state(T)` means after T transitions. There must be a full 2700-tick run with legitimate gameplay and a verifiable hash.

1. **Inspect:** `T` is an integer `0..2700`. Read verified source with the pinned reducer, restore full state from checkpoint+fold, verify canonical hash. No position-only shortcuts.
2. **Challenge:** living starts in independently instantiated world at original `state(0)` and same seed/rules/layout/spawn. The original ghost is a read-only stream of source positions/charge/actions/events, never an entity in current collision/pickup/Burst logic. On completion, verify and link a new child tagged Challenge (conceptually a branch at tick0, using supported Timeline semantics).
3. **Fork:** selected **verified original** at `T` with `0<=T<2700`. Reconstruct complete source `state(T)` and check its hash, create a supported canonical child referencing exactly that prefix, accept first new living input at **T+1**. The source timeline continues unchanged and remains a visual ghost; the child's own future becomes independently recorded. T=2700 is inspect-only, not forkable. A child-of-child fork is intentionally out of scope.
4. **Separate worlds:** historical events are immutable read-only cue data. Current world's candle consumption, RNG/pending action state and charge come from the **current** canonical reducer only; never replay ghost pickup as a live-world mutation. Same pinned schedule and opportunity generation; independent consumption. An input ordering difference can alter scores but cannot rewrite the source.
5. **Children:** completing challenge/fork yields verified child provenance, canonical final hash and score. Keep distinct child IDs for repeated attempts. Parent/source hash is checked again on save where the port permits. Never overwrite the original.

**Full state:** all gameplay-significant fields actually used by the frozen reducer: position and resolved movement/remainders, score components, Soulfire, Overlit deadline/manual edge/auto action, deterministic candles (schedule, IDs, lifetimes and collected state), RNG stream state, previous effective input and any relevant pending sequence/cooldown fields. Avoid inventing fields absent from `ghost-v1`; the codec and golden hashes are authoritative. The same full-state rules apply to the solo 0–4 charge progression, fast candles, manual radial Burst and 240-tick Overlit auto-Burst. The ghost never acquires multiplayer contact behavior.

**Proof examples:** exact T=0, T=1140 (nontrivial movement remainder, candle consumption, charge/Overlit where reachable), T=2699, T=2700; two children forked at T=1140 with different suffixes, equal source prefix/hash, independent final hashes and unchanged source final hash. One source candle crossing with a living player confirms that no collection/charge event crosses between worlds. Include a complete played original with an actual manual/auto-Burst in the pinned solo profile where achievable.

## 4. Read models for a premium timeline UI (not visual implementation)

Build the **smallest typed public adapter** that an actual browser can consume; names must follow the existing project style, not fictional endpoints. Suggested conceptual operations (bind to real implementation):

| Operation | Result needed for the real UI |
|---|---|
| Start/finish/save original | run/session identity, sealed seed/rules/character, progress, verified final score and `Saved` vs `Pending/Failed` |
| List mine / get source | owned completed originals and children, correct original/child kind, source/fork/version/hash metadata |
| Get source trace / inspect T | source tick-indexed ghost position, Soulfire/expression triggers, relevant candle/Burst cues, **source score at each tick**, exact restored-state/hash identity |
| Start challenge(original) | independent living start at `0`, read-only source ghost identity, active attempt ID |
| Fork(original,T) | confirmed source+T, full restored state/provenance and new child/attempt identity; input begins T+1 |
| Compare(source,current) | same-tick living/source score components, signed deltas, available ticks and selected-source identity |
| Finish child | truthful final hash/score and linked original/fork metadata |

Read large traces with **one bounded 0..2700 tick projection** (possibly chunked if the existing transport requires it), not hundreds of independent DB calls per scrub movement. Verify/replay the source once per request/read session and sample from that canonical sequence; if performance data supports a small cache, key it by `(sourceId, rulesVersion, sourceHash)`, never let an unverified cache override canonical state. Do not add a materialized score table by default.

A frontend may interpolate positions between 30Hz ticks to draw at 60FPS+ and perform a reversible montage, but **scrub selection, branch point, charge, score and candle effects remain anchored to exact tick data**. Event IDs should be deterministic/stable within a verified source if the existing codec exposes them; use actual canonical event ordering rather than randomly-generated display events. The same Marrow Timeline UI concept/semantics should underpin Revenant's historical counterfactual and Ghost Lap's source/fork, without altering the shared UI component now.

**Typed outcomes:** `Saved`, `SavePending`, `Interrupted`, `SourceNotVerified`, `FinalTickInspectOnly`, `InvalidTick`, `VersionUnsupported`, `CorruptHistory`, `NotFoundOrForbidden` as appropriate; implement only codes needed by concrete UX/API and stay consistent with existing error envelopes. No silent fallbacks that invent a history.

## 5. Access, reliability and host composition

- Authenticate with server-derived verified guest principal and established guest cookie/session. Every catalog, source trace, restore and fork service rechecks ownership; no public replay token and no share-derived principal. An API identity boundary protects raw Timeline IDs even if a user guesses one.
- Use the established API/Realtime host route/DI/auth policy. Solo may run alongside MP but not use MP match receipts, ranking, `ghost-mp-v2` codec or Redis outbox. Keep Marrow and Revenant DI/policies and DB role privileges unchanged.
- Validate input tick count, size and rules version at ingestion; a true 90-second run cannot become a verified history solely through the client claiming a final hash. Keep canonical write/verification work outside the 30Hz MP reducer thread; no new scheduler lane unless measured blocking requires it.
- Bound active recording attempts and expensive trace/seek calls with modest existing host limits; expose typed transient errors and protect the server from unbounded payloads. Record actual 2700-tick source bytes, cold seek and trace projection timing. Do not invent performance savings or capacity claims.
- **No new partial-session crash recovery system:** process loss during unfinished play yields Interrupted/restart. A successfully completed verified source survives process restart and can be read later. If an ACK is lost after saved catalog commit, retry reads the same record. The actual ID correlation method must be evidenced; unresolvable states remain explicit and non-destructive.

## 6. Verification plan: strong where the product depends on it

| Gate | Fast Kiro implementation checks | Independent technical verification |
|---|---|---|
| Existing ports and baseline | One source/codec inspection, a representative pinned golden | Focused frozen regression; no sweeping unrelated test suite |
| Original and save | A full 2700-tick source verifies; save status correct | One real DB cold reload + repeated identical/conflicting save-key tests; one lost HTTP ACK path if supported |
| Ghost and seek | T=0,1140,2699,2700 full-state/hash, noninteractive ghost | Repeat deterministic source with nonzero gameplay state and source unchanged |
| Challenge/fork | Challenge0, two T1140 children, suffix from T+1, pinned versions | Cold reload branch verify and sibling/source immutability |
| Read model | Signed same-tick scores and tick-accurate events | Bounded trace size/latency and foreign-owner denial |
| Host integration | Real production DI/routes and least-privilege policy check | One MP room + representative simultaneous solo record/seek smoke; **reuse** Spec10 full load proof |

Do **not** require lost-create ACK × every crash boundary × cross-process competing solo writers, three 4-room stress runs, Redis projections, extensive fairness sweeps or per-tick full persistence fault matrices. They do not improve the core verified-history promise relative to cost. **Do** treat corrupt canonical source, incorrect branch-prefix hash, history ownership leak or fake `Saved` as blocking.

**Spec 11 exit certificate:** real source ID, seed/rules/codec, canonical final hash, actual exact-restore hashes (redact principals), two child IDs/fork tick and changed suffixes, unchanged original hash, same-tick source/current score samples, cold-reload ownership and measured seek/trace time, focused commands/commit evidence. Keep test logs short and true. An unexecuted assertion is not a passing test.

## 7. Handoff boundaries

- **To Spec 12:** no dependency for solo history. Completed solo sources are private. `MatchFinalizedV1` / multiplayer rating policies have nothing to do with solo recording.
- **To later visual/client work:** expose verified history ticks, authored original-vs-current data, chronological effects and reliable typed errors. **Do not design sprite art, sound packs, animations or modify the user's asset direction here.** The final browser must separately verify precise seek and smooth playback with the actual Pixi renderer.
- **To Marrow and Revenant:** no changes. Only reuse public Timeline concepts/ports; the same exact-source/branch behavior is what the demonstration needs to make visible.
