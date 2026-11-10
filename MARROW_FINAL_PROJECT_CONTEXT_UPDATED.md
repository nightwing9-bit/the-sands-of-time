# MARROW — REVENANT × GHOST LAP
## Unified final project context | Kiroween 2026 | Skeleton Crew

**State as documented:** planned product and architecture, not proof of implemented, tested or deployed software. **Consolidated source through 7 November 2026.** Names are working names: **Marrow** (foundation), **Revenant** (financial app), **Ghost Lap** (haunted arena game; **Ghost Loop** is an unchosen alternative). This document contains the single current project design, with decisions distinguished from proposals and open variables.

**Decision labels:** **[LOCKED]** decided direction; **[CONSTRAINT]** requirement/boundary; **[TARGET]** unvalidated tuning/target number; **[PROPOSED]** specific implementation candidate; **[OPEN]** unresolved; **[DEFERRED]** not part of the initial polished release; **[SOURCE-CLAIM]** details from supplied rules not independently verified. All planned demos, quantitative claims, features, models and Kiro integrations remain planned until functioning evidence exists.

# 1. Product, ambition, constraints

## 1.1 Final ambition and why it exists

**One bank. One game. The same bones.** **Marrow** is a lean, modular, reusable **C#/.NET foundation for authenticated, auditable, realtime, deterministic systems**. Its distinguishing shared capability is **Timeline**: record ordered history, restore exact state, continue in an isolated branch, and compare historical and alternate outcomes. It composes **two distinctly useful, working applications** rather than two skins of the same interface.

- **Revenant (App A):** a bank-grade-*style* **synthetic payments, fraud decision and liquidity operations control tower**. Staff observe live payment flow, investigate held payments, inspect anomaly/ML reasons, see consequences in settlement and forecast liquidity, rewind to a historical point and recompute an alternative policy/model decision path. The alternate history appears as a financial **ghost line**, with fraud prevented, false positives, legitimate payments delayed and net effect. The **actual ledger is never altered by counterfactual replay**.
- **Ghost Lap (App B):** a **2–4-player, ~90-second, top-down haunted arena arcade game**, **not a lap/vehicle racer**. Players traverse a visible 16:9 arena, collect timed candles, accumulate four Soulfire charge stages, contest space with contact and radial Bursts, and win on total score. Its separate **Race Your Ghost** mode records an **original solo run**, plays an exact read-only spectral history at the same seed/spawn/ticks and lets the player rewind to **any valid tick**, **restore complete historical state**, change subsequent inputs and compare time-aligned scores. It also has live friend lobbies, chat/emotes, spectators, rated standings and shareable permitted **solo** replays.
- **Why together:** bank and game have incompatible domain logic but the same general journal/restore/fork/compare need. **Both must use one actual Marrow Timeline implementation and one reusable React scrubber interaction** with distinct domain adapters. Genuine separate products and verifiable reuse are central to the Skeleton Crew category.

**Users:** Revenant — fraud analysts, payment operations, treasury/liquidity staff, auditors and compliance teams exploring **synthetic** decisions; not a real-money bank. Ghost Lap — casual browser players, friends arriving via invitation, and solo players wanting replay and personal improvement without an AI opponent. The game is a full app, not a toy animation to decorate the bank pitch.

**Builder and platform constraints:** ambitious enterprise-quality backend, security and causality in App A; polished immediately understandable fun and multiplayer interactions in App B; shared foundation must remain clear/lean. Preferred AWS over Azure; backend C# 14/.NET 10; React/TypeScript browser interfaces; ML.NET instead of adding a Python ML subsystem. Source context describes a student builder and limited prior ML/game-development experience. App B explicitly has **no ML**. Do not force game concepts into finance or vice versa.

## 1.2 Hackathon and directly relevant submission rules [SOURCE-CLAIM]

**Kiroween 2026 / Skeleton Crew:** a working app built with **real Kiro use** (IDE/CLI/Web/Crew as applicable), and **a lean reusable skeleton demonstrated through two distinct applications**. A viability/theme/Kiro-use pass–fail precedes five **equal 20% judging dimensions**: **Potential Impact, Technological Implementation (including Kiro), Design, Innovation/Idea, Presentation**. The source excerpt warns insufficient Kiro use can disqualify.

Submission requires **public working application URL(s)**, free judge access for the judging period with test credentials/instructions if needed, accurate feature description, **video shorter than three minutes**, **public GitHub (not GitLab/Bitbucket)** containing working source, assets, run instructions and visible **open-source LICENSE**, **root `.kiro/` committed and not ignored**, and a substantive truthful **how Kiro was used** explanation. Eligible student category, Best Demo and public Kiro blog bonuses may matter; a blog must be in the eligible window and mention **Kiroween Hackathon 2026**. Exact submission deadline, eligibility and official terms were **not verified in the source** and remain [OPEN]. Prize amounts and historical award research do not affect product architecture.

**Judging impact on product:** demonstrably functional two-app reuse; real causal finance outcomes; a game understood in five seconds; full usable navigation; compelling real replay/branch; verifiable architecture/tests; visible authentic Kiro involvement; a concise video. Unmeasured reuse percentages or invented ML/game performance results are not selling points.

## 1.3 Current scope and honest limits

- **Two working apps and one Timeline** are the product; optional third mini-app/scaffold demonstration is **[DEFERRED]**. One modular monolith with distinct host configurations, not an obligatory microservice fleet.
- Revenant uses **public synthetic PaySim plus a project-built seeded payment simulator**. No real payment processing, production fraud accuracy, financial certification, guaranteed forecasts or live customer data is represented.
- Ghost Lap has **2–4 living multiplayer competitors**, but **full multiplayer input archives, multiplayer ghost challenges and multiplayer timeline forks are not initial features**. Multiplayer result summaries are durable. **Original solo histories** are the replay/ghost source; a ghost is not an opponent or AI and cannot affect the current world.
- Early **fully functional interfaces with placeholder graphics** precede final haunted art. A placeholder is acceptable; a fake branch, broken button, non-persisted history, simulated remote opponent or cosmetically moving scrubber is not a completed feature.
- Performance/reuse figures, score examples, tentative mechanics and delivery schedule are targets or illustrations, not measurements or guaranteed results. All unresolved choices remain explicitly open below.

# 2. Final demonstration design and signature interactions

## 2.1 Final demo pitch — “The Blunder” [LOCKED NARRATIVE; VISUAL DIRECTION NOT LOCKED]

The following is the exact new demo pitch for the YouTube video and project narrative. Its timed visual-direction labels are not fixed production instructions. It supersedes the earlier bank-first demo storyline and timed storyboard; the existing app requirements, scenarios and visual capabilities remain part of the project.

**[0:00, face cam, casual]**\
 “Last month I was beating my best friend in a Valorant 1v1 series. Up 11 to 8. One round from winning. Then I peeked a corner I had no business peeking. Dead. I lost the next four rounds, and honestly, the match.

And the thing that kept me up that night wasn’t that I lost. It was that I knew the exact second it happened. One decision. And I couldn’t go back to it.”

**[0:25, screen: Ghost Lap, rewind scrubber]**\
 “So I did what any sleep-deprived engineer does and built the thing I wished existed. A game where you can scrub back through your own match, drop in at the exact moment you blundered, and play on from there. Against your own ghost, the original you, replaying every move exactly as it happened.

This is Ghost Lap. Watch. I rewind to my worst moment, I pick a different path, and the ghost keeps doing what I originally did. Two timelines, same arena, scores side by side. For the first time in my life I’m competing against a version of myself.”

**[1:00, the turn]**\
 “But I was playing it and I realized I’d accidentally built something that isn’t about games. Every time I rewound, the system was doing four things: record history, rebuild a past state, branch without touching the original, compare outcomes.

And I thought, who else is desperate to do that?”

**[1:20, screen: Revenant dashboard]**\
 “Banks. A fraud analyst is making my Valorant decision every day. Tighten the fraud rule and you stop more fraud, but you block real customers trying to pay rent. Loosen it and you’re friendlier, but you let bad money through. That’s my peek. The problem is, you can’t test a new rule on real history. You can’t un-send a payment.

So I took the same engine and pointed it at a bank. This is Revenant: six simulated banks, a double-entry ledger, live flagged transactions. All synthetic data. No real money.

Here’s the analyst’s version of my moment. I rewind to a past day. I change the fraud policy. Revenant runs an isolated alternate history on the same transactions and shows both outcomes side by side: fraud intercepted, legitimate payments delayed, and what each cost. The real ledger is never touched.”

**[2:05, screen: architecture diagram, two apps on one core]**\
 “Here’s what matters for Skeleton Crew. Those aren’t two projects. They’re two applications on one foundation I call Marrow. Timeline, identity, real-time messaging, event history, observability: shared, and unchanged. What differs is only the rules. A game has movement and scoring. A bank has ledgers and payment controls. I wrote the foundation once, with Kiro specs driving it, and each app plugged in.

Same Timeline code restores a Valorant-style arena and a bank’s Tuesday afternoon.”

**[2:40, close, face cam]**\
 “I started by wondering what would have happened if I hadn’t peeked that corner. It turns out that’s the question every system asks eventually.

Marrow: one engine, two worlds, and a way to ask ‘what if’ without paying for it. Thanks for watching.”

### Demo-first development emphasis

**Hero moment:** **Rewind → See the original ghost → Choose a different action → Watch timelines diverge → See the score difference.**

Then demonstrate that the same mechanism works with money and fraud decisions.

The **Ghost Lap rewind interaction** becomes the **first major end-to-end demonstration milestone**, with **Revenant's corresponding financial branch developed alongside it**. Existing multiplayer, game mechanics, financial screens, ML capabilities and supporting features **remain in scope**; they receive less screen time in the video. The demo story and its actual working interactions are a development priority from day one, not a video assembled after completing unrelated features.

### Open demo-acceptance questions and cautions

- Can the user visibly identify their mistake in Ghost Lap?
- Can they rewind, branch and see a meaningful divergence immediately?
- Can Revenant demonstrate an equally understandable financial consequence?
- Can we prove both are powered by one implementation?
- Can these moments be captured and understood in under three minutes?
- Does the phrase **“rewind your own match”** demonstrably refer to an **original saved solo run**, rather than implying that multiplayer matches are recorded and branchable?
- Does **“same transactions”** accurately refer to the **same synthetic historical payment inputs**, while allowing downstream holds, approvals, settlements and outcomes to differ under the alternative policy?
- Does the architecture reveal provide actual shared-code proof, and is the claimed Kiro usage backed by real development evidence, even though the new pitch gives Kiro less standalone narration time?

The specific financial and gameplay proof paths in §§2.2–2.3 below remain relevant application/demo capabilities; they do not restore the superseded old video ordering.

## 2.2 Revenant golden path — “The Friday Ring”

**Seeded, repeatable synthetic scenario**; figures derive from simulator/policy/model computations and are labeled as synthetic.

1. **Normal flow:** animated balances and directed payment arcs between **six simulated banks** with live activity.
2. **Triggered ring:** a known-seed/time group of mule accounts initiates structured transfers; **Policy v7** lets some suspect traffic through.
3. **Causal decisions:** scores and anomaly signals flag/hold some payments. Holds/settlement alter bank balances and feed a liquidity forecast with a shortfall indication **about 40 minutes ahead** (**illustrative scenario output**, not a validated universal prediction).
4. **Analyst workflow:** an operator approves/rejects a held payment; high-value release illustrates dual control/four-eyes if that tier is implemented.
5. **Counterfactual:** select **09:00**, swap to **Policy v8**, run isolated replay at **target 100× visualization/playback speed** (not proven throughput).
6. **Original vs alternate:** overlay original/v8 trajectories as **ghost-blue** line and derived metrics: **fraud prevented, legitimate transfers delayed, false-positive cost, net effect**. The exact v7/v8 rule/model difference and formulae are still [OPEN].
7. **Audit:** verify original hash-chain continuity and that the branch did not rewrite real ledger history; optional audit export.

A minimal stubbed end-to-end path may precede completed ML/ledger internals during development; **the final recording must accurately distinguish real completed capabilities from stubs**. The five user screens are Control Tower, Held Queue, Liquidity Fan, Rewind Mode and Audit.

## 2.3 Ghost Lap live and solo demonstration

**Live:** on a stationary whole-arena 16:9 view, four visually distinct head-like mascots traverse walls and timed candles. Candles increment visible charge through **0–4**; the armed Overlit mascot glows, then emits a **radial Soulfire Burst** that affects eligible *live* rivals under server rules. Show an actual invite/join, real multiplayer impact, time expiry, score-based result and appropriate social/rating/spectator interface.

**Personal ghost:** create/save a real **90-second original solo run** or select an eligible saved one. Challenge it with the **identical seed, rules/layout version, initial spawn and candle schedule**; at tick 0 living and spectral mascots are superimposed. The ghost repeats **original historical inputs at normal speed** and is completely noninteractive. HUD compares **past versus current scores at the same tick**. Scrub to an earlier time (**00:38** is illustrative), select **Rewrite From Here** and continue from **complete historical state** using different future inputs. Display the score curves, old/new total and signed delta; original is unchanged. Example source figures **1,420 → 1,578 (+158)** are **hypothetical**, not actual test measurements.

**Proof image/interaction:** bank v7-v8 ghost financial line beside game previous-run cyan ghost/score curve; **one** generic Timeline mechanism behind both. Formal hash/invariant tests and a one-source/two-adapter code reveal support the visual.


# 3. Marrow — shared architecture, contracts and deployment

## 3.1 Architectural shape, deployment and module boundaries

**Style:** **Modular monolith**, deliberately not a sprawling microservices fleet. Reason: reusable skeleton should be **lean and understandable**; the financial ledger benefits from a local ACID transaction; a ten-service demo is fragile. Yet the modules are designed for boundary discipline and possible future extraction.

- **Three host processes/configurations:** **Api** (HTTP/API endpoints), **Realtime** (SignalR hubs and owned match/server tick loops), **Worker** (payment stream processing, ML inference-related/background workflows, settlement jobs, replay projections as appropriate). Hosts may share one codebase/reusable modules and be deployed together or separately where their actual configuration supports it.
- **Contract isolation:** each module exposes a public **`Contracts` assembly**. No direct references into another module's internals. **NetArchTest** (or equivalent) checks dependency rules in CI.
- **Persistence ownership:** a single PostgreSQL instance, **schema per module**, **no cross-schema foreign keys**. Modules interact with other modules through public in-process commands/contracts or **transactional-outbox integration events**; don't silently couple domains through foreign-key convenience or direct table updates.
- **Event delivery:** transactional outbox, idempotent consumers and planned **Redis Streams**; a **small hand-rolled mediator/command-query pipeline** is the preferred implementation (exact code/contracts remain undecided).
- **Shared infrastructural primitives:** typed/strong IDs, `Result`, entities/domain events, idempotency, deterministic RNG/simulation, snapshots/journals, health, telemetry and tests.
- **Optional scaling proof [PROPOSED]:** configuration-based Realtime host extraction into its own container with no code changes, only if actually supported and demonstrated. **Do not assume separation of a tick-loop process automatically solves distributed session ownership**.
- **Operational topology:** start with **Docker Compose** on one reasonably inexpensive AWS Lightsail/EC2-class instance behind HTTPS/TLS reverse proxy; split subdomains for the two products; database/Redis as Compose services where appropriate for a hackathon. ECS Fargate + RDS + ElastiCache and full IaC are **optional upgrades**, not baseline requirements. Keep a working local `docker compose up` fallback.
- **Local developer orchestration [planned]:** .NET Aspire AppHost/dashboard and OpenTelemetry for traces/health/telemetry; CI on GitHub Actions; Playwright browser tests; k6 measurement. Not every optional tool must ship.

**Cross-domain boundary rule:** **Ghost Lap must never import Revenant ledger/ML assemblies**, and Revenant must never import Ghost Lap's game rules. Both may depend on Marrow's shared protocols, Timeline engine, building blocks and infrastructure abstractions. Shared Realtime is transport/presence/session infrastructure; the messages and domain policies are distinct. A bank transaction is never a “game tick” in disguise.

## 3.2 Intended core repository organization (logical layout; paths not finalized)

The tree is a logical arrangement of shared modules, domain modules and host compositions; **exact physical folder paths remain [OPEN]**.

```text
marrow/
├── .kiro/                    # Real specs, steering, hooks, etc.; committed
├── apps/                     # Optional, prominent separate product compositions
│   ├── revenant/             # App A web/product-specific assets and configuration
│   └── ghost-lap/            # App B web/server/assets; placeholders then production
├── src/
│   ├── BuildingBlocks/      # Kernel: Result, Entity, DomainEvent, typed IDs, etc.
│   ├── Marrow/              # Shared reusable modules (or src/Modules/)
│   │   ├── Identity/
│   │   ├── Realtime/
│   │   ├── Ranking/
│   │   ├── Chat/
│   │   ├── Journal/
│   │   ├── Timeline/
│   │   ├── Simulation/
│   │   ├── Outbox/
│   │   └── Observability/
│   ├── Revenant/            # Ledger, payments, policy, risk, liquidity, etc.
│   ├── GhostLap/            # Physics, arena, Soulfire, game modes and rules
│   ├── Intelligence/        # ML.NET opt-in, used in Revenant; placement adjustable
│   └── Hosts/               # Api, Realtime, Worker
├── web/                     # Shared React shell, tokens, typed API/hub clients
│   └── ...                  # Actual app-specific web UI may live under apps/
├── tests/                   # shared, architecture, unit, property, integration, E2E, load
├── deploy/                  # Compose, optional Aspire/AWS config and IaC
└── docs/
    ├── adr/                # Actual architectural decisions, supersessions
    ├── kiro-log.md         # Evidence of meaningful real development interactions
    └── ...
```

**Logical per-module layers** (where justified): `Domain`, `Application`, `Infrastructure`, `Contracts`, `Api`. The Ghost Lap modules may add a `RealtimeHostAdapter`. This is a **suggested organization**, not a mandate to generate hundreds of empty projects. Respect the Skeleton Crew requirement for leanness.

## 3.3 Shared modules and their *actual* distinct domain uses

| Shared engine/module | Revenant implementation/composition | Ghost Lap implementation/composition | What should actually be reused |
|---|---|---|---|
| **Kernel / BuildingBlocks** | Money-compatible primitives, Result, domain events, typed IDs | Typed IDs, deterministic state and domain events | Infrastructure patterns and contracts; not shared business types such as `Money` with `Soulfire` |
| **Identity** | Staff authentication, JWT/refresh cookie, MFA, policies/ABAC, four-eyes workflows | Guest/remembered player identity, profile, history ownership and permitted sharing | Authentication/session primitives and claim/policy building blocks, **different policies and UX** |
| **Realtime** | Live payments and liquidity streaming, alert/presence updates | Game rooms/state stream, lobbies, spectator transport, chat transport | SignalR abstractions, presence/reconnection, typed transport, appropriate Redis backplane; **not bank payloads as game payloads** |
| **Journal** | Append-only financial/domain/audit event history | Original solo inputs/events, history identity, result trace | Ordered durable history and verification abstraction |
| **Timeline** | Restore historical financial projection; branch alternate fraud policy; compare | Restore world at tick; branch new player inputs; compare old and new score | **Same generic restore / replay / fork / compare orchestration, immutable source semantics and shared scrubber** |
| **Simulation Kernel** | Seeded simulated payment flow/fraud-ring events, domain state evolution | Deterministic 30 Hz arena world update loop, movement/candle/Burst state | Deterministic scheduling/RNG/snapshot contracts; **not one hardcoded 30 Hz clock across both** |
| **Outbox / Eventing** | Ledger/payment/case transitions to projections and notifications | Match-finalized event to ranking/leaderboard, notifications | Reliable delivery, idempotency and event integration infrastructure |
| **Ranking** | Risk/account leaderboard/queue metrics | Player ratings and standings | Board/cache/storage mechanics (e.g. Redis sorted sets + Postgres snapshots); **not shared scoring policy** |
| **Chat / comments** | Analyst case comments | Lobby quick chat and emotes | Messaging transport/storage patterns with app-specific moderation, permissions and meaning |
| **Observability** | Ledger/replay/ML/system telemetry, tracing, health | Match loop/tick and networking telemetry, health | OTel/health/logging instrumentation pattern |
| **Shared web shell / design tokens** | Dense serious analytical interface | Fullscreen arcade renderer, compact menus/HUD | Foundation, navigation patterns if appropriate, typed clients, shared Timeline scrubber; **not forcing identical visual layouts** |
| **Intelligence (ML.NET)** | **Enabled**: fraud classifier, anomaly, SSA forecast, explanation | **Disabled / no ML dependency** | **Opt-in extensibility**, not pretending that game and bank share a trained model |

**Reuse evidence:** A reuse percentage is **not measured or locked**. If reported, it requires a verifiable `cloc`/dependency methodology with an explicit denominator. Shared boundaries and actual engine reuse matter more than line-count targets.

## 3.4 Intended technology stack and why each part exists

**Backend:** C# **14** with **.NET 10 LTS**, ASP.NET Core Web API, EF Core, PostgreSQL (schema-per-module, transaction constraints, optional row-level security), Redis (Streams, sorted sets, caching, presence/backplane and only justified locks), SignalR (MessagePack where useful), ML.NET **Revenant only**, OpenTelemetry and optional .NET Aspire. **C# for ML.NET** was an intentional choice to avoid creating a Python subsystem for this prototype.

**Frontend:** React + TypeScript + Vite; TanStack Router/Query; Zustand where state needs it; OpenAPI-generated typed REST client; typed SignalR client; shared tokens/components; PixiJS/WebGL for the game; uPlot or equivalent for high-density financial time series/ghost overlays. The UI may include WebGL-inspired visualizations for payments but the bank must not depend on game-specific assets.

**Quality/infra:** Docker/Compose; Git/GitHub; GitHub Actions; **FsCheck** property tests; **Testcontainers** with real Postgres/Redis service integration; **NetArchTest** boundary rules; **Playwright** E2E/demo automation; **k6** load testing; optional AWS infrastructure-as-code; versioned configs, health endpoints, measured performance results.

**Mediator selection:** a **small hand-rolled command/query pipeline** is the intended baseline. Any library alternative requires a current license/dependency assessment; no unverified license claims are part of the design.

## 3.5 What must **not** be shared

- Revenant ledger aggregates, money/settlement state machine, bank-specific RLS/authorization policies, fraud models, sanctions logic, liquidity forecasting and compliance presentation **belong to App A**.
- Ghost Lap movement, physics, walls/candles, Soulfire, contact/Burst, tick rate, gameplay scoring, matchmaking/rating formula and PixiJS asset scene **belong to App B**.
- App A's “policy branch” and App B's “player input branch” adapt to one Timeline but do **not** need identical payloads, clocks, schemas or domain processors.
- Shared module code must not be copied into app-specific projects and called reusable. Both application compositions should depend on the same actual Marrow assemblies/services, with clear dependency direction and automated boundary tests.


# 4. Revenant — financial product and technical specification

## 4.1 Identity, differentiator and audience

**Revenant** is a **bank-grade-*style* payments/ledger and liquidity operations control tower**, built as a realistic but simulated **financial decision laboratory**. It combines payment intake/lifecycle, double-entry ledger semantics, case handling, fraud-policy and anomaly intelligence, live balance/liquidity views, audit evidence and **“what if we had made a different fraud decision?”** replay.

**Primary benefit:** Financial ops teams can inspect what happened, understand why a payment was held or released, rewind to an earlier historical point and explore the **counterfactual cost/benefit of a different fraud policy** without modifying original records. The notable product moment is a **ghost alternate-history line overlaying the original financial timeline**, terminating in a clear headline effect.

**Realistic framing:** Model examples and payments derive from **public synthetic PaySim** data plus a project-built seeded payment/fraud-ring simulator. No real customer payments, real fraud-detection deployment, real liquidity guarantees or production-banking certification are claimed. Functional fidelity and correctness should be evidenced for what is implemented.

## 4.2 Five-screen product plan

1. **Control Tower:** Serious dark live overview showing money-flow arcs/Sankey-like movement among simulated banks, activity pulse/balance heartbeats, an alert ticker, and a visible causal chain as traffic, held decisions and liquidity respond.
2. **Held Queue:** Payment cases with fraud/anomaly scores and **plain-language explanations**, high-level reason indicators (e.g., unusually high velocity/new beneficiary if true of actual model/feature rules), approve/reject workflow and **four-eyes** supervisor release for high-value decisions if included in scope.
3. **Liquidity Fan:** Intraday liquidity forecast with central line, uncertainty/confidence bands and shortfall alert. Must visibly respond to payment/settlement scenario rather than a detached random animation.
4. **Rewind Mode:** **Global Timeline scrubber**, select source time/event, switch **Policy v7/v8** (threshold/model/approval rules as defined), launch 100×-style replay, overlay alternate financial **ghost line**, display counterfactual card with fraud prevented, false-positive cost, legitimate payments delayed, net effect; ideally replay link/inspectable branch ID.
5. **Audit:** Hash-chain verification, transaction/event evidence and optional export, demonstrating that original journal history was not edited by the branch.

The video may show all five briefly, concentrating on the **Rewind Mode**. Payment and alert details are integrated into these five surfaces rather than additional separate top-level screens.

**Visual identity:** obsidian/dark charcoal background, bone-white typography, one restrained ectoplasm-green accent, **ghost blue** for alternate histories. Calm, data-rich and credible; motion reserved for actual money/state changes. In the video, whenever one account or bank balance drops, show the upstream action responsible and downstream liquidity/alert implication.

## 4.3 Core financial domain and transaction correctness

**Intended backend depth** (architectural commitments, not proof of an audited bank system):

- **Double-entry ledger**: debit/credit parity enforced at the **PostgreSQL database** level using **deferrable constraint triggers** for balanced posting groups, not only a C# check. Property tests should verify debits equal credits and conservation across transactions/sequences.
- **Append-only journals**: financial/domain historical rows cannot be silently updated in ordinary application flows; planned DB privilege strategy **revokes `UPDATE`** for append-only tables. Corrections should be explicit new events/transactions rather than rewritten past.
- **Payment lifecycle state machine:** **initiated → screened → held → approved → cleared → settled**, with **returned** as an appropriate terminal/failure flow. Legal transition matrix and idempotency must be specified; treat the source arrow sketch as intended lifecycle, not proof every return path has been formalized.
- **Idempotency** for payment initiation, event consumption, approvals and settlement effects; transactional outbox for reliable follow-up streams/projections.
- **Settlement concurrency:** serializable transactions where justified, hot-account correctness, controlled locking and `FOR UPDATE SKIP LOCKED` for competing batch workers. Validate deadlock/retry/correctness behavior in integration tests.
- **Multi-currency `Money` value type** with explicit currency and rounding rules. Rates/exchange conversion policy are not specified; don't invent a cross-currency guarantee.
- **Sanctions/name screening** proposed via **fuzzy name matching without ML**. Must distinguish fuzzy rules from model-based fraud scoring.
- **Tenant isolation:** PostgreSQL **Row-Level Security** was planned for a multi-tenant bank-style deployment; whether all tenant boundaries are required in the critical path is flexible (earlier cut line made full RLS demo optional). Never claim tenant isolation is comprehensive without tests.
- **Security:** ASP.NET Identity, JWT access plus **rotating refresh cookie**, authorization policies/ABAC, staff MFA, and **four-eyes** release for particularly sensitive payments. Detailed threat model, identity UX and scope should be driven by actual work.
- **Audit:** **hash-chained tamper-evident audit log**, with verifiable continuity and documented assumptions. This supplements, rather than replaces, actual database access controls.
- **Case management concurrency:** optimistic locking/versioning on cases; Redis locks were proposed for assignment when warranted. Match transactional side-effects to authoritative Postgres operations.
- **Architecture:** Revenant-specific ledger/Payment/Case/Settlement/Policy/Liquidity modules or domain slices live **above** Marrow's generic Identity, Journal, Timeline, Simulation, Realtime, Outbox, Ranking and Observability.

**Why these matter to judging:** The product is not just a pretty graph that says “fraud.” Correct state transitions, balance conservation, independent replay and verified audit information make the counterfactual claim credible. On camera, prove selectively; place details in code/tests and technical write-up.

## 4.4 Four planned ML.NET capabilities (Intelligence module, opt-in)

| Capability | Intended model/technique | Inputs/purpose/observable outcome | Required honesty boundary |
|---|---|---|---|
| **1. Fraud classification** | ML.NET **FastTree or LightGBM** classifier, trained from public synthetic **PaySim** plus controlled simulated payment flows | Score suspect payments; drive screening/held queue; compare policies v7/v8 | Evaluate on held-out data, avoid claiming bank-grade detection or guaranteed validity. “Fraud prevented” comes from labelled **simulated** outcomes. |
| **2. Velocity anomaly detection** | ML.NET anomaly methods such as **SSA / PCA-based** techniques as appropriate | Detect unusual account/payment velocity or behavior spikes, make a feature/reason visible | Method/feature choice, thresholds, drift handling and false positives **not finalized**. |
| **3. Intraday liquidity forecast** | **SSA time-series forecasting**, central path and uncertainty/confidence bands | Predict simulated cash/liquidity availability, illustrate likely settlement shortfall roughly 40 minutes ahead in Friday Ring | Bands and claimed shortfall must be computed from implemented model/assumptions, not hard-coded as universally accurate. |
| **4. Per-payment explanations** | **Feature contribution** or permutation feature importance / model-specific explanation method | Expose comprehensible contributing factors such as unusual velocity or new beneficiary **when actually grounded in model inputs** | Explanation mechanism must match model/library behavior. A global importance score is not automatically a faithful per-payment local attribution; validate API and semantics. |

**Versioning:** Policy v7 vs v8 may mean thresholds, rule parameters, approval strategy and/or pinned trained model versions; the **precise difference is not locked**. For deterministic comparison, historical inputs/features, policy code and any relevant model artifact/version must be pinned or their outputs recorded as needed. Offline model training + committed/versioned artifacts avoids live-demo training fragility. **ONNX import** was mentioned as an escape hatch if required by an actually supported model, not a prerequisite.

**No ML in Ghost Lap.** The opt-in boundary is an explicit reuse/extensibility point: same Marrow foundation; one composition includes Intelligence, the other does not.

## 4.5 Historical replay/counterfactual technical semantics

1. The source financial timeline is an **immutable ordered history** of relevant business events, state transitions and model/policy evidence.
2. Select a historical point such as **09:00**, restore a snapshot/projection and replay to the intended branch position as required by domain state.
3. Apply the proposed **policy/model override** to a **sandboxed in-memory projection on the Worker**, using **the same actual ledger/domain rules** but **never mutating production ledger tables** or rewriting the source journal.
4. Re-evaluate downstream decisions, settlement effects and liquidity under the alternative assumptions, with deterministic ordering and version pinning.
5. Compute original vs alternative result curves and a counterfactual-card breakdown: **fraud prevented, false-positive cost, legitimate payments delayed, net effect**. Define monetary/operational metrics before claiming measured results.
6. Preserve an auditable reference to the original timeline, branch ID/override and verification hashes; do not let branch display/UI alter source state.

**Important nonidentity:** App A is ordered by financial events or business time; App B is ordered by 30 Hz game ticks. Marrow provides a **generic ordering/replay position abstraction**, not “force banking to tick at 30 Hz.” ML inference may introduce model/version/external-state issues beyond seed determinism; explicitly control them.

## 4.6 Revenant scenario examples beyond the hero shot

- **Payment holds:** Analyst sees a 12× velocity anomaly and new beneficiary flag **only if** those are actual available features; chooses hold/reject/approve, then observes how the action changes the bank flow view.
- **Liquidity:** A change in held transfers affects receiving-bank balances and an SSA-derived warning, helping a non-specialist see financial consequences.
- **Four-eyes:** Higher-value transfer approved by one staff user remains pending until authorized second approval; no privileged self-approval.
- **Audit:** Try to verify historic chain after a replay; verify source unchanged. Do not suggest that a UI green check proves immutable external storage.
- **Concurrency:** Trigger idempotent duplicate payment requests, settlement batch overlap or simultaneous case edits; show correct effects and tests in technical evidence, not necessarily the 3-minute video.

# 5. Ghost Lap — complete game, multiplayer and ghost specification

## 5.1 Game identity, emotional goal and genre — [LOCKED]

**Genre:** **2–4-player competitive haunted arena arcade game**, top-down, **fixed full-arena camera**, smooth free movement, straightforward to understand in **five seconds**. It borrows the immediate readability of Pac-Man's pickup loop **without** Pac-Man's tile-by-tile corridors; it is not a car racer, endless runner or lap race. The camera stays fixed; no follower, translation, aggressive zoom, roaming POV or hidden areas. A wide **16:9 rectangular** logical world should fit a landscape browser frame and show every active player at once. The web experience should work on **desktop and mobile**, asking for/supporting landscape orientation appropriately.

**Feel:** fast turns, understandable world, contested temporary pickups, decision-making around collection versus avoidance and Burst positioning, short rounds, satisfying impacts and sound, instant rematch, an unusually personal challenge against visible past decisions. Don't make the game bloated with lore, large tutorials, skill trees, menus, combat clutter or endless meta-progression.

**Visual ambition:** An exceptionally polished **haunted stone/slate arena**, short architectural walls with sparse restrained occult illumination, warm candles, character glow that signals charge, and a cool cyan spectral past. Sparse pumpkins/skull carvings may decorate walls; there are **no pumpkin bombs or damage hazards** merely because of Halloween decoration. Avoid a cheap full-screen neon prototype look. Final art is **deferred behind functional art contracts**; style intention is locked, final sprites/colors/textures are not.

**Main modes:**

- **Multiplayer Arena:** 2–4 actual remote participants, share one authoritative active arena, race for **highest total score** in a ~90-second match, interact via contact and Soulfire Burst, with friend lobbies, chat, emotes, spectators, results/rematch and eligible competitive rating.
- **Race Your Ghost:** one current living player and a read-only projection of that same player's **immutable historical solo run**. First create/save a full original solo history. Later select/challenge it, watch it, repeat it or fork a new continuation from a selected historical tick. **No AI opponent or rival-network dependency.**

**Immediate novice explanation:** “Move around. Grab glowing candles. Four candles charge your Burst. Avoid stronger rivals. Score the most points before time runs out. Later, race your old self—or rewind a mistake and try another path.” In-game onboarding stays focused on controls and objectives, not backend architecture.

## 5.2 Arena layout and visual/simulation geometry

**[LOCKED geometry direction]:** large open traversable floor with **short, spaced walls/loops/routes**, little clutter, few dead ends, no dense one-width corridors. Players can see and understand movement paths. Four identifiable corner spawn sockets support a 4P match. 2P spawns must be balanced (e.g. opposite/symmetric); 3P placement must not simply take arbitrarily biased three corners without fairness evaluation. Start with **one deliberately authored replayable arena**. Later variation uses a **curated set of deterministic obstacle/layout patterns** selected by versioned seed, not untested unrestricted random walls.

**Initial tuning values [TARGET, not finalized]:**

| Parameter | Provisional initial target | Interpretation |
|---|---:|---|
| Arena shape / logical view | **16:9** | Fixed world geometry; letterbox/scale, **never stretch** to fit viewport |
| Round duration | **90 seconds** | Current intended round length (earlier range not an active separate requirement) |
| Server fixed simulation | **30 ticks/sec** | The current simulation target; **2,700 ticks** in a 90 s round |
| Wall footprint | **≈12–18%** of floor | Visual-density target, not a universal hard invariant |
| Active competitors | **2–4** | Max 4; separate fairness for 2, 3 and 4 |
| Character rendered size | **≈40–48 logical pixels** | Illustrative phone-readability target, not collision radius |
| Collider shapes | **Circles** for players; simple **rectangles/constrained shapes** for walls | Keep collision radius independent of sprite size |

**World rules:** All server-authoritative positions/collisions are expressed in world coordinates; canvas/client scaling never changes gameplay geometry. Use wall-slide/stop normal velocity for clear collision behavior; no sticky corners. Characters should feel controllable with little momentum/inertia.

**Candle schedule determinism:** Seed + **rules/layout versions** define the chosen arena, static obstacles, player spawn assignments, time-indexed **candle activation/expiry schedule** with stable IDs and any truly relevant random events. Prefer **precomputed world/candle schedule** or **independent domain-specific RNG streams** so a branch choosing a different candle cannot accidentally shift every future pickup. Do **not** advance one global PRNG in an input-dependent way that makes branch comparison meaningless. An untouched candle scheduled for tick 120 must remain scheduled for tick 120 whether historical ghost or current player visited the location.

**Fair spawn logic:** Candle positions from validated reachable points/cells; no wall overlaps, unreachable isolated pockets, mandatory one-cell bottlenecks or impossible pickups. Allocate meaningful opportunities in quadrants and a contested middle. Show a **short pre-spawn telegraph** before activation, then expire visible candles on schedule. A player standing where a beneficial candle activates may collect it then; do not silently relocate that scheduled candle just because of the player's location. Candle IDs, positions, activation ticks and expiry ticks must be reproducible independently of frame rate, rendering and network jitter.

**Fairness evaluation:** multiple seeds/layouts; comparable access/route distances across start sockets, 2/3/4P viability; tests for unreachable items, trapped players, biased quadrants and overly defensible empty corners. Contested area should matter without making the game a camping trap.

## 5.3 Four playable characters and input/movement

**[LOCKED]:** Exactly **four recognizable selectable mascot identities**, mainly **head/face silhouettes**, not complex full-body animation rigs. Cosmetics represent distinct characters. Initial implementation may use **colored circles/simple heads with eyes or labels**. Later original heads should preserve unique silhouette/readability when small and allow charge glow to signal danger. A player's glow escalates perceptibly with stages **0–4**, visible to rivals even without reading HUD.

**[PROPOSED fairness rule]:** Mascots have **identical movement speed, physical collider, health-equivalent rules and gameplay abilities** at launch; no hidden stronger character. Later game modes would require explicit balance/spec changes.

**Desktop inputs:** **WASD / arrows** move, **Space** (or configurable key) triggers Burst; no aim reticle or precision aiming mechanic. **Mobile:** landscape UI, **left virtual thumbstick**, **large right Burst control**, proper touch cancel/blur behavior. Show tiny in-context hint: **“Move → collect candles → charge 4/4 → Burst.”** Keep inputs usable with keyboard/tab where appropriate, contrast/focus states, volume/mute and reduced screen shake. Don't require account registration just to start a casual match.

**Physics authority:** The **C# server** owns position, velocity, wall and player collision, pickups, charge, score and Burst outcomes. Normalize diagonal vector so diagonal isn't faster; maintain deterministic acceleration/turn behavior; avoid stuns, knockouts, forced five-second pauses, elimination, heavy knockback or long control lockout. Client may predict local input and reconcile from snapshots; remote players are interpolated. **Render interpolation must never accrue distance or score.**

## 5.4 Candle collection and four-stage Soulfire economy

**[LOCKED core mechanic]:** Visible, **temporary candles** enter the world on predetermined schedules. A living player touching an active candle collects it **once in that player's current world**. A candle increases their **Soulfire `charge` by one**, bounded **0–4**, and contributes pickup score. Collection has immediate audio/visual feedback, even in greybox. Stage 4 is distinctly marked **Overlit/armed**: the mascot burns/glows brightly, the player can trigger **Soulfire Burst**, and rivals can recognize the threat.

**Targets for playtesting [TARGET]:** roughly **8–12 active candles** on an average 4P arena; each visible for roughly **6–7 seconds**; active movement should make a full four bars attainable in roughly **12–20 seconds**. These are pacing aspirations and may need major adjustment. Candle count, point coefficient, wave spacing, expiry, charge caps and timer must live in a **typed versioned `GameRules`**, not in sprites or arbitrary React constants.

**Precise charge semantics [PROPOSED where indicated]:**

1. `charge ∈ {0,1,2,3,4}`; a successful candle collection adds **1** up to cap.
2. First reaching 4 starts **Overlit**, with an **≈8-second** [TARGET] expiry window. Burst available immediately; if unused at expiry, **server automatically emits Burst** exactly once.
3. A Burst consumes the charge, resetting to **0**. No indefinite armed state.
4. While at 4, a collected candle may still score points **without increasing charge or resetting Overlit timer** [PROPOSED].
5. If an opponent damages a fully charged player below 4 before activation, **cancel Overlit**; regaining 4 starts a new window [PROPOSED].
6. Every timer, auto-event and same-tick decision is **server-authoritative and deterministically ordered**.

The HUD must show exact personal charge; other mascots broadcast approximate charge clearly through stage visuals. At stage 4, emit a conspicuous but readable telegraph. No visual effects imply extra game mechanics unless the versioned rules say so.

## 5.5 Direct multiplayer combat: contact and Soulfire Burst

### 5.5.1 Contact mechanics

**[LOCKED behavior]:** If two **equal-charge** players touch, **no gameplay damage**; display a mild bump. If one has **strictly higher charge**, the weaker player incurs a charge cost; the stronger remains unchanged. Ordinary contact **never directly gains/subtracts match score**. Both keep moving; no stun/elimination.

**Initial charge loss [TARGET]:** weaker loses **1 bar** on an eligible contact (not a permanently fixed balance value). **Contact cooldown [TARGET]: ≈1.5 s pair-specific** to prevent continuously overlapped players from draining each other repeatedly. Authoritative server decides which collision is eligible, charge comparison, immunity and event resolution order. Include blink/particles, quiet impact sound and modest local screen shake (configurable and reduced-motion friendly).

### 5.5.2 Soulfire Burst

**[LOCKED ability]:** At four bars the living player can release a **radial area-of-effect energy Burst**, possibly automatically after an **~8 s [TARGET]** Overlit window. It is the strongest visual/sound beat: charge pulse → circular wave → transient flash → valid impacts, particles and crisp event audio. Client visual sequence follows **resolved server events**, not vice versa.

**Initial balancing proposals, not final values:**

- **Radius:** meaningful relative to arena size/speed; must be measured and playtested, not guessed permanently.
- **Target effect:** for each eligible live rival within radius and (if adopted) line-of-sight, subtract **2 charge bars [TARGET]** and **80–100 fixed score points [TARGET]**, clamped at zero. A fixed penalty avoids volatile late-game percentage scaling.
- **Attacker reward:** small fixed hit bonus **[OPEN]** if useful; don't invent it as locked.
- **Wall occlusion:** whether walls block valid Burst targets **[OPEN]**; simple readable line-of-sight if implemented.
- **Same-tick interactions:** burst activations, contact, candle pickup, Overlit expiry and end-of-round must have one deterministic resolution order and tests.
- **Continuation:** no knockouts, damage-over-time, invulnerability frames, extra knockback or hidden effects unless explicitly specified. A cyan shockwave is a visual cue, not evidence of those mechanics.

**Illustrative 4P match:** Amber picks candles, Blue reaches four and Overlit, Amber takes refuge behind a short wall, Blue Bursts and hits Purple (not Amber if wall shielding is chosen). Purple loses score/charge but keeps moving. Gold then collects a contested center wave in the final 20 seconds and wins on **total score**, not remaining Soulfire.

## 5.6 Scoring, winning and fairness

**[LOCKED direction]:** **Legitimate actual distance traveled** is the principal score component. **Candle pickups** add a significant incentive to enter contested regions. **Successfully striking opponents** may provide a smaller competitive contribution if attacker reward is adopted. **Ordinary player contact awards no score**. Server calculates distance from authoritative post-collision movement with velocity caps, never from client claim, interpolated sprite travel, teleport correction or input spam.

**Initial average contribution goal [TARGET, not a formula]:** approximately **68% movement, 24% pickups, 8% combat** across normal gameplay. These are rough *observed match contribution* intentions, **not automatically the numeric coefficients** and not a constraint every player score must follow. Formally define score coefficients only after prototype/abuse testing. Deliberately test **“run in an empty corner forever”** strategies: they must not dominate active collecting and contesting play.

**Victory [LOCKED]:** At expiry, **highest final total score wins**. This applies to comparing living player with ghost as well; a higher final charge is **not** a second condition. Players who strategically spend their charge should not be punished for ending on zero. Results show total, distance/movement points, pickups, Burst count, Burst hits, players/standings (for live match); celebratory categories may acknowledge biggest improvement/most candles, etc. **Ties [OPEN]**; display as tie is a reasonable proposed policy rather than a hidden arbitrary winner.

**Important open design issue:** In **solo original-history and ghost challenges**, no real opponent exists to take Burst damage. Charge remains visible, Burst fires and pickups score; however the ability may have little strategic value. **Do not solve this by silently adding bombs, hostile AI or ghost damage**. Playtest whether charge timing itself is fun. If genuinely needed, consider one explicit **shared, deterministic environmental Burst use** affecting both modes in a future rules/version revision, with approval; none is part of current locked scope.

## 5.7 Multiplayer Arena: complete guest-to-rematch workflow

1. **Landing/guest identity:** minimal remembered guest/player. Menu exposes **Play Multiplayer** and **Race Your Ghost** as dominant actions plus History, Leaderboard, Watch, How to Play/Settings.
2. **Create or Join room:** short invite code and shareable link; guest joins without staff-style MFA/login. Validate membership, permissions and lobby transitions server-side.
3. **Lobby:** **2–4 active player slots**, chosen character, player names, ready/connected status, chat/emote picker, leave controls. Spectators occupy distinct read-only slots and cannot steal participant capacity. Host/ready rules and late-entry behavior are **[OPEN]**.
4. **Start/countdown:** host triggers only when valid minimum ready participants; optional **3–2–1–GO** countdown is **[PROPOSED]**. Server chooses/commits seed, rules/layout version and fair spawn sockets.
5. **Match:** Authoritative fixed tick loop processes validated movement/Burst inputs, scheduled pickups, contact, scores and auto-bursts; sends snapshots for rendering and spectators. HUD always shows time, score, charge and competitors without hiding critical space.
6. **Finish:** at approximately 90 seconds server **finalizes once**, persists roster/scores/statistics/rules/seed and publishes durable result and eventual ranking event for eligible competitive matches.
7. **Results/rematch:** winner/standings with breakdown and Return/Rematch; rematch new seed, reset states. Handle someone leaving, disconnecting or refreshing without hanging a lobby indefinitely. Leaderboard/profile navigation remains available.
8. **Spectators:** permitted observer joins a running room, gets state snapshots and names/scores but **cannot send gameplay commands**. Spectating live multiplayer stays in initial scope.

**What multiplayer does *not* save:** Do **not** create full permanent logs/snapshots of every remote match for later ghost racing. Persist **authoritative match-result summaries** (participants, final order/score, rule version/seed, timestamp, ranking outcome and useful validation data). Operational telemetry is not user-selectable replay archive. A shareable multiplayer **result link** could exist; full replay of multiplayer games is **[DEFERRED]**. Shareable replay URLs apply to **solo histories**.

## 5.8 Race Your Ghost: full original-history and replay workflow

### 5.8.1 What the ghost actually is [LOCKED]

The ghost is an **immutable exact historical trace of the player's own *solo* run**: same character identity/silhouette with spectral tint, same source seed and version, same original spawn, **same normal historical speed and decisions**, original pickups, charge/Overlit actions, Burst cues and **historical score at each aligned time**. It is **not an autonomous agent, physical adversary, resource thief, damage source or separate multiplayer participant**. Do not relocate its spawn, accelerate it, double damage or add AI behaviors.

There are **two logical worlds**:

- **Historical source projection:** read-only original world containing past inputs, score, movement, pickups and effects, reconstructed from immutable records/trace.
- **Current living world:** new authoritative attempt with its own pickup collection flags, charge, score, actions, cooldowns and mutable state, using **same original scheduled candle opportunities**.

The **ghost sprite does not enter current-world collision/collection/Burst handlers**. It may appear to pass over an active candle, but that candle remains available to the current living player until their own world collects/expires it. Conversely, ghost score changes are driven by **historical trace**, not current pickups.

### 5.8.2 First-time flow: Create History

1. Enter **Race Your Ghost**.
2. If no saved eligible original exists, show one prominent **“Create Your First History”** action, not an empty picker.
3. Run a new full **90-second solo arena session**, living character only, with normal walls/candles/movement/Soulfire/Burst/score and no enemy NPC or rival ghost. Server deterministic simulation records ordered inputs, events and complete checkpoints/verification data as defined.
4. At finish, **automatically persist one new immutable original history** with source owner, seed, rules/layout version, character ID/spawn, input log, snapshots/replay evidence, score and time. **Only show “History saved” when the save actually succeeded**; handle retry safely.
5. Result offers **Challenge This Run**, **Watch Replay**, **Create Another History**, **Back to Menu**, and perhaps **Pin/Share** according to availability.

**History reuse [LOCKED]:** Every **new original** requires a freshly played complete run; an already saved original remains **challengeable indefinitely** without recording a new run.

### 5.8.3 Returning player: select source

- Open **History/Ghost Mode**. Show recent original runs, personal best, pinned entries, optionally branch/attempt history, previews and scores; distinguish **Original History** vs **Branch Attempt**.
- Basic card: source ID/session/date, final score, duration, type and perhaps mascot; advanced/details can expose seed/version/hash. Controls: **Watch**, **Challenge**, **Rewrite From…**, **Share** where permitted, **Pin**.
- Keep originals challengeable across restarts according to disclosed retention/pinning policy; do not silently delete a pinned source or mutate its results.
- Multiple branches and repeat attempts can coexist. If an attempt is itself made reusable as a future source, that must be a clearly modelled UX/storage decision, not automatic accidental recursion.

### 5.8.4 Standard full challenge from tick 0

1. Select a saved original and press **Challenge**.
2. Start a new current solo session from **exact same seed, rule/layout version, original spawn and source starting state**. Use same immutable candle activation schedule; **collection state is per world**.
3. On GO the solid current mascot and spectral ghost **start exactly superimposed**. They diverge only because present inputs differ from past inputs. The engine must not artificially separate them for readability; instead renderer handles ghost opacity/layering.
4. Ghost moves along recorded source path and emits understated historic cues/score at original timestamps. Living player can take any valid route, collect own candles, charge/Burst and score.
5. Compact mobile-readable HUD continuously compares **`PAST YOU`** versus **`YOU NOW`** at the **same elapsed tick**, with a signed `+X AHEAD`/`−X BEHIND`. **Never compare current tick to ghost's final score** during active play.
6. At the same duration, display old vs current **final total**, signed delta, winner/loss/tie semantics, major divergence and graph if useful. **Try Again** restarts a new attempt from zero against the same original; unlimited challenges.

### 5.8.5 Rewind and Branch: the application's signature interaction [LOCKED]

A functional **shared Marrow Timeline scrubber** lets a player inspect any valid historical tick and press **“Rewrite From Here.”** The operation must be actual state reconstruction, not scrubbing a progress bar while simulation stays at the end.

**Exact algorithm:**

1. Select immutable source timeline `S` and valid **fork tick `T`**; render a truthful preview of source `position`, `charge`, `score`, active/collected candle state and salient history effects at `T`.
2. Find an appropriate prior **complete checkpoint** and deterministically replay stored input/events to recover the **entire source world state** at `T`.
3. Create a **new branch ID** with `parentTimelineId=S`, `forkTick=T`, pinned seed/rules/layout version and mutable state initialized to **exact historical state** at `T`. **Do not restart charge at zero, reset score, replace used candles or clear cooldowns**.
4. Render ghost and living mascot colocated at `T`. Historical projection continues to read **original source S** and cannot be changed.
5. From `T+1`, current player supplies **new inputs** and evolves a separate current world. Future candle spawn/expiry schedule is the same seed schedule; **only current collection and actions change**.
6. At every aligned tick, compare current score and **original source score at that tick**. Historical position/action and source score are immutable even when the player diverges.
7. At match expiry, save a **child attempt/branch** with prefix reference, fork tick, post-fork inputs, final score and verification metadata per retention policy. Show old/new histories, score curves and signed improvement; permit another replay/branch as supported.

**Fairness example:** A player rewinds at **00:41** when they already earned 720 points, collected ten candles and have two charge bars. Their branch starts with **those** 720 points, that exact pickup ledger, same remaining/active candles and same cooldown/timer state. The branch changes only future choices. It does **not** provide all previously consumed candles fresh. This makes bank and game branching semantically comparable.

**Demonstration example from :** Original solo history scores **1,420**; at `00:41` player went right and missed a center candle. Challenge it, watch blue ghost. Rewind to `00:38`, choose center route after branch; hypothetical new score **1,578**, result **+158**, ghost source still **1,420**. **These are illustrative numbers**, not measured gameplay.

**Valid edges:** Rewind at **tick 0** = whole new attempt from true initial state. Scrub at **final tick** = inspect allowed but branch disabled/explained because no remaining play time. Opening Timeline mid-active attempt vs post-match is **[OPEN]**; an initial [PROPOSED] approach is to safely pause solo attempt, fork as new child and never mutate the running source.

**Reverse rewind FX:** A short reverse montage/whoosh/tint is welcome; actual simulation still **seeks from checkpoint and runs forward**. Do not build fragile negative-tick physics just for the animation. Ghost mode may shift music/ambient color cooler but must preserve readable active candle/player/world state.

### 5.8.6 Rendering and comparison rules

- Render **solid living player** dominant over a **pale cool-blue/cyan translucent historical mascot** with halo and subtle trails/afterimages. Ghost can be positioned on an underlayer and faded more during overlap **[PROPOSED]**.
- Ghost historical Bursts are **visual-only**; they should never trigger living-world hit events or remove current charge. Historical candle pickups can show effects without changing current pickups.
- Compact header example: **`00:42 / 01:30`**, **`PAST 820`**, **`YOU 940`**, **`+120 AHEAD`**. Time-aligned values, warm current cue versus blue past cue.
- More extensive two-curve **score evolution chart** belongs in result/timeline view rather than cluttering the action screen. At finish show past/current totals, signed delta, possibly divergence event, and Watch/Retry/Branch actions.
- Ghost mode charge/Burst remains meaningful visually even if it has no opponent to hit; **solo strategic role stays [OPEN]** as noted above.

### 5.8.7 What the app stores and exposes [LOCKED vs PROPOSED]

- **Original Create History:** always save an **immutable original** with replayable inputs, versions and verification evidence **[LOCKED]**.
- **Challenge/fork attempts:** proposed to persist as **child attempts** with source reference and fork tick, show Recent Attempts and allow replay/rebranch where supported **[PROPOSED implementation details]**.
- **Pin/retention:** keep favorites/originals available; regular attempts may have expiration/quota **[OPEN]**. Explain any removal/expiry clearly.
- **Replay links:** explicit public or permissioned URL, validate owner/sharing policy, possible revocation/expiry **[OPEN details]**. Private history should never become publicly discoverable by default.
- **Rules compatibility:** a historical run must replay under its **pinned original rules/version** or valid full trace fallback. If incompatible, state replay unavailable rather than silently recomputing under new mechanics.
- **No multiplayer full log persistence in MVP**. Live multiplayer stores match results and rating evidence; solo replay links meet 's shareability story.

## 5.9 Complete screen/navigation inventory — all functional before final art

1. **Start/Menu:** two conspicuous choices, easy guest identity, compact how-to-play and settings (controls, sound, reduced motion); no forced registration.
2. **Multiplayer Lobby:** create/join code/link, 2–4 participant slots, avatar selection, ready state, presence, chat/emote picker, reconnect/leave.
3. **Live Arena:** whole world with short walls and timed candles; timer, scores, charge status; mobile virtual controls; info/exit; active input feedback.
4. **Results:** multiplayer winner/order/distance/pickups/Burst stats and rematch; solo saved history or historical comparison and next action.
5. **Ghost History:** originals, recent branches, pinned/personal best, source type, metadata/details, choose/watch/challenge/rewrite/share.
6. **Ghost Challenge:** living solid mascot versus original spectral ghost; HUD with two **time-aligned** scores, delta, timer and legitimate pickups.
7. **Replay/Timeline:** the actual shared component, play/pause, tick/time seek, optional event markers, branch eligibility, **Rewrite From Here**.
8. **Branch Comparison:** old/new scores and curves, improved/missed decisions, source/child identity, retry or further history actions.
9. **Leaderboard/Profile:** eligible multiplayer rating/standing and summary, optionally separate solo personal best. **Never treat rewindable solo runs as Elo matches**.
10. **Watch/Spectator:** live read-only multiplayer spectator and permissioned solo replay viewer with appropriate access; observers cannot control active games.

All screens require intentional loading/error/empty state and navigation/keyboard/touch affordances. In the greybox stage, ugly sprites are acceptable; **broken buttons, static mock scrubbers, missing menus or unhandled disconnect states are not**.

## 5.10 Game modes, lifecycle, state models and events

**Illustrative mode enumeration [PROPOSED API shape]:**

```text
GameMode = MultiplayerArena | SoloHistory | GhostChallenge | GhostBranch | ReplaySpectator
```

`ReplaySpectator` is a **presentation state**, not a score-generating competitor. Example transitions:

```text
Match / Session:
  Created → Lobby / Prepared → Countdown → Running → Finished → Finalized → Archived

Solo original history:
  Recording → Completed → Verified → Saved → Pinned / Expired

Branch attempt:
  PreparingFork → Restored → Running → Completed → Saved
```

Build legal-transition validation, timer authority and exactly-once/idempotent finalization around these states. A disconnected client resending finish must not award a match twice.

**Illustrative authoritative game state [PROPOSED schema, exact fields adjustable]:**

```text
GameState
  matchId, gameMode, seed, rulesVersion, layoutVersion
  tick, phase, roundLengthTicks
  players[]
    playerId, slot, originalSpawnId
    position, velocity, colliderRadius
    charge [0..4], overlitUntilTick?, pairCollisionCooldowns
    accumulatedScore, authoritativeDistance, pickups, bursts, hits
    connectionStatus, playerEligibility
  candles[] / pickupLedger
    stableCandleId, position, activationTick, expiryTick
    collectedBy?, collectedTick?
  world
    selectedLayout, immutableGeometry, immutableSpawnScheduleId
  domainRngState / independent RNG streams (if used after precomputation)
  scheduledActions / cooldowns / pendingAutoBurst
  finalizeVersion / resultMetadata

HistoricalTrace (READ-ONLY)
  historyId, ownerId, sourceRulesVersion, sourceLayoutVersion
  sourceSeed, sourceCharacterId, originalSpawnId
  tick → originalPosition, originalCharge, pastActionCues, pastScore
  orderedInputLog, fullCheckpoints, stateHashes
```

**Typed versioned `GameRules` object** must own: tick rate, duration, physical radius/speed/accel, candle schedule/lifespan/score, charge cap, Overlit timer, contact/burst damage, cooldowns, Burst radius/wall occlusion, score coefficients, tiebreaks, spawn/layout IDs. Persist exact version/rules hash where needed. **Visual palette/sprite changes should not change simulation rules version.**

**Command examples**: `MoveInput(tick, normalizedVector, sequence)`, `RequestBurst(tick, sequence)`, `JoinLobby`, `Ready`, `StartMatch`, `CreateHistory`, `ReplayAtTick`, `BranchFrom(timelineId,tick)`.

**Domain event examples**: `MatchStarted`, `CandleActivated`, `CandleCollected`, `ChargeChanged`, `OverlitStarted`, `BurstTriggered`, `BurstHitPlayer`, `CollisionChargeLost`, `ScoreChanged`, `MatchFinished`, `HistoryRecorded`, `TimelineBranched`. A render-only particle sparkle is **not** necessarily a journalled domain event; a scoring Burst must be reconstructible from the historical state/input trail.

## 5.11 Ghost Lap project structure, ownership and web rendering

**[PROPOSED module split]**: `GhostLap.Domain` (geometry, movement, collision, deterministic candle schedule, Soulfire, Burst, scoring/rules/state), `GhostLap.Application` (room/lifecycle orchestration, solo replay/history, branch requests, permissions), `GhostLap.Infrastructure` (EF Core histories, compact input log codec, SignalR adapter, ranking/outbox, trace/checkpoints), `GhostLap.Contracts` (public typed DTO/events), `GhostLap.RealtimeHostAdapter` (server-owned 30 Hz session loops and snapshots), and `ghost-lap-web` (React shell/HUD, PixiJS scene, input/prediction/interpolation, asset and audio adapters).

**Authoritative split:**

| Component | Owns | Explicitly must **not** own |
|---|---|---|
| C# Game Domain | Position, physics, walls, pickup, charge, collision/Burst, score, outcome | CSS, images, browser audio |
| Shared Marrow Simulation/Timeline | Deterministic orchestration, ordered snapshots/replay/branch infrastructure, RNG primitives | Game-specific damage/scoring or bank fraud rules |
| Marrow Journal + App B records | Immutable source identity, inputs, snapshot verification, child-parent links | Visual ghost trails and particle random noise |
| Marrow Identity | Guest/profile/permissions and replay ownership | Arena simulation |
| Marrow Realtime + rooms | Hubs, session ownership, presence, snapshots, spectator transport | Trusting arbitrary client scores/positions |
| Marrow Ranking + App B rating | Durable result -> eligible rating/leaderboard | ML or deterministic replay manipulation |
| Marrow Chat | Lobby comments/emotes, permissions/rate limits | Physics timing |
| PixiJS | World presentation, mascots, pickup sprites, ghost and effects | Domain collision, scoring, authoritative item states |
| React/shared UI | Menus, HUD, settings, Timeline scrubber, results and score charts | Inventing history without server state |

**Rendering separation [PROPOSED]:**

```text
Authoritative State + Semantic Domain Events
  → typed GameViewModel adapter
    → PixiJS ArenaScene
      → WorldLayer: floor, geometry
      → PickupsLayer: candles
      → GhostLayer: read-only projection
      → CharactersLayer: living mascots
      → EffectsLayer: Soulfire glow, Burst, particles
      → Overlay/DebugLayer: optional debug hitboxes
    → React: HUD, menus, branch/Timeline, results
    → AudioCueRouter: semantic event → replaceable audio
    → InputMapper: keys/touch → bounded control commands
```

**Do not make a render sprite component compute charge, collision, historical scoring, rewind state or network authority.** The main risk is cosmetic code creeping into shared domain logic. Gameplay must run in tests without loading sprites, CSS, canvas or sound assets.

## 5.12 Placeholder-first presentation plan and final art swaps [LOCKED workflow]

The earliest playable version must have **complete user flows, correct state, meaningful UX and simple placeholders**. Final polish changes art/render adapters, NOT domain outcomes:

| Layer | Functional-first placeholder | Later visual treatment | Stable adapter/contract |
|---|---|---|---|
| Four mascots | Four distinct circles/heads, eyes/labels/colors | Four original expressive illustrated head-only figures, small-size silhouette readability | `CharacterAppearanceId`, render/sprite manifest |
| Charge glow | Four HUD blocks and concentric stage rings | Soft local illumination, increasing glow radius/brightness/particles/wall edge spill | `charge`, stage/effect presets |
| Ghost | Pale blue translucent circle/outline/trail | Spectral treatment **of the player's original selected mascot** | Historical trace renderer only |
| Candles | Simple candle/flame shapes, activation/expiry cue | Refined hand-drawn/animated warm flame candle | `CandleView`, stable candle ID |
| Floor | Muted grid/pattern with contrast | Seamless dark slate/stone/tile texture | `ArenaMaterialId`, world material config |
| Walls | Readable solid rectangles and hitboxes | Textured architectural stone, selective edge light, sparse pumpkin/skull decals | Geometry distinct from wall skin |
| Burst | Expanding radial circle, hit blink | Shockwave, particles, local shake, small bloom, crisp SFX | `BurstTriggered`, `BurstHit` events |
| HUD | Fully functional timer, score, bars/buttons | Refined typography/layout/icons/transitions | Typed view model, tokens |
| Timeline | Real working seek/branch/compare without animation | Event markers, polish, rewind whoosh | **Same shared Timeline component** |
| Audio | Safe synthesized/basic cues, mute/volume | Original/licensed haunted ambience/SFX/soundtrack | `AudioCueId` router |
| Navigation | All buttons, empty/loading/error states functional | Cinematic but restrained transitions | UI state machine/router |

**Asset formats candidates:** transparent PNG/WebP character sprites, PNG spritesheets/atlas metadata, tiled PNG/WebP stone textures, optional SVG/vector decorations, OGG/MP3/WebAudio-safe cues. Use **original or correctly licensed** assets in final public repo; inspiration imagery is not permission to copy. A **central asset manifest** maps stable semantic IDs to paths/animations/effect presets, with **functional placeholder fallback** if a production asset is missing. No gameplay `if (filename === '...')` logic and no dependence on asset path to generate authoritative pickup coordinate.

**Game-feel budget:** glow and trails, pickup particles, impact light/screen shake, brief camera easing **without moving whole-world authoritative camera** (a localized visual shake is okay), clear countdown/last seconds/results, emotional historical ghost, and unique sounds for events. Stage 0 subdued; stages 1–3 grow brighter; **stage 4 instantly recognizable**. Phone performance and readability outweigh expensive physically correct lighting; simple layered radial sprites/blends/bloom suffice. A ghost must not visually overpower active candles, and reduced motion should keep semantic state legible.

**Semantic audio IDs to define early:** UI select/back; optional move/ambient; candle activated, collected and charge increment; Overlit ready/warning; Burst fire/hit; player contact; countdown, final seconds; win/lose; ghost history selection; rewind whoosh; branch start; branch result/improvement. Browser audio starts after user gesture, music/FX have independent controls, concurrent effects are capped, mute and reduced-shake/reduced-motion accessible.

**UX before final art:** A plain circle can be replaced. A clipped HUD, dead rematch button, broken focus, unreadable score, missing mobile touch, false “Saved” indicator, untested reconnect or fake branch button cannot be excused as “art later.”

## 5.13 Server networking, session ownership and anti-cheat

**Tick authority [TARGET 30 Hz]:** Each room has **exactly one authoritative session owner** and tick loop. Clients transmit validated, bounded, sequence-tagged input commands associated with ticks; **not** arbitrary authoritative positions/score/pickups or `hit:true` claims. Server resolves validated inputs, world schedules, collision/charge/Burst, score and final placement. Clients predict only local movement, reconcile and interpolate remote snapshots; spectator clients receive data but cannot act.

**Safety measures [PLANNED]:** normalize and clamp input; rate-limit move/Burst/chat/emotes; reject stale/duplicated sequence numbers as designed; prevent teleport/scoring exploit; handle jitter gracefully; meaningful reconnect grace and disconnected-player state; ensure a single server tick owner even if Realtime host scales out. An authoritative Redis backplane is **not automatically authoritative session ownership**; match affinity/distributed ownership requires design.

**Results and rating:** One durable finalized match result yields one idempotently published `MatchFinished` event/outbox message. The appropriate rating worker computes new standings **exactly once in business effect** even with retries/duplicates, publishing to Marrow Ranking (Redis sorted set with durable Postgres snapshots as applicable). A reconnect cannot award rank twice.

**Elo is currently a category label, not a defined 4P algorithm.** For 2–4-player match ranking, choose and evaluate a specific transparent multiplayer Elo-like formula (e.g. pairwise expectation and explicit K factor) or another valid rating system **[OPEN]**; don't apply ordinary 1v1 Elo blindly to 4P. Only eligible **live** competitive matches contribute; solo branches/challenges do **not**. Rank eligibility for guests, abuse, disconnections and forfeits remain undecided.

## 5.14 Durable storage, replay codec and privacy

**Conceptual records (storage shape not committed):**

- `PlayerProfile` / guest identity.
- `SoloHistory`: original ID, owner, seed, rules/layout versions, original spawn/character, creation/completion time, final score, verification hash, log/checkpoint reference, pinned/visibility.
- `SoloInputLog`: compact **tick-indexed** movement/buttons, possibly run-length/delta encoded.
- `SoloSnapshot`: complete authoritative state at a checkpoint tick and verification hash.
- `BranchAttempt`: source or parent ID, fork tick, inherited prefix, post-fork inputs, final score, pin/share status, validation.
- `MatchResult`: multiplayer participant roster, final standings/scores/stats, seed/rules version, rating update status; **no permanent full MP replay requirement**.
- `ReplayShare`: owner/visibility/token/URL, authorization rules, potential expiry/revocation.

**Storage strategy [PROPOSED]:** Begin with **PostgreSQL** for metadata/histories and compact logs/checkpoints at modest volume. Consider compression/binary structures, retention policy and maybe later S3/object storage only when necessary; do not add storage infrastructure just to appear enterprise-grade.

**Reliability:** originals are logically append-only; invalidations/corrections recorded explicitly, not silent edit. Access to input history is owner-validated, share links are intentional and restricted appropriately. Failed save shows failure/retry, never false success; idempotent save avoids duplicate originals after refresh. Old histories must use pinned rule version/valid trace; if not possible, mark incompatibility transparently. Store only game-significant state/inputs/events: **not** raw canvas frames, cosmetic particles, shader noise, or full videos.

## 5.15 Deterministic replay and snapshot rigor (game-specific)

**A snapshot must hold full relevant authoritative state:** player position/velocity, score, charge, Overlit deadline, relevant pair-contact cooldowns, selected world layout, active/upcoming candle schedule identity, current-world collection/expiry ledger, pending auto-Burst, tick, domain RNG state when necessary and rule/version identity. **A position-only snapshot is invalid for meaningful branching.**

**Checkpoint target [TARGET]:** full snapshots at approximately **1–5-second** intervals plus ordered input log/necessary events. 90 seconds × 30 Hz = **2,700 ticks**, so compact logs should be plausible, but exact storage/performance is **unmeasured**. Use state hashes to verify authoritative replay versus recorded source and detect version drift.

**Historical trace:** source ghost can be projected efficiently from input replay/checkpoints and/or verified recorded state samples, but every displayed action/score must match the immutable source. A score history cannot be fabricated by interpolating sprite x/y positions. A separate logical ghost world is read-only and unaffected by the new current world's item state.

**Exact assertions:**

```text
Replay(seed, rulesVersion, orderedInputs).FinalHash == RecordedLiveRun.FinalHash
Restore(source,T).StateHash == OriginalStateAtTick(T).StateHash
For all t ≤ T: Branch(t) == Source(t)
For t > T: Source(t) unchanged no matter what BranchInput does
Collect(CurrentWorld, candleId) does NOT mutate GhostHistory
0 ≤ charge ≤ 4
One candleId can be collected at most once PER current world
Distinct future branches from one prefix have independent state
One MatchResult can update rating effect at most once
```

Property-based testing should generate legal/illegal event sequences, input timing and random seeds; example failures (if any) must be captured from **actual** test execution, not fabricated.

## 5.16 Game-specific edge-case inventory (30 explicit edge cases)

1. **No saved originals:** show Create History rather than a dead empty list.
2. **Existing originals:** selectable/challengeable repeatedly; never require re-recording per attempt.
3. **Missing/expired source:** explicit unavailable explanation; never select some unrelated seed silently.
4. **Version mismatch:** pinned old logic/valid stored trace or honest incompatibility, never silently replay with new rules.
5. **Rewind tick 0:** proper initial state, score zero, no historical current-world pickups.
6. **Rewind final tick:** inspect allowed; branch disabled/explained if no future duration remains.
7. **Rewind during active solo:** explicit safe policy [PROPOSED pause and branch], no mutation of source/running attempt.
8. **Full ghost/live overlap:** living player stays visible; ghost underlay/opacities respond.
9. **Ghost crosses a candle:** current-world candle remains collectible while valid; ghost score still updates from source.
10. **Current collects source-ignored candle:** only current world changes, ghost original unchanged.
11. **Two live players same candle on same tick:** stable tie-break, one owner, no double award.
12. **Same-tick Burst and contact:** formal deterministic resolution order.
13. **Overlit auto-expiry with simultaneous key press:** exactly one Burst, no duplicate charge consumption.
14. **Burst request without charge:** no domain effect; restrained UI response optional.
15. **Penalty below zero:** clamp at zero; no accidental negative score.
16. **Overlapping players indefinitely:** pair cooldown, no unbounded repeated charge drain.
17. **Someone leaves lobby:** slot vacated; countdown cancels/continues per explicit room policy.
18. **Mid-match disconnect:** grace/rejoin/forfeit handling and ranking policy explicit; no invisible score edits.
19. **Spectator sends control command:** reject at server, never affect outcome.
20. **Forged client location/speed/score/pickup:** reject/ignore; authoritative state remains source of truth.
21. **Mobile portrait:** explain/request landscape or provide supported fallback; do not crop controls/world.
22. **Small viewport:** timer, score, touch targets readable; simulation geometry remains unwarped.
23. **Slow GPU/phone:** reduce particles/postprocessing without affecting physics.
24. **Network spikes:** reconcile/smooth; never derive scoring from drawn interpolation.
25. **Reduced motion/mute:** warning and gameplay cues stay accessible visually when shake/audio disabled.
26. **Browser refresh after solo finish:** persisted original remains and duplicate save request does not create conflicting originals.
27. **Public replay link logged out:** respect public/permissioned visibility; no private-history leak.
28. **Many branches from same source tick:** siblings coexist, source immutable, comparison identifies source correctly.
29. **Solo Burst without rival:** remains functional but may lack strategic role; documented test/design question.
30. **Match ending on pickup/Burst tick:** update order determines whether event counts and must replay identically.

---


# 6. Timeline — shared replay, branch and comparison contract

## 6.1 Six non-negotiable pieces of actual shared implementation

1. **Ordered historical identity/recording:** Timeline/source IDs and an append-only journal or ordered input/event history abstraction.
2. **Versioned reconstruction:** restore a domain state at an ordered position using actual checkpoints and ordered records with deterministic interpretation.
3. **Fork/alternate continuation:** create a new branch from an exact original state, with domain-appropriate override inputs/policy.
4. **Historical immutability:** source record/state never mutated by the branch; branches are separately identifiable, with parent/fork metadata.
5. **Outcome comparison:** original and branch domain results are independently computed and compared on aligned time/event dimensions.
6. **One React Timeline interaction component:** actual shared scrub/seek/branch/compare interface with domain-specific visualization, labels and time conversions.

**Illustrative API sketch only, not a committed interface:**

```csharp
public interface ITimelineEngine<TState, TInput, TOverrides>
{
    Task<TState> RestoreAsync(TimelineId id, long position);
    Task<BranchId> BranchAsync(
        TimelineId source, long position, TOverrides overrides);
    Task<ReplayResult<TState>> ReplayAsync(TimelineId id);
}
```

The real design must decide types, storage, domain-specific codec, streaming/cancellation, checkpoint strategy and compare results. **Do not blindly bind the shared API to “30 Hz GameTick”**: Ghost Lap positions are discrete game ticks, Revenant positions are financial event sequence/business timestamps. Common Timeline orchestration is generic; both domains provide a deterministic transition/adapter.

## 6.2 Replay proof and fraud/ML determinism

**General principle:** Given original initial state, pinned domain/rules version, ordered inputs/events and external/model assumptions, replay computes exactly the same authoritative result. A branch matches the original for **all positions through its fork** and can differ only after fork. A source history cannot change when a new branch runs. Tests and verifiable hashes are evidence, not mere diagram labels.

**Game determinism complications:** PRNG state and spawn schedule, input ordering, cooldown and timer state, collision tie-breaks, all completed/collected pickup flags, same-tick resolution and exact fixed-step arithmetic. Independent source and current worlds preserve identical future *opportunities* but can have different pickup consumption.

**Bank determinism complications:** idempotent payment/ledger events, business clock, stable policy/model versions, model features/outputs, transaction/legal posting, late external data and differences between “fraud labelled in a synthetic scenario” versus actual future financial truth. **Financial projections should be isolated from live ledger write paths**. Claims of deterministic alternate ML outcomes require version pins/recorded outputs or verified deterministic inference.

## 6.3 One Timeline is real even when presentation differs

The shared scrubber can carry selected position, play/pause, branch action, markers, comparisons and source/branch IDs. Revenant paints time-series money/decision curves plus metrics and policy selector; Ghost Lap paints a whole arena historical trace plus score curves and “Rewrite From Here.” Do not force a bank to share the game's PixiJS HUD or a game to share the bank's accounting data table. **Reuse infrastructure and interaction contract; customize the domain visualization.**

**Visual reveal:** Place the bank's original-vs-policy-v8 ghost-blue curve beside a game mascot's ghost-blue prior route/score. Move the **same scrubber** in both apps, show the same Timeline restore/branch implementation and two different adapters. This is what converts “same codebase” from a marketing claim to a tangible product contribution.


# 7. Build sequence, working protocol and scope priorities

## 7.1 Project working method [PLANNED]

**Functional vertical slices before decorative breadth.** Marrow is validated against both applications from the beginning rather than built as a huge unused framework. The first integrated milestones are a visible seeded **Friday Ring** flow and a **playable Ghost Lap greybox** with actual input, movement, candle, charge, Burst and results. Stable contracts separate game rules from render assets and separate actual bank posting from demo visualization. Initial stubs/fakes are development scaffolds only, identified accurately and replaced before corresponding functionality is described as working.

**Implementation/validation cadence:** bounded requirements/design/acceptance tests → implement one coherent feature through domain/API/UI → test determinism, persistence, error and replay boundaries → integrate both app compositions and host behavior → commit working code and real evidence → improve visuals only behind stable adapters. Important decisions and actual deviations have concise ADRs, not multiple competing project-context Markdown copies. Production results/metrics and Kiro actions enter evidence records only after observed.

**Revenant discipline:** explicit synthetic scenario seed and known labeled truth; correctly balanced real postings; safe separate counterfactual projection; pinned policy/model/feature assumptions; causal payment→hold/settlement→liquidity update; model evaluation and explanations consistent with implemented APIs. A UI green check is not substitute for a verifiable invariant.

**Ghost Lap discipline:** complete navigation, lobby and solo history flows with replaceable placeholder visuals **before** detailed mascots/stone art; server-owned 30 Hz target simulation; history checkpoint/hash verification; independent ghost/current worlds; actual mobile input; end-to-end errors/reconnect/privacy. A sprite renderer cannot invent score, replay, collision or charge. Final polish may change textures/effects/audio but not simulation results.

**Shared protocol:** code belongs in Marrow only for genuinely common functionality. App-specific state transitions remain in their own modules. Architectural tests block cross-domain internals imports and rendering dependencies in domain code. The same Timeline service, journaling/snapshot conventions and shared scrubber must exercise **two real domain adapters**. Never count duplication as reuse.

## 7.2 Five implementation phases (dependency order, not an estimated schedule)

1. **Foundation + two visible journeys:** repo/license/root `.kiro`, minimal modular monolith, Api/Realtime/Worker compositions, contracts, basic guest/staff identities, database/Redis, early AWS/TLS/Compose + local fallback, boundary/CI tests, seeded bank scenario mock-to-real flow and playable arena greybox.
2. **Actual deterministic kernels and shared Timeline:** Journal, versioned RNG/schedules, complete snapshots, restore/replay/fork/compare, common React scrubber. Real ledger correctness and isolated policy projection; complete original-solo recording, replay hashes and any-valid-tick ghost fork.
3. **Distinct complete applications:** Revenant's payment lifecycle/held workflow/fraud models/liquidity/audit and five screens; Ghost Lap's live 2–4 player rules, real rooms/chat/emotes, spectators, eligible rating, result loop, durable solo histories, share and history UX.
4. **Adversarial tests and polish:** FsCheck, real Postgres/Redis integration, architecture/E2E/load tests, game fairness/exploit/playability, settlement concurrency, mobile/touch and reconnect; then replace placeholders with original/licensed art, audio and refined analytics. Stable deployment and browser/device smoke tests.
5. **Honest demo/submission:** reproduce Friday Ring, multiplayer/solo ghost/branch, shared-code reveal, meaningful actual Kiro evidence; edit ~2:50 video, verify license/repo/root `.kiro`, deployment/judge accounts and accessible links. Capture functional clips at milestones, freeze new optional features before submission.

## 7.3 Priority and explicitly excluded work

| Tier | Revenant | Ghost Lap | Shared |
|---|---|---|---|
| **Signature / non-negotiable** | Synthetic seeded Friday Ring; payment/hold with causal flow; real ledger rules; actual v7/v8 sandboxed historical counterfactual and numerical ghost curve | 2–4-person working authoritative arena with core mechanics; original saved solo history, exact inert ghost, repeated challenge, complete-state any-valid-tick branch and aligned score comparison | One reusable deterministic Timeline and true distinct apps; public functioning access, credible Kiro, repo and video |
| **Full initial experience** | Liquidity warning/fan, explanation, case actions/four-eyes where implemented, audit/ML paths, five connected screens | Guest/lobby links, chat/emotes, spectators, results/rematch, ranking, mobile controls, real persistence, permitted solo replay links, finished audio/art | CI and invariant proof, honest access and product UX; fully working settings/empty/errors |
| **Possible enhancements** | Richer fraud workflows, deeper full RLS demonstration, improved forecasting/explanations | More curated layouts, cosmetic diversity, richer controls on sharing/spectating | Independent Realtime host/extraction if genuine; verified reuse chart; third tiny sample/scaffold |
| **Outside initial release** | Real-money bank service; unsupported production/security/ML guarantees | Full multiplayer archival and ghost forks, tracks/cars/laps, item bombs/oil slicks, AI/damaging ghosts, elaborate meta-progression | Ten-service microservices fleet, mandatory ECS/Fargate/RDS, unreliable third-app showpiece |

**Priority conflict:** a complete game includes live spectators, social and rating as specified; if an actual deadline forces reduction, that becomes an explicit changed scope, not an invisible reinterpretation of the product. Timeline authenticity and two accessible usable apps outweigh unmeasured reuse percentages or optional infrastructure.

# 8. Validation and completion standard

## 8.1 Test plan and evidence

| Test kind | Revenant | Ghost Lap | Shared checks |
|---|---|---|---|
| **Unit / rule** | Legal payment lifecycle, currency rounding, posting group balance, policy result, audit chain | Normalized diagonal speed, wall slides, collectible timing, charge cap, contact cooldown, Overlit expiry, Burst target effects, score | Domain state evolves independent of frontend and wall-clock/cosmetic state |
| **FsCheck property-based** | Debits = credits; conservation, repeated idempotent requests don't multiply effects; ordered replay equals live state | 0≤charge≤4; unique candle collection **per world**; identical replay hash; branch prefix equality; source immutability; no double Burst; score respects legal gains/losses | Determinism/version pins, real reduced counterexamples only if observed |
| **Postgres/Redis Testcontainers** | Deferrable DB constraints, transaction isolation, outbox retries, concurrent settlement, staff authorization | Durable originals/checkpoints/branches, guest ownership, share access, lobby, match finalization and idempotent rating | Public contract/persistence boundaries, retry-safe events |
| **NetArchTest** | Finance module owns ledger/policy/ML and is not imported by game | Game/domain never depends on sprites/PixiJS, no Revenant domain dependency | Shared modules only through public contracts; architecture fails CI on violation |
| **Playwright E2E** | Full Friday Ring, held-case decision, rewind v7/v8, outcome/audit | Guest first source, page reload/persist, challenge, seek/branch/results, actual 2/3/4 P room, spectator read-only, disconnect/reconnect, mobile | Both URLs usable with truthful instructions and intact shared scrubber |
| **k6/browser and human testing** | Measured concurrency, replay latency, settlement worker behavior and scenario clarity | Measured 30 Hz loop health, room load, network jitter/snapshot size, mobile FPS, camping/spawn fairness, 5-second comprehension | Publish actual results only; never invented numbers or fabricated Kiro bugs |

**Hard Timeline invariants:** `Replay(initial, versions, orderedInputs).FinalHash == Original.FinalHash`; `Restore(source,T).Hash == OriginalAt(T).Hash`; branch prefix through `T` equals source; branch inputs after `T` do not mutate source; game ghost interactions never modify current-world candle/charge; financial branch never posts to actual ledger; result/rating and history-save side effects occur once despite retries.

## 8.2 Six blocking Ghost Lap gates

1. **Playable greybox:** one fair arena, working controls, timed candles, Soulfire/contact/Burst, score, match end and visible feedback.
2. **Real history engine:** complete solo original, ordered log/checkpoints, verified deterministic replay, exact seek/fork at any valid tick, unchanged source.
3. **True ghost challenge:** identical seed/layout/spawn, overlapping tick-0 start, independent worlds, time-aligned HUD and repeatable branch/result.
4. **Authoritative multiplayer:** real 2–4 clients, room/invite, server-owned hits/score, spectators unable to issue game controls, reliable finish/rejoin paths.
5. **Actual shared Marrow:** same backend restore/fork core and web scrubber used in financial policy replay and game, module tests and Kiro files committed.
6. **Finished user product:** all functional states, privacy/share/retention rules, art/audio/performance, public deployment, license, write-up and actual video.

## 8.3 Completion meaning

**A completed project** allows judges to use **both real distinct applications**, not static previews; demonstrates true non-destructive Timeline fork/compare; contains functioning financial causality and validated accounting invariants; offers a complete connected 2–4P arena and solo replay/branch journey with persistence and permission checks; works at supported browser/mobile sizes with a polished usable interface; ships source/license/root `.kiro` and truthful Kiro evidence; and has a repeatable under-three-minute demo. Claims are restricted to actual implemented and tested behavior.

# 9. Kiro use and development evidence [PLANNED, NOT CLAIMED AS DONE]

**Purpose:** Kiro supports the actual engineering difficulty: making **one reusable, correct Timeline** work in a bank and an arena without cross-domain leakage, while keeping a modular template lean and preserving requirements as features evolve. Real use is required by the hackathon; **specific Kiro tool choices and automation outputs are not documented as executed**.

| Kiro component | Planned role and concrete target evidence | Status |
|---|---|---|
| **Specs / spec-driven work** | Requirements → design → bounded tasks/acceptance checks for Marrow/kernel/Timeline and each domain's counterfactual or solo history/live game; an actual design decision that affects contract/code/tests | [PLANNED] |
| **Steering** | Persistent module contracts, determinism rules (no implicit wall-clock or nonseeded RNG in replay), tests, data/privacy/ML honesty, isolated bank projection, rendering-independent gameplay and source immutability | [PLANNED] |
| **Hooks** | Meaningful executed checks such as architecture tests when module contracts change, re-generate OpenAPI TypeScript clients when API contracts change, rerun replay/properties for journal/snapshot edits, precommit verification, possibly ADR hygiene | [PROPOSED] |
| **Custom agents** | Themed possible roles: **Architect** (boundary-safe module scaffold), **Gravedigger** (adversarial properties/invariants), **Scribe** (ADR/decision capture), **Medium** (Playwright demo/UI flows) | [PROPOSED; do not imply they exist] |
| **MCP** | Targeted real integration if useful: PostgreSQL introspection, GitHub, browser/Playwright, framework docs; optional **`marrow-scaffold`** generating a bounded module as a stretch proof, not a required third app | [PROPOSED] |
| **FsCheck / PBT** | Finance balancing/idempotency/replay + game charge/pickup/branch constraints; actual minimized counterexamples/defect fixes if they occur | [PLANNED] |
| **Kiro IDE/CLI/Web/Crew/powers** | Use modes or bundled expertise where actually available and helpful; write-up may contrast approaches only if tried | [OPEN] |

**Evidence protocol:** root **`.kiro/`** remains in repo from real use, with actual specs/steering/hooks/agents/configuration as applicable. **`docs/kiro-log.md`** holds compact dated entries: engineering problem, Kiro surface, decision/output accepted or rejected, code/test/commit linkage, measured result and limitation. Real screenshots, hook runs, test logs, ADRs and representative differences are retained as they happen. `docs/adr/` records genuine architecture changes, not several conflicting full-project specifications. A tested correction is more valuable than a long tool inventory. No retroactive claims that a hook caught a bug, PBT shrank a case or an agent generated an app without evidence.

**Video placement:** ~25 seconds of high-signal actual Kiro work after the two app journeys and reuse reveal. The substance is **how Kiro affected correctness, modularity and reuse** rather than a list of tool names. Kiro tools are **engineering-time integrations**, not necessarily embedded end-user features of Revenant or Ghost Lap.

# 10. Unresolved decisions, adjustable targets and principal risks

**Unresolved does not mean specified by implication.** Parameters identified below remain [OPEN], with numeric examples already marked [TARGET] or [PROPOSED]. The current app design is fixed at the level of genre, two products, core mechanics, domain boundaries and exact branch semantics.

## 10.1 Shared, hackathon and infrastructure [OPEN]

- Official Kiroween 2026 deadline, final eligibility/bonus terms, solo/team status, student category evidence and judging-period access duration.
- Marrow/Revenant/Ghost Lap branding (including Ghost Loop option), URL/subdomain details, deployment/test access, product logos/copy.
- Exact folder/project paths and interface method signatures; per-module in-memory/Redis/Postgres split; typed client/versioning and final hand-rolled mediator details.
- Final Timeline type/codec/checkpoint cadence per domain; branch metadata/retention, time/event ordering and app-specific adapter/compare details.
- Optional Aspire, AWS managed services, separate Realtime host and infrastructure-as-code degree; match/session ownership if horizontally scaled.
- Which Kiro hooks/MCPs/agents/powers are actually useful and available; actual logged outcomes; how to measure code reuse, if claimed; third-app scaffold only if genuine and dependable.

## 10.2 Revenant [OPEN unless explicitly locked above]

1. **Friday Ring design:** reproducible six-bank network/account graph, mule payment event sequence/labels, source data mix, reset and actual numerical effects.
2. **Policy v7 vs v8:** exact thresholds/rules/model or approval behavior, pinned artifacts, false-positive/legitimate-delay/fraud-prevented/net-effect mathematical definitions.
3. **ML.NET:** PaySim preprocessing/features/leakage protections, train/test splits/evaluation metrics, chosen classifier/anomaly/SSA/explanations, model versioning and which of four paths fully ship; local versus global explanation validity.
4. **Finance correctness:** precise legal payment transitions/return handling, settlement posting groups, rounding/cross-currency conversion, transaction isolation/retries and idempotency conflict policy.
5. **Security/cases:** fuzzy sanctions thresholds, case assignment and concurrent changes, role/MFA/four-eyes release conditions, scope of tenant RLS, demo identities/permissions.
6. **Rewind/liquidity:** business-time seek semantics, checkpoint strategy, Worker safe replay and true 100× feasibility; forecast features/uncertainty/warning causality, visual graphs and audit export.
7. **Measured data:** real replay latency, transaction throughput and error/performance characteristics. None is established by planning text alone.

## 10.3 Ghost Lap — 15 open/tunable groups

1. **Movement:** speed, acceleration/deceleration, interpolation/reconciliation and collider radius.
2. **Arena:** actual geometry/wall density, 2/3/4P spawn fairness and reachable candle placements.
3. **Candle schedule:** exact count/waves/expiry, warning timing, charge pacing and region distribution.
4. **Score formula:** movement coefficient, candle reward, optional hit reward and anti-corner camping.
5. **Burst walls:** occlusion/line-of-sight, edge behavior.
6. **Burst balance:** radius, target charge loss/score penalty, possible attacker reward, same-tick ordering.
7. **Solo Burst:** no opponent to hit; any shared deterministic noncombat purpose requires a separate explicit rule decision; no automatically added AI/bombs.
8. **Ratings:** multiplayer Elo-like 2–4P formula, guest eligibility, abuse prevention, disconnects/forfeits.
9. **Rooms/results:** tie treatment, readiness/countdown, lobby host change, late join, rematch and spectator access.
10. **Persistence/sharing:** retention/pin quota, original availability, history privacy/share/revoke/expiry, sibling branches and whether attempts can become sources.
11. **Timeline UX:** seek/pause/branch during active solo compared with postgame; invalid/final-tick handling.
12. **Final four mascots:** look, silhouette/identification, colors, ghost tint/opacity/overlap.
13. **Art/audio:** final stone/wall/candle assets, decorations, lighting, event sound/music licensing, transitions.
14. **Real performance budgets:** mobile FPS, 30 Hz tick health, snapshot payload, networking latency and replay storage.
15. **Name and marketing:** Ghost Lap versus Ghost Loop/other final name.

**Additional unsettled interaction details:** candle collected at full charge scoring/timer behavior; Overlit cancellation/rearm on collision; precise tie-breaking and Burst/event ordering; spectator privacy; historical replay compatibility fallback. Described as proposed logic, not finalized values.

## 10.4 Main delivery risks and established containment strategies

- **Two-app scope:** cross-app vertical slices and one completed arena/five focused finance screens; optional infrastructure/custom tooling/extra maps deferred.
- **False skeleton reuse:** one Timeline source, two adapters, shared web interaction and CI architectural boundaries; no copied engine internals.
- **Weak counterfactual:** fixed Friday Ring, real v7/v8 difference, causal accounts/holds/liquidity, computed business metrics and immutable original.
- **Ledger/concurrency/ML risk:** DB triggers, idempotent outbox, tests with real Postgres, saved model artifacts/evaluations and explicit synthetic labels.
- **Ghost fidelity failure:** full checkpoints and ordered inputs, deterministic hashes/version pins, read-only past/current-world separation and no non-deterministic item schedule shift.
- **Multiplayer/network weakness:** real server authority, single match loop owner, predictable reconnect/forfeit behavior; saved solo mode works without a remote rival.
- **Art-first distraction/gameplay abuse:** complete placeholder experience, human playtest of mobility/camping/spawns/Burst; stable render/asset layer and licensed art.
- **Submission/credibility:** early live hosting, working fallback, clips from functioning runs, verifiable Kiro use, only measured performance/reuse, no secrets or unlicensed media.

# 11. Final submission deliverables and validation

- **Public functional URLs** for both apps (or clear paths/subdomains), free access throughout required judging, staff demo credentials/instructions where necessary, guest-friendly game, no paywall.
- **Public GitHub repository**, all build/run source and assets, functioning `docker compose up` fallback, **open-source license visible in repository header/About**, no secrets, properly licensed game media.
- **Actual root `.kiro/`** tracked by Git, with meaningful genuine development artifacts and concise evidence-based **how Kiro was used** write-up.
- **Accurate text feature description**, no claims of live banking/production ML/real performance, no listed capabilities absent in deployed apps.
- **Under-three-minute working demo video**, target ~2:50, covering Friday Ring, Ghost Lap multiplayer + solo historical fork, shared Timeline/code, and actual Kiro contributions.
- **End-to-end judge smoke check:** reproducible fixed-seed bank scenario, correct policy comparison; create/reload solo original, challenge/rewrite/share; real 2–4 player match, spectator read-only, result/rating; responsive desktop/mobile, error/loading states, URLs/account access and deployment availability.
- **Optional qualified bonus**: eligible public Kiro blog within the required period and specified hackathon mention. Official date/terms remain unverified here.

**Final project identity:** Marrow is the reusable engine. Revenant is the synthetic financial decision/replay app. Ghost Lap is the 2–4-player haunted Soulfire arena with immutable solo histories and exact forks. Their shared Timeline is the architectural and demonstration center; everything else is implementation detail supporting two complete, honest, distinct products.
