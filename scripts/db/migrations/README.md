# Module migrations

Spec 01 supplies **no** migrations. Each owning spec adds its own files here and nothing else:

```
scripts/db/migrations/<module>/NNNN_description.sql
```

| Order | Module directory | Owner |
|------:|------------------|-------|
| 1 | `marrow_timeline` | Spec 02 |
| 2 | `marrow_identity` | Spec 03 |
| 3 | `revenant` | Revenant specs |
| 4 | `ghostlap` | Ghost Lap specs |

Rules enforced by `../migrate.sh`:

- Modules apply in the order above; files within a module apply in ascending name order.
- A module may only create and use its own schema (same name as the directory). No cross-schema foreign keys.
- Each file runs in one transaction with its history row (`<module>.schema_migrations`). Do not write `BEGIN`/`COMMIT` or `CREATE INDEX CONCURRENTLY`.
- Forward-only: editing an applied file makes the executor fail. Add a new numbered file instead.
- Names must match `NNNN_lowercase_words.sql`; an unknown module directory is an error.
- Only `migrate.sh` applies migrations. API, Realtime and Worker never do.

Run it: `docker compose --env-file .env -f deploy/compose/docker-compose.yml run --rm migrate`
(it also runs automatically before the hosts in `up`).
