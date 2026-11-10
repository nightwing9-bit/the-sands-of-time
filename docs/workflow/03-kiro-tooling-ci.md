# E. Kiro tooling, hooks, MCPs, CI, evidence

## Official-current conventions (verified 2026-11-09)

Feature Spec directories contain `requirements.md`, `design.md`, `tasks.md`, as Kiro Feature Specs uses EARS criteria: https://kiro.dev/docs/specs/feature-specs/ . Steering frontmatter matches https://kiro.dev/docs/steering/ . Workspace MCP file is `.kiro/settings/mcp.json`: https://kiro.dev/docs/mcp/configuration/ . Standalone hook schema is `version:"v1"`, `hooks:[{trigger,matcher?,action:{type:"command",command},enabled?}]` at `.kiro/hooks/*.json`: https://kiro.dev/docs/hooks/ . Kiro IDE/CLI versions vary; confirm local surface supports `PostTaskExecution` before enabling. The included configs are **inactive** so they cannot run scripts not yet implemented.

## Selected MCPs; least privilege

1. **Context7** in spec-01 foundation: workspace config has vendor-documented remote URL `https://mcp.context7.com/mcp` (https://github.com/upstash/context7/blob/master/packages/mcp/README.md). Connect only on supported Kiro surface; confirm connected state and permission approval. Use for **targeted** .NET/EF Core/ML.NET/PixiJS/SignalR API lookup when code requires external precision, not on every task. No API key checked into repo; use provider-supported user-local/secret mechanism if a key is needed.
2. **Chrome DevTools MCP** enabled only in spec-04 after React app boots and reused in 09/13 browser gates; vendor command `npx -y chrome-devtools-mcp@latest --headless --isolated`, as documented at https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/README.md and Kiro client guide. Pin package version after install smoke testing in actual repo (latest in example is not deterministic). Inspect console, network, layout, screenshots; never capture authentication secrets or production user data. Config is `disabled:true` until the browser and Node prerequisites actually exist.
3. Do **not** install extra MCPs/custom agents uncritically. No claim of connected servers was made.

## Three selected shell-command hooks

**Marrow Contract Guardian** (`.kiro/hooks/marrow-contract-guardian.json`): `PostFileSave` on public C# contract/shared-Timeline and typed client files; `bash scripts/checks/contract-guardian.sh` runs *fast* `dotnet build` relevant projects, NetArchTest (or focused equivalent) and TypeScript generated client/type check where practical. Script must be **written and tested in spec-01**, then guardian enabled after spec-02/03 contracts exist. **No** full Playwright/Testcontainers on save. Event JSON is passed on stdin; script must safely ignore it unless validating.

**Architecture & Quality Gate** (`architecture-quality-gate.json`): `PostTaskExecution`; `bash scripts/checks/spec-gate.sh` consults an explicit, **repository-controlled current-spec gate file** or task selection to execute only that spec's fast tests; full integration is a separate basket-gate CI job. Create script spec-01 and enable only once it has actual suites. Avoid cyclic hook-triggered edits, default skip if no marker, nonzero on actual failure. In case Kiro version lacks `PostTaskExecution`, keep disabled and run the script manually at task/spec exits; never substitute unverified alternate schema.

**Browser Evidence Capture** (`browser-evidence-capture.json`): `PostTaskExecution`; `bash scripts/checks/browser-evidence.sh` uses deterministic Playwright script to run **named** existing UI journeys (spec-09 or 13 only) and retains traces/screenshots/console+network error logs with secrets masked. Enable in spec-09/13 once Playwright and browser paths are ready, only for explicit UI task marker; not every code save or agent turn. Chrome DevTools MCP is for manual diagnosis and is **not** a mandatory MCP call inside a shell hook.

Scripts belong in `scripts/checks/`; the **hook JSON alone is not an executable test suite**. None has run yet. Script exit status must reflect true failed check. Never recursively invoke Kiro or expensive AI agents from a hook.

## CI pipeline

- Spec-01 creates `.github/workflows/ci.yml`: PR/push `restore/build`, TypeScript typecheck/lint, cheap unit tests, NetArchTest and changed-scope tests. Cache package restore only; lock dependencies. Use short timeouts and cancel stale PR workflows.
- Integration: Testcontainers real PostgreSQL/Redis; migrations on empty and upgrade fixture; schema ownership/DB balance trigger/settlement concurrency/outbox inbox; triggered on relevant changes and basket gates, not every file save.
- Domain property: FsCheck replay invariants, finance conservation/idempotency, Ghost charge/candle/schedule/same-tick; deterministic fixed seeds in PR, broaden seeded matrix at basket gate.
- UI E2E: Playwright browser smoke at spec-04, expanded finance in spec-09 and Ghost 2–4 browser clients including spectator/mobile in spec-13. Retain actual trace/screenshot only on fail or nominated demo evidence run.
- k6/tick FPS/load/projection latency: **measure**, record hardware/seed/suite, no assert targets without baseline. Docker Compose local with health waits. Deployment smoke on AWS once hosted; cloud cost tracked separately from Kiro credits.

## Evidence and change control

One task entry appended in `docs/kiro-log.md` only for observed activity, with specific commit and tests. Use `docs/evidence/` for actual logs/Playwright artifacts, not mock output. Use a short ADR in `docs/adr/` only for changed cross-spec contract/authority/rules decisions; update specs/contract matrix and verify producer+consumers together. Genuine failed first-pass reasoning can be recorded as an accepted/rejected engineering decision, not embellished as a Kiro success story.
