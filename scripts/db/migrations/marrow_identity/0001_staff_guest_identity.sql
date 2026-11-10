-- Spec 03 task 3.1: staff and guest identity (owner: Marrow.Infrastructure identity; schema: marrow_identity).
-- Applied only by scripts/db/migrate.sh, inside one transaction with its history row.
--
-- Two separate principal families that never share a table, a key or a role:
--   staff_*  : ASP.NET Core Identity users (password hash, lockout, encrypted TOTP key), roles,
--              server-side sessions and rotating hashed opaque refresh tokens.
--   guest_*  : anonymous-but-remembered game visitors and their server-backed sessions.
-- Secrets (refresh/guest session secrets) are stored only as SHA-256 hashes. The TOTP shared key is
-- stored encrypted by the application (AES-GCM, key derived from the identity master secret).
-- No cross-schema foreign keys. The runtime role may DELETE only staff role memberships.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'marrow_identity_runtime') THEN
    CREATE ROLE marrow_identity_runtime NOLOGIN;
  END IF;
END
$$;
-- Write-once columns: a revoked/consumed marker can never be cleared or moved, not even by the owner.
CREATE FUNCTION marrow_identity.keep_once_set() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  col text;
BEGIN
  FOREACH col IN ARRAY TG_ARGV LOOP
    IF (to_jsonb(OLD) ->> col) IS NOT NULL AND (to_jsonb(NEW) ->> col) IS DISTINCT FROM (to_jsonb(OLD) ->> col) THEN
      RAISE EXCEPTION 'marrow_identity.%.% is write-once', TG_TABLE_NAME, col USING ERRCODE = 'MI001';
    END IF;
  END LOOP;
  RETURN NEW;
END
$$;
CREATE TABLE marrow_identity.staff_users (
  id                      uuid        PRIMARY KEY,
  user_name               text        NOT NULL CHECK (user_name <> ''),
  normalized_user_name    text        NOT NULL CHECK (normalized_user_name <> ''),
  password_hash           text,
  security_stamp          text,
  concurrency_stamp       text        NOT NULL,
  lockout_enabled         boolean     NOT NULL DEFAULT true,
  lockout_end             timestamptz,
  access_failed_count     integer     NOT NULL DEFAULT 0 CHECK (access_failed_count >= 0),
  two_factor_enabled      boolean     NOT NULL DEFAULT false,
  authenticator_key_enc   bytea,
  totp_last_code          text,
  totp_last_accepted_at   timestamptz,
  disabled_at             timestamptz,
  created_at              timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT staff_users_name_unique UNIQUE (normalized_user_name),
  CONSTRAINT staff_users_mfa_needs_key CHECK (NOT two_factor_enabled OR authenticator_key_enc IS NOT NULL)
);
CREATE TABLE marrow_identity.staff_user_roles (
  user_id uuid NOT NULL REFERENCES marrow_identity.staff_users (id),
  role    text NOT NULL CHECK (role IN ('operator', 'supervisor')),
  PRIMARY KEY (user_id, role)
);
CREATE TABLE marrow_identity.staff_sessions (
  id                uuid        PRIMARY KEY,
  user_id           uuid        NOT NULL REFERENCES marrow_identity.staff_users (id),
  created_at        timestamptz NOT NULL,
  last_refreshed_at timestamptz NOT NULL,
  expires_at        timestamptz NOT NULL,
  mfa_verified_at   timestamptz,
  revoked_at        timestamptz,
  revoke_reason     text,
  CONSTRAINT staff_sessions_revoke_reason CHECK ((revoked_at IS NULL) = (revoke_reason IS NULL))
);
CREATE INDEX staff_sessions_user ON marrow_identity.staff_sessions (user_id);
CREATE TRIGGER staff_sessions_revoked_once BEFORE UPDATE ON marrow_identity.staff_sessions
  FOR EACH ROW EXECUTE FUNCTION marrow_identity.keep_once_set('revoked_at', 'revoke_reason');
CREATE TABLE marrow_identity.staff_refresh_tokens (
  id          uuid        PRIMARY KEY,
  session_id  uuid        NOT NULL REFERENCES marrow_identity.staff_sessions (id),
  token_hash  bytea       NOT NULL CHECK (length(token_hash) = 32),
  issued_at   timestamptz NOT NULL,
  expires_at  timestamptz NOT NULL,
  consumed_at timestamptz,
  CONSTRAINT staff_refresh_tokens_lifetime CHECK (expires_at > issued_at)
);
CREATE INDEX staff_refresh_tokens_session ON marrow_identity.staff_refresh_tokens (session_id);
CREATE TRIGGER staff_refresh_tokens_consumed_once BEFORE UPDATE ON marrow_identity.staff_refresh_tokens
  FOR EACH ROW EXECUTE FUNCTION marrow_identity.keep_once_set('consumed_at');
CREATE TABLE marrow_identity.guest_principals (
  id           uuid        PRIMARY KEY,
  created_at   timestamptz NOT NULL,
  last_seen_at timestamptz NOT NULL,
  banned_at    timestamptz,
  ban_reason   text,
  CONSTRAINT guest_principals_ban_reason CHECK ((banned_at IS NULL) = (ban_reason IS NULL))
);
CREATE TABLE marrow_identity.guest_sessions (
  id            uuid        PRIMARY KEY,
  guest_id      uuid        NOT NULL REFERENCES marrow_identity.guest_principals (id),
  secret_hash   bytea       NOT NULL CHECK (length(secret_hash) = 32),
  created_at    timestamptz NOT NULL,
  last_seen_at  timestamptz NOT NULL,
  expires_at    timestamptz NOT NULL,
  revoked_at    timestamptz,
  revoke_reason text,
  CONSTRAINT guest_sessions_revoke_reason CHECK ((revoked_at IS NULL) = (revoke_reason IS NULL))
);
CREATE INDEX guest_sessions_guest ON marrow_identity.guest_sessions (guest_id);
CREATE TRIGGER guest_sessions_revoked_once BEFORE UPDATE ON marrow_identity.guest_sessions
  FOR EACH ROW EXECUTE FUNCTION marrow_identity.keep_once_set('revoked_at', 'revoke_reason');
-- Least privilege for the runtime role: read + insert; UPDATE only on the columns that legitimately change.
REVOKE ALL ON ALL TABLES IN SCHEMA marrow_identity FROM PUBLIC;
GRANT USAGE ON SCHEMA marrow_identity TO marrow_identity_runtime;
GRANT SELECT, INSERT ON marrow_identity.staff_users, marrow_identity.staff_user_roles,
  marrow_identity.staff_sessions, marrow_identity.staff_refresh_tokens,
  marrow_identity.guest_principals, marrow_identity.guest_sessions TO marrow_identity_runtime;
GRANT DELETE ON marrow_identity.staff_user_roles TO marrow_identity_runtime;
GRANT UPDATE (user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp,
  lockout_enabled, lockout_end, access_failed_count, two_factor_enabled, authenticator_key_enc,
  totp_last_code, totp_last_accepted_at, disabled_at)
  ON marrow_identity.staff_users TO marrow_identity_runtime;
GRANT UPDATE (last_refreshed_at, expires_at, mfa_verified_at, revoked_at, revoke_reason)
  ON marrow_identity.staff_sessions TO marrow_identity_runtime;
GRANT UPDATE (consumed_at) ON marrow_identity.staff_refresh_tokens TO marrow_identity_runtime;
GRANT UPDATE (last_seen_at, banned_at, ban_reason) ON marrow_identity.guest_principals TO marrow_identity_runtime;
GRANT UPDATE (last_seen_at, expires_at, revoked_at, revoke_reason) ON marrow_identity.guest_sessions TO marrow_identity_runtime;
