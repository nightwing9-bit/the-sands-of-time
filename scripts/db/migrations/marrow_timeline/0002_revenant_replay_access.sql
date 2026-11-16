-- Replay may lock an authorized source while forking, but cannot mutate it.
-- Existing runtime policy preserves the permissions of LiveOps and Ghost Lap.
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'marrow_timeline_revenant_replay') THEN
    CREATE ROLE marrow_timeline_revenant_replay NOLOGIN;
  END IF;
END $$;

GRANT USAGE ON SCHEMA marrow_timeline TO marrow_timeline_revenant_replay;
GRANT SELECT, INSERT ON marrow_timeline.context_artifacts, marrow_timeline.histories,
  marrow_timeline.checkpoints, marrow_timeline.steps, marrow_timeline.step_inputs TO marrow_timeline_revenant_replay;
GRANT UPDATE (last_position, latest_state_hash, last_record_hash, status, failure_reason, updated_at)
  ON marrow_timeline.histories TO marrow_timeline_revenant_replay;

-- Source bindings are provisioned by the owning domain before a replay session starts.
CREATE TABLE marrow_timeline.revenant_replay_source (
  timeline_id uuid PRIMARY KEY REFERENCES marrow_timeline.histories (timeline_id),
  tenant_id text NOT NULL
);
REVOKE ALL ON marrow_timeline.revenant_replay_source FROM PUBLIC;
GRANT SELECT ON marrow_timeline.revenant_replay_source TO marrow_timeline_revenant_replay;
ALTER TABLE marrow_timeline.revenant_replay_source ENABLE ROW LEVEL SECURITY;
CREATE POLICY revenant_replay_source_read ON marrow_timeline.revenant_replay_source FOR SELECT TO marrow_timeline_revenant_replay
  USING (tenant_id = current_setting('revenant.tenant_id', true));

ALTER TABLE marrow_timeline.histories ADD COLUMN revenant_replay_tenant text;
-- PostgreSQL requires an UPDATE grant and visible UPDATE rows for FOR SHARE. Keep rows
-- lockable under RLS, and use a trigger to reject any actual source mutation.
CREATE FUNCTION marrow_timeline.guard_revenant_replay_history() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NOT pg_has_role(current_user, 'marrow_timeline_revenant_replay', 'USAGE') THEN
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
CREATE TRIGGER a_revenant_replay_history_guard BEFORE INSERT OR UPDATE ON marrow_timeline.histories
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.guard_revenant_replay_history();

ALTER TABLE marrow_timeline.histories ENABLE ROW LEVEL SECURITY;
CREATE POLICY histories_runtime ON marrow_timeline.histories TO marrow_timeline_runtime USING (true) WITH CHECK (true);
CREATE POLICY histories_revenant_replay ON marrow_timeline.histories TO marrow_timeline_revenant_replay
  USING (revenant_replay_tenant = current_setting('revenant.tenant_id', true)
    OR EXISTS (SELECT 1 FROM marrow_timeline.revenant_replay_source s WHERE s.timeline_id = histories.timeline_id))
  WITH CHECK (revenant_replay_tenant = current_setting('revenant.tenant_id', true));

-- The runtime may use its existing engine, but replay can only append to a sandbox child.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['checkpoints', 'steps', 'step_inputs'] LOOP
    EXECUTE format('ALTER TABLE marrow_timeline.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY runtime ON marrow_timeline.%I TO marrow_timeline_runtime USING (true) WITH CHECK (true)', t);
    EXECUTE format('CREATE POLICY replay_read ON marrow_timeline.%I FOR SELECT TO marrow_timeline_revenant_replay
      USING (EXISTS (SELECT 1 FROM marrow_timeline.histories h WHERE h.timeline_id = %I.timeline_id))', t, t);
    EXECUTE format('CREATE POLICY replay_append ON marrow_timeline.%I FOR INSERT TO marrow_timeline_revenant_replay
      WITH CHECK (EXISTS (SELECT 1 FROM marrow_timeline.histories h WHERE h.timeline_id = %I.timeline_id
        AND h.revenant_replay_tenant = current_setting(''revenant.tenant_id'', true)))', t, t);
  END LOOP;
END $$;
