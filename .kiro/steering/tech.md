---
inclusion: always
---
# Engineering defaults

C# 14/.NET 10 ASP.NET Core modular monolith, PostgreSQL schemas by owner, Redis only for warranted transport/cache, SignalR, React/TypeScript/Vite, PixiJS game presentation, ML.NET **Revenant only**. API/Realtime/Worker are composition hosts, not independently invented domain models. Use small explicit public contracts, deterministic version-pinned state transitions, cancellation and idempotency. Do not invent external integration evidence, unsupported performance numbers or production-security claims. Root `.kiro/` remains committed. Before tasks, inspect **only** owned files and named contracts. Use tests matching the risk, not repeated global suites. Detailed contracts: `docs/architecture/03-contracts.md` (read on demand).
