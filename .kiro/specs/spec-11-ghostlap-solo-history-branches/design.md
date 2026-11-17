# Spec 11 design — Exact solo history with crash-honest recording and branch integrity

## 0. Boundary, prerequisites and nonclaims

This spec owns the **Ghost Lap integration** around already frozen Marrow Timeline and the Spec 06 canonical solo codec/reducer. It does not own Timeline journal schema/append algorithms, `web/packages/timeline-ui` semantics, shared Marrow contracts, or Revenant. It is a **planned** specification. Before coding inspect actual exported Timeline/Spec-06 contracts, manifest/append/verification lifecycle, version/hash fixtures, `SoloOriginals`/`SoloBranches` and `solo_originals`/`solo_branches` implementations, host DI, identity, DB roles and `docs/architecture/{01-master,02-spec-map,03-contracts}.md`. Never infer an exported method exists because the design names a conceptual operation.

**Critical preflight decision record:** can the existing frozen ports (a) recover one exact created/verified source by durable owner+operation identity across a lost response, and (b) enforce exclusive monotonic canonical append/restore after restart? Produce an evidence-backed matrix with concrete API/schema/tests. If either is absent, **STOP dependent tasks** and document a compatible Ghost-Lap-owned approach using existing ports for explicit approval; never invent or change a Marrow API and never downgrade exactness. The architecture below specifies invariants and candidate private strategies, not unverifiable claims about current V1 capability.

## 1. Authority and ownership graph

```
remembered guest principal --authenticated--> GhostLap solo application
     |                                           |
     |                              durable ghostlap recording intent
     |                                           |
     |                           single long-lived solo-attempt owner
     |                           (existing Realtime hosted service)
     |                                           |
     +--------------------------------> Spec-06 canonical solo adapter
                                                 |
                                       frozen Timeline public port
                              (sole canonical input/checkpoint/history store)

GhostLap solo_history/catalog --> scalar verified Timeline identity
GhostLap solo_score_sample  --> verified *derived* tick score projection
Read-only ghost/Timeline UI --> verified original source, never living world
```

The **existing Realtime host**, run as one guarded authority process per Spec 10 deployment when present, is the initial preferred execution home for production live solo original/challenge/branch loops; solo uses an **independent `ISoloAttemptOwner` registry/command lane**, not MP room reducer/lobby. Hubs are transient; request handlers cannot instantiate new authoritative loops. The solo implementation must remain independently usable if Spec 10 lobby code is not in use, relying on Spec 03/06 host contracts.

Exactly one controlling connection for an active attempt; authenticated reconnect revokes old controller epoch. Critical distinction: **one registry in one process is not enough** if an old authority can survive guard loss and continue appending. Every canonical suffix append must be guarded by a persistence-enforced attempt owner/expected-version/next-tick contract validated **at the actual write**, not only during session creation. If the existing Timeline append method provides expected version/tick and owner fencing, use it unchanged. Otherwise examine a compatible GhostLap-owned per-attempt exclusive write gate using existing transaction/append boundaries. A separate ghostlap generation read *before* an unguarded Timeline append is TOCTOU and NOT an acceptable solution; if atomic/serialized protection cannot be demonstrated without modifying frozen Marrow, block the promise of resumable live writing instead of fabricating it.

## 2. Source identities and two-stage persistence

**Storage ownership** (exact private names can follow actual schema conventions):

- `ghostlap.solo_recording_intent`: owner ID + high-entropy stable `operationKey` unique per owner; attempt ID, original/challenge/fork kind, sealed source selection/fork T if any, selected seed/rules/layout/codec, lifecycle (`Preparing`,`Recording`,`Paused`,`Verifying`,`VerifiedUnlisted`,`Saved`,`Failed`,`Incompatible`), `timelineId?`, authorityGeneration, controllerEpoch, timestamps and **non-authoritative** progress hint. A conflicting payload on the same operation key is a conflict. Unique `timelineId` once populated. This record is **not** a second log or source of canonical inputs.
- `ghostlap.solo_history`: unique `(ownerId,timelineId)`, unique owner+operationKey catalog identity, original versus attempt, root/source/parent/fork metadata, owner, seed/rules/layout/codec/spawn, verified canonical hash, final tick/score, pinned/visibility, completion and status; **scalar** Timeline ID only, no cross-schema FK.
- `ghostlap.solo_score_sample`: immutable or regenerable canonical tick-score projection keyed by verified timeline/version/hash; generated only by authenticated canonical reconstruction. On projection disagreement, verify/rebuild/fail closed; it is not authoritative gameplay state.
- Reuse existing Spec 06 `solo_originals`/`solo_branches` verified metadata and Timeline sources rather than conflicting duplicates, and do not introduce a second encoder or an additional clone of canonical input/checkpoint bytes in GhostLap schema.

### The correlation invariant — must hold across every crash boundary

Before a source may be created/verified, establish one durable relation:

`(verified owner, stable operationKey) -> exactly one Timeline source identity`

and prove it remains **recoverable after crash even if the GhostLap catalog and its idempotency transaction never committed**. Candidate compatible mechanisms **only if supported by real frozen ports**:

1. Existing Spec 06/Timeline manifest has stable owner/attempt-key metadata and a concrete lookup returning exactly one source; or
2. Existing creation API accepts a preallocated stable Timeline identity recorded in the committed GhostLap intent **before** creation, and duplicate creation is idempotent against that identity; or
3. Existing verified-source API already exposes a durable unique, owner-bound attempt ID whose recovery lookup survives lost acknowledgement.

Merely recording `timelineId` after `Create` returns is **insufficient** if creation can commit and its response can be lost before the GhostLap update. A broad owner list is also insufficient for two pending sources. If none of the existing interfaces can satisfy this, **do not claim the recovery path works**. A new private adapter must be proven compatible with frozen ports, otherwise require an explicit scope/architecture decision before implementation. Never write directly to Marrow tables or retrofit its public API.

### Idempotent verified-source -> catalog handoff

1. Authenticate owner; resolve `operationKey` and sealed intent. Read the exact owner-bound Timeline source from durable identity, **not** from an arbitrary same-owner query or live UI state.
2. Confirm manifest `Verified`, final tick, codec/rules/layout/seed/spawn and verified hash using the existing public Timeline port and canonical reducer. Recording sources cannot be listed as completed. Preserve the verified source even if later Ghost Lap operations fail.
3. In one **GhostLap-owned PostgreSQL transaction**, insert/return catalog `(owner,timelineId)` and operation-idempotency record and mark intent `Saved`; enforce uniqueness. A conflicting operation/body/timeline mapping fails. Return `Saved` **only** after transaction commit or an authoritative post-ack-loss read verifying that exact commit.
4. If catalog commit fails, mark status truthfully if possible and retry/recover the **same** verified source by durable key. If DB unavailable, return `Pending/Retryable`, not a false saved receipt. Recover after process death without an additional background daemon or cross-schema distributed transaction.
5. Apply same commit protocol to verified child attempts; child creation/finalization never mutates source. Operations have bounded retries, visible typed errors and audit records; no secret history expiration.

### Crash table (must be implemented and tested)

| Crash boundary | Required recovery |
|---|---|
| Before durable intent | No attempt started; same request can initialize safely |
| After intent, before source creation | Resolve exact reserved/source identity or resume idempotent creation |
| Source creation committed, response lost | Discover **same** identity, do not mint sibling |
| During canonical input/checkpoint append | Restore last **durably committed** prefix; ack only committed work; one writer |
| At tick 2700, before verification | Remain `Verifying`/recoverable, never claim original saved |
| Verification committed, catalog failed | `VerifiedUnlisted`, retry exact source by operation key |
| Catalog committed, HTTP ACK lost | Read exact committed catalog link and return same result |
| Old process alive after guard/connection loss | Old writer cannot append after replacement fence; no forked suffix |

## 3. Live attempt lifecycle and exact prefix recovery

`Preparing -> Recording <-> Paused -> Verifying -> VerifiedUnlisted -> Saved`; `Failed` or `Incompatible` only with specific truth-backed reason. **No automatic completion of a partial record.** The active world has an authority generation, session/controller epoch, immutable sealed rules and a single monotonic canonical tick. On authenticated disconnect/refresh, a live solo attempt transitions to Paused at a durable canonical tick boundary; the client later resumes the same source, rather than earning unseen time/score. User navigation to another completed original also **pauses** the living solo attempt at a committed tick; no unseen score accrues. The attempt remains independently recoverable, not quietly replaced by the selected original. A completed original replay is read-only and cannot be forked from an actively Recording source.

At every committed append: canonical bytes/tick and hash/sequence are owned by Timeline, not a separate ghostlap write-ahead input log. An acknowledged/applied input is distinguishable from a **durably persisted** one; recovery consults canonical persisted prefix/append version, not merely the optional GhostLap progress hint. Recover state exactly at committed tick N by existing full-checkpoint + fold, verify hash, obtain the next valid tick N+1 and validate any deferred/retried input against the committed sequence. Client retries with same command/operation identity must not append twice. An input accepted in memory but not durably committed may be rejected/resubmitted, never silently claimed to have survived.

A resume obtains authority/fence **before** loading/accepting new inputs, rechecks it at actual append, and emits a new controller epoch. If two resumes race, one attaches as current authority; the other receives typed conflict/reattach. A stale writer cannot turn lost process ownership into a second valid suffix. At minimum test process A with append in flight, isolated guard-connection loss, replacement B, and divergent clients; committed canonical state may have exactly one legal continuation. There is no new Timeline serializer or speculative duplicate replay log.

## 4. Deterministic simulation, ghosts and exact branching

- **Canonical baseline:** actual Spec 06 `ghost-v1`, 30Hz, 2,700 ticks, seed/rules/layout/spawn and domain RNG pinned, tick0 initial state, states t1..t2700 after one step. Full authoritative checkpoint includes position/velocity, charge/Overlit deadline, pending auto-Burst, effective input/accepted sequence/Burst edge **where relevant to the pinned reducer**, pair-contact cooldowns where relevant, score categories and fixed-point movement remainder, active/scheduled/collected candles with stable IDs, seed/layout identity and RNG domain streams. Restore(T) folds all necessary inputs after checkpoint, validates state/hash at T; no skipping idle ticks or position-only reconstruction.
- **Original history** is owner-verified, immutable and always a selected explicit source ID. Genuine first original requires finishing the full newly played run; neither an abandoned attempt nor a challenge/fork is promoted silently to original. Challenges from tick0 use original initial state and a read-only source ghost, while living world accepts its own inputs; a completed challenge is labelled `Attempt(Challenge)`.
- **Fork:** only from a completed verified Original at `0 <= T < 2700`. Restore full source state/hash at T; create child through **existing** Timeline branch semantics with parent/source identity, inherited canonical prefix through T and new suffix starting **T+1**. A completed `Attempt(Fork)` is not an eligible original source; nested branches outside scope. T=0 means a full restart as a child from original initial state; T=2699 leaves one tick; T=2700 seek-only. A paused/running attempt cannot become accidental source merely because its state is cached.
- **Independent histories:** a read-only ghost reconstructs historical position, charge, action cues and score from original journal. Its candle/Burst events are historical visualization only and NEVER interact with living candles/player. Living candle consumption and pending actions are separate, but precomputed schedule or independent RNG substreams preserve same future opportunity IDs/ticks across children even after different pickups. Multiple children of same original at T retain exact prefix/source hash and diverge independently.
- **Aligned comparisons:** at tick T, `Delta(T)=CurrentScore(T)-SourceScore(T)`, using backend canonical integer score components, not renderer or completed-source final score. Build `solo_score_sample` as a hash/version-bound cache that can be verified/regenerated. Historical event IDs can be deterministic `(historyId,tick,index)` for honest later visual feedback; no added game mechanic.

### Historical version compatibility

Keep `ghost-v1` codec/reducer fixtures runnable and unchanged for existing V1 histories; future MP speed/geometry changes do not alter stored bytes. Versioned new solo physics/layout only through **explicit** rules identity and preserved old implementations where feasible. Retain verified originals by default, and declare a reducer unsupported only when genuinely absent; expose metadata with `Incompatible` rather than attempting rewind via substitute reducer. Any historical migration must preserve original bytes and verified hashes; no rewriting Marrow.

## 5. Authorization, resources, recovery boundaries

Access checks derive actor from server principal on every catalog, Timeline, source/hash, trace, score and write operation. Public history list is private; Spec 12 may issue a specific read-only replay grant that cannot start a challenge/fork or read owner-wide history. Sharing revocation is checked by Spec 12's policy, not by rendering code. Guest reauth preserves owner identity when valid; an unrecognized browser has no ownership simply because it knows a catalog/timeline ID.

Measure canonical log/checkpoint storage for 2700 ticks, cold Restore at T=0, 1140, 2699, 2700, multiple concurrent read-only seekers and attempt owner count; isolate seek/verification/canonical I/O behind bounded execution lanes so they cannot stall the Realtime multiplayer tick-critical path; set practical attempt/concurrent-rerecord limits based on this evidence. Fail closed on corrupted hash or unsupported codec. Do not write directly across schemas, create cross-schema FK, run a background magical reconciliation service, or silently remove originals to meet a quota.

## 6. Tests and acceptance evidence

**Mandatory deterministic fixture:** a real 2700-tick source with nonzero movement score/remainder, collected candles, nonzero charge/Overlit, cooldown/pending action where reachable at T=1140. `Restore(1140).Hash == recorded hash`, branch A/B first differ only after tick1140; source final hash remains unchanged. Repeat T0,2699,2700 inspect-only, same-tick candle schedule continuity, original ghost crossing living candle, multiple independent branches, old rules fixture and unauthorized source.

**Mandatory persistence fault matrix:** two same-owner verified-but-unlisted sources with different keys; lost `Create` ACK; source verified then catalog commit failure/HTTP ACK loss; process crash after each stage; one exact recovered catalog link per key. Cold reload actual Postgres + Timeline manifests. Race concurrent save keys and conflicting duplicate payloads. Never assert a claimed `Saved` state without actual verified manifest and committed catalog link.

**Mandatory exclusive suffix test:** process A owner guard/connection loss while ordinary DB access remains, in-flight append at N; replacement B attempts same attempt/branch from committed prefix; two clients send divergent packets. Exactly one canonical suffix verifies after cold reload; old generation rejected at *write boundary*, no double tick/seq. If frozen append APIs cannot enforce this, **leave gate blocked** and do not weaken requirements.

**Mandatory integration:** real product API/Realtime registrations with least-privilege grants, GhostLap/Marrow ports and Revenant policies intact; no `/__e2e` production routes or test-only bridge; history access isolation; actual cold restart and finite storage/seek measurements, including 4 live MP rooms while concurrent solo attempts are recording and cold seeks run. Test result/hash commands and evidence logged, never presumed passed.

**External technology note:** Microsoft documents invocation-scoped SignalR hubs, so live attempt state belongs to a long-lived hosted service/owner, not hub object fields. This does not prove any current project implementation.
