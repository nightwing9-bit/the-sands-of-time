namespace Revenant.Contracts;

/// <summary>
/// Currency code plus integer minor units. No floating point, no implicit conversion between currencies.
/// The Friday Ring scenario uses USD only.
/// </summary>
public readonly record struct Money
{
    public Money(string currency, long minorUnits)
    {
        if (currency is null || currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException("Currency must be a three-letter upper-case code.", nameof(currency));
        }

        Currency = currency;
        MinorUnits = minorUnits;
    }

    public string Currency { get; }

    public long MinorUnits { get; }

    public static Money Usd(long minorUnits) => new("USD", minorUnits);

    public override string ToString() => $"{Currency} {MinorUnits} minor";
}
