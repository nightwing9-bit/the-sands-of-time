using GhostLap.Rules;

namespace GhostLap.Simulation;

internal static partial class GhostLapPhases
{
    private static void ResolveSharedContact(PlayerState[] players, long[] pairs, long tick, List<GameEvent> events, bool[]? forfeited)
    {
        var n = players.Length;
        Span<int> charge = stackalloc int[n];
        for (var slot = 0; slot < n; slot++)
        {
            charge[slot] = players[slot].Charge;
        }

        // All pair intents compare the same charges. Clamping can change attribution of the last unit,
        // but every eligible pair enters cooldown and the victims' total losses are order independent.
        for (var a = 0; a < n; a++)
        {
            for (var b = a + 1; b < n; b++)
            {
                var k = GameState.PairIndex(a, b, n);
                if (Masked(forfeited, a) || Masked(forfeited, b) || pairs[k] > tick || charge[a] == charge[b]
                    || !ArenaV1.ColliderAt(players[a].XFixed, players[a].YFixed)
                        .Overlaps(ArenaV1.ColliderAt(players[b].XFixed, players[b].YFixed)))
                {
                    continue;
                }
                var (weak, strong) = charge[a] < charge[b] ? (a, b) : (b, a);
                var before = players[weak].Charge;
                players[weak] = Drain(players[weak], GameRulesV1.ContactChargeLoss, events);
                events.Add(new GameEvent(GameEventKind.ContactDrain, (byte)weak, (byte)strong, before - players[weak].Charge));
                pairs[k] = tick + GameRulesV1.ContactCooldownTicks;
            }
        }
    }

    private static void ResolveSharedBursts(PlayerState[] players, bool[] burstEdge, long tick, List<GameEvent> events, bool[]? forfeited)
    {
        Span<GameEventKind> intents = stackalloc GameEventKind[players.Length];
        intents.Clear();
        for (var slot = 0; slot < players.Length; slot++)
        {
            var p = players[slot];
            if (!Masked(forfeited, slot) && p.IsOverlit)
            {
                intents[slot] = burstEdge[slot] ? GameEventKind.BurstManual
                    : p.OverlitDeadlineTick <= tick ? GameEventKind.BurstAuto : default;
                if (intents[slot] != default)
                {
                    // An initiated Burst spends charge even if its owner also takes a hit.
                    players[slot] = p with { Charge = 0, OverlitDeadlineTick = 0 };
                }
            }
        }
        ReadOnlySpan<GameEventKind> orderedKinds = [GameEventKind.BurstManual, GameEventKind.BurstAuto];
        foreach (var kind in orderedKinds)
        {
            for (var slot = 0; slot < players.Length; slot++)
            {
                if (intents[slot] == kind)
                {
                    Fire(players, slot, kind, events, forfeited);
                }
            }
        }
    }
}
