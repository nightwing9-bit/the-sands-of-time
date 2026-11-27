using GhostLap.Contracts.Multiplayer;
using Marrow.Kernel.Security;

namespace GhostLap.Multiplayer;

public static class GhostLapKinds
{
    /// <summary>Resource and membership kind of a room. The same string as the shared-hub stream kind.</summary>
    public const string Room = GhostLapWire.RoomStreamKind;

    /// <summary>Every kind that starts with this belongs to Ghost Lap and is decided only by Ghost Lap (default deny), never by another domain.</summary>
    public const string Prefix = "ghostlap";

    public static bool IsGhostLap(string? kind) => kind is not null && kind.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Ghost Lap's authorization over the shared Kernel ports. Guests only (staff are never players), a known room kind only, and the
/// answer comes from the room owner's current membership, never from the request. Spectators may read but never write.
/// One decision for the room stream (membership) and for application-level commands (resource), so the layers cannot drift.
/// </summary>
public sealed class GhostLapAuthorizer(IActiveRoomOwner owner) : IResourceAuthorizer, IMembershipAuthorizer
{
    public ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, MembershipScope scope, CancellationToken ct) =>
        ValueTask.FromResult(Decide(actor, scope.Kind, scope.Id, write: false));

    public ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, ResourceRef resource, ResourceOperation operation, CancellationToken ct) =>
        ValueTask.FromResult(Decide(actor, resource.Kind, resource.Id, write: operation == ResourceOperation.Write));

    private AccessDecision Decide(IActorContext actor, string kind, string id, bool write)
    {
        if (actor is null || !actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return AccessDecision.Deny(AccessDenial.Unauthenticated);
        }
        if (actor.PrincipalKind != PrincipalKind.Guest)
        {
            return AccessDecision.Deny(AccessDenial.OwnerKindMismatch);
        }
        if (!string.Equals(kind, GhostLapKinds.Room, StringComparison.Ordinal))
        {
            return AccessDecision.Deny(AccessDenial.NoPolicy);
        }
        if (!Guid.TryParse(id, out var roomId))
        {
            return AccessDecision.Deny(AccessDenial.UnknownResource);
        }
        return owner.IsMember(actor.SubjectId, roomId, competitorOnly: write)
            ? AccessDecision.Allow
            : AccessDecision.Deny(AccessDenial.NotMember);
    }
}
