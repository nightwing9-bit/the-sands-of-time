namespace Revenant.Finance;

/// <summary>Payment status shared by the pure V1 projection and the durable Spec 07 payment machine.</summary>
public enum PaymentStatus : byte
{
    Initiated = 1,
    Screened = 2,
    Held = 3,
    Cleared = 4,
    Approved = 5,
    Rejected = 6,
    Returned = 7,
    Settled = 8,
}

/// <summary>Why a payment was held, rejected or returned. <see cref="None"/> means cleared without a hold.</summary>
public enum PaymentReason : byte
{
    None = 0,
    SanctionsHit = 1,
    VelocityRule = 2,
    NewBeneficiaryAmountRule = 3,
    InsufficientAvailable = 4,
    UnsupportedCurrency = 5,
}

/// <summary>
/// The one frozen legal-transition matrix (Spec 07 design), used by the pure V1 reducer's trail checks, by the durable
/// payment machine, and mirrored by <c>revenant.is_legal_payment_transition</c> in PostgreSQL (a test compares all
/// 64 pairs). Initiated to Screened; Screened to Held, Cleared or Rejected; Held to Approved, Rejected or Returned;
/// Approved to Cleared or Returned; Cleared to Settled or Returned; Settled to Returned (only through an explicit
/// compensating return posting). Rejected and Returned are terminal. Everything else is illegal.
/// The V1 reducer produces a subset of these edges; adding edges here changes none of its bytes or hashes.
/// </summary>
public static class PaymentLifecycle
{
    public static bool IsLegal(PaymentStatus from, PaymentStatus to) => (from, to) switch
    {
        (PaymentStatus.Initiated, PaymentStatus.Screened) => true,
        (PaymentStatus.Screened, PaymentStatus.Held or PaymentStatus.Cleared or PaymentStatus.Rejected) => true,
        (PaymentStatus.Held, PaymentStatus.Approved or PaymentStatus.Rejected or PaymentStatus.Returned) => true,
        (PaymentStatus.Approved, PaymentStatus.Cleared or PaymentStatus.Returned) => true,
        (PaymentStatus.Cleared, PaymentStatus.Settled or PaymentStatus.Returned) => true,
        (PaymentStatus.Settled, PaymentStatus.Returned) => true,
        _ => false,
    };

    /// <summary>Rejected and Returned have no outgoing transition. Settled can still be returned by compensation.</summary>
    public static bool IsTerminal(PaymentStatus status) =>
        status is PaymentStatus.Rejected or PaymentStatus.Returned;

    /// <summary>
    /// True when the step moves money: entering Settled or leaving it (the compensating return). Such a step is only
    /// legal together with its balanced posting group, which PostgreSQL enforces at commit; the generic transition
    /// command refuses it and the settlement path (task 7.3) owns it.
    /// </summary>
    public static bool RequiresPosting(PaymentStatus from, PaymentStatus to) =>
        to == PaymentStatus.Settled || from == PaymentStatus.Settled;

    /// <summary>True when the trail starts at Initiated and every consecutive pair is legal.</summary>
    public static bool IsLegalTrail(IReadOnlyList<PaymentStatus> trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (trail.Count == 0 || trail[0] != PaymentStatus.Initiated)
        {
            return false;
        }
        for (var i = 1; i < trail.Count; i++)
        {
            if (!IsLegal(trail[i - 1], trail[i]))
            {
                return false;
            }
        }
        return true;
    }
}
