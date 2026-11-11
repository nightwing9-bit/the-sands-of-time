# Deterministic check scripts

Plain bash, no network, no AI. None of them starts Kiro or any agent, and none of them reads the hook event JSON on stdin. Each step ends as exactly one of `PASSED`, `FAILED` or `SKIPPED`. A step is `PASSED` only when its command ran and exited 0. A run where every step was skipped prints `NOTHING VERIFIED` and is never reported as a pass.

Shared exit codes: `0` ok (skips are reported), `1` a check that ran failed, `2` bad arguments or malformed marker (nothing ran), `3` a required or `--strict` run could not run something.

## Invocation contracts

| Script | Used by | Reads | Runs |
|---|---|---|---|
| `contract-guardian.sh [--strict]` | hook `marrow-contract-guardian` (PostFileSave), CI-equivalent locally | nothing | `dotnet build` of Marrow.Kernel, Marrow.Timeline, Revenant.Contracts, GhostLap.Contracts; `tests/Architecture` (NetArchTest on compiled assemblies); `tsc` for `@sands/contracts` and `@sands/timeline-ui`. Fails if the working-tree fingerprint (`git diff` + untracked list) changed while it ran. Writes only gitignored `bin/obj`. |
| `spec-gate.sh [--gate-file PATH] [--strict]` | hook `architecture-quality-gate` (PostTaskExecution) | `scripts/checks/.active-gate` | Only the allow-listed suites named in the marker. No marker or no suites: reports `SKIPPED`, exits 0. |
| `realtime-scale-guard.sh [-f COMPOSE_FILE]... [--env-file FILE]` | CI `scripts` job, by hand before a deploy | the Compose file(s) (default `deploy/compose/docker-compose.yml`) and `--env-file` (default `.env.example`, interpolated only, never printed) | `docker compose config --format json` + `jq`. Fails (exit 1) when the resolved model runs Realtime as more than one process; exit 3 if docker or jq is missing. See `docs/architecture/07-realtime-room-authority.md`. |
| `ml-platform-probe.sh --native\|--docker-amd64\|--ci` | CI job `ml-platform-x64` (`--ci`), by hand | `tests/Revenant/Platform` | Spec 08 R08.13. Real ML.NET fit, save/load, predict: FastTree (`--native`, any host), plus `DetectSpikeBySsa` and `ForecastBySsa` (`--docker-amd64` runs a `linux/amd64` SDK container, emulated and best-effort on Apple Silicon; `--ci` runs on a real Linux x86_64 host with `libomp-dev` installed). Passes only if the expected number of probe tests executed and passed (TRX counters); a wrong host or a missing `libiomp5.so` exits 3 (not a pass). Evidence JSON goes to `TestResults/ml-platform/<mode>/`. SSA tests carry `Category=MlSsaX64` and are excluded from the fast filters. |
| `browser-evidence.sh [--route NAME] [--required] [--gate-file PATH]` | hook `browser-evidence-capture` (PostTaskExecution) | `--route`, else `browser_route=` in the marker | One Playwright spec, `tests/E2E/routes/NAME.spec.ts`, with `tests/E2E/playwright.config.ts`, writing artifacts to `docs/evidence/browser/NAME/`. |

`browser-evidence.sh` never downloads Playwright. If Playwright, the config or the route spec is missing it prints `NOT PROVISIONED`. Without `--required` that is a reported skip (exit 0). With `--required` (or `BROWSER_EVIDENCE_REQUIRED=1`) it exits 3. Route names must match `[a-z0-9][a-z0-9-]{0,63}`. Provisioning Playwright, the E2E project and secret-masked trace capture belong to specs 04/09/13; the "run Playwright" path has not been exercised yet.

## .NET SDK environment
`global.json` requires SDK 10.0.402 or newer. A system `dotnet` of 10.0.401 is refused ("The command could not be loaded"). `source scripts/checks/dotnet-env.sh` puts `~/.dotnet-10.0.403` (override with `DOTNET_SDK_DIR`) first on `PATH`, sets `DOTNET_ROOT`, and prints the resolved path and version. It never edits `global.json`. Source it in every shell that runs `dotnet`, including the Playwright `webServer` commands, which inherit the environment.
## Task gate marker

`spec-gate.sh` is armed by `scripts/checks/.active-gate`. That file is local runtime state and is gitignored. `.active-gate.sample` is the tracked template.

```
cp scripts/checks/.active-gate.sample scripts/checks/.active-gate
```

The marker is parsed as data with a strict `key=value` grammar, never sourced or executed. Keys: `spec`, `task` (labels), `suites` (comma-separated), `browser_route`. An unknown key, a malformed line or a suite outside the allow-list exits 2 before anything runs.

Allow-listed suites: `dotnet-build`, `architecture`, `web-typecheck`, `unit-marrow`, `unit-revenant`, `unit-ghostlap`.

- `unit-*` run the first test project under `tests/Marrow`, `tests/Revenant` or `tests/GhostLap` with `Category!=Integration&Category!=E2E`. If there is none, the suite is `SKIPPED`.
- Integration (Testcontainers) and E2E suites are not allow-listed. They run in their own CI jobs, never from a hook.
- To add a suite, edit `ALLOWED_SUITES` and the `case` blocks in `spec-gate.sh` together.

Set the marker at the start of a task and delete it (or leave it empty of suites) when the task ends, so a stale marker does not run old suites.

## Hooks

All three files in `.kiro/hooks/` keep `"enabled": false`. Enable one only after its script exists, has been run, and the suites it names exist. Run the scripts by hand at task and spec exits until then. The contract guardian hook has a 50 s timeout. Warm runs take about 5 s on the dev machine, but a cold restore will exceed that.

## Not covered here

R01.08 / R04.08 (OpenAPI -> generated TypeScript with a stale-output check) lives in `scripts/contracts/generate.sh [--check]`, run by the CI job `contracts`. It is not a hook script: it builds `Hosts.Api`, so it takes longer than the 50 s hook budget on a cold restore. See `web/packages/contracts/README.md`.
