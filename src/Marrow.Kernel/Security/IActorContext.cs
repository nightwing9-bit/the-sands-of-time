namespace Marrow.Kernel.Security;

public enum PrincipalKind { Staff, Guest }

/// <summary>Verified server-side actor. Never populated from request-supplied owner ids.</summary>
public interface IActorContext
{
    string SubjectId { get; }
    PrincipalKind PrincipalKind { get; }
    string? TenantId { get; }
    IReadOnlyCollection<string> Roles { get; }
    IReadOnlyCollection<string> Claims { get; }
    bool IsAuthenticated { get; }
}
