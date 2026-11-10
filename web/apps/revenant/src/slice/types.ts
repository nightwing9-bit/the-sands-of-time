// Wire types of the Revenant slice API (spec 05, task 5.4). Every value the slice screen shows is read from one of these records,
// which the server built from Marrow Timeline restores of the real finance projection. Nothing here is a default or a sample.
// Money is integer minor units of `currency`; positions are decimal strings.
import type { TimelinePosition } from "@sands/contracts";
import type { TimelineMarker } from "@sands/timeline-ui";

export interface Labelling {
  synthetic: boolean;
  projectionOnly: boolean;
  /** "none" means the server performed no database ledger posting. */
  ledgerPosting: string;
  labelSchema: string;
  costModel: string;
  note: string;
}
export interface SliceBank { bankId: string; name: string; openingMinor: number }
export interface SliceEvent {
  position: TimelinePosition;
  intentId: string;
  businessTime: string;
  sourceBankId: string;
  sourceAccountId: string;
  destBankId: string;
  destAccountId: string;
  amountMinor: number;
  currency: string;
  syntheticLabel: string;
  newBeneficiary: boolean;
  sanctionsHit: boolean;
}
export interface SlicePolicy {
  policyId: string;
  configHash: string;
  velocityHoldThreshold: number;
  velocityAmountFloorMinor: number;
  newBeneficiaryAmountFloorMinor: number | null;
  velocityWindowMs: number;
  holdOnSanctionsHit: boolean;
}
export interface RunDescriptor {
  timelineId: string;
  runId: string;
  sourceId: string;
  branchId?: string | null;
  bounds: { start: TimelinePosition; end: TimelinePosition };
  readOnly: boolean;
  markers: TimelineMarker[];
  historyStatus: string;
  sourcePolicyId: string;
  scenario: { scenarioId: string; scenarioVersion: string; seed: number; manifestHash: string; factsHash: string; startBusinessTime: string };
  labelling: Labelling;
  banks: SliceBank[];
  events: SliceEvent[];
  policies: SlicePolicy[];
  sourceFinalStateHash: string;
}
export interface SliceBankState { bankId: string; name: string; postedMinor: number; reservedMinor: number; availableMinor: number }
export interface SlicePayment {
  position: TimelinePosition;
  intentId: string;
  status: string;
  reason: string;
  policyId: string;
  velocity15m: number;
  trail: string[];
}
export interface SliceCounters {
  fraudSettled: number;
  fraudPendingHold: number;
  fraudTerminallyStopped: number;
  legitimateSettled: number;
  legitimatePendingHold: number;
  legitimateTerminallyStopped: number;
}
export interface SliceState {
  businessTime: string;
  stateHash: string;
  currency: string;
  totalPostedMinor: number;
  totalReservedMinor: number;
  banks: SliceBankState[];
  payments: SlicePayment[];
  counters: SliceCounters;
  labelling: Labelling;
}
export interface SliceMetrics {
  costModel: string;
  labelSchema: string;
  fraudPreventedCount: number;
  fraudPreventedExposureMinor: number;
  fraudDeferredAtHorizonCount: number;
  legitimateDelayedCount: number;
  falsePositiveCostMinor: number;
  incrementalDelayCostMinor: number;
  netEffectMinor: number;
  decisionDifferenceCount: number;
}
export interface SliceAligned { position: TimelinePosition; businessTime: string; original: SliceState; alternate: SliceState; metrics: SliceMetrics }
export interface SliceDecision { status: string; reason: string; policyId: string }
export interface SliceDifference {
  position: TimelinePosition;
  intentId: string;
  syntheticLabel: string;
  amountMinor: number;
  currency: string;
  original: SliceDecision;
  alternate: SliceDecision;
}
export interface SliceComparison {
  branchId: string;
  sourceId: string;
  forkPosition: TimelinePosition;
  finalPosition: TimelinePosition;
  sourcePolicyId: string;
  branchPolicyId: string | null;
  branchStatus: string;
  sourceFinalStateHash: string;
  branchFinalStateHash: string;
  aligned: SliceAligned[];
  differences: SliceDifference[];
  sourceCheck: { hashWhenRecorded: string; hashNow: string; unchanged: boolean };
  labelling: Labelling;
}
/** The options the scrubber passes through to the branch endpoint. */
export interface BranchOptions { policyId: string }
