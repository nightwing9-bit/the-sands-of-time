/**
 * Plain-language explanation of a failed case decision. `unwrap` produces "<message> · <code>", so the server's structured code
 * is matched first and most specific first: a sandbox denial is a 403 and must not fall into the generic role denial.
 */
export function explainDecisionFailure(error: string): string {
  if (/sandbox_live_write_denied/i.test(error))
    return "Live payment decisions cannot be made on a sandbox branch. Return to the original authorized run.";
  if (/mfa_required|\bmfa\b/i.test(error))
    return "A fresh MFA code is required. Sign out, sign in again with your MFA code, then retry.";
  if (/StaleVersion|\bstale\b/i.test(error))
    return "This case changed since you opened it. The queue has refreshed; review the new version before deciding.";
  if (/DistinctSupervisorRequired|\bdistinct\b|\bsupervisor\b/i.test(error))
    return "A different authorized supervisor must complete the required approval.";
  if (/run_forbidden|\bpermission\b|\bforbidden\b|\bdenied\b/i.test(error))
    return "The current staff authority does not permit this action.";
  return "The decision was not committed. Retry, or cancel and refresh the queue.";
}
