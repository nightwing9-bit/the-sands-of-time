using Marrow.Kernel.Results;

namespace Revenant.Finance;

public enum FinanceErrorCode
{
    /// <summary>A pinned scenario, policy, reducer or feature version does not match exactly.</summary>
    VersionMismatch,
    /// <summary>The intent belongs to a different <c>ScenarioRunId</c> than the state.</summary>
    RunMismatch,
    /// <summary>Logical sequence not strictly increasing, or event time earlier than the projection clock.</summary>
    OutOfOrderInput,
    /// <summary>Same intent id already applied with different facts.</summary>
    ConflictingDuplicate,
    /// <summary>Account or bank not part of this run's state namespace.</summary>
    UnknownAccount,
    /// <summary>States cannot be compared (different run, scenario or position).</summary>
    NotAligned,
    /// <summary>A scenario run's intent stream is not a contiguous, run-scoped sequence matching its facts hash.</summary>
    InvalidScenarioRun,
}

public sealed record FinanceError(FinanceErrorCode Code, string Message)
{
    public static Result<T, FinanceError> Fail<T>(FinanceErrorCode code, string message) =>
        Result<T, FinanceError>.Failure(new FinanceError(code, message));
}
