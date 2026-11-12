# R6 first financial checkpoint

The candidate is stacked on R5 documentation commit `0d03007d61bbabe1ae4e0819fc1d5fdd2437b8f7`, which includes R5 implementation `88eb898d289233739706961da612c65d9934cfb3`. The candidate revision is the commit that adds this report.

| Verified locally | Result |
| --- | --- |
| R5 ZIP SHA-256 | `56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c` |
| R5 manifest SHA-256 | `37a00f88142256ead46d958696c8bd7d77d8d7115c3e52b78cd578536c2820ec` |
| V3 ZIP SHA-256 | `c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de` |
| R5 model | `revenant-behavioral-risk-v2/d1801a3f72f9d4525cd39b8b7873e2fc43288dea0f7c1f5ee8646612833a28e4` |
| Feature schema | `risk-features-v2`, SHA-256 `9b905e1f3f3a89e9627f0f970b46638d1a60aaa9c073b24690659a087b54e87f` |
| Threshold and qualified runtime | Inclusive 852,469 PPM; ML.NET 5.0.1 on .NET 10/Linux AMD64 |
| Local Release | SDK 10.0.404; 384 filtered tests passed, zero skipped, on macOS ARM64 |

The local tests exercise the warmup cutoff, evidence tampering, first-slot admission, checkpoint capacity inheritance and v8 rule precedence. The existing frozen V4 fixture checks still pass. R6 did not load the native scorer or start local Docker.

The first financial case passed in [run 43252548987](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43252548987). The full suite reported 89 of 90 passed because the upgrade test still expected two migrations after R2. A focused test correction required all three, including migration 0012. The [corrected run 43253620780](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43253620780) passed all jobs at source `26751b532d6669e9d640290a64fc4a24f29ddd99` and merge `f2b9d0b5a3e0fc9172370164500edc6e657a8286`. Its [Revenant TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43253620780/artifacts/13959499061) records 90 executed and passed, zero skips, including exactly one `Published_R5_score_admits_a_real_V4_reservation_and_replays_cold`.

The deterministic R5 /2 challenge root 1 recorded 116 scores, six threshold requests and one admitted learned hold. At fact 532, the actual score was 868,798 PPM. The canonical feature digest was `b9a3f265b8fb2ddafcdab8f9314be9775d85c6566fe4ab82dc233d9ad8fbbb49` and the preceding prefix digest was `6d4bd666496ac001a5f8b05b309c8188983705ac276a12a1b494c88ae31af72e`. The current opportunity was 28, with one allowed slot, zero mandatory reviews and zero prior learned reviews. Payer posted/reserved/available went from `4142715/0/4142715` to `4142715/96101/4046614` in minor units. The real hold, open case, SQL evidence reference, command outcome and cold Marrow reconciliation all passed. Root 0 recorded 105 scores and three threshold requests with zero admitted learned holds.

Published Worker dispatch, authorized review and return qualification, concurrent capacity admission, native fork evidence, and the paired rules-only/live model comparison remain pending. R5's published results are offline evidence; they show strongest coverage on ring behavior. The synthetic score is not a validated probability of real-world fraud. Capacity may suppress above-threshold requests, and admitted holds can change later features and settlement activity. A pending hold is a reservation and settlement deferral.
