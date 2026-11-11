using Marrow.Kernel.Realtime;
using Marrow.Kernel.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Revenant.Contracts;
using Revenant.Payments;

namespace Revenant.Api;

/// <summary>
/// The Revenant run stream on the shared realtime hub: stream id <c>revenant-run:{runId:N}</c>. Access is the same
/// <see cref="RevenantStaffAuthorizer"/> decision as reading the run over the API (tenant-bearing staff, current run membership),
/// so the hub's subscribe check and its periodic revalidation drop a subscriber whose tenant membership or run membership was
/// revoked. The snapshot is the run's last committed fact sequence; events are published by later specs from the outbox.
/// </summary>
public sealed class RevenantRunStreamSource(RevenantLedgerDataSource source, RevenantRunDirectory directory) : IRealtimeStreamSource
{
    public const string Kind = "revenant-run";

    public string StreamKind => Kind;

    public RealtimeStreamAccess ResolveAccess(string streamKey) => RealtimeStreamAccess.RequireRead(new ResourceRef(RevenantResources.RunKind, streamKey));

    public async ValueTask<RealtimeSourceSnapshot?> GetSnapshotAsync(string streamId, CancellationToken ct)
    {
        if (!RealtimeWire.TryParseStreamId(streamId, out var kind, out var key) || kind != Kind || !Guid.TryParseExact(key, "N", out var runId))
        {
            return null;
        }
        var owner = await directory.FindOwnershipAsync(RevenantResources.Run(new ScenarioRunId(runId)), ct);
        if (owner?.TenantId is not { } tenant)
        {
            return null;
        }
        await using var connection = await source.DataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var set = new NpgsqlCommand("SELECT set_config('revenant.tenant_id', @tenant, true)", connection, tx))
        {
            set.Parameters.AddWithValue("tenant", tenant);
            await set.ExecuteNonQueryAsync(ct);
        }
        await using var read = new NpgsqlCommand("SELECT last_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @run", connection, tx);
        read.Parameters.AddWithValue("run", runId);
        var sequence = await read.ExecuteScalarAsync(ct) is long last ? last : (long?)null;
        await tx.CommitAsync(ct);
        return sequence is { } s ? new RealtimeSourceSnapshot(s, new { runId, lastFactSequence = s }) : null;
    }
}

public static class RevenantRealtimeRegistration
{
    /// <summary>
    /// Registers the Revenant run stream and makes <see cref="RevenantStaffAuthorizer"/> the hub's resource and membership policy
    /// when <c>ConnectionStrings:RevenantLedger</c> is configured (a login that can read the run directory and cursor under RLS,
    /// e.g. a member of <c>revenant_replay_reader</c>; deployed login provisioning is task 7.5). Without it nothing is registered
    /// and the hub keeps its deny-all defaults. Call after the shared identity registration.
    /// </summary>
    public static IServiceCollection AddRevenantRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(RevenantLedgerRegistration.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(new RevenantLedgerDataSource(dataSource));
        services.AddSingleton(new RevenantRunDirectory(dataSource));
        services.AddSingleton(sp => new RevenantStaffAuthorizer(
            sp.GetRequiredService<RevenantRunDirectory>(), sp.GetRequiredService<RevenantRunDirectory>()));
        services.Replace(ServiceDescriptor.Singleton<IResourceAuthorizer>(sp => sp.GetRequiredService<RevenantStaffAuthorizer>()));
        services.Replace(ServiceDescriptor.Singleton<IMembershipAuthorizer>(sp => sp.GetRequiredService<RevenantStaffAuthorizer>()));
        services.AddSingleton<IRealtimeStreamSource, RevenantRunStreamSource>();
        return services;
    }
}
