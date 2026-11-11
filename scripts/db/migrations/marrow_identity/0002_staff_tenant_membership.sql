-- Spec 07 task 7.4: tenant-bearing staff identities (owner: Marrow.Infrastructure identity; schema: marrow_identity).
-- Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- A staff user belongs to at most ONE active tenant. The tenant is server state: the access-token handler and the
-- realtime revalidator read it from this table on every request/check (never from the token or the request) and put it
-- on the verified actor. Revoking the membership (revoked_at) removes the tenant from the actor on the next request, so
-- every tenant-scoped domain policy denies from then on. A revoked row is never reactivated or deleted; a new grant is a
-- new row for another tenant (or, after revocation, the same tenant is refused: PK).
-- No cross-schema foreign key. The runtime role may INSERT a grant and set revoked_at once.

CREATE TABLE marrow_identity.staff_tenant_memberships (
  user_id    uuid        NOT NULL REFERENCES marrow_identity.staff_users (id),
  tenant_id  text        NOT NULL CHECK (tenant_id ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$'),
  granted_at timestamptz NOT NULL DEFAULT now(),
  revoked_at timestamptz,
  PRIMARY KEY (user_id, tenant_id)
);
CREATE UNIQUE INDEX staff_tenant_memberships_one_active
  ON marrow_identity.staff_tenant_memberships (user_id) WHERE revoked_at IS NULL;

CREATE TRIGGER staff_tenant_memberships_revoked_once BEFORE UPDATE ON marrow_identity.staff_tenant_memberships
  FOR EACH ROW EXECUTE FUNCTION marrow_identity.keep_once_set('revoked_at');

CREATE FUNCTION marrow_identity.staff_tenant_memberships_identity() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.user_id, NEW.tenant_id, NEW.granted_at) IS DISTINCT FROM (OLD.user_id, OLD.tenant_id, OLD.granted_at) THEN
    RAISE EXCEPTION 'marrow_identity.staff_tenant_memberships identity is immutable' USING ERRCODE = 'MI001';
  END IF;
  RETURN NEW;
END
$$;
CREATE TRIGGER staff_tenant_memberships_immutable BEFORE UPDATE ON marrow_identity.staff_tenant_memberships
  FOR EACH ROW EXECUTE FUNCTION marrow_identity.staff_tenant_memberships_identity();

REVOKE ALL ON marrow_identity.staff_tenant_memberships FROM PUBLIC;
GRANT SELECT, INSERT ON marrow_identity.staff_tenant_memberships TO marrow_identity_runtime;
GRANT UPDATE (revoked_at) ON marrow_identity.staff_tenant_memberships TO marrow_identity_runtime;
