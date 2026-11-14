import type { components } from "@sands/contracts";

type OpeningBalance = components["schemas"]["R8Balance"];
export const bufferPolicy = "R8-SyntheticBufferReference/1";
export const bufferLabel = "Illustrative operating buffer — 10% of opening available balance";
export const bufferDisclosure = "Scenario reference only; not a bank or regulatory minimum, and not an ML warning.";

export type BufferReference =
  | { status: "Available"; openingMinor: string; minor: string }
  | { status: "ReferenceUnavailable" };

const maxMinor = 9_223_372_036_854_775_807n;

function parseMinor(value: unknown): bigint | null {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]{0,18})$/.test(value)) return null;
  const minor = BigInt(value);
  return minor <= maxMinor ? minor : null;
}

export function bufferReference(opening: readonly OpeningBalance[] | null | undefined, bank: string): BufferReference {
  const matches = opening?.filter(balance => balance?.bankId === bank);
  if (matches?.length !== 1) return { status: "ReferenceUnavailable" };
  const balance = matches[0]!;
  const posted = parseMinor(balance.postedMinor);
  const reserved = parseMinor(balance.reservedMinor);
  const available = parseMinor(balance.availableMinor);
  if (posted === null || reserved === null || available === null || available <= 0n || posted - reserved !== available) {
    return { status: "ReferenceUnavailable" };
  }
  return { status: "Available", openingMinor: available.toString(), minor: (available / 10n).toString() };
}
