using Microsoft.Extensions.Options;

namespace Marrow.Infrastructure.Realtime;

/// <summary>
/// Realtime transport bounds (section <c>Realtime</c>). These are safety limits, not measured capacity figures.
/// Every value is validated at startup against a fixed range (<see cref="MarrowRealtimeOptionsValidator"/>), so a
/// configuration can tighten a limit but cannot disable it or make it unbounded.
/// </summary>
public sealed class MarrowRealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>Largest client-to-server hub message. SignalR closes the connection on a larger one.</summary>
    public long MaxInboundMessageBytes { get; set; } = 8 * 1024;

    /// <summary>Largest serialized event or snapshot payload the server will send.</summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    /// <summary>Token bucket per connection: sustained hub invocations per second.</summary>
    public double InvocationsPerSecond { get; set; } = 10;

    /// <summary>Token bucket per connection: burst size.</summary>
    public int InvocationBurst { get; set; } = 20;

    public int MaxSubscriptionsPerConnection { get; set; } = 32;

    /// <summary>How long startup keeps retrying the single-instance room-authority lock (0 = one attempt).</summary>
    public int InstanceLockAcquireSeconds { get; set; } = 15;

    /// <summary>Interval between probes of the held lock connection.</summary>
    public int InstanceLockCheckSeconds { get; set; } = 5;

    /// <summary>
    /// Interval at which every open hub connection's session and subscriptions are re-authorized. Bounds how long a
    /// connection can keep receiving a stream after its session is revoked or its membership removed elsewhere.
    /// </summary>
    public int AccessRevalidationSeconds { get; set; } = 10;
}

/// <summary>Fixed allowed ranges for <see cref="MarrowRealtimeOptions"/>. Out-of-range values fail host startup.</summary>
public sealed class MarrowRealtimeOptionsValidator : IValidateOptions<MarrowRealtimeOptions>
{
    public const long MaxMessageBytesCeiling = 1024 * 1024;
    public const double MaxInvocationsPerSecond = 1000;
    public const int MaxBurst = 1000;
    public const int MaxSubscriptions = 1024;

    public ValidateOptionsResult Validate(string? name, MarrowRealtimeOptions o)
    {
        var errors = new List<string>();
        Range(errors, nameof(o.MaxInboundMessageBytes), o.MaxInboundMessageBytes, 256, MaxMessageBytesCeiling);
        Range(errors, nameof(o.MaxPayloadBytes), o.MaxPayloadBytes, 256, MaxMessageBytesCeiling);
        if (!double.IsFinite(o.InvocationsPerSecond) || o.InvocationsPerSecond <= 0 || o.InvocationsPerSecond > MaxInvocationsPerSecond)
        {
            errors.Add($"Realtime:{nameof(o.InvocationsPerSecond)} must be greater than 0 and at most {MaxInvocationsPerSecond}.");
        }
        Range(errors, nameof(o.InvocationBurst), o.InvocationBurst, 1, MaxBurst);
        Range(errors, nameof(o.MaxSubscriptionsPerConnection), o.MaxSubscriptionsPerConnection, 1, MaxSubscriptions);
        Range(errors, nameof(o.InstanceLockAcquireSeconds), o.InstanceLockAcquireSeconds, 0, 300);
        Range(errors, nameof(o.InstanceLockCheckSeconds), o.InstanceLockCheckSeconds, 1, 60);
        Range(errors, nameof(o.AccessRevalidationSeconds), o.AccessRevalidationSeconds, 1, 60);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Range(List<string> errors, string key, long value, long min, long max)
    {
        if (value < min || value > max)
        {
            errors.Add($"Realtime:{key} must be between {min} and {max}.");
        }
    }
}
