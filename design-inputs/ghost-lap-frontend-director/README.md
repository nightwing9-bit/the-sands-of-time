# Ghost Lap — Frontend & Experience Director Skill

**Status:** comprehensive implementation-direction baseline (2026-11-18). This archive contains **only skill instructions, no copyrighted art/audio/font files**. It does not contain or simulate the backend.

## Install / handoff

1. Give the **entire folder** `ghost-lap-frontend-director/` to the coding agent (not only `SKILL.md`); keep `references/` and `scripts/` adjacent to it.
2. For agents supporting Agent Skills, place the folder in that agent's recognised local skills directory (e.g. Claude Code `~/.claude/skills/ghost-lap-frontend-director/`; Codex `~/.codex/skills/ghost-lap-frontend-director/`). Or keep in project repo and explicitly instruct agent to read its `SKILL.md` and every mandatory referenced file.
3. Preserve existing project source assets at the user's known Mac path `~/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/` until the frontend agent creates a **derived** deployable copy.
4. Start with `SKILL.md`, then `references/00...` through `12...` in order. Execute discovery/audit first. The skill is not permission to change backend rules.
5. Bring `references/00-authority-and-boundaries.md` and `references/11-acceptance-and-open-items.md` to the main backend agent to map actual contracts.

## Why multiple files?

`SKILL.md` is the high-level operating procedure. Supporting references are detailed enough for a smaller coding agent to execute one component at a time without trying to remember a giant one-shot prompt. Every reference is part of the deliverable, not optional prose.

## Verified vs unknown

Decisions and file names in this package come from the user's conversation, attached `characters.md` and backend-provided feature inventory. **The actual full Mac `game-assets` directory and frontend/backend codebase were not uploaded to this authoring environment.** Therefore exact atlas coordinates, media licenses, native backend DTO fields, collision/world dimensions, app repo path and codec compatibility are explicit `VERIFY` gates, never guessed.

## What to tell the implementing agent

> Use the Ghost Lap Frontend & Experience Director skill, read its references in mandatory order, identify the real frontend repo and verify `$GAME_ASSETS` first, then implement in the numbered playbook. Respect backend-authoritative mechanics; use the fixed snow arena, Kenney shapes and grey modular walls, exact approved effects/audio, restrained Alto-inspired typography/UI and signature left-hourglass timeline. Do not invent missing APIs, social features, media, hit effects or history frames. Report each gate and test visually before declaring done.
