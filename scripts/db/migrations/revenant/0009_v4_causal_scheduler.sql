-- V4 is a new run family. Historical rows and their payloads remain intact.
ALTER TABLE revenant.scenario_run DROP CONSTRAINT scenario_run_finance_version;
ALTER TABLE revenant.scenario_run ADD CONSTRAINT scenario_run_finance_version
  CHECK (finance_version IN ('revenant-finance-reducer/2', 'revenant-finance-reducer/3', 'revenant-finance-reducer/4'));

ALTER TABLE revenant.finance_fact
  ALTER COLUMN payment_id DROP NOT NULL,
  ADD COLUMN effective_time_ms bigint,
  ADD COLUMN v4_prefix_digest bytea CHECK (length(v4_prefix_digest) = 32),
  DROP CONSTRAINT finance_fact_kind_check,
  ADD CONSTRAINT finance_fact_kind_check
    CHECK (kind IN ('PaymentIntent', 'ReviewDecision', 'ReturnInstruction', 'Screening', 'Settlement', 'ObservationSeal')),
  ADD CONSTRAINT finance_fact_payment_or_seal CHECK (
    (kind = 'ObservationSeal' AND payment_id IS NULL AND schema_version = 'observation-seal/1'
      AND evidence_schema IS NULL AND evidence_sha256 IS NULL AND effective_time_ms IS NOT NULL AND effective_time_ms % 300000 = 0)
    OR (kind <> 'ObservationSeal' AND payment_id IS NOT NULL AND schema_version <> 'observation-seal/1'));

CREATE UNIQUE INDEX finance_fact_one_v4_seal_per_boundary ON revenant.finance_fact (scenario_run_id, effective_time_ms)
  WHERE kind = 'ObservationSeal';

-- The V4 origin can be a signed Unix instant. The insert guard below retains the old zero lower bound
-- for V2/V3. The existing cursor update guard still prevents time going backwards for every family.
ALTER TABLE revenant.run_cursor DROP CONSTRAINT run_cursor_last_event_time_ms_check;

CREATE TABLE revenant.v4_scheduler_run (
  run_id uuid PRIMARY KEY CHECK (run_id <> '00000000-0000-0000-0000-000000000000'::uuid),
  tenant_id text NOT NULL,
  origin_ms bigint NOT NULL CHECK (origin_ms % 300000 = 0 AND origin_ms <= 9223372036854475807),
  watermark_exclusive_ms bigint NOT NULL CHECK (watermark_exclusive_ms % 300000 = 0 AND watermark_exclusive_ms >= origin_ms),
  clock_ms bigint NOT NULL CHECK (clock_ms >= watermark_exclusive_ms AND clock_ms::numeric < watermark_exclusive_ms::numeric + 300000),
  next_command_seq bigint NOT NULL CHECK (next_command_seq > 0),
  last_terminal_command_seq bigint NOT NULL DEFAULT 0 CHECK (last_terminal_command_seq >= 0),
  status text NOT NULL CHECK (status IN ('Open', 'Closing', 'Closed', 'Failed')),
  scheduler_contract_id text NOT NULL CHECK (scheduler_contract_id = 'sim-scheduler/1'),
  command_completion_digest bytea NOT NULL CHECK (length(command_completion_digest) = 32),
  canonical_context bytea NOT NULL CHECK (length(canonical_context) > 0),
  UNIQUE (run_id, tenant_id),
  FOREIGN KEY (run_id, tenant_id) REFERENCES revenant.scenario_run (id, tenant_id)
);

CREATE TABLE revenant.v4_scheduled_command (
  run_id uuid NOT NULL,
  tenant_id text NOT NULL,
  command_seq bigint NOT NULL CHECK (command_seq > 0),
  command_key text NOT NULL CHECK (btrim(command_key) <> ''),
  command_class text NOT NULL CHECK (command_class IN ('Exogenous', 'ConditionalHumanRequest', 'DerivedOpportunity')),
  command_kind text NOT NULL CHECK (btrim(command_kind) <> ''),
  payload_schema text NOT NULL CHECK (btrim(payload_schema) <> ''),
  canonical_payload bytea NOT NULL CHECK (length(canonical_payload) > 0),
  payload_sha256 bytea NOT NULL CHECK (payload_sha256 = sha256(canonical_payload)),
  requested_time_ms bigint NOT NULL,
  admitted_time_ms bigint NOT NULL,
  due_time_ms bigint NOT NULL CHECK (due_time_ms >= admitted_time_ms AND due_time_ms < 9223372036854600000),
  effective_time_ms bigint,
  status text NOT NULL CHECK (status IN ('Pending', 'Completed', 'Cancelled', 'FailedPermanent', 'RetryableBlocked')),
  outcome_code text,
  outcome_sha256 bytea,
  PRIMARY KEY (run_id, command_seq),
  UNIQUE (run_id, command_key),
  FOREIGN KEY (run_id, tenant_id) REFERENCES revenant.v4_scheduler_run (run_id, tenant_id),
  CHECK ((status = 'Completed' AND effective_time_ms IS NOT NULL AND effective_time_ms = due_time_ms)
    OR (status <> 'Completed' AND effective_time_ms IS NULL)),
  CHECK ((status = 'Pending' AND outcome_code IS NULL AND outcome_sha256 IS NULL)
    OR (status <> 'Pending' AND outcome_code IS NOT NULL AND btrim(outcome_code) <> ''
      AND outcome_sha256 IS NOT NULL AND length(outcome_sha256) = 32))
);

CREATE INDEX v4_scheduled_command_due ON revenant.v4_scheduled_command (run_id, due_time_ms, command_seq)
  WHERE status IN ('Pending', 'RetryableBlocked');
CREATE INDEX v4_scheduled_command_latest_classified ON revenant.v4_scheduled_command (run_id, due_time_ms DESC, command_seq DESC)
  WHERE status IN ('RetryableBlocked', 'Completed', 'Cancelled', 'FailedPermanent');

ALTER TABLE revenant.v4_scheduler_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.v4_scheduler_run FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.v4_scheduled_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.v4_scheduled_command FORCE ROW LEVEL SECURITY;
CREATE POLICY v4_scheduler_tenant ON revenant.v4_scheduler_run TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY v4_command_tenant ON revenant.v4_scheduled_command TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
REVOKE ALL ON revenant.v4_scheduler_run, revenant.v4_scheduled_command FROM PUBLIC;
GRANT SELECT, INSERT ON revenant.v4_scheduler_run, revenant.v4_scheduled_command TO revenant_ledger_writer;
GRANT UPDATE (watermark_exclusive_ms, clock_ms, next_command_seq, last_terminal_command_seq, command_completion_digest)
  ON revenant.v4_scheduler_run TO revenant_ledger_writer;
GRANT UPDATE (effective_time_ms, status, outcome_code, outcome_sha256) ON revenant.v4_scheduled_command TO revenant_ledger_writer;
GRANT SELECT ON revenant.v4_scheduler_run, revenant.v4_scheduled_command TO revenant_replay_reader;

CREATE TRIGGER v4_scheduler_no_delete BEFORE DELETE ON revenant.v4_scheduler_run
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER v4_scheduler_no_truncate BEFORE TRUNCATE ON revenant.v4_scheduler_run
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER v4_command_no_delete BEFORE DELETE ON revenant.v4_scheduled_command
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER v4_command_no_truncate BEFORE TRUNCATE ON revenant.v4_scheduled_command
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();

-- Match Marrow's binary encoding for the ASCII domain/schema identifiers.
CREATE FUNCTION revenant.v4_canonical_string(value text) RETURNS bytea
LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog
RETURN int4send(octet_length(convert_to(value, 'UTF8'))) || convert_to(value, 'UTF8');

CREATE FUNCTION revenant.v4_command_digest(prior bytea, seq bigint, payload bytea, due bigint, status text, outcome bytea)
RETURNS bytea LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog
RETURN sha256(revenant.v4_canonical_string('revenant-v4-command-completion/1')
  || int4send(32) || prior || int8send(seq) || int4send(32) || payload || int8send(due)
  || revenant.v4_canonical_string(status) || int4send(32) || outcome);

CREATE FUNCTION revenant.guard_v4_scheduler() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  c revenant.v4_scheduled_command%ROWTYPE;
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'Open' OR NEW.watermark_exclusive_ms <> NEW.origin_ms OR NEW.clock_ms <> NEW.origin_ms
       OR NEW.next_command_seq <> 1 OR NEW.last_terminal_command_seq <> 0
       OR NEW.command_completion_digest <> sha256(revenant.v4_canonical_string('revenant-v4-command-completion/1') || uuid_send(NEW.run_id))
       OR NOT EXISTS (SELECT 1 FROM revenant.scenario_run r JOIN revenant.run_cursor k ON k.scenario_run_id = r.id
                      WHERE r.id = NEW.run_id AND r.tenant_id = NEW.tenant_id AND r.finance_version = 'revenant-finance-reducer/4'
                        AND k.last_fact_seq = 0 AND k.last_intent_sequence = 0
                        AND k.last_event_time_ms = NEW.origin_ms AND k.closed_fact_seq IS NULL) THEN
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
CREATE TRIGGER v4_scheduler_guard BEFORE INSERT OR UPDATE ON revenant.v4_scheduler_run
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_v4_scheduler();

CREATE FUNCTION revenant.guard_v4_command() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  s revenant.v4_scheduler_run%ROWTYPE;
  head bigint;
BEGIN
  SELECT * INTO s FROM revenant.v4_scheduler_run WHERE run_id = NEW.run_id AND tenant_id = NEW.tenant_id FOR UPDATE;
  IF NOT FOUND OR s.status <> 'Open' THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 command requires an open scheduler in the current tenant';
  END IF;
  IF TG_OP = 'INSERT' THEN
    IF NEW.command_seq <> s.next_command_seq OR NEW.admitted_time_ms <> s.clock_ms OR NEW.status <> 'Pending' THEN
      RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'Invalid V4 command admission';
    END IF;
    RETURN NEW;
  END IF;

  IF (NEW.run_id, NEW.tenant_id, NEW.command_seq, NEW.command_key, NEW.command_class, NEW.command_kind, NEW.payload_schema,
      NEW.canonical_payload, NEW.payload_sha256, NEW.requested_time_ms, NEW.admitted_time_ms, NEW.due_time_ms)
    IS DISTINCT FROM (OLD.run_id, OLD.tenant_id, OLD.command_seq, OLD.command_key, OLD.command_class, OLD.command_kind, OLD.payload_schema,
      OLD.canonical_payload, OLD.payload_sha256, OLD.requested_time_ms, OLD.admitted_time_ms, OLD.due_time_ms) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 admitted command identity and payload are immutable';
  END IF;
  SELECT command_seq INTO head FROM revenant.v4_scheduled_command
    WHERE run_id = NEW.run_id AND status IN ('Pending', 'RetryableBlocked') ORDER BY due_time_ms, command_seq LIMIT 1;
  IF head IS DISTINCT FROM OLD.command_seq OR OLD.due_time_ms < s.clock_ms
     OR OLD.due_time_ms::numeric >= s.watermark_exclusive_ms::numeric + 300000
     OR NOT ((OLD.status = 'RetryableBlocked' AND NEW.status = 'Pending')
             OR (OLD.status = 'Pending' AND NEW.status IN ('Completed', 'Cancelled', 'FailedPermanent', 'RetryableBlocked'))) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'Invalid V4 command transition or barrier violation';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER v4_command_guard BEFORE INSERT OR UPDATE ON revenant.v4_scheduled_command
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_v4_command();

CREATE FUNCTION revenant.project_v4_command_progress() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    UPDATE revenant.v4_scheduler_run SET next_command_seq = next_command_seq + 1 WHERE run_id = NEW.run_id;
  ELSIF NEW.status = 'RetryableBlocked' THEN
    UPDATE revenant.v4_scheduler_run SET clock_ms = NEW.due_time_ms WHERE run_id = NEW.run_id;
  ELSIF NEW.status IN ('Completed', 'Cancelled', 'FailedPermanent') THEN
    UPDATE revenant.v4_scheduler_run
       SET clock_ms = NEW.due_time_ms, last_terminal_command_seq = NEW.command_seq, command_completion_digest = revenant.v4_command_digest(
         command_completion_digest, NEW.command_seq, NEW.payload_sha256, NEW.due_time_ms, NEW.status, NEW.outcome_sha256)
     WHERE run_id = NEW.run_id;
  END IF;
  RETURN NULL;
END
$$;
CREATE TRIGGER v4_command_progress AFTER INSERT OR UPDATE ON revenant.v4_scheduled_command
  FOR EACH ROW EXECUTE FUNCTION revenant.project_v4_command_progress();

CREATE FUNCTION revenant.guard_v4_finance_fact() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  family text;
  s revenant.v4_scheduler_run%ROWTYPE;
  c revenant.run_cursor%ROWTYPE;
  prior bytea;
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

  -- R2's only durable V4 fact is a seal. R3 adds transaction-aware financial handlers.
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

  IF NEW.fact_seq = 1 THEN
    prior := sha256(hash_domain || uuid_send(s.run_id));
  ELSE
    SELECT v4_prefix_digest INTO prior FROM revenant.finance_fact
      WHERE scenario_run_id = s.run_id AND fact_seq = NEW.fact_seq - 1 AND effective_time_ms < NEW.effective_time_ms;
  END IF;

  IF prior IS NULL OR NEW.canonical_sha256 <> sha256(NEW.canonical)
     OR NEW.canonical <> (int4send(1) || revenant.v4_canonical_string('observation-seal/1') || uuid_send(s.run_id)
       || int8send(NEW.effective_time_ms) || int8send(s.watermark_exclusive_ms) || int8send(NEW.fact_seq - 1)
       || int4send(32) || prior || int4send(32) || s.command_completion_digest) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 seal bytes do not match the finance and command prefixes';
  END IF;

  NEW.v4_prefix_digest := sha256(hash_domain || int4send(32) || prior || int8send(NEW.fact_seq)
    || int8send(NEW.effective_time_ms) || decode('06', 'hex') || int4send(32) || NEW.canonical_sha256);
  RETURN NEW;
END
$$;
CREATE TRIGGER finance_fact_v4_shape BEFORE INSERT ON revenant.finance_fact
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_v4_finance_fact();

CREATE FUNCTION revenant.guard_v4_cursor_origin() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.last_event_time_ms < 0 AND NOT EXISTS (
    SELECT 1 FROM revenant.scenario_run WHERE id = NEW.scenario_run_id AND tenant_id = NEW.tenant_id
      AND finance_version = 'revenant-finance-reducer/4') THEN
    RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'run_cursor_last_event_time_ms_check',
      MESSAGE = 'Historical finance cursor requires nonnegative time';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER run_cursor_v4_origin BEFORE INSERT ON revenant.run_cursor
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_v4_cursor_origin();

-- Keep the original gap-free trigger and add the converse: an advanced V4 cursor cannot commit alone.
CREATE FUNCTION revenant.check_v4_cursor() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  c revenant.run_cursor%ROWTYPE;
  s revenant.v4_scheduler_run%ROWTYPE;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM revenant.scenario_run WHERE id = NEW.scenario_run_id AND tenant_id = NEW.tenant_id
                 AND finance_version = 'revenant-finance-reducer/4') THEN
    RETURN NULL;
  END IF;
  SELECT * INTO c FROM revenant.run_cursor WHERE scenario_run_id = NEW.scenario_run_id;
  SELECT * INTO s FROM revenant.v4_scheduler_run WHERE run_id = NEW.scenario_run_id;
  IF NOT FOUND OR c.closed_fact_seq IS NOT NULL OR c.last_intent_sequence <> 0 OR c.last_event_time_ms <> s.watermark_exclusive_ms
     OR (c.last_fact_seq = 0 AND c.last_event_time_ms <> s.origin_ms)
     OR (c.last_fact_seq > 0 AND NOT EXISTS (SELECT 1 FROM revenant.finance_fact WHERE scenario_run_id = c.scenario_run_id
          AND fact_seq = c.last_fact_seq AND effective_time_ms = c.last_event_time_ms AND kind = 'ObservationSeal')) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 cursor, seal and watermark must commit together';
  END IF;
  RETURN NULL;
END
$$;
CREATE CONSTRAINT TRIGGER run_cursor_v4_matches_seal AFTER INSERT OR UPDATE ON revenant.run_cursor
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_v4_cursor();

REVOKE ALL ON FUNCTION revenant.guard_v4_scheduler(), revenant.guard_v4_command(), revenant.project_v4_command_progress(),
  revenant.guard_v4_finance_fact(), revenant.guard_v4_cursor_origin(), revenant.check_v4_cursor(),
  revenant.v4_canonical_string(text), revenant.v4_command_digest(bytea, bigint, bytea, bigint, text, bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION revenant.v4_canonical_string(text),
  revenant.v4_command_digest(bytea, bigint, bytea, bigint, text, bytea) TO revenant_ledger_writer;
