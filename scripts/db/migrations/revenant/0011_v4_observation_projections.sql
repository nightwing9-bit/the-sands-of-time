-- Derived materializations are immutable and tenant-scoped. A child owns its rows even when a seal is inherited.
DO $$
DECLARE relation_name text;
DECLARE entity_column text;
BEGIN
  FOR relation_name, entity_column IN VALUES
      ('v4_bank_observation', 'bank_id'), ('v4_account_activity_observation', 'account_id')
  LOOP
    EXECUTE format($ddl$
      CREATE TABLE revenant.%I (
        tenant_id text NOT NULL,
        execution_run_id uuid NOT NULL,
        %I text NOT NULL CHECK (%I <> ''),
        boundary_ms bigint NOT NULL CHECK (boundary_ms %% 300000 = 0),
        schema_id text NOT NULL CHECK (schema_id <> ''),
        seal_fact_seq bigint NOT NULL CHECK (seal_fact_seq > 0),
        sealed_prefix_digest bytea NOT NULL CHECK (octet_length(sealed_prefix_digest) = 32),
        canonical bytea NOT NULL CHECK (octet_length(canonical) > 0),
        projection_digest bytea NOT NULL CHECK (projection_digest = sha256(canonical)),
        PRIMARY KEY (execution_run_id, %I, boundary_ms, schema_id),
        FOREIGN KEY (execution_run_id, tenant_id) REFERENCES revenant.scenario_run(id, tenant_id)
      )
    $ddl$, relation_name, entity_column, entity_column, entity_column);
    EXECUTE format('ALTER TABLE revenant.%I ENABLE ROW LEVEL SECURITY', relation_name);
    EXECUTE format('ALTER TABLE revenant.%I FORCE ROW LEVEL SECURITY', relation_name);
    EXECUTE format($policy$
      CREATE POLICY projection_tenant ON revenant.%I TO revenant_ledger_writer, revenant_replay_reader
      USING (tenant_id = current_setting('revenant.tenant_id', true))
      WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true))
    $policy$, relation_name);
    EXECUTE format('REVOKE ALL ON revenant.%I FROM PUBLIC', relation_name);
    EXECUTE format('GRANT SELECT, INSERT ON revenant.%I TO revenant_ledger_writer', relation_name);
    EXECUTE format('GRANT SELECT ON revenant.%I TO revenant_replay_reader', relation_name);
  END LOOP;
END $$;
