using System.Security.Cryptography;
using System.Text;
using GhostLap.Rules;

namespace GhostLap.Multiplayer;

/// <summary>
/// Identity of the multiplayer rules profile sealed into every countdown (ADR-P1-22). <see cref="MpRules"/> implements
/// <c>ghost-mp-v1</c> over the shared ghost-v1 phase code (task 10.2). <c>ghost-v1</c> codecs, version key and hashes are not touched.
/// </summary>
public static class MpRulesProfile
{
    public const string RulesId = "ghost-mp-v1";
    public const string StateSchema = "ghost-mp-state/1";
    public const string EligibilityPolicy = "mp-eligibility-v1";
    public const string InitialRatingAlgorithmId = "elo-pair-v1";

    public static string LayoutId => GameRulesV1.LayoutVersion;

    public static string RulesHash => $"{RulesId}+{GameRulesV1.LayoutVersion}";

    public static int TargetTicks => GameRulesV1.RoundTicks;

    /// <summary>
    /// ADR-P1-22 D5: ticks D..D+449 after the server observes a disconnect at tick D are grace (450 ticks, ADR-P1-11); the slot is forfeited in phase 0 of
    /// tick F = D + 450 unless a controller is connected when tick F begins. Pinned by the rules profile, deliberately not configurable.
    /// </summary>
    public const int GraceTicks = 450;

    /// <summary>
    /// SHA-256 over the authored arena-v1 geometry (obstacles, candle sites, 2/3/4 spawn sockets). Computed from the code, so it
    /// changes if the layout data changes. It is a Ghost Lap-owned fingerprint, not a Spec 06 recorded hash.
    /// </summary>
    public static string LayoutFingerprint { get; } = ComputeLayoutFingerprint();

    private static string ComputeLayoutFingerprint()
    {
        var text = new StringBuilder("arena-fingerprint/1\n");
        foreach (var o in ArenaV1.Obstacles)
        {
            text.Append($"o {o.X0} {o.Y0} {o.X1} {o.Y1}\n");
        }
        foreach (var c in ArenaV1.CandleSites)
        {
            text.Append($"c {c.X} {c.Y}\n");
        }
        for (var players = 2; players <= 4; players++)
        {
            foreach (var s in ArenaV1.SpawnSockets(players))
            {
                text.Append($"s{players} {s.X} {s.Y}\n");
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

/// <summary>Startup validation. A host refuses to start with a non-empty result (no silent degraded multiplayer).</summary>
public static class MultiplayerStartup
{
    public static IReadOnlyList<string> Validate(MultiplayerOptions o)
    {
        var errors = new List<string>();
        Range(errors, nameof(o.MaxCompetitors), o.MaxCompetitors, MultiplayerOptions.MinCompetitorsFixed, MultiplayerOptions.MaxCompetitorsCeiling);
        Range(errors, nameof(o.MaxSpectators), o.MaxSpectators, 0, 64);
        Range(errors, nameof(o.MaxRooms), o.MaxRooms, 1, 4096);
        Range(errors, nameof(o.MaxConnectionsPerMember), o.MaxConnectionsPerMember, 1, 8);
        Range(errors, nameof(o.CountdownMilliseconds), o.CountdownMilliseconds, 1000, 10_000);
        Range(errors, nameof(o.AdmissionTimeoutMilliseconds), o.AdmissionTimeoutMilliseconds, 500, 30_000);
        Range(errors, nameof(o.RoomCodeSeconds), o.RoomCodeSeconds, 30, 86_400);
        Range(errors, nameof(o.LobbyIdleSeconds), o.LobbyIdleSeconds, 60, 86_400);
        Range(errors, nameof(o.MaxRoomAgeSeconds), o.MaxRoomAgeSeconds, 60, 86_400);
        Range(errors, nameof(o.DisconnectGraceSeconds), o.DisconnectGraceSeconds, 1, 600);
        Range(errors, nameof(o.EmptyRoomSeconds), o.EmptyRoomSeconds, 1, 3600);
        Range(errors, nameof(o.RoomCodeLength), o.RoomCodeLength, 6, MultiplayerLimits.MaxRoomCodeLength);
        Range(errors, nameof(o.CodeLookupsPerMinute), o.CodeLookupsPerMinute, 1, 600);
        Range(errors, nameof(o.InputBurst), o.InputBurst, 1, 1000);
        Range(errors, nameof(o.InputOverLimitDisconnectAfter), o.InputOverLimitDisconnectAfter, 1, 100_000);
        Range(errors, nameof(o.MaxConnectionsPerPrincipal), o.MaxConnectionsPerPrincipal, 1, 32);
        Range(errors, nameof(o.MailboxCapacity), o.MailboxCapacity, 16, 65_536);
        Range(errors, nameof(o.MaxInputsPerSlotPerTick), o.MaxInputsPerSlotPerTick, 1, 64);
        Range(errors, nameof(o.CatchUpTicksPerIteration), o.CatchUpTicksPerIteration, 1, 30);
        Range(errors, nameof(o.LateAlarmTicks), o.LateAlarmTicks, 1, 90);
        Range(errors, nameof(o.OverloadSustainedMilliseconds), o.OverloadSustainedMilliseconds, 100, 60_000);
        Range(errors, nameof(o.OverloadMaxBacklogTicks), o.OverloadMaxBacklogTicks, 3, 2700);
        Range(errors, nameof(o.GenerationCheckMilliseconds), o.GenerationCheckMilliseconds, 50, 60_000);
        Range(errors, nameof(o.ActivationTimeoutMilliseconds), o.ActivationTimeoutMilliseconds, 1000, 600_000);
        Range(errors, nameof(o.AbortAttempts), o.AbortAttempts, 1, 100);
        Range(errors, nameof(o.SnapshotIntervalTicks), o.SnapshotIntervalTicks, 1, 30);
        Range(errors, nameof(o.EventRetentionTicks), o.EventRetentionTicks, 30, GameRulesV1.RoundTicks);
        Range(errors, nameof(o.SendTimeoutMilliseconds), o.SendTimeoutMilliseconds, 100, 60_000);
        Range(errors, nameof(o.ControlQueueCapacity), o.ControlQueueCapacity, 8, 1024);
        Range(errors, nameof(o.HubKeepAliveSeconds), o.HubKeepAliveSeconds, 1, 60);
        Range(errors, nameof(o.HubClientTimeoutSeconds), o.HubClientTimeoutSeconds, 2, 300);
        if (o.HubClientTimeoutSeconds < 2 * o.HubKeepAliveSeconds)
        {
            errors.Add($"{MultiplayerOptions.SectionName}:{nameof(o.HubClientTimeoutSeconds)} must be at least twice {nameof(o.HubKeepAliveSeconds)}.");
        }
        if (o.OverloadMaxBacklogTicks <= o.LateAlarmTicks)
        {
            errors.Add($"{MultiplayerOptions.SectionName}:{nameof(o.OverloadMaxBacklogTicks)} must exceed {nameof(o.LateAlarmTicks)}.");
        }
        if (o.InputsPerSecond is < 30 or > 1000)
        {
            errors.Add($"{MultiplayerOptions.SectionName}:{nameof(o.InputsPerSecond)} must be between 30 and 1000 (the game sends 30 Hz plus bursts of changes).");
        }
        if (string.IsNullOrWhiteSpace(o.RatingAlgorithmId) || o.RatingAlgorithmId.Length > 64
            || !o.RatingAlgorithmId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_'))
        {
            errors.Add($"{MultiplayerOptions.SectionName}:{nameof(o.RatingAlgorithmId)} must be 1 to 64 characters of [a-z0-9._-].");
        }
        if (o.AllowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || (uri.Scheme is not ("http" or "https"))))
        {
            errors.Add($"{MultiplayerOptions.SectionName}:{nameof(o.AllowedOrigins)} entries must be absolute http(s) origins without a path.");
        }
        ValidateRulesAndLayout(errors);
        return errors;
    }

    /// <summary>The fixed facts the sealed config depends on: tick budget and 2/3/4 sockets (occupiable, distinct, apart).</summary>
    public static void ValidateRulesAndLayout(List<string> errors)
    {
        if (Runtime(GameRulesV1.TickRate) != 30 || Runtime(GameRulesV1.RoundTicks) != 2700)
        {
            errors.Add("Rules constants do not match the sealed multiplayer baseline (30 Hz, 2700 ticks).");
        }
        if (Runtime(GameRulesV1.MaxPlayers) < MultiplayerOptions.MaxCompetitorsCeiling)
        {
            errors.Add("The rules support fewer than four players.");
        }
        for (var players = 2; players <= MultiplayerOptions.MaxCompetitorsCeiling; players++)
        {
            var sockets = ArenaV1.SpawnSockets(players);
            if (sockets.Length != players)
            {
                errors.Add($"arena-v1 has {sockets.Length} spawn sockets for {players} players.");
                continue;
            }
            if (sockets.Any(s => !ArenaV1.CanOccupy(s.X, s.Y)))
            {
                errors.Add($"arena-v1 {players}-player spawn socket is not occupiable.");
            }
            if (sockets.Distinct().Count() != players)
            {
                errors.Add($"arena-v1 {players}-player spawn sockets are not distinct.");
            }
            var minApart = 4L * GameRulesV1.ColliderHalfExtentFixed;
            for (var a = 0; a < players; a++)
            {
                for (var b = a + 1; b < players; b++)
                {
                    var dx = Math.Abs((long)sockets[a].X - sockets[b].X);
                    var dy = Math.Abs((long)sockets[a].Y - sockets[b].Y);
                    if (Math.Max(dx, dy) < minApart)
                    {
                        errors.Add($"arena-v1 {players}-player spawn sockets {a} and {b} start overlapped or touching.");
                    }
                }
            }
            if (players is 2 or 4)
            {
                // Point symmetry about the arena centre: the set of sockets maps onto itself under (x,y) -> (W-x, H-y).
                var set = sockets.ToHashSet();
                if (sockets.Any(s => !set.Contains(new FixedPoint(GameRulesV1.WorldWidthFixed - s.X, GameRulesV1.WorldHeightFixed - s.Y))))
                {
                    errors.Add($"arena-v1 {players}-player spawn sockets are not point-symmetric.");
                }
            }
        }
    }

    // Prevents the compiler from folding a deliberate guard on a rules constant into dead code.
    private static int Runtime(int value) => value;

    private static void Range(List<string> errors, string key, long value, long min, long max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{MultiplayerOptions.SectionName}:{key} must be between {min} and {max}.");
        }
    }
}

public static class MultiplayerLimits
{
    public const int MaxRoomCodeLength = 12;
    public const int MaxRequestIdLength = 64;
}
