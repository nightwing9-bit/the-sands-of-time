using System.Security.Cryptography;
using System.Text;
using Revenant.Intelligence;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Intelligence;

public sealed class RiskDatasetEtlTests(ITestOutputHelper output)
{
    private const string Header = "step,type,amount,nameOrig,oldbalanceOrg,newbalanceOrig,nameDest,oldbalanceDest,newbalanceDest,isFraud,isFlaggedFraud,scenarioId";

    private static readonly RiskSourceDescription TestSource = new("test", "inline", "none", ["test data"]);

    private static string Row(string step, string type, string amount, string orig, string old, string dest = "D1", string fraud = "0", string scenario = "")
        => $"{step},{type},{amount},{orig},{old},0.00,{dest},0.00,0.00,{fraud},0,{scenario}";

    /// <summary>10 observed steps -> train steps 1-6, validation 7-8, test 9-10; one distinct account per step.</summary>
    private static List<string> BaseRows() =>
        [.. Enumerable.Range(1, 10).Select(i => Row(i.ToString(), "TRANSFER", $"{100 + i}.00", $"A{i}", "1000.00", fraud: (i % 2).ToString()))];

    private static Func<Stream> Source(IEnumerable<string> rows, string header = Header)
    {
        var bytes = Encoding.UTF8.GetBytes(header + "\n" + string.Join('\n', rows) + "\n");
        return () => new MemoryStream(bytes);
    }

    private static (RiskEtlResult Result, string[] Lines) RunWithOutput(Func<Stream> source, bool requireBoth = false, RiskSourceDescription? description = null)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        var result = RiskDatasetEtl.Run(source, description ?? TestSource, writer, requireBoth);
        return (result, writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Paysim_shaped_row_and_live_like_facts_give_identical_canonical_features()
    {
        var rows = BaseRows();
        rows[3] = Row("4", " transfer ", "1500.00", "A4", "3000.00"); // lower case and padding are accepted
        var (_, lines) = RunWithOutput(Source(rows));
        var cols = lines.Single(l => l.Split('\t')[1] == "4").Split('\t'); // ordinal 4 is the fourth data row
        var etl = RiskFeaturesV1.Build(cols[4], long.Parse(cols[5]), long.Parse(cols[6])).Value;

        var live = RiskFeaturesV1.BuildFromLedger("USD", 150_000, new SourceBalanceObservation(300_000, BalanceMoment.BeforeScreening, 17), new ScreeningBoundary(17)).Value;

        Assert.Equal(150_000, long.Parse(cols[5]));
        Assert.Equal(300_000, long.Parse(cols[6]));
        Assert.Equal(live.Raw.ToCanonicalBytes(), etl.Raw.ToCanonicalBytes());
        Assert.Equal(live.FeatureHash, etl.FeatureHash);
        Assert.Equal(live.Vector, etl.Vector);
    }

    [Fact]
    public void Post_state_destination_and_flag_columns_cannot_change_any_example_and_the_label_changes_only_the_label()
    {
        var csv = Encoding.UTF8.GetString(RiskFixture.GenerateCsv());
        string Mutate(Func<string[], string[]> edit) => string.Join('\n', csv.Split('\n').Select((l, i) => i == 0 || l.Length == 0 ? l : string.Join(',', edit(l.Split(',')))));
        var baseline = RunWithOutput(Source(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)));

        var forbidden = Mutate(c => { c[5] = "123456789.01"; c[6] = "Z" + c[6]; c[7] = "42.00"; c[8] = "99999.99"; c[10] = "1"; return c; });
        var mutatedForbidden = RunWithOutput(Source(forbidden.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)));
        Assert.Equal(baseline.Result.Manifest.SplitExamplesSha256, mutatedForbidden.Result.Manifest.SplitExamplesSha256);
        Assert.Equal(baseline.Lines, mutatedForbidden.Lines);

        var flipped = Mutate(c => { c[9] = c[9] == "1" ? "0" : "1"; return c; });
        var mutatedLabel = RunWithOutput(Source(flipped.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)));
        Assert.Equal(baseline.Lines.Length, mutatedLabel.Lines.Length);
        foreach (var (a, b) in baseline.Lines.Zip(mutatedLabel.Lines).Skip(1))
        {
            var (x, y) = (a.Split('\t'), b.Split('\t'));
            Assert.NotEqual(x[3], y[3]);
            Assert.Equal(x[4..], y[4..]);
            Assert.Equal(x[..3], y[..3]);
        }
    }

    [Fact]
    public void An_account_or_scenario_that_straddles_cohorts_is_purged_from_the_later_cohort()
    {
        var rows = BaseRows();
        rows.Add(Row("9", "TRANSFER", "55.00", "A2", "800.00"));                       // account A2 first seen at step 2 (train)
        rows.Add(Row("3", "TRANSFER", "70.00", "B1", "800.00", scenario: "S1"));        // scenario S1 starts in train
        rows.Add(Row("8", "TRANSFER", "71.00", "B2", "800.00", scenario: "S1"));        // different account, same scenario, validation
        var (result, lines) = RunWithOutput(Source(rows));

        var c = result.Manifest.Counters;
        Assert.Equal(13, c.Eligible);
        Assert.Equal(2, c.PurgedOverlap);
        Assert.Equal(11, lines.Length - 1);
        var split = result.Manifest.Splits.ToDictionary(s => s.Split);
        Assert.Equal(7, split["Train"].Rows);          // A1..A6 and B1 (the first row of scenario S1)
        Assert.Equal(2, split["Validation"].Rows);     // A7, A8 (B2 purged)
        Assert.Equal(2, split["Test"].Rows);           // A9, A10 (second A2 row purged)
    }

    [Fact]
    public void Malformed_rows_are_counted_once_each_and_reconcile()
    {
        var rows = BaseRows();
        rows.AddRange(
        [
            "1,TRANSFER,5",                                                     // field count
            Row("1", "TRANSFER", "abc", "Z1", "100.00"),                        // amount format
            Row("1", "TRANSFER", "0.00", "Z2", "100.00"),                       // amount zero
            Row("1", "TRANSFER", "-5.00", "Z3", "100.00"),                      // amount negative
            Row("1", "TRANSFER", "5.00", "Z4", "-1.00"),                        // negative prebalance
            Row("1", "TRANSFER", "5.00", "Z5", "100.00", fraud: "2"),           // label
            Row("x", "TRANSFER", "5.00", "Z6", "100.00"),                       // step text
            Row("-1", "TRANSFER", "5.00", "Z7", "100.00"),                      // step sign
            Row("1", "TRANSFER", "5.00", "", "100.00"),                         // missing identifier
            Row("1", "CASH_OUT", "5.00", "Z8", "100.00"),                       // other type: filtered, not an error
            Row("1", "TRANSFER", "99999999999999999.00", "Z9", "100.00"),       // out-of-range amount
            rows[0],                                                             // exact duplicate
        ]);
        var (result, _) = RunWithOutput(Source(rows));
        var c = result.Manifest.Counters;
        Assert.Equal(1, c.RejectedFieldCount);
        Assert.Equal(2, c.RejectedAmountFormat);          // "abc" and the out-of-range amount
        Assert.Equal(2, c.RejectedAmountNotPositive);
        Assert.Equal(1, c.RejectedBalance);
        Assert.Equal(1, c.RejectedLabel);
        Assert.Equal(2, c.RejectedStep);
        Assert.Equal(1, c.RejectedIdentifier);
        Assert.Equal(1, c.FilteredNonTransfer);
        Assert.Equal(1, c.RejectedDuplicate);
        Assert.Equal(10, c.Eligible);
        Assert.Equal(rows.Count, c.RowsRead);
        Assert.Equal(c.RowsRead, c.FilteredNonTransfer + c.RejectedFieldCount + c.RejectedIdentifier + c.RejectedStep + c.RejectedAmountFormat
            + c.RejectedAmountNotPositive + c.RejectedBalance + c.RejectedLabel + c.RejectedFeature + c.RejectedDuplicate + c.Eligible);
    }

    [Fact]
    public void Schema_level_problems_fail_visibly()
    {
        Assert.Contains("Missing required column", Assert.Throws<RiskEtlException>(() =>
            RiskDatasetEtl.Run(Source(BaseRows(), Header.Replace("oldbalanceOrg", "oldBalance")), TestSource)).Message);
        Assert.Contains("Duplicate header", Assert.Throws<RiskEtlException>(() =>
            RiskDatasetEtl.Run(Source(BaseRows(), Header + ",step"), TestSource)).Message);
        Assert.Contains("distinct observed steps", Assert.Throws<RiskEtlException>(() =>
            RiskDatasetEtl.Run(Source(BaseRows().Take(3)), TestSource)).Message);
        Assert.Contains("lacks one class", Assert.Throws<RiskEtlException>(() =>
            RiskDatasetEtl.Run(Source(BaseRows().Select(r => r.Replace(",1,0,", ",0,0,"))), TestSource, requireBothClasses: true)).Message);
        Assert.Throws<RiskEtlException>(() => RiskDatasetEtl.Run(() => new MemoryStream([]), TestSource));
    }

    [Fact]
    public void Fixture_has_authentic_labels_and_both_classes_in_every_split()
    {
        var bytes = RiskFixture.GenerateCsv();
        var (result, lines) = RunWithOutput(() => new MemoryStream(bytes), requireBoth: true, RiskFixture.Description);
        var m = result.Manifest;
        output.WriteLine(result.ManifestJson);

        Assert.True(m.MetricsEligible);
        Assert.Equal("project-synthetic-fixture", m.SourceKind);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), m.SourceSha256);
        Assert.Equal(bytes.Length, m.SourceBytes);
        foreach (var s in m.Splits)
        {
            Assert.True(s.Positives >= 15, $"{s.Split} positives {s.Positives}");
            Assert.True(s.Negatives >= 150, $"{s.Split} negatives {s.Negatives}");
        }
        Assert.True(m.Splits[0].LastStep < m.Splits[1].FirstStep && m.Splits[1].LastStep < m.Splits[2].FirstStep, "windows must be chronological and disjoint");
        Assert.True(m.Counters.FilteredNonTransfer > 0 && m.Counters.PurgedOverlap > 0);
        Assert.Equal(m.Counters.Eligible - m.Counters.PurgedOverlap, m.Splits.Sum(s => s.Rows));
        Assert.Equal(m.Splits.Sum(s => s.Rows) + 1, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.Equal(7, l.Split('\t').Length));
        Assert.All(lines.Skip(1), l =>
        {
            var c = l.Split('\t'); // split, ordinal, step, label, currency, amountMinor, postedBeforeMinor: no account, scenario or counterparty text
            Assert.Contains(c[0], new[] { "Train", "Validation", "Test" });
            Assert.True(long.TryParse(c[1], out _) && long.TryParse(c[2], out _) && long.TryParse(c[5], out _) && long.TryParse(c[6], out _));
            Assert.Contains(c[3], new[] { "0", "1" });
            Assert.Equal("USD", c[4]);
        });
        Assert.Contains(m.Limitations, l => l.Contains("Not a Friday Ring evaluation", StringComparison.Ordinal));
        Assert.Contains(m.Limitations, l => l.StartsWith("Project synthetic fixture", StringComparison.Ordinal));
    }

    [Fact]
    public void No_account_or_scenario_appears_in_two_cohorts_in_the_emitted_split()
    {
        var bytes = RiskFixture.GenerateCsv();
        var dataLines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        var (_, lines) = RunWithOutput(() => new MemoryStream(bytes), requireBoth: true);
        var cohortsByAccount = new Dictionary<string, HashSet<string>>();
        var cohortsByScenario = new Dictionary<string, HashSet<string>>();
        foreach (var l in lines.Skip(1))
        {
            var c = l.Split('\t');
            var src = dataLines[int.Parse(c[1]) - 1].Split(',');
            Assert.Equal("TRANSFER", src[1]);
            Assert.Equal(c[3], src[9]);
            cohortsByAccount.TryAdd(src[3], []);
            cohortsByAccount[src[3]].Add(c[0]);
            cohortsByScenario.TryAdd(src[11], []);
            cohortsByScenario[src[11]].Add(c[0]);
        }
        Assert.All(cohortsByAccount.Values, s => Assert.Single(s));
        Assert.All(cohortsByScenario.Values, s => Assert.Single(s));
    }

    [Fact]
    public void Source_split_and_manifest_hashes_are_reproducible_and_tamper_evident()
    {
        var a = RiskDatasetEtl.Run(() => new MemoryStream(RiskFixture.GenerateCsv()), RiskFixture.Description);
        var b = RiskDatasetEtl.Run(() => new MemoryStream(RiskFixture.GenerateCsv()), RiskFixture.Description);
        var other = RiskDatasetEtl.Run(() => new MemoryStream(RiskFixture.GenerateCsv(RiskFixture.DefaultSeed + 1)), RiskFixture.Description);
        output.WriteLine($"source {a.Manifest.SourceSha256}\nsplit  {a.Manifest.SplitExamplesSha256}\nmanifest {a.Manifest.ManifestSha256}");

        Assert.Equal(a.ManifestJson, b.ManifestJson);
        Assert.True(RiskDatasetEtl.ManifestHashMatches(a.Manifest));
        Assert.NotEqual(a.Manifest.SourceSha256, other.Manifest.SourceSha256);
        Assert.NotEqual(a.Manifest.SplitExamplesSha256, other.Manifest.SplitExamplesSha256);
        Assert.False(RiskDatasetEtl.ManifestHashMatches(a.Manifest with { MetricsEligible = false }));
        Assert.False(RiskDatasetEtl.ManifestHashMatches(a.Manifest with { SourceSha256 = new string('0', 64) }));
        // Regression pins copied from this implementation's own output (not independent oracles).
        Assert.Equal(PinnedSourceSha256, a.Manifest.SourceSha256);
        Assert.Equal(PinnedSplitSha256, a.Manifest.SplitExamplesSha256);
        Assert.Equal(PinnedManifestSha256, a.Manifest.ManifestSha256);
    }

    private const string PinnedSourceSha256 = "52602588079433070176616ca134dcdcb503b117ee712a3baddc6f05a7704623";
    private const string PinnedSplitSha256 = "699185eca229522fde23fd0aa026d6e356e709c2a0b6f807b4c6384face537a5";
    private const string PinnedManifestSha256 = "3a770fbc04633c7898769ddd8e62218157b30bc173fd6132e945a7c5855a9a75";
}
