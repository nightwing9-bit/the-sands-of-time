-- V4 execution ownership is separate from the canonical lineage inherited by Marrow.
CREATE TABLE revenant.v4_branch_execution (
  tenant_id text NOT NULL,
  execution_run_id uuid PRIMARY KEY,
  lineage_run_id uuid NOT NULL,
  timeline_id uuid NOT NULL UNIQUE,
  source_execution_run_id uuid,
  source_timeline_id uuid,
  fork_fact_seq bigint CHECK (fork_fact_seq > 0),
  fork_boundary_ms bigint CHECK (fork_boundary_ms % 300000 = 0),
  fork_state_hash bytea CHECK (length(fork_state_hash) = 32),
  fork_prefix_digest bytea CHECK (length(fork_prefix_digest) = 32),
  child_context_hash bytea NOT NULL CHECK (length(child_context_hash) = 32),
  fork_operation_key text CHECK (btrim(fork_operation_key) <> ''),
  readiness_status text NOT NULL CHECK (readiness_status = 'SealOnly'),
  UNIQUE (tenant_id, fork_operation_key),
  UNIQUE (execution_run_id, tenant_id, lineage_run_id, timeline_id),
  FOREIGN KEY (execution_run_id, tenant_id) REFERENCES revenant.scenario_run (id, tenant_id),
  FOREIGN KEY (source_execution_run_id, tenant_id, lineage_run_id, source_timeline_id)
    REFERENCES revenant.v4_branch_execution (execution_run_id, tenant_id, lineage_run_id, timeline_id),
  CHECK (
    (source_execution_run_id IS NULL AND source_timeline_id IS NULL AND fork_fact_seq IS NULL
      AND fork_boundary_ms IS NULL AND fork_state_hash IS NULL AND fork_prefix_digest IS NULL
      AND fork_operation_key IS NULL AND lineage_run_id = execution_run_id)
    OR
    (source_execution_run_id IS NOT NULL AND source_execution_run_id <> execution_run_id
      AND source_timeline_id IS NOT NULL AND source_timeline_id <> timeline_id
      AND lineage_run_id <> execution_run_id
      AND fork_fact_seq IS NOT NULL AND fork_boundary_ms IS NOT NULL AND fork_state_hash IS NOT NULL
      AND fork_prefix_digest IS NOT NULL AND fork_operation_key IS NOT NULL))
);

ALTER TABLE revenant.v4_branch_execution ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.v4_branch_execution FORCE ROW LEVEL SECURITY;
CREATE POLICY v4_branch_tenant ON revenant.v4_branch_execution TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
REVOKE ALL ON revenant.v4_branch_execution FROM PUBLIC;
GRANT SELECT, INSERT ON revenant.v4_branch_execution TO revenant_ledger_writer;
GRANT SELECT ON revenant.v4_branch_execution TO revenant_replay_reader;

CREATE TRIGGER v4_branch_immutable BEFORE UPDATE OR DELETE ON revenant.v4_branch_execution
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER v4_branch_no_truncate BEFORE TRUNCATE ON revenant.v4_branch_execution
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();

-- Check Revenant provenance in the caller's tenant before consulting the Marrow manifest.
CREATE FUNCTION revenant.guard_v4_branch_execution() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.tenant_id IS DISTINCT FROM current_setting('revenant.tenant_id', true)
     OR NOT EXISTS (SELECT 1 FROM revenant.scenario_run
          WHERE id = NEW.execution_run_id AND tenant_id = NEW.tenant_id
            AND finance_version = 'revenant-finance-reducer/4')
     OR EXISTS (SELECT 1 FROM revenant.run_cursor WHERE scenario_run_id = NEW.execution_run_id)
     OR (NEW.source_execution_run_id IS NOT NULL AND NOT EXISTS (
          SELECT 1 FROM revenant.v4_branch_execution b
          JOIN revenant.run_cursor c ON c.scenario_run_id = b.execution_run_id AND c.tenant_id = b.tenant_id
          JOIN revenant.finance_fact f ON f.scenario_run_id = b.execution_run_id AND f.tenant_id = b.tenant_id
          JOIN revenant.scenario_run parent ON parent.id = b.execution_run_id AND parent.tenant_id = b.tenant_id
          JOIN revenant.scenario_run child ON child.id = NEW.execution_run_id AND child.tenant_id = NEW.tenant_id
          WHERE b.execution_run_id = NEW.source_execution_run_id AND b.tenant_id = NEW.tenant_id
            AND b.lineage_run_id = NEW.lineage_run_id AND b.timeline_id = NEW.source_timeline_id
            AND c.closed_fact_seq IS NOT NULL AND f.kind = 'ObservationSeal'
            AND f.fact_seq = NEW.fork_fact_seq AND f.effective_time_ms = NEW.fork_boundary_ms
            AND f.v4_prefix_digest = NEW.fork_prefix_digest
            AND (parent.owner_subject_id, parent.seed, parent.scenario_version, parent.manifest_hash) =
                (child.owner_subject_id, child.seed, child.scenario_version, child.manifest_hash))) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'Invalid V4 execution or source seal binding';
  END IF;
  RETURN NEW;
END
$$;

-- Marrow history cannot be deleted or truncated. Verify the reference at registration
-- while preserving module-local foreign keys and the existing writer privileges.
CREATE FUNCTION revenant.guard_v4_marrow_origin() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM marrow_timeline.histories h
    JOIN marrow_timeline.checkpoints k ON k.timeline_id = h.timeline_id AND k.position = h.base_position
    LEFT JOIN marrow_timeline.histories p ON p.timeline_id = h.parent_timeline_id
    LEFT JOIN marrow_timeline.steps step ON step.timeline_id = p.timeline_id AND step.position = h.fork_position
    WHERE h.timeline_id = NEW.timeline_id
      AND (h.domain_id, h.reducer_version, h.input_schema_version, h.state_schema_version, h.context_schema_version) =
          ('revenant-finance', 'revenant-finance-reducer/4', 'revenant-finance-fact/4',
           'revenant-finance-state/4', 'revenant-finance-context/4')
      AND h.context_hash = encode(NEW.child_context_hash, 'hex')
      -- V4 format tag, ledger blob length and ledger format tag precede the lineage UUID.
      AND substring(k.state_bytes FROM 13 FOR 16) = uuid_send(NEW.lineage_run_id)
      AND h.parent_timeline_id IS NOT DISTINCT FROM NEW.source_timeline_id
      AND h.fork_position IS NOT DISTINCT FROM NEW.fork_fact_seq
      AND (NEW.source_timeline_id IS NULL OR (
        p.status = 'Verified' AND h.operation_key = NEW.fork_operation_key
        AND h.base_state_hash = encode(NEW.fork_state_hash, 'hex')
        AND k.state_hash = h.base_state_hash AND step.state_hash = h.base_state_hash
        AND sha256(revenant.v4_canonical_string(h.state_schema_version) || int4send(length(k.state_bytes)) || k.state_bytes) = NEW.fork_state_hash
        -- The last 52 bytes are effective time, sealed boundary and the length-prefixed digest.
        AND substring(k.state_bytes FROM length(k.state_bytes) - 51) =
            int8send(NEW.fork_boundary_ms) || int8send(NEW.fork_boundary_ms) || int4send(32) || NEW.fork_prefix_digest
      ))
  ) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 binding must match its Marrow origin';
  END IF;
  RETURN NEW;
END
$$;

CREATE TRIGGER v4_branch_01_execution BEFORE INSERT ON revenant.v4_branch_execution
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_v4_branch_execution();
CREATE TRIGGER v4_branch_02_marrow BEFORE INSERT ON revenant.v4_branch_execution
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_v4_marrow_origin();

-- Unbound R2 test runs retain their scheduler journal. The new binding can advance seals at this checkpoint.
CREATE FUNCTION revenant.guard_v4_bound_command() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM revenant.v4_branch_execution
    WHERE execution_run_id = NEW.run_id AND tenant_id = NEW.tenant_id AND readiness_status = 'SealOnly') THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 binding is ready for seals only';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER v4_command_bound_readiness BEFORE INSERT OR UPDATE ON revenant.v4_scheduled_command
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_v4_bound_command();

REVOKE ALL ON FUNCTION revenant.guard_v4_branch_execution(), revenant.guard_v4_marrow_origin(),
  revenant.guard_v4_bound_command() FROM PUBLIC;

CREATE OR REPLACE FUNCTION revenant.guard_v4_scheduler() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  c revenant.v4_scheduled_command%ROWTYPE;
  b revenant.v4_branch_execution%ROWTYPE;
  start_ms bigint;
BEGIN
  IF TG_OP = 'INSERT' THEN
    SELECT * INTO b FROM revenant.v4_branch_execution
      WHERE execution_run_id = NEW.run_id AND tenant_id = NEW.tenant_id;
    start_ms := coalesce(b.fork_boundary_ms, NEW.origin_ms);
    IF (b.execution_run_id IS NOT NULL AND b.child_context_hash <> sha256(
          revenant.v4_canonical_string('revenant-finance-context/4')
          || int4send(length(NEW.canonical_context)) || NEW.canonical_context))
       OR (b.source_execution_run_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM revenant.v4_scheduler_run parent
             WHERE parent.run_id = b.source_execution_run_id AND parent.tenant_id = NEW.tenant_id
               AND parent.origin_ms = NEW.origin_ms))
       OR NEW.status <> 'Open' OR NEW.watermark_exclusive_ms <> start_ms OR NEW.clock_ms <> start_ms
       OR NEW.next_command_seq <> 1 OR NEW.last_terminal_command_seq <> 0
       OR NEW.command_completion_digest <> sha256(revenant.v4_canonical_string('revenant-v4-command-completion/1') || uuid_send(NEW.run_id))
       OR NOT EXISTS (SELECT 1 FROM revenant.scenario_run r JOIN revenant.run_cursor k ON k.scenario_run_id = r.id
                      WHERE r.id = NEW.run_id AND r.tenant_id = NEW.tenant_id AND r.finance_version = 'revenant-finance-reducer/4'
                        AND k.last_fact_seq = coalesce(b.fork_fact_seq, 0) AND k.last_intent_sequence = 0
                        AND k.last_event_time_ms = start_ms AND k.closed_fact_seq IS NULL) THEN
      RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'Invalid V4 scheduler genesis';
    END IF;
    RETURN NEW;
  END IF;

  IF (NEW.run_id, NEW.tenant_id, NEW.origin_ms, NEW.status, NEW.scheduler_contract_id, NEW.canonical_context)
    IS DISTINCT FROM (OLD.run_id, OLD.tenant_id, OLD.origin_ms, OLD.status, OLD.scheduler_contract_id, OLD.canonical_context)
    OR OLD.status <> 'Open' THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 scheduler identity and context are immutable';
  END IF;

  IF NEW.watermark_exclusive_ms <> OLD.watermark_exclusive_ms THEN
    IF NEW.watermark_exclusive_ms::numeric <> OLD.watermark_exclusive_ms::numeric + 300000
       OR NEW.clock_ms <> NEW.watermark_exclusive_ms OR NEW.next_command_seq <> OLD.next_command_seq
       OR NEW.last_terminal_command_seq <> OLD.last_terminal_command_seq
       OR NEW.command_completion_digest <> OLD.command_completion_digest
       OR NOT EXISTS (SELECT 1 FROM revenant.finance_fact f
                      WHERE f.scenario_run_id = NEW.run_id AND f.kind = 'ObservationSeal'
                        AND f.effective_time_ms = NEW.watermark_exclusive_ms) THEN
      RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 watermark must advance with its next canonical seal';
    END IF;
  ELSIF NEW.next_command_seq <> OLD.next_command_seq THEN
    IF NEW.next_command_seq::numeric <> OLD.next_command_seq::numeric + 1 OR NEW.clock_ms <> OLD.clock_ms
       OR NEW.last_terminal_command_seq <> OLD.last_terminal_command_seq
       OR NEW.command_completion_digest <> OLD.command_completion_digest
       OR NOT EXISTS (SELECT 1 FROM revenant.v4_scheduled_command WHERE run_id = NEW.run_id AND command_seq = OLD.next_command_seq) THEN
      RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 admission must advance the command sequence by one';
    END IF;
  ELSE
    SELECT * INTO c FROM revenant.v4_scheduled_command
     WHERE run_id = NEW.run_id AND status IN ('RetryableBlocked', 'Completed', 'Cancelled', 'FailedPermanent')
     ORDER BY due_time_ms DESC, command_seq DESC LIMIT 1;
    IF NOT FOUND OR NEW.clock_ms <> c.due_time_ms OR NEW.clock_ms < OLD.clock_ms
       OR (CASE WHEN c.status = 'RetryableBlocked' THEN
            NEW.last_terminal_command_seq <> OLD.last_terminal_command_seq OR NEW.command_completion_digest <> OLD.command_completion_digest
          ELSE
            NEW.last_terminal_command_seq <> c.command_seq OR NEW.last_terminal_command_seq = OLD.last_terminal_command_seq
            OR NEW.command_completion_digest IS DISTINCT FROM
              revenant.v4_command_digest(OLD.command_completion_digest, c.command_seq, c.payload_sha256, c.due_time_ms, c.status, c.outcome_sha256)
          END) THEN
      RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 scheduler progress must match the latest classified command';
    END IF;
  END IF;
  RETURN NEW;
END
$$;

CREATE OR REPLACE FUNCTION revenant.guard_v4_finance_fact() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  family text;
  s revenant.v4_scheduler_run%ROWTYPE;
  c revenant.run_cursor%ROWTYPE;
  prior bytea;
  b revenant.v4_branch_execution%ROWTYPE;
  lineage uuid;
  hash_domain bytea := revenant.v4_canonical_string('revenant-v4-fact-prefix/1');
BEGIN
  SELECT finance_version INTO family FROM revenant.scenario_run
    WHERE id = NEW.scenario_run_id AND tenant_id = NEW.tenant_id;
  IF family IS DISTINCT FROM 'revenant-finance-reducer/4' THEN
    IF NEW.kind = 'ObservationSeal' OR NEW.effective_time_ms IS NOT NULL OR NEW.v4_prefix_digest IS NOT NULL THEN
      RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 finance metadata requires a V4 run';
    END IF;
    RETURN NEW;
  END IF;

  -- The identity checkpoint admits only seals.
  SELECT * INTO s FROM revenant.v4_scheduler_run WHERE run_id = NEW.scenario_run_id AND tenant_id = NEW.tenant_id FOR UPDATE;
  IF NOT FOUND OR s.status <> 'Open' OR NEW.kind <> 'ObservationSeal'
     OR NEW.effective_time_ms IS NULL OR NEW.effective_time_ms::numeric <> s.watermark_exclusive_ms::numeric + 300000
     OR EXISTS (SELECT 1 FROM revenant.v4_scheduled_command WHERE run_id = s.run_id
                AND status IN ('Pending', 'RetryableBlocked') AND due_time_ms < NEW.effective_time_ms) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 seal requires the next boundary and a resolved command prefix';
  END IF;

  SELECT * INTO c FROM revenant.run_cursor WHERE scenario_run_id = s.run_id;
  IF NOT FOUND OR c.closed_fact_seq IS NOT NULL OR c.last_fact_seq <> NEW.fact_seq OR c.last_event_time_ms <> NEW.effective_time_ms THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 fact must match the open run cursor';
  END IF;

  SELECT * INTO b FROM revenant.v4_branch_execution
    WHERE execution_run_id = s.run_id AND tenant_id = s.tenant_id;
  lineage := coalesce(b.lineage_run_id, s.run_id);
  IF NEW.fact_seq - 1 = coalesce(b.fork_fact_seq, 0) THEN
    prior := coalesce(b.fork_prefix_digest, sha256(hash_domain || uuid_send(lineage)));
  ELSE
    SELECT v4_prefix_digest INTO prior FROM revenant.finance_fact
      WHERE scenario_run_id = s.run_id AND fact_seq = NEW.fact_seq - 1 AND effective_time_ms <= NEW.effective_time_ms;
  END IF;

  IF prior IS NULL OR NEW.canonical_sha256 <> sha256(NEW.canonical)
     OR NEW.canonical <> (int4send(1) || revenant.v4_canonical_string('observation-seal/1') || uuid_send(lineage)
       || int8send(NEW.effective_time_ms) || int8send(s.watermark_exclusive_ms) || int8send(NEW.fact_seq - 1)
       || int4send(32) || prior || int4send(32) || s.command_completion_digest) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 seal bytes do not match the finance and command prefixes';
  END IF;

  NEW.v4_prefix_digest := sha256(hash_domain || int4send(32) || prior || int8send(NEW.fact_seq)
    || int8send(NEW.effective_time_ms) || decode('06', 'hex') || int4send(32) || NEW.canonical_sha256);
  RETURN NEW;
END
$$;

CREATE OR REPLACE FUNCTION revenant.check_fact_matches_cursor() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  cursor_seq bigint;
  base_seq bigint := 0;
BEGIN
  SELECT c.last_fact_seq INTO cursor_seq FROM revenant.run_cursor c WHERE c.scenario_run_id = NEW.scenario_run_id;
  IF NEW.effective_time_ms IS NOT NULL THEN
    SELECT fork_fact_seq INTO base_seq FROM revenant.v4_branch_execution
      WHERE execution_run_id = NEW.scenario_run_id AND tenant_id = NEW.tenant_id;
    base_seq := coalesce(base_seq, 0);
  END IF;
  IF cursor_seq IS NULL OR NEW.fact_seq > cursor_seq OR NEW.fact_seq <= base_seq
     OR (SELECT count(*) FROM revenant.finance_fact f WHERE f.scenario_run_id = NEW.scenario_run_id) <> cursor_seq - base_seq THEN
    RAISE EXCEPTION USING ERRCODE = 'RV004',
      MESSAGE = format('revenant.finance_fact %s/%s: facts must be gap-free and match the run cursor (%s)',
                       NEW.scenario_run_id, NEW.fact_seq, cursor_seq);
  END IF;
  RETURN NULL;
END
$$;

CREATE OR REPLACE FUNCTION revenant.check_v4_cursor() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  c revenant.run_cursor%ROWTYPE;
  s revenant.v4_scheduler_run%ROWTYPE;
  b revenant.v4_branch_execution%ROWTYPE;
  base_seq bigint;
  base_ms bigint;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM revenant.scenario_run WHERE id = NEW.scenario_run_id AND tenant_id = NEW.tenant_id
                 AND finance_version = 'revenant-finance-reducer/4') THEN
    RETURN NULL;
  END IF;
  SELECT * INTO c FROM revenant.run_cursor WHERE scenario_run_id = NEW.scenario_run_id;
  SELECT * INTO s FROM revenant.v4_scheduler_run WHERE run_id = NEW.scenario_run_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 cursor requires a scheduler';
  END IF;
  SELECT * INTO b FROM revenant.v4_branch_execution
    WHERE execution_run_id = c.scenario_run_id AND tenant_id = c.tenant_id;
  base_seq := coalesce(b.fork_fact_seq, 0);
  base_ms := coalesce(b.fork_boundary_ms, s.origin_ms);
  IF c.last_intent_sequence <> 0 OR c.last_event_time_ms <> s.watermark_exclusive_ms
     OR c.last_fact_seq < base_seq
     OR (c.last_fact_seq = base_seq AND c.last_event_time_ms <> base_ms)
     OR (c.last_fact_seq > base_seq AND NOT EXISTS (SELECT 1 FROM revenant.finance_fact WHERE scenario_run_id = c.scenario_run_id
          AND fact_seq = c.last_fact_seq AND effective_time_ms = c.last_event_time_ms AND kind = 'ObservationSeal'))
     OR (c.closed_fact_seq IS NOT NULL AND (b.execution_run_id IS NULL OR c.closed_fact_seq <> c.last_fact_seq
          OR EXISTS (SELECT 1 FROM revenant.v4_scheduled_command
              WHERE run_id = c.scenario_run_id AND (status IN ('Pending', 'RetryableBlocked') OR due_time_ms >= s.watermark_exclusive_ms)))) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 cursor, seal and watermark must commit together';
  END IF;
  RETURN NULL;
END
$$;
