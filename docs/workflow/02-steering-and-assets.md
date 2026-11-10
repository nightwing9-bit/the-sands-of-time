# C. Steering and frontend design input plan

| File | Inclusion | Responsibilities | Relevant specs | Must exclude |
|---|---|---|---|---|
| `.kiro/steering/product.md` | `always` | identity and no-fake-signature boundaries | 01–13 | long pitch/source text |
| `.kiro/steering/tech.md` | `always` | stack, architecture discipline, test/evidence defaults | 01–13 | pasted domain rules |
| `.kiro/steering/structure.md` | `always` | exact owner paths and execution mode | 01–13 | all 13 task lists |
| `.kiro/steering/timeline-correctness.md` | `fileMatch` | determinism, version/branch invariants | 02,04,05,06,09,11 when shared files touched | bank/game business logic |
| `.kiro/steering/finance-integrity.md` | `fileMatch` | finance integrity, ML honesty, branch isolation | 05,07,08,09 | game behavior |
| `.kiro/steering/game-authority.md` | `fileMatch` | authoritative game tick/ghost semantics | 06,10,11,12,13 | finance behavior |

**Why only six:** the three short foundational files always load; three small domain/Timeline files load when associated implementation/test paths are touched. We did **not** include the whole project-context file, nor a per-spec steering copy, nor an all-files glob in a custom agent. If a `fileMatch` rule fails to activate on a particular Kiro surface, the executing engineer should explicitly refer to that steering file when performing the corresponding spec, not expand global context.

## Two existing frontend design artifacts (present; on-demand only)

- The exact original files already exist at **`design-inputs/skills1 (1).md`** and **`design-inputs/skills2 (1).md`**. Do **not** recreate, rename or copy them merely to satisfy an outdated path. Specs **09** and **13** load their respective original files explicitly only when implementing frontend; Specs 05/06 may inspect small layout-relevant portions for greybox if useful.
- Do **not** assume arbitrary `Skills1.md` and `Skills2.md` auto-register as native Kiro Agent Skills. Kiro skills use an explicit `.kiro/skills/<skill>/SKILL.md` packaging scheme; keep supplied design files as ordinary cited inputs unless their contents are inspected and a deliberate wrapper is later justified. No fabricated summaries, generated replacement files or automatic recursive inclusion.
- To load, explicitly reference the **actual repository path** in the relevant UI task; no always-loaded steering or new native Agent Skill is required. If these files are later moved, update the consumers in the same change. If temporarily missing in a new checkout, use semantic placeholders and flag the missing input rather than fabricate it. Design assets govern **presentation**, never simulation, auth, financial semantics, replay or API authority.
- **Known compatibility decisions:** Revenant's locked dark/obsidian demo direction wins over the paper-primary proposal in Skills1; preserve its useful typography, data hierarchy and interaction ideas and allow a paper-style **optional light mode** without displacing dark by default. Skills2's “offline Race Your Ghost works” sentence is **not** permission to implement a new offline authoritative/replay engine; provide a clear disconnected/unavailable state until server-backed history is reachable. “Original ledger unchanged” means the original source hash stays identical before/after; divergent source/branch final-state hashes are **expected to differ**. All sample scores, balances, names and speeds are illustrative until derived from actual state.

**Confirmed official current Kiro steering modes**: YAML front matter `always`, `fileMatch`, `manual`, `auto`; use `fileMatchPattern` arrays for conditional files. Reference https://kiro.dev/docs/steering/ .
