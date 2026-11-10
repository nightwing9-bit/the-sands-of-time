-- Spec 02 task 2.2: Marrow Timeline journal (owner: Marrow.Timeline; schema: marrow_timeline).
-- Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- Immutable history: context_artifacts, checkpoints, steps, step_inputs accept INSERT only.
-- histories is the manifest: identity/pins/fork origin are immutable; only progress
-- (last_position, latest_state_hash, last_record_hash), status and failure_reason advance through
-- guarded transitions. Enforcement is layered: privileges for the runtime role, and triggers that
-- also stop the schema owner. Hashes are lowercase hex SHA-256 computed by Marrow.Timeline
-- (Internal/TimelineHashing.cs); the database checks their shape and chain linkage.

-- Runtime role (NOLOGIN). Application logins are granted membership by deployment, not here.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'marrow_timeline_runtime') THEN
    CREATE ROLE marrow_timeline_runtime NOLOGIN;
  END IF;
END
$$;

CREATE TABLE marrow_timeline.context_artifacts (
  context_ref            text        PRIMARY KEY,
  context_schema_version text        NOT NULL CHECK (context_schema_version <> ''),
  context_hash           text        NOT NULL CHECK (context_hash ~ '^[0-9a-f]{64}$'),
  canonical_bytes        bytea       NOT NULL,
  created_at             timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT context_artifacts_content_address CHECK (context_ref = 'sha256:' || context_hash)
);

CREATE TABLE marrow_timeline.histories (
  timeline_id            uuid        PRIMARY KEY,
  domain_id              text        NOT NULL CHECK (domain_id <> ''),
  owner_id               text        NOT NULL CHECK (owner_id <> ''),
  reducer_version        text        NOT NULL CHECK (reducer_version <> ''),
  input_schema_version   text        NOT NULL CHECK (input_schema_version <> ''),
  state_schema_version   text        NOT NULL CHECK (state_schema_version <> ''),
  context_schema_version text        NOT NULL CHECK (context_schema_version <> ''),
  context_hash           text        NOT NULL CHECK (context_hash ~ '^[0-9a-f]{64}$'),
  context_ref            text        NOT NULL REFERENCES marrow_timeline.context_artifacts (context_ref),
  rules_hash             text        NOT NULL CHECK (rules_hash <> ''),
  seed_hash              text        NOT NULL CHECK (seed_hash <> ''),
  -- Branch record: a child stores its parent, fork position T and operation key; it owns only steps > T.
  parent_timeline_id     uuid        REFERENCES marrow_timeline.histories (timeline_id),
  fork_position          bigint,
  base_position          bigint      NOT NULL,
  base_state_hash        text        NOT NULL CHECK (base_state_hash ~ '^[0-9a-f]{64}$'),
  base_record_hash       text        NOT NULL CHECK (base_record_hash ~ '^[0-9a-f]{64}$'),
  operation_key          text        NOT NULL CHECK (operation_key <> ''),
  request_fingerprint    text        NOT NULL CHECK (request_fingerprint ~ '^[0-9a-f]{64}$'),
  -- Controlled lifecycle metadata.
  last_position          bigint      NOT NULL,
  latest_state_hash      text        NOT NULL CHECK (latest_state_hash ~ '^[0-9a-f]{64}$'),
  last_record_hash       text        NOT NULL CHECK (last_record_hash ~ '^[0-9a-f]{64}$'),
  status                 text        NOT NULL CHECK (status IN ('Recording', 'Verified', 'Failed')),
  failure_reason         text,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT histories_context_ref CHECK (context_ref = 'sha256:' || context_hash),
  CONSTRAINT histories_fork_shape CHECK (
    (parent_timeline_id IS NULL AND fork_position IS NULL AND base_position = 0)
    OR (parent_timeline_id IS NOT NULL AND fork_position >= 0 AND base_position = fork_position)),
  CONSTRAINT histories_progress CHECK (last_position >= base_position),
  CONSTRAINT histories_failure_reason CHECK ((status = 'Failed') = (failure_reason IS NOT NULL))
);

-- Idempotency scope for create/fork: (owner, source, T, operation key). Sources have NULL parent/T.
CREATE UNIQUE INDEX histories_operation_scope
  ON marrow_timeline.histories (owner_id, parent_timeline_id, fork_position, operation_key) NULLS NOT DISTINCT;
CREATE INDEX histories_parent ON marrow_timeline.histories (parent_timeline_id) WHERE parent_timeline_id IS NOT NULL;

CREATE TABLE marrow_timeline.checkpoints (
  timeline_id          uuid        NOT NULL REFERENCES marrow_timeline.histories (timeline_id),
  position             bigint      NOT NULL CHECK (position >= 0),
  state_schema_version text        NOT NULL CHECK (state_schema_version <> ''),
  state_hash           text        NOT NULL CHECK (state_hash ~ '^[0-9a-f]{64}$'),
  state_bytes          bytea       NOT NULL,
  created_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (timeline_id, position)
);

CREATE TABLE marrow_timeline.steps (
  timeline_id          uuid        NOT NULL REFERENCES marrow_timeline.histories (timeline_id),
  position             bigint      NOT NULL CHECK (position >= 1),
  operation_key        text        NOT NULL CHECK (operation_key <> ''),
  input_count          integer     NOT NULL CHECK (input_count >= 0),
  state_hash           text        NOT NULL CHECK (state_hash ~ '^[0-9a-f]{64}$'),
  previous_record_hash text        NOT NULL CHECK (previous_record_hash ~ '^[0-9a-f]{64}$'),
  record_hash          text        NOT NULL CHECK (record_hash ~ '^[0-9a-f]{64}$'),
  created_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (timeline_id, position),
  CONSTRAINT steps_operation_key UNIQUE (timeline_id, operation_key)
);

-- Inputs in canonical (Ordinal, InputId) order; input_index is the 0-based canonical index.
CREATE TABLE marrow_timeline.step_inputs (
  timeline_id uuid    NOT NULL,
  position    bigint  NOT NULL,
  input_index integer NOT NULL CHECK (input_index >= 0),
  input_id    text    NOT NULL CHECK (input_id <> ''),
  ordinal     integer NOT NULL,
  payload     bytea   NOT NULL,
  PRIMARY KEY (timeline_id, position, input_index),
  CONSTRAINT step_inputs_input_id UNIQUE (timeline_id, input_id),
  FOREIGN KEY (timeline_id, position) REFERENCES marrow_timeline.steps (timeline_id, position)
);

-- SQLSTATE MT001 = Timeline history immutability/lifecycle guard.
CREATE FUNCTION marrow_timeline.reject_mutation() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  RAISE EXCEPTION USING ERRCODE = 'MT001',
    MESSAGE = format('marrow_timeline.%s is append-only: %s is not permitted', TG_TABLE_NAME, TG_OP);
END
$$;

CREATE FUNCTION marrow_timeline.guard_history_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.status <> 'Recording' OR NEW.failure_reason IS NOT NULL
     OR NEW.last_position <> NEW.base_position
     OR NEW.latest_state_hash <> NEW.base_state_hash
     OR NEW.last_record_hash <> NEW.base_record_hash THEN
    RAISE EXCEPTION USING ERRCODE = 'MT001',
      MESSAGE = 'marrow_timeline.histories: a new history must start Recording at its base record';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM marrow_timeline.context_artifacts a
                 WHERE a.context_ref = NEW.context_ref
                   AND a.context_schema_version = NEW.context_schema_version) THEN
    RAISE EXCEPTION USING ERRCODE = 'MT001',
      MESSAGE = 'marrow_timeline.histories: pinned context artifact/schema version mismatch';
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION marrow_timeline.guard_history_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.timeline_id, NEW.domain_id, NEW.owner_id, NEW.reducer_version, NEW.input_schema_version,
      NEW.state_schema_version, NEW.context_schema_version, NEW.context_hash, NEW.context_ref,
      NEW.rules_hash, NEW.seed_hash, NEW.parent_timeline_id, NEW.fork_position, NEW.base_position,
      NEW.base_state_hash, NEW.base_record_hash, NEW.operation_key, NEW.request_fingerprint, NEW.created_at)
     IS DISTINCT FROM
     (OLD.timeline_id, OLD.domain_id, OLD.owner_id, OLD.reducer_version, OLD.input_schema_version,
      OLD.state_schema_version, OLD.context_schema_version, OLD.context_hash, OLD.context_ref,
      OLD.rules_hash, OLD.seed_hash, OLD.parent_timeline_id, OLD.fork_position, OLD.base_position,
      OLD.base_state_hash, OLD.base_record_hash, OLD.operation_key, OLD.request_fingerprint, OLD.created_at) THEN
    RAISE EXCEPTION USING ERRCODE = 'MT001',
      MESSAGE = format('marrow_timeline.histories %s: identity, pinned versions and fork origin are immutable', OLD.timeline_id);
  END IF;

  IF NEW.status IS DISTINCT FROM OLD.status THEN
    IF NOT ((OLD.status = 'Recording' AND NEW.status IN ('Verified', 'Failed'))
            OR (OLD.status = 'Verified' AND NEW.status = 'Failed')) THEN
      RAISE EXCEPTION USING ERRCODE = 'MT001',
        MESSAGE = format('marrow_timeline.histories %s: illegal status transition %s -> %s', OLD.timeline_id, OLD.status, NEW.status);
    END IF;
  ELSIF NEW.failure_reason IS DISTINCT FROM OLD.failure_reason THEN
    RAISE EXCEPTION USING ERRCODE = 'MT001',
      MESSAGE = format('marrow_timeline.histories %s: failure_reason is set only by the transition to Failed', OLD.timeline_id);
  END IF;

  IF (NEW.last_position, NEW.latest_state_hash, NEW.last_record_hash)
     IS DISTINCT FROM (OLD.last_position, OLD.latest_state_hash, OLD.last_record_hash) THEN
    IF OLD.status <> 'Recording' OR NEW.status <> 'Recording' OR NEW.last_position <> OLD.last_position + 1 THEN
      RAISE EXCEPTION USING ERRCODE = 'MT001',
        MESSAGE = format('marrow_timeline.histories %s: progress may only advance by exactly one step while Recording', OLD.timeline_id);
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM marrow_timeline.steps s
        WHERE s.timeline_id = NEW.timeline_id
          AND s.position = NEW.last_position
          AND s.previous_record_hash = OLD.last_record_hash
          AND s.record_hash = NEW.last_record_hash
          AND s.state_hash = NEW.latest_state_hash
          AND s.input_count = (SELECT count(*) FROM marrow_timeline.step_inputs i
                               WHERE i.timeline_id = NEW.timeline_id AND i.position = NEW.last_position)) THEN
      RAISE EXCEPTION USING ERRCODE = 'MT001',
        MESSAGE = format('marrow_timeline.histories %s: progress must match the appended step record', OLD.timeline_id);
    END IF;
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION marrow_timeline.guard_step_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM marrow_timeline.histories h
                 WHERE h.timeline_id = NEW.timeline_id
                   AND h.status = 'Recording'
                   AND NEW.position = h.last_position + 1
                   AND NEW.previous_record_hash = h.last_record_hash) THEN
    RAISE EXCEPTION USING ERRCODE = 'MT001',
      MESSAGE = format('marrow_timeline.steps: step %s must extend a Recording history chain at its next position', NEW.position);
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION marrow_timeline.guard_step_input_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  -- Inputs may only be attached to the in-flight step (written before the manifest advances).
  IF NOT EXISTS (SELECT 1 FROM marrow_timeline.histories h
                 WHERE h.timeline_id = NEW.timeline_id AND NEW.position = h.last_position + 1) THEN
    RAISE EXCEPTION USING ERRCODE = 'MT001',
      MESSAGE = format('marrow_timeline.step_inputs: inputs of committed step %s are immutable', NEW.position);
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION marrow_timeline.guard_checkpoint_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NOT EXISTS (
      SELECT 1 FROM marrow_timeline.histories h
      WHERE h.timeline_id = NEW.timeline_id
        AND h.state_schema_version = NEW.state_schema_version
        AND ((NEW.position = h.base_position AND NEW.state_hash = h.base_state_hash)
             OR (NEW.position > h.base_position AND EXISTS (
                   SELECT 1 FROM marrow_timeline.steps s
                   WHERE s.timeline_id = NEW.timeline_id AND s.position = NEW.position
                     AND s.state_hash = NEW.state_hash)))) THEN
    RAISE EXCEPTION USING ERRCODE = 'MT001',
      MESSAGE = format('marrow_timeline.checkpoints: checkpoint %s must match the recorded state hash', NEW.position);
  END IF;
  RETURN NEW;
END
$$;

CREATE TRIGGER histories_guard_insert BEFORE INSERT ON marrow_timeline.histories
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.guard_history_insert();
CREATE TRIGGER histories_guard_update BEFORE UPDATE ON marrow_timeline.histories
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.guard_history_update();
CREATE TRIGGER histories_no_delete BEFORE DELETE ON marrow_timeline.histories
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.reject_mutation();
CREATE TRIGGER histories_no_truncate BEFORE TRUNCATE ON marrow_timeline.histories
  FOR EACH STATEMENT EXECUTE FUNCTION marrow_timeline.reject_mutation();

CREATE TRIGGER steps_guard_insert BEFORE INSERT ON marrow_timeline.steps
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.guard_step_insert();
CREATE TRIGGER step_inputs_guard_insert BEFORE INSERT ON marrow_timeline.step_inputs
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.guard_step_input_insert();
CREATE TRIGGER checkpoints_guard_insert BEFORE INSERT ON marrow_timeline.checkpoints
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.guard_checkpoint_insert();

CREATE TRIGGER context_artifacts_immutable BEFORE UPDATE OR DELETE ON marrow_timeline.context_artifacts
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.reject_mutation();
CREATE TRIGGER context_artifacts_no_truncate BEFORE TRUNCATE ON marrow_timeline.context_artifacts
  FOR EACH STATEMENT EXECUTE FUNCTION marrow_timeline.reject_mutation();
CREATE TRIGGER checkpoints_immutable BEFORE UPDATE OR DELETE ON marrow_timeline.checkpoints
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.reject_mutation();
CREATE TRIGGER checkpoints_no_truncate BEFORE TRUNCATE ON marrow_timeline.checkpoints
  FOR EACH STATEMENT EXECUTE FUNCTION marrow_timeline.reject_mutation();
CREATE TRIGGER steps_immutable BEFORE UPDATE OR DELETE ON marrow_timeline.steps
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.reject_mutation();
CREATE TRIGGER steps_no_truncate BEFORE TRUNCATE ON marrow_timeline.steps
  FOR EACH STATEMENT EXECUTE FUNCTION marrow_timeline.reject_mutation();
CREATE TRIGGER step_inputs_immutable BEFORE UPDATE OR DELETE ON marrow_timeline.step_inputs
  FOR EACH ROW EXECUTE FUNCTION marrow_timeline.reject_mutation();
CREATE TRIGGER step_inputs_no_truncate BEFORE TRUNCATE ON marrow_timeline.step_inputs
  FOR EACH STATEMENT EXECUTE FUNCTION marrow_timeline.reject_mutation();

-- Least privilege for the runtime role: read + append everywhere; UPDATE only on manifest lifecycle columns.
REVOKE ALL ON ALL TABLES IN SCHEMA marrow_timeline FROM PUBLIC;
GRANT USAGE ON SCHEMA marrow_timeline TO marrow_timeline_runtime;
GRANT SELECT, INSERT ON marrow_timeline.context_artifacts, marrow_timeline.checkpoints,
  marrow_timeline.steps, marrow_timeline.step_inputs, marrow_timeline.histories TO marrow_timeline_runtime;
GRANT UPDATE (last_position, latest_state_hash, last_record_hash, status, failure_reason, updated_at)
  ON marrow_timeline.histories TO marrow_timeline_runtime;
