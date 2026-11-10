# Architectural decision register — Phase 1

| ID | Decision, reason | Alternative rejected / invalidation trigger |
|---|---|---|
| ADR-P1-01 | 13 specs / four baskets / early real two-adapter slice | App-complete serial order hides reuse bugs; invalidate if actual repo already has proven shared engine |
| ADR-P1-02 | One modular monolith, API/Realtime/Worker compositions, one Realtime replica | Microservices and concurrent room ownership; reconsider only with measured load and fencing plan |
| ADR-P1-03 | Immutable position-indexed journal, complete snapshots, canonical state+record hashes | Event-time-only ordering, pos-only game snapshots; invalidate on proven canonical version/migration issue |
| ADR-P1-04 | Input/upstream-fact replay in finance; decisions are derived | Replay original settlement decisions into v8 and call it counterfactual |
| ADR-P1-05 | Branch suffix refs immutable source prefix; one engine in both adapters | Copy source and rewrite; duplicated per-app forks |
| ADR-P1-06 | Atomic Postgres ledger posting, deferrable DB balance trigger; integer minor units | Application-only balance check, floats, cross-currency implied conversion |
| ADR-P1-07 | Version-pinned ML artifacts; precompute/store measured explanations and replay-relevant inference evidence | Nonreproducible live training or claims of local explanations from global importance |
| ADR-P1-08 | Fixed-point game world, precomputed schedule, stable tick phase order | Frame-dependent float scoring, input-dependent RNG schedule |
| ADR-P1-09 | Burst v1 initially ignores wall occlusion, no hit bonus, fixed hit penalty; tune by new rules version | Complex raycast/LOS without playtest; revise only with readability/abuse evidence |
| ADR-P1-10 | 2P opposite sockets; 3P authored triangle; 4P fair four sockets | Arbitrary 3 of four corners; fairness measurements may force updated layout version |
| ADR-P1-11 | Single-room loop authority, user rejoin grace 450 ticks, neutral input while disconnected | Redis backplane interpreted as owner-election; revisit on measured deployment requirements |
| ADR-P1-12 | Multiplayer rating pairwise-Elo average K=24 preliminary, only live eligible matches | 1v1 Elo naively applied to 4P; change after abuse/competitive fairness tests |
| ADR-P1-13 | Original solo history durable by default; branch attempts durable, private by default; revocable token shares | Silent expiry and public enumeration; quota decisions require actual storage measurement |
| ADR-P1-14 | Shared scrubber only, not shared visual design | One skin for unrelated bank/game UI; frontend guidance files loaded narrowly |
| ADR-P1-15 | Context7 early, Chrome after browser exists, shell hooks only on targeted gate | Repeated large agent hooks and unverified installations |
| ADR-P1-16 | Credits budgeted across baskets with reserve, then recalibrated | Unsupported claim of fixed credits per task |

These are **planning decisions**, not evidence of executed implementation. Changes that alter shared contract/replay/ledger/security authority need an actual ADR in `docs/adr/`, impact evaluation, and tests.
