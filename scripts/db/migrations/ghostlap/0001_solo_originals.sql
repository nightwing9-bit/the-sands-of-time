-- Spec 06 task 6.3: Ghost Lap solo originals (owner: GhostLap; schema: ghostlap).
-- Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- One row per recorded solo original. The game history itself (every tick, checkpoints, record chain) lives in the
-- Marrow Timeline journal; this row holds what the Ghost Lap domain owns: the verified owner (subject and principal
-- kind taken from the server-side actor, never from a request), the pinned game identity, and the completion facts
-- written only after the 2700-tick history was completed and replay-verified. No cross-schema foreign key: timeline_id
-- is the Marrow TimelineId by value.
--
-- Lifecycle: inserted as Recording; one guarded transition Recording -> Complete sets the completion facts. Identity
-- columns never change, a Complete row never changes again, and rows are never deleted. Spec 11 adds listing on top.

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'ghostlap_runtime') THEN
    CREATE ROLE ghostlap_runtime NOLOGIN;
  END IF;
END
$$;

CREATE TABLE ghostlap.solo_originals (
  timeline_id      uuid        PRIMARY KEY,
  owner_subject_id text        NOT NULL CHECK (owner_subject_id <> ''),
  owner_kind       text        NOT NULL CHECK (owner_kind IN ('Guest', 'Staff')),
  tenant_id        text,
  operation_key    text        NOT NULL CHECK (operation_key <> ''),
  rules_version    text        NOT NULL CHECK (rules_version <> ''),
  layout_version   text        NOT NULL CHECK (layout_version <> ''),
  seed             bigint      NOT NULL,
  mascot_id        smallint    NOT NULL CHECK (mascot_id BETWEEN 0 AND 255),
  status           text        NOT NULL CHECK (status IN ('Recording', 'Complete')),
  final_tick       bigint,
  final_state_hash text        CHECK (final_state_hash ~ '^[0-9a-f]{64}$'),
  last_record_hash text        CHECK (last_record_hash ~ '^[0-9a-f]{64}$'),
  created_at       timestamptz NOT NULL DEFAULT now(),
  completed_at     timestamptz,
  CONSTRAINT solo_originals_owner_operation UNIQUE (owner_subject_id, operation_key),
  CONSTRAINT solo_originals_completion_shape CHECK (
    (status = 'Recording' AND final_tick IS NULL AND final_state_hash IS NULL AND last_record_hash IS NULL AND completed_at IS NULL)
    OR (status = 'Complete' AND final_tick > 0 AND final_state_hash IS NOT NULL AND last_record_hash IS NOT NULL
        AND completed_at IS NOT NULL))
);

-- SQLSTATE GL001 = Ghost Lap solo-original immutability/lifecycle guard.
CREATE FUNCTION ghostlap.reject_mutation() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  RAISE EXCEPTION USING ERRCODE = 'GL001',
    MESSAGE = format('ghostlap.%s: %s is not permitted', TG_TABLE_NAME, TG_OP);
END
$$;

CREATE FUNCTION ghostlap.guard_solo_original_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.status <> 'Recording' THEN
    RAISE EXCEPTION USING ERRCODE = 'GL001',
      MESSAGE = 'ghostlap.solo_originals: a new original must start Recording';
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION ghostlap.guard_solo_original_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.timeline_id, NEW.owner_subject_id, NEW.owner_kind, NEW.tenant_id, NEW.operation_key, NEW.rules_version,
      NEW.layout_version, NEW.seed, NEW.mascot_id, NEW.created_at)
     IS DISTINCT FROM
     (OLD.timeline_id, OLD.owner_subject_id, OLD.owner_kind, OLD.tenant_id, OLD.operation_key, OLD.rules_version,
      OLD.layout_version, OLD.seed, OLD.mascot_id, OLD.created_at) THEN
    RAISE EXCEPTION USING ERRCODE = 'GL001',
      MESSAGE = format('ghostlap.solo_originals %s: owner and game identity are immutable', OLD.timeline_id);
  END IF;
  IF NOT (OLD.status = 'Recording' AND NEW.status = 'Complete') THEN
    RAISE EXCEPTION USING ERRCODE = 'GL001',
      MESSAGE = format('ghostlap.solo_originals %s: only Recording -> Complete is permitted (%s -> %s)',
                       OLD.timeline_id, OLD.status, NEW.status);
  END IF;
  RETURN NEW;
END
$$;

CREATE TRIGGER solo_originals_guard_insert BEFORE INSERT ON ghostlap.solo_originals
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_solo_original_insert();
CREATE TRIGGER solo_originals_guard_update BEFORE UPDATE ON ghostlap.solo_originals
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_solo_original_update();
CREATE TRIGGER solo_originals_no_delete BEFORE DELETE ON ghostlap.solo_originals
  FOR EACH ROW EXECUTE FUNCTION ghostlap.reject_mutation();
CREATE TRIGGER solo_originals_no_truncate BEFORE TRUNCATE ON ghostlap.solo_originals
  FOR EACH STATEMENT EXECUTE FUNCTION ghostlap.reject_mutation();

-- Least privilege for the runtime role: read + insert; UPDATE only on the completion columns.
REVOKE ALL ON ALL TABLES IN SCHEMA ghostlap FROM PUBLIC;
GRANT USAGE ON SCHEMA ghostlap TO ghostlap_runtime;
GRANT SELECT, INSERT ON ghostlap.solo_originals TO ghostlap_runtime;
GRANT UPDATE (status, final_tick, final_state_hash, last_record_hash, completed_at)
  ON ghostlap.solo_originals TO ghostlap_runtime;
