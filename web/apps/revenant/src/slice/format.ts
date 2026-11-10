// Display formatting only. Money stays integer minor units until this point; no floating point enters a displayed amount.
const grouping = new Intl.NumberFormat("en-US", { useGrouping: true, maximumFractionDigits: 0 });

/** "USD 12,345.67" from integer minor units (two decimals, as the scenario's USD). Negative values keep their sign. */
export function formatMinor(minor: number, currency: string): string {
  const sign = minor < 0 ? "-" : "";
  const abs = Math.abs(minor);
  const whole = Math.trunc(abs / 100);
  const cents = String(abs % 100).padStart(2, "0");
  return `${sign}${currency} ${grouping.format(whole)}.${cents}`;
}

/** "09:41" style UTC clock from an ISO instant, or the raw text if it does not parse. */
export function formatClock(iso: string): string {
  const t = Date.parse(iso);
  return Number.isNaN(t) ? iso : new Date(t).toISOString().slice(11, 16);
}

/** Status text for a reader. A held payment is pending, never "stopped": the slice has no review action that resolves it. */
export function statusText(status: string): string {
  return status === "Held" ? "Held · pending" : status;
}

const REASONS: Record<string, string> = {
  None: "no rule fired",
  SanctionsHit: "sanctions hit",
  VelocityRule: "velocity rule",
  NewBeneficiaryAmountRule: "new beneficiary + amount rule",
  InsufficientAvailable: "insufficient available balance",
  UnsupportedCurrency: "unsupported currency",
};
export const reasonText = (reason: string): string => REASONS[reason] ?? reason;

export const syntheticLabelText = (label: string): string =>
  label === "synthetic-fraud-mule-ring" ? "synthetic label: mule ring" : label === "synthetic-legitimate" ? "synthetic label: legitimate" : label;
