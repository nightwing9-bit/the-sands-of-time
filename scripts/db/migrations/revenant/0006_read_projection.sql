-- Spec 07 exit review (R07.09): the idempotent read projection of the financial outbox events
-- (owner: Revenant; schema: revenant). Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
--   * payment_view, hold_view, posting_view: the minimal durable read model of payment, hold and settlement events. They are derived
--     data, never the authority: balances, statuses and postings stay in the ledger tables. Rows carry ids, statuses and amounts only
--     (the same payload the shared outbox carries), so they are system tables like outbox/inbox, not tenant tables.
--   * Ordering: payment_view only moves to a strictly higher payment version; hold_view only moves Active -> terminal; posting_view
--     rows are immutable (one per ledger group). A late or duplicated event therefore cannot move a row backwards.
--   * revenant_read_projector (NOLOGIN, NOBYPASSRLS): SELECT/INSERT/UPDATE on the three views and INSERT on the inbox, nothing
--     else. It cannot read or write any ledger, fact, case or audit table. migrate.sh grants it to the sands_worker login, which
--     already reads the ledger only through revenant_replay_reader.
-- No cross-schema foreign key.

CREATE TABLE revenant.payment_view (
  payment_id       uuid PRIMARY KEY,
  scenario_run_id  uuid NOT NULL,
  intent_id        text NOT NULL,
  status           text NOT NULL,
  version          bigint NOT NULL CHECK (version > 0),
  last_event_id    uuid NOT NULL,
  updated_utc      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX payment_view_run_idx ON revenant.payment_view (scenario_run_id);

CREATE TABLE revenant.hold_view (
  hold_id          uuid PRIMARY KEY,
  scenario_run_id  uuid NOT NULL,
  payment_id       uuid NOT NULL,
  status           text NOT NULL CHECK (status IN ('Active', 'Consumed', 'Released')),
  amount_minor     bigint NOT NULL CHECK (amount_minor > 0),
  last_event_id    uuid NOT NULL,
  updated_utc      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX hold_view_run_idx ON revenant.hold_view (scenario_run_id);

CREATE TABLE revenant.posting_view (
  group_id             uuid PRIMARY KEY,
  scenario_run_id      uuid NOT NULL,
  payment_id           uuid NOT NULL,
  purpose              text NOT NULL,
  currency             text NOT NULL,
  amount_minor         bigint NOT NULL CHECK (amount_minor > 0),
  compensates_group_id uuid NULL,
  event_id             uuid NOT NULL,
  recorded_utc         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX posting_view_run_idx ON revenant.posting_view (scenario_run_id);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'revenant_read_projector') THEN
    CREATE ROLE revenant_read_projector NOLOGIN NOBYPASSRLS;
  ELSE
    ALTER ROLE revenant_read_projector NOLOGIN NOBYPASSRLS;
  END IF;
END
$$;

REVOKE ALL ON revenant.payment_view, revenant.hold_view, revenant.posting_view FROM PUBLIC;
GRANT USAGE ON SCHEMA revenant TO revenant_read_projector;
GRANT SELECT, INSERT, UPDATE ON revenant.payment_view, revenant.hold_view TO revenant_read_projector;
GRANT SELECT, INSERT ON revenant.posting_view TO revenant_read_projector;
GRANT INSERT ON revenant.inbox TO revenant_read_projector;
