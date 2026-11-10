// Validates: the shared CSRF cookie reader used by the typed client and the app wires (Spec 03 double-submit rule).
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { GUEST_CSRF_COOKIE, STAFF_CSRF_COOKIE, readCsrfToken } from '../src/csrf.ts';
test('reads the guest or staff CSRF cookie out of a document.cookie string', () => {
  assert.equal(readCsrfToken(`theme=dark; ${GUEST_CSRF_COOKIE}=abc.def; other=1`), 'abc.def');
  assert.equal(readCsrfToken(`${STAFF_CSRF_COOKIE}=staff.token`), 'staff.token');
});
test('ignores look-alike names, empty values, over-long values and no cookies at all', () => {
  assert.equal(readCsrfToken(''), null);
  assert.equal(readCsrfToken('sands_csrf_guest=plain'), null);
  assert.equal(readCsrfToken(`x${GUEST_CSRF_COOKIE}=nope`), null);
  assert.equal(readCsrfToken(`${GUEST_CSRF_COOKIE}=`), null);
  assert.equal(readCsrfToken(`${GUEST_CSRF_COOKIE}=${'a'.repeat(201)}`), null);
});
test('a token value may contain "=" and "." without being cut', () => {
  assert.equal(readCsrfToken(`${GUEST_CSRF_COOKIE}=a.b=c`), 'a.b=c');
});
test('the cookie names match the identity module (Spec 03 IdentityCookies)', () => {
  assert.equal(GUEST_CSRF_COOKIE, '__Host-sands_csrf_guest');
  assert.equal(STAFF_CSRF_COOKIE, '__Host-sands_csrf_staff');
});
