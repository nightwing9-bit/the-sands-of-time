---
inclusion: fileMatch
fileMatchPattern:
  - "src/Revenant/**/*.cs"
  - "src/Revenant.Contracts/**/*.cs"
  - "tests/Revenant/**/*"
  - "web/apps/revenant/**/*"
  - "tests/E2E/**/*revenant*"
---
# Revenant authority

Synthetic money only. Integer minor units, per-currency balanced double-entry posting enforced **inside PostgreSQL**, legal payment lifecycle, idempotency and outbox. DB live ledger is not writable from replay/counterfactual. Version-pin policy/model/features; explain only what trained/inferred feature evidence supports. Four-eyes release requires distinct actors. Source event decisions are evidence, exogenous intents are replay inputs. No actual production ML accuracy claims. Load `design-inputs/skills1 (1).md` only in Revenant frontend tasks. For the locked demo use dark/obsidian by default; the paper design can inform optional light mode. Never guess features or display synthetic “prevented fraud” for an unresolved hold.
