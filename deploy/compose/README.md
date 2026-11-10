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
- Keep `realtime` at one replica. The Redis backplane does not make a second match authority safe.
- Reset all local data: `docker compose --env-file .env -f deploy/compose/docker-compose.yml down -v`.
