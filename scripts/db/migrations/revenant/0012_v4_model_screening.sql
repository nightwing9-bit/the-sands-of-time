-- R6. Keep the existing cursor, command ordering and digest guards.
-- Derive the capacity policy from the immutable canonical context at opening.
-- Context v4 format 1: five length-prefixed fields, two int64 fields, five
-- length-prefixed policy fields, then the one-byte risk mode.
CREATE FUNCTION revenant.v4_requires_r5(context_bytes bytea) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE STRICT SET search_path = pg_catalog AS $$
DECLARE
  p integer := 4;
  n bigint;
  field integer;
  mode integer;
BEGIN
  IF substring(context_bytes FROM 1 FOR 4) <> int4send(1) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV010', MESSAGE = 'Unknown V4 context format';
  END IF;
  FOR field IN 1..10 LOOP
    IF field = 6 THEN p := p + 16; END IF;
    IF p > length(context_bytes) - 4 THEN
      RAISE EXCEPTION USING ERRCODE = 'RV010', MESSAGE = 'Truncated V4 context';
    END IF;
    n := (get_byte(context_bytes, p)::bigint << 24) + (get_byte(context_bytes, p + 1) << 16)
       + (get_byte(context_bytes, p + 2) << 8) + get_byte(context_bytes, p + 3);
    IF n > length(context_bytes) - p - 4 THEN
      RAISE EXCEPTION USING ERRCODE = 'RV010', MESSAGE = 'Invalid V4 context field length';
    END IF;
    p := p + 4 + n;
  END LOOP;
  IF p >= length(context_bytes) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV010', MESSAGE = 'Missing V4 context risk mode';
  END IF;
  mode := get_byte(context_bytes, p);
  IF mode NOT IN (1, 2) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV010', MESSAGE = 'Unknown V4 context risk mode';
  END IF;
  RETURN mode = 1;
END
$$;
REVOKE ALL ON FUNCTION revenant.v4_requires_r5(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION revenant.v4_requires_r5(bytea) TO revenant_ledger_writer;

ALTER TABLE revenant.v4_scheduler_run ADD COLUMN model_review_capacity_policy text
  GENERATED ALWAYS AS (CASE WHEN revenant.v4_requires_r5(canonical_context) THEN 'v4-model-review-capacity/1' END) STORED;

CREATE OR REPLACE FUNCTION revenant.guard_finance_fact_evidence() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  family text;
  model_required boolean := false;
BEGIN
  SELECT r.finance_version INTO family FROM revenant.scenario_run r WHERE r.id = NEW.scenario_run_id;
  IF family = 'revenant-finance-reducer/4' THEN
    SELECT s.model_review_capacity_policy IS NOT NULL INTO model_required FROM revenant.v4_scheduler_run s
      WHERE s.run_id = NEW.scenario_run_id AND s.tenant_id = NEW.tenant_id;
  END IF;
  IF NEW.kind = 'Screening' AND model_required THEN
    IF NEW.schema_version <> 'v4-model-screening-evidence/1'
       OR NEW.evidence_schema IS DISTINCT FROM 'v4-model-screening-evidence/1'
       OR NEW.evidence_sha256 IS DISTINCT FROM NEW.canonical_sha256 THEN
      RAISE EXCEPTION USING ERRCODE = 'RV010', MESSAGE = 'V4 ModelRequired Screening needs its canonical inference evidence';
    END IF;
  ELSIF NEW.kind = 'Screening' AND family = 'revenant-finance-reducer/3' THEN
    IF NEW.schema_version <> 'revenant-inference-evidence/1'
       OR NEW.evidence_schema IS DISTINCT FROM 'revenant-inference-evidence/1'
       OR NEW.evidence_sha256 IS DISTINCT FROM NEW.canonical_sha256 THEN
      RAISE EXCEPTION USING ERRCODE = 'RV010',
        MESSAGE = format('revenant.finance_fact %s/%s: a model-aware screening needs its inference evidence', NEW.scenario_run_id, NEW.fact_seq);
    END IF;
  ELSIF NEW.evidence_schema IS NOT NULL OR NEW.evidence_sha256 IS NOT NULL
        OR (NEW.kind = 'Screening' AND NEW.schema_version <> 'revenant-screening/1') THEN
    RAISE EXCEPTION USING ERRCODE = 'RV010',
      MESSAGE = format('revenant.finance_fact %s/%s: only a model-aware screening carries inference evidence', NEW.scenario_run_id, NEW.fact_seq);
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
  fact_tag integer;
  hash_domain bytea := revenant.v4_canonical_string('revenant-v4-fact-prefix/1');
BEGIN
  SELECT finance_version INTO family FROM revenant.scenario_run
    WHERE id = NEW.scenario_run_id AND tenant_id = NEW.tenant_id;
  IF family IS DISTINCT FROM 'revenant-finance-reducer/4' THEN
    IF NEW.kind = 'ObservationSeal' OR NEW.effective_time_ms IS NOT NULL OR NEW.v4_prefix_digest IS NOT NULL OR NEW.v4_command_seq IS NOT NULL THEN
      RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 finance metadata requires a V4 run';
    END IF;
    RETURN NEW;
  END IF;

  SELECT * INTO s FROM revenant.v4_scheduler_run WHERE run_id = NEW.scenario_run_id AND tenant_id = NEW.tenant_id FOR UPDATE;
  IF NOT FOUND OR s.status <> 'Open' OR NEW.effective_time_ms IS NULL THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 fact requires an open scheduler and an effective time';
  END IF;
  SELECT * INTO b FROM revenant.v4_branch_execution
    WHERE execution_run_id = s.run_id AND tenant_id = s.tenant_id;

  IF NEW.kind = 'ObservationSeal' THEN
    IF NEW.v4_command_seq IS NOT NULL OR NEW.effective_time_ms::numeric <> s.watermark_exclusive_ms::numeric + 300000
       OR EXISTS (SELECT 1 FROM revenant.v4_scheduled_command WHERE run_id = s.run_id
                  AND status IN ('Pending', 'RetryableBlocked') AND due_time_ms < NEW.effective_time_ms) THEN
      RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 seal requires the next boundary and a resolved command prefix';
    END IF;
  ELSIF b.readiness_status IS DISTINCT FROM 'Ready'
     OR NEW.effective_time_ms < s.clock_ms OR NEW.effective_time_ms::numeric >= s.watermark_exclusive_ms::numeric + 300000
     OR NOT EXISTS (
       SELECT 1 FROM revenant.v4_scheduled_command q WHERE q.run_id = s.run_id AND q.tenant_id = s.tenant_id
         AND q.command_seq = NEW.v4_command_seq AND q.status = 'Pending'
         AND q.due_time_ms = NEW.effective_time_ms AND q.command_kind = NEW.kind
         AND (NEW.kind <> 'PaymentIntent' OR q.canonical_payload = NEW.canonical)
         AND NOT EXISTS (SELECT 1 FROM revenant.v4_scheduled_command earlier WHERE earlier.run_id = q.run_id
            AND earlier.status IN ('Pending', 'RetryableBlocked')
            AND (earlier.due_time_ms, earlier.command_seq) < (q.due_time_ms, q.command_seq))
     ) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 finance fact requires the earliest eligible command in the open interval';
  END IF;

  SELECT * INTO c FROM revenant.run_cursor WHERE scenario_run_id = s.run_id;
  IF NOT FOUND OR c.closed_fact_seq IS NOT NULL OR c.last_fact_seq <> NEW.fact_seq OR c.last_event_time_ms <> NEW.effective_time_ms THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 fact must match the open run cursor';
  END IF;

  lineage := coalesce(b.lineage_run_id, s.run_id);
  IF NEW.fact_seq - 1 = coalesce(b.fork_fact_seq, 0) THEN
    prior := coalesce(b.fork_prefix_digest, sha256(hash_domain || uuid_send(lineage)));
  ELSE
    SELECT v4_prefix_digest INTO prior FROM revenant.finance_fact
      WHERE scenario_run_id = s.run_id AND fact_seq = NEW.fact_seq - 1 AND effective_time_ms <= NEW.effective_time_ms;
  END IF;

  IF prior IS NULL OR NEW.canonical_sha256 <> sha256(NEW.canonical)
     OR (NEW.kind = 'ObservationSeal' AND NEW.canonical <> (int4send(1) || revenant.v4_canonical_string('observation-seal/1') || uuid_send(lineage)
       || int8send(NEW.effective_time_ms) || int8send(s.watermark_exclusive_ms) || int8send(NEW.fact_seq - 1)
       || int4send(32) || prior || int4send(32) || s.command_completion_digest)) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 seal bytes do not match the finance and command prefixes';
  END IF;

  fact_tag := CASE NEW.kind WHEN 'PaymentIntent' THEN 1 WHEN 'ReviewDecision' THEN 2 WHEN 'ReturnInstruction' THEN 3
                            WHEN 'Screening' THEN 4 WHEN 'Settlement' THEN 5 ELSE 6 END;
  IF NEW.kind = 'PaymentIntent' AND substring(NEW.canonical FROM 5 FOR 16) <> uuid_send(lineage) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV011', MESSAGE = 'V4 payment intent must name the canonical lineage';
  END IF;
  NEW.v4_prefix_digest := sha256(hash_domain || int4send(32) || prior || int8send(NEW.fact_seq)
    || int8send(NEW.effective_time_ms) || set_byte(decode('00', 'hex'), 0, fact_tag) || int4send(32) || NEW.canonical_sha256);
  RETURN NEW;
END
$$;
