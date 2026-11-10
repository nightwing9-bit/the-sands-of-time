// Double-submit CSRF token for cookie-authenticated mutations (Spec 03 identity). The server sets a readable cookie next to the
// HttpOnly session cookie and requires the same value in `X-CSRF-Token` on every unsafe request. Reading it here keeps that rule in
// one place for the typed REST client and for the app wires. Erasable syntax only: Node runs this file directly in tests.
/** Name of the readable CSRF cookie for a remembered guest session (`IdentityCookies.GuestCsrf`). */
export const GUEST_CSRF_COOKIE = '__Host-sands_csrf_guest';
/** Name of the readable CSRF cookie for a staff refresh session (`IdentityCookies.StaffCsrf`). */
export const STAFF_CSRF_COOKIE = '__Host-sands_csrf_staff';
export const CSRF_HEADER = 'X-CSRF-Token';
/** Value of the first cookie in `cookieHeader` (the `document.cookie` format) whose name is in `names`, or null. */
export function readCsrfToken(cookieHeader: string, names: readonly string[] = [GUEST_CSRF_COOKIE, STAFF_CSRF_COOKIE]): string | null {
  for (const part of cookieHeader.split(';')) {
    const text = part.trim();
    const equals = text.indexOf('=');
    if (equals <= 0) continue;
    if (names.includes(text.slice(0, equals))) {
      const value = text.slice(equals + 1);
      return value.length > 0 && value.length <= 200 ? value : null;
    }
  }
  return null;
}
/** The CSRF token the browser currently holds, or null outside a browser or without a session. */
export function currentCsrfToken(): string | null {
  return typeof document === 'undefined' ? null : readCsrfToken(document.cookie);
}
