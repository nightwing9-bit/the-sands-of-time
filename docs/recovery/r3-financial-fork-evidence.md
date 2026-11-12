# R3 financial fork evidence

## Early identity checkpoint

- Verified R2 implementation ancestor `0e042125d5103e36673d1054fa4e42f90ec045a5`, starting branch HEAD `7dcae197c609a02c4dac0252fcba231370453ac2`.
- Local SDK `10.0.404`. Model ZIP SHA-256 `c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de`. Model manifest file SHA-256 `a96b3d5f364cf543c071c1be17ca7a292b181272cdb6b934a15671b8ba574d5a`. Both hashes match R1.
- The Release solution build completed with zero warnings and zero errors. Local Revenant non-Docker tests passed 171 cases, including the V4 context pin and opening-state test.
- The candidate adds the branch execution coordinator, binding persistence and migration `0010_v4_branch_execution.sql`. It extends the V4 seal store, upgrades the R2 migration regression fixture and adds an executed-test requirement to the existing CI workflow.
- The PostgreSQL test fixes the source and child execution UUIDs at `00000000-0000-0000-0000-000000010001` and `00000000-0000-0000-0000-000000010002`. It reports the actual Timeline IDs, state hash and prefix digest when run. Native results and TRX evidence await the approved first push.

The identity test covers a source that seals through 09:05, a real fork at the 09:00 seal, and the first child suffix at sequence 3. Its assertions cover canonical bytes and hash, child prefix ownership, invalid bindings, retry and cold seal replay. These PostgreSQL assertions are unverified. The financial counterfactual, financial invariants and two-worker tests are pending.
