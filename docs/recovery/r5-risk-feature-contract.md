# Revenant V4 risk features

The schema is `risk-features-v2`, with 70 ordered columns and canonical schema SHA-256
`9b905e1f3f3a89e9627f0f970b46638d1a60aaa9c073b24690659a087b54e87f`.

`V4ObservationProjector.RiskAt(canonicalPrefix, acceptedIntentId, screeningTimeMs)` builds a snapshot from replayable V4 facts. The caller supplies the verified lineage through the fact immediately before Screening and the scheduled effective time of that Screening. The candidate must already be present as an Initiated payment. The prefix may include intervening facts since intake. It must include the latest completed seal required by the scheduler, and Screening must lie within its open bucket. Batch replay with `Project(history, captureRisk: true)` calls the same builder before applying each Screening fact.

The builder uses posted and actively reserved balances at the prefix position. Activity windows include both endpoints, `[max(origin, screeningTime - duration), screeningTime]`, but only facts in the prefix are visible. Lower sequence facts at the same timestamp are visible; higher sequence facts are absent. The candidate's own intent is excluded from every history aggregate. Source and child must each supply their own verified lineage and branch suffix.

The table gives column order. Replace `<w>` in all 12 window columns with each suffix in order before moving to the next window. These are the exact wire names.

| Positions (1-based) | Column names, in order |
| --- | --- |
| 1–8 | `amount`, `currency_usd`, `amount_to_available`, `posted`, `reserved`, `available`, `reported_new_beneficiary`, `zero_available` |
| 9–14 | `beneficiary_seen`, `counterparty_seen`, `outgoing_age`, `outgoing_missing`, `amount_to_mean_submitted_24h`, `submitted_24h_missing` |
| 15–62 | For each of `5m`, `30m`, `60m`, `24h`: `complete_<w>`, `submitted_count_<w>`, `submitted_<w>`, `settled_out_count_<w>`, `settled_out_<w>`, `settled_in_count_<w>`, `settled_in_<w>`, `holds_<w>`, `returns_in_<w>`, `returns_out_<w>`, `counterparties_<w>`, `beneficiaries_<w>` |
| 63–70 | `incoming_age`, `incoming_missing`, `destination_in_count_30m`, `destination_in_senders_30m`, `destination_in_30m`, `source_cross_bank_settled_share_30m`, `forwarded_principal_match_30m`, `forwarded_principal_missing_30m` |

- Currency is USD only; `currency_usd` is always 1. Flags are 0 or 1. Counts and fixed point values are nonnegative integers clipped to 8,388,607. Principal values use integer division of minor units by 256 before clipping.
- Ratios use integer floor in parts per million, then clip. For zero available balance, the ratio is 0 and `zero_available` is 1. For absent submissions in the 24-hour history, the amount-to-mean ratio is 0 and its missing indicator is 1. Where present, the amount-to-mean ratio is computed as candidate principal times the count divided by the sum, without first rounding the mean.
- Ages are floored seconds, clipped at 86,400. Missing recency is encoded as 0 with its missing flag set. Prior outgoing recency includes submitted or settled outgoing facts; incoming recency uses successful settlement only. Coverage flags are 1 if the entire window is observed since the opening. Partial history carries measured counts with coverage 0.
- `beneficiary_seen` and per-window beneficiary cardinality use prior outgoing submissions. `counterparty_seen` and per-window counterparty cardinality include all prior recorded account activity. Returns are counted on the side receiving or sending the compensating principal. Holds count the actual prior Screening outcomes that entered Held.
- The destination features count successful incoming settlements and distinct sources at the destination. The cross-bank share is the proportion of the source's settled transfers involving a different bank in either direction over 30 minutes, with 0 for no settlements. Principal match uses the smaller of the candidate amount and most recent incoming settlement divided by the larger; it is 0 with a missing flag if no incoming settlement is visible in the previous 30 minutes.
- Feature bytes are format int32 1, the length-prefixed schema identifier and schema hash, the int32 count, and each value as int32 big-endian. `Digest` is lowercase SHA-256 of the vector bytes. The schema digest covers its own canonical column names, unit tags and bounds. Every accepted integer is exactly representable as an ML.NET `Single` on conversion. There is no fitted preprocessing in the vector contract.

The score input has no identities, truth annotations, calendar fields, sanctions flag, outcome or score. The offline join retains such metadata separately where needed for evaluation. Local tests exercise exact replay/prefix parity, missing history, branch suffix isolation, same-time ordering and byte validation. The PostgreSQL smoke must still verify the runtime path. No scorer or model pin is available yet for R6.
