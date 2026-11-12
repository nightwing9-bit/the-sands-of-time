using Marrow.Kernel.Canonical;
using Revenant.Finance;
using Revenant.Observations;
using Revenant.Synthetic;
using Revenant.Tests.Observations;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Synthetic;

public sealed class V4RiskDatasetReaderTests
{
    [Fact]
    public void Canonical_export_round_trips_and_a_truncated_or_unpinned_stream_is_rejected()
    {
        var config = V4EpisodeConfig.RiskSmoke;
        var group = Assert.Single(config.Roots());
        var design = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
        var fixture = new ObservationFixture(design.Financial.Opening);
        var outcomes = new List<V4AttemptOutcome>();
        foreach (var planned in design.Financial.Payments)
        {
            var p = planned.Intent;
            var at = p.EventTimeUtcMs;
            while (ObservedFinanceVersion.NextBoundary(fixture.State.LastSealedBoundaryMs) <= at) fixture.Seal();
            fixture.Intake(p);
            fixture.Screen(p, at);
            var screen = fixture.State.Ledger.Payments[p.IntentId].Status;
            string? settlement = null;
            string? returned = null;
            if (screen == PaymentStatus.Cleared)
            {
                fixture.Settle(p, at);
                var settled = fixture.State.Ledger.Payments[p.IntentId];
                settlement = settled.Status.ToString();
                if (settled.SettlementPosted && planned.RequestReturn)
                {
                    fixture.Return(p, at);
                    returned = fixture.State.Ledger.Payments[p.IntentId].Status.ToString();
                }
            }
            outcomes.Add(new(p.IntentId, nameof(PaymentStatus.Initiated), screen.ToString(), settlement, returned));
        }
        while (fixture.State.LastSealedBoundaryMs < design.Financial.EndMs) fixture.Seal();
        var history = fixture.History();
        var projection = V4ObservationProjector.Project(history, captureRisk: true);
        var directory = Directory.CreateTempSubdirectory("r5-reader-");
        try
        {
            V4DatasetManifest manifest;
            using (var exporter = new V4DatasetExporter(config, new string('0', 40), directory.FullName, writeTables: true))
            {
                exporter.Add(design, new(history, [.. outcomes], new(TimeSpan.Zero, TimeSpan.Zero, [])), projection);
                manifest = exporter.Complete();
            }
            var reader = new V4RiskDatasetReader(directory.FullName, manifest.Sha256);
            var rows = reader.ReadSplit(group.Split);
            var expected = projection.RiskAsOf.Where(r => r.EffectiveTimeMs - fixture.Origin >= V4EpisodeConfig.WarmupMs);
            Assert.Equal(expected.Select(x => x.Vector.Digest), rows.Select(x => x.Vector.Digest));
            Assert.All(rows, x => Assert.All(x.AsOf.Windows, w => Assert.Equal(ObservationCoverage.Complete, w.Coverage)));
            Assert.Throws<CanonicalFormatException>(() => new V4RiskDatasetReader(directory.FullName, new string('0', 64)));
            var bytes = Path.Combine(directory.FullName, "Train-risk-asof.bin");
            using (var file = File.OpenWrite(bytes)) file.SetLength(file.Length - 1);
            Assert.Throws<CanonicalFormatException>(() => reader.ReadSplit(group.Split));

            // Exercise budget arithmetic on a controlled outcome mix. The actual v7 result is read above.
            var reviewRows = rows.Take(20).Select((r, i) => r with
            { Screening = i switch { 0 => nameof(PaymentStatus.Held), 1 => nameof(PaymentStatus.Rejected), _ => nameof(PaymentStatus.Cleared) } }).ToArray();
            var primary = V4RiskReviewBudget.Evaluate(reviewRows, 5, true, _ => 1);
            Assert.Equal(1, primary.Slots);
            Assert.Equal(0, primary.Additional);
            Assert.Equal(reviewRows[0], Assert.Single(primary.Selected));
            Assert.True(V4RiskReviewBudget.Evaluate(reviewRows, 2, true, _ => 1).Overloaded);
            var sensitivity = V4RiskReviewBudget.Evaluate(reviewRows, 10, true, _ => 1);
            Assert.Equal(new[] { reviewRows[0], reviewRows[2] }, sensitivity.Selected);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
