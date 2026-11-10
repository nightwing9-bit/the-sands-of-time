# Local Compose stack

Services: `postgres`, `redis`, one-shot `migrate`, then `api`, `realtime`, `worker`. Run from the repo root.

```
cp .env.example .env     # then replace every REPLACE_ME value (see the comments in .env.example)
docker compose --env-file .env -f deploy/compose/docker-compose.yml up --build
```

- `migrate` runs `scripts/db/migrate.sh` once after Postgres is healthy. The hosts start only if it exits 0.
  Re-run by hand: `docker compose --env-file .env -f deploy/compose/docker-compose.yml run --rm migrate`.
  Hosts never run migrations. Layout and rules: `scripts/db/migrations/README.md`.
- Host ports (127.0.0.1 only): API 8081, Realtime 8082, Worker 8083; Postgres 55432, Redis 56379 (override in `.env`).
- `GET /health/live` reports only that the process is serving. `GET /health/ready` returns 503 while Postgres or
  Redis is unreachable and 200 again after recovery. Neither says anything about gameplay or payments.
- Hosts read `ConnectionStrings__Postgres` and `ConnectionStrings__Redis` and refuse to start without them.
- Database logins: `POSTGRES_USER` (superuser) is used only by `postgres` itself and the `migrate` job. After
  migrating, `migrate` provisions one non-superuser login per host from `API_DB_PASSWORD`, `REALTIME_DB_PASSWORD`
  and `WORKER_DB_PASSWORD` (letters/digits, at least 24): `sands_api` and `sands_realtime` hold only
  `marrow_identity_runtime`, `sands_worker` holds no schema role yet. No host can read `marrow_timeline`, alter a
  schema or bypass the identity column grants. Rotating a password: change it in `.env`, then
  `run --rm migrate` and restart the hosts. Domain specs add their own runtime roles and grants in their migrations
  and extend the login mapping in `scripts/db/migrate.sh`.
- Redis persistence: `appendonly yes`, `appendfsync always`, RDB snapshots off, data in the `redis-data` volume.
  Every acknowledged write (including outbox stream entries and consumer-group acknowledgements) is in the AOF
  before Redis replies, at the cost of one fsync per write. Deleting the volume (`down -v`) deletes undelivered
  stream entries; see "Outbox delivery guarantee" in `docs/architecture/07-realtime-room-authority.md`.
- Keep `realtime` at one replica. The Redis backplane does not make a second match authority safe.
  `bash scripts/checks/realtime-scale-guard.sh [-f override.yml ...]` fails on any resolved model with more than one
  Realtime process, and a second Realtime process against the same database refuses to start (exit code 2). Details:
  `docs/architecture/07-realtime-room-authority.md`.
- Reset all local data: `docker compose --env-file .env -f deploy/compose/docker-compose.yml down -v`.
