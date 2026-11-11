using System.Globalization;
using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;

namespace Revenant.Intelligence;

/// <summary>Typed reasons a transfer cannot be turned into a <c>risk-features-v1</c> vector. Never silently coerced.</summary>
public enum RiskFeatureError
{
    UnsupportedCurrency = 1,
    AmountNotPositive = 2,
    AmountOutOfRange = 3,
    SourcePostedBalanceInvalid = 4,

    /// <summary>The balance was observed after the payment (a post-state, the PaySim <c>newbalanceOrig</c> class of leak).</summary>
    BalanceNotPreScreening = 5,

    /// <summary>The balance reflects a fact later than the screening decision boundary (a lookup of a later row).</summary>
    BalanceAfterDecisionBoundary = 6,
}

/// <summary>When a source posted balance was observed relative to the payment being screened.</summary>
public enum BalanceMoment : byte
{
    /// <summary>Committed posted balance before this payment's screening decision. The only legitimate predictor input.</summary>
    BeforeScreening = 1,

    /// <summary>Balance after the payment's effect. Never a predictor input.</summary>
    AfterPayment = 2,
}

/// <summary>
/// A source account's committed posted balance (integer minor units, reserves NOT subtracted) together with the highest run-local
/// fact sequence that balance reflects. Live adapters fill it from the ledger inside the screening transaction.
/// </summary>
public readonly record struct SourceBalanceObservation(long PostedMinor, BalanceMoment Moment, long AsOfFactSeq);

/// <summary>
/// The causal boundary of one screening decision: every fact with <c>fact_seq &lt;= DecisionBoundaryFactSeq</c> is visible, nothing later is.
/// Identity (run and intent) travels in <see cref="InferenceEvidenceV1"/>, never in the feature vector.
/// </summary>
public readonly record struct ScreeningBoundary(long DecisionBoundaryFactSeq);

/// <summary>The four numeric model inputs, in the frozen order of <see cref="RiskFeaturesV1.FeatureNames"/>.</summary>
public readonly record struct RiskFeatureVector(float LogAmount, float LogSourcePostedBefore, float TransferFraction, float WouldDrainPosted)
{
    public float[] ToArray() => [LogAmount, LogSourcePostedBefore, TransferFraction, WouldDrainPosted];
}

/// <summary>
/// Validated, immutable pre-screening facts of one USD transfer: the canonical integer raw snapshot of <c>risk-features-v1</c>.
/// Construct only through <see cref="RiskFeaturesV1.Build"/> so the checks cannot be skipped.
/// </summary>
public sealed record RiskFeatureRaw
{
    internal RiskFeatureRaw(string currency, long amountMinor, long sourcePostedBeforeMinor)
    {
        Currency = currency;
        AmountMinor = amountMinor;
        SourcePostedBeforeMinor = sourcePostedBeforeMinor;
    }

    public string Currency { get; }

    public long AmountMinor { get; }

    public long SourcePostedBeforeMinor { get; }

    /// <summary>Canonical bytes: schema id, currency, amount minor, source posted before minor (CanonicalWriter order).</summary>
    public byte[] ToCanonicalBytes() => new CanonicalWriter()
        .WriteString(RiskFeaturesV1.SchemaId)
        .WriteString(Currency)
        .WriteInt64(AmountMinor)
        .WriteInt64(SourcePostedBeforeMinor)
        .ToArray();

    /// <summary>Lower-case hex SHA-256 of <see cref="ToCanonicalBytes"/>. Floats never enter the hash.</summary>
    public string FeatureHash => Convert.ToHexStringLower(SHA256.HashData(ToCanonicalBytes()));
}

/// <summary>A built snapshot: the raw integers, their hash and the model vector derived from them (and only from them).</summary>
public sealed record RiskFeatureSnapshot(RiskFeatureRaw Raw, RiskFeatureVector Vector)
{
    public string FeatureHash => Raw.FeatureHash;
}

/// <summary>
/// <c>risk-features-v1</c>: the single pure feature builder shared by offline ETL and the live Worker (design.md section 2).
/// Inputs are three pre-screening integers; there is deliberately no overload that accepts a label, a post-state balance, a
/// timestamp, a row position or an identifier.
/// <list type="bullet">
/// <item>0 <c>logAmount</c> = ln(1 + amountMinor/100)</item>
/// <item>1 <c>logSourcePostedBefore</c> = ln(1 + postedBeforeMinor/100)</item>
/// <item>2 <c>transferFraction</c> = min(10, amountMinor / max(postedBeforeMinor, 100)); both are minor units, so this is the
/// ratio in major units with the denominator floored at one major unit</item>
/// <item>3 <c>wouldDrainPosted</c> = 1 iff postedBeforeMinor &gt; 0 and amountMinor &gt;= postedBeforeMinor, else 0</item>
/// </list>
/// Synthetic convention: one PaySim nominal unit is one simulated USD major unit = 100 minor units. This is a modelling
/// choice for shared magnitude features, not a currency conversion. Applicable only to USD TRANSFER-like payments.
/// Floats are computed in double and narrowed once; <see cref="Math.Log(double)"/> may differ in the last double bit between
/// CPU architectures, so decisions and hashes rely on the integers and on the quantised PPM score, never on the floats.
/// </summary>
public static class RiskFeaturesV1
{
    public const string SchemaId = "risk-features-v1";
    public const string RequiredCurrency = "USD";
    public const long MinorUnitsPerMajor = 100;

    /// <summary>2^53: every accepted integer converts to double exactly.</summary>
    public const long MaxMinorUnits = 9_007_199_254_740_992L;

    public const double TransferFractionCap = 10d;

    private static readonly string[] Names = ["logAmount", "logSourcePostedBefore", "transferFraction", "wouldDrainPosted"];

    /// <summary>The exact ordered model input columns. This list is also the allow-list: nothing else may reach the trainer.</summary>
    public static IReadOnlyList<string> FeatureNames => Names;

    /// <summary>Source fields that must never be (or derive) a predictor. Informational: the allow-list above is what is enforced.</summary>
    public static IReadOnlySet<string> ForbiddenSourceFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "isFraud", "isFlaggedFraud", "newbalanceOrig", "newbalanceDest", "oldbalanceDest", "nameOrig", "nameDest", "step", "type",
        "truth", "syntheticTruth", "newBeneficiary", "sanctionsHit", "runId", "intentId", "accountId", "sequence", "eventTimeUtcMs",
    };

    /// <summary>Validates and builds the snapshot from three pre-screening facts.</summary>
    public static Result<RiskFeatureSnapshot, RiskFeatureError> Build(string currency, long amountMinor, long sourcePostedBeforeMinor)
    {
        if (!string.Equals(currency, RequiredCurrency, StringComparison.Ordinal))
        {
            return Result<RiskFeatureSnapshot, RiskFeatureError>.Failure(RiskFeatureError.UnsupportedCurrency);
        }
        if (amountMinor <= 0)
        {
            return Result<RiskFeatureSnapshot, RiskFeatureError>.Failure(RiskFeatureError.AmountNotPositive);
        }
        if (amountMinor > MaxMinorUnits)
        {
            return Result<RiskFeatureSnapshot, RiskFeatureError>.Failure(RiskFeatureError.AmountOutOfRange);
        }
        if (sourcePostedBeforeMinor < 0 || sourcePostedBeforeMinor > MaxMinorUnits)
        {
            return Result<RiskFeatureSnapshot, RiskFeatureError>.Failure(RiskFeatureError.SourcePostedBalanceInvalid);
        }
        var raw = new RiskFeatureRaw(currency, amountMinor, sourcePostedBeforeMinor);
        return Result<RiskFeatureSnapshot, RiskFeatureError>.Success(new RiskFeatureSnapshot(raw, Vector(raw)));
    }

    /// <summary>Live Worker entry: rejects post-state balances and balances that include facts after the decision boundary.</summary>
    public static Result<RiskFeatureSnapshot, RiskFeatureError> BuildFromLedger(
        string currency, long amountMinor, SourceBalanceObservation balance, ScreeningBoundary boundary)
    {
        if (balance.Moment != BalanceMoment.BeforeScreening)
        {
            return Result<RiskFeatureSnapshot, RiskFeatureError>.Failure(RiskFeatureError.BalanceNotPreScreening);
        }
        if (balance.AsOfFactSeq > boundary.DecisionBoundaryFactSeq)
        {
            return Result<RiskFeatureSnapshot, RiskFeatureError>.Failure(RiskFeatureError.BalanceAfterDecisionBoundary);
        }
        return Build(currency, amountMinor, balance.PostedMinor);
    }

    /// <summary>The vector as a pure function of the raw integers.</summary>
    public static RiskFeatureVector Vector(RiskFeatureRaw raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var amountMajor = (double)raw.AmountMinor / MinorUnitsPerMajor;
        var postedMajor = (double)raw.SourcePostedBeforeMinor / MinorUnitsPerMajor;
        var fraction = Math.Min(TransferFractionCap, (double)raw.AmountMinor / Math.Max(raw.SourcePostedBeforeMinor, MinorUnitsPerMajor));
        var drain = raw.SourcePostedBeforeMinor > 0 && raw.AmountMinor >= raw.SourcePostedBeforeMinor ? 1f : 0f;
        return new RiskFeatureVector(
            (float)Math.Log(1d + amountMajor),
            (float)Math.Log(1d + postedMajor),
            (float)fraction,
            drain);
    }

    /// <summary>
    /// Allow-list check for a model input schema (trainer, loaded artifact, manifest): the columns must be exactly
    /// <see cref="FeatureNames"/> in order. Returns violations; empty means compatible.
    /// </summary>
    public static IReadOnlyList<string> ValidateModelInputColumns(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var violations = new List<string>();
        foreach (var c in columns.Where(c => ForbiddenSourceFields.Contains(c)))
        {
            violations.Add($"forbidden predictor '{c}'");
        }
        if (!columns.SequenceEqual(Names, StringComparer.Ordinal))
        {
            violations.Add($"columns [{string.Join(", ", columns)}] are not exactly [{string.Join(", ", Names)}] in order");
        }
        return violations;
    }

    /// <summary>
    /// Converts a PaySim decimal amount string to minor units under the synthetic 100-minor-per-unit convention, rounding half away
    /// from zero. Rejects anything that is not a plain invariant-culture decimal (no thousands separators, exponents or signs other than -).
    /// </summary>
    public static bool TryParseMinorUnits(ReadOnlySpan<char> text, out long minor)
    {
        minor = 0;
        text = text.Trim();
        if (text.IsEmpty
            || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        if (Math.Abs(value) > 1e15m)
        {
            return false;
        }
        var scaled = decimal.Round(value * MinorUnitsPerMajor, 0, MidpointRounding.AwayFromZero);
        if (scaled < long.MinValue || scaled > long.MaxValue)
        {
            return false;
        }
        minor = (long)scaled;
        return true;
    }
}
