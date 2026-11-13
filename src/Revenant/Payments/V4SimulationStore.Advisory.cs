using Revenant.Intelligence;
using Revenant.Observations;

namespace Revenant.Payments;

internal sealed partial class V4SimulationStore
{
    internal async Task<string> PersistAdvisoryAsync(string tenant, AdvisoryEvidence evidence, CancellationToken ct)
    {
        // Availability failures remain visible in the Worker receipt and may recover on retry.
        if (evidence.Status != AdvisoryStatus.Available) throw new InvalidOperationException("Only successful predictions are immutable.");
        var canonical = evidence.Encode();
        var digest = ObservationEncoding.Hash(canonical);
        var series = evidence.Series;
        var seal = series.Origin!;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        await ExecuteAsync(tx, """
            INSERT INTO revenant.v4_advisory_evidence
                (tenant_id, execution_run_id, subject_id, boundary_ms, schema_id, model_id, policy_id,
                 seal_fact_seq, prefix_digest, canonical, evidence_digest)
            VALUES (@tenant, @run, @subject, @boundary, @schema, @model, @policy, @seq, @prefix, @canonical, @digest)
            ON CONFLICT DO NOTHING
            """, ct, ("tenant", tenant), ("run", series.ExecutionRunId.Value), ("subject", series.SubjectId),
            ("boundary", seal.BoundaryMs), ("schema", evidence.Schema), ("model", evidence.ProducerId), ("policy", evidence.Policy),
            ("seq", seal.FactSequence), ("prefix", Convert.FromHexString(seal.PrefixDigest)), ("canonical", canonical),
            ("digest", Convert.FromHexString(digest)));
        await using var query = Sql(tx, """
            SELECT evidence_digest FROM revenant.v4_advisory_evidence
            WHERE tenant_id = @tenant AND execution_run_id = @run AND subject_id = @subject AND boundary_ms = @boundary
                AND schema_id = @schema AND model_id = @model AND policy_id = @policy
            """, ("tenant", tenant), ("run", series.ExecutionRunId.Value), ("subject", series.SubjectId),
            ("boundary", seal.BoundaryMs), ("schema", evidence.Schema), ("model", evidence.ProducerId), ("policy", evidence.Policy));
        if (await query.ExecuteScalarAsync(ct) is not byte[] saved || !saved.AsSpan().SequenceEqual(Convert.FromHexString(digest)))
            throw new InvalidOperationException("Conflicting advisory evidence for an immutable origin.");
        await tx.CommitAsync(ct);
        return digest;
    }
}
