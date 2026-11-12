# R6 live financial ML evidence

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

The [published Worker run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43254583935) passed at source `f45ecbcb34452373a35d086fc772886e09378c2a` and merge `92d1b14846e721c2cc41645b10056079c6f0aa14`. Its [TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43254583935/artifacts/13959644761) records 91 passes and zero skips. The Worker dispatched Screening fact 532 at 868,798 PPM and reserved 96,101 minor units. It synchronized the exact prefix `eb488b5d7db775e7b09da4e1c09ff5171d25e82eede8cf98aa835703222beef3`, denied an unauthorized settlement and repeated reconciliation without duplicating the hold. The offline /2 sidecar identifies this payment, `P-00000054`, as a legitimate hard negative.

## Final native financial and fork checks

The final implementation is `4ad02158b5b626738bc7ae88d7ffa4dee099b5d5`, tested at merge `e9ef90fc50755db771ff8d8e6c9a82da1c04d4d1` on [draft PR #6](https://github.com/nightwing9-bit/the-sands-of-time/pull/6). The [final native run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43261561932) recorded [93 executed and passed PostgreSQL cases with zero skips](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43261561932/artifacts/13964262887). Exactly one result passed for each required R6 case.

- `Published_R5_score_admits_a_real_V4_reservation_and_replays_cold` verified the transactional vector and score, SQL facts and evidence hash, reservation and case, balances, and cold replay. The last pre-Screening prefix was `1cedb9b2e84ac58bb5272672bd74fe96b774ed433d0949794da61c806be72643`.
- `Published_Worker_dispatches_and_synchronizes_a_model_admitted_hold` verified published Linux AMD64 dispatch, the synchronized digest `3a64ae6bb6d02760fc17ed6ba2304cbbfda2e6772ce31f977d7fde21ddb15307`, idempotent reconciliation, and denied unauthorized settlement. Missing registration and a corrupt ZIP blocked the head command. A case outbox failure and a corrupted SQL evidence reference rolled back the reservation, case, cursor and command. Retrying admitted the original opportunity once.
- `Competing_model_screenings_consume_the_last_slot_once` processed 868,798 and 951,759 PPM at eligible opportunities 28 and 29 with one slot. The first reserved 96,101 minor units. The second explicitly recorded capacity suppression, opened no case and settled legally. The competing command recomputed its evidence against the committed first Screening prefix.
- `ModelRequired_fork_inherits_capacity_and_scores_its_own_suffix` verified shared checkpoint bytes at seal fact 533, hash `1e11d5d37881a15242428463b4235ead47cc91aa5394760340e284543d79bb9b`. The child inherited 28 eligible, zero mandatory and one learned review admission. Its vector for the shared payment was `93826ec690b1a4e8f7a271095e78c2057e63d1d42f114c1508295cfb1f7eeb1f` at 261,571 PPM, while the source vector was `3097cfbd414036850e3bf221c0868c5c74cd144ad13fba0f1f317a8c61a8be04`. The child preserved v8 holds, rejected an unfunded mandatory hold without consuming a slot, denied duplicate effects, approved a high-value case through a distinct supervisor, and settled then returned 1,000,000 minor units. It rejected the inherited learned hold and released 96,101 minor units on the child. Wrong-tenant access and replay-role writes were denied. The source SQL fingerprint `e80e806143201fb6f6d4e21f3308fc443036a599b5f7e504a49f25a73c446978` and Marrow record hash `5c4084cffe8a6ab81998b4d56f271e38e0663538215bff1039d6331547a61418` matched before and after. Cold replay verified both histories.

An earlier native fork exposed a capacity extension placed after the clock trailer. The fix moved the extension before the trailer and preserved the existing SQL provenance guard. A subsequent run found cleared payments left in two test fixtures. The fixtures now execute their lawful settlements before closing, and the closure guard remained in force.

## Paired live qualification and R7 series

The [qualification TRX, report and bank observations](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43261561932/artifacts/13964832380) record one executed and passed qualification case, with zero skips. The same run passed all seven jobs. The separate [ML.NET platform evidence](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43261561932/artifacts/13963064648) includes one executed R5 pin test, three native probes and published Worker parity for all seven R5 references on .NET 10.0.14/Linux AMD64.

The 72-hour paired comparison selected deterministic shifted-ring /2 root 3. Root 2 produced a legitimate learned admission, so the test advanced to the next fixed root. Root 3 had a malicious ring admission and capacity suppressions. The two modes used identical exogenous instructions, with shared plan digest `a241fcf948e9f5e08ff8716dcfa198857695355e6b213e44ed44495bf27f8184`, and dispatched their own financial outcomes. Hidden truth was joined after dispatch for reporting.

| Measure | Rules-only v7 | Live model-required v7 |
| --- | ---: | ---: |
| Warmup / scored post-warmup Screenings | Rules only | 35 / 110 |
| Cumulative slots / post-warmup mandatory reviews | — | 5 / 1 |
| Threshold requests / learned admissions / suppressions | — | 5 / 1 / 4 |
| Learned admissions classified as malicious / legitimate | — | 1 / 0 |
| Posted settlement principal by end of customer plan | 14,355,782 | 14,221,977 |
| Returned principal by end of customer plan | 920,526 | 920,526 |
| Principal reserved by end of customer plan | 399,660 | 533,465 |
| Posted settlement principal after the human review | 14,355,782 | 14,355,782 |
| Open cases after the review | 3 | 3 |
| Released cases after the review | 0 | 1 |

Amounts are USD minor units. The learned case held 133,805 minor units for `P-00000055` (Ring). The v7 baseline cleared and settled it. The model requested review at fact 568 at 939,089 PPM (raw Single bits `1099038722`). The opportunity count was 20 with one allowed and one available slot. The prior prefix digest at fact 567 was `6570e3182d60142607c46a25541c285a576d074a1179f7e31656186fd4de75c5`, the canonical vector digest was `17fb7e5323b30672c069c642ab8bc5f792d219b9420a1b5169358bb632817707`, and the evidence SHA-256 was `8e20716f4c7956be0c69cfa543a657aa1ffff6779c647deb4367b4ea248632b1`. The report includes the full canonical vector. In feature order its 70 values were:

```text
522, 1, 26496, 19726, 0, 19726, 1, 0, 0, 0, 0, 1, 0, 1,
1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
1, 0, 0, 0, 0, 1, 542, 0, 0, 0, 1, 0,
1, 0, 0, 0, 0, 1, 542, 0, 0, 0, 1, 0,
1, 0, 0, 0, 0, 3, 2411, 0, 0, 0, 3, 0,
392, 0, 0, 0, 0, 1000000, 964346, 0
```

Before the late review the payer's posted/reserved/available balance was `5372810/133805/5239005`. After authorized release and settlement it was `5239005/0/5239005`, the same as the baseline at the end of its plan. Four later feature vectors differed between the two live financial histories. Scoring took 0.418 ms median and 0.474 ms p95 across 110 scores. Finance dispatch took 28.913 ms median and 49.107 ms p95 across 435 committed commands. The measured run had zero inference failures and retries. Peak five-minute workload was two actual cases and one threshold request. The JSON report contains the bank/day workload and terminal payment and case statuses.

The qualification includes 5,190 model-aware bank observations covering all six banks. It audited account reservations, actual future settled flows, missing horizon coverage, and cold replay. The bank series SHA-256 is `cf1775026e3d48e278f77a4ca69519dcf930aa0cbb5b1497bc13173a7c51a3bb`. Regenerate on disposable Linux AMD64/PostgreSQL infrastructure using the repository integration job and:

```bash
REVENANT_R6_EVIDENCE_DIR=TestResults/r6-qualification \
REVENANT_R6_SOURCE_SHA="$(git rev-parse HEAD)" \
dotnet test tests/Revenant/Revenant.Tests.csproj -c Release \
  --filter 'Category=R6Qualification&FullyQualifiedName~Six_bank_model_required_run_measures_interventions_against_rules_only'
```

R5's offline results show strongest coverage on ring behavior. The synthetic calibration has no real-world fraud probability validation. The selected root demonstrates the financial intervention and its consequences. Sequential review capacity suppressed four high-risk requests, and a held payment remained pending until review. The approved ring payment ultimately settled, so the measured effect was deferred settlement. Both histories replayed without inference.
