# ADR-P1-27 — Ghost Lap competitive conflicts and terminal ingress

**Status** Accepted for Task 10.5 remediation.

**Date** 2026-11-17

The independent Task 10.5 audit found a repeatable 2-player win bias under equivalent policies. Phase ablation showed that fixed pickup priority and same-tick Burst suppression each contributed and that both changes were required in the 32-seed diagnostic. The larger 3-player skew followed the path-choice preference of the old scripted controller even when the physical sockets stayed fixed. The paired-seed and real-host evidence is recorded in [the Task 10.5 report](../architecture/ghostlap-mp-load-benchmark.md).

## Competitive rules identity

New countdowns and rematches seal **`ghost-mp-v2`** with **`arena-v1`**, the original geometry fingerprint, the same candle schedule and the existing sealed rating algorithm. The rules identity is part of the config digest and match ID, and selects the reducer in the authoritative match context. Snapshots and durable results carry the sealed rules identity. Canonical state hashes use **`ghost-mp-state/2`** and include `ghost-mp-v2`. The resolver and canonical schema for `ghost-mp-v1` remain available for its existing configurations. Solo keeps its historical entry point and schema.

- For each candle, take the greatest priority among overlapping eligible players. Priority is a fixed 64-bit integer permutation over a domain-separated key from the match seed, logical tick, site index and slot. The sealed schedule permits at most one active candle at a site on a tick, so the site and tick identify the contest. Distinct slots have distinct ranks in each contest. Inputs, clock, network scheduling and principal identities never contribute. Candle collection still grants 25 points and one charge up to the cap.
- Contact compares **post-pickup charge at the beginning of the contact phase**. Every overlapping, ready pair with unequal charge contributes one loss to its weaker member and enters the existing per-pair cooldown. Total loss clamps at zero. Canonical pair order attributes a capped last charge unit and orders events. A pair cannot become eligible because another pair reduced a player during the same phase.
- Burst eligibility is determined from **post-contact charge and the manual edge or automatic deadline**. A manual edge and automatic deadline in the same slot produce one manual Burst. Every eligible initiator spends its charge before hits are applied. The set of hits uses the existing radius, active-target mask and damage values. A simultaneous hit cannot cancel an already initiated Burst. Manual events precede automatic events and slots order their events within each group. The stable order attributes capped score loss without changing any player's aggregate loss.
- Existing movement, collision, grace, forfeit exclusion, score, placement, timing, fixed-point geometry, receipt transaction and outbox type are retained. The database stores versioned data through its existing constraints. This change needs no data migration.

## Terminal input disposition

The engine's bounded channel is the admission boundary. Its writer completes before the final tick drains. A write that wins the race joins that tick's ordinary admission pass, which applies or refuses it. A write that loses returns `no_running_match`. The loop does not extend play beyond tick 2,700.

The runtime closes admission and drains as typed refusals on early exit before attempting the terminal database operation. A failed reducer step also refuses provisional admissions from the failed tick. Authority and controller fencing still apply. Rejections use the existing bounded fan-out, so delivery can be interrupted by disconnect, shutdown or authority loss. No socket or database operation runs under the ingress boundary.
