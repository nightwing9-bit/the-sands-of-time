using System.Collections.Immutable;
using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;

namespace Revenant.Scenario;

/// <summary>Canonical bytes and content hashes for the run-independent scenario manifest.</summary>
public static class ScenarioManifestCodec
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Canonical bytes of every manifest field except <see cref="ScenarioManifest.ContentHash"/>.</summary>
    public static byte[] EncodeContent(ScenarioManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var writer = new CanonicalWriter()
            .WriteString(manifest.ScenarioId)
            .WriteString(manifest.ScenarioVersion)
            .WriteString(manifest.InputSchemaVersion)
            .WriteString(manifest.FeatureSchemaVersion);
        WriteStrings(writer, manifest.FeatureNames);
        writer.WriteString(manifest.LabelSchemaVersion);
        WriteStrings(writer, manifest.LabelNames);
        writer.WriteInt64(manifest.Seed).WriteInt64(manifest.StartUtcMs).WriteCount(manifest.Banks.Length);
        foreach (var bank in manifest.Banks)
        {
            writer.WriteString(bank.BankId).WriteString(bank.Name);
            WriteMoney(writer, bank.OpeningBalance);
        }

        writer.WriteCount(manifest.Accounts.Length);
        foreach (var account in manifest.Accounts)
        {
            writer.WriteString(account.AccountId).WriteString(account.BankId);
            WriteMoney(writer, account.OpeningBalance);
        }

        return writer.ToArray();
    }

    public static string ComputeContentHash(ScenarioManifest manifest) => Sha256Hex(EncodeContent(manifest));

    public static bool HasValidContentHash(ScenarioManifest manifest) =>
        string.Equals(manifest.ContentHash, ComputeContentHash(manifest), StringComparison.Ordinal);

    /// <summary>
    /// Hash of the namespace-free exogenous facts: the manifest content hash plus every intent without its
    /// run id, in stream order.
    /// </summary>
    public static string ComputeFactsHash(ScenarioManifest manifest, IReadOnlyList<PaymentIntent> intents)
    {
        var writer = new CanonicalWriter().WriteString(manifest.ContentHash).WriteCount(intents.Count);
        foreach (var intent in intents)
        {
            writer.WriteBytes(PaymentIntentCodec.EncodeFacts(intent));
        }

        return Sha256Hex(writer.ToArray());
    }

    private static void WriteMoney(CanonicalWriter writer, Money money) =>
        writer.WriteString(money.Currency).WriteInt64(money.MinorUnits);

    private static void WriteStrings(CanonicalWriter writer, ImmutableArray<string> values)
    {
        writer.WriteCount(values.Length);
        foreach (var value in values)
        {
            writer.WriteString(value);
        }
    }
}
