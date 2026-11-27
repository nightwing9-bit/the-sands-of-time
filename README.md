# The Sands of Time

### One past. Two futures. Nothing erased.

**What if you could return to the moment a decision was made, choose differently, and see what changed—without rewriting what really happened?**

**The Sands of Time** makes that idea tangible through **Marrow**, a reusable deterministic history engine built in C#/.NET, and two complete applications that use it for entirely different purposes:

| | **Ghost Lap** | **Revenant** |
|---|---|---|
| **The question** | *What if I had played that moment differently?* | *What if the bank had made a different decision?* |
| **The experience** | A haunted arcade game where you rewind your run and race your own past. | A six-bank financial operations interface where you examine payments and compare policy outcomes. |
| **The alternate future** | Your new run plays beside a non-interacting ghost of the original. | An alternative policy produces a separate financial history and side-by-side results. |
| **Explore the source** | [Ghost Lap](web/apps/ghost-lap/) | [Revenant](web/apps/revenant/) |

**One shared foundation. Two independent applications.** Built for **Kiroween 2026 — Skeleton Crew**.

---

## The idea in 30 seconds

Every system has a history. Most systems can show you what happened; very few let you **return to a precise past state, try something different, and keep both outcomes**.

Marrow provides a common sequence:

```text
                     ONE ORIGINAL HISTORY
                              │
                     Record and verify
                              │
                      Choose a moment
                              │
                    Restore exact state
                              │
                      ┌───────┴───────┐
                      │               │
                ORIGINAL          NEW BRANCH
                unchanged         new decisions
                      │               │
                      └───────┬───────┘
                              │
                       Compare futures
```

In **Ghost Lap**, that moment is a game tick. In **Revenant**, it is a point in an ordered financial event history. Each application owns its rules; **Marrow owns the shared history mechanics**.

## Experience the applications

### 01 / Ghost Lap — every run leaves a ghost behind

**A real game about competing with the person you used to be.**

Ghost Lap is a 90-second, top-down arena game with four expressive geometric characters, glowing candles, Soulfire charge, Bursts, reactive effects, music, and a world that shifts from soft, snowy daylight to supernatural midnight blue.

Its defining feature is not a conventional replay. **You can go back into your own recorded run and play a different continuation.**

**The player journey**

1. **Play an original.** Choose a character, collect candles, manage Soulfire and finish a server-scored solo run.
2. **Keep the history.** The completed original is verified and saved to *My Histories*.
3. **Return to the moment.** Scrub the hourglass Timeline to any valid tick; inspect the original state, score and movement.
4. **Fork the run.** Begin again from that exact historical position, with the original's state restored. Your cyan ghost continues along its old path without affecting your new world.
5. **See the difference.** Compare scores at the same tick, finish the new future and keep both versions. The original is never overwritten.

**There's more than solo play:** real, server-authoritative **2–4-player multiplayer** includes private room creation/joining, live competition, spectators, results and rematches. The game server—not the browser—owns the authoritative rules and scores.

> **The signature moment:** You choose *Fork here* at a mistake, the arena turns dark, and you encounter the exact version of yourself who made the original choice.

**Implementation:** [React + PixiJS game](web/apps/ghost-lap/) · [C# game domain](src/GhostLap/) · [Contracts](src/GhostLap.Contracts/)

### 02 / Revenant — see the cost of a different decision

**A financial control room for exploring the consequences of fraud policy.**

Imagine a payment passing through a network of six banks. A stricter fraud policy may stop suspicious activity—but it may also hold legitimate payments and change liquidity. The difficult question is not simply *which policy catches more fraud?* It is **what happens to the entire network when the decision changes?**

Revenant lets you follow that question through five connected screens:

| Screen | What it reveals |
|---|---|
| **Control Tower** | Six connected banks, routed payments, positions, suspicious flows and priority alerts, on a live-looking city network map. |
| **Rewind** | The original financial history, a chosen fork moment, an alternate policy test and a visual comparison of the two outcomes. |
| **Held Queue** | Flagged payments, risk evidence, transaction paths and human review/approval decisions. |
| **Liquidity Fan** | Bank availability, a clearly bounded forecast baseline and liquidity-floor pressure. |
| **Audit** | Event records, original-versus-scenario provenance and integrity information. |

**The signature moment:** Select a moment in Rewind, press **Test New Policy**, watch the model stages, then see one history separate into **the original and an alternate future**. The comparison surfaces net financial effect, fraud prevented, false-positive cost and legitimate payments delayed.

The polished **browser demo** is a deterministic *prepared scenario*. Its amounts, animations and policy-test sequence are illustrative—not live bank transactions or a live call to the trained models. This allows anyone to explore the complete visual story without provisioning infrastructure.

**The implemented backend is separate and real:** PostgreSQL double-entry accounting, holds and multi-person approval controls, pinned **ML.NET FastTree** fraud scoring, advisory **SSA** anomaly detection, audit/provenance, and an isolated replay process for v7-to-v8 policy counterfactuals. Backend comparison logic calculates outcomes from synthetic inputs without modifying the original ledger. The liquidity projection uses a limited persistence baseline, **not** a trained forecasting model.

All banks, accounts, payments, and financial outcomes are **synthetic**. No real money or customer banking data is used.

**Implementation:** [React interface](web/apps/revenant/) · [C# finance domain](src/Revenant/) · [Contracts](src/Revenant.Contracts/)

---

## Try Revenant now — no backend required

This is the fastest way to understand the product. You only need **Node.js 22.18+ or 24.x**, npm and Git.

```bash
git clone https://github.com/nightwing9-bit/the-sands-of-time.git
cd the-sands-of-time/web
npm ci
npm run dev:demo -w @sands/revenant
```

Open **http://127.0.0.1:5173/?view=r8&demo=1**. The demo sign-in identity is prefilled; click **Sign in**. No external authentication, PostgreSQL, Docker or .NET service is needed in this mode.

**A suggested two-minute walkthrough:**

1. Start at **Control Tower**. Follow the suspicious payment route and inspect the six-bank overview.
2. Open **Held Queue** to see the decision and supporting indicators behind a large held payment.
3. Go to **Rewind**, select a time in the hourglass Timeline and choose **Fork here**.
4. Run **Test New Policy** and watch the original and alternate histories separate.
5. Read the four outcome metrics, then inspect **Liquidity Fan** and **Audit**.

To play Ghost Lap's real solo and multiplayer modes, use the full-stack instructions below.

## One engine, two different kinds of history

Marrow is not a shared theme, a duplicated library or just a Timeline component. **Both C# application domains depend on the same Marrow foundation** for recording, restoring and branching deterministic histories.

| Shared capability | Ghost Lap | Revenant |
|---|---|---|
| **Ordered history** | Fixed simulation ticks, including game inputs and state transitions | Ordered financial events and external payment facts |
| **Restore** | Reconstruct player position, charge, score and simulation state at a tick | Reconstruct policy-relevant financial state at a chosen boundary |
| **Fork** | Continue with new player inputs while the original ghost keeps its path | Re-evaluate synthetic inputs with a different screening policy |
| **Compare** | Original-versus-fork score at the same tick | Original-versus-alternate financial and operational outcomes |
| **Preserve** | Original verified run stays intact | Original Timeline and authoritative ledger are never rewritten by replay |

Marrow's core concerns include canonical event/state encoding, versioned transition context, checkpoints, integrity verification, deterministic reconstruction and immutable parent/child history relationships. The domains provide their own reducers, rules and projections; neither application imports the other's business logic.

The visible controls are intentionally different: Ghost Lap uses a game-focused hourglass history dock, while Revenant uses a financial analysis Timeline. **The reusable contract lives underneath them.**

### Architecture at a glance

```text
                      THE SANDS OF TIME
                              │
                ┌─────────────┴─────────────┐
                │                           │
         GHOST LAP                     REVENANT
      Game rules, scoring           Ledger, fraud, policy
      Solo + multiplayer            Holds, replay, liquidity
                │                           │
                └─────────────┬─────────────┘
                              │
                       SHARED MARROW
                    Kernel · Timeline
                     Infrastructure
                              │
                  PostgreSQL · Redis
                   API · Realtime · Worker
```

**Technology:** C# / .NET 10, PostgreSQL, Redis, SignalR, React, TypeScript, PixiJS, Vite and ML.NET. The solution is organized as a modular monorepo with separate application domains and shared infrastructure—not two unrelated repositories or a fleet of unnecessary microservices.

### Repository layout

```text
.kiro/                         13 specs, steering, settings, hooks
src/
  Marrow.Kernel/               Shared contracts and primitives
  Marrow.Timeline/             Deterministic record / restore / fork / compare
  Marrow.Infrastructure/       Shared persistence, identity and messaging
  GhostLap/                    Game rules, solo history and multiplayer domain
  GhostLap.Contracts/
  Revenant/                    Finance, ledger, screening and counterfactuals
  Revenant.Contracts/
  Hosts.Api/                   HTTP API and authentication
  Hosts.Realtime/              Realtime game authority and transport
  Hosts.Worker/                Background operations and replay modes
web/
  apps/
    ghost-lap/                 Application 1 — independent React/PixiJS frontend
    revenant/                  Application 2 — independent React frontend
  packages/
    contracts/                 Shared typed browser contracts
    timeline-ui/               Reusable Timeline UI package
scripts/                       Database and verification scripts
tests/                         .NET, package, integration and browser tests
deploy/                        Compose and deployment configuration
docs/
  adr/                         Architecture decision records
  architecture/                Design and contract references
```

**Skeleton Crew structure:** `web/apps/ghost-lap/` and `web/apps/revenant/` are two separate application folders, each with its own package, entry point, components and experience. Marrow is shared rather than copied into either one.

---

## Run the full stack — Ghost Lap and backend services

For **Ghost Lap's server-backed solo recording, history library, forks and multiplayer**, and for Revenant's service-backed implementation, run the local infrastructure.

**Prerequisites:** Docker with Compose v2, Node.js **22.18+ or 24.x**, npm, Git and OpenSSL. The .NET SDK version is specified by [`global.json`](global.json) if you also build or debug the backend outside Docker.

**1. Configure local secrets**

From the repository root:

```bash
cp .env.example .env
```

Replace every `REPLACE_ME_*` entry in `.env` with a locally generated secret as described in [`.env.example`](.env.example). For Ghost Lap's persisted solo histories, also set `REALTIME_SOLO_DB_PASSWORD` to a strong generated password; an empty value disables that feature. Keep `.env` private—it is ignored by Git.

**2. Start the services**

```bash
docker compose --env-file .env -f deploy/compose/docker-compose.yml up --build -d
curl -fsS http://127.0.0.1:8081/health/ready
```

This starts PostgreSQL, Redis, the one-shot database migration job, and the API, Realtime and Worker hosts. The readiness endpoint confirms dependency availability; it is not itself a gameplay or financial acceptance test.

**3. Start the browser applications**

After `npm ci` in `web/`, run each command in its own terminal **from `web/`**:

| Experience | Command | Address |
|---|---|---|
| **Ghost Lap (real backend)** | `npm run dev -w @sands/ghost-lap` | http://localhost:5174/ |
| **Revenant (service-backed R8)** | `npm run dev -w @sands/revenant` | http://localhost:5175/?view=r8 |
| **Revenant (prepared demo)** | `npm run dev:demo -w @sands/revenant` | http://127.0.0.1:5173/?view=r8&demo=1 |

**Revenant backend note:** Full authenticated finance and counterfactual operation additionally requires provisioned staff/tenant context and the correctly configured isolated Replay worker. Starting Compose alone is not a substitute for those advanced workflow prerequisites. The prepared demo above is the recommended first-look experience.

See [Compose setup](deploy/compose/README.md) for hosts, ports and lifecycle. To stop services **without deleting stored volumes**:

```bash
docker compose --env-file .env -f deploy/compose/docker-compose.yml down
```

## Built with Kiro

The development process is retained in **[13 Kiro specifications](.kiro/specs/)** spanning the shared platform, early cross-domain integration, Revenant's financial capabilities, and Ghost Lap's full game and browser experience. Each spec organizes its requirements, design and implementation tasks.

The root `.kiro/` directory includes:

- **[Specifications](.kiro/specs/):** the 13-stage architecture and application work.
- **[Steering](.kiro/steering/):** project structure, technologies, product boundaries, game authority, financial integrity and Timeline correctness.
- **[Hooks](.kiro/hooks/) and [settings](.kiro/settings/):** committed development configuration and tool integration definitions.

The central engineering rule remained the same throughout: **recorded originals must stay intact; alternate outcomes must be separate, deterministic and verifiable**.

## Verification

The source is backed by executable tests, not only demonstration screens. At the verified integrated source checkpoint, the [Linux GitHub Actions run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/45215858378) passed all **seven standard jobs**, including:

- **576 passing .NET solution tests** (with five configured skips), including Ghost Lap, Revenant and Marrow tests.
- **203 passing PostgreSQL integration tests** in the principal integration run (with two configured skips).
- **53 passing Playwright browser end-to-end tests** across stubbed and real-backend scenarios.
- Linux AMD64 ML.NET model/worker checks, shared Timeline and typed-contract tests, frontend builds and architecture checks.

The counts describe their respective CI runs; they are not meant to be added into one unduplicated total. The linked run documents the tested commit and environment.

To check the source locally:

```bash
# Repository root
dotnet build TheSandsOfTime.slnx
dotnet test tests/Architecture/Architecture.Tests.csproj

# web/ directory
npm run typecheck
npm run build
npm run build:demo -w @sands/revenant
npm run check:bundles
```

Some native ML.NET tests require Linux x64. See [GitHub Actions](.github/workflows/) for the full integration environment.

## What this project demonstrates

**Marrow is the product foundation. Ghost Lap and Revenant are the proof.** One is about a personal decision in a game; the other is about an operational decision in a financial network. They have different data, rules, risks and user interfaces—but both can return to an exact moment, preserve the original and investigate another future.

That is the idea behind **The Sands of Time**: **change what happens next, without changing what happened before.**

---

**License:** [MIT](LICENSE). All financial scenarios use synthetic data. This repository contains runnable source and local setup instructions; it does not claim to be a publicly hosted banking service.
