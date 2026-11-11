using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Revenant.Intelligence;

public enum RiskSplit : byte
{
    Train = 1,
    Validation = 2,
    Test = 3,
}

/// <summary>Schema-level ETL failure (unusable header, too few steps, a class missing from a split). Row-level problems are counted instead.</summary>
public sealed class RiskEtlException(string message) : Exception(message);

/// <summary>Row validation and split accounting. Every input row is counted exactly once in <see cref="RowsRead"/>.</summary>
public sealed record RiskEtlCounters
{
    public long RowsRead { get; init; }

    /// <summary>Valid rows of another PaySim type (CASH_OUT, PAYMENT, ...): outside the transfer-only applicability, not an error.</summary>
    public long FilteredNonTransfer { get; init; }

    public long RejectedFieldCount { get; init; }
    public long RejectedIdentifier { get; init; }
    public long RejectedStep { get; init; }
    public long RejectedAmountFormat { get; init; }
    public long RejectedAmountNotPositive { get; init; }
    public long RejectedBalance { get; init; }
    public long RejectedLabel { get; init; }
    public long RejectedFeature { get; init; }
    public long RejectedDuplicate { get; init; }

    /// <summary>Valid transfer rows that passed all checks before the group purge.</summary>
    public long Eligible { get; init; }

    /// <summary>Eligible rows dropped because their source account or scenario already belongs to an earlier cohort.</summary>
    public long PurgedOverlap { get; init; }
}

public sealed record RiskSplitSummary(string Split, long Rows, long Positives, long Negatives, int FirstStep, int LastStep);

/// <summary>Reproducible, timestamp-free description of one ETL run. <see cref="ManifestSha256"/> covers every other field.</summary>
public sealed record RiskDatasetManifest(
    string ManifestSchema,
    string SourceKind,
    string SourceName,
    string SourceSha256,
    long SourceBytes,
    string SourceLicenseNote,
    string FeatureSchemaId,
    string[] FeatureNames,
    string AmountConvention,
    string EtlVersion,
    string SplitMethod,
    RiskEtlCounters Counters,
    int DistinctObservedSteps,
    RiskSplitSummary[] Splits,
    string SplitExamplesSha256,
    bool MetricsEligible,
    string[] Limitations,
    string ManifestSha256);

public sealed record RiskEtlResult(RiskDatasetManifest Manifest, string ManifestJson);

/// <summary>Where the rows come from; recorded verbatim in the manifest.</summary>
public sealed record RiskSourceDescription(string Kind, string Name, string LicenseNote, string[] Limitations);

/// <summary>
/// Deterministic, bounded-memory ETL for PaySim-shaped CSV (design.md section 2). Memory is O(distinct steps + distinct source
/// accounts/scenarios), never O(rows): the source is read three times (hash, scan, emit). Only the features of
/// <see cref="RiskFeaturesV1"/> and the evaluation label leave the ETL; names, steps beyond the split, balances after the payment
/// and <c>isFlaggedFraud</c> are read only to validate or to place the row, never to describe it.
/// <para>
/// Split: chronological windows over the OBSERVED DISTINCT steps (60 / 20 / 20, counts rounded half away from zero, so the
/// train window is the earliest). Purge: a source account (and a scenario id when the column exists) belongs to the cohort of its
/// earliest step; its rows in any later cohort are dropped and counted. Destination accounts are not features and are not purged.
/// </para>
/// </summary>
public static class RiskDatasetEtl
{
    public const string ManifestSchema = "risk-dataset-manifest/1";
    public const string EtlVersion = "risk-etl/1";
    public const string SplitMethod = "chronological-observed-steps-60-20-20; purged-groups(source account, scenario id)";
    public const string AmountConvention = "1 PaySim nominal unit = 1 synthetic USD major unit = 100 minor units; decimal, round half away from zero; not a currency conversion";
    public const string TransferType = "TRANSFER";

    private static readonly string[] RequiredColumns =
    [
        "step", "type", "amount", "nameOrig", "oldbalanceOrg", "newbalanceOrig", "nameDest", "oldbalanceDest", "newbalanceDest", "isFraud", "isFlaggedFraud",
    ];

    private const string OptionalScenarioColumn = "scenarioId";

    /// <summary>Runs the ETL. <paramref name="examplesOut"/> optionally receives the processed split file (tab-separated, no identifiers).</summary>
    public static RiskEtlResult Run(
        Func<Stream> openSource, RiskSourceDescription source, TextWriter? examplesOut = null, bool requireBothClasses = true)
    {
        ArgumentNullException.ThrowIfNull(openSource);
        ArgumentNullException.ThrowIfNull(source);

        string sourceHash;
        long sourceBytes;
        using (var s = openSource())
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            sourceBytes = 0;
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                sourceBytes += n;
            }
            sourceHash = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        // Pass 1: validate, learn the observed steps and the earliest step of every source account / scenario.
        var steps = new SortedDictionary<int, long>();
        var firstAccountStep = new Dictionary<ulong, int>();
        var firstScenarioStep = new Dictionary<ulong, int>();
        RiskEtlCounters scanCounters;
        using (var s = openSource())
        {
            scanCounters = ReadRows(s, row =>
            {
                steps[row.Step] = steps.GetValueOrDefault(row.Step) + 1;
                Track(firstAccountStep, row.AccountKey, row.Step);
                if (row.ScenarioKey is { } scn)
                {
                    Track(firstScenarioStep, scn, row.Step);
                }
            });
        }
        if (steps.Count < 5)
        {
            throw new RiskEtlException($"Only {steps.Count} distinct observed steps; a 60/20/20 chronological split needs at least 5.");
        }

        var ordered = steps.Keys.ToArray();
        var trainEnd = (int)Math.Round(ordered.Length * 0.6, MidpointRounding.AwayFromZero);
        var validationEnd = (int)Math.Round(ordered.Length * 0.8, MidpointRounding.AwayFromZero);
        var stepIndex = new Dictionary<int, int>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            stepIndex[ordered[i]] = i;
        }
        RiskSplit CohortOf(int step) => stepIndex[step] < trainEnd ? RiskSplit.Train : stepIndex[step] < validationEnd ? RiskSplit.Validation : RiskSplit.Test;

        // Pass 2: place, purge and emit.
        var rows = new long[4];
        var positives = new long[4];
        var firstStep = new int[4];
        var lastStep = new int[4];
        long purged = 0;
        using var examplesHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Emit(string line)
        {
            examplesHash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
            examplesOut?.Write(line + "\n");
        }
        Emit("split\tordinal\tstep\tlabel\tcurrency\tamountMinor\tsourcePostedBeforeMinor");
        RiskEtlCounters emitCounters;
        using (var s = openSource())
        {
            emitCounters = ReadRows(s, row =>
            {
                var cohort = CohortOf(row.Step);
                var accountOwner = CohortOf(firstAccountStep[row.AccountKey]);
                var scenarioOwner = row.ScenarioKey is { } scn ? CohortOf(firstScenarioStep[scn]) : cohort;
                if (accountOwner != cohort || scenarioOwner != cohort)
                {
                    purged++;
                    return;
                }
                var c = (int)cohort;
                rows[c]++;
                positives[c] += row.IsFraud ? 1 : 0;
                firstStep[c] = rows[c] == 1 ? row.Step : Math.Min(firstStep[c], row.Step);
                lastStep[c] = Math.Max(lastStep[c], row.Step);
                Emit(string.Join('\t', cohort.ToString(), row.Ordinal.ToString(CultureInfo.InvariantCulture), row.Step.ToString(CultureInfo.InvariantCulture),
                    row.IsFraud ? "1" : "0", row.Raw.Currency, row.Raw.AmountMinor.ToString(CultureInfo.InvariantCulture),
                    row.Raw.SourcePostedBeforeMinor.ToString(CultureInfo.InvariantCulture)));
            });
        }
        if (emitCounters != scanCounters)
        {
            throw new RiskEtlException("The source changed between ETL passes (counters differ).");
        }
        var counters = emitCounters with { PurgedOverlap = purged };

        var splits = new[] { RiskSplit.Train, RiskSplit.Validation, RiskSplit.Test }
            .Select(sp => new RiskSplitSummary(sp.ToString(), rows[(int)sp], positives[(int)sp], rows[(int)sp] - positives[(int)sp], firstStep[(int)sp], lastStep[(int)sp]))
            .ToArray();
        var bothClasses = splits.All(x => x.Positives > 0 && x.Negatives > 0);
        if (requireBothClasses && !bothClasses)
        {
            throw new RiskEtlException(
                "A split lacks one class (" + string.Join("; ", splits.Select(x => $"{x.Split}: {x.Positives} positive / {x.Negatives} negative")) +
                "). Metrics must not be claimed; use the seeded fixture instead of relaxing the purge.");
        }

        var unsigned = new RiskDatasetManifest(
            ManifestSchema, source.Kind, source.Name, sourceHash, sourceBytes, source.LicenseNote, RiskFeaturesV1.SchemaId, [.. RiskFeaturesV1.FeatureNames],
            AmountConvention, EtlVersion, SplitMethod, counters, ordered.Length, splits, Convert.ToHexStringLower(examplesHash.GetHashAndReset()),
            bothClasses, source.Limitations, string.Empty);
        var body = JsonSerializer.Serialize(unsigned);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var manifest = unsigned with { ManifestSha256 = hash };
        return new RiskEtlResult(manifest, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Recomputes the manifest hash exactly as <see cref="Run"/> does (hash field blank), to detect tampering.</summary>
    public static bool ManifestHashMatches(RiskDatasetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var body = JsonSerializer.Serialize(manifest with { ManifestSha256 = string.Empty });
        return string.Equals(manifest.ManifestSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), StringComparison.Ordinal);
    }

    private static void Track(Dictionary<ulong, int> first, ulong key, int step)
    {
        if (!first.TryGetValue(key, out var existing) || step < existing)
        {
            first[key] = step;
        }
    }

    private readonly record struct ParsedRow(long Ordinal, int Step, ulong AccountKey, ulong? ScenarioKey, RiskFeatureRaw Raw, bool IsFraud);

    /// <summary>Streams the CSV once, counts every row and hands each valid transfer to <paramref name="onRow"/>.</summary>
    private static RiskEtlCounters ReadRows(Stream stream, Action<ParsedRow> onRow)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);
        var headerLine = reader.ReadLine() ?? throw new RiskEtlException("The source is empty: no header line.");
        var header = headerLine.Split(',').Select(h => h.Trim()).ToArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Length; i++)
        {
            if (!index.TryAdd(header[i], i))
            {
                throw new RiskEtlException($"Duplicate header column '{header[i]}'.");
            }
        }
        var missing = RequiredColumns.Where(c => !index.ContainsKey(c)).ToArray();
        if (missing.Length > 0)
        {
            throw new RiskEtlException($"Missing required column(s): {string.Join(", ", missing)}.");
        }
        int iStep = index["step"], iType = index["type"], iAmount = index["amount"], iOrig = index["nameOrig"], iDest = index["nameDest"],
            iOld = index["oldbalanceOrg"], iFraud = index["isFraud"];
        var iScenario = index.GetValueOrDefault(OptionalScenarioColumn, -1);

        var seen = new HashSet<ulong>();
        long read = 0, nonTransfer = 0, fieldCount = 0, ident = 0, stepBad = 0, amountFormat = 0, amountNotPositive = 0, balance = 0, label = 0,
            feature = 0, duplicate = 0, eligible = 0, ordinal = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }
            ordinal++;
            read++;
            var f = line.Split(',');
            if (f.Length != header.Length)
            {
                fieldCount++;
                continue;
            }
            if (!string.Equals(f[iType].Trim(), TransferType, StringComparison.OrdinalIgnoreCase))
            {
                nonTransfer++;
                continue;
            }
            var orig = f[iOrig].Trim();
            var dest = f[iDest].Trim();
            if (orig.Length == 0 || dest.Length == 0)
            {
                ident++;
                continue;
            }
            if (!int.TryParse(f[iStep].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var step))
            {
                stepBad++;
                continue;
            }
            if (!RiskFeaturesV1.TryParseMinorUnits(f[iAmount], out var amountMinor))
            {
                amountFormat++;
                continue;
            }
            if (amountMinor <= 0)
            {
                amountNotPositive++;
                continue;
            }
            if (!RiskFeaturesV1.TryParseMinorUnits(f[iOld], out var oldMinor) || oldMinor < 0)
            {
                balance++;
                continue;
            }
            var labelText = f[iFraud].Trim();
            if (labelText is not ("0" or "1"))
            {
                label++;
                continue;
            }
            var built = RiskFeaturesV1.Build(RiskFeaturesV1.RequiredCurrency, amountMinor, oldMinor);
            if (built.IsFailure)
            {
                feature++;
                continue;
            }
            var rowKey = Hash64($"{step}|{orig}|{dest}|{amountMinor}|{oldMinor}");
            if (!seen.Add(rowKey))
            {
                duplicate++;
                continue;
            }
            eligible++;
            var scenario = iScenario >= 0 && f[iScenario].Trim() is { Length: > 0 } scn ? Hash64("scenario:" + scn) : (ulong?)null;
            onRow(new ParsedRow(ordinal, step, Hash64("account:" + orig), scenario, built.Value.Raw, labelText == "1"));
        }
        return new RiskEtlCounters
        {
            RowsRead = read,
            FilteredNonTransfer = nonTransfer,
            RejectedFieldCount = fieldCount,
            RejectedIdentifier = ident,
            RejectedStep = stepBad,
            RejectedAmountFormat = amountFormat,
            RejectedAmountNotPositive = amountNotPositive,
            RejectedBalance = balance,
            RejectedLabel = label,
            RejectedFeature = feature,
            RejectedDuplicate = duplicate,
            Eligible = eligible,
        };
    }

    private static ulong Hash64(string text) => BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
}
