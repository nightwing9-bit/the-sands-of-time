using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Timeline;

namespace Revenant.Payments;

internal enum V4CommandClass { Exogenous, ConditionalHumanRequest, DerivedOpportunity }
internal enum V4CommandStatus { Pending, Completed, Cancelled, FailedPermanent, RetryableBlocked }

internal sealed record V4CommandRequest(string Key, V4CommandClass Class, string Kind, string PayloadSchema,
    ImmutableArray<byte> CanonicalPayload, long RequestedTimeMs, long DueTimeMs);

internal sealed record V4ScheduledCommand(ScenarioRunId RunId, string TenantId, long Sequence, V4CommandRequest Request,
    string PayloadSha256, long AdmittedTimeMs, V4CommandStatus Status, long? EffectiveTimeMs, string? OutcomeCode, string? OutcomeSha256);

internal sealed record V4CommandOutcome(V4CommandStatus Status, string Code, ImmutableArray<byte> CanonicalEvidence);

internal enum V4StepKind { ProcessedCommand, Sealed, Blocked, UnsupportedHandler, WorkDue, NotNextBoundary }

internal sealed record V4Step(V4StepKind Kind, V4ScheduledCommand? Command = null, ObservedFinanceInput? Seal = null);

internal enum V4ScheduleError { RunNotFound, RunNotOpen, InvalidCommand, ConflictingCommand, InvalidRetry, SequenceOrTimeOverflow }

internal sealed class V4ScheduleException(V4ScheduleError error) : InvalidOperationException(error.ToString())
{
    public V4ScheduleError Error { get; } = error;
}

internal static class V4SimulationScheduler
{
    private const string CompletionContract = "revenant-v4-command-completion/1";

    internal static string InitialCommandDigest(ScenarioRunId runId) =>
        ObservedFinanceEncoding.Hash(new CanonicalWriter().WriteString(CompletionContract).WriteGuid(runId.Value));

    internal static string RecomputeCommandDigest(ScenarioRunId runId, long boundary, IEnumerable<V4ScheduledCommand> journal)
    {
        var digest = InitialCommandDigest(runId);
        foreach (var c in journal.Where(c => c.Request.DueTimeMs < boundary).OrderBy(c => c.Request.DueTimeMs).ThenBy(c => c.Sequence))
        {
            if (c.RunId != runId || !IsTerminal(c.Status) || c.OutcomeSha256 is null)
            {
                throw new InvalidOperationException("The command journal cannot certify this boundary.");
            }
            digest = ObservedFinanceEncoding.Hash(new CanonicalWriter().WriteString(CompletionContract).WriteBytes(Convert.FromHexString(digest))
                .WriteInt64(c.Sequence).WriteBytes(Convert.FromHexString(c.PayloadSha256)).WriteInt64(c.Request.DueTimeMs)
                .WriteString(c.Status.ToString()).WriteBytes(Convert.FromHexString(c.OutcomeSha256)));
        }
        return digest;
    }

    internal static bool IsTerminal(V4CommandStatus status) =>
        status is V4CommandStatus.Completed or V4CommandStatus.Cancelled or V4CommandStatus.FailedPermanent;

    internal static string OutcomeHash(V4CommandOutcome outcome)
    {
        if ((!IsTerminal(outcome.Status) && outcome.Status != V4CommandStatus.RetryableBlocked)
            || string.IsNullOrWhiteSpace(outcome.Code) || outcome.CanonicalEvidence.IsDefaultOrEmpty)
        {
            throw new V4ScheduleException(V4ScheduleError.InvalidCommand);
        }
        return ObservedFinanceEncoding.Hash(new CanonicalWriter().WriteString("revenant-v4-command-outcome/1")
            .WriteString(outcome.Status.ToString()).WriteString(outcome.Code).WriteBytes(outcome.CanonicalEvidence.AsSpan()));
    }
}
