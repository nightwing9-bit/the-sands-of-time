-- Add the statistical persistence projection; historical learned forecast rows keep their schemas and bytes.
-- For this schema only, model_id stores the versioned method ID, with no artifact digest in the canonical payload.
ALTER TABLE revenant.v4_advisory_evidence DROP CONSTRAINT v4_advisory_evidence_schema_id_check;
ALTER TABLE revenant.v4_advisory_evidence ADD CONSTRAINT v4_advisory_evidence_schema_id_check CHECK (schema_id IN (
  'anomaly-evidence/1', 'liquidity-forecast-evidence/1', 'anomaly-evidence/2', 'liquidity-forecast-evidence/2',
  'liquidity-projection-evidence/1'));
