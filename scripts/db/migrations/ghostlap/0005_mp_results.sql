-- Spec 10 task 10.4: durable multiplayer results and the MatchFinalizedV1 outbox (owner: GhostLap; schema: ghostlap).
-- Applied only by scripts/db/migrate.sh, inside one transaction with its history row. Decision record: ADR-P1-26.
--
--   match_result  one immutable row per finalized match (unique match_id), written in the SAME transaction that moves the receipt
--                 Started -> ResultCommitted and inserts the one MatchFinalizedV1 outbox row.
--   outbox        Outbox v1 in the Marrow MessagingTables shape (src/Marrow.Infrastructure/Messaging/MessagingTables.cs), so the shared
--                 PostgresOutboxWriter and OutboxRelay work unchanged. Delivery is at-least-once; Spec 12 owns the rating effect.
--
-- Lock order is unchanged from 0003/0004: the receipt row FOR UPDATE first, then the generation row. The finalizer, the result insert
-- trigger (which locks the receipt itself, for any direct SQL), the fenced abort and the successor's retirement all follow it, so
-- whichever transaction locks a receipt first decides it and the other sees that decision.
--
-- Database invariants (SQLSTATE GL002 = authority fence, GL003 = result consistency, GL001 = immutability):
--   * a result row can be inserted only while its receipt is Started under an Active generation, and must repeat the receipt's sealed
--     room, generation, config digest, rating algorithm id, roster and target tick; its placements must follow mp-placement-v1;
--   * Started -> ResultCommitted requires the result row and the MatchFinalizedV1 outbox row to exist already (same transaction);
--   * at commit (deferred): a result row needs a ResultCommitted receipt and its outbox row; a MatchFinalizedV1 outbox row needs the
--     result row with the same digest and document. An Aborted receipt therefore can never have a result or a rankable event;
--   * results are immutable and undeletable; outbox business columns are immutable; nothing here can be deleted or truncated.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'ghostlap_outbox_relay') THEN
    CREATE ROLE ghostlap_outbox_relay NOLOGIN NOBYPASSRLS;
  END IF;
END
$$;

CREATE TABLE ghostlap.outbox (
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
CREATE INDEX outbox_pending_idx ON ghostlap.outbox (next_attempt_at, seq) WHERE status = 'pending';
-- At most one MatchFinalizedV1 per match, whatever its event id.
CREATE UNIQUE INDEX outbox_one_match_finalized ON ghostlap.outbox (aggregate_id) WHERE event_type = 'ghostlap.MatchFinalizedV1';

CREATE TABLE ghostlap.match_result (
  match_id            uuid        PRIMARY KEY REFERENCES ghostlap.match_session (match_id),
  room_id             uuid        NOT NULL,
  generation_id       uuid        NOT NULL,
  config_digest       text        NOT NULL,
  result_schema       text        NOT NULL CHECK (result_schema = 'ghost-mp-result/1'),
  result_digest       text        NOT NULL CHECK (result_digest ~ '^[0-9a-f]{64}$'),
  rating_algorithm_id text        NOT NULL,
  roster_subjects     text[]      NOT NULL,
  scores              integer[]   NOT NULL,
  forfeited           boolean[]   NOT NULL,
  placements          integer[]   NOT NULL,
  final_tick          integer     NOT NULL,
  placement_policy    text        NOT NULL CHECK (placement_policy = 'mp-placement-v1'),
  eligibility_policy  text        NOT NULL CHECK (eligibility_policy = 'mp-eligibility-v1'),
  rated_eligible      boolean     NOT NULL,
  eligibility_reason  text        NOT NULL CHECK (eligibility_reason IN ('eligible', 'no_finisher', 'fewer_than_two_guest_principals')),
  result_document     text        NOT NULL,
  committed_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT match_result_shape CHECK (
    cardinality(scores) = cardinality(roster_subjects)
    AND cardinality(forfeited) = cardinality(roster_subjects)
    AND cardinality(placements) = cardinality(roster_subjects)
    AND array_position(scores, NULL) IS NULL
    AND array_position(forfeited, NULL) IS NULL
    AND array_position(placements, NULL) IS NULL
    AND rated_eligible = (eligibility_reason = 'eligible')
    AND (eligibility_reason <> 'no_finisher' OR true = ALL (forfeited))
    AND (eligibility_reason = 'no_finisher' OR false = ANY (forfeited)))
);

-- The insert locks the receipt row itself (FOR UPDATE; a no-op for the finalizer, which already holds it), then share-locks the generation:
-- the same order as every other terminal writer, also for direct SQL.
CREATE FUNCTION ghostlap.guard_match_result_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  r ghostlap.match_session%ROWTYPE;
BEGIN
  SELECT * INTO r FROM ghostlap.match_session WHERE match_id = NEW.match_id FOR UPDATE;
  IF NOT FOUND OR r.terminal_decision <> 'Started' THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002',
      MESSAGE = format('ghostlap.match_result %s: the receipt is not Started', NEW.match_id);
  END IF;
  PERFORM 1 FROM ghostlap.realtime_generation WHERE generation_id = r.generation_id AND status = 'Active' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION USING ERRCODE = 'GL002',
      MESSAGE = format('ghostlap.match_result %s: generation %s is not active', NEW.match_id, r.generation_id);
  END IF;
  IF (NEW.room_id, NEW.generation_id, NEW.config_digest, NEW.rating_algorithm_id, NEW.roster_subjects, NEW.final_tick)
     IS DISTINCT FROM (r.room_id, r.generation_id, r.config_digest, r.rating_algorithm_id, r.roster_subjects, r.target_ticks) THEN
    RAISE EXCEPTION USING ERRCODE = 'GL003',
      MESSAGE = format('ghostlap.match_result %s: the result does not repeat the sealed start identity', NEW.match_id);
  END IF;
  -- mp-placement-v1: placement = 1 + number of players strictly ahead (finisher ahead of forfeit; within a class, higher score ahead).
  IF EXISTS (
    SELECT 1
      FROM unnest(NEW.placements, NEW.forfeited, NEW.scores) WITH ORDINALITY AS a(placement, forfeit, score, i)
     WHERE a.placement <> 1 + (
       SELECT count(*)
         FROM unnest(NEW.forfeited, NEW.scores) WITH ORDINALITY AS b(forfeit, score, j)
        WHERE b.j <> a.i AND ((NOT b.forfeit AND a.forfeit) OR (b.forfeit = a.forfeit AND b.score > a.score)))) THEN
    RAISE EXCEPTION USING ERRCODE = 'GL003',
      MESSAGE = format('ghostlap.match_result %s: placements do not follow mp-placement-v1', NEW.match_id);
  END IF;
  RETURN NEW;
END
$$;

-- Deferred: at commit a result row must belong to a ResultCommitted receipt and have its outbox row.
CREATE FUNCTION ghostlap.check_match_result_committed() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM ghostlap.match_session WHERE match_id = NEW.match_id AND terminal_decision = 'ResultCommitted')
     OR NOT EXISTS (SELECT 1 FROM ghostlap.outbox
                     WHERE event_type = 'ghostlap.MatchFinalizedV1' AND aggregate_id = NEW.match_id::text) THEN
    RAISE EXCEPTION USING ERRCODE = 'GL003',
      MESSAGE = format('ghostlap.match_result %s: needs a ResultCommitted receipt and its MatchFinalizedV1 in the same transaction', NEW.match_id);
  END IF;
  RETURN NULL;
END
$$;

-- Deferred: a MatchFinalizedV1 row must carry exactly the committed result (same match, digest and document).
CREATE FUNCTION ghostlap.check_match_finalized_outbox() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  digest text;
  document text;
  body jsonb;
BEGIN
  SELECT result_digest, result_document INTO digest, document FROM ghostlap.match_result WHERE match_id::text = NEW.aggregate_id;
  body := NEW.payload::jsonb;
  IF digest IS NULL OR NEW.event_version <> 1
     OR (body ->> 'matchId') IS DISTINCT FROM replace(NEW.aggregate_id, '-', '')
     OR (body ->> 'resultDigest') IS DISTINCT FROM digest
     OR (body -> 'result') IS DISTINCT FROM document::jsonb THEN
    RAISE EXCEPTION USING ERRCODE = 'GL003',
      MESSAGE = format('ghostlap.outbox %s: MatchFinalizedV1 does not match the committed result of %s', NEW.event_id, NEW.aggregate_id);
  END IF;
  RETURN NULL;
END
$$;

-- Replaces the 0003 version: Started -> ResultCommitted is now permitted, but only after the result and outbox rows exist.
CREATE OR REPLACE FUNCTION ghostlap.guard_match_session_update() RETURNS trigger
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
  IF OLD.terminal_decision = 'Started' AND NEW.terminal_decision = 'Aborted' THEN
    RETURN NEW;
  END IF;
  IF OLD.terminal_decision = 'Started' AND NEW.terminal_decision = 'ResultCommitted' THEN
    IF NOT EXISTS (SELECT 1 FROM ghostlap.match_result WHERE match_id = NEW.match_id)
       OR NOT EXISTS (SELECT 1 FROM ghostlap.outbox
                       WHERE event_type = 'ghostlap.MatchFinalizedV1' AND aggregate_id = NEW.match_id::text) THEN
      RAISE EXCEPTION USING ERRCODE = 'GL002',
        MESSAGE = format('ghostlap.match_session %s: ResultCommitted needs its result and MatchFinalizedV1 rows first', OLD.match_id);
    END IF;
    RETURN NEW;
  END IF;
  RAISE EXCEPTION USING ERRCODE = 'GL002',
    MESSAGE = format('ghostlap.match_session %s: only Started -> Aborted or Started -> ResultCommitted is permitted (%s -> %s)',
                     OLD.match_id, OLD.terminal_decision, NEW.terminal_decision);
END
$$;

CREATE FUNCTION ghostlap.guard_outbox_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.seq, NEW.event_id, NEW.event_type, NEW.event_version, NEW.aggregate_id, NEW.occurred_utc, NEW.payload, NEW.correlation_id,
      NEW.enqueued_at)
     IS DISTINCT FROM
     (OLD.seq, OLD.event_id, OLD.event_type, OLD.event_version, OLD.aggregate_id, OLD.occurred_utc, OLD.payload, OLD.correlation_id,
      OLD.enqueued_at) THEN
    RAISE EXCEPTION USING ERRCODE = 'GL001', MESSAGE = format('ghostlap.outbox %s: the event is immutable', OLD.event_id);
  END IF;
  RETURN NEW;
END
$$;

CREATE TRIGGER match_result_insert BEFORE INSERT ON ghostlap.match_result
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_match_result_insert();
CREATE CONSTRAINT TRIGGER match_result_committed AFTER INSERT ON ghostlap.match_result
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ghostlap.check_match_result_committed();
CREATE TRIGGER match_result_no_update BEFORE UPDATE OR DELETE ON ghostlap.match_result
  FOR EACH ROW EXECUTE FUNCTION ghostlap.reject_mutation();
CREATE TRIGGER match_result_no_truncate BEFORE TRUNCATE ON ghostlap.match_result
  FOR EACH STATEMENT EXECUTE FUNCTION ghostlap.reject_mutation();
CREATE CONSTRAINT TRIGGER outbox_match_finalized_consistent AFTER INSERT ON ghostlap.outbox
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.event_type = 'ghostlap.MatchFinalizedV1')
  EXECUTE FUNCTION ghostlap.check_match_finalized_outbox();
CREATE TRIGGER outbox_update BEFORE UPDATE ON ghostlap.outbox
  FOR EACH ROW EXECUTE FUNCTION ghostlap.guard_outbox_update();
CREATE TRIGGER outbox_no_delete BEFORE DELETE ON ghostlap.outbox
  FOR EACH ROW EXECUTE FUNCTION ghostlap.reject_mutation();
CREATE TRIGGER outbox_no_truncate BEFORE TRUNCATE ON ghostlap.outbox
  FOR EACH STATEMENT EXECUTE FUNCTION ghostlap.reject_mutation();

-- Least privilege. The Realtime finalizer may insert results and outbox rows and read results; of the outbox it may read only the two
-- columns the fence triggers check (it never reads payloads back). The relay role (Worker, ConnectionStrings:GhostLapRelay) gets the
-- shared OutboxRelay grants and nothing else; it cannot read results or receipts. No role gets DELETE or TRUNCATE.
REVOKE ALL ON ghostlap.match_result, ghostlap.outbox FROM PUBLIC;
GRANT SELECT, INSERT ON ghostlap.match_result TO ghostlap_mp_runtime;
GRANT INSERT ON ghostlap.outbox TO ghostlap_mp_runtime;
GRANT SELECT (event_type, aggregate_id) ON ghostlap.outbox TO ghostlap_mp_runtime;
GRANT USAGE ON SCHEMA ghostlap TO ghostlap_outbox_relay;
GRANT SELECT ON ghostlap.outbox TO ghostlap_outbox_relay;
GRANT UPDATE (status, attempts, next_attempt_at, lease_token, lease_until, last_error, published_at, stream_entry_id, poisoned_at)
  ON ghostlap.outbox TO ghostlap_outbox_relay;
