// Validates: Requirements R04.05 (decimal-string positions, exact above 2^53)
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  MAX_POSITION,
  clampPosition,
  comparePositions,
  formatPosition,
  isTimelinePosition,
  parsePosition,
  readPositionField,
  stepPosition,
} from '../src/position.ts';

test('positions above 2^53 round-trip through BigInt without loss', () => {
  const beyondSafe = '9007199254740993'; // 2^53 + 1: the nearest double is 2^53
  assert.equal(Number(beyondSafe).toString(), '9007199254740992', 'sanity: a double really would lose it');
  assert.equal(formatPosition(parsePosition(beyondSafe)!), beyondSafe);
  assert.equal(formatPosition(parsePosition('9223372036854775807')!), '9223372036854775807');
});

test('random int64 positions survive parse -> format -> JSON -> parse unchanged', () => {
  let state = 0x9e3779b97f4a7c15n;
  const next = (): bigint => {
    state = (state * 6364136223846793005n + 1442695040888963407n) & 0xffffffffffffffffn;
    return state;
  };
  for (let i = 0; i < 2000; i++) {
    const value = next() & MAX_POSITION; // 0 .. 2^63-1
    const text = formatPosition(value);
    const wire = JSON.parse(JSON.stringify({ position: text })) as { position: string };
    assert.equal(parsePosition(wire.position), value);
    assert.equal(wire.position, text);
  }
});

test('only canonical non-negative int64 decimal strings are positions', () => {
  for (const good of ['0', '1', '42', '9223372036854775807']) assert.ok(isTimelinePosition(good), good);
  for (const bad of ['', '-1', '+1', '01', '1.0', '1e3', ' 1', '1 ', '0x10', '9223372036854775808', '99999999999999999999', 7, 7n, null, undefined, {}]) {
    assert.equal(parsePosition(bad), null, String(bad));
  }
  assert.throws(() => formatPosition(-1n), RangeError);
  assert.throws(() => formatPosition(MAX_POSITION + 1n), RangeError);
});

test('a JSON number is never accepted as a position, even a safe one', () => {
  assert.equal(readPositionField({ position: 42 }, 'position'), null);
  assert.equal(readPositionField({ position: '42' }, 'position'), '42');
  assert.equal(readPositionField(null, 'position'), null);
  assert.equal(readPositionField({}, 'position'), null);
});

test('compare, clamp and step are exact above 2^53 and stay inside the bounds', () => {
  const a = '9007199254740993';
  const b = '9007199254740992';
  assert.equal(comparePositions(a, b), 1, 'doubles would call these equal');
  assert.equal(comparePositions(b, a), -1);
  assert.equal(comparePositions(a, a), 0);
  const bounds = { start: '9007199254740990', end: '9007199254740999' };
  assert.equal(stepPosition(a, 1n, bounds), '9007199254740994');
  assert.equal(stepPosition(a, -3n, bounds), '9007199254740990');
  assert.equal(stepPosition(a, 100n, bounds), '9007199254740999', 'clamped to end');
  assert.equal(stepPosition(a, -(10n ** 18n), bounds), '9007199254740990', 'clamped to start');
  assert.equal(clampPosition('0', bounds), '9007199254740990');
  assert.equal(clampPosition('9223372036854775807', bounds), '9007199254740999');
  assert.throws(() => comparePositions('x', '1'), RangeError);
  assert.throws(() => clampPosition('5', { start: '9', end: '2' }), RangeError);
});
