-- Spec 06 task 6.4: Ghost Lap solo branches (owner: GhostLap; schema: ghostlap).
-- Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- One row per branch forked from a complete solo original at a nonterminal tick. The branch history itself (shared
-- source prefix through fork_tick, its own steps fork_tick+1..) lives in the Marrow Timeline journal as a child of the
-- source; this row holds what the Ghost Lap domain owns: the verified owner (from the server-side actor), the source and
-- exact fork tick, the pinned game identity copied from the source original, and completion facts written only after the
-- branch reached the final tick and replay-verified. The source original row and its Timeline history are never changed.
--
-- Lifecycle: inserted as Recording; one guarded transition Recording -> Complete. Identity columns never change, a
-- Complete row never changes again, and rows are never deleted. source_timeline_id references the same-schema original.
CREATE TABLE ghostlap.solo_branches (
  timeline_id        uuid        PRIMARY KEY,
  source_timeline_id uuid        NOT NULL REFERENCES ghostlap.solo_originals (timeline_id),
  fork_tick          bigint      NOT NULL CHECK (fork_tick >= 0),
  owner_subject_id   text        NOT NULL CHECK (owner_subject_id <> ''),
  owner_kind         text        NOT NULL CHECK (owner_kind IN ('Guest', 'Staff')),
  tenant_id          text,
  operation_key      text        NOT NULL CHECK (operation_key <> ''),
  rules_version      text        NOT NULL CHECK (rules_version <> ''),
  layout_version     text        NOT NULL CHECK (layout_version <> ''),
  seed               bigint      NOT NULL,
  mascot_id          smallint    NOT NULL CHECK (mascot_id BETWEEN 0 AND 255),
  status             text        NOT NULL CHECK (status IN ('Recording', 'Complete')),
  final_tick         bigint,
  final_state_hash   text        CHECK (final_state_hash ~ '^[0-9a-f]{64}$'),
  last_record_hash   text        CHECK (last_record_hash ~ '^[0-9a-f]{64}$'),
  created_at         timestamptz NOT NULL DEFAULT now(),
  completed_at       timestamptz,
  CONSTRAINT solo_branches_not_source CHECK (timeline_id <> source_timeline_id),
  CONSTRAINT solo_branches_owner_operation UNIQUE (owner_subject_id, operation_key),
  CONSTRAINT solo_branches_completion_shape CHECK (
    (status = 'Recording' AND final_tick IS NULL AND final_state_hash IS NULL AND last_record_hash IS NULL AND completed_at IS NULL)
    OR (status = 'Complete' AND final_tick > fork_tick AND final_state_hash IS NOT NULL AND last_record_hash IS NOT NULL
        AND completed_at IS NOT NULL))
);
CREATE INDEX solo_branches_source ON ghostlap.solo_branches (source_timeline_id);
CREATE FUNCTION ghostlap.guard_solo_branch_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.status <> 'Recording' THEN
    RAISE EXCEPTION USING ERRCODE = 'GL001',
      MESSAGE = 'ghostlap.solo_branches: a new branch must start Recording';
  END IF;
  RETURN NEW;
END
$$;
CREATE FUNCTION ghostlap.guard_solo_branch_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.timeline_id, NEW.source_timeline_id, NEW.fork_tick, NEW.owner_subject_id, NEW.owner_kind, NEW.tenant_id,
      NEW.operation_key, NEW.rules_version, NEW.layout_version, NEW.seed, NEW.mascot_id, NEW.created_at)
     IS DISTINCT FROM
     (OLD.timeline_id, OLD.source_timeline_id, OLD.fork_tick, OLD.owner_subject_id, OLD.owner_kind, OLD.tenant_id,
      OLD.operation_key, OLD.rules_version, OLD.layout_version, OLD.seed, OLD.mascot_id, OLD.created_at) THEN
    RAISE EXCEPTION USING ERRCODE = 'GL001',
      MESSAGE = format('ghostlap.solo_branches %s: source, fork tick, owner and game identity are immutable', OLD.timeline_id);
  END IF;
  IF NOT (OLD.status = 'Recording' AND NEW.status = 'Complete') THEN
    RAISE EXCEPTION USING ERRCODE = 'GL001',
      MESSAGE = format('ghostlap.solo_branches %s: only Recording -> Complete is permitted (%s -> %s)',
                       OLD.timeline_id, OLD.status, NEW.status);
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER solo_branches_guard_insert BEFORE INSERT ON ghostlap.solo_branches
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_solo_branch_insert();
CREATE TRIGGER solo_branches_guard_update BEFORE UPDATE ON ghostlap.solo_branches
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_solo_branch_update();
CREATE TRIGGER solo_branches_no_delete BEFORE DELETE ON ghostlap.solo_branches
  FOR EACH ROW EXECUTE FUNCTION ghostlap.reject_mutation();
CREATE TRIGGER solo_branches_no_truncate BEFORE TRUNCATE ON ghostlap.solo_branches
  FOR EACH STATEMENT EXECUTE FUNCTION ghostlap.reject_mutation();
-- Least privilege for the runtime role: read + insert; UPDATE only on the completion columns.
REVOKE ALL ON ghostlap.solo_branches FROM PUBLIC;
GRANT SELECT, INSERT ON ghostlap.solo_branches TO ghostlap_runtime;
GRANT UPDATE (status, final_tick, final_state_hash, last_record_hash, completed_at)
  ON ghostlap.solo_branches TO ghostlap_runtime;
