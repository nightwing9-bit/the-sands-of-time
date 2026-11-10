// Timeline positions on the browser wire (docs/architecture/03-contracts.md, "Browser Timeline adapter v1").
// A position is a canonical decimal string of a non-negative int64. It is never a JavaScript number, because a long
// logical event offset can exceed 2^53 and a double would silently round it. All arithmetic goes through BigInt.
// Erasable syntax only, no non-type imports: Node runs this file directly in tests.

/** Canonical decimal string, 0 .. 9223372036854775807, no sign, no leading zeros, no exponent. */
export type TimelinePosition = string;

export const MIN_POSITION = 0n;
/** C# `long.MaxValue`, the largest position the server can hold. */
export const MAX_POSITION = 9223372036854775807n;

const CANONICAL = /^(0|[1-9][0-9]{0,18})$/;

/** Canonical decimal string to bigint, or null for anything else (numbers, signs, spaces, leading zeros, > int64). */
export function parsePosition(text: unknown): bigint | null {
  if (typeof text !== 'string' || !CANONICAL.test(text)) return null;
  const value = BigInt(text);
  return value <= MAX_POSITION ? value : null;
}

export function isTimelinePosition(text: unknown): text is TimelinePosition {
  return parsePosition(text) !== null;
}

/** bigint to wire string. Throws RangeError outside 0..int64 so a bad position cannot be sent. */
export function formatPosition(value: bigint): TimelinePosition {
  if (typeof value !== 'bigint' || value < MIN_POSITION || value > MAX_POSITION) {
    throw new RangeError('A timeline position must be an integer from 0 to 9223372036854775807.');
  }
  return value.toString(10);
}

/** Strict reader for a position field of an untrusted JSON object. A JSON number is rejected, never rounded. */
export function readPositionField(record: unknown, field: string): TimelinePosition | null {
  if (typeof record !== 'object' || record === null) return null;
  const value = (record as Record<string, unknown>)[field];
  return isTimelinePosition(value) ? value : null;
}

/** -1, 0 or 1. Both arguments must be valid positions. */
export function comparePositions(a: TimelinePosition, b: TimelinePosition): -1 | 0 | 1 {
  const left = requirePosition(a);
  const right = requirePosition(b);
  return left < right ? -1 : left > right ? 1 : 0;
}

export interface PositionBounds {
  start: TimelinePosition;
  end: TimelinePosition;
}

/** Nearest position inside `bounds`. */
export function clampPosition(position: TimelinePosition, bounds: PositionBounds): TimelinePosition {
  const value = requirePosition(position);
  const start = requirePosition(bounds.start);
  const end = requirePosition(bounds.end);
  if (start > end) throw new RangeError('Timeline bounds start after they end.');
  return formatPosition(value < start ? start : value > end ? end : value);
}

/** `position + delta` (delta may be negative or huge), clamped to `bounds`. Exact above 2^53. */
export function stepPosition(position: TimelinePosition, delta: bigint, bounds: PositionBounds): TimelinePosition {
  const target = requirePosition(position) + delta;
  const start = requirePosition(bounds.start);
  const end = requirePosition(bounds.end);
  if (start > end) throw new RangeError('Timeline bounds start after they end.');
  return formatPosition(target < start ? start : target > end ? end : target);
}

function requirePosition(text: TimelinePosition): bigint {
  const value = parsePosition(text);
  if (value === null) throw new RangeError('Not a canonical timeline position.');
  return value;
}
