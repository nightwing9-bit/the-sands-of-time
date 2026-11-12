# R4 qualification evidence — first push checkpoint

The verified R3 implementation is `927704d5c7fef3e00673860ec73b81c60b1cb8ef`.
The R4 candidate is based on its local child
`91b9706558cd2aeba340e60cc183d9865e2b2c2e`, verified to change only the two
R3 recovery documents. The stacked PR may include those documents if its R3
remote base still ends at the implementation commit.

The macOS Release build with SDK 10.0.404 passed with zero warnings and errors.
The CI-equivalent local test filter passed 359 tests with zero skips: 173
Revenant, 103 Marrow, 68 Ghost Lap, 11 architecture and 4 other pure cases.
The tests cover deterministic manifest bytes across physical run IDs, duration
and population changing the hash, exact context pinning, invalid genesis
rejection, legacy context rejection and the existing Friday Ring golden.
The frozen model hash matched
`c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de`.

The native gate is `BehavioralEpisodePostgresTests.Six_bank_behavioral_root_commits_real_payments_holds_returns_and_seals`.
The existing CI job requires it to execute and pass alongside R1–R3. The
fixture specifies 6 banks, 18 accounts, 2 simulated hours, 4 intakes, 4
screenings, 2 settlements, 1 hold, 1 rejection, 1 compensating return, 24 seals
and 35 total facts. It compares posted, reserved and available values against
PostgreSQL, checks the exact seal boundary and verifies Timeline replay.
Those counts are assertions, not measured native results.

No candidate was pushed or tested natively at this checkpoint. There is no
draft PR, CI URL, merge SHA, PostgreSQL result, dataset manifest, split hash,
row count or throughput measurement. The pilot and qualification remain
unrun. No local Docker or unrelated PostgreSQL was used. The first push
requires explicit approval; the native opening result gates the next
checkpoint.

| Gate | Status | Executed proof and limitation |
| --- | --- | --- |
| R4-01 | VERIFIED PASS | Ancestry, protected scope, model hash, and local historical golden tests checked. |
| R4-02 | IMPLEMENTED / UNVERIFIED | Pure manifest and V4 genesis tests passed; actual PostgreSQL root and seal gate pending. |
| R4-03 | NOT STARTED | No multi-day run. |
| R4-04 | NOT STARTED | Stable opening bytes tested; no semantic dataset export. |
| R4-05 | NOT STARTED | No bank projector. |
| R4-06 | NOT STARTED | No account projector or rolling windows. |
| R4-07 | NOT STARTED | Financial hold and settlement assertions compiled; no bank projection or native result. |
| R4-08 | NOT STARTED | Financial balance assertions compiled; no native result or bank reconciliation. |
| R4-09 | NOT STARTED | No coverage query. |
| R4-10 | NOT STARTED | No pre-screening row builder. |
| R4-11 | NOT STARTED | No suffix invariance test. |
| R4-12 | NOT STARTED | Fixture inputs use a neutral legacy value; no offline annotation flip. |
| R4-13 | NOT STARTED | No split assignment. |
| R4-14 | NOT STARTED | No multi-family behavior generator. |
| R4-15 | NOT STARTED | No split independence checks. |
| R4-16 | NOT STARTED | No eight-horizon target builder. |
| R4-17 | NOT STARTED | Scenario manifest hashing tested; projection and dataset hashing pending. |
| R4-18 | NOT STARTED | No pilot or qualification run. |
| R4-19 | BLOCKED | Existing workflow amended and test compiled; awaiting first push approval. |
| R4-20 | PARTIAL | Opening contract and checkpoint evidence written; R5/R7 handoffs pending. |
