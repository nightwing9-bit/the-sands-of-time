-- Revenant owns the job bindings used here. Apply after 0016, without changing Marrow records or hashes.
-- Derive only the existing V4 policy-v8 continuation from the authoritative, pinned V4 source bytes.
CREATE FUNCTION revenant.v4_replay_child_context_hash(source_bytes bytea) RETURNS text
LANGUAGE plpgsql IMMUTABLE STRICT SET search_path = pg_catalog AS $$
DECLARE
  cursor_at integer := 4;
  field_length integer;
  child_bytes bytea;
  v7 bytea := revenant.v4_canonical_string('policy-v7')
    || revenant.v4_canonical_string('sha256:687078f26fefcf081ee0f39e5ff185385c11130b52198e74c3861f7347b5c023');
  v8 bytea := revenant.v4_canonical_string('policy-v8')
    || revenant.v4_canonical_string('sha256:b702047a23f2e5e02efc44ef1354aec91e913159f35b69f65b112612f1cbafbd');
BEGIN
  IF substring(source_bytes FROM 1 FOR 4) <> int4send(1) THEN RETURN NULL; END IF;
  -- Reducer, scenario, manifest hash, scheduler and observation fields are length-prefixed.
  FOR field IN 1..5 LOOP
    IF cursor_at > octet_length(source_bytes) - 4 OR get_byte(source_bytes, cursor_at) <> 0 THEN
      RETURN NULL;
    END IF;
    field_length := get_byte(source_bytes, cursor_at + 1) * 65536
                  + get_byte(source_bytes, cursor_at + 2) * 256
                  + get_byte(source_bytes, cursor_at + 3);
    cursor_at := cursor_at + 4 + field_length;
  END LOOP;
  cursor_at := cursor_at + 16; -- Origin and cadence, both int64.
  IF substring(source_bytes FROM cursor_at + 1 FOR octet_length(v7)) <> v7 THEN RETURN NULL; END IF;
  -- Catalog v7 and v8 share their model and feature schema pins. Preserve every remaining byte.
  child_bytes := overlay(source_bytes PLACING v8 FROM cursor_at + 1 FOR octet_length(v7));
  RETURN encode(sha256(revenant.v4_canonical_string('revenant-finance-context/4')
    || int4send(octet_length(child_bytes)) || child_bytes), 'hex');
END $$;
REVOKE ALL ON FUNCTION revenant.v4_replay_child_context_hash(bytea) FROM PUBLIC;

-- SECURITY DEFINER can resolve existing job and history bindings without recursively applying their RLS.
-- The provisioned session login, not current_user or a caller-controlled GUC, fixes the tenant.
CREATE FUNCTION revenant.replay_context_authorized(artifact_ref text, artifact_schema text, artifact_hash text,
    artifact_bytes bytea, writing boolean) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  WITH RECURSIVE jobs AS (
    SELECT job.id, job.tenant_id, job.source_timeline_id, job.status,
      job.request->>'forkPosition' AS fork_position, h.context_ref,
      'sha256:' || revenant.v4_replay_child_context_hash(s.canonical_context) AS child_context_ref
    FROM revenant.counterfactual_run job
    JOIN revenant.v4_branch_execution b ON b.execution_run_id = job.source_run_id
      AND b.tenant_id = job.tenant_id AND b.timeline_id = job.source_timeline_id
    JOIN revenant.v4_scheduler_run s ON s.run_id = job.source_run_id AND s.tenant_id = job.tenant_id
    JOIN marrow_timeline.histories h ON h.timeline_id = job.source_timeline_id
    WHERE job.tenant_id = revenant.counterfactual_session_tenant()
      AND job.status IN ('Pending', 'Running', 'Verified')
      AND job.request->>'alternativePolicy' = 'policy-v8'
      AND h.domain_id = 'revenant-finance' AND h.reducer_version = 'revenant-finance-reducer/4'
      AND h.context_schema_version = 'revenant-finance-context/4' AND h.status = 'Verified'
      AND h.context_hash = encode(sha256(revenant.v4_canonical_string(h.context_schema_version)
        || int4send(octet_length(s.canonical_context)) || s.canonical_context), 'hex')
  ), ancestry AS (
    SELECT h.timeline_id, h.parent_timeline_id, h.context_ref
    FROM jobs j JOIN marrow_timeline.histories h ON h.timeline_id = j.source_timeline_id
    UNION
    SELECT h.timeline_id, h.parent_timeline_id, h.context_ref
    FROM ancestry a JOIN marrow_timeline.histories h ON h.timeline_id = a.parent_timeline_id
  )
  SELECT CASE WHEN NOT pg_has_role(session_user, 'marrow_timeline_revenant_replay', 'MEMBER')
    AND NOT pg_has_role(session_user, 'revenant_counterfactual_worker', 'MEMBER') THEN true
  WHEN writing THEN
    artifact_schema = 'revenant-finance-context/4'
    AND artifact_hash = encode(sha256(revenant.v4_canonical_string(artifact_schema)
      || int4send(octet_length(artifact_bytes)) || artifact_bytes), 'hex')
    AND EXISTS (SELECT 1 FROM jobs j WHERE j.child_context_ref = artifact_ref AND j.status = 'Running')
  ELSE EXISTS (SELECT 1 FROM ancestry a WHERE a.context_ref = artifact_ref)
    OR EXISTS (
      SELECT 1 FROM jobs j WHERE j.child_context_ref = artifact_ref AND (
        j.status = 'Running' OR EXISTS (
          SELECT 1 FROM marrow_timeline.histories h
          WHERE h.parent_timeline_id = j.source_timeline_id AND h.revenant_replay_tenant = j.tenant_id
            AND h.operation_key = 'r8:' || replace(j.id::text, '-', '')
            AND h.fork_position::text = j.fork_position AND h.context_ref = j.child_context_ref
            AND h.status = 'Verified'
        )
      )
    )
  END
$$;
REVOKE ALL ON FUNCTION revenant.replay_context_authorized(text, text, text, bytea, boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION revenant.replay_context_authorized(text, text, text, bytea, boolean)
  TO marrow_timeline_runtime, marrow_timeline_revenant_replay;

ALTER TABLE marrow_timeline.context_artifacts ENABLE ROW LEVEL SECURITY;
CREATE POLICY context_runtime_access ON marrow_timeline.context_artifacts
  TO marrow_timeline_runtime, marrow_timeline_revenant_replay USING (true) WITH CHECK (true);
CREATE POLICY context_replay_read ON marrow_timeline.context_artifacts AS RESTRICTIVE FOR SELECT
  USING (revenant.replay_context_authorized(context_ref, context_schema_version, context_hash, NULL, false));
CREATE POLICY context_replay_insert ON marrow_timeline.context_artifacts AS RESTRICTIVE FOR INSERT
  WITH CHECK (revenant.replay_context_authorized(context_ref, context_schema_version, context_hash, canonical_bytes, true));

-- Tie the child history itself to the same authorized job, position and context.
CREATE FUNCTION revenant.guard_replay_child_context() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
  IF (pg_has_role(session_user, 'marrow_timeline_revenant_replay', 'MEMBER')
      OR pg_has_role(session_user, 'revenant_counterfactual_worker', 'MEMBER'))
    AND NOT EXISTS (
      SELECT 1 FROM revenant.counterfactual_run j
      JOIN revenant.v4_scheduler_run s ON s.run_id = j.source_run_id AND s.tenant_id = j.tenant_id
      JOIN marrow_timeline.histories parent ON parent.timeline_id = j.source_timeline_id
      WHERE j.tenant_id = revenant.counterfactual_session_tenant()
        AND j.tenant_id = NEW.revenant_replay_tenant AND j.status = 'Running'
        AND j.source_timeline_id = NEW.parent_timeline_id AND j.request->>'alternativePolicy' = 'policy-v8'
        AND NEW.operation_key = 'r8:' || replace(j.id::text, '-', '')
        AND NEW.fork_position::text = j.request->>'forkPosition'
        AND NEW.context_schema_version = 'revenant-finance-context/4'
        AND NEW.context_hash = revenant.v4_replay_child_context_hash(s.canonical_context)
        AND parent.context_hash = encode(sha256(revenant.v4_canonical_string(parent.context_schema_version)
          || int4send(octet_length(s.canonical_context)) || s.canonical_context), 'hex')
    ) THEN
      RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Replay child context is not authorized by its job';
  END IF;
  RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION revenant.guard_replay_child_context() FROM PUBLIC;
-- The existing a_revenant_replay_history_guard stamps the tenant first.
CREATE TRIGGER b_revenant_replay_child_context BEFORE INSERT ON marrow_timeline.histories
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_replay_child_context();
