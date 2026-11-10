// Validates: Requirements R04.04, R04.08 (every Spec-02 TimelineErrorCode maps to an explicit browser state)
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { classifyHttpFailure, timelineFailure, viewStateOf } from '../src/failure.ts';
import { KNOWN_TIMELINE_ERROR_CODES, TIMELINE_ERRORS, describeTimelineError, isTimelineErrorCode } from '../src/timeline-errors.ts';

/** Member names of the real C# enum, read from the source of truth rather than copied here. */
function csharpTimelineErrorCodes(): string[] {
  const source = readFileSync(new URL('../../../../src/Marrow.Timeline/TimelineErrors.cs', import.meta.url), 'utf8');
  const body = /enum\s+TimelineErrorCode\s*\{([^}]*)\}/.exec(source)?.[1];
  assert.ok(body, 'TimelineErrorCode enum not found in TimelineErrors.cs');
  return body
    .split(',')
    .map((member) => member.trim())
    .filter((member) => member.length > 0);
}

test('the client handles exactly the members of the C# TimelineErrorCode enum', () => {
  const csharp = csharpTimelineErrorCodes();
  assert.equal(csharp.length, 7, 'the frozen v1 contract has seven codes; a new one needs an ADR');
  assert.deepEqual([...KNOWN_TIMELINE_ERROR_CODES].sort(), [...csharp].sort());
});

test('the committed OpenAPI document publishes the same codes (so the generated union is the C# enum)', () => {
  const openapi = JSON.parse(readFileSync(new URL('../openapi/hosts-api.json', import.meta.url), 'utf8')) as {
    components: { schemas: { TimelineErrorCode: { enum: string[] } } };
  };
  assert.deepEqual(openapi.components.schemas.TimelineErrorCode.enum, csharpTimelineErrorCodes(), 'same members in the same order');
});

test('every code has a table entry, a message and a recoverability rule, and none is ever usable', () => {
  for (const code of KNOWN_TIMELINE_ERROR_CODES) {
    const info = describeTimelineError(code);
    assert.ok(info.message.length > 10, code);
    assert.ok(['never', 'after_refetch'].includes(info.retry), code);
    assert.equal(info.usable, false, `${code} must never present a failed verification as usable`);
    assert.ok(isTimelineErrorCode(code));
  }
  assert.equal(isTimelineErrorCode('Teapot'), false);
  assert.equal(isTimelineErrorCode('toString'), false, 'prototype keys are not codes');
});

test('each code maps to its explicit failure and view state', () => {
  const expected: Record<string, { kind: string; view: string }> = {
    NotFound: { kind: 'timeline', view: 'error' },
    Forbidden: { kind: 'denied', view: 'denied' },
    InvalidPosition: { kind: 'timeline', view: 'error' },
    IncompatibleVersion: { kind: 'timeline', view: 'incompatible' },
    CorruptHistory: { kind: 'timeline', view: 'error' },
    Conflict: { kind: 'timeline', view: 'error' },
    Cancelled: { kind: 'cancelled', view: 'cancelled' },
  };
  assert.deepEqual(Object.keys(expected).sort(), [...KNOWN_TIMELINE_ERROR_CODES].sort(), 'the expectation table covers every code');
  for (const code of KNOWN_TIMELINE_ERROR_CODES) {
    const failure = timelineFailure(code, 409, 'trace-1');
    assert.equal(failure.kind, expected[code]!.kind, code);
    assert.equal(viewStateOf({ ok: false, failure }), expected[code]!.view, code);
    // The same code arriving in an HTTP error body gives the same result.
    const viaHttp = classifyHttpFailure(409, { code: 'whatever', status: 409, traceId: 'trace-1', timelineError: code });
    assert.deepEqual(viaHttp, failure, code);
  }
  assert.equal(TIMELINE_ERRORS.Conflict.retry, 'after_refetch');
  assert.equal(TIMELINE_ERRORS.CorruptHistory.retry, 'never');
});

test('a timeline error code this client does not know blocks instead of being guessed', () => {
  const failure = classifyHttpFailure(409, { code: 'x', status: 409, timelineError: 'SomethingNew' });
  assert.equal(failure.kind, 'incompatible');
  assert.equal(viewStateOf({ ok: false, failure }), 'incompatible');
});
