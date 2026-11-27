-- Spec 10 task 10.2: Ghost Lap multiplayer authority fences (owner: GhostLap; schema: ghostlap).
-- Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- Two tables, both minimal start/authority identity, never a match log (no per-tick inputs or states are stored anywhere):
--   realtime_generation  one row per Realtime process generation; at most ONE is Active (unique partial index).
--   match_session        the durable start receipt of one multiplayer match, with its single terminal decision.
-- The process-level single-instance guard stays the existing Marrow RealtimeInstanceGuard (session advisory lock); these
-- tables are the durable fence that keeps a stale process from deciding a match after a successor retired it.
--
-- Lock order (every writer): match_session row(s) FOR UPDATE first, then the realtime_generation row. Retirement and the
-- (task 10.4) finalizer both follow it, so whichever locks a receipt first decides it and the other sees that decision.
-- Task 10.2 permits only Started -> Aborted. Started -> ResultCommitted is reserved for the task 10.4 migration, which adds
-- the result and outbox rows that must commit in the same transaction.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'ghostlap_mp_runtime') THEN
    CREATE ROLE ghostlap_mp_runtime NOLOGIN;
  END IF;
END
$$;

CREATE TABLE ghostlap.realtime_generation (
  generation_id uuid        PRIMARY KEY,
  status        text        NOT NULL CHECK (status IN ('Active', 'Retired')),
  started_at    timestamptz NOT NULL DEFAULT now(),
  retired_at    timestamptz,
  retired_by    uuid,
  CONSTRAINT realtime_generation_retirement_shape CHECK (
    (status = 'Active' AND retired_at IS NULL AND retired_by IS NULL)
    OR (status = 'Retired' AND retired_at IS NOT NULL AND retired_by IS NOT NULL AND retired_by <> generation_id))
);
CREATE UNIQUE INDEX realtime_generation_one_active ON ghostlap.realtime_generation ((true)) WHERE status = 'Active';

CREATE TABLE ghostlap.match_session (
  match_id            uuid        PRIMARY KEY,
  room_id             uuid        NOT NULL,
  generation_id       uuid        NOT NULL REFERENCES ghostlap.realtime_generation (generation_id),
  round_ordinal       integer     NOT NULL CHECK (round_ordinal >= 1),
  config_version      text        NOT NULL CHECK (config_version <> ''),
  config_digest       text        NOT NULL CHECK (config_digest ~ '^[0-9a-f]{64}$'),
  rules_id            text        NOT NULL CHECK (rules_id <> ''),
  layout_id           text        NOT NULL CHECK (layout_id <> ''),
  rules_hash          text        NOT NULL CHECK (rules_hash <> ''),
  layout_fingerprint  text        NOT NULL CHECK (layout_fingerprint ~ '^[0-9a-f]{64}$'),
  seed                bigint      NOT NULL CHECK (seed >= 0),
  rating_algorithm_id text        NOT NULL CHECK (rating_algorithm_id ~ '^[a-z0-9._-]{1,64}$'),
  target_ticks        integer     NOT NULL CHECK (target_ticks > 0),
  roster_subjects     text[]      NOT NULL CHECK (cardinality(roster_subjects) BETWEEN 2 AND 4 AND array_position(roster_subjects, NULL) IS NULL),
  terminal_decision   text        NOT NULL CHECK (terminal_decision IN ('Started', 'ResultCommitted', 'Aborted')),
  terminal_version    integer     NOT NULL,
  abort_reason        text,
  started_at          timestamptz NOT NULL DEFAULT now(),
  terminal_at         timestamptz,
  CONSTRAINT match_session_terminal_shape CHECK (
    (terminal_decision = 'Started' AND terminal_version = 0 AND abort_reason IS NULL AND terminal_at IS NULL)
    OR (terminal_decision = 'Aborted' AND terminal_version = 1 AND abort_reason <> '' AND terminal_at IS NOT NULL)
    OR (terminal_decision = 'ResultCommitted' AND terminal_version = 1 AND abort_reason IS NULL AND terminal_at IS NOT NULL))
);
CREATE INDEX match_session_started_by_generation ON ghostlap.match_session (generation_id) WHERE terminal_decision = 'Started';

-- SQLSTATE GL002 = Ghost Lap multiplayer authority fence.
CREATE FUNCTION ghostlap.guard_generation_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.status <> 'Active' THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002', MESSAGE = 'ghostlap.realtime_generation: a new generation must be Active';
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION ghostlap.guard_generation_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.generation_id IS DISTINCT FROM OLD.generation_id OR NEW.started_at IS DISTINCT FROM OLD.started_at
     OR NOT (OLD.status = 'Active' AND NEW.status = 'Retired') THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002',
      MESSAGE = format('ghostlap.realtime_generation %s: only Active -> Retired is permitted', OLD.generation_id);
  END IF;
  RETURN NEW;
END
$$;

-- A receipt can only be created Started, under a generation that is Active at that moment. The share lock on the generation row
-- serializes against retirement: if retirement committed first the row is no longer Active and the insert fails; if this insert
-- holds the lock first, retirement waits and then aborts the new receipt in its own transaction.
CREATE FUNCTION ghostlap.guard_match_session_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.terminal_decision <> 'Started' THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002', MESSAGE = 'ghostlap.match_session: a new receipt must be Started';
  END IF;
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

CREATE FUNCTION ghostlap.guard_match_session_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.match_id, NEW.room_id, NEW.generation_id, NEW.round_ordinal, NEW.config_version, NEW.config_digest, NEW.rules_id,
      NEW.layout_id, NEW.rules_hash, NEW.layout_fingerprint, NEW.seed, NEW.rating_algorithm_id, NEW.target_ticks,
      NEW.roster_subjects, NEW.started_at)
     IS DISTINCT FROM
     (OLD.match_id, OLD.room_id, OLD.generation_id, OLD.round_ordinal, OLD.config_version, OLD.config_digest, OLD.rules_id,
      OLD.layout_id, OLD.rules_hash, OLD.layout_fingerprint, OLD.seed, OLD.rating_algorithm_id, OLD.target_ticks,
      OLD.roster_subjects, OLD.started_at) THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002',
      MESSAGE = format('ghostlap.match_session %s: the sealed start identity is immutable', OLD.match_id);
  END IF;
  IF NOT (OLD.terminal_decision = 'Started' AND NEW.terminal_decision = 'Aborted') THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002',
      MESSAGE = format('ghostlap.match_session %s: only Started -> Aborted is permitted (%s -> %s)',
                       OLD.match_id, OLD.terminal_decision, NEW.terminal_decision);
  END IF;
  RETURN NEW;
END
$$;

CREATE TRIGGER realtime_generation_insert BEFORE INSERT ON ghostlap.realtime_generation
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_generation_insert();
CREATE TRIGGER realtime_generation_update BEFORE UPDATE ON ghostlap.realtime_generation
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_generation_update();
CREATE TRIGGER realtime_generation_no_delete BEFORE DELETE ON ghostlap.realtime_generation
  FOR EACH ROW EXECUTE FUNCTION ghostlap.reject_mutation();
CREATE TRIGGER realtime_generation_no_truncate BEFORE TRUNCATE ON ghostlap.realtime_generation
  FOR EACH STATEMENT EXECUTE FUNCTION ghostlap.reject_mutation();
CREATE TRIGGER match_session_insert BEFORE INSERT ON ghostlap.match_session
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_match_session_insert();
CREATE TRIGGER match_session_update BEFORE UPDATE ON ghostlap.match_session
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_match_session_update();
CREATE TRIGGER match_session_no_delete BEFORE DELETE ON ghostlap.match_session
  FOR EACH ROW EXECUTE FUNCTION ghostlap.reject_mutation();
CREATE TRIGGER match_session_no_truncate BEFORE TRUNCATE ON ghostlap.match_session
  FOR EACH STATEMENT EXECUTE FUNCTION ghostlap.reject_mutation();

-- Least privilege: the multiplayer role sees only these two tables (not the solo tables), may insert, and may update only the
-- lifecycle columns. No DELETE, no TRUNCATE. Granted to the Realtime login by scripts/db/migrate.sh (provision_login).
REVOKE ALL ON ghostlap.realtime_generation, ghostlap.match_session FROM PUBLIC;
GRANT USAGE ON SCHEMA ghostlap TO ghostlap_mp_runtime;
GRANT SELECT, INSERT ON ghostlap.realtime_generation TO ghostlap_mp_runtime;
GRANT UPDATE (status, retired_at, retired_by) ON ghostlap.realtime_generation TO ghostlap_mp_runtime;
GRANT SELECT, INSERT ON ghostlap.match_session TO ghostlap_mp_runtime;
GRANT UPDATE (terminal_decision, terminal_version, abort_reason, terminal_at) ON ghostlap.match_session TO ghostlap_mp_runtime;
