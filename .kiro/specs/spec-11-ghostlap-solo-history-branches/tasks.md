# Spec 11 implementation tasks — Exact and durable Ghost Lap solo history

**All unchecked; work has not been performed.** Dependencies: Spec 02/03/06, their real public contracts/fixtures, actual project host and schemas. Spec 11 does not require speculative changes to shared Marrow or any Revenant feature. Do not begin later tasks until the explicit upstream compatibility blockers have evidence. Log test commands, failures, artifacts and commits in `docs/kiro-log.md`.

- [ ] **11.0 Mandatory frozen-port / recovery feasibility gate**
  - **Inspect:** actual exported `Marrow.Timeline` and Spec 06 solo create/record/verify/recovery/branch APIs, canonical codec full GameState, tick/input append semantics, manifest uniqueness, `SoloOriginals`/`SoloBranches`, existing migrations, `docs/architecture/{01-master,02-spec-map,03-contracts}.md`, guest identity and host DI.
  - **Produce:** explicit matrix proving (a) owner+operation key identifies one exact Timeline source across lost creation/verification response, and (b) stale writer cannot commit an append after authority replacement using existing ports; choose a compatible GhostLap-only design and name existing Realtime host/`ISoloAttemptOwner` registry. If not provable, record hard blocker, not a made-up port. Document verified original format, `ghost-v1` hashes and tick semantics.
  - **Verify:** real upstream fixtures and narrow concurrency/port tests. Confirm no extra canonical codec/log, cross-schema FK, test-only runtime bridge, shared ABI or Revenant change.
  - **Done when:** source correlation **and** storage-enforced single-writer feasibility are established before 11.2/11.3 coding.

- [ ] **11.1 Canonical codec and Ghost Lap-owned persistence mapping**
  - **Produce:** small `ghostlap` recording-intent/catalog/idempotency and derived score-sample persistence matching existing conventions; sealed owner/operation/attempt identity, statuses, Timeline scalar IDs with unique constraints, source/root/parent/fork metadata and version/hash binding. Existing `ghost-v1` codec, source and branch services remain the only canonical serializer/Timeline writer.
  - **Verify:** migration, DB constraints, same-owner two-operation case, ID/key conflicting duplicate, real 2700-tick cold reload and canonical codec hash fixtures. Test no second log/checkpoint byte store in `ghostlap`.
  - **Done when:** new metadata faithfully references verified canonical sources, with no version reinterpretation.

- [ ] **11.2 Real original recording, crash correlation and saved catalog**
  - **Produce:** production solo creation/recording flow using confirmed existing ports, durable intent-to-source recovery, verified source -> idempotent catalog handoff, owner retrieval/pins, truthful `Pending`, `VerifiedUnlisted`, `Saved`/failure states; full 2700-tick original only, no fake offline source. Bind API and runtime roles through existing DI/port mechanisms.
  - **Verify:** fault at intent commit/source creation/lost creation ACK/append/verify/catalog commit/HTTP ACK; two different source keys belonging to one owner; recovery after process death with **only durable identity**. Same request returns same source; no duplicate original; no success before verified source plus committed catalog.
  - **Done when:** a guest can complete/refresh/recover one genuine original even after failure at every handoff boundary.

- [ ] **11.3 Long-lived solo writer, resume and exact branch/challenge**
  - **Produce:** separate hosted solo attempt owner (not MP room service), guarded canonical append at actual storage boundary, controller epoch, explicit pause at committed boundary on navigation/disconnect, tick N -> N+1 restore, selected verified original and independent ghost/living states; challenge as child from tick0, fork at eligible T with suffix-only persistence and full-state hash proof. Preserve current unrelated paused attempt when selecting original.
  - **Verify:** crash during append/after commit/before ACK; old process retains ordinary DB connection after guard loss, replacement startup and two competing resume clients; one verified suffix after cold reload. Golden T1140 with complete nonzero state, independent siblings, T0/2699/2700 and Recording/source mismatch, old solo fixture unchanged. If storage cannot fence writes through frozen port, **stop and record explicit architecture blocker**.
  - **Done when:** histories cannot diverge under restart races and no fork mutates original.

- [ ] **11.4 Time-aligned reconstruction and viewer data contracts**
  - **Produce:** source vs current true tick score/charge/action samples, verified derived score cache or on-demand reducer fold, typed errors `RecordingNotForkable`, `FinalTickInspectOnly`, `VersionUnsupported`, `Unauthorized`, `SourceMissing`, `RetryableSave`. Avoid frontend ownership of score or game state.
  - **Verify:** T0,1140,2699,2700; historical score not final score at T; verified candle opportunity independence and ghost visual-only events; cache corruption/regeneration cannot falsify result. Share-read scope cannot reach writer/fork API.
  - **Done when:** backend source/current timelines reconcile at identical ticks.

- [ ] **11.5 Cross-domain, compatibility, storage and cold-runtime assurance**
  - **Produce:** explicit rule/codec compatibility policy with goldens, history integrity failures and measured checkpoint sizes/seek latency/attempt volume; production endpoints/host DI/role grants coexist with Marrow and certified Revenant. Keep original histories by default and bound guest abuse without silent deletion.
  - **Verify:** real DB/API/Realtime and guest principal, cold restart of recording and completed source, foreign-owner denial, incompatibility without decoding under new rules, session revocation, Testcontainers as needed; concurrent four-room MP tick-load during solo recording, cold seek and verification must remain within recorded Spec 10 scheduling budgets; actual distinct-staff Revenant access and forbidden cross-domain guest access remain correct. No `/__e2e` route or test bridge deployed.
  - **Done when:** required capabilities survive cold restart and unverified owners/versions cannot access or mutate private history.

## Blocking exit certificate

Publish actual canonical source/child IDs and final hashes (redact owner secrets), two-source recovery test, old-guard-with-live-pool suffix race result, T1140 full-state equality and sibling independence, tick-align score sample tests, real runtime/authorization evidence, performance measurements, commit IDs. **A design statement is not a passing test.** If a frozen port is insufficient, leave Spec 11 incomplete and escalate with precise evidence rather than rewriting Marrow.
