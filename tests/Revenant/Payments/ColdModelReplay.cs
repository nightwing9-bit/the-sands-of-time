using System.Diagnostics;
using System.Text.Json;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Projection;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Payments;

internal static class ColdModelReplay
{
    private const string Mode = "--r1-cold-replay";

    private sealed record Request(string Reader, string Tenant, Guid Run, Guid History);
    private sealed record HistoryProof(string SourceFingerprint, string CanonicalInputHash, long Inputs);
    private sealed record Reply(int Pid, bool PredictorUnavailable, bool LedgerWriteDenied, bool Reconciled, HistoryProof History,
        long Position, string StateHash, string RecordHash, byte[] State);

    public static async Task<int> Main(string[] args)
    {
        if (args is not [Mode])
        {
            return 2;
        }

        try
        {
            var request = JsonSerializer.Deserialize<Request>(Console.In.ReadToEnd())
                ?? throw new InvalidOperationException("Replay request missing.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = timeout.Token;
            await using var host = ModelAwareScreeningTests.ProjectionHost(request.Reader);
            var artifact = Path.Combine(RiskModelOptions.DefaultArtifactRoot, RiskPolicyCatalog.V1.ArtifactDirectory,
                RiskModelScorer.ArtifactFileName);
            var predictorUnavailable = host.GetService<RiskModelProvider>() is null
                && !File.Exists(artifact)
                && !File.Exists(Path.Combine(Path.GetDirectoryName(artifact)!, RiskModelScorer.ManifestFileName));
            var source = host.GetRequiredService<NpgsqlDataSource>();
            await using var privilege = source.CreateCommand("""
                SELECT NOT EXISTS (
                    SELECT FROM pg_catalog.pg_tables WHERE schemaname = 'revenant'
                       AND tablename IN ('scenario_run', 'run_cursor', 'payment', 'finance_fact', 'payment_transition',
                           'payment_command', 'account', 'ledger_group', 'ledger_entry', 'hold', 'review_case', 'audit_event', 'outbox')
                       AND (has_table_privilege(format('%I.%I', schemaname, tablename), 'INSERT,UPDATE,DELETE,TRUNCATE')
                           OR has_any_column_privilege(format('%I.%I', schemaname, tablename), 'INSERT,UPDATE'))
                )
                """);
            var ledgerWriteDenied = (bool)(await privilege.ExecuteScalarAsync(ct))!;
            if (!predictorUnavailable || !ledgerWriteDenied)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Result = "FAIL", predictorUnavailable, ledgerWriteDenied }));
                return 1;
            }

            var run = new ScenarioRunId(request.Run);
            var history = new TimelineId(request.History);
            var verified = (await host.GetRequiredService<ModelLedgerProjector>().VerifySourceAsync(request.Tenant, run, ct)).Value;
            var engine = host.GetRequiredService<ITimelineEngine<ModelLedgerState, FinanceFact, ModelLedgerContext>>();
            var replay = (await engine.ReplayAsync(history, ct)).Value;
            var proof = await ReadHistoryAsync(source, history, ct);
            var reconciled = verified.Reconciled && verified.Projection.History == history && replay.Status == HistoryStatus.Verified;
            var reply = new Reply(Environment.ProcessId, predictorUnavailable, ledgerWriteDenied, reconciled, proof,
                replay.FinalPosition, replay.FinalStateHash, replay.LastRecordHash, ModelLedgerStateCodec.Instance.EncodeCanonical(replay.FinalState));
            Console.WriteLine(JsonSerializer.Serialize(reply));
            return reconciled ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Credentials are supplied on stdin and are never emitted, even on failure.
            Console.WriteLine(JsonSerializer.Serialize(new { Result = "FAIL", Error = ex.GetType().Name, SqlState = (ex as PostgresException)?.SqlState }));
            return 1;
        }
    }

    public static async Task VerifyInChildAsync(string reader, string tenant, ScenarioRunId run,
        VerifiedReplay<ModelLedgerState> original, ITestOutputHelper output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        await using var source = NpgsqlDataSource.Create(reader);
        var before = await ReadHistoryAsync(source, original.Timeline, ct);
        var isolated = Directory.CreateTempSubdirectory("r1-cold-replay-");
        try
        {
            // Copy only top-level managed output. The child's base directory has no artifact or native ML runtime.
            foreach (var file in new DirectoryInfo(AppContext.BaseDirectory).EnumerateFiles())
            {
                if (file.Extension is ".dll" or ".json")
                {
                    file.CopyTo(Path.Combine(isolated.FullName, file.Name));
                }
            }
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = isolated.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Path.Combine(isolated.FullName, typeof(ColdModelReplay).Assembly.GetName().Name + ".dll"));
            start.ArgumentList.Add(Mode);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Replay child did not start.");
            try
            {
                var stdout = child.StandardOutput.ReadToEndAsync(ct);
                var stderr = child.StandardError.ReadToEndAsync(ct);
                await child.StandardInput.WriteAsync(JsonSerializer.Serialize(new Request(reader, tenant, run.Value, original.Timeline.Value)).AsMemory(), ct);
                child.StandardInput.Close();
                await child.WaitForExitAsync(ct);
                var json = await stdout;
                var diagnostic = await stderr;
                Assert.True(child.ExitCode == 0, $"Replay child exited {child.ExitCode}. Output: {json}. Stderr: {diagnostic}");
                var replay = JsonSerializer.Deserialize<Reply>(json)!;
                Assert.NotEqual(Environment.ProcessId, replay.Pid);
                Assert.True(replay.PredictorUnavailable && replay.LedgerWriteDenied && replay.Reconciled);
                Assert.Equal(original.FinalPosition, before.Inputs);
                Assert.Equal(before, replay.History);
                Assert.Equal(before, await ReadHistoryAsync(source, original.Timeline, ct));
                Assert.Equal((original.FinalPosition, original.FinalStateHash, original.LastRecordHash),
                    (replay.Position, replay.StateHash, replay.RecordHash));
                Assert.Equal(ModelLedgerStateCodec.Instance.EncodeCanonical(original.FinalState), replay.State);
                output.WriteLine($"Cold V3 replay child {replay.Pid}, run {run}, history {original.Timeline.Value}, " +
                    $"position {replay.Position}, state {replay.StateHash}, chain {replay.RecordHash}, source fingerprint {before.SourceFingerprint}, " +
                    $"canonical input hash {before.CanonicalInputHash}. Predictor unavailable, ledger writes denied, reconciliation passed.");
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
        }
        finally
        {
            isolated.Delete(recursive: true);
        }
    }

    private static async Task<HistoryProof> ReadHistoryAsync(NpgsqlDataSource source, TimelineId history, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT h.request_fingerprint, a.canonical_bytes, i.position, i.input_index, i.payload
              FROM marrow_timeline.histories h
              JOIN marrow_timeline.context_artifacts a ON a.context_ref = h.context_ref
              JOIN marrow_timeline.step_inputs i ON i.timeline_id = h.timeline_id
             WHERE h.timeline_id = @history
             ORDER BY i.position, i.input_index
            """);
        command.Parameters.AddWithValue("history", history.Value);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
        {
            throw new InvalidOperationException("Verified history has no inputs.");
        }
        var fingerprint = rows.GetString(0);
        var canonical = new CanonicalWriter().WriteBytes(rows.GetFieldValue<byte[]>(1));
        long inputs = 0;
        do
        {
            var position = rows.GetInt64(2);
            var index = rows.GetInt32(3);
            if (position != ++inputs || index != 0)
            {
                throw new InvalidOperationException("Expected one canonical finance fact per contiguous Timeline position.");
            }
            canonical.WriteInt64(position).WriteBytes(rows.GetFieldValue<byte[]>(4));
        }
        while (await rows.ReadAsync(ct));
        return new HistoryProof(fingerprint, RiskModelManifestCodec.Sha256Hex(canonical.ToArray()), inputs);
    }
}
