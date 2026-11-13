CREATE TABLE revenant.v4_advisory_evidence (
  tenant_id text NOT NULL,
  execution_run_id uuid NOT NULL,
  subject_id text NOT NULL CHECK (subject_id <> ''),
  boundary_ms bigint NOT NULL CHECK (boundary_ms % 300000 = 0),
  schema_id text NOT NULL CHECK (schema_id IN ('anomaly-evidence/1', 'liquidity-forecast-evidence/1')),
  model_id text NOT NULL CHECK (model_id <> ''),
  policy_id text NOT NULL CHECK (policy_id <> ''),
  seal_fact_seq bigint NOT NULL CHECK (seal_fact_seq > 0),
  prefix_digest bytea NOT NULL CHECK (octet_length(prefix_digest) = 32),
  canonical bytea NOT NULL CHECK (octet_length(canonical) > 0),
  evidence_digest bytea NOT NULL CHECK (evidence_digest = sha256(canonical)),
  PRIMARY KEY (execution_run_id, subject_id, boundary_ms, schema_id, model_id, policy_id),
  FOREIGN KEY (execution_run_id, tenant_id) REFERENCES revenant.scenario_run(id, tenant_id)
);
ALTER TABLE revenant.v4_advisory_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.v4_advisory_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY advisory_tenant ON revenant.v4_advisory_evidence TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
REVOKE ALL ON revenant.v4_advisory_evidence FROM PUBLIC;
GRANT SELECT, INSERT ON revenant.v4_advisory_evidence TO revenant_ledger_writer;
GRANT SELECT ON revenant.v4_advisory_evidence TO revenant_replay_reader;
