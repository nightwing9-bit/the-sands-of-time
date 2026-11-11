-- Spec 07 task 7.2: durable payment intake, transition trail, ordered canonical facts and the outbox
-- (owner: Revenant; schema: revenant). Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- What this migration adds on top of 0001_ledger_core.sql:
--   * scenario_run / scenario_run_staff: the run DIRECTORY. It maps a ScenarioRunId to its tenant, owner and staff
--     members so the domain can authorize a request BEFORE it knows which tenant context to set. It holds no
--     financial data and is deliberately NOT row-level-secured (documented exemption; runtime has no UPDATE/DELETE).
--   * run_cursor: per run, the last committed fact sequence, last accepted intent sequence and business-time clock.
--     Intake takes this row FOR UPDATE, so intake into one run is serialized and fact order equals commit order.
--   * finance_fact: the run-local, gap-free, ordered CANONICAL facts (here: the PaymentIntent bytes + their SHA-256)
--     that the restartable Timeline projection will read. evidence_schema/evidence_sha256 are the minimum future
--     evidence boundary (an opaque, hash-pinned reference, both set or both null); Spec 08 owns concrete evidence.
--   * payment_transition: append-only payment trail. Row v is the state after version v; v1 is the Initiated row.
--   * payment_command: one row per command key per run (idempotency). Same key + same request hash replays the
--     recorded result; same key + different hash is a conflict. A rejected (illegal) transition is recorded here as a
--     rejected command RESULT and creates no transition row and no outbox event.
--   * outbox / inbox: the shared Marrow mechanics (Marrow.Infrastructure.Messaging.MessagingTables) in this schema.
--     They are system tables, not tenant-isolated; payloads carry ids, statuses and versions only.
--
-- Tenant-owned tables here (run_cursor, finance_fact, payment_transition, payment_command) get ENABLE + FORCE RLS on
-- the same transaction-local setting revenant.tenant_id as 0001. All new history tables are append-only (RV001).
-- No cross-schema foreign key. Runtime logins and the outbox relay's UPDATE grants arrive with task 7.5.

ALTER TABLE revenant.payment ADD CONSTRAINT payment_run_scope UNIQUE (id, tenant_id, scenario_run_id);

CREATE TABLE revenant.scenario_run (
  id               uuid        PRIMARY KEY,
  tenant_id        text        NOT NULL CHECK (tenant_id <> ''),
  owner_subject_id text        NOT NULL CHECK (owner_subject_id <> ''),
  seed             bigint      NOT NULL,
  scenario_version text        NOT NULL CHECK (scenario_version <> ''),
  manifest_hash    text        NOT NULL CHECK (manifest_hash <> ''),
  -- Timeline version family the run's facts are projected under; V1 histories are never rewritten.
  finance_version  text        NOT NULL CHECK (finance_version <> ''),
  created_at       timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT scenario_run_tenant UNIQUE (id, tenant_id)
);

CREATE TABLE revenant.scenario_run_staff (
  scenario_run_id uuid        NOT NULL REFERENCES revenant.scenario_run (id),
  subject_id      text        NOT NULL CHECK (subject_id <> ''),
  role            text        NOT NULL CHECK (role IN ('operator', 'supervisor')),
  granted_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (scenario_run_id, subject_id)
);

CREATE TABLE revenant.run_cursor (
  scenario_run_id      uuid   PRIMARY KEY,
  tenant_id            text   NOT NULL CHECK (tenant_id <> ''),
  last_fact_seq        bigint NOT NULL DEFAULT 0 CHECK (last_fact_seq >= 0),
  last_intent_sequence bigint NOT NULL DEFAULT 0 CHECK (last_intent_sequence >= 0),
  last_event_time_ms   bigint NOT NULL DEFAULT 0 CHECK (last_event_time_ms >= 0),
  CONSTRAINT run_cursor_run FOREIGN KEY (scenario_run_id, tenant_id) REFERENCES revenant.scenario_run (id, tenant_id)
);

CREATE TABLE revenant.finance_fact (
  tenant_id        text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id  uuid        NOT NULL,
  fact_seq         bigint      NOT NULL CHECK (fact_seq >= 1),
  kind             text        NOT NULL CHECK (kind IN ('PaymentIntent')),
  schema_version   text        NOT NULL CHECK (schema_version <> ''),
  payment_id       uuid        NOT NULL,
  canonical        bytea       NOT NULL CHECK (length(canonical) > 0),
  canonical_sha256 bytea       NOT NULL CHECK (length(canonical_sha256) = 32),
  evidence_schema  text,
  evidence_sha256  bytea,
  created_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (scenario_run_id, fact_seq),
  CONSTRAINT finance_fact_one_per_payment UNIQUE (payment_id),
  CONSTRAINT finance_fact_evidence_pair CHECK (
    (evidence_schema IS NULL AND evidence_sha256 IS NULL)
    OR (evidence_schema <> '' AND evidence_sha256 IS NOT NULL AND length(evidence_sha256) = 32)),
  CONSTRAINT finance_fact_payment FOREIGN KEY (payment_id, tenant_id, scenario_run_id)
    REFERENCES revenant.payment (id, tenant_id, scenario_run_id)
);

CREATE TABLE revenant.payment_transition (
  tenant_id       text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id uuid        NOT NULL,
  payment_id      uuid        NOT NULL,
  version         bigint      NOT NULL CHECK (version >= 1),
  from_status     text,
  to_status       text        NOT NULL CHECK (to_status IN
                    ('Initiated', 'Screened', 'Held', 'Cleared', 'Approved', 'Rejected', 'Returned', 'Settled')),
  reason          text,
  command_key     text        NOT NULL CHECK (command_key <> ''),
  actor           text        NOT NULL CHECK (actor <> ''),
  occurred_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (payment_id, version),
  -- v1 is the Initiated row (no predecessor); every later row is one legal step of the frozen matrix.
  CONSTRAINT payment_transition_shape CHECK (
    (version = 1 AND from_status IS NULL AND to_status = 'Initiated')
    OR (version > 1 AND from_status IS NOT NULL AND revenant.is_legal_payment_transition(from_status, to_status))),
  CONSTRAINT payment_transition_payment FOREIGN KEY (payment_id, tenant_id, scenario_run_id)
    REFERENCES revenant.payment (id, tenant_id, scenario_run_id)
);

CREATE TABLE revenant.payment_command (
  tenant_id       text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id uuid        NOT NULL,
  command_key     text        NOT NULL CHECK (command_key <> ''),
  kind            text        NOT NULL CHECK (kind IN ('Intake', 'Transition')),
  payment_id      uuid        NOT NULL,
  request_sha256  bytea       NOT NULL CHECK (length(request_sha256) = 32),
  outcome         text        NOT NULL CHECK (outcome IN ('Accepted', 'Rejected')),
  result_status   text        NOT NULL,
  result_version  bigint      NOT NULL CHECK (result_version >= 1),
  reject_code     text,
  actor           text        NOT NULL CHECK (actor <> ''),
  created_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (scenario_run_id, command_key),
  CONSTRAINT payment_command_reject_shape CHECK ((outcome = 'Rejected') = (reject_code IS NOT NULL)),
  CONSTRAINT payment_command_payment FOREIGN KEY (payment_id, tenant_id, scenario_run_id)
    REFERENCES revenant.payment (id, tenant_id, scenario_run_id)
);
CREATE INDEX payment_command_payment_idx ON revenant.payment_command (payment_id);

-- Outbox v1 / inbox in the shape Marrow.Infrastructure.Messaging.MessagingTables documents.
CREATE TABLE revenant.outbox (
  seq             bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
  event_id        uuid PRIMARY KEY,
  event_type      text NOT NULL,
  event_version   integer NOT NULL CHECK (event_version > 0),
  aggregate_id    text NOT NULL,
  occurred_utc    timestamptz NOT NULL,
  payload         text NOT NULL,
  correlation_id  text NOT NULL,
  status          text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'published', 'poisoned')),
  attempts        integer NOT NULL DEFAULT 0,
  next_attempt_at timestamptz NOT NULL DEFAULT now(),
  lease_token     uuid NULL,
  lease_until     timestamptz NULL,
  last_error      text NULL,
  enqueued_at     timestamptz NOT NULL DEFAULT now(),
  published_at    timestamptz NULL,
  stream_entry_id text NULL,
  poisoned_at     timestamptz NULL
);
CREATE INDEX outbox_pending_idx ON revenant.outbox (next_attempt_at, seq) WHERE status = 'pending';

CREATE TABLE revenant.inbox (
  consumer_id  text NOT NULL,
  event_id     uuid NOT NULL,
  received_utc timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (consumer_id, event_id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- Append-only guards (reuse revenant.reject_mutation from 0001). run_cursor is the one deliberately mutable table.
-- ---------------------------------------------------------------------------------------------------------------

CREATE TRIGGER scenario_run_no_update BEFORE UPDATE OR DELETE ON revenant.scenario_run
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER scenario_run_staff_no_update BEFORE UPDATE OR DELETE ON revenant.scenario_run_staff
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER finance_fact_no_update BEFORE UPDATE OR DELETE ON revenant.finance_fact
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER payment_transition_no_update BEFORE UPDATE OR DELETE ON revenant.payment_transition
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER payment_command_no_update BEFORE UPDATE OR DELETE ON revenant.payment_command
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER run_cursor_no_delete BEFORE DELETE ON revenant.run_cursor
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();

CREATE TRIGGER scenario_run_no_truncate BEFORE TRUNCATE ON revenant.scenario_run
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER scenario_run_staff_no_truncate BEFORE TRUNCATE ON revenant.scenario_run_staff
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER finance_fact_no_truncate BEFORE TRUNCATE ON revenant.finance_fact
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER payment_transition_no_truncate BEFORE TRUNCATE ON revenant.payment_transition
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER payment_command_no_truncate BEFORE TRUNCATE ON revenant.payment_command
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER run_cursor_no_truncate BEFORE TRUNCATE ON revenant.run_cursor
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();

-- The cursor only ever moves forward, by exactly one fact, and never changes identity.
CREATE FUNCTION revenant.guard_run_cursor_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.scenario_run_id, NEW.tenant_id) IS DISTINCT FROM (OLD.scenario_run_id, OLD.tenant_id)
     OR NEW.last_fact_seq <> OLD.last_fact_seq + 1
     OR NEW.last_intent_sequence < OLD.last_intent_sequence
     OR NEW.last_event_time_ms < OLD.last_event_time_ms THEN
    RAISE EXCEPTION USING ERRCODE = 'RV004',
      MESSAGE = format('revenant.run_cursor %s: the cursor only advances by exactly one fact and never moves back', OLD.scenario_run_id);
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER run_cursor_guard_update BEFORE UPDATE ON revenant.run_cursor
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_run_cursor_update();

-- A fact must be exactly the cursor's next sequence value inside the same transaction that advanced the cursor,
-- so fact order has no gaps and no reordering. (A deferred check keeps the two statements order-independent.)
CREATE FUNCTION revenant.check_fact_matches_cursor() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  cursor_seq bigint;
BEGIN
  SELECT c.last_fact_seq INTO cursor_seq FROM revenant.run_cursor c WHERE c.scenario_run_id = NEW.scenario_run_id;
  IF cursor_seq IS NULL OR NEW.fact_seq > cursor_seq
     OR (SELECT count(*) FROM revenant.finance_fact f WHERE f.scenario_run_id = NEW.scenario_run_id) <> cursor_seq THEN
    RAISE EXCEPTION USING ERRCODE = 'RV004',
      MESSAGE = format('revenant.finance_fact %s/%s: facts must be gap-free and match the run cursor (%s)',
                       NEW.scenario_run_id, NEW.fact_seq, cursor_seq);
  END IF;
  RETURN NULL;
END
$$;
CREATE CONSTRAINT TRIGGER finance_fact_matches_cursor AFTER INSERT ON revenant.finance_fact
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_fact_matches_cursor();

-- ---------------------------------------------------------------------------------------------------------------
-- Row level security on the tenant-owned tables added here.
-- ---------------------------------------------------------------------------------------------------------------

ALTER TABLE revenant.run_cursor ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.run_cursor FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.finance_fact ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.finance_fact FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.payment_transition ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.payment_transition FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.payment_command ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.payment_command FORCE ROW LEVEL SECURITY;

CREATE POLICY run_cursor_tenant ON revenant.run_cursor TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY finance_fact_tenant ON revenant.finance_fact TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY payment_transition_tenant ON revenant.payment_transition TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY payment_command_tenant ON revenant.payment_command TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));

-- ---------------------------------------------------------------------------------------------------------------
-- Least privilege. 0001 revoked everything from PUBLIC for the tables that existed then; new tables repeat it.
-- ---------------------------------------------------------------------------------------------------------------

REVOKE ALL ON revenant.scenario_run, revenant.scenario_run_staff, revenant.run_cursor, revenant.finance_fact,
  revenant.payment_transition, revenant.payment_command, revenant.outbox, revenant.inbox FROM PUBLIC;
REVOKE ALL ON FUNCTION revenant.guard_run_cursor_update(), revenant.check_fact_matches_cursor() FROM PUBLIC;

GRANT SELECT, INSERT ON revenant.scenario_run, revenant.scenario_run_staff, revenant.run_cursor, revenant.finance_fact,
  revenant.payment_transition, revenant.payment_command, revenant.outbox, revenant.inbox TO revenant_ledger_writer;
GRANT UPDATE (last_fact_seq, last_intent_sequence, last_event_time_ms) ON revenant.run_cursor TO revenant_ledger_writer;

-- The replay reader sees tenant data through RLS and the directory, and never the outbox/inbox. It cannot write.
GRANT SELECT ON revenant.scenario_run, revenant.scenario_run_staff, revenant.run_cursor, revenant.finance_fact,
  revenant.payment_transition, revenant.payment_command TO revenant_replay_reader;
