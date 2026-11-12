# R4 observation data contract — checkpoint 1

This candidate introduces the enhanced scenario opening. The bank, account,
pre-screening, truth, dataset and target contracts are still pending the native
opening gate. There is no consumer dataset in this revision.

## Enhanced opening

- Scenario: `revenant-behavioral-episodes/1`. Generator contract pin:
  `revenant-episode-generator/1`. This revision implements the population and
  opening, with a bounded command plan in the integration fixture.
- Catalog: BANK-A Aster Bank, BANK-B Bramble Trust, BANK-C Cinder Savings,
  BANK-D Dune Credit, BANK-E Ember Bank and BANK-F Fjord Union.
- Population: callers supply six counts, each 2–1,000. The manifest pins seed,
  each logical account and bank, and USD posted opening capital. Initial reserves
  are zero. The population stream is deterministic SplitMix64 stream
  `0x52563401`; account keys use the episode seed and bank/account ordinal.
- The aligned UTC origin and duration are Unix milliseconds and milliseconds,
  respectively. The candidate fixture uses seed 411, origin 2026-01-12T00:00:00Z,
  a two-hour duration and populations 2/3/4/2/3/4.
- The V4 canonical wrapper `revenant-behavioral-episode-manifest/1` hashes
  format Int32 1, wrapper ID, existing canonical manifest content as a byte
  field, generator contract, duration Int64, the four v7 policy reference
  strings, review version, and counted command kinds in order: PaymentIntent,
  ReviewDecision, ReturnInstruction, Screening, Settlement. The root pins
  that hash and the rules-only v7 catalog. The wrapper does not change any
  historical encoding or the V4 context wire format.
- Source metadata uses the existing `revenant-features/1` schema for
  `newBeneficiary` and `sanctionsHit`. The fixture uses the fixed legacy
  `SyntheticTruth.Legitimate` value on every input. It has no hidden labels.
  The future offline truth sidecar must treat v7/v8 decisions as financial
  outcomes, never as truth labels.

`BehavioralEpisodeScenario.Create` returns a manifest and a matching genesis.
The V4 opener validates the manifest content, origin and all opening accounts
before invoking the existing ledger opening. The V4-only adapter supplies the
existing financial transitions with the enhanced scenario pins. Legacy
V2 pinning still rejects the enhanced scenario, and the existing root and fork
policy rules still apply.

## Downstream contract gates

R5 is waiting for the typed `fraud-asof-observation/1` reader, a separate truth
sidecar, stable join keys and independent cohort evidence. The required cutoff
is the fact immediately before Screening. Current outcome and any subsequent
same-timestamp facts are outside the prefix.

R7 is waiting for `bank-observation/1`, `account-activity-observation/1`,
`bank-eight-horizon-target/1`, and anomaly truth. The required interval is
`[boundary - 300000, boundary)`, bound to the post-seal digest and physical
execution ownership. An inactive account can have a covered zero interval;
a missing future seal must yield an uncovered target. These requirements
describe subsequent checkpoints and are not implemented here.
