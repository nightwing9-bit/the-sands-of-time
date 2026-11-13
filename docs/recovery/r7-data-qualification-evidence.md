# R7 data qualification evidence

## Historical census

The [machine-readable census](r7-historical-series-census.json) summarizes the 24 R4 roots, 24 R5 roots and retained model-required R6 root. The audit reconstructed the rules-only histories with the original planners and canonical V4 reducer, then matched both published dataset manifest hashes, including every underlying stream hash. The R6 audit verified the retained SQL qualification bank stream against its published SHA-256 and checked each accepted-intent count against its bank and five-minute bucket before attributing the intents to accounts using the fingerprint-matched plan. R6 account attribution comes from that reconciliation. R6 bank balances come from the original model-required execution. [R4 qualification](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43207060325/artifacts/13939063651) · [R5 qualification](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43221126952/artifacts/13945274825) · [R6 qualification](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43261561932/artifacts/13964832380)

| Account activity | R4 | R5 | R6 |
| --- | ---: | ---: | ---: |
| Distinct accounts | 661 | 642 | 26 |
| Accepted outgoing intents | 2,219 | 3,406 | 145 |
| Outgoing intents per account, p50 / p90 / p99 | 2 / 7 / 27 | 3 / 13 / 24 | 3 / 15 / 20 |
| Nonzero buckets per account, p50 / p90 / p99 | 2 / 6 / 9 | 3 / 10 / 13 | 3 / 10 / 14 |
| Zero buckets | 99.706% | 99.494% | 99.484% |
| Active on at least two days | 319 | 393 | 16 |
| Sequences with at least 49 buckets and 16 nonzero buckets | 0 | 2 | 0 |
| Explicit anomaly-positive / normal / unlabeled buckets | 411 / 1,266 / 569,427 | 846 / 1,961 / 551,881 | 36 / 80 / 22,374 |
| Explicit normal buckets with multiple intents | 24 | 49 | 2 |

Sixteen nonzero buckets is a census diagnostic for informative history. Each account had at least 49 completed buckets. The active bucket p50 and p90 were both one in all three histories. Median account lag-1 and lag-8 autocorrelations were near zero. R4 showed some repeated daily timing, with median lag-288 autocorrelation 0.246 among 355 accounts with defined correlation. The corresponding medians for R5 and R6 were near zero. The cohort offers isolated burst examples and almost no repeated dense behavior for transferable account SSA training. A pilot needs more recurring legitimate intake, overlapping ordinary and unusual burst magnitudes, and explicit normal and anomaly labels across a much larger fraction of evaluated buckets.

| Bank observations | R4 | R5 | R6 |
| --- | ---: | ---: | ---: |
| Completed bank rows | 124,416 | 124,416 | 5,190 |
| Balance changes | 2,833 | 4,581 | 191 |
| Unchanged fraction | 97.72% | 96.32% | 96.32% |
| Per-bank range of changed buckets | 11–30 | 12–54 | 18–46 |
| Bank origins with all eight future targets | 123,264 | 123,264 | 5,142 |
| Net settlement change buckets | 2,951 | 4,687 | 197 |
| Net reservation change buckets | 26 | 28 | 4 |
| Buckets at or below 20% of opening available | 0 | 0 | 0 |

A changed bucket compares the available balance at its seal to the preceding seal or, for the first bucket, to genesis. Across banks, the median absolute change among changed buckets ranged from 19,436 to 119,978 minor units in R4 and from 41,599 to 143,963 in R5. The median bank lag-288 change autocorrelation was 0.138, 0.009 and 0.008 respectively. These results provide weak evidence for choosing a common daily SSA window. A pilot needs legal repeated short-period cross-bank flows, material changing origins, reservation pressure and measured approaches to a frozen diagnostic buffer. Use active and stress strata alongside aggregate error.

R6 opened at epoch millisecond `1771499400000`. The first bank observation was at `1771499700000`, the 864th seal was at `1771758600000`, and the 865th seal was at `1771758900000`. The extra seal captured the authorized review, release and settlement after the planned end. Genesis supplied a reference balance and contributed no observation row. During the 864 common seals, the retained model-required run differed from its reconstructed rules-only counterpart on 459 available-balance rows, 918 posted-balance rows and 459 reserved-balance rows. The maximum available-balance difference was 133,805 USD minor units. The learned held payment later settled after review. [R6 live evidence](r6-live-financial-ml-evidence.md)

## Reproduce the detailed census

Download the retained `r6-live-financial-evidence` artifact from run `43261561932` into an ignored results directory. The census case writes per-root, per-bank distributions and per-horizon counts to the output file.

```bash
gh run download 43261561932 --repo nightwing9-bit/the-sands-of-time \
  -n r6-live-financial-evidence -D TestResults/r7/r6
REVENANT_R6_CENSUS_INPUT="$PWD/TestResults/r7/r6" \
REVENANT_R7_CENSUS_OUTPUT="$PWD/TestResults/r7/census.json" \
  dotnet test tests/Revenant/Revenant.Tests.csproj -c Release \
  --filter FullyQualifiedName~Reconstruct_and_audit_the_R4_R5_R6_histories
```

The seven-day R7 pilot and independent qualification cohort are pending the Linux AMD64 SSA transfer result. The initial native fixture uses three shorter six-bank roots to test fit, transfer and reconstruction.

## Seven-day PostgreSQL pilot

Generator `revenant-episode-generator/3` used two independent six-bank roots and 2,016 completed seals per root. The native test passed once with zero skips in 10m32s. The job failed after the test because the TRX guard called `rg`, which the runner did not have. The uploaded TRX and manifest checks were verified against the retained artifact. The workflow now uses `grep` for these checks. [Pilot run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43363691834) · [TRX, manifest and canonical series](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43363691834/artifacts/14009897750)

| Measure | Root A | Root B |
| --- | ---: | ---: |
| Committed outgoing intents | 1,772 | 1,739 |
| Posted settlements | 1,771 | 1,739 |
| Holds / authorized releases | 7 / 7 | 7 / 7 |
| Posted returns | 24 | 22 |
| Qualified accounts | 10 of 12 | 8 of 12 |
| Unlabeled-event-free normal nonzero account buckets | 1,246 | 1,246 |
| Developing-pressure crossings of the 10% diagnostic floor | 2 | 2 |
| Sudden-shock floor crossings | 0 | 0 |
| PostgreSQL execution and replay | 631.9 s | 614.9 s |

There were 4,882 changed bank intervals of 24,192 (20.18%) and 24,096 origins with all eight targets. Maximum observed round-trip normalization error at scale 1,024 was zero minor units. The histories include enough real variation to test transfer and expose the starter failures. Seven days are sufficient for this diagnostic. Daily seasonality and a longer simulation have no demonstrated benefit here.

The pilot manifest SHA-256 is `36cc5b4c229f70953a19b38d41f675d0e70d70941f9c7ba6225bd4edd06c5d78`. Root keys are `514e1dc178bde270ebd60c95ce8075228457b7d2e62e8505a46a9a02e45fdc74` and `be567b6a27658b2136b0806a2418f2f376c6c6aa69455ec281ee7d9b13f8b446`. Source `5c8d48dd9beaebcc12cab0aa4c02ebf40c370bf7` and merge `bd3ebd34eae337dfc1395b82aa359ce41e74890f` generated the streams.
