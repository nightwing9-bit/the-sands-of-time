-- Spec 07 task 7.4: review cases, four-eyes release, staff comments, run-staff revocation and the review/return fact kinds
-- (owner: Revenant; schema: revenant). Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- A review case is opened in the same transaction that puts a payment on Held (and reserves its funds, 0003). The case
-- records the versioned release policy (required approvals: 2 at or above USD 10,000.00, else 1) and the deterministic,
-- non-ML name-screening evidence. The payment stays Held until the case is Released.
--
-- What the database itself guarantees here (no application check is trusted, no bypass flag):
--   * RV007 privileged release only after the proper sequence: at commit, a payment that moved Held -> Approved must have
--     a Released case whose approvals equal its required approvals; a Released case's payment is no longer Held; a
--     Rejected case's payment is Rejected.
--   * Four-eyes: case_approval has UNIQUE (case_id, subject_id) so one person can never fill both slots (no
--     self-approval, even by direct SQL); slot 2 must be a supervisor; every approval records a server-verified fresh
--     MFA step-up (CHECK); slots fill strictly in order 1, 2 and never beyond the case's required approvals.
--   * Optimistic concurrency: a case UPDATE must advance version by exactly 1, follow a legal status edge and leave the
--     case identity, policy and screening evidence untouched (RV008). Approvals, comments and revocations are append-only.
--   * At commit the case status matches its approvals: Open = none, AwaitingSecondApproval = 1 of 2, Released = all,
--     Rejected = fewer than required (RV008).
--   * finance_fact accepts the ReviewDecision and ReturnInstruction kinds (ADR-P1-17, decision 5: the kind set is still
--     open because no V2 Timeline history exists yet). Exactly one PaymentIntent fact per payment remains enforced.
--   * scenario_run_staff_revocation removes a staff member from a run's staff scope for good (directory table, like
--     scenario_run_staff: not row-level-secured, holds ids only).
-- Tenant-owned tables (review_case, case_approval, case_comment) get ENABLE + FORCE RLS on the transaction-local setting
-- revenant.tenant_id. No cross-schema foreign key. SQLSTATEs added: RV007 release sequence, RV008 case integrity.

-- ---------------------------------------------------------------------------------------------------------------
-- Fact kinds and command kinds.
-- ---------------------------------------------------------------------------------------------------------------

ALTER TABLE revenant.finance_fact DROP CONSTRAINT finance_fact_kind_check;
ALTER TABLE revenant.finance_fact ADD CONSTRAINT finance_fact_kind_check
  CHECK (kind IN ('PaymentIntent', 'ReviewDecision', 'ReturnInstruction'));
ALTER TABLE revenant.finance_fact DROP CONSTRAINT finance_fact_one_per_payment;
CREATE UNIQUE INDEX finance_fact_one_intent_per_payment ON revenant.finance_fact (payment_id) WHERE kind = 'PaymentIntent';
CREATE INDEX finance_fact_payment_idx ON revenant.finance_fact (payment_id);

ALTER TABLE revenant.payment_command DROP CONSTRAINT payment_command_kind_check;
ALTER TABLE revenant.payment_command ADD CONSTRAINT payment_command_kind_check
  CHECK (kind IN ('Intake', 'Transition', 'Review', 'Comment'));

-- ---------------------------------------------------------------------------------------------------------------
-- Run-staff revocation (directory).
-- ---------------------------------------------------------------------------------------------------------------

CREATE TABLE revenant.scenario_run_staff_revocation (
  scenario_run_id uuid        NOT NULL,
  subject_id      text        NOT NULL,
  revoked_by      text        NOT NULL CHECK (revoked_by <> ''),
  revoked_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (scenario_run_id, subject_id),
  CONSTRAINT scenario_run_staff_revocation_member FOREIGN KEY (scenario_run_id, subject_id)
    REFERENCES revenant.scenario_run_staff (scenario_run_id, subject_id)
);

-- ---------------------------------------------------------------------------------------------------------------
-- Cases, approvals, comments.
-- ---------------------------------------------------------------------------------------------------------------

CREATE TABLE revenant.review_case (
  id                           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id                    text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id              uuid        NOT NULL,
  payment_id                   uuid        NOT NULL,
  currency                     text        NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  amount_minor                 bigint      NOT NULL CHECK (amount_minor > 0),
  policy_version               text        NOT NULL CHECK (policy_version <> ''),
  required_approvals           smallint    NOT NULL CHECK (required_approvals IN (1, 2)),
  screening_version            text        NOT NULL CHECK (screening_version <> ''),
  screened_name                text        NOT NULL,
  screening_entry              text,
  screening_score_permille     integer     NOT NULL CHECK (screening_score_permille BETWEEN 0 AND 1000),
  screening_threshold_permille integer     NOT NULL CHECK (screening_threshold_permille BETWEEN 0 AND 1000),
  screening_match              boolean     NOT NULL,
  status                       text        NOT NULL DEFAULT 'Open'
                                 CHECK (status IN ('Open', 'AwaitingSecondApproval', 'Released', 'Rejected')),
  version                      bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
  assigned_subject             text        CHECK (assigned_subject <> ''),
  created_at                   timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT review_case_one_per_payment UNIQUE (payment_id),
  CONSTRAINT review_case_scope UNIQUE (id, tenant_id, scenario_run_id),
  CONSTRAINT review_case_screening_shape CHECK (
    screening_match = (screening_score_permille >= screening_threshold_permille)),
  CONSTRAINT review_case_payment FOREIGN KEY (payment_id, tenant_id, scenario_run_id)
    REFERENCES revenant.payment (id, tenant_id, scenario_run_id)
);

CREATE TABLE revenant.case_approval (
  tenant_id       text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id uuid        NOT NULL,
  case_id         uuid        NOT NULL,
  approval_order  smallint    NOT NULL CHECK (approval_order IN (1, 2)),
  subject_id      text        NOT NULL CHECK (subject_id <> ''),
  actor_role      text        NOT NULL CHECK (actor_role IN ('operator', 'supervisor')),
  mfa_verified    boolean     NOT NULL CHECK (mfa_verified),
  decided_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (case_id, approval_order),
  CONSTRAINT case_approval_distinct_approvers UNIQUE (case_id, subject_id),
  CONSTRAINT case_approval_second_is_supervisor CHECK (approval_order = 1 OR actor_role = 'supervisor'),
  CONSTRAINT case_approval_case FOREIGN KEY (case_id, tenant_id, scenario_run_id)
    REFERENCES revenant.review_case (id, tenant_id, scenario_run_id)
);

CREATE TABLE revenant.case_comment (
  id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tenant_id       text        NOT NULL CHECK (tenant_id <> ''),
  scenario_run_id uuid        NOT NULL,
  case_id         uuid        NOT NULL,
  author_subject  text        NOT NULL CHECK (author_subject <> ''),
  body            text        NOT NULL CHECK (length(body) BETWEEN 1 AND 2000),
  created_at      timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT case_comment_case FOREIGN KEY (case_id, tenant_id, scenario_run_id)
    REFERENCES revenant.review_case (id, tenant_id, scenario_run_id)
);
CREATE INDEX case_comment_case_idx ON revenant.case_comment (case_id, id);

-- ---------------------------------------------------------------------------------------------------------------
-- Guards (immediate).
-- ---------------------------------------------------------------------------------------------------------------

CREATE FUNCTION revenant.guard_case_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.status <> 'Open' OR NEW.version <> 1 THEN
    RAISE EXCEPTION USING ERRCODE = 'RV008', MESSAGE = 'revenant.review_case: a new case must start Open at version 1';
  END IF;
  RETURN NEW;
END
$$;

CREATE FUNCTION revenant.guard_case_update() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF (NEW.id, NEW.tenant_id, NEW.scenario_run_id, NEW.payment_id, NEW.currency, NEW.amount_minor, NEW.policy_version,
      NEW.required_approvals, NEW.screening_version, NEW.screened_name, NEW.screening_entry, NEW.screening_score_permille,
      NEW.screening_threshold_permille, NEW.screening_match, NEW.created_at)
     IS DISTINCT FROM
     (OLD.id, OLD.tenant_id, OLD.scenario_run_id, OLD.payment_id, OLD.currency, OLD.amount_minor, OLD.policy_version,
      OLD.required_approvals, OLD.screening_version, OLD.screened_name, OLD.screening_entry, OLD.screening_score_permille,
      OLD.screening_threshold_permille, OLD.screening_match, OLD.created_at) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV001',
      MESSAGE = format('revenant.review_case %s: identity, policy and screening evidence are immutable', OLD.id);
  END IF;
  IF NEW.version <> OLD.version + 1 THEN
    RAISE EXCEPTION USING ERRCODE = 'RV008',
      MESSAGE = format('revenant.review_case %s: version must advance by exactly 1 (%s -> %s)', OLD.id, OLD.version, NEW.version);
  END IF;
  IF NOT ((OLD.status, NEW.status) IN (
       ('Open', 'Open'), ('Open', 'AwaitingSecondApproval'), ('Open', 'Released'), ('Open', 'Rejected'),
       ('AwaitingSecondApproval', 'AwaitingSecondApproval'), ('AwaitingSecondApproval', 'Released'),
       ('AwaitingSecondApproval', 'Rejected'))) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV008',
      MESSAGE = format('revenant.review_case %s: illegal case transition %s -> %s', OLD.id, OLD.status, NEW.status);
  END IF;
  RETURN NEW;
END
$$;

-- Slots fill in order and only while the case is open; the row lock taken here serializes concurrent approvals of a case.
CREATE FUNCTION revenant.guard_approval_insert() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  c record;
  existing bigint;
BEGIN
  SELECT rc.status, rc.required_approvals INTO c FROM revenant.review_case rc WHERE rc.id = NEW.case_id FOR UPDATE;
  IF NOT FOUND THEN
    RETURN NEW; -- unknown or other-tenant case: the composite foreign key reports it
  END IF;
  SELECT count(*) INTO existing FROM revenant.case_approval a WHERE a.case_id = NEW.case_id;
  IF c.status NOT IN ('Open', 'AwaitingSecondApproval') OR NEW.approval_order <> existing + 1
     OR NEW.approval_order > c.required_approvals THEN
    RAISE EXCEPTION USING ERRCODE = 'RV008',
      MESSAGE = format('revenant.case_approval: case %s (%s, %s of %s approvals) cannot take approval %s',
                       NEW.case_id, c.status, existing, c.required_approvals, NEW.approval_order);
  END IF;
  RETURN NEW;
END
$$;

CREATE TRIGGER review_case_guard_insert BEFORE INSERT ON revenant.review_case
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_case_insert();
CREATE TRIGGER review_case_guard_update BEFORE UPDATE ON revenant.review_case
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_case_update();
CREATE TRIGGER case_approval_guard_insert BEFORE INSERT ON revenant.case_approval
  FOR EACH ROW EXECUTE FUNCTION revenant.guard_approval_insert();

CREATE TRIGGER review_case_no_delete BEFORE DELETE ON revenant.review_case
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER case_approval_no_update BEFORE UPDATE OR DELETE ON revenant.case_approval
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER case_comment_no_update BEFORE UPDATE OR DELETE ON revenant.case_comment
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER scenario_run_staff_revocation_no_update BEFORE UPDATE OR DELETE ON revenant.scenario_run_staff_revocation
  FOR EACH ROW EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER review_case_no_truncate BEFORE TRUNCATE ON revenant.review_case
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER case_approval_no_truncate BEFORE TRUNCATE ON revenant.case_approval
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER case_comment_no_truncate BEFORE TRUNCATE ON revenant.case_comment
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();
CREATE TRIGGER scenario_run_staff_revocation_no_truncate BEFORE TRUNCATE ON revenant.scenario_run_staff_revocation
  FOR EACH STATEMENT EXECUTE FUNCTION revenant.reject_mutation();

-- ---------------------------------------------------------------------------------------------------------------
-- Commit-time (deferred) checks.
-- ---------------------------------------------------------------------------------------------------------------

-- RV008: case status <-> approvals; RV007: case outcome <-> payment status.
CREATE FUNCTION revenant.check_case_at_commit() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
DECLARE
  target uuid;
  c record;
  approvals bigint;
  payment_status text;
  consistent boolean;
BEGIN
  IF TG_TABLE_NAME = 'review_case' THEN
    target := NEW.id;
  ELSE
    target := NEW.case_id;
  END IF;
  SELECT rc.id, rc.status, rc.required_approvals, rc.payment_id INTO c
    FROM revenant.review_case rc WHERE rc.id = target;
  IF NOT FOUND THEN
    RAISE EXCEPTION USING ERRCODE = 'RV008', MESSAGE = 'revenant.review_case: not visible in the current tenant context';
  END IF;
  SELECT count(*) INTO approvals FROM revenant.case_approval a WHERE a.case_id = c.id;
  consistent := CASE c.status
       WHEN 'Open' THEN approvals = 0
       WHEN 'AwaitingSecondApproval' THEN c.required_approvals = 2 AND approvals = 1
       WHEN 'Released' THEN approvals = c.required_approvals
       ELSE approvals < c.required_approvals
     END;
  IF NOT consistent THEN
    RAISE EXCEPTION USING ERRCODE = 'RV008',
      MESSAGE = format('revenant.review_case %s: status %s does not match %s of %s approvals', c.id, c.status, approvals, c.required_approvals);
  END IF;
  SELECT p.status INTO payment_status FROM revenant.payment p WHERE p.id = c.payment_id;
  IF (c.status = 'Released' AND payment_status IN ('Initiated', 'Screened', 'Held'))
     OR (c.status = 'Rejected' AND payment_status <> 'Rejected') THEN
    RAISE EXCEPTION USING ERRCODE = 'RV007',
      MESSAGE = format('revenant.review_case %s: a %s case cannot leave its payment %s', c.id, c.status, payment_status);
  END IF;
  RETURN NULL;
END
$$;

-- RV007: Held -> Approved only with a Released case whose approvals are complete.
CREATE FUNCTION revenant.check_release_at_commit() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF OLD.status = 'Held' AND NEW.status = 'Approved' AND NOT EXISTS (
       SELECT 1 FROM revenant.review_case rc
        WHERE rc.payment_id = NEW.id AND rc.status = 'Released'
          AND (SELECT count(*) FROM revenant.case_approval a WHERE a.case_id = rc.id) = rc.required_approvals) THEN
    RAISE EXCEPTION USING ERRCODE = 'RV007',
      MESSAGE = format('revenant.payment %s: Held -> Approved requires a Released review case with all required approvals', NEW.id);
  END IF;
  RETURN NULL;
END
$$;

CREATE CONSTRAINT TRIGGER review_case_consistent_at_commit AFTER INSERT OR UPDATE ON revenant.review_case
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_case_at_commit();
CREATE CONSTRAINT TRIGGER case_approval_consistent_at_commit AFTER INSERT ON revenant.case_approval
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_case_at_commit();
CREATE CONSTRAINT TRIGGER payment_release_at_commit AFTER UPDATE ON revenant.payment
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION revenant.check_release_at_commit();

-- ---------------------------------------------------------------------------------------------------------------
-- Row level security and least privilege.
-- ---------------------------------------------------------------------------------------------------------------

ALTER TABLE revenant.review_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.review_case FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.case_approval ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.case_approval FORCE ROW LEVEL SECURITY;
ALTER TABLE revenant.case_comment ENABLE ROW LEVEL SECURITY;
ALTER TABLE revenant.case_comment FORCE ROW LEVEL SECURITY;

CREATE POLICY review_case_tenant ON revenant.review_case TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY case_approval_tenant ON revenant.case_approval TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));
CREATE POLICY case_comment_tenant ON revenant.case_comment TO revenant_ledger_writer, revenant_replay_reader
  USING (tenant_id = current_setting('revenant.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('revenant.tenant_id', true));

REVOKE ALL ON revenant.review_case, revenant.case_approval, revenant.case_comment, revenant.scenario_run_staff_revocation FROM PUBLIC;
REVOKE ALL ON FUNCTION revenant.guard_case_insert(), revenant.guard_case_update(), revenant.guard_approval_insert(),
  revenant.check_case_at_commit(), revenant.check_release_at_commit() FROM PUBLIC;

GRANT SELECT, INSERT ON revenant.review_case, revenant.case_approval, revenant.case_comment,
  revenant.scenario_run_staff_revocation TO revenant_ledger_writer;
GRANT UPDATE (status, version, assigned_subject) ON revenant.review_case TO revenant_ledger_writer;

GRANT SELECT ON revenant.review_case, revenant.case_approval, revenant.case_comment, revenant.scenario_run_staff_revocation
  TO revenant_replay_reader;
