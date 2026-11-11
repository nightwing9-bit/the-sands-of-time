-- Spec 07 exit review: a run is closed for new financial facts before its Timeline source can be verified.
-- (owner: Revenant; schema: revenant). Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
--   * run_cursor.closed_fact_seq: NULL while the run is open. Closing sets it once to the final committed fact sequence, in a
--     transaction that holds the cursor row lock. Every fact append (intake, screening, review, settlement, return) advances the
--     cursor with an UPDATE of that same row, so a late action serializes behind the close and is then refused by this trigger
--     (RV009) and rolls back with its whole transaction: no late fact, status step, posting or outbox event can commit.
--   * payment_run_open: the payments of a closed run accept no insert or status change (covers a step that records no fact).
--     This is a read of the cursor without a lock, to avoid lock-order conflicts with the fact path; it is defense in depth, the
--     cursor guard is the serialization point.
--   * Closing is permanent: the cursor row of a closed run accepts no further UPDATE.
-- The replay role reads the column (table SELECT) and cannot write it; the writer may set only this column in addition.

ALTER TABLE revenant.run_cursor
  ADD COLUMN closed_fact_seq bigint NULL CHECK (closed_fact_seq IS NULL OR closed_fact_seq >= 0);

CREATE OR REPLACE FUNCTION revenant.guard_run_cursor_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.scenario_run_id, NEW.tenant_id) IS DISTINCT FROM (OLD.scenario_run_id, OLD.tenant_id) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV004',
      MESSAGE = format('revenant.run_cursor %s: the cursor never changes identity', OLD.scenario_run_id);
  END IF;
  IF OLD.closed_fact_seq IS NOT NULL THEN
    RAISE EXCEPTION USING ERRCODE = 'RV009',
      MESSAGE = format('revenant.run_cursor %s: the run was closed at fact %s and accepts no further financial fact', OLD.scenario_run_id, OLD.closed_fact_seq);
  END IF;
  IF NEW.closed_fact_seq IS NOT NULL THEN
    -- Closing: nothing else moves, and the closing point is exactly the last committed fact.
    IF NEW.last_fact_seq <> OLD.last_fact_seq OR NEW.closed_fact_seq <> OLD.last_fact_seq
       OR NEW.last_intent_sequence <> OLD.last_intent_sequence OR NEW.last_event_time_ms <> OLD.last_event_time_ms THEN
      RAISE EXCEPTION USING ERRCODE = 'RV004',
        MESSAGE = format('revenant.run_cursor %s: closing records the last committed fact and changes nothing else', OLD.scenario_run_id);
    END IF;
    RETURN NEW;
  END IF;
  IF NEW.last_fact_seq <> OLD.last_fact_seq + 1
     OR NEW.last_intent_sequence < OLD.last_intent_sequence
     OR NEW.last_event_time_ms < OLD.last_event_time_ms THEN
    RAISE EXCEPTION USING ERRCODE = 'RV004',
      MESSAGE = format('revenant.run_cursor %s: the cursor only advances by exactly one fact and never moves back', OLD.scenario_run_id);
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION revenant.guard_payment_run_open() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM revenant.run_cursor c WHERE c.scenario_run_id = NEW.scenario_run_id AND c.closed_fact_seq IS NOT NULL) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV009',
      MESSAGE = format('revenant.payment %s: the run is closed and accepts no payment change', NEW.id);
  END IF;
  RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION revenant.guard_payment_run_open() FROM PUBLIC;
CREATE TRIGGER payment_run_open BEFORE INSERT OR UPDATE ON revenant.payment
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_payment_run_open();

GRANT UPDATE (closed_fact_seq) ON revenant.run_cursor TO revenant_ledger_writer;
