-- Spec 07 task 7.5: hash-chained audit, the LiveOps screening/settlement ordering facts and the outbox relay role
-- (owner: Revenant; schema: revenant). Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
--   * finance_fact accepts the Screening and Settlement ordering facts (ADR-P1-17 decision 5). With the first V2 Timeline
--     history (this task) the kind set is FROZEN: PaymentIntent, ReviewDecision, ReturnInstruction, Screening, Settlement.
--     Any further kind is a new finance version, never an edit of this list.
--   * audit_event is an append-only, per-run hash chain. A BEFORE INSERT trigger (SECURITY DEFINER, so no runtime role can
--     touch the chain head directly) assigns the next gap-free seq under the run's audit_head row lock and computes
--       hash = sha256(prev_hash || utf8(payload)),  payload = 'revenant-audit/1' then, for each field, '|' || octets || ':' || value
--     over (run id, seq, tenant, event type, actor, correlation id, subject ref, subject version, ref sha256 hex, recorded_us).
--     The first prev_hash is 32 zero bytes. This is tamper EVIDENCE inside this database (a later edit or deletion breaks
--     the recomputed chain); it is not external notarization: an owner who rewrites every later row and the head is not
--     detected by the chain alone.
--   * revenant_outbox_relay (NOLOGIN, NOBYPASSRLS) may claim/confirm/release outbox rows (the shared Marrow OutboxRelay
--     columns) and nothing else; it gets no DELETE, so published rows stay as the re-publish source after Redis loss.
-- No cross-schema foreign key.

ALTER TABLE revenant.finance_fact DROP CONSTRAINT finance_fact_kind_check;
ALTER TABLE revenant.finance_fact ADD CONSTRAINT finance_fact_kind_check
  CHECK (kind IN ('PaymentIntent', 'ReviewDecision', 'ReturnInstruction', 'Screening', 'Settlement'));

-- One screening fact and one settlement fact per payment, at most.
CREATE UNIQUE INDEX finance_fact_one_screening_per_payment ON revenant.finance_fact (payment_id) WHERE kind = 'Screening';
CREATE UNIQUE INDEX finance_fact_one_settlement_per_payment ON revenant.finance_fact (payment_id) WHERE kind = 'Settlement';

-- LiveOps screening queue.
CREATE INDEX payment_initiated_queue_idx ON revenant.payment (tenant_id, created_at) WHERE status = 'Initiated';
CREATE INDEX payment_approved_queue_idx ON revenant.payment (tenant_id, created_at) WHERE status = 'Approved';

-- ---------------------------------------------------------------------------------------------------------------
-- Hash-chained audit.
-- ---------------------------------------------------------------------------------------------------------------

CREATE TABLE revenant.audit_head (
  scenario_run_id uuid   PRIMARY KEY REFERENCES revenant.scenario_run (id),
  last_seq        bigint NOT NULL CHECK (last_seq >= 0),
  last_hash       bytea  NOT NULL CHECK (octet_length(last_hash) = 32)
);

CREATE TABLE revenant.audit_event (
  tenant_id       text   NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id uuid   NOT NULL,
  seq             bigint NOT NULL,
  event_type      text   NOT NULL CHECK (event_type ~ '^[a-z][a-z0-9_.-]{0,99}$'),
  actor           text   NOT NULL CHECK (actor <> '' AND length(actor) <= 200),
  correlation_id  text   NOT NULL CHECK (correlation_id <> '' AND length(correlation_id) <= 200),
  subject_ref     text   NOT NULL CHECK (subject_ref <> '' AND length(subject_ref) <= 200),
  subject_version bigint NULL,
  ref_sha256      bytea  NULL CHECK (ref_sha256 IS NULL OR octet_length(ref_sha256) = 32),
  recorded_us     bigint NOT NULL,
  prev_hash       bytea  NOT NULL CHECK (octet_length(prev_hash) = 32),
  hash            bytea  NOT NULL CHECK (octet_length(hash) = 32),
  PRIMARY KEY (scenario_run_id, seq),
  FOREIGN KEY (scenario_run_id, tenant_id) REFERENCES revenant.scenario_run (id, tenant_id)
);

CREATE FUNCTION revenant.audit_field(value text) RETURNS text
LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog AS $$
  SELECT '|' || octet_length(convert_to(value, 'UTF8'))::text || ':' || value
$$;

CREATE FUNCTION revenant.chain_audit_event() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
DECLARE
  head_seq  bigint;
  head_hash bytea;
BEGIN
  INSERT INTO revenant.audit_head (scenario_run_id, last_seq, last_hash)
  VALUES (NEW.scenario_run_id, 0, decode(repeat('00', 32), 'hex'))
  ON CONFLICT (scenario_run_id) DO NOTHING;
  SELECT h.last_seq, h.last_hash INTO head_seq, head_hash
    FROM revenant.audit_head h WHERE h.scenario_run_id = NEW.scenario_run_id FOR UPDATE;
  NEW.seq := head_seq + 1;
  NEW.prev_hash := head_hash;
  NEW.recorded_us := (extract(epoch FROM clock_timestamp()) * 1000000)::bigint;
  NEW.hash := sha256(head_hash || convert_to(
      'revenant-audit/1'
      || revenant.audit_field(NEW.scenario_run_id::text)
      || revenant.audit_field(NEW.seq::text)
      || revenant.audit_field(NEW.tenant_id)
      || revenant.audit_field(NEW.event_type)
      || revenant.audit_field(NEW.actor)
      || revenant.audit_field(NEW.correlation_id)
      || revenant.audit_field(NEW.subject_ref)
      || revenant.audit_field(coalesce(NEW.subject_version::text, ''))
      || revenant.audit_field(coalesce(encode(NEW.ref_sha256, 'hex'), ''))
      || revenant.audit_field(NEW.recorded_us::text), 'UTF8'));
  UPDATE revenant.audit_head SET last_seq = NEW.seq, last_hash = NEW.hash WHERE scenario_run_id = NEW.scenario_run_id;
  RETURN NEW;
END
$$;
CREATE TRIGGER audit_event_chain BEFORE INSERT ON revenant.audit_event
  FOR EACH ROW EXECUTE FUNCTION revenant.chain_audit_event();
CREATE TRIGGER audit_event_no_update BEFORE UPDATE OR DELETE ON revenant.audit_event
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER audit_event_no_truncate BEFORE TRUNCATE ON revenant.audit_event
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER audit_head_no_delete BEFORE DELETE ON revenant.audit_head
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER audit_head_no_truncate BEFORE TRUNCATE ON revenant.audit_head
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();

ALTER TABLE revenant.audit_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.audit_event FORCE ROW LEVEL SECURITY;
CREATE POLICY audit_event_tenant ON revenant.audit_event TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
-- audit_head holds only run ids, sequence numbers and hashes (no tenant data); like the run directory it is not
-- row-level-secured. No runtime role can write it; only the SECURITY DEFINER chain trigger does.

REVOKE ALL ON revenant.audit_head, revenant.audit_event FROM PUBLIC;
REVOKE ALL ON FUNCTION revenant.chain_audit_event(), revenant.audit_field(text) FROM PUBLIC;
GRANT SELECT, INSERT ON revenant.audit_event TO revenant_ledger_writer;
GRANT SELECT ON revenant.audit_head TO revenant_ledger_writer, revenant_replay_reader;
GRANT SELECT ON revenant.audit_event TO revenant_replay_reader;

-- ---------------------------------------------------------------------------------------------------------------
-- Outbox relay role (shared Marrow OutboxRelay mechanics: claim with lease, confirm, release, poison; operator re-publish).
-- ---------------------------------------------------------------------------------------------------------------

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'revenant_outbox_relay') THEN
    CREATE ROLE revenant_outbox_relay NOLOGIN NOBYPASSRLS;
  END IF;
END
$$;
GRANT USAGE ON SCHEMA revenant TO revenant_outbox_relay;
GRANT SELECT ON revenant.outbox TO revenant_outbox_relay;
GRANT UPDATE (status, attempts, next_attempt_at, lease_token, lease_until, last_error, published_at, stream_entry_id, poisoned_at)
  ON revenant.outbox TO revenant_outbox_relay;
