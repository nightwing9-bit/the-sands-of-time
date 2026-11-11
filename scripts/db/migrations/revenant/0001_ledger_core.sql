-- Spec 07 task 7.1: Revenant durable ledger core (owner: Revenant; schema: revenant).
-- Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- What the database itself guarantees (no application check is trusted and there is no bypass flag):
--   * Every synthetic bank/account/payment/posting row belongs to exactly one tenant and one ScenarioRunId. Composite
--     foreign keys (id, tenant_id, scenario_run_id, currency) keep a payment, its posting group and every leg inside
--     one run namespace, one tenant and one currency. A cross-currency transfer cannot be expressed (no FX path).
--   * Payment intake is idempotent per (scenario_run_id, intent_id). Payment rows start Initiated at version 1; each
--     UPDATE must be one legal lifecycle step with version + 1; identity/amount columns never change; no DELETE.
--   * Posting groups and legs are append-only (UPDATE/DELETE/TRUNCATE raise RV001 even for the schema owner; the
--     runtime role additionally has no UPDATE/DELETE privilege). Legs can only be added in the transaction that
--     created their group, so a committed group is sealed. Corrections are new compensating (Return) groups.
--   * At commit, a DEFERRABLE INITIALLY DEFERRED constraint trigger validates each new group: at least one debit and
--     one credit leg, every leg nonzero (CHECK), one currency, debits = credits, total = the linked payment amount,
--     and legs on the payment's own accounts in the right direction (Settlement: credit source, debit destination;
--     Return: the mirror image, compensating that payment's Settlement group).
--   * At commit, a second constraint trigger ties payment status to postings: Settled <=> exactly a Settlement group
--     and no Return group; Returned after settlement <=> both; any other status <=> no group. So Settled and its
--     balanced group can only commit in the same transaction, and no orphan Settled status or orphan group exists.
--     SET CONSTRAINTS ... IMMEDIATE only makes the check earlier (and stricter), never skips it.
--
-- Asset accounting: posted balance = opening_balance_minor + sum(debits) - sum(credits) per account (view
-- revenant.account_posted_balance). A -> B transfer credits A and debits B, so balanced groups conserve the sum.
--
-- Roles (NOLOGIN; logins are provisioned outside migrations, Spec 07 task 7.5):
--   revenant_ledger_writer  LiveOps finance writer: SELECT/INSERT, UPDATE(status, version) on payment only.
--   revenant_replay_reader  future Replay mode (Spec 09): SELECT only; cannot write any revenant table.
--   The migration owner (the executor's superuser) owns every table; runtime roles are neither owner nor BYPASSRLS.
-- Row level security (ENABLE + FORCE) on every tenant-owned table here, keyed on the transaction-local setting
-- revenant.tenant_id (set with SET LOCAL / set_config(..., true)). Unset context sees and writes nothing.
-- revenant.schema_migrations is a shared system table and is NOT tenant-isolated (runtime roles have no access).
--
-- SQLSTATEs: RV001 immutability, RV002 posting group invalid, RV003 payment lifecycle/posting link invalid.
-- No cross-schema foreign key. Holds, cases, approvals, audit chain and outbox/inbox arrive in later 07 migrations.

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'revenant_ledger_writer') THEN
    CREATE ROLE revenant_ledger_writer NOLOGIN NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'revenant_replay_reader') THEN
    CREATE ROLE revenant_replay_reader NOLOGIN NOBYPASSRLS;
  END IF;
END
$$;

CREATE TABLE revenant.bank (
  id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id uuid        NOT NULL,
  local_bank_id   text        NOT NULL CHECK (local_bank_id <> ''),
  name            text        NOT NULL CHECK (name <> ''),
  created_at      timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT bank_run_local UNIQUE (scenario_run_id, local_bank_id),
  CONSTRAINT bank_scope UNIQUE (id, tenant_id, scenario_run_id)
);

CREATE TABLE revenant.account (
  id                    uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id             text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id       uuid        NOT NULL,
  bank_id               uuid        NOT NULL,
  local_account_id      text        NOT NULL CHECK (local_account_id <> ''),
  currency              text        NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  opening_balance_minor bigint      NOT NULL CHECK (opening_balance_minor >= 0),
  created_at            timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT account_run_local UNIQUE (scenario_run_id, local_account_id),
  CONSTRAINT account_scope UNIQUE (id, tenant_id, scenario_run_id, currency),
  CONSTRAINT account_bank FOREIGN KEY (bank_id, tenant_id, scenario_run_id)
    REFERENCES revenant.bank (id, tenant_id, scenario_run_id)
);

CREATE TABLE revenant.payment (
  id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id         text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id   uuid        NOT NULL,
  intent_id         text        NOT NULL CHECK (intent_id <> ''),
  source_account_id uuid        NOT NULL,
  dest_account_id   uuid        NOT NULL,
  currency          text        NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  amount_minor      bigint      NOT NULL CHECK (amount_minor > 0),
  status            text        NOT NULL CHECK (status IN
                      ('Initiated', 'Screened', 'Held', 'Cleared', 'Approved', 'Rejected', 'Returned', 'Settled')),
  version           bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
  created_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT payment_run_intent UNIQUE (scenario_run_id, intent_id),
  CONSTRAINT payment_scope UNIQUE (id, tenant_id, scenario_run_id, currency),
  CONSTRAINT payment_distinct_accounts CHECK (source_account_id <> dest_account_id),
  CONSTRAINT payment_source FOREIGN KEY (source_account_id, tenant_id, scenario_run_id, currency)
    REFERENCES revenant.account (id, tenant_id, scenario_run_id, currency),
  CONSTRAINT payment_dest FOREIGN KEY (dest_account_id, tenant_id, scenario_run_id, currency)
    REFERENCES revenant.account (id, tenant_id, scenario_run_id, currency)
);

CREATE TABLE revenant.ledger_group (
  id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id            text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id      uuid        NOT NULL,
  payment_id           uuid        NOT NULL,
  currency             text        NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  purpose              text        NOT NULL CHECK (purpose IN ('Settlement', 'Return')),
  compensates_group_id uuid,
  -- Forced by trigger to the creating top-level transaction; legs may only be added by that transaction.
  created_txid         xid8        NOT NULL DEFAULT pg_current_xact_id(),
  created_at           timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ledger_group_scope UNIQUE (id, tenant_id, scenario_run_id, currency),
  CONSTRAINT ledger_group_payment_pair UNIQUE (id, payment_id),
  CONSTRAINT ledger_group_compensation_shape CHECK ((purpose = 'Settlement') = (compensates_group_id IS NULL)),
  CONSTRAINT ledger_group_payment FOREIGN KEY (payment_id, tenant_id, scenario_run_id, currency)
    REFERENCES revenant.payment (id, tenant_id, scenario_run_id, currency),
  CONSTRAINT ledger_group_compensates FOREIGN KEY (compensates_group_id, payment_id)
    REFERENCES revenant.ledger_group (id, payment_id)
);
CREATE UNIQUE INDEX ledger_group_one_settlement ON revenant.ledger_group (payment_id) WHERE purpose = 'Settlement';
CREATE UNIQUE INDEX ledger_group_one_return ON revenant.ledger_group (compensates_group_id) WHERE purpose = 'Return';

CREATE TABLE revenant.ledger_entry (
  id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  group_id        uuid        NOT NULL,
  tenant_id       text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id uuid        NOT NULL,
  account_id      uuid        NOT NULL,
  currency        text        NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  direction       text        NOT NULL CHECK (direction IN ('Debit', 'Credit')),
  amount_minor    bigint      NOT NULL CHECK (amount_minor > 0),
  created_at      timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ledger_entry_group FOREIGN KEY (group_id, tenant_id, scenario_run_id, currency)
    REFERENCES revenant.ledger_group (id, tenant_id, scenario_run_id, currency),
  CONSTRAINT ledger_entry_account FOREIGN KEY (account_id, tenant_id, scenario_run_id, currency)
    REFERENCES revenant.account (id, tenant_id, scenario_run_id, currency)
);
CREATE INDEX ledger_entry_group_idx ON revenant.ledger_entry (group_id);
CREATE INDEX ledger_entry_account_idx ON revenant.ledger_entry (account_id);

-- ---------------------------------------------------------------------------------------------------------------
-- Immutability and insert/update guards (immediate).
-- ---------------------------------------------------------------------------------------------------------------

CREATE FUNCTION revenant.reject_mutation() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  RAISE EXCEPTION USING ERRCODE = 'RV001',
    MESSAGE = format('revenant.%s: %s is not permitted (append-only)', TG_TABLE_NAME, TG_OP);
END
$$;

CREATE FUNCTION revenant.guard_group_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  NEW.created_txid := pg_current_xact_id();
  RETURN NEW;
END
$$;

CREATE FUNCTION revenant.guard_entry_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  group_txid xid8;
BEGIN
  SELECT g.created_txid INTO group_txid FROM revenant.ledger_group g WHERE g.id = NEW.group_id;
  -- A missing (or other-tenant, invisible) group is left to the composite foreign key, which reports it.
  IF group_txid IS NOT NULL AND group_txid <> pg_current_xact_id() THEN
    RAISE EXCEPTION USING ERRCODE = 'RV002',
      MESSAGE = format('revenant.ledger_entry: posting group %s is sealed; post a new compensating group', NEW.group_id);
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION revenant.is_legal_payment_transition(from_status text, to_status text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path = pg_catalog AS $$
  SELECT (from_status, to_status) IN (
    ('Initiated', 'Screened'),
    ('Screened', 'Held'), ('Screened', 'Cleared'), ('Screened', 'Rejected'),
    ('Held', 'Approved'), ('Held', 'Rejected'), ('Held', 'Returned'),
    ('Approved', 'Cleared'), ('Approved', 'Returned'),
    ('Cleared', 'Settled'), ('Cleared', 'Returned'),
    ('Settled', 'Returned'))
$$;

CREATE FUNCTION revenant.guard_payment_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.status <> 'Initiated' OR NEW.version <> 1 THEN
    RAISE EXCEPTION USING ERRCODE = 'RV003',
      MESSAGE = 'revenant.payment: a new payment must start Initiated at version 1';
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION revenant.guard_payment_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, revenant AS $$
BEGIN
  IF (NEW.id, NEW.tenant_id, NEW.scenario_run_id, NEW.intent_id, NEW.source_account_id, NEW.dest_account_id,
      NEW.currency, NEW.amount_minor, NEW.created_at)
     IS DISTINCT FROM
     (OLD.id, OLD.tenant_id, OLD.scenario_run_id, OLD.intent_id, OLD.source_account_id, OLD.dest_account_id,
      OLD.currency, OLD.amount_minor, OLD.created_at) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV001',
      MESSAGE = format('revenant.payment %s: identity, accounts and amount are immutable', OLD.id);
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION USING ERRCODE = 'RV003',
      MESSAGE = format('revenant.payment %s: version must advance by exactly 1 (%s -> %s)', OLD.id, OLD.version, NEW.version);
  END IF;
  IF NOT revenant.is_legal_payment_transition(OLD.status, NEW.status) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV003',
      MESSAGE = format('revenant.payment %s: illegal transition %s -> %s', OLD.id, OLD.status, NEW.status);
  END IF;
  RETURN NEW;
END
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- Commit-time (deferred) balance, completeness and status-link checks.
-- ---------------------------------------------------------------------------------------------------------------

CREATE FUNCTION revenant.validate_posting_group(group_id uuid) RETURNS void
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  g record;
  debit_legs bigint;
  credit_legs bigint;
  debit_total numeric;
  credit_total numeric;
  foreign_currency bigint;
  misplaced bigint;
BEGIN
  SELECT lg.id, lg.purpose, lg.currency, lg.compensates_group_id,
         p.source_account_id, p.dest_account_id, p.amount_minor, p.currency AS payment_currency
    INTO g
    FROM revenant.ledger_group lg JOIN revenant.payment p ON p.id = lg.payment_id
   WHERE lg.id = group_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION USING ERRCODE = 'RV002',
      MESSAGE = format('revenant.ledger_group %s: group or its payment is not visible in the current tenant context', group_id);
  END IF;

  SELECT count(*) FILTER (WHERE e.direction = 'Debit'),
         count(*) FILTER (WHERE e.direction = 'Credit'),
         coalesce(sum(e.amount_minor) FILTER (WHERE e.direction = 'Debit'), 0),
         coalesce(sum(e.amount_minor) FILTER (WHERE e.direction = 'Credit'), 0),
         count(*) FILTER (WHERE e.currency <> g.currency),
         count(*) FILTER (WHERE
           (g.purpose = 'Settlement' AND e.direction = 'Credit' AND e.account_id <> g.source_account_id) OR
           (g.purpose = 'Settlement' AND e.direction = 'Debit'  AND e.account_id <> g.dest_account_id) OR
           (g.purpose = 'Return'     AND e.direction = 'Debit'  AND e.account_id <> g.source_account_id) OR
           (g.purpose = 'Return'     AND e.direction = 'Credit' AND e.account_id <> g.dest_account_id))
    INTO debit_legs, credit_legs, debit_total, credit_total, foreign_currency, misplaced
    FROM revenant.ledger_entry e
   WHERE e.group_id = validate_posting_group.group_id;

  IF debit_legs = 0 OR credit_legs = 0 THEN
    RAISE EXCEPTION USING ERRCODE = 'RV002',
      MESSAGE = format('revenant.ledger_group %s: incomplete group (%s debit legs, %s credit legs); at least one of each is required',
                       group_id, debit_legs, credit_legs);
  END IF;
  IF foreign_currency <> 0 OR g.currency <> g.payment_currency THEN
    RAISE EXCEPTION USING ERRCODE = 'RV002',
      MESSAGE = format('revenant.ledger_group %s: legs must all be in %s', group_id, g.currency);
  END IF;
  IF debit_total <> credit_total THEN
    RAISE EXCEPTION USING ERRCODE = 'RV002',
      MESSAGE = format('revenant.ledger_group %s: unbalanced %s group (debits %s, credits %s)',
                       group_id, g.currency, debit_total, credit_total);
  END IF;
  IF debit_total <> g.amount_minor THEN
    RAISE EXCEPTION USING ERRCODE = 'RV002',
      MESSAGE = format('revenant.ledger_group %s: group total %s does not equal the linked payment amount %s',
                       group_id, debit_total, g.amount_minor);
  END IF;
  IF misplaced <> 0 THEN
    RAISE EXCEPTION USING ERRCODE = 'RV002',
      MESSAGE = format('revenant.ledger_group %s: %s leg(s) are not on the linked payment''s accounts in the %s direction',
                       group_id, misplaced, g.purpose);
  END IF;
  IF g.purpose = 'Return' AND NOT EXISTS (
       SELECT 1 FROM revenant.ledger_group s WHERE s.id = g.compensates_group_id AND s.purpose = 'Settlement') THEN
    RAISE EXCEPTION USING ERRCODE = 'RV002',
      MESSAGE = format('revenant.ledger_group %s: a Return must compensate the payment''s Settlement group', group_id);
  END IF;
END
$$;

CREATE FUNCTION revenant.validate_payment_posting(payment_id uuid) RETURNS void
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  current_status text;
  has_settlement boolean;
  has_return boolean;
BEGIN
  SELECT p.status INTO current_status FROM revenant.payment p WHERE p.id = payment_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION USING ERRCODE = 'RV003',
      MESSAGE = format('revenant.payment %s: not visible in the current tenant context', payment_id);
  END IF;
  SELECT EXISTS (SELECT 1 FROM revenant.ledger_group g WHERE g.payment_id = validate_payment_posting.payment_id AND g.purpose = 'Settlement'),
         EXISTS (SELECT 1 FROM revenant.ledger_group g WHERE g.payment_id = validate_payment_posting.payment_id AND g.purpose = 'Return')
    INTO has_settlement, has_return;
  IF (current_status = 'Settled' AND NOT (has_settlement AND NOT has_return))
     OR (current_status = 'Returned' AND has_settlement <> has_return)
     OR (current_status NOT IN ('Settled', 'Returned') AND (has_settlement OR has_return)) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV003',
      MESSAGE = format('revenant.payment %s: status %s does not match its postings (settlement group: %s, return group: %s); '
                       'Settled and its balanced group must commit together', payment_id, current_status, has_settlement, has_return);
  END IF;
END
$$;

CREATE FUNCTION revenant.check_group_at_commit() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  PERFORM revenant.validate_posting_group(NEW.id);
  PERFORM revenant.validate_payment_posting(NEW.payment_id);
  RETURN NULL;
END
$$;

CREATE FUNCTION revenant.check_payment_at_commit() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  PERFORM revenant.validate_payment_posting(NEW.id);
  RETURN NULL;
END
$$;

CREATE TRIGGER bank_no_update BEFORE UPDATE OR DELETE ON revenant.bank
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER account_no_update BEFORE UPDATE OR DELETE ON revenant.account
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER ledger_group_no_update BEFORE UPDATE OR DELETE ON revenant.ledger_group
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER ledger_entry_no_update BEFORE UPDATE OR DELETE ON revenant.ledger_entry
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER payment_no_delete BEFORE DELETE ON revenant.payment
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER bank_no_truncate BEFORE TRUNCATE ON revenant.bank
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER account_no_truncate BEFORE TRUNCATE ON revenant.account
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER payment_no_truncate BEFORE TRUNCATE ON revenant.payment
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER ledger_group_no_truncate BEFORE TRUNCATE ON revenant.ledger_group
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER ledger_entry_no_truncate BEFORE TRUNCATE ON revenant.ledger_entry
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();

CREATE TRIGGER payment_guard_insert BEFORE INSERT ON revenant.payment
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_payment_insert();
CREATE TRIGGER payment_guard_update BEFORE UPDATE ON revenant.payment
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_payment_update();
CREATE TRIGGER ledger_group_guard_insert BEFORE INSERT ON revenant.ledger_group
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_group_insert();
CREATE TRIGGER ledger_entry_guard_insert BEFORE INSERT ON revenant.ledger_entry
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_entry_insert();

-- Legs can only be inserted in their group's own transaction, so one commit-time check per group sees every leg.
CREATE CONSTRAINT TRIGGER ledger_group_balanced_at_commit AFTER INSERT ON revenant.ledger_group
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_group_at_commit();
CREATE CONSTRAINT TRIGGER payment_posting_at_commit AFTER UPDATE ON revenant.payment
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_payment_at_commit();

-- Posted asset balance per account (security_invoker: the caller's row level security applies).
CREATE VIEW revenant.account_posted_balance WITH (security_invoker = true) AS
SELECT a.id AS account_id, a.tenant_id, a.scenario_run_id, a.bank_id, a.local_account_id, a.currency,
       (a.opening_balance_minor
        + coalesce(sum(CASE WHEN e.direction = 'Debit' THEN e.amount_minor ELSE -e.amount_minor END), 0))::bigint
         AS posted_minor
  FROM revenant.account a
  LEFT JOIN revenant.ledger_entry e ON e.account_id = a.id
 GROUP BY a.id;

-- ---------------------------------------------------------------------------------------------------------------
-- Row level security on tenant-owned tables (transaction-local tenant context).
-- ---------------------------------------------------------------------------------------------------------------

ALTER TABLE revenant.bank ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.bank FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.account ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.account FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.payment ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.payment FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.ledger_group ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.ledger_group FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.ledger_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.ledger_entry FORCE ROW LEVEL SECURITY;

CREATE POLICY bank_tenant ON revenant.bank TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY account_tenant ON revenant.account TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY payment_tenant ON revenant.payment TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY ledger_group_tenant ON revenant.ledger_group TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY ledger_entry_tenant ON revenant.ledger_entry TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));

-- ---------------------------------------------------------------------------------------------------------------
-- Least privilege. The runtime roles never own a table and are NOBYPASSRLS.
-- ---------------------------------------------------------------------------------------------------------------

REVOKE ALL ON ALL TABLES IN SCHEMA revenant FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA revenant FROM PUBLIC;
GRANT USAGE ON SCHEMA revenant TO revenant_ledger_writer, revenant_replay_reader;

GRANT SELECT, INSERT ON revenant.bank, revenant.account, revenant.payment, revenant.ledger_group, revenant.ledger_entry
  TO revenant_ledger_writer;
GRANT UPDATE (status, version) ON revenant.payment TO revenant_ledger_writer;
GRANT SELECT ON revenant.account_posted_balance TO revenant_ledger_writer;
-- Helpers called from the writer's trigger functions (read-only checks; callable directly, they can only raise).
GRANT EXECUTE ON FUNCTION revenant.is_legal_payment_transition(text, text), revenant.validate_posting_group(uuid),
  revenant.validate_payment_posting(uuid) TO revenant_ledger_writer;

GRANT SELECT ON revenant.bank, revenant.account, revenant.payment, revenant.ledger_group, revenant.ledger_entry,
  revenant.account_posted_balance TO revenant_replay_reader;
