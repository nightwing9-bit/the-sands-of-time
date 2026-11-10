# Spec 06 implementation tasks — Ghost Lap V1: deterministic greybox and first solo ghost

**Execute tasks in listed order.** Dependencies: spec-02, spec-03, spec-04. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **6.1 Implement versioned arena, schedule and physics**
  - **Precondition/input & inspect:** `src/GhostLap`, `GameRules`, Marrow reducer contract. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** fixed-point geometry/collision, authored spawn sockets, deterministic candle plan.
  - **Forbidden changes:** DateTime-driven spawns, client distance score or global random coupled to pickups. Do not change frozen shared public contracts without ADR.
  - **Verification:** unit tick/geometry replay tests and 2/3/4 spawn property checks.
  - **Done when:** same input produces same state hash.

- [ ] **6.2 Implement charge/contact/Burst/score**
  - **Precondition/input & inspect:** rule engine, event priority table, rules hash. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** full four-stage Soulfire, contact cooldown, score clamps, manual+auto Burst and finish; canonical fixed-point distance/remainder and effective input/latch state in the single Spec-06 state/input codec.
  - **Forbidden changes:** unversioned constants or duplicate client simulation authority. Do not change frozen shared public contracts without ADR.
  - **Verification:** FsCheck same-tick, charge, overlap, Burst once; missing/late inputs, Burst edge and checkpoint at 1023/1024 score-unit boundary.
  - **Done when:** server rule kernel handles complete greybox game.

- [ ] **6.3 Record and restore original solo**
  - **Precondition/input & inspect:** Marrow Timeline, guest identity/session, complete codec. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** 2700 canonical effective-input steps, the one final versioned game input/state codec, checkpoint/hash, verified original Timeline source with owner; Spec 11 later adds catalog/listing, never replaces this codec.
  - **Forbidden changes:** saving only position/video or false save toast. Do not change frozen shared public contracts without ADR.
  - **Verification:** real DB replay all tick sample/any T, refresh and owner tests.
  - **Done when:** original survives refresh and hash verification.

- [ ] **6.4 Branch and inert ghost**
  - **Precondition/input & inspect:** source trace adapter, source/branch worlds, common component. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** exact checkpoint fork T, same opportunities, independent collection and score curves.
  - **Forbidden changes:** ghost collider or separate replay backend engine. Do not change frozen shared public contracts without ADR.
  - **Verification:** nonzero-charge/pickups T test; source-hash unchanged.
  - **Done when:** real branch changes outcome independent of ghost.

- [ ] **6.5 Connect usable browser greybox (B2 joint proof is a separate basket gate)**
  - **Precondition/input & inspect:** GhostLap React/Pixi placeholders and shared UI package; **no need for completed Spec 05 inside this spec**. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** play keyboard/Burst, seek and Rewrite, old/current aligned HUD.
  - **Forbidden changes:** static mock score animation or new scrubber copy. Do not change frozen shared public contracts without ADR.
  - **Verification:** Playwright real GhostLap backend seek/branch; verify **only at B2** after both 05/06 that both app adapters import Marrow assembly.
  - **Done when:** independent real GhostLap greybox and branch journey is observed; B2 joint milestone follows separately.

## Spec exit verification (blocking)

User plays full greybox solo, reloads original, rewinds at an already-collected-candle/nonzero-charge tick, forks, witnesses independent inert ghost and aligned score delta. The bank/game shared Timeline + scrubber integration proof is separately required at B2 after Specs 05 and 06, not a prerequisite for this spec exit.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
