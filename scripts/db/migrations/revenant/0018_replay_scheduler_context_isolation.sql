-- The scheduler retains the same canonical V4 context bytes as the Marrow artifact.
-- Preserve 0017's checksum and close this second read path for the dedicated Replay login.
CREATE FUNCTION revenant.replay_scheduler_context_allowed(scheduler_run uuid, scheduler_tenant text)
RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
  SELECT CASE WHEN NOT pg_has_role(session_user, 'marrow_timeline_revenant_replay', 'MEMBER')
      AND NOT pg_has_role(session_user, 'revenant_counterfactual_worker', 'MEMBER') THEN true
    ELSE scheduler_tenant = revenant.counterfactual_session_tenant() AND EXISTS (
      SELECT 1 FROM revenant.counterfactual_run j
      JOIN revenant.v4_branch_execution b ON b.execution_run_id = j.source_run_id
        AND b.tenant_id = j.tenant_id AND b.timeline_id = j.source_timeline_id
      JOIN marrow_timeline.histories h ON h.timeline_id = j.source_timeline_id
      WHERE j.source_run_id = scheduler_run AND j.tenant_id = scheduler_tenant
        AND j.status IN ('Pending', 'Running', 'Verified')
        AND j.request->>'alternativePolicy' = 'policy-v8'
        AND h.domain_id = 'revenant-finance' AND h.reducer_version = 'revenant-finance-reducer/4'
        AND h.context_schema_version = 'revenant-finance-context/4' AND h.status = 'Verified'
    ) END
$$;
REVOKE ALL ON FUNCTION revenant.replay_scheduler_context_allowed(uuid, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION revenant.replay_scheduler_context_allowed(uuid, text)
  TO revenant_ledger_writer, revenant_replay_reader;

CREATE POLICY replay_scheduler_context_read ON revenant.v4_scheduler_run AS RESTRICTIVE FOR SELECT
  USING (revenant.replay_scheduler_context_allowed(run_id, tenant_id));
