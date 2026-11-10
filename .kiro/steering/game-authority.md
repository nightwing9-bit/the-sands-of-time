---
inclusion: fileMatch
fileMatchPattern:
  - "src/GhostLap/**/*.cs"
  - "src/GhostLap.Contracts/**/*.cs"
  - "tests/GhostLap/**/*"
  - "web/apps/ghost-lap/**/*"
  - "tests/E2E/**/*ghost*"
---
# Ghost Lap authority

C# owns score/physics/pickups; one Realtime process owns each active room. Fixed-tick, fixed-point integer deterministic game state; rules/layout versions pinned; candle schedule independent of action-dependent RNG. Historical ghost cannot collide/collect/score in living world. Every branch restores full charge/score/pickups/cooldowns/Overlit/timers at exact fork tick. Solo is not eligible for multiplayer rating. Spectators are denied gameplay actions. Load `design-inputs/skills2 (1).md` only in Ghost Lap frontend tasks. Its offline playback idea does not authorize a second client-local game engine; the server is authoritative. Do not equate a changed branch final-state hash with a source integrity failure.
