# @sands/contracts

Typed browser access to the C# hosts. Both app shells (`web/apps/revenant`, `web/apps/ghost-lap`) build their clients with `createMarrowTransport`; neither redeclares a DTO.

## Where each type comes from

| Piece | Source of truth | How it gets here |
|---|---|---|
| REST paths, request/response DTOs, `TimelineErrorCode` union, `ApiProblem` | `Hosts.Api` endpoint metadata | `scripts/contracts/generate.sh` builds `Hosts.Api`, emits `openapi/hosts-api.json`, runs `openapi-typescript` into `src/generated/` |
| API contract version | `ContractMetadata.ApiContractVersion` (C#), also OpenAPI `info.version` | generated `src/generated/contract-version.ts` |
| Realtime envelope and cursor | `Marrow.Kernel/Realtime` | hand-written mirror in `src/realtime.ts`, checked against the live server JSON by `tests/Integration/Realtime` |

Change a C# DTO, then run `scripts/contracts/generate.sh` and commit `openapi/hosts-api.json` and `src/generated/*`. CI (`contracts` job) runs `scripts/contracts/generate.sh --check` and fails with a diff when they are stale. Needs the SDK from `global.json` and `npm ci` in `web/`; it never starts the host or contacts a database.

## Wire rules

- A timeline position is a canonical decimal string (`src/position.ts`); a JSON number is rejected. Arithmetic uses `BigInt`.
- A failed call is a `TransportFailure` value, never a thrown error: `cancelled`, `stale`, `offline`, `denied`, `timeline`, `incompatible`, `rate_limited`, `server`, `malformed`. `viewStateOf` maps a result to `loading | ready | cancelled | denied | offline | incompatible | error`.
- Timeline failures: an endpoint that surfaces a `Marrow.Timeline` error must answer with `ApiProblem` and set `timelineError` to the exact `TimelineErrorCode` member name. `Forbidden` becomes a permission state, `Cancelled` a cancelled state, the rest `timeline` failures. The HTTP status for each code is chosen by the endpoint-owning spec. A code the client does not know is treated as `incompatible`.
- `createMarrowTransport` refuses every request except `GET /meta/contracts` until `negotiate()` returned a compatible verdict (same API contract major, same realtime schema, only known timeline error codes).
- `LatestRequestGate` lets only the newest request deliver; an older answer that arrives late is `stale`.
- `RealtimeLink` wraps the hub client: `connecting | syncing | online | reconnecting | offline | denied | closed`. It reports `online` only after each stream has been re-synchronised from a snapshot, and restarts a closed connection with bounded backoff.

## Commands (from `web/`)

```
npm run typecheck                         # strict tsc, includes test/types.check.ts compile-time tests
npm test -w @sands/contracts              # node --test (type stripping, Node >= 22.18; see engines in web/package.json)
CONTRACTS_LIVE_API_ORIGIN=http://127.0.0.1:5199 npm test -w @sands/contracts   # also runs test/live-host.test.ts
npm run generate -w @sands/contracts      # regenerate; add :check to only verify
```
CSRF: cookie-authenticated mutations echo the readable double-submit cookie in `X-CSRF-Token`; `csrf.ts` (`currentCsrfToken`, `readCsrfToken`) is the one reader, used by the typed client and by the app wires.
## Where the browser Timeline wires fit
`@sands/contracts` has no Timeline REST client: Hosts.Api serves no Timeline route and the OpenAPI document lists none. Each app's `*TimelineWire.ts` talks to the TEST-ONLY host in `tests/E2E/backend`; Specs 05/09 (Revenant) and 06/11 (Ghost Lap) replace it with their own domain endpoints, which then appear in OpenAPI and regenerate here.
