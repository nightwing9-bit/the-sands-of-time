// Presentation-only formatting for R8. Amounts arrive as decimal strings of USD minor units and stay BigInt here.
const group = new Intl.NumberFormat("en-US");
const utc = (options: Intl.DateTimeFormatOptions) => new Intl.DateTimeFormat("en-GB", { timeZone: "UTC", hourCycle: "h23", ...options });
const hm = utc({ hour: "2-digit", minute: "2-digit" });
const hms = utc({ hour: "2-digit", minute: "2-digit", second: "2-digit" });

export const time = (ms: number) => hm.format(ms) + " UTC";
export const clock = (ms: number) => hms.format(ms);
export const hourMinute = (ms: number) => hm.format(ms);

export function money(value: string, signed = false) {
  const n = BigInt(value);
  const x = n < 0n ? -n : n;
  return (n < 0n ? "−" : signed && n > 0n ? "+" : "") + "$" + group.format(x / 100n) + "." + String(x % 100n).padStart(2, "0");
}

export function demoMoney(value: string, signed = false) {
  return money(value, signed).replace(/\.00$/, "");
}

export function demoMillions(value: bigint | string) {
  return "$" + (Number(value) / 100_000_000).toFixed(2) + "M";
}

/** Short label for dense visuals (basins, axes). Exact values are always available beside or behind it. */
export function compactMoney(value: bigint | string) {
  const n = typeof value === "bigint" ? value : BigInt(value);
  const neg = n < 0n;
  const cents = neg ? -n : n;
  // divisor is in cents per hundredth of the unit: K → 1,000 cents, M → 1,000,000 cents. Truncates, never rounds up.
  const scaled = (divisor: bigint, suffix: string) => {
    const hundredths = cents / divisor;
    const whole = hundredths / 100n, frac = hundredths % 100n;
    return group.format(whole) + (frac === 0n ? "" : "." + String(frac).padStart(2, "0").replace(/0$/, "")) + suffix;
  };
  const dollars = cents / 100n;
  const body = dollars >= 1_000_000n ? scaled(1_000_000n, "M") : dollars >= 10_000n ? scaled(1_000n, "K") : group.format(dollars);
  return (neg ? "−$" : "$") + body;
}

export const signedInteger = (s: string) => (BigInt(s) > 0n ? "+" + s : s.replace("-", "−"));
export const sumMinor = (values: readonly string[]) => values.reduce((total, v) => total + BigInt(v), 0n).toString();
export const shortHash = (h: string | null | undefined, head = 8, tail = 4) => !h ? "—" : h.length <= head + tail + 1 ? h : `${h.slice(0, head)}…${h.slice(-tail)}`;

/**
 * Display aliases for the six synthetic banks. Presentation only: every API call and evidence panel keeps the real BANK-x id.
 */
export const BANKS = [
  { id: "BANK-A", code: "MRD", name: "Meridian" },
  { id: "BANK-B", code: "HLD", name: "Halden" },
  { id: "BANK-C", code: "QLN", name: "Quillon" },
  { id: "BANK-D", code: "OST", name: "Oster" },
  { id: "BANK-E", code: "TSL", name: "Tessel" },
  { id: "BANK-F", code: "CRV", name: "Corvane" },
] as const;
export type BankAlias = { id: string; code: string; name: string };
export const bankAlias = (id: string): BankAlias => BANKS.find(b => b.id === id) ?? { id, code: id, name: id };
export const bankCode = (id: string) => bankAlias(id).code;
