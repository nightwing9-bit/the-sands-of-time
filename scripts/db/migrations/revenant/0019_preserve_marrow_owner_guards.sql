-- PostgreSQL treats a superuser as having every role for pg_has_role().
-- Let the original Marrow guards handle owner operations; restricted login checks remain unchanged.
CREATE OR REPLACE FUNCTION marrow_timeline.guard_revenant_replay_history() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND rolsuper)
    OR NOT pg_has_role(current_user, 'marrow_timeline_revenant_replay', 'USAGE') THEN
    IF TG_OP = 'UPDATE' AND NEW.revenant_replay_tenant IS DISTINCT FROM OLD.revenant_replay_tenant THEN
      RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Replay tenant is immutable';
    END IF;
    RETURN NEW;
  END IF;
  IF TG_OP = 'UPDATE' THEN
    IF OLD.revenant_replay_tenant IS NULL THEN
      RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Replay cannot mutate source histories';
    END IF;
  ELSE
    NEW.revenant_replay_tenant := nullif(current_setting('revenant.tenant_id', true), '');
    IF NEW.revenant_replay_tenant IS NULL OR NEW.domain_id <> 'revenant-finance' OR NEW.reducer_version <> 'revenant-finance-reducer/4'
       OR NOT EXISTS (SELECT 1 FROM marrow_timeline.revenant_replay_source s WHERE s.timeline_id = NEW.parent_timeline_id) THEN
      RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Replay may only create authorized V4 children';
    END IF;
  END IF;
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION revenant.guard_replay_child_context() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = session_user AND rolsuper) THEN
    RETURN NEW;
  END IF;
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
