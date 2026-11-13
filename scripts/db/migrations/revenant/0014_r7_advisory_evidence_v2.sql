-- R7/2 advisory evidence (policy revenant-ssa-advisory/2). Additive: schema-1 rows remain valid and immutable.
-- anomaly-evidence/2 records the SSA-expected count, native residual, calibrated alert statistic and the optional native
-- p-value diagnostic. liquidity-forecast-evidence/2 records the model representation, causal local scale and the
-- split-conformal level band. Neither enters Screening or any payment decision.
ALTER TABLE revenant.v4_advisory_evidence DROP CONSTRAINT v4_advisory_evidence_schema_id_check;
ALTER TABLE revenant.v4_advisory_evidence ADD CONSTRAINT v4_advisory_evidence_schema_id_check CHECK (schema_id IN (
  'anomaly-evidence/1', 'liquidity-forecast-evidence/1', 'anomaly-evidence/2', 'liquidity-forecast-evidence/2'));
