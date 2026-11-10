namespace Revenant.Finance;

/// <summary>Projection payment status for the V1 slice. Spec 07 owns the final full matrix and posting controls.</summary>
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
/// Legal transitions: Initiated to Screened; Screened to Held or Cleared; Held to Approved, Rejected or
/// Returned; Approved to Cleared; Cleared to Settled or Returned. Everything else is illegal.
/// </summary>
public static class PaymentLifecycle
{
    public static bool IsLegal(PaymentStatus from, PaymentStatus to) => (from, to) switch
    {
        (PaymentStatus.Initiated, PaymentStatus.Screened) => true,
        (PaymentStatus.Screened, PaymentStatus.Held or PaymentStatus.Cleared) => true,
        (PaymentStatus.Held, PaymentStatus.Approved or PaymentStatus.Rejected or PaymentStatus.Returned) => true,
        (PaymentStatus.Approved, PaymentStatus.Cleared) => true,
        (PaymentStatus.Cleared, PaymentStatus.Settled or PaymentStatus.Returned) => true,
        _ => false,
    };

    /// <summary>Rejected, Returned and Settled have no outgoing transition.</summary>
    public static bool IsTerminal(PaymentStatus status) =>
        status is PaymentStatus.Rejected or PaymentStatus.Returned or PaymentStatus.Settled;

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
