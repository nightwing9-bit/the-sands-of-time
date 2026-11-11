-- Spec 08 task 8.3: the model-aware finance family (revenant-finance-reducer/3, ADR-P1-20).
-- (owner: Revenant; schema: revenant). Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
--   * scenario_run.finance_version is one of the two durable families. A run's family is fixed at opening (scenario_run already
--     accepts no UPDATE), so a V2 run can never become model-aware and a V3 run can never fall back to rules-only.
--   * A Screening fact of a V3 run must carry its inference evidence: payload schema 'revenant-inference-evidence/1', and an evidence
--     reference of that same schema whose SHA-256 is the payload's SHA-256 (the payload IS the canonical evidence). A Screening fact
--     of a V2 run must keep the evidence-free 'revenant-screening/1' shape. Any other fact kind carries no evidence. Violations raise
--     RV010 and roll back the whole screening transaction (steps, hold, case, outbox, audit). The one-screening-per-payment index of
--     0005 already makes a second, conflicting screening of the same payment impossible.
-- No existing row changes: existing runs are all 'revenant-finance-reducer/2' and their facts are evidence-free.

ALTER TABLE revenant.scenario_run
  ADD CONSTRAINT scenario_run_finance_version CHECK (finance_version IN ('revenant-finance-reducer/2', 'revenant-finance-reducer/3'));

CREATE FUNCTION revenant.guard_finance_fact_evidence() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  family text;
BEGIN
  SELECT r.finance_version INTO family FROM revenant.scenario_run r WHERE r.id = NEW.scenario_run_id;
  IF NEW.kind = 'Screening' AND family = 'revenant-finance-reducer/3' THEN
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
REVOKE ALL ON FUNCTION revenant.guard_finance_fact_evidence() FROM PUBLIC;
CREATE TRIGGER finance_fact_evidence_shape BEFORE INSERT ON revenant.finance_fact
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_finance_fact_evidence();
