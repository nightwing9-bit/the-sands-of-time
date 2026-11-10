// Contract version handshake (design: `/meta/contracts`). The host reports the versions it speaks; this client was
// generated from one API contract version and one realtime envelope schema. A different major version, a different
// realtime schema or a Timeline error code this client cannot handle blocks every other call, with a readable reason.
import { GENERATED_API_CONTRACT_VERSION } from './generated/contract-version.ts';
import type { components } from './generated/hosts-api.ts';
import type { ApiResult, IncompatibleReason, TransportFailure } from './failure.ts';
import { REALTIME_SCHEMA_VERSION } from './realtime.ts';
import { isTimelineErrorCode } from './timeline-errors.ts';

export type ContractVersions = components['schemas']['ContractVersions'];

export interface LocalContractVersions {
  /** Semver of the OpenAPI document this client was generated from. */
  apiContract: string;
  realtimeSchemaVersion: number;
}

export function localContractVersions(): LocalContractVersions {
  return { apiContract: GENERATED_API_CONTRACT_VERSION, realtimeSchemaVersion: REALTIME_SCHEMA_VERSION };
}

export type HandshakeVerdict =
  | { compatible: true; remote: ContractVersions; warnings: string[] }
  | { compatible: false; reason: IncompatibleReason; message: string; remote?: ContractVersions };

const SEMVER = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;

function parseVersions(raw: unknown): ContractVersions | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.apiContract !== 'string' || !SEMVER.test(r.apiContract)) return null;
  if (typeof r.realtimeSchemaVersion !== 'number' || !Number.isInteger(r.realtimeSchemaVersion)) return null;
  if (!Array.isArray(r.timelineErrorCodes) || !r.timelineErrorCodes.every((c) => typeof c === 'string')) return null;
  return { apiContract: r.apiContract, realtimeSchemaVersion: r.realtimeSchemaVersion, timelineErrorCodes: r.timelineErrorCodes as ContractVersions['timelineErrorCodes'] };
}

function parts(version: string): [number, number, number] {
  const m = SEMVER.exec(version);
  return [Number(m?.[1]), Number(m?.[2]), Number(m?.[3])];
}

/** Decides whether this client may talk to a host that reported `rawRemote`. Fails closed on anything unreadable. */
export function evaluateContractVersions(rawRemote: unknown, local: LocalContractVersions = localContractVersions()): HandshakeVerdict {
  const remote = parseVersions(rawRemote);
  if (remote === null) {
    return { compatible: false, reason: 'api_major', message: 'The server did not report readable contract versions. Update the app or contact support.' };
  }
  const [remoteMajor, remoteMinor] = parts(remote.apiContract);
  const [localMajor, localMinor] = parts(local.apiContract);
  if (remoteMajor !== localMajor) {
    return {
      compatible: false,
      reason: 'api_major',
      remote,
      message: `This app speaks API contract ${local.apiContract} but the server speaks ${remote.apiContract}. Update ${remoteMajor > localMajor ? 'the app' : 'the server'} to continue.`,
    };
  }
  if (remote.realtimeSchemaVersion !== local.realtimeSchemaVersion) {
    return {
      compatible: false,
      reason: 'realtime_schema',
      remote,
      message: `This app reads realtime schema ${local.realtimeSchemaVersion} but the server sends ${remote.realtimeSchemaVersion}. Live updates are disabled until the app is updated.`,
    };
  }
  const unknown = remote.timelineErrorCodes.filter((code) => !isTimelineErrorCode(code));
  if (unknown.length > 0) {
    return {
      compatible: false,
      reason: 'unknown_timeline_error_code',
      remote,
      message: 'The server can report timeline errors this version cannot handle. Update the app to continue.',
    };
  }
  const warnings: string[] = [];
  if (remoteMinor < localMinor) {
    warnings.push(`The server (${remote.apiContract}) is older than this app (${local.apiContract}); some features may be unavailable.`);
  }
  return { compatible: true, remote, warnings };
}

export function incompatibleFailure(verdict: Extract<HandshakeVerdict, { compatible: false }>): TransportFailure {
  return { kind: 'incompatible', reason: verdict.reason, message: verdict.message };
}

/** One line for the shell to display. */
export function describeHandshake(result: ApiResult<HandshakeVerdict> | null): string {
  if (result === null) return 'Checking server contract…';
  if (!result.ok) {
    return result.failure.kind === 'offline' ? 'Cannot reach the server to check its contract version.' : 'Could not check the server contract version.';
  }
  const verdict = result.value;
  if (!verdict.compatible) return verdict.message;
  return `Server contract ${verdict.remote.apiContract} is compatible.`;
}
