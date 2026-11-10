// Browser handling of every Marrow Timeline error code (Spec 02 `TimelineErrorCode`, frozen in 03-contracts.md).
// The code union is not typed by hand: it comes from the OpenAPI schema generated from the real C# enum, so adding a
// C# member regenerates the union and this file stops compiling until the new code is handled here.
import type { components } from './generated/hosts-api.ts';

/** NotFound | Forbidden | InvalidPosition | IncompatibleVersion | CorruptHistory | Conflict | Cancelled (generated). */
export type TimelineErrorCode = components['schemas']['TimelineErrorCode'];

/** What the shared UI shows. Maps onto the loading/cancelled/permission/offline states of R04.04. */
export type TimelineViewState = 'denied' | 'cancelled' | 'incompatible' | 'error';

export interface TimelineErrorInfo {
  /** Which explicit UI state this code belongs to. */
  viewState: TimelineViewState;
  /** Whether repeating the identical request can help. Only a refetch of the authoritative state ever helps. */
  retry: 'never' | 'after_refetch';
  /** A failed verification is never an available branch or a valid preview. */
  usable: false;
  /** Plain text for the user; carries no server detail. */
  message: string;
}

/**
 * Exhaustive by construction: `satisfies Record<TimelineErrorCode, ...>` fails to compile for a missing code and for
 * an extra one.
 */
export const TIMELINE_ERRORS = {
  NotFound: {
    viewState: 'error',
    retry: 'never',
    usable: false,
    message: 'This timeline does not exist or is no longer available.',
  },
  Forbidden: {
    viewState: 'denied',
    retry: 'never',
    usable: false,
    message: 'You do not have permission to use this timeline.',
  },
  InvalidPosition: {
    viewState: 'error',
    retry: 'never',
    usable: false,
    message: 'That position is outside this timeline.',
  },
  IncompatibleVersion: {
    viewState: 'incompatible',
    retry: 'never',
    usable: false,
    message: 'This history was recorded with rules or data formats this version cannot read.',
  },
  CorruptHistory: {
    viewState: 'error',
    retry: 'never',
    usable: false,
    message: 'This history failed verification and cannot be shown or branched.',
  },
  Conflict: {
    viewState: 'error',
    retry: 'after_refetch',
    usable: false,
    message: 'The timeline changed or the request conflicts with an earlier one. Reload and try again.',
  },
  Cancelled: {
    viewState: 'cancelled',
    retry: 'after_refetch',
    usable: false,
    message: 'The request was cancelled before it finished. Nothing was changed.',
  },
} as const satisfies Record<TimelineErrorCode, TimelineErrorInfo>;

/** Every code the client can handle, derived from the table above. */
export const KNOWN_TIMELINE_ERROR_CODES = Object.keys(TIMELINE_ERRORS) as readonly TimelineErrorCode[];

export function isTimelineErrorCode(value: unknown): value is TimelineErrorCode {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(TIMELINE_ERRORS, value);
}

export function describeTimelineError(code: TimelineErrorCode): TimelineErrorInfo {
  return TIMELINE_ERRORS[code];
}
