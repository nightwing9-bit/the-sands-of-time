import { expect, test } from "@playwright/test";
import { explainDecisionFailure } from "../../web/apps/revenant/src/r8/decisionErrors.ts";
// PRESENTATION coverage only (pure formatter, no browser, no backend). Strings mirror what `unwrap` builds: "<message> · <code>".
test("decision errors resolve to their specific explanation, most specific code first", () => {
  const sandbox = explainDecisionFailure("You are not allowed to do that. · sandbox_live_write_denied");
  expect(sandbox).toBe("Live payment decisions cannot be made on a sandbox branch. Return to the original authorized run.");
  expect(explainDecisionFailure("Forbidden · mfa_required")).toContain("fresh MFA code");
  expect(explainDecisionFailure("The request conflicts with the current state. · inapplicable-StaleVersion")).toContain("changed since you opened it");
  const distinct = explainDecisionFailure("The request conflicts with the current state. · inapplicable-DistinctSupervisorRequired");
  expect(distinct).toContain("different authorized supervisor");
  expect(distinct).not.toContain("staff authority");
  expect(explainDecisionFailure("Access denied · forbidden")).toBe("The current staff authority does not permit this action.");
  expect(explainDecisionFailure("run_forbidden · run_forbidden")).toBe("The current staff authority does not permit this action.");
  const unknown = explainDecisionFailure("Something unexpected · inapplicable-Closed");
  expect(unknown).toContain("was not committed");
  expect(unknown).not.toMatch(/committed for|released|approved/i);
});
