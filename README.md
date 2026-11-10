# The Sands Of Time — Phase 1 architecture package

**Status:** implementation-ready planning package, **not an implemented application**, test report, Kiro usage log, or deployed software. Prepared 2026-11-09.

**Brand:** **The Sands Of Time** is the product/hackathon umbrella (formerly called Marrow); **Marrow** is the reusable technical foundation in this plan. **Revenant** is the synthetic finance app; **Ghost Lap** is the solo-history and multiplayer arena app. Changing the public brand does not require renaming all internal namespaces.

## Start here

1. Read `docs/architecture/01-master.md`, `02-spec-map.md`, `03-contracts.md`, and `docs/workflow/01-execution.md` once. Verify the actual repo state before applying this package.
2. Copy the root `.kiro/` folder into the repository root, preserving `.kiro/specs/spec-NN-<slug>/{requirements,design,tasks}.md`, steering, and MCP/hook config. Treat `docs/` as the engineering decision reference, not as always-loaded steering.
3. Start **spec-01-foundation-bootstrap** in Kiro; execute its tasks in listed order. There is deliberately **no application implementation code in this archive**.
4. Run checks on real code once implemented, record **actual** test/credit/CI evidence in `docs/kiro-log.md`. Never mark planned work complete from these documents alone.

## Local setup (from a fresh clone)

Prerequisites: .NET SDK 10.0.402 or newer 10.0 feature band (`global.json` requires at least 10.0.402, whose ASP.NET Core 10.0.13+ runtime is patched for CVE-2026-70159; older SDKs are refused), Node.js 22+ with npm, Docker with Compose v2, `openssl`, and Git. License: MIT (`LICENSE`).

```
git clone https://github.com/nightwing9-bit/the-sands-of-time && cd the-sands-of-time

# 1. Secrets: there are no defaults. .env is gitignored; .env.example holds placeholders only.
#    Generates the Postgres superuser (migrate job only), Redis, the three per-host DB logins and the identity master secret.
cp .env.example .env
for k in POSTGRES REDIS API_DB REALTIME_DB WORKER_DB; do sed -i.bak "s/=REPLACE_ME_${k}\$/=$(openssl rand -hex 24)/" .env; done
sed -i.bak "s/=REPLACE_ME_IDENTITY\$/=$(openssl rand -hex 32)/" .env && rm .env.bak
grep -v '^#' .env | grep -c REPLACE_ME   # must print 0

# 2. .NET: build and run the architecture gate (real-service tests need Docker; see the integration job in ci.yml)
dotnet build TheSandsOfTime.slnx
dotnet test tests/Architecture/Architecture.Tests.csproj
dotnet test tests/Integration/Integration.Tests.csproj   # optional: Testcontainers PostgreSQL/Redis

# 3. Web: install, typecheck, build both shells
(cd web && npm ci && npm run typecheck && npm run build)

# 4. Postgres, Redis, one-shot migrate, then API / Realtime / Worker
docker compose --env-file .env -f deploy/compose/docker-compose.yml up --build -d
curl -fsS http://127.0.0.1:8081/health/ready   # API; Realtime is 8082, Worker 8083

# 5. Browser shells (separate terminals)
(cd web/apps/ghost-lap && npm run dev)   # http://localhost:5174
(cd web/apps/revenant && npm run dev)    # http://localhost:5175

# Stop and wipe local data
docker compose --env-file .env -f deploy/compose/docker-compose.yml down -v
```

More detail: `deploy/compose/README.md` (ports, health, migrations) and `scripts/checks/README.md` (gate scripts). `.kiro/` is committed on purpose; never ignore it. Real test, Kiro and CI results are recorded only in `docs/kiro-log.md`.

## Contents

- `docs/architecture/`: master architecture, 13-spec DAG, versioned interface contracts, acceptance/coverage matrix, decision register.
- `docs/workflow/`: steering/inclusion plan, Kiro/MCP/hook/CI procedures, risk-and-budget allocation, execution playbook.
- `.kiro/specs/`: 13 complete **native** spec directories; each has requirements, design and bounded executable tasks.
- `.kiro/steering/`: three small foundation files and three focused conditional/manual files.
- `.kiro/settings/mcp.json`: selected Context7 and Chrome DevTools MCP configurations. Chrome disabled until browser apps exist; no credentials included.
- `.kiro/hooks/`: three valid Kiro hook configs, initially disabled until their referenced check scripts and apps exist (see workflow).
- `docs/kiro-log.md`: evidence ledger with instructions; only performed work is recorded.

## Non-negotiable boundaries

Shared engine is instantiated by two application adapters. A bank history is event-ordered; a game history is tick-ordered. Immutable originals cannot be rewritten. The original financial ledger is never a branch write target. A game ghost is a historical rendering, not a rival collision actor. Server, not browser, owns multiplayer scoring.

## External material still required

The original design inputs are **included**, unmodified, at `design-inputs/skills1 (1).md` (Revenant) and `design-inputs/skills2 (1).md` (Ghost Lap). They are on-demand presentation references for Specs 09/13, not automatically loaded Kiro steering and not authoritative over game/financial rules. See `docs/workflow/02-steering-and-assets.md` for the compatibility decisions and loading rules.

## Architecture corrections and manifest scope

The original `MANIFEST.json` reflects the pre-correction archive and is **not** a trustworthy post-patch whole-repository hash ledger. The selective correction packages each carry their own before/after hash manifests and backups; do not use the old manifest to overwrite corrected documents or mark software verification complete. Rebuild a comprehensive repository manifest only when the entire working project tree (including Spec 01–03, hidden Kiro configs and original context) is available and stable. This housekeeping is not a Kiro start gate.

## Verification scope

Package consistency (files/dependency DAG/internal references) is validated during archive generation. Compiling .NET, running integration/E2E tests, connecting Kiro MCPs, measuring credit consumption, and confirming public deployment **require actual implementation and cannot be claimed here**.

**Official rule check:** `docs/architecture/06-official-rules-check.md` records verified 2026 event dates, Skeleton Crew two-app-folder requirement, and credit-request deadline; this does not replace official rules or prove entrant eligibility.
