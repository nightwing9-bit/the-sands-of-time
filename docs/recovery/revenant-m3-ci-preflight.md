# Revenant Milestone 3 GitHub Actions preflight

16 November 2026

## 1. Executive verdict

**READY — REAL CI GREEN.** The approved M2 UI and its assets are preserved in one commit on the existing R8 branch. The real R8 browser test passed against the authenticated API, PostgreSQL and a separate Replay Worker, with four verified economic deltas and matching source hashes. A local production-build Chromium check confirmed that a browser without a session sees sign-in even with preview-like URL parameters. The real browser test exercises case approval through the API and verifies the UI update, leaving the authenticated approval dialog and multi-actor review for Milestone 3.

## 2. Git and preservation

| Item | Evidence |
|---|---|
| Repository | [nightwing9-bit/the-sands-of-time](https://github.com/nightwing9-bit/the-sands-of-time), public. The existing GitHub CLI identity had repository push access. |
| Branch | `codex/r8-counterfactual-revenant-20261114` |
| Starting SHA | `a1c7c2473ac05f15005c1f0cba477c90a2c96656`. This was the local HEAD, live remote tip and draft PR head on entry. |
| New SHA | [e08ac9ba86dc8c11813d3a0a29edfb3d061bd3fa](https://github.com/nightwing9-bit/the-sands-of-time/commit/e08ac9ba86dc8c11813d3a0a29edfb3d061bd3fa), `feat(revenant): implement approved laptop UI on R8`. The normal fast-forward push succeeded and a fresh live ref lookup matched. [Draft PR #8](https://github.com/nightwing9-bit/the-sands-of-time/pull/8) updated automatically. |
| Commit contents | 35 paths. R8 React, CSS and formatting helpers. Two WOFF2 fonts plus their SIL OFL texts. Revenant's package script, TypeScript include and separate fixture Vite config. Real R8 spec and laptop config, stubbed M2 and liquidity specs, and the shared synthetic fixture module. Approved HTML and nine PNGs. M1 and M2 handoffs. |
| Preservation | The original three modified Kiro documents, old design Markdown deletion and five unrelated untracked notes retained identical status and SHA-256 fingerprints across the commit and push. The index was empty after the commit. No application files were edited during preflight. `git diff --cached --check` flagged one trailing space in the supplied Plus Jakarta Sans license, which was preserved verbatim. A targeted scan found no matches for private-key, GitHub-token or AWS access-key signatures in the staged text. |

### Preview isolation

- Launch with `cd web && npm run dev:preview -w @sands/revenant`, then open `http://127.0.0.1:5173/?view=r8`. The separate config `web/apps/revenant/vite.r8-preview.config.ts` injects `import.meta.env.R8_PREVIEW_TOKEN` from the synthetic fixture. The normal `view=r8` parameter selects the R8 React shell. The preview config itself enables bypassing the sign-in form.
- The same React components issue their normal requests. The preview server middleware handles staff auth, run, case, liquidity, Timeline, Replay and audit requests with in-memory responses sourced from `tests/E2E/revenant-r8-m2.fixtures.ts`. The config requires Vite's serve command, rejects Vite preview mode and enforces loopback binding. The fixture responses live in that dev-server middleware.
- `RevenantR8.tsx` initializes the fixture token only under `import.meta.env.DEV`. The default Vite config provides no preview token or fixture middleware. The production build uses that default config. Bundle checks found no preview token variable or fixture middleware signatures. Chromium served the production output on an ephemeral loopback port and checked both `/?view=r8` and `/?view=r8&preview=1&R8_PREVIEW_TOKEN=fixture&run=fixture&job=fixture#rewind`. Both showed the sign-in button with zero navigation elements for authenticated screens, zero staff or finance requests and zero uncaught page errors.
- CI has its own normal Vite server on 5175 with `VITE_API_ORIGIN` pointed at the real backend on 18181. The real test explicitly signs in and uses the backend bearer token. Its spec and config contain no request interception, HAR substitute or preloaded browser session.

## 3. GitHub Actions execution

| Item | Evidence |
|---|---|
| Workflow and trigger | `ci` in `.github/workflows/ci.yml`. The push synchronized same-repository draft PR #8 and triggered one `pull_request` run, attempt 1. The workflow's direct push trigger is scoped to `main`. |
| Run and revision | [Run 43851411472](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43851411472). Head `e08ac9ba86dc8c11813d3a0a29edfb3d061bd3fa`. GitHub's PR test-merge revision `9ece86a54c6aa890c86433c4e83b582c9807d3c2` has the identical tree `3155dc8262ebab3dd0bae0574b71089ef30b355a`. |
| Target | YAML job `e2e`, **browser e2e (stubbed + real backend)**, [job 133244619860](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43851411472/job/133244619860). Step 12, **Playwright R8 with the real API and separate Replay Worker**. The job and step both completed successfully. |
| Timing | Job 3m 36s. R8 step 58s including both .NET builds. Playwright reported 1 passed in 32.2s, with 22.8s for the laptop test itself. |
| Runner and dependencies | Standard GitHub-hosted `ubuntu-latest`, observed Ubuntu 24.04.5 LTS. `actions/setup-dotnet@v6` reads `global.json` (.NET SDK 10.0.402, `latestFeature` roll-forward). `actions/setup-node@v6` selects Node 24, caches npm, and the job runs `npm ci` separately in `web` and `tests/E2E`. Chromium is installed with Playwright's `--with-deps`. Standard compute for public repositories is free under GitHub's published rules. [Runner documentation](https://docs.github.com/en/actions/how-tos/write-workflows/choose-where-workflows-run/choose-the-runner-for-a-job) |
| Browser prerequisites | The same job passed Chromium and package setup, 26 stubbed tests, 17 general real-backend browser tests and four real Revenant slice tests before the R8 step. |
| Workflow conclusion | Completed successfully. The separate .NET Testcontainers integration job also passed its Revenant real-PostgreSQL step. The coverage table below uses the R8 browser spec as its evidence. [Testcontainers integration job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43851411472/job/133244619835) |
| Browser artifacts | The always-run upload step looked at `tests/E2E/test-results` with seven-day retention. Its log says the directory contained no matching files, so `e2e-test-results` was absent from the run's artifact list. The R8 config has no automatic trace, screenshot or video capture option. The browser job log is the evidence. |

### Real-stack orchestration

1. The job pre-pulls `postgres:18.6-alpine3.24`. `tests/E2E/backend/E2eDatabase.cs` starts an ephemeral PostgreSQL container in runner Docker through Testcontainers. It copies `scripts/db` into the container, executes `scripts/db/migrate.sh` there, then provisions limited identity/Timeline, ledger and Replay database roles. There are no workflow service containers or persistent local Docker data. Redis is unused by the browser path. `E2eBackendFactory.cs` sets a placeholder Redis connection for readiness registration, and the test waits on `/__e2e/info`.
2. R8 step 12 builds `E2E.Backend.csproj` and `Hosts.Worker.csproj`. `playwright.r8.config.ts` starts the built backend with `E2E_R8=1`, `ASPNETCORE_ENVIRONMENT=Testing` and generated fixture credentials, then runs the normal React Vite server. `E2eBackendEntry.cs` starts the real `Hosts.Api` composition over Kestrel on loopback. The fixture adds control routes and provisions an operator in tenant `r8-browser`. The product registers `MapRevenantR8(IdentityPolicies.Staff)` in `Hosts.Api/Program.cs`.
3. The host startup checks that the restricted Replay credential is refused a live ledger write, then launches `dotnet Hosts.Worker.dll --replay` as a separate process with the tenant-scoped Replay connection. The ordinary LiveOps Worker does not start. Product R8 commands drive the simulation and finance routes directly against PostgreSQL.
4. Chromium signs in via the real staff form. The spec uses the returned bearer to enroll and confirm TOTP via the real MFA endpoints. Real browser writes then use the app and typed client, except the explicitly direct approval and invalid-fork API requests. The Replay job is queued by the real API and the browser waits for the worker-produced result.

## 4. Real integration coverage

References below point to `tests/E2E/revenant-r8.spec.ts` unless specified. PASS reflects assertions reached in this run. Source-only contract evidence appears separately in section 6. NOT COVERED indicates that the spec lacks the interaction or assertion. NOT ESTABLISHED indicates that the run did not capture the claimed detail.

| Area | Covered? | Result | Evidence |
|---|---|---|---|
| Real staff login and protected access | Yes | PASS | Sign-in POST succeeded, its bearer was used for MFA and protected reads and actions, and the operator created a real run (lines 18–51). |
| MFA positive path | Yes | PASS | Real TOTP enrollment and confirmation succeeded and the session performed protected review and replay intervention (28–32, 48–51, 71–77). |
| Browser session recovery | Yes | PASS | The page reloaded via a failed-job deep link, rendered the job and created a second run without submitting another login (100–109). Refresh rotation, expiry and sign-out are NOT COVERED. |
| Missing MFA, incorrect role and forbidden run | No | NOT COVERED | The test provisions one permitted operator and confirms MFA before the tested writes. Its successes do not validate access-denial behavior. |
| Six-bank scenario, snapshot and held case | Yes | PASS | Real run creation returned HTTP 200, the six-bank map and Current balances rendered, a protected snapshot contained an Open case, and a second launch returned a distinct run ID (36–47, 105–109). |
| Opening and current amounts for all banks | Partial | NOT COVERED | The test loads the real fixture and asserts the section and map. It has no per-bank amount or opening-ledger parity assertion. |
| Recorded model evidence | Partial | PASS | The real Held Queue displayed the recorded-score UI backed by case risk data (54–56). Exact ppm, model pin and evidence digest are NOT COVERED. |
| Single case authorization, release and settlement | Yes | PASS | One versioned, idempotent, authenticated approval through the API returned HTTP 200. The browser showed the payment Settled and the Held Queue case Released (48–56). |
| Browser approval dialog, second distinct approver, live rejection, stale case version and numeric balance deltas | No | NOT COVERED | The live approval is a direct API call from one operator using the current version. There are no decline, contention or monetary reconciliation assertions. The separate sandbox intervention uses Reject. |
| Liquidity | Partial | PASS | A real liquidity response drove the baseline disclosure and data drawer with Available column (58–61). Exact posted, reserved and available totals, observation row count and values, eight projected points and curve coordinates are NOT COVERED. |
| Original closure and historical Timeline | Yes | PASS | The UI closed the source, entered the verified-source controls, restored event 0 via Home, and tapped the 08:55 UTC completed seal at event 288 (62–73). |
| Valid policy-v8 replay and verified child | Yes | PASS | The browser submitted policy-v8 with a sandbox intervention for P-001, displayed Verified and rendered a comparison chart. The real worker produced a distinct child Timeline ID and child record hash (71–86, evidence below). |
| Visible Pending and Running | Partial | NOT ESTABLISHED | The genuine job was submitted and reached Verified. The spec does not require either intermediate status to be visible or record the transition times. |
| Failed Replay | Yes | PASS | Direct API submission at event 289 was accepted as a job, then returned `Failed` with `verified: null`. The refreshed browser showed Replay Failed and zero KPI tiles (90–103). |
| Replay cancellation | No | NOT COVERED | No cancellation request was made. |
| Economic KPIs and UI parity | Yes | PASS | `verified.headlines` matched the fixture's ordered deltas and four DOM `data-delta` values matched the backend in order (78–85). Exact formatted value strings and breakdown rows are NOT COVERED. |
| Source hashes and original/child isolation | Yes | PASS | The Audit banner requires source status Verified and equality of the current record and state hashes with the verified result's source hashes. It rendered after Replay (87–88, `Audit.tsx`). The child ID, child record hash and evidence digest were logged. |
| Ledger audit-chain verdict and full provenance UI | Partial | NOT ESTABLISHED | The Audit handler calls `VerifyAuditChainAsync` while serving the response. The tested banner checks Timeline proof and source hashes. The spec does not assert `ledgerVerification`, ledger head, manifest hash, or the contents of the Child evidence drawer. |

Verified headline deltas were `150000`, `1`, `250`, `149600` in backend order, equivalent to $1,500 intercepted, one legitimate payment delayed, $2.50 false-positive intervention cost and +$1,496 net. The existing spec separately compares the browser attributes with those real API deltas. `Rewind.tsx` formats the returned values. The $1.50 delay cost in the prior handoff is supporting fixture evidence. The current browser test does not separately assert that breakdown amount.

The job log records the following values from the verified API response.

| Field | Observed value |
|---|---|
| Source record hash | `4b8e15a9084e1945a62a7222c96cff25884e9a07f7fbce53314244235a007a21` |
| Child record hash | `d40d0ab3917bfb1d2a75c6473cdd99669a630ce930d6cbbbc5eed061ac330f86` |
| Child Timeline ID | `01a1e3f7-b65d-7f2e-99c5-1d8daf9b90ca` |
| Comparison evidence digest | `8c30fdc1af2ca8a5783ab4207923a73c0af331a226144a7f3b56822c2648c65f` |

## 5. Failures

The target job and all browser prerequisites passed. The intentionally invalid Replay job was the expected negative case. The preview and Git checks passed. One error-copy mismatch is demonstrable from the committed source and was outside the browser test.

| Failure ID | Category | Exact symptom | Evidence | Likely cause | Files involved | Recommended next correction |
|---|---|---|---|---|---|---|
| M3-01 (source-confirmed) | FRONTEND INTEGRATION | A `403 sandbox_live_write_denied` response maps to the staff-role message instead of the sandbox-specific message. | The R8 review route emits this code when the run binding has a fork. The contracts client preserves the code, and `unwrap` appends it to the message. `explain` matches the generic `permission\|forbidden\|denied` branch first. This branch was not exercised in CI. | The generic denial match precedes the sandbox match. | `src/Revenant/Api/RevenantR8Api.cs:261`, `web/packages/contracts/src/failure.ts:144`, `web/apps/revenant/src/r8/shared.tsx:14`, `web/apps/revenant/src/r8/Held.tsx:200` | Prioritize the sandbox code ahead of generic forbidden handling. Verify the typed response and dialog copy using the existing M2 approval-dialog spec pattern. Include the real denial in an authenticated test where a permitted actor can address a sandbox binding. |

## 6. Outstanding M2 integration questions

1. **Staff identity.** Source confirms that `GET /auth/staff/me` uses the ordinary Staff bearer policy and returns `ActorResponse` with JSON `{subject, kind, roles, claims}`. The React shell reads `roles` and prefers supervisor, then operator. The R8 browser suite does not assert this request's status/body or the role chip, so live response verification is NOT ESTABLISHED. See `IdentityEndpoints.cs:33,56,101`, `IdentityServiceCollectionExtensions.cs:66` and `RevenantR8.tsx:47,75`.
2. **Comparison balances.** The real planner's alternative history starts at source genesis and includes source facts through the chosen fork seal, then appends the alternate suffix. The projector emits a row per bank per completed seal and aligns source and child by `(boundaryMs, bankId)` through the requested completed horizon. The API serializes that aligned series directly as `verified.balances[] = {atMs, bankId, source, alternative}`, each side containing `bankId` and decimal-string `postedMinor`, `reservedMinor`, `availableMinor`. It includes the inherited completed observations before and at the fork, then both sides' observations after the fork. Genesis has no standalone balance row in this series. `Rewind.tsx` plots those points directly. This is established by `V4Counterfactual.cs:101,158–163`, `V4ObservationProjector.cs:33–81`, `V4CounterfactualEconomics.cs:180–188` and `RevenantR8Api.cs:495`. CI asserted chart presence and did not log or compare individual rows.
3. **Timeline markers.** The server serializes the fact kind as `PaymentIntent`, `ReviewDecision`, `ReturnInstruction`, `Screening`, `Settlement` or `ObservationSeal` and attaches an intent ID only to Screening markers. `charts.tsx` draws seal ticks, screening triangles and dots for the other kinds. `Rewind.tsx` derives selectable dock markers from seals. Replay requires an eligible seal strictly before the horizon, a verified seek and source-verified controls. CI passed the real 08:55 seal and direct invalid-position 289 negative case. Exhaustive browser marker validation was NOT COVERED. See `ObservedFinanceV4.cs:99`, `RevenantR8Api.cs:240–242`, `Rewind.tsx:20–48,68–79` and `charts.tsx:115–117`.
4. **Approval error codes.** The source wiring preserves `403 mfa_required`, which matches the dialog's fresh MFA message, and `409 inapplicable-StaleVersion` plus `409 inapplicable-DistinctSupervisorRequired`, which match the stale/version and distinct/supervisor messages. The latter two are sourced from `PaymentLedgerStore.Cases.cs` through `PaymentLedgerStore.V4.cs` and the R8 review handler. The M2 stubbed suite exercised the MFA message. The real browser test covered the positive approval only. The sandbox denial currently maps as documented under M3-01.
5. **Development preview separation.** Confirmed by the compiled bundle, Chromium sign-in check, real config and real run. CI used the default Vite config and real Kestrel API with a staff session. Only the distinct local fixture server supplies the dev token and stubbed finance endpoints.

## 7. Kiro Milestone 3 handoff

- Keep the existing five-screen React app, real staff authentication, synthetic R8 fixture, PostgreSQL harness, product API and separate worker. Real CI demonstrated sign-in, MFA confirmation, scenario launch, a single server-side approve/release/settle, liquidity response rendering, verified historical seek, worker-executed policy-v8 Replay, API-to-UI headline parity, original hash comparison, and failed-job recovery.
- Correct the source-confirmed sandbox error copy in `Held.tsx` and verify it through the existing `revenant-r8-m2.spec.ts` dialog pattern. Treat the authorization rules and returned status/code as part of the existing contract.
- To complete the authenticated UI journey, use `revenant-r8.spec.ts` and its hosted harness to drive the approval dialog in Chromium, then add the necessary authority and balance assertions for distinct actors and stale versions where the fixture supports them. Verify post-decision posted, reserved and available amounts against the real response. Add focused assertions for the optional role chip, observed/projection values, and the Audit ledger-chain verdict. The current CI provides no failure evidence requiring changes to those implementations.
- The observed fork was 08:55 at event 288. The requested later 09:00 proof, equal-seed isolation, competing jobs, cancellation and interruption recovery, complete reconciliation, and worker timing are separate B3 acceptance work. Preserve the approved laptop composition and existing financial assertions.

## 8. Verification accounting

| Executed | Outcome |
|---|---|
| `pwd`, `git status --short --branch`, `git rev-parse`, `git branch -vv`, `git log -5 --oneline`, sanitized remote inspection, `git merge-base --is-ancestor` | Confirmed checkout, starting history and R8 ancestry. |
| `gh --version`, `gh auth status`, `gh repo view`, `gh pr list`, `gh pr view 8`, repository/PR/commit metadata via `gh api`, authenticated `git ls-remote` | Checked existing auth, public visibility, push permission, live branch head, draft PR, and PR test-merge tree. |
| Scoped `git diff`, `rg`, staged `git diff --cached --name-only`, `--stat`, `--summary`, `--check`, text signature scan and unrelated-path `shasum -a 256` fingerprints | Reviewed and staged only the named milestone paths, including all nine PNGs, fonts and licenses. Compared dirty files before and after. Retained supplied license whitespace. |
| `npm run typecheck -w @sands/revenant` and `npm run build -w @sands/revenant` in `web/` | Both passed. Vite reported two existing SignalR dependency comment annotations and completed the bundle. |
| One-off Node loopback static server using installed Playwright Chromium, plus `rg` on production `dist` | Verified production sign-in on both URLs and no fixture signatures in emitted assets. |
| Explicit-path `git add`, `git commit`, `git show --stat`, normal `git push origin HEAD:refs/heads/codex/r8-counterfactual-revenant-20261114`, `git ls-remote` | One commit and one fast-forward push. |
| `gh run list`, `gh run view`, job/run/artifact metadata via `gh api`, filtered `gh api …/actions/jobs/133244619860/logs` | Monitored the PR attempt and captured the browser result and hashes. `gh run view --log` initially deferred logs while another workflow job was still active. The completed browser job's REST log was available independently. No binary browser artifacts were available or downloaded. |

Skipped local Docker, PostgreSQL, .NET/ML suites, the full local stubbed browser rerun, manual workflow dispatch, and GitHub reruns. The historical M1/M2 runtime claims and 11 local stubbed passes were treated as prior evidence. The current commit received fresh build checks and its own hosted trial. No authentication, billing or runner blocker was observed. One standard workflow run was triggered, with no reruns or unusual runner usage observed. This report is a local working-tree artifact.
