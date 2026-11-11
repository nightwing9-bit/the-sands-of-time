-- Spec 07 task 7.3: liquidity reservations (holds), a database-enforced available-balance guard, the payment/hold
-- link and the settlement queue (owner: Revenant; schema: revenant). Applied only by scripts/db/migrate.sh, inside one
-- transaction with its history row.
--
-- Accounting model (matches the pure V1 reducer, src/Revenant/Finance/FinanceReducer.cs):
--   posted    = opening + sum(debits) - sum(credits)                      (revenant.account_posted_balance, 0001)
--   reserved  = sum(amount) of the account's Active holds
--   available = posted - reserved                                       (revenant.account_available_balance, here)
-- A payment entering Held reserves its amount on the source account: available drops, posted is unchanged. The hold
-- stays Active through Approved and Cleared and is resolved exactly once: Consumed by the settlement that posts the
-- payment, or Released when the payment is Rejected/Returned before settlement. A payment cleared without a hold
-- (Screened -> Cleared) reserves nothing; its funds are checked when it settles, as in V1.
--
-- What the database itself guarantees here (no application check is trusted, no bypass flag):
--   * A hold belongs to exactly one payment (UNIQUE payment_id, so a payment can reserve at most once and can never be
--     re-reserved after release), on that payment's own source account, for exactly its amount and currency
--     (composite FK onto the payment). Holds are never deleted; the only UPDATE is Active -> Consumed|Released, once.
--   * RV005 overdraft guard: inserting a hold, or a Credit leg, first takes FOR NO KEY UPDATE on the account row and
--     then requires available >= amount. Every writer that can lower an account's available balance therefore
--     serializes on that row, and the check reads committed state taken after the lock (READ COMMITTED takes a new
--     snapshot per statement). Debits and hold resolution only raise available and need no lock. Even a writer that
--     skipped the application check cannot overspend, double-reserve or overdraw a return.
--   * RV006 (deferred, at commit) ties payment status to its hold:
--       Held/Approved           <=> an Active hold
--       Cleared                 -> no hold, or an Active hold (released from review, still reserved until settlement)
--       Consumed hold           -> Settled, or Returned after settlement (a Settlement group exists)
--       Released hold           -> Rejected or Returned without any settlement
--       Initiated/Screened      -> no hold
--     So a release or reject unwinds the reservation in the same transaction, and a settled payment's hold is consumed
--     in the transaction that posts it.
--
-- Roles: revenant_ledger_writer gains SELECT/INSERT on hold, UPDATE(status, resolved_at) on hold, and UPDATE(id) on
-- account. The account grant exists only because PostgreSQL requires an UPDATE privilege for a row lock; the 0001
-- account_no_update trigger still rejects every UPDATE (RV001). revenant_replay_reader: SELECT only.
-- RLS ENABLE + FORCE on hold (tenant-owned), same transaction-local setting revenant.tenant_id. No cross-schema FK.
-- SQLSTATEs added: RV005 insufficient available balance, RV006 payment/hold link invalid.

ALTER TABLE revenant.payment ADD CONSTRAINT payment_reservation_key
  UNIQUE (id, source_account_id, amount_minor, currency, tenant_id, scenario_run_id);

CREATE TABLE revenant.hold (
  id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id       text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id uuid        NOT NULL,
  payment_id      uuid        NOT NULL,
  account_id      uuid        NOT NULL,
  currency        text        NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  amount_minor    bigint      NOT NULL CHECK (amount_minor > 0),
  status          text        NOT NULL DEFAULT 'Active' CHECK (status IN ('Active', 'Consumed', 'Released')),
  created_at      timestamptz NOT NULL DEFAULT now(),
  resolved_at     timestamptz,
  CONSTRAINT hold_one_per_payment UNIQUE (payment_id),
  CONSTRAINT hold_resolution_shape CHECK ((status = 'Active') = (resolved_at IS NULL)),
  CONSTRAINT hold_payment FOREIGN KEY (payment_id, account_id, amount_minor, currency, tenant_id, scenario_run_id)
    REFERENCES revenant.payment (id, source_account_id, amount_minor, currency, tenant_id, scenario_run_id),
  CONSTRAINT hold_account FOREIGN KEY (account_id, tenant_id, scenario_run_id, currency)
    REFERENCES revenant.account (id, tenant_id, scenario_run_id, currency)
);
CREATE INDEX hold_active_account_idx ON revenant.hold (account_id) WHERE status = 'Active';

-- The settlement queue: Cleared payments, claimed by workers with FOR UPDATE SKIP LOCKED.
CREATE INDEX payment_cleared_queue_idx ON revenant.payment (tenant_id, created_at) WHERE status = 'Cleared';

-- Available balance per account (security_invoker: the caller's row level security applies).
CREATE VIEW revenant.account_available_balance WITH (security_invoker = true) AS
SELECT p.account_id, p.tenant_id, p.scenario_run_id, p.bank_id, p.local_account_id, p.currency, p.posted_minor,
       r.reserved_minor,
       (p.posted_minor - r.reserved_minor)::bigint AS available_minor
  FROM revenant.account_posted_balance p
  CROSS JOIN LATERAL (
    SELECT coalesce(sum(h.amount_minor), 0)::bigint AS reserved_minor
      FROM revenant.hold h
     WHERE h.account_id = p.account_id AND h.status = 'Active') r;

-- ---------------------------------------------------------------------------------------------------------------
-- RV005: lock the account row, then require available >= amount (immediate, per row).
-- ---------------------------------------------------------------------------------------------------------------

CREATE FUNCTION revenant.require_available(account uuid, amount bigint, what text) RETURNS void
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  available bigint;
BEGIN
  -- NO KEY UPDATE conflicts with itself (so lowering writers serialize) but not with the KEY SHARE locks that
  -- foreign-key checks take, so concurrent debits into the account are not blocked.
  PERFORM 1 FROM revenant.account a WHERE a.id = account FOR NO KEY UPDATE;
  IF NOT FOUND THEN
    RETURN; -- unknown or other-tenant account: the composite foreign key reports it
  END IF;
  SELECT b.available_minor INTO available FROM revenant.account_available_balance b WHERE b.account_id = account;
  IF available < amount THEN
    RAISE EXCEPTION USING ERRCODE = 'RV005',
      MESSAGE = format('revenant.account %s: insufficient available balance for %s of %s (available %s)',
                       account, what, amount, available);
  END IF;
END
$$;

CREATE FUNCTION revenant.guard_hold_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.status <> 'Active' OR NEW.resolved_at IS NOT NULL THEN
    RAISE EXCEPTION USING ERRCODE = 'RV006', MESSAGE = 'revenant.hold: a new hold must be Active and unresolved';
  END IF;
  PERFORM revenant.require_available(NEW.account_id, NEW.amount_minor, 'a hold');
  RETURN NEW;
END
$$;

CREATE FUNCTION revenant.guard_hold_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.id, NEW.tenant_id, NEW.scenario_run_id, NEW.payment_id, NEW.account_id, NEW.currency, NEW.amount_minor, NEW.created_at)
     IS DISTINCT FROM
     (OLD.id, OLD.tenant_id, OLD.scenario_run_id, OLD.payment_id, OLD.account_id, OLD.currency, OLD.amount_minor, OLD.created_at)
     OR OLD.status <> 'Active' OR NEW.status NOT IN ('Consumed', 'Released') THEN
    RAISE EXCEPTION USING ERRCODE = 'RV001',
      MESSAGE = format('revenant.hold %s: only one resolution Active -> Consumed|Released is permitted (%s -> %s)',
                       OLD.id, OLD.status, NEW.status);
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION revenant.guard_credit_available() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.direction = 'Credit' THEN
    PERFORM revenant.require_available(NEW.account_id, NEW.amount_minor, 'a credit leg');
  END IF;
  RETURN NEW;
END
$$;

-- ---------------------------------------------------------------------------------------------------------------
-- RV006: payment status <-> hold, at commit.
-- ---------------------------------------------------------------------------------------------------------------

CREATE FUNCTION revenant.validate_payment_hold(payment_id uuid) RETURNS void
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  current_status text;
  hold_status text;
  has_settlement boolean;
  valid boolean;
BEGIN
  SELECT p.status INTO current_status FROM revenant.payment p WHERE p.id = payment_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION USING ERRCODE = 'RV006',
      MESSAGE = format('revenant.payment %s: not visible in the current tenant context', payment_id);
  END IF;
  SELECT h.status INTO hold_status FROM revenant.hold h WHERE h.payment_id = validate_payment_hold.payment_id;
  SELECT EXISTS (SELECT 1 FROM revenant.ledger_group g
                  WHERE g.payment_id = validate_payment_hold.payment_id AND g.purpose = 'Settlement')
    INTO has_settlement;
  valid := CASE
    WHEN hold_status IS NULL THEN current_status NOT IN ('Held', 'Approved')
    WHEN hold_status = 'Active' THEN current_status IN ('Held', 'Approved', 'Cleared')
    WHEN hold_status = 'Consumed' THEN has_settlement AND current_status IN ('Settled', 'Returned')
    ELSE NOT has_settlement AND current_status IN ('Rejected', 'Returned')
  END;
  IF NOT valid THEN
    RAISE EXCEPTION USING ERRCODE = 'RV006',
      MESSAGE = format('revenant.payment %s: status %s does not match its hold (%s, settlement group: %s); '
                       'a reservation is placed on Held and resolved once, in the same transaction as release, rejection or settlement',
                       payment_id, current_status, coalesce(hold_status, 'none'), has_settlement);
  END IF;
END
$$;

CREATE FUNCTION revenant.check_payment_hold_at_commit() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF TG_TABLE_NAME = 'hold' THEN
    PERFORM revenant.validate_payment_hold(NEW.payment_id);
  ELSE
    PERFORM revenant.validate_payment_hold(NEW.id);
  END IF;
  RETURN NULL;
END
$$;

CREATE TRIGGER hold_guard_insert BEFORE INSERT ON revenant.hold
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_hold_insert();
CREATE TRIGGER hold_guard_update BEFORE UPDATE ON revenant.hold
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_hold_update();
CREATE TRIGGER hold_no_delete BEFORE DELETE ON revenant.hold
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER hold_no_truncate BEFORE TRUNCATE ON revenant.hold
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER ledger_entry_guard_credit_available BEFORE INSERT ON revenant.ledger_entry
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_credit_available();

CREATE CONSTRAINT TRIGGER hold_matches_payment_at_commit AFTER INSERT OR UPDATE ON revenant.hold
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_payment_hold_at_commit();
CREATE CONSTRAINT TRIGGER payment_hold_at_commit AFTER UPDATE ON revenant.payment
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_payment_hold_at_commit();

-- ---------------------------------------------------------------------------------------------------------------
-- Row level security and least privilege.
-- ---------------------------------------------------------------------------------------------------------------

ALTER TABLE revenant.hold ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.hold FORCE ROW LEVEL SECURITY;
CREATE POLICY hold_tenant ON revenant.hold TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));

REVOKE ALL ON revenant.hold, revenant.account_available_balance FROM PUBLIC;
REVOKE ALL ON FUNCTION revenant.require_available(uuid, bigint, text), revenant.guard_hold_insert(),
  revenant.guard_hold_update(), revenant.guard_credit_available(), revenant.validate_payment_hold(uuid),
  revenant.check_payment_hold_at_commit() FROM PUBLIC;

GRANT SELECT, INSERT ON revenant.hold TO revenant_ledger_writer;
GRANT UPDATE (status, resolved_at) ON revenant.hold TO revenant_ledger_writer;
GRANT UPDATE (id) ON revenant.account TO revenant_ledger_writer; -- row-lock privilege only; RV001 rejects every UPDATE
GRANT SELECT ON revenant.account_available_balance TO revenant_ledger_writer;
GRANT EXECUTE ON FUNCTION revenant.require_available(uuid, bigint, text), revenant.validate_payment_hold(uuid)
  TO revenant_ledger_writer;

GRANT SELECT ON revenant.hold, revenant.account_available_balance TO revenant_replay_reader;
