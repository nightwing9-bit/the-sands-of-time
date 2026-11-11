using System.Text;

namespace Revenant.Cases;

/// <summary>
/// Versioned release policy for held payments (Spec 07 task 7.4). A held payment of USD 10,000.00 or more needs two
/// approvals: any authorized staff member with a fresh MFA step-up first, then a DIFFERENT supervisor with a fresh MFA
/// step-up. Lower amounts need one MFA-verified approval. Any currency other than USD fails safe to two approvals
/// (the ledger has no FX path, so there is no conversion to compare against the threshold).
/// </summary>
public static class ReviewPolicy
{
    public const string Version = "revenant-review-policy/1";
    public const string ThresholdCurrency = "USD";

    /// <summary>USD 10,000.00 in minor units. A payment at or above it is high-value.</summary>
    public const long HighValueThresholdMinor = 1_000_000;

    public static int RequiredApprovals(string currency, long amountMinor) =>
        !string.Equals(currency, ThresholdCurrency, StringComparison.Ordinal) || amountMinor >= HighValueThresholdMinor ? 2 : 1;
}

/// <summary>The outcome of screening one party name against the synthetic list. Review evidence, never an ML prediction.</summary>
public sealed record NameScreeningResult(
    string Version, string ScreenedName, string? BestEntry, int ScorePermille, int ThresholdPermille, bool IsMatch);

/// <summary>
/// Deterministic, non-ML fuzzy name screening: a fixed rule over an explicit SYNTHETIC list (no real sanctioned party is
/// named). Names are normalized (ASCII letters and digits upper-cased, everything else a single space) and compared with
/// Levenshtein similarity in integer per-mille, <c>1000 * (maxLen - distance) / maxLen</c>, both as written and with tokens
/// sorted; the higher score counts. A score at or above the versioned threshold is a match. Same input, same output, always.
/// </summary>
public static class NameScreening
{
    public const string Version = "revenant-name-screen/1";
    public const int ThresholdPermille = 850;

    /// <summary>Synthetic watch list of version 1. Invented names only.</summary>
    public static readonly IReadOnlyList<string> SyntheticList = ["Dune Kredit", "Ombra Holdings", "Vesper Mutual Exchange"];

    public static NameScreeningResult Screen(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = Normalize(name);
        string? best = null;
        var bestScore = 0;
        foreach (var entry in SyntheticList)
        {
            var score = Score(normalized, Normalize(entry));
            if (score > bestScore)
            {
                (best, bestScore) = (entry, score);
            }
        }
        return new NameScreeningResult(Version, name, best, bestScore, ThresholdPermille, bestScore >= ThresholdPermille);
    }

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(char.ToUpperInvariant(c));
                space = false;
            }
            else
            {
                space = true;
            }
        }
        return builder.ToString();
    }

    /// <summary>Per-mille similarity of two normalized names (0 when either is empty).</summary>
    public static int Score(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }
        return Math.Max(Similarity(a, b), Similarity(SortTokens(a), SortTokens(b)));
    }

    private static string SortTokens(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    private static int Similarity(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        return (int)((long)(max - Levenshtein(a, b)) * 1000 / max);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}

/// <summary>Review case status. Separate from the payment status: the payment stays Held until the case is Released.</summary>
public enum CaseStatus { Open, AwaitingSecondApproval, Released, Rejected }

public enum CaseAction { Assign, Approve, Reject }

/// <summary>The verified facts about the acting staff member that the case rules need (from the server-side actor).</summary>
public sealed record CaseActor(string SubjectId, bool IsStaff, bool IsSupervisor, bool HasFreshMfa);

/// <summary>Current persisted state of a case: status, optimistic-concurrency version and the approvers so far, in order.</summary>
public sealed record CaseSnapshot(CaseStatus Status, long Version, int RequiredApprovals, IReadOnlyList<string> Approvers);

public enum CaseDecisionKind
{
    Accepted,
    NotStaff,
    Stale,
    Closed,
    MfaRequired,
    /// <summary>The second approval of a high-value case must come from a supervisor who did not give the first one.</summary>
    DistinctSupervisorRequired,
}

/// <param name="ApprovalOrder">1 or 2 for an accepted approval, otherwise 0.</param>
public readonly record struct CaseDecision(CaseDecisionKind Kind, CaseStatus NextStatus, long NextVersion, int ApprovalOrder);

/// <summary>
/// Pure four-eyes rules. Every accepted action advances the case version by exactly one (optimistic concurrency: a caller
/// must present the version it read, so of two concurrent edits from the same version one is <see cref="CaseDecisionKind.Stale"/>).
/// Approving or rejecting is privileged and needs a fresh MFA step-up. Released and Rejected are terminal.
/// </summary>
public static class CaseMachine
{
    public static CaseDecision Decide(CaseSnapshot current, CaseAction action, CaseActor actor, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsStaff || string.IsNullOrEmpty(actor.SubjectId))
        {
            return Refuse(CaseDecisionKind.NotStaff, current);
        }
        if (expectedVersion != current.Version)
        {
            return Refuse(CaseDecisionKind.Stale, current);
        }
        if (current.Status is CaseStatus.Released or CaseStatus.Rejected)
        {
            return Refuse(CaseDecisionKind.Closed, current);
        }
        var next = checked(current.Version + 1);
        if (action == CaseAction.Assign)
        {
            return new CaseDecision(CaseDecisionKind.Accepted, current.Status, next, 0);
        }
        if (!actor.HasFreshMfa)
        {
            return Refuse(CaseDecisionKind.MfaRequired, current);
        }
        if (action == CaseAction.Reject)
        {
            return new CaseDecision(CaseDecisionKind.Accepted, CaseStatus.Rejected, next, 0);
        }

        var order = current.Approvers.Count + 1;
        if (order > current.RequiredApprovals)
        {
            return Refuse(CaseDecisionKind.Closed, current);
        }
        if (order >= 2 && (!actor.IsSupervisor || current.Approvers.Contains(actor.SubjectId, StringComparer.Ordinal)))
        {
            return Refuse(CaseDecisionKind.DistinctSupervisorRequired, current);
        }
        var status = order == current.RequiredApprovals ? CaseStatus.Released : CaseStatus.AwaitingSecondApproval;
        return new CaseDecision(CaseDecisionKind.Accepted, status, next, order);
    }

    private static CaseDecision Refuse(CaseDecisionKind kind, CaseSnapshot current) => new(kind, current.Status, current.Version, 0);
}

/// <summary>Sanitizes staff comment text: control characters other than line breaks are dropped, ends are trimmed.</summary>
public static class CaseCommentText
{
    public const int MaxLength = 2000;

    /// <summary>The sanitized body, or null when nothing printable remains or it exceeds <see cref="MaxLength"/>.</summary>
    public static string? Sanitize(string? body)
    {
        if (body is null)
        {
            return null;
        }
        var builder = new StringBuilder(body.Length);
        foreach (var c in body.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (c == '\n' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }
        var text = builder.ToString().Trim();
        return text.Length is 0 or > MaxLength ? null : text;
    }
}
