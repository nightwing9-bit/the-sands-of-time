// Compile-time tests, run by `npm run typecheck` (tsc strict). Nothing here executes; a wrong type fails the build.
// An unused `@ts-expect-error` is itself an error, so each line below proves the compiler rejects the misuse.
import { createApiClient } from '../src/api-client.ts';
import type { ApiResult } from '../src/failure.ts';
import type { ContractVersions } from '../src/handshake.ts';
import { TIMELINE_ERRORS, type TimelineErrorCode } from '../src/timeline-errors.ts';
import type { RealtimeHubConnection } from '../src/realtime.ts';
import type { SupervisedHubConnection } from '../src/realtime-link.ts';
import type { HubConnection } from '@microsoft/signalr';
import type { TimelinePosition } from '../src/position.ts';
import type { components } from '../src/generated/hosts-api.ts';

type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
function assertType<_T extends true>(): void {}

// --- Timeline error codes: the union is the generated C# enum, and the table covers it exactly --------------------
assertType<Equal<TimelineErrorCode, 'NotFound' | 'Forbidden' | 'InvalidPosition' | 'IncompatibleVersion' | 'CorruptHistory' | 'Conflict' | 'Cancelled'>>();
assertType<Equal<keyof typeof TIMELINE_ERRORS, TimelineErrorCode>>();
assertType<Equal<ContractVersions['timelineErrorCodes'][number], TimelineErrorCode>>();
assertType<Equal<NonNullable<components['schemas']['ApiProblem']['timelineError']>, TimelineErrorCode>>();

// --- Positions are strings on the wire, never numbers -------------------------------------------------------------
const position: TimelinePosition = '9007199254740993';
// @ts-expect-error a number is not a TimelinePosition
const bad: TimelinePosition = 9007199254740993;
void position;
void bad;

// --- The REST client is typed from the generated paths -------------------------------------------------------------
const api = createApiClient({ baseUrl: '' });

async function typedCalls(): Promise<void> {
  const login = await api.post('/auth/staff/login', { body: { userName: 'a', password: 'b', totpCode: null } });
  if (login.ok) {
    assertType<Equal<typeof login.value.accessToken, string>>();
    assertType<Equal<typeof login.value.roles, string[]>>();
    // @ts-expect-error not a field of StaffSessionResponse
    login.value.refreshToken;
  } else {
    // a failure never carries a value
    // @ts-expect-error value exists only on the ok branch
    login.value;
  }

  const meta = await api.get('/meta/contracts');
  if (meta.ok) assertType<Equal<typeof meta.value, ContractVersions>>();

  // @ts-expect-error unknown path
  await api.get('/auth/nowhere');
  // @ts-expect-error /meta/contracts has no POST
  await api.post('/meta/contracts');
  // @ts-expect-error the login body is required
  await api.post('/auth/staff/login');
  // @ts-expect-error wrong body shape
  await api.post('/auth/staff/login', { body: { userName: 1 } });
  // @ts-expect-error the path parameter is required
  await api.post('/auth/admin/guests/{guestId}/ban');
  // @ts-expect-error path parameter has the wrong name
  await api.post('/auth/admin/guests/{guestId}/ban', { path: { id: 'x' } });
  await api.post('/auth/admin/guests/{guestId}/ban', { path: { guestId: 'x' } });
  await api.post('/auth/guest/start');
}
void typedCalls;

// --- ApiResult must be narrowed before the value is used ----------------------------------------------------------
function needsNarrowing(result: ApiResult<string>): string {
  // @ts-expect-error `value` is not on the failure branch
  return result.value;
}
void needsNarrowing;

// --- A real SignalR HubConnection satisfies the structural hub interfaces (no cast needed) -------------------------
declare const realHub: HubConnection;
const forStreamClient: RealtimeHubConnection = realHub;
const forLink: SupervisedHubConnection = realHub;
void forStreamClient;
void forLink;
