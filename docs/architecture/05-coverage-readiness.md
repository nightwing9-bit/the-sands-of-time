# F. Coverage, acceptance and remaining true blockers

Every locked Phase 1 capability has an implementing spec and a concrete verification owner. **Planned acceptance checks below have NOT yet run.** A spec is finished only with real automated/observed results.

| Product/architecture capability | Implementing spec(s) | Objective acceptance owner |
|---|---|---|
| Public brand The Sands Of Time; internal Marrow names | 01,04 | Repo README/host labels, cross-app package imports |
| Modular monolith/3 hosts/Compose/AWS-ready/CI/architecture rules | 01,03 | Clean boot; NetArchTest; CI; HTTPS deployment config smoke |
| One reusable Journal+Timeline restoring/forking/comparing | 02,05,06 | Two reducers + both real adapters; hash/prefix/immutability tests |
| One shared React timeline scrubber / typed REST+SignalR | 04,05,06,09,13 | One import path; real seek/fork and E2E tests |
| Staff/guest identities, MFA/permissions and owner security | 03,07,11,12 | TOTP, guest persistence, cross-owner denial |
| Reliable outbox/Redis/reconnect/event dedupe | 03,07,10,12 | Postgres/Redis outage + duplicate effect tests |
| Six-bank Friday Ring seeded synthetic inputs and v7/v8 | 05,07,09 | Repeatable seed **and fresh per-run opening balances/IDs**; two non-destructive runs; real source-vs-alternative decision divergence and pending-versus-prevented finality |
| Payment lifecycle, holds, real double-entry/settlement and cases | 05,07 | DB deferrable trigger rejects incomplete **and** unbalanced groups; legal transitions/concurrent settlement |
| Four-eyes high-value release, audit chain, tenant policy | 07,09 | Two staff, self-deny, hash verify, RLS tests |
| ML.NET fraud classifier, anomaly, liquidity SSA, explanations | 08 | Evaluations/artifacts; real factors; forecast backtest |
| Five functioning Revenant screens and nonmutating financial branch | 09 | Browser Friday Ring and source ledger unchanged post branch |
| Game fixed whole 16:9 arena/4 mascots/2–4 slots | 06,10,13 | Geometry/collision tests and browser 2/3/4P visual checks |
| Candle schedule/charge/contact/Burst/distance score/same tick | 06 | FsCheck deterministic rules and anti-corner/playtest |
| Server-owned multiplayer realtime 2–4P/rooms/ready/rematch | 10,13 | Real clients, forged input denied, one loop owner |
| Original saved solo history/repeat challenges/full state forks | 06,11,13 | Any valid tick, source unchanged, guest reload/ownership |
| Read-only exact ghost with independent current candle world | 06,11,13 | Same seed/spawn, ghost cannot collide/collect, aligned score curves |
| Chat/emotes/invites/spectators/ranked results | 10,12,13 | Room messaging, spectator denial, one rating effect |
| Shareable permitted solo replay, profile/history and privacy | 11,12,13 | Revocable token, private default, all routes |
| Desktop/mobile input/reconciliation/mute/reduced-motion and UX | 13 | Playwright input cancel, small screen, load/error states |
| Real testing/sound evidence, budget discipline, MCP/hooks | 01–13 | Actual CI/Playwright/test/Kiro log entries only once executed |
| Public HTTPS/judge access and repeatable external demonstration | Late deployment/submission gate after 13; supporting app work 05,07,09,13 | Public URLs for both apps, real external HTTPS browser smoke, working test staff access/MFA instructions, successful fresh Friday Ring run and Ghost Lap guest journey |

## External inputs / bounded assumptions (not disguised as solved)

| Input or uncertainty | Default / owner action | What invalidates it |
|---|---|---|
| Existing frontend design inputs | Available unmodified at `design-inputs/skills1 (1).md` and `design-inputs/skills2 (1).md`; load explicitly in 09/13 only, never automatically in backend tasks | Their presentation preferences must not override the locked dark-default Revenant demo, server authority, or truthful verification hashes |
| Existing repository state and .NET/Node/DB versions | Treat all code as **not yet built**; spec-01 inspect, create/merge; SDK .NET 10 and compatible Node | Actual repo already contains verified implementation; preserve real working contracts |
| Current Kiro model pricing/remaining credits and MCP connected status | Plan total 4,000 with 550 reserve; read actual usage and server connection in Kiro | Observed costs, plan availability or connection errors |
| Hackathon dates and public requirements | Deadline/period and two app folders verified in `06-official-rules-check.md`; entrant-specific eligibility remains unverified | Actual entrant facts or later official rule amendment |
| LICENSE choice / public repo & hosting | Pick actual compatible open-source license; AWS instance/HTTPS credentials needed later | Team ownership, media third-party licenses or hosting cost/security policy |
| PaySim data artifact, redistribution/legal provenance | Use lawful reproducible ingest, synthetic fixture and real held-out report; don't ship copyrighted dataset blindly | Unavailable/legal restrictions; use different source, document model scope |
| ML.NET model/forecast true evaluated outcomes and attribution method | Pick/verify supported APIs and trained artifact in 08; show honest unavailable/error until working | Model fails backtesting or per-case API unsupported—adjust explanation labeling/method |
| Domain targets: 30Hz/90s, score coefficient/candle/Burst, 100× replay, 40m forecast | Keep versioned initial defaults, measure at 06/08/09/13; treat 100×/40m as unproven targets | Actual performance/fairness/SSA output evidence |
| Realtime scaling and room affinity | Deploy one Realtime replica for authority, no scale-out until owner fencing designed | Need for multiple instances or failover moves ownership; ADR mandatory |
| Final bespoke game art/licensing and soundtrack | Phase 1 complete with semantic placeholders and assets contract; don't fabricate unknown files | Product release/polish later depends on original/licensed assets |
| Browser test harness and infrastructure costs | Run real Playwright/Testcontainers/CI/k6 only after code exists; retain results | CI runner limits/phone availability; describe omitted measurements transparently |

## Readiness and risk call

Foundational Spec 01–03 correction has been applied in this architecture package; begin **spec-01** only after the actual local project and installation hashes are checked. The working software, migrations, models, benchmark results, live URL, entrant eligibility verification and real Kiro-credit expenditure **are not provided**. This is a real plan, not evidence of an implemented service. No deferred third app, full multiplayer archives, final art pass or submission-production schedule has been introduced in Phase 1.
