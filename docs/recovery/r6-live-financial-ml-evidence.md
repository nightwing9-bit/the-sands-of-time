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

No R6 native CI has executed. The required integration test processes genuine six-bank `/2` instructions and must observe an actual learned hold using the pinned scorer. It checks exact vector bytes and score, SQL reservation, case, canonical evidence, command outcome, and predictor-free Marrow replay. It emits the measured cutoff, vector digest, score, capacity and before/after balances to the integration TRX. CI rejects a result that omits the test. Actual scores, reservations, cases and timings are pending.

Published Worker dispatch, authorized review and return qualification, concurrent capacity admission, native fork evidence, and the paired rules-only/live model comparison remain pending. R5's published results are offline evidence; they show strongest coverage on ring behavior. The synthetic score is not a validated probability of real-world fraud. Capacity may suppress above-threshold requests, and admitted holds can change later features and settlement activity. A pending hold is a reservation and settlement deferral.
