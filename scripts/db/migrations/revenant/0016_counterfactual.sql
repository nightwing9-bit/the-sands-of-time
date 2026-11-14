DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'revenant_counterfactual_worker') THEN
    CREATE ROLE revenant_counterfactual_worker NOLOGIN;
  END IF;
END $$;
GRANT USAGE ON SCHEMA revenant TO revenant_counterfactual_worker;

CREATE TABLE revenant.counterfactual_run (
  id uuid PRIMARY KEY,
  tenant_id text NOT NULL,
  actor_id text NOT NULL,
  source_run_id uuid NOT NULL,
  source_timeline_id uuid NOT NULL,
  operation_key text NOT NULL,
  request jsonb NOT NULL,
  status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Running','Verified','Failed','Cancelled')),
  result jsonb,
  failure_code text,
  UNIQUE (tenant_id, operation_key),
  FOREIGN KEY (source_run_id, tenant_id) REFERENCES revenant.scenario_run (id, tenant_id),
  CHECK ((status = 'Verified') = (result IS NOT NULL)),
  CHECK ((status = 'Failed') = (failure_code IS NOT NULL))
);
ALTER TABLE revenant.counterfactual_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.counterfactual_run FORCE ROW LEVEL SECURITY;
CREATE POLICY counterfactual_scope ON revenant.counterfactual_run TO revenant_counterfactual_worker, revenant_ledger_writer
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
GRANT SELECT, INSERT ON revenant.counterfactual_run TO revenant_ledger_writer;
GRANT UPDATE (status) ON revenant.counterfactual_run TO revenant_ledger_writer;
GRANT SELECT ON revenant.counterfactual_run TO revenant_counterfactual_worker;
GRANT UPDATE (status, result, failure_code) ON revenant.counterfactual_run TO revenant_counterfactual_worker;

CREATE FUNCTION revenant.counterfactual_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN
    IF (OLD.status, NEW.status) NOT IN (('Pending','Running'),('Pending','Cancelled'),
       ('Running','Verified'),('Running','Failed'),('Running','Cancelled'))
       OR (pg_has_role(session_user, 'revenant_ledger_writer', 'USAGE') AND NEW.status <> 'Cancelled') THEN
      RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Invalid counterfactual status transition';
    END IF;
    IF NEW.status = 'Verified' AND NOT EXISTS (
      SELECT 1 FROM marrow_timeline.histories child
      JOIN marrow_timeline.histories parent ON parent.timeline_id=child.parent_timeline_id
      WHERE child.timeline_id::text=NEW.result->>'childTimelineId'
        AND child.parent_timeline_id=OLD.source_timeline_id AND child.revenant_replay_tenant=OLD.tenant_id
        AND child.operation_key='r8:' || replace(OLD.id::text,'-','') AND child.status='Verified' AND parent.status='Verified'
        AND child.last_record_hash=NEW.result->>'childRecordHash' AND child.latest_state_hash=NEW.result->>'childStateHash'
        AND parent.last_record_hash=NEW.result->>'sourceRecordHash' AND parent.latest_state_hash=NEW.result->>'sourceStateHash'
        AND child.base_state_hash=NEW.result->>'forkStateHash'
        AND child.fork_position::text=OLD.request->>'forkPosition'
        AND NEW.result#>>'{economics,evidenceDigest}' ~ '^[0-9a-f]{64}$'
        AND NEW.result#>>'{economics,horizonMs}'=OLD.request->>'horizonMs'
        AND NEW.result->>'alternativePolicy'=OLD.request->>'alternativePolicy') THEN
      RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Verified publication requires the matching completed source and child';
    END IF;
    RETURN NEW;
  END IF;

  IF NEW.status <> 'Pending' OR NOT EXISTS (
    SELECT 1 FROM revenant.v4_branch_execution b
    JOIN revenant.run_cursor c ON c.scenario_run_id = b.execution_run_id AND c.tenant_id = b.tenant_id
    JOIN revenant.scenario_run_staff s ON s.scenario_run_id = b.execution_run_id AND s.subject_id = NEW.actor_id
    WHERE b.execution_run_id = NEW.source_run_id AND b.tenant_id = NEW.tenant_id
      AND b.timeline_id = NEW.source_timeline_id AND b.source_execution_run_id IS NULL AND c.closed_fact_seq IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM revenant.scenario_run_staff_revocation r
        WHERE r.scenario_run_id = s.scenario_run_id AND r.subject_id = s.subject_id)) THEN
    RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Counterfactual source is not an authorized closed V4 root';
  END IF;
  INSERT INTO marrow_timeline.revenant_replay_source (timeline_id, tenant_id)
    VALUES (NEW.source_timeline_id, NEW.tenant_id) ON CONFLICT DO NOTHING;
  RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION revenant.counterfactual_guard() FROM PUBLIC;
CREATE TRIGGER counterfactual_guard BEFORE INSERT OR UPDATE ON revenant.counterfactual_run
  FOR EACH ROW EXECUTE FUNCTION revenant.counterfactual_guard();

-- The synthetic scenario producer supplies retrospective annotations separately from finance inputs.
CREATE TABLE revenant.counterfactual_truth (
  tenant_id text NOT NULL,
  source_run_id uuid NOT NULL,
  intent_id text NOT NULL,
  malicious boolean NOT NULL,
  PRIMARY KEY (source_run_id, intent_id),
  FOREIGN KEY (source_run_id, tenant_id) REFERENCES revenant.scenario_run (id, tenant_id)
);
ALTER TABLE revenant.counterfactual_truth ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.counterfactual_truth FORCE ROW LEVEL SECURITY;
CREATE POLICY truth_record ON revenant.counterfactual_truth FOR INSERT TO revenant_ledger_writer
  WITH CHECK (tenant_id=current_setting('revenant.tenant_id', true));
CREATE POLICY truth_read ON revenant.counterfactual_truth FOR SELECT TO revenant_counterfactual_worker
  USING (tenant_id=current_setting('revenant.tenant_id', true));
GRANT INSERT ON revenant.counterfactual_truth TO revenant_ledger_writer;
GRANT SELECT ON revenant.counterfactual_truth TO revenant_counterfactual_worker;

-- A replay LOGIN is provisioned for one tenant. The mutable request GUC is only
-- a scope selector; changing it cannot expand the login's database authority.
CREATE TABLE revenant.counterfactual_worker_tenant (
  login_name name PRIMARY KEY,
  tenant_id text NOT NULL CHECK (tenant_id ~ '^[A-Za-z0-9_.-]+$')
);
REVOKE ALL ON revenant.counterfactual_worker_tenant FROM PUBLIC;
CREATE FUNCTION revenant.counterfactual_session_tenant() RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog
RETURN (SELECT tenant_id FROM revenant.counterfactual_worker_tenant WHERE login_name=session_user);
REVOKE ALL ON FUNCTION revenant.counterfactual_session_tenant() FROM PUBLIC;
CREATE FUNCTION revenant.counterfactual_tenant_allows(requested text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog
RETURN NOT pg_has_role(session_user, 'revenant_counterfactual_worker', 'USAGE')
  OR requested = revenant.counterfactual_session_tenant();
GRANT EXECUTE ON FUNCTION revenant.counterfactual_session_tenant() TO revenant_counterfactual_worker, marrow_timeline_revenant_replay;
REVOKE ALL ON FUNCTION revenant.counterfactual_tenant_allows(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION revenant.counterfactual_tenant_allows(text) TO revenant_ledger_writer, revenant_replay_reader,
  revenant_counterfactual_worker, revenant_read_projector, revenant_outbox_relay;
CREATE POLICY counterfactual_bound_login ON revenant.counterfactual_run AS RESTRICTIVE TO revenant_counterfactual_worker
  USING (tenant_id = revenant.counterfactual_session_tenant()) WITH CHECK (tenant_id = revenant.counterfactual_session_tenant());
CREATE POLICY truth_bound_login ON revenant.counterfactual_truth AS RESTRICTIVE TO revenant_counterfactual_worker
  USING (tenant_id = revenant.counterfactual_session_tenant());
CREATE POLICY replay_source_bound_login ON marrow_timeline.revenant_replay_source AS RESTRICTIVE TO marrow_timeline_revenant_replay
  USING (tenant_id = revenant.counterfactual_session_tenant());
CREATE POLICY replay_history_bound_login ON marrow_timeline.histories AS RESTRICTIVE TO marrow_timeline_revenant_replay
  USING (revenant_replay_tenant = revenant.counterfactual_session_tenant()
    OR EXISTS (SELECT 1 FROM marrow_timeline.revenant_replay_source s WHERE s.timeline_id = histories.timeline_id))
  WITH CHECK (revenant_replay_tenant = revenant.counterfactual_session_tenant());

-- Apply the same immutable limit to the already tenant-scoped finance tables granted
-- to the existing reader role. Identity-only directories have separate policies below.
DO $$
DECLARE row record;
BEGIN
  FOR row IN
    SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    JOIN pg_attribute a ON a.attrelid=c.oid AND a.attname='tenant_id' AND NOT a.attisdropped
    WHERE n.nspname='revenant' AND c.relrowsecurity AND has_table_privilege('revenant_replay_reader',c.oid,'SELECT')
  LOOP
    EXECUTE format('CREATE POLICY replay_bound_login ON revenant.%I AS RESTRICTIVE TO PUBLIC
      USING (revenant.counterfactual_tenant_allows(tenant_id))', row.relname);
  END LOOP;
END $$;

-- Older directory tables were intentionally not RLS-scoped. Preserve existing
-- roles' behavior and enforce the dedicated replay login's tenant on these too.
ALTER TABLE revenant.scenario_run ENABLE ROW LEVEL SECURITY;
CREATE POLICY run_legacy_access ON revenant.scenario_run TO revenant_ledger_writer, revenant_replay_reader USING (true) WITH CHECK (true);
CREATE POLICY run_replay_tenant ON revenant.scenario_run AS RESTRICTIVE TO PUBLIC
  USING (revenant.counterfactual_tenant_allows(tenant_id));
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['scenario_run_staff','scenario_run_staff_revocation'] LOOP
    EXECUTE format('ALTER TABLE revenant.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY legacy_access ON revenant.%I TO revenant_ledger_writer, revenant_replay_reader USING (true) WITH CHECK (true)', t);
    EXECUTE format('CREATE POLICY replay_tenant ON revenant.%I AS RESTRICTIVE TO PUBLIC
      USING (NOT pg_has_role(session_user, ''revenant_counterfactual_worker'', ''USAGE'')
        OR EXISTS (SELECT 1 FROM revenant.scenario_run r WHERE r.id=%I.scenario_run_id))', t, t);
  END LOOP;
END $$;

ALTER TABLE revenant.audit_head ENABLE ROW LEVEL SECURITY;
CREATE POLICY audit_head_legacy_access ON revenant.audit_head TO revenant_ledger_writer, revenant_replay_reader
  USING (true) WITH CHECK (true);
CREATE POLICY audit_head_replay_tenant ON revenant.audit_head AS RESTRICTIVE TO PUBLIC
  USING (NOT pg_has_role(session_user, 'revenant_counterfactual_worker', 'USAGE')
    OR EXISTS (SELECT 1 FROM revenant.scenario_run r WHERE r.id=audit_head.scenario_run_id));
