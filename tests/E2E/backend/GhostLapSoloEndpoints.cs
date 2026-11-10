using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Npgsql;
namespace Sands.E2E.Backend;
// ---- Wire DTOs of the TEST-ONLY Ghost Lap greybox surface (Spec 06 task 6.5) ----------------------------------------------
// Every value a browser draws comes from these records, built from the authoritative C# GameState (live recorder state, or a
// verified Marrow Timeline restore/aligned comparison). World coordinates are sent twice: the fixed-point integers the rules use
// and world units (fixed / 1024) for drawing only. The browser never sends a position, a score or a tick it claims to be at.
public sealed record GlRect(int X0, int Y0, int X1, int Y1);
public sealed record GlArena(
    string RulesVersion, string LayoutVersion, int TickRate, int RoundTicks, int Width, int Height, int ColliderHalf, int CandleHalf,
    int BurstRadius, int MaxCharge, IReadOnlyList<GlRect> Obstacles);
public sealed record GlPlayer(
    int Slot, int MascotId, double X, double Y, int XFixed, int YFixed, string DistanceFixed, int Charge, bool Overlit,
    long OverlitDeadlineTick, int MovementPoints, int PickupPoints, int PenaltyPoints, int TotalScore, int Pickups, int Bursts,
    int LastSequence);
public sealed record GlCandle(string Id, double X, double Y, int ActivateTick, int ExpireTick);
public sealed record GlEvent(string Kind, int Slot, int OtherSlot, int Value, string CandleId);
public sealed record GlWorld(
    long Tick, string? StateHash, IReadOnlyList<GlPlayer> Players, IReadOnlyList<GlCandle> ActiveCandles,
    IReadOnlyList<GlCandle> TelegraphCandles, int CollectedCount, IReadOnlyList<GlEvent> Events, bool IsFinal);
/// <summary>The inert source ghost at one tick: verified source data only, drawn but never part of the living world.</summary>
public sealed record GlGhost(long Tick, double X, double Y, int Charge, int TotalScore, int Pickups);
public sealed record GlAligned(long Tick, int GhostScore, int CurrentScore, int Delta, bool IsFinal, string CurrentStatus);
public sealed record GlFrame(
    string SessionId, string Kind, string Status, string? Error, string? SourceId, long? ForkTick, int TickRate, GlWorld World,
    GlGhost? Ghost, GlAligned? Aligned, string? CompletedId, string? FinalStateHash);
public sealed record GlMarker(string Position, string Label);
public sealed record GlOriginal(
    string TimelineId, string SourceId, GlBounds Bounds, bool ReadOnly, IReadOnlyList<GlMarker> Markers, string HistoryStatus,
    long Seed, int MascotId, int FinalScore, string FinalStateHash, GlArena Arena);
public sealed record GlBounds(string Start, string End);
public sealed record GlStartBody(long? Seed, int? MascotId);
public sealed record GlInputBody(int? Sequence, long? ClientTick, int? MoveX, int? MoveY, bool? RequestBurst);
public sealed record GlBranchBody(string? Position, string? OperationKey);
public enum GlLiveStatus
{
    /// <summary>Created; no tick runs until the player's browser says it is ready.</summary>
    Ready,
    Running,
    /// <summary>The final tick is committed; the history is being completed and replay-verified.</summary>
    Completing,
    /// <summary>Completed, replay-verified and its Ghost Lap row marked Complete.</summary>
    Complete,
    Failed,
    /// <summary>No browser asked for a frame for <see cref="GhostLapLiveSession.AbandonAfter"/>; the tick loop stopped. Never an original.</summary>
    Abandoned,
}
/// <summary>
/// One live solo world (an original being recorded, or a branch of one) and its server tick loop. The loop owns time: it commits
/// one tick every 1/30 s through the domain recorder (<see cref="SoloRecording"/> or <see cref="SoloBranch"/>), with whatever
/// inputs the policy admitted (none on an empty tick). The browser only submits intents, which the same recorder admits or rejects.
/// </summary>
public sealed class GhostLapLiveSession
{
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromSeconds(20);
    private const int MaxConsecutiveFailures = 30;
    private readonly Func<SubmittedInput, Result<GameInput, InputRejection>> _submit;
    private readonly Func<byte, Result<GameInput, InputRejection>> _neutralize;
    private readonly Func<CancellationToken, Task<Result<SoloTickReceipt, SoloError>>> _commit;
    private readonly Func<CancellationToken, Task<Result<string, SoloError>>> _complete;
    private readonly Func<GameState> _state;
    private long _lastPollMs = Environment.TickCount64;
    private int _started;
    private int _status = (int)GlLiveStatus.Ready;
    private SoloTickReceipt? _lastReceipt;
    private GhostLapLiveSession(
        Guid id, string kind, string ownerId, Guid? sourceId, long? forkTick, GameContext context,
        Func<SubmittedInput, Result<GameInput, InputRejection>> submit, Func<byte, Result<GameInput, InputRejection>> neutralize,
        Func<CancellationToken, Task<Result<SoloTickReceipt, SoloError>>> commit,
        Func<CancellationToken, Task<Result<string, SoloError>>> complete, Func<GameState> state)
    {
        Id = id;
        Kind = kind;
        OwnerId = ownerId;
        SourceId = sourceId;
        ForkTick = forkTick;
        Context = context;
        _submit = submit;
        _neutralize = neutralize;
        _commit = commit;
        _complete = complete;
        _state = state;
    }
    public Guid Id { get; }
    public string Kind { get; }
    public string OwnerId { get; }
    public Guid? SourceId { get; }
    public long? ForkTick { get; }
    public GameContext Context { get; }
    public GameState State => _state();
    public GlLiveStatus Status => (GlLiveStatus)Volatile.Read(ref _status);
    public string? Error { get; private set; }
    public string? FinalStateHash { get; private set; }
    public string? LastStateHash => Volatile.Read(ref _lastReceipt)?.StateHash;
    public static GhostLapLiveSession ForOriginal(SoloRecording r, string ownerId) => new(
        r.Timeline.Value, "original", ownerId, null, null, r.Context, r.Submit, r.Neutralize, r.CommitTickAsync,
        async ct => (await r.CompleteAsync(ct)).Match(o => Result<string, SoloError>.Success(o.FinalStateHash), Result<string, SoloError>.Failure),
        () => r.State);
    public static GhostLapLiveSession ForBranch(SoloBranch b, string ownerId) => new(
        b.Timeline.Value, "branch", ownerId, b.Source.Value, b.ForkTick, b.Context, b.Submit, b.Neutralize, b.CommitTickAsync,
        async ct => (await b.CompleteAsync(ct)).Match(o => Result<string, SoloError>.Success(o.FinalStateHash), Result<string, SoloError>.Failure),
        () => b.State);
    private void SetStatus(GlLiveStatus status) => Volatile.Write(ref _status, (int)status);
    public void Touch() => Volatile.Write(ref _lastPollMs, Environment.TickCount64);
    public Result<GameInput, InputRejection> Submit(SubmittedInput input)
    {
        Touch();
        return _submit(input);
    }
    public Result<GameInput, InputRejection> Neutralize()
    {
        Touch();
        return _neutralize(0);
    }
    /// <summary>Starts the tick loop once; later calls are no-ops (idempotent).</summary>
    public bool Start(CancellationToken stopping)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return false;
        }
        Touch();
        SetStatus(State.IsFinal ? GlLiveStatus.Completing : GlLiveStatus.Running);
        _ = Task.Run(() => RunAsync(stopping), CancellationToken.None);
        return true;
    }
    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / GameRulesV1.TickRate));
            var failures = 0;
            while (!State.IsFinal && await timer.WaitForNextTickAsync(stopping))
            {
                if (Environment.TickCount64 - Volatile.Read(ref _lastPollMs) > AbandonAfter.TotalMilliseconds)
                {
                    Error = $"No client for {AbandonAfter.TotalSeconds:0} s; recording stopped at tick {State.Tick}. It is not an original.";
                    SetStatus(GlLiveStatus.Abandoned);
                    return;
                }
                var committed = await _commit(stopping);
                if (committed.TryGetValue(out var receipt))
                {
                    Volatile.Write(ref _lastReceipt, receipt);
                    failures = 0;
                    Error = null;
                    continue;
                }
                // Visible and retried: the recorder froze this tick's step, so the next attempt resends the identical payload.
                Error = $"Tick {State.Tick + 1} not committed yet: {committed.Error}";
                if (++failures >= MaxConsecutiveFailures)
                {
                    SetStatus(GlLiveStatus.Failed);
                    return;
                }
            }
            SetStatus(GlLiveStatus.Completing);
            for (var attempt = 1; ; attempt++)
            {
                var completed = await _complete(stopping);
                if (completed.TryGetValue(out var hash))
                {
                    FinalStateHash = hash;
                    Error = null;
                    SetStatus(GlLiveStatus.Complete);
                    return;
                }
                Error = $"Completion not verified: {completed.Error}";
                if (attempt >= 3 || completed.Error.Code == SoloErrorCode.VerificationFailed)
                {
                    SetStatus(GlLiveStatus.Failed);
                    return;
                }
                await Task.Delay(250, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Error = "The host stopped; the recording ended without completion.";
            SetStatus(GlLiveStatus.Failed);
        }
    }
}
public sealed class GhostLapSessionRegistry
{
    private const int MaxSessions = 64;
    private readonly ConcurrentDictionary<Guid, GhostLapLiveSession> _sessions = new();
    /// <summary>Returns the registered session for this id (an idempotent retry resumes the same one) or adds this one.</summary>
    public GhostLapLiveSession? GetOrAdd(GhostLapLiveSession session)
    {
        if (_sessions.TryGetValue(session.Id, out var existing))
        {
            return existing;
        }
        if (_sessions.Count >= MaxSessions)
        {
            foreach (var (id, s) in _sessions)
            {
                if (s.Status is GlLiveStatus.Complete or GlLiveStatus.Failed or GlLiveStatus.Abandoned)
                {
                    _sessions.TryRemove(id, out _);
                }
            }
            if (_sessions.Count >= MaxSessions)
            {
                return null;
            }
        }
        return _sessions.GetOrAdd(session.Id, session);
    }
    /// <summary>The session if it exists and belongs to <paramref name="subjectId"/>; otherwise null (same answer for both).</summary>
    public GhostLapLiveSession? Find(Guid id, string subjectId) =>
        _sessions.TryGetValue(id, out var s) && string.Equals(s.OwnerId, subjectId, StringComparison.Ordinal) ? s : null;
}
/// <summary>
/// The Ghost Lap domain handlers over the API's own data source and the real Ghost Lap Timeline engine, with the Ghost Lap
/// owner policy (<see cref="SoloOwnershipLookup"/>) composed directly, so it never depends on the fixture's history lookup.
/// </summary>
public sealed class GhostLapSoloServices(ITimelineEngine<GameState, GameInput, GameContext> engine, NpgsqlDataSource dataSource)
{
    private readonly SoloOriginalStore _store = new(dataSource);
    private readonly ConcurrentDictionary<Guid, GameContext> _contexts = new();
    /// <summary>Pinned context of a verified original (seed and mascot never change once recorded). Only read after an owner check.</summary>
    public GameContext? ContextOf(Guid original) => _contexts.GetValueOrDefault(original);
    public GameContext Remember(Guid id, SoloOriginal original) =>
        _contexts.GetOrAdd(id, _ => GameContext.Create(original.Seed, 1, [original.MascotId]));
    public SoloOriginals Originals => field ??= new SoloOriginals(engine, _store, new OwnerResourceAuthorizer(new SoloOwnershipLookup(_store)));
    public SoloBranches Branches => field ??= new SoloBranches(engine, _store, new OwnerResourceAuthorizer(new SoloOwnershipLookup(_store)));
}
/// <summary>
/// TEST-ONLY Ghost Lap greybox HTTP surface for Spec 06 task 6.5. Like the Revenant slice routes it is mapped only inside this
/// assembly; <c>Hosts.Api</c> serves none of it. It is the thinnest honest bridge between the browser and the real domain:
/// <see cref="SoloOriginals"/> records and opens originals, <see cref="SoloBranches"/> forks, restores and compares, and the server
/// tick loop in <see cref="GhostLapLiveSession"/> commits every tick. It adds no game rule. The production Ghost Lap realtime
/// session (SignalR, rooms) arrives with Spec 10; the catalog with Spec 11.
/// </summary>
public static class GhostLapSoloEndpoints
{
    public const string PathPrefix = "/ghost-lap";
    private const long DefaultSeed = 42;
    private const long MaxSeed = 1_000_000;
    private const int MaxMarkers = 12;
    public static bool IsGhostLapPath(PathString path) => path.StartsWithSegments(PathPrefix, StringComparison.Ordinal);
    public static void Map(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(PathPrefix).RequireAuthorization(IdentityPolicies.Guest);
        group.MapGet("/arena", () => Results.Json(Arena()));
        group.MapPost("/solo/sessions", StartOriginal);
        group.MapPost("/sessions/{id:guid}/start", StartLoop);
        group.MapPost("/sessions/{id:guid}/inputs", SubmitInput);
        group.MapPost("/sessions/{id:guid}/neutral", Neutral);
        group.MapGet("/sessions/{id:guid}/frame", Frame);
        group.MapGet("/originals/{id:guid}", OpenOriginal);
        group.MapGet("/originals/{id:guid}/restore", RestoreOriginal);
        group.MapPost("/originals/{id:guid}/branches", CreateBranch);
    }
    // ---- Live sessions ----------------------------------------------------------------------------------------------------
    private static async Task<IResult> StartOriginal(HttpContext http, IActorContext actor, GhostLapSoloServices services, GhostLapSessionRegistry registry)
    {
        var ct = http.RequestAborted;
        var body = await ReadAsync<GlStartBody>(http, 512);
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        var seed = body?.Seed ?? DefaultSeed;
        var mascot = body?.MascotId ?? 0;
        if (body is null || !ValidKey(key) || seed is < 0 or > MaxSeed || mascot is < 0 or >= GameRulesV1.MascotCount)
        {
            return Invalid(http);
        }
        // Owner = the verified guest; a retry with the same key resumes the same recording (domain idempotency).
        var started = await services.Originals.StartAsync(actor, seed, (byte)mascot, key, ct);
        if (!started.TryGetValue(out var recording))
        {
            return Failure(http, started.Error);
        }
        var session = registry.GetOrAdd(GhostLapLiveSession.ForOriginal(recording, actor.SubjectId));
        return session is null ? Busy(http) : Results.Json(await FrameOfAsync(session, actor, services, ct));
    }
    private static IResult StartLoop(Guid id, HttpContext http, IActorContext actor, GhostLapSessionRegistry registry, IHostApplicationLifetime lifetime)
    {
        if (registry.Find(id, actor.SubjectId) is not { } session)
        {
            return NotFound(http);
        }
        session.Start(lifetime.ApplicationStopping);
        return Results.Json(new { sessionId = id.ToString("D"), status = session.Status.ToString(), tick = session.State.Tick });
    }
    private static async Task<IResult> SubmitInput(Guid id, HttpContext http, IActorContext actor, GhostLapSessionRegistry registry)
    {
        if (registry.Find(id, actor.SubjectId) is not { } session)
        {
            return NotFound(http);
        }
        var body = await ReadAsync<GlInputBody>(http, 512);
        if (body is not { Sequence: { } seq, ClientTick: { } clientTick, MoveX: { } mx, MoveY: { } my, RequestBurst: { } burst }
            || seq < 0 || clientTick < 0 || mx is < -1 or > 1 || my is < -1 or > 1)
        {
            return Invalid(http);
        }
        // Slot 0 is the solo player; the actor owns it because the actor owns the session.
        var admitted = session.Submit(new SubmittedInput(0, seq, clientTick, (sbyte)mx, (sbyte)my, burst));
        return InputAnswer(session, admitted);
    }
    private static IResult Neutral(Guid id, HttpContext http, IActorContext actor, GhostLapSessionRegistry registry)
    {
        if (registry.Find(id, actor.SubjectId) is not { } session)
        {
            return NotFound(http);
        }
        return InputAnswer(session, session.Neutralize());
    }
    private static async Task<IResult> Frame(Guid id, HttpContext http, IActorContext actor, GhostLapSoloServices services, GhostLapSessionRegistry registry)
    {
        if (registry.Find(id, actor.SubjectId) is not { } session)
        {
            return NotFound(http);
        }
        session.Touch();
        return Results.Json(await FrameOfAsync(session, actor, services, http.RequestAborted));
    }
    private static async Task<GlFrame> FrameOfAsync(GhostLapLiveSession session, IActorContext actor, GhostLapSoloServices services, CancellationToken ct)
    {
        var state = session.State;
        GlGhost? ghost = null;
        GlAligned? aligned = null;
        var error = session.Error;
        if (session.Kind == "branch")
        {
            // Ghost and delta at exactly the committed tick of the living branch, from the Timeline's aligned verified states.
            var compared = await services.Branches.CompareAsync(actor, new TimelineId(session.Id), [state.Tick], ct);
            if (compared.TryGetValue(out var rows) && rows.Count == 1)
            {
                var a = rows[0];
                ghost = new GlGhost(a.Tick, Units(a.Ghost.XFixed), Units(a.Ghost.YFixed), a.Ghost.Charge, a.Ghost.TotalScore, a.Ghost.Pickups);
                aligned = new GlAligned(a.Tick, a.Ghost.TotalScore, a.Current.TotalScore, a.Delta, a.IsFinal, a.CurrentStatus.ToString());
            }
            else
            {
                error ??= $"Comparison unavailable: {compared.Error}";
            }
        }
        var status = session.Status;
        return new GlFrame(
            session.Id.ToString("D"), session.Kind, status.ToString(), error, session.SourceId?.ToString("D"), session.ForkTick,
            GameRulesV1.TickRate, World(state, session.Context, session.LastStateHash), ghost, aligned,
            status == GlLiveStatus.Complete ? session.Id.ToString("D") : null, session.FinalStateHash);
    }
    // ---- Originals: open, restore (scrubber seek), branch (scrubber fork) ----------------------------------------------------
    private static async Task<IResult> OpenOriginal(Guid id, HttpContext http, IActorContext actor, GhostLapSoloServices services)
    {
        var opened = await services.Originals.OpenAsync(actor, new TimelineId(id), http.RequestAborted);
        if (!opened.TryGetValue(out var original))
        {
            return Failure(http, opened.Error);
        }
        services.Remember(id, original);
        var final = original.FinalState;
        // Markers are ticks the server read from the verified final state: the first pickups and the first Bursts.
        var markers = final.Collected.OrderBy(c => c.Tick).Take(MaxMarkers - 4)
            .Select(c => new GlMarker(c.Tick.ToString(CultureInfo.InvariantCulture), $"Candle {c.Id} collected"))
            .ToList();
        var id0 = id.ToString("D");
        return Results.Json(new GlOriginal(
            id0, id0, new GlBounds("0", original.FinalTick.ToString(CultureInfo.InvariantCulture)), ReadOnly: false,
            [.. markers.OrderBy(m => long.Parse(m.Position, CultureInfo.InvariantCulture))], HistoryStatus.Verified.ToString(),
            original.Seed, original.MascotId, original.Result.Scores[0].TotalScore, original.FinalStateHash, Arena()));
    }
    private static async Task<IResult> RestoreOriginal(Guid id, string? position, HttpContext http, IActorContext actor, GhostLapSoloServices services)
    {
        if (!TimelinePosition.TryParseWire(position, out var parsed))
        {
            return Failure(http, new SoloError(SoloErrorCode.InvalidTick, "Not a canonical position."));
        }
        var restored = await services.Originals.RestoreAsync(actor, new TimelineId(id), parsed.Value, http.RequestAborted);
        if (!restored.TryGetValue(out var v))
        {
            return Failure(http, restored.Error);
        }
        // Candle positions for drawing come from the original's pinned seed (read after the owner-checked restore succeeded).
        if (services.ContextOf(id) is not { } context)
        {
            var opened = await services.Originals.OpenAsync(actor, new TimelineId(id), http.RequestAborted);
            if (!opened.TryGetValue(out var original))
            {
                return Failure(http, opened.Error);
            }
            context = services.Remember(id, original);
        }
        return Results.Json(new
        {
            position = v.Position.ToString(CultureInfo.InvariantCulture),
            state = World(v.State, context, v.StateHash),
            hashOrigin = v.HashOrigin.ToString(),
            historyStatus = v.HistoryStatus.ToString(),
        });
    }
    private static async Task<IResult> CreateBranch(Guid id, HttpContext http, IActorContext actor, GhostLapSoloServices services, GhostLapSessionRegistry registry)
    {
        var ct = http.RequestAborted;
        var body = await ReadAsync<GlBranchBody>(http, 1024);
        var header = http.Request.Headers["Idempotency-Key"].ToString();
        if (body is null || !ValidKey(body.OperationKey) || header != body.OperationKey)
        {
            return Invalid(http);
        }
        if (!TimelinePosition.TryParseWire(body.Position, out var parsed))
        {
            return Failure(http, new SoloError(SoloErrorCode.InvalidTick, "Not a canonical position."));
        }
        var forked = await services.Branches.ForkAsync(actor, new TimelineId(id), parsed.Value, body.OperationKey!, ct);
        if (!forked.TryGetValue(out var branch))
        {
            return Failure(http, forked.Error);
        }
        var session = registry.GetOrAdd(GhostLapLiveSession.ForBranch(branch, actor.SubjectId));
        if (session is null)
        {
            return Busy(http);
        }
        return Results.Json(new
        {
            branchId = branch.Timeline.Value.ToString("D"),
            sourceId = id.ToString("D"),
            forkPosition = parsed.ToWireString(),
        });
    }
    // ---- Projection ---------------------------------------------------------------------------------------------------------
    public static GlArena Arena() => new(
        GameRulesV1.RulesVersion, GameRulesV1.LayoutVersion, GameRulesV1.TickRate, GameRulesV1.RoundTicks,
        GameRulesV1.WorldWidthUnits, GameRulesV1.WorldHeightUnits, GameRulesV1.ColliderHalfExtentFixed / GameRulesV1.FixedOne,
        GameRulesV1.CandleHalfExtentFixed / GameRulesV1.FixedOne, GameRulesV1.BurstRadiusFixed / GameRulesV1.FixedOne, GameRulesV1.MaxCharge,
        [.. ArenaV1.Obstacles.Select(o => new GlRect(o.X0 / GameRulesV1.FixedOne, o.Y0 / GameRulesV1.FixedOne, o.X1 / GameRulesV1.FixedOne, o.Y1 / GameRulesV1.FixedOne))]);
    private static GlWorld World(GameState state, GameContext context, string? stateHash)
    {
        var active = state.ActiveCandleIds.ToHashSet(StringComparer.Ordinal);
        var plan = context.Plan.Candles;
        return new GlWorld(
            state.Tick,
            stateHash,
            [.. state.Players.Select(p => new GlPlayer(
                p.Slot, p.MascotId, Units(p.XFixed), Units(p.YFixed), p.XFixed, p.YFixed, p.DistanceFixed.ToString(CultureInfo.InvariantCulture),
                p.Charge, p.IsOverlit, p.OverlitDeadlineTick, p.MovementPoints, p.PickupPoints, p.PenaltyPoints, p.TotalScore, p.Pickups,
                p.Bursts, p.LastSequence))],
            [.. plan.Where(c => active.Contains(c.Id)).Select(Candle)],
            [.. plan.Where(c => c.TelegraphTick <= state.Tick && state.Tick < c.ActivateTick).Select(Candle)],
            state.Collected.Length,
            [.. state.Events.Select(e => new GlEvent(e.Kind.ToString(), e.Slot, e.OtherSlot, e.Value, e.CandleId))],
            state.IsFinal);
    }
    private static GlCandle Candle(CandleSpec c) => new(c.Id, Units(c.Site.X), Units(c.Site.Y), c.ActivateTick, c.ExpireTick);
    private static double Units(int fixedValue) => fixedValue / (double)GameRulesV1.FixedOne;
    private static IResult InputAnswer(GhostLapLiveSession session, Result<GameInput, InputRejection> admitted)
    {
        var state = session.State;
        return Results.Json(new
        {
            accepted = admitted.IsSuccess,
            rejection = admitted.IsFailure ? ToSnake(admitted.Error.ToString()) : null,
            appliesToTick = admitted.IsSuccess ? admitted.Value.Tick : (long?)null,
            nextTick = state.Tick + 1,
            lastSequence = state.Players[0].LastSequence,
        });
    }
    // ---- Failures -----------------------------------------------------------------------------------------------------------
    private static async Task<T?> ReadAsync<T>(HttpContext http, int maxBytes) where T : class
    {
        if (http.Request.ContentLength is { } length && length > maxBytes)
        {
            return null;
        }
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(http.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }
    public static int Status(SoloErrorCode code) => code switch
    {
        SoloErrorCode.NotFound => StatusCodes.Status404NotFound,
        SoloErrorCode.Unauthenticated => StatusCodes.Status401Unauthorized,
        SoloErrorCode.InvalidRequest or SoloErrorCode.InvalidTick => StatusCodes.Status400BadRequest,
        SoloErrorCode.IncompatibleVersion => StatusCodes.Status422UnprocessableEntity,
        SoloErrorCode.Conflict or SoloErrorCode.NotComplete or SoloErrorCode.ForkNotAllowed => StatusCodes.Status409Conflict,
        SoloErrorCode.Cancelled => TimelineHttpErrors.ClientClosedRequest,
        SoloErrorCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
        SoloErrorCode.CorruptHistory or SoloErrorCode.VerificationFailed => StatusCodes.Status500InternalServerError,
        _ => throw new UnreachableException($"Unmapped Ghost Lap error code {code}."),
    };
    private static TimelineErrorCode? TimelineCodeOf(SoloErrorCode code) => code switch
    {
        SoloErrorCode.NotFound => TimelineErrorCode.NotFound,
        SoloErrorCode.InvalidTick => TimelineErrorCode.InvalidPosition,
        SoloErrorCode.IncompatibleVersion => TimelineErrorCode.IncompatibleVersion,
        SoloErrorCode.CorruptHistory or SoloErrorCode.VerificationFailed => TimelineErrorCode.CorruptHistory,
        SoloErrorCode.Conflict => TimelineErrorCode.Conflict,
        _ => null,
    };
    private static IResult Failure(HttpContext http, SoloError error)
    {
        var status = Status(error.Code);
        return Results.Json(new E2eTimelineProblem
        {
            Code = "ghostlap_" + ToSnake(error.Code.ToString()),
            Status = status,
            TraceId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            TimelineError = TimelineCodeOf(error.Code),
        }, statusCode: status);
    }
    private static IResult Invalid(HttpContext http) => Failure(http, new SoloError(SoloErrorCode.InvalidRequest, "invalid"));
    private static IResult NotFound(HttpContext http) => Failure(http, new SoloError(SoloErrorCode.NotFound, "No such session."));
    private static IResult Busy(HttpContext http) => Results.Json(
        new E2eTimelineProblem { Code = "ghostlap_too_many_sessions", Status = 503, TraceId = http.TraceIdentifier }, statusCode: 503);
    /// <summary>Same shape the domain enforces (it re-validates); checked here so a bad key is a 400 before any work.</summary>
    private static bool ValidKey([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= SoloOriginals.MaxOperationKeyLength
        && key.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.');
    private static string ToSnake(string pascal)
    {
        var builder = new StringBuilder();
        foreach (var c in pascal)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
