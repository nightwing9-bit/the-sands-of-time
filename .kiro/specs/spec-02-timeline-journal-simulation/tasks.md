# Spec 02 implementation tasks — Marrow Journal, Simulation and Timeline

**Execute tasks in listed order.** Dependencies: spec-01. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [x] **2.1 Freeze codec and contract surface**
  - **Precondition/input & inspect:** `docs/architecture/03-contracts.md`, `src/Marrow.Kernel`, Marrow Timeline project. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** one source/branch-addressable `TimelineId`, `CreateSourceAsync`/`AppendAsync`/`CompleteAsync`/`RestoreAsync`/`ReplayAsync`/`ForkAsync`/`CompareAlignedAsync`, canonical state+input+context codecs and version resolver, typed Kernel Result/errors. Continue child through the same append path.
  - **Forbidden changes:** bank/game types or hidden DateTime.Now/Random. Do not change frozen shared public contracts without ADR.
  - **Verification:** compile the full lifecycle, verify typed failure mapping and legal references; source/child IDs both usable with restore/replay.
  - **Done when:** two test reducers compile against one interface.

- [x] **2.2 Implement immutable store and migrations**
  - **Precondition/input & inspect:** `marrow_timeline` schema, migrations, Postgres adapter. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** `marrow_timeline` module migrations, manifest/steps/checkpoint/fork/context-artifact storage, atomic create(position 0)/append/complete and controlled status; migration executed through Spec-01 one-shot runner.
  - **Forbidden changes:** generic CRUD update/Delete for history. Do not change frozen shared public contracts without ADR.
  - **Verification:** real Postgres duplicate/gap/concurrent append, restart-persistent initial checkpoint and pinned context, UPDATE/DELETE denied for immutable records.
  - **Done when:** append-only persisted steps verified.

- [x] **2.3 Implement restore/replay/hash**
  - **Precondition/input & inspect:** transition/codec/checkpoint ports, test adapters. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** nearest-checkpoint loading, canonical input decode, segment-specific durable context/reducer resolution, deterministic fold, SHA-256 chain and typed errors.
  - **Forbidden changes:** timestamp ordering or position-only snapshots. Do not change frozen shared public contracts without ADR.
  - **Verification:** FsCheck restore/replay across random sequences, cold restart, missing/mismatched codec/context and corrupted stored bytes; verify position 0 and final bounds.
  - **Done when:** all valid positions reproduce recorded hashes.

- [x] **2.4 Implement fork/continue/compare**
  - **Precondition/input & inspect:** branch metadata, source resolver, stable operation id. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** readonly verified parent prefix, own child suffix under new pinned context, idempotent forks, aligned verified-state comparison (no finance/game metrics); child append delegates to source append path.
  - **Forbidden changes:** copy-and-rewrite of original, business effects inside Timeline. Do not change frozen shared public contracts without ADR.
  - **Verification:** sibling branches with changed context, prefix under original context, child cold restore, terminal-position behavior, cancel/retry and typed error tests.
  - **Done when:** branches are independently reproducible.

- [ ] **2.5 Cross-domain-shape proof and gate**
  - **Precondition/input & inspect:** tests/Marrow fake tick+event reducers, shared Assembly refs. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** end-to-end Testcontainers proof, docs for domain adapter usage.
  - **Forbidden changes:** passing tests only from in-memory fake store. Do not change frozen shared public contracts without ADR.
  - **Verification:** clean DB create(0)/append/complete/restore/fork/child append/compare, then restart and replay using pinned codecs/contexts with both reducer shapes.
  - **Done when:** single engine works with both logical clocks.

## Spec exit verification (blocking)

One `Marrow.Timeline` assembly and real Postgres store pass both event-ordered and tick-ordered reducers, arbitrary valid T, hashes, branch isolation, corrupt history and cancellation checks.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
