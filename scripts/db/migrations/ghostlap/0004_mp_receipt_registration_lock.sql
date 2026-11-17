-- MatchAuthorityStore.RegisterGenerationAsync takes this key exclusively before scanning Started receipts.
-- Take it in shared mode before any row lock so the scan includes every insert that won the race with registration.
-- The transaction releases the lock on commit or rollback, including for direct SQL and ON CONFLICT inserts.
CREATE OR REPLACE FUNCTION ghostlap.guard_match_session_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.terminal_decision <> 'Started' THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002', MESSAGE = 'ghostlap.match_session: a new receipt must be Started';
  END IF;

  PERFORM pg_advisory_xact_lock_shared(0x474C4D5047454E31::bigint); -- GLMPGEN1, also used by MatchAuthorityStore

  PERFORM 1 FROM ghostlap.realtime_generation
   WHERE generation_id = NEW.generation_id AND status = 'Active'
   FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002',
      MESSAGE = format('ghostlap.match_session %s: generation %s is not active', NEW.match_id, NEW.generation_id);
  END IF;
  RETURN NEW;
END
$$;
