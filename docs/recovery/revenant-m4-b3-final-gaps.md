# Revenant Milestone 4 · B3 final gaps

## 1. Verdict

**BOTH GAPS CLOSED.** A regular browser mouse click on the payment-day 09:00 marker selected verified event `302` and produced a verified policy-v8 Replay child. The PostgreSQL-backed browser journey completed a $15,000 V4 payment through two live React approvals by distinct staff, with a server-denied repeat attempt and a single reconciled settlement. All eight standard GitHub-hosted jobs passed. [CI run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349)

## 2. Git

| Item | Result |
| --- | --- |
| Branch | `codex/r8-counterfactual-revenant-20261114` |
| Starting commit | `5900db2f72f3be633c02691f322ca1a023313c08` |
| Final commit | `bb09c90ff8eddd3db492a227d0e0f4c6f7bb7cac` |
| Commit message | `fix(revenant): make timeline markers clickable and prove four-eyes UI` |
| Push | Normal fast-forward to the existing branch. Local HEAD, upstream and draft PR #8 match. |

The pre-existing modified and deleted paths and untracked notes were preserved. The Git status after the push matched the starting status. This report was written after CI and remains local. [Draft PR #8](https://github.com/nightwing9-bit/the-sands-of-time/pull/8)

## 3. Timeline root cause

The shared scrubber places absolute marker buttons at their event-position percentage. Revenant's dock overrode the shared 8 px width with 12 px and centered each hit rectangle using a −6 px margin. The buttons occupied the same vertical band. Events `302` and `304` sit about 0.66% of the track width apart, which put their centers about 6.2–6.4 px apart. The later terminal button painted above the earlier one at the collision edge. Chromium's center hit test returned the 09:10 button at both centers.

Measured before the fix with the real React dock and modeled seal responses. Coordinates are in CSS px relative to the viewport.

| Viewport | 09:00 rectangle x / y / w / h | 09:10 rectangle x / y / w / h | Centers x / y | Horizontal overlap |
| --- | --- | --- | --- | ---: |
| 1440 × 900 | 1082.625 / 812 / 12 / 26 | 1089 / 812 / 12 / 26 | 1088.625 / 825 and 1095 / 825 | 5.625 px |
| 1366 × 768 | 1032.828 / 688 / 12 / 26 | 1039 / 688 / 12 / 26 | 1038.828 / 701 and 1045 / 701 | 5.828 px |

## 4. Timeline correction

Revenant measures the track with its existing resize hook and allocates space between marker centers from their actual positions. At the two laptop sizes, labels and 24 px-wide buttons have at least 36 px between centers. The dock shifts each crowded label and button together and draws a short leader to its exact track coordinate. The dark dock, hourglass, track, chart composition and teal styling remain in place. Both laptop screenshots were reviewed. [1440 × 900 dock](../../TestResults/revenant-b3/43885774349/local/1440-dense-dock.png) · [1366 × 768 dock](../../TestResults/revenant-b3/43885774349/local/1366-dense-dock.png)

The shared scrubber accepts an optional presentation-only offset and still seeks the marker's original decimal string through the adapter. Revenant uses the typed `GET /revenant/r8/runs/{id}/timeline/{position}` route and selects the verified response. Ghost Lap uses the existing default placement and adapter. Its hosted mount, keyboard seek, fork and denial checks passed, and the shared unit case verified that an offset marker still seeks exactly above 2⁵³. The source remains read-only. [Hosted browser job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386113)

| Scope | Changed files |
| --- | --- |
| Revenant dock | `web/apps/revenant/src/r8/Rewind.tsx`, `web/apps/revenant/src/r8/r8.css` |
| Shared presentation and unit coverage | `web/packages/timeline-ui/src/TimelineScrubber.tsx`, `web/packages/timeline-ui/test/scrubber.test.tsx` |
| Browser tests and host lifetime | `tests/E2E/revenant-r8-m2.spec.ts`, `tests/E2E/revenant-r8.spec.ts`, `tests/E2E/playwright.r8.config.ts` |
| Test fixture startup and scheduler preparation | `tests/E2E/backend/E2E.Backend.csproj`, `tests/E2E/backend/E2eBackendEntry.cs`, `tests/E2E/backend/E2eBackendFactory.cs`, `tests/E2E/backend/R8FixtureStartup.cs`, `tests/Revenant/Payments/R8FourEyesFixture.cs` |

## 5. Pointer tests

| Browser test | Viewport | Executed result |
| --- | --- | --- |
| Adjacent Rewind seals with modeled responses | 1440 × 900 and 1366 × 768 | Local and hosted pass. Regular clicks selected `302` → `304` → `302`. Hit rectangles were disjoint and each center hit its own button. The active time label and preview tracked selection. `302` enabled Replay and was submitted as a decimal string. `304` disabled Replay. The dock fit without document overflow. |
| Actual R8 Replay with PostgreSQL and the separate worker | 1440 × 900 | Hosted pass. The 09:10 click returned Verified event `304` and kept Replay disabled. The 09:00 click returned Verified event `302` for 13 September 2026, 09:00 UTC. The actual policy-v8 submission reached Verified. |

The real source run was `dc24666c-a647-1f3f-2b2c-87f965e9ccb8`, Timeline `01a1e4c3-e123-7141-896b-c409aabf9295`. Preview state hash was `f2200f8dee61236715899f8c1fd12e04dffd3d2aee780596cb3340b5f1582e50`. Replay job `cca2f734-b297-46e2-97e8-e4de84597d9b` produced verified child `01a1e4c4-15a2-78ce-b61c-bcf97e96f789`, record hash `bd6eee4670b5bc4c648d20a93439bba88467c9911675f0f73e4995583d354ec3` and state hash `7d132fd3260097fe6053324d219de7220779c1e20e78224f492a661762fc3e15`. The complete source Audit response matched before and after Replay, and the bank balances matched across the inherited prefix. The existing 08:55 demonstration retained its **+$1,496** net change. [Hosted browser evidence and JUnit](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14233593778)

## 6. Four-eyes fixture architecture

The test host provisions a dedicated operator and supervisor in the existing `r8-browser` tenant through ASP.NET Identity. It prepares one fresh six-bank run with a unique run ID in the isolated Testcontainers PostgreSQL instance. The input uses a funded BANK-A account and a BANK-B beneficiary account from the real deterministic account manifest. The test checks account-level available funds before submission and the principal-sized reservation after screening.

The existing V4 scheduler seals the 24-hour warmup, then dispatches the canonical $15,000 intake and screening at 13 September 2026, 08:55 UTC. The production policy-v7 sanctions rule returned `SanctionsHit` and the pinned model recorded `167151` ppm. The scheduler created the Held payment and two-approval Open case. The startup assertion verifies the SQL-backed case and the reservation through the synchronized V4 history.

The authenticated operator owns the run and used the production run-staff grant route to add the supervisor, receiving HTTP 204. Both staff identities obtained fresh MFA through the real React form in separate browser contexts. The preparer runs only as startup code in the R8 test host under `Testing` and has no HTTP route. The normal Release API publish contained neither test assembly, fixture type nor fixture route. The E2E executable exited with code 2 when started under `Production`. The production finance, identity, authorization and RLS implementations and the standard scenario seed were unchanged.

## 7. Real browser approval evidence

| Hosted identity or resource | Observed value |
| --- | --- |
| Dedicated run | `667f0c15-f0da-4f7b-b848-34c70aacac51` |
| Timeline | `01a1e4c3-d1bf-7203-b13e-1f3ab3a990ab` |
| Case and intent | `74cd9ad9-6ddf-4be2-a65a-844e7ffb29ba` · `P-FOUR-EYES` |
| Principal and route | 1,500,000 USD minor units ($15,000) · BANK-A → BANK-B |
| First actor | `staff:7f8ceb3f-c0e4-4637-9ff8-a0f85c34be9f`, operator with fresh MFA |
| Second actor | `staff:7aee1bda-2ae1-42c1-8225-85f37f9b193e`, supervisor with fresh MFA |

| Browser action | Observed outcome |
| --- | --- |
| Cancel first dialog | Zero review POSTs. Case stayed Open at version 1 and 0/2. Balances and Audit stayed identical. |
| Operator confirms first approval | One HTTP 200 POST with expected version 1 and a valid idempotency key. Case became AwaitingSecondApproval, version 2 and 1/2. Payment stayed Held and all balances stayed identical. |
| Same operator attempts final approval | One HTTP 409 POST with expected version 2. Backend returned `inapplicable-DistinctSupervisorRequired`, which React displayed as a requirement for a different authorized supervisor. Case, balances and settlement status stayed identical. |
| Supervisor confirms final approval | One HTTP 200 POST with expected version 2 and its own valid key. The same case became Released, version 3 and 2/2, and the payment became Settled. |

All values below are posted / reserved / available in integer USD minor units. Each observation reconciled for all six banks. The only scheduled financial effects after the initial snapshot were the two accepted review facts and a single settlement at the same observation boundary.

| Bank | Opening | Held and after first approval | After settlement |
| --- | --- | --- | --- |
| BANK-A | 5590514 / 0 / 5590514 | 5590514 / 1500000 / 4090514 | 4090514 / 0 / 4090514 |
| BANK-B | 5977575 / 0 / 5977575 | 5977575 / 0 / 5977575 | 7477575 / 0 / 7477575 |
| BANK-C | 3021688 / 0 / 3021688 | 3021688 / 0 / 3021688 | 3021688 / 0 / 3021688 |
| BANK-D | 4092434 / 0 / 4092434 | 4092434 / 0 / 4092434 | 4092434 / 0 / 4092434 |
| BANK-E | 5121395 / 0 / 5121395 | 5121395 / 0 / 5121395 | 5121395 / 0 / 5121395 |
| BANK-F | 1577712 / 0 / 1577712 | 1577712 / 0 / 1577712 | 1577712 / 0 / 1577712 |

Audit returned and displayed review facts `291` and `292` for the two authenticated staff subjects, followed by one settlement fact `293`. The fixture has one payment, and these fact positions match the case and payment transitions in its real snapshots. The open Timeline reported Recording and the ledger audit chain reported Verified, with head `7a4d88ecf33bfbeb6d6b36f6b77f8d973153934ac8616547295e8a9f0ad6d8f8`. [Hosted browser evidence](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14233593778)

## 8. CI

| Gate | Result |
| --- | --- |
| PR-triggered workflow | Eight standard jobs passed, zero failed, four opt-in jobs skipped. One workflow, attempt 1, zero reruns and zero corrective pushes. [Run 43885774349](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349) |
| Browser | 29 stubbed, 17 real-backend, 4 Revenant slice and 3 real R8 tests passed. Zero failed. The two-actor test executed in 29.633 s. [Job 133363386113](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386113) |
| PostgreSQL integration | 109 integration, 61 Marrow, 98 Revenant and 1 focused upgrade test passed. Zero failed. Existing Replay, lifecycle and four-eyes PostgreSQL assertions remained green. [Job 133363386694](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386694) · [Revenant TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129071) |
| Local | Revenant typecheck and build, 17 shared Timeline unit tests, 20 scoped stubbed browser tests, the final focused two-viewport rerun, C# harness compilation, real R8 discovery and the production isolation checks passed. Local Docker and PostgreSQL were left idle. |

Commit `bb09c90ff8eddd3db492a227d0e0f4c6f7bb7cac` was the head SHA for the workflow, pushed branch and draft PR.

## 9. Remaining limitations

The real-backend journey uses the existing 1440 × 900 configuration. The 1366 × 768 pointer and layout evidence uses Chromium with modeled API responses. There are no open blockers for the two requested acceptance gaps.

## 10. Stage C readiness

**READY FOR FINAL B3 CERTIFICATION.** Final certification is a separate handoff.
