using System.Text.Json;
using GhostLap.Simulation;
using GhostLap.Multiplayer;
using Xunit;

namespace GhostLap.Tests.Multiplayer;

public sealed class FairnessEvidenceFactAttribute : FactAttribute
{
    public FairnessEvidenceFactAttribute(string variable)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = $"Set {variable} to an evidence output path.";
        }
    }
}

public sealed class FairnessRemediationTests
{
    [FairnessEvidenceFact("GHOSTLAP_ABLATION_OUT")]
    public void Phase_ablation_and_spawn_rotation()
    {
        using var writer = File.CreateText(Environment.GetEnvironmentVariable("GHOSTLAP_ABLATION_OUT")!);
        for (var n = 2; n <= 4; n++)
        {
            for (var i = 0; i < 32; i++)
            {
                var seed = 1000 + 7919L * i;
                foreach (var phases in Enum.GetValues<CompetitivePhases>().Append(CompetitivePhases.Pickups | CompetitivePhases.Burst))
                {
                    var players = Enumerable.Range(0, n).Select(_ => new RoutedSeeker(true)).ToArray();
                    writer.WriteLine(JsonSerializer.Serialize(Playtest.Run(seed, players, phases.ToString(), phases: phases, reverseInputs: true)));
                }
                for (var shift = 1; shift < n; shift++)
                {
                    var players = Enumerable.Range(0, n).Select(_ => new RoutedSeeker(true)).ToArray();
                    writer.WriteLine(JsonSerializer.Serialize(Playtest.Run(seed, players, $"spawn+{shift}", spawnRotation: shift)));
                }
            }
        }
    }
    [FairnessEvidenceFact("GHOSTLAP_PERSONA_OUT")]
    public void Rotate_script_preferences_independently_of_the_spawn()
    {
        using var writer = File.CreateText(Environment.GetEnvironmentVariable("GHOSTLAP_PERSONA_OUT")!);
        for (var n = 2; n <= 4; n++)
        {
            for (var i = 0; i < 64; i++)
            {
                var seed = 1000 + 7919L * i;
                for (var shift = 0; shift < n; shift++)
                {
                    var slots = Enumerable.Range(0, n);
                    var rotated = slots.Select(s => new RoutedSeeker(true, orientation: (s + shift) % n)).ToArray();
                    writer.WriteLine(JsonSerializer.Serialize(Playtest.Run(seed, rotated, $"orientation+{shift}", phases: CompetitivePhases.All)));
                }
                if (n == 3)
                {
                    for (var shift = 0; shift < n; shift++)
                    {
                        var scripts = Enumerable.Range(0, n).Select(s => new CandleSeeker(true, legacyOrientation: (s + shift) % n)).ToArray();
                        writer.WriteLine(JsonSerializer.Serialize(Playtest.Run(seed, scripts, $"greedy-orientation+{shift}",
                            rulesId: MpRulesProfile.CurrentRulesId)));
                    }
                }
                foreach (var (name, make) in Policies())
                {
                    var scripts = Enumerable.Range(0, n).Select(s => make((s + i) % n)).ToArray();
                    writer.WriteLine(JsonSerializer.Serialize(Playtest.Run(seed, scripts, name, phases: CompetitivePhases.All)));
                }
            }
        }
    }

    [FairnessEvidenceFact("GHOSTLAP_HOLDOUT_OUT")]
    public void Independent_holdout_compares_sealed_profiles_and_rotates_policies()
    {
        using var writer = File.CreateText(Environment.GetEnvironmentVariable("GHOSTLAP_HOLDOUT_OUT")!);
        for (var n = 2; n <= 4; n++)
        {
            for (var i = 0; i < 512; i++)
            {
                var seed = 91234567 + 104729L * i;
                var slots = Enumerable.Range(0, n);
                var offset = (i / n + i % n) % n;
                foreach (var (name, make) in Policies())
                {
                    foreach (var rules in new[] { MpRulesProfile.RulesId, MpRulesProfile.CurrentRulesId })
                    {
                        var scripts = slots.Select(s => make((s + offset) % n)).ToArray();
                        writer.WriteLine(JsonSerializer.Serialize(Playtest.Run(seed, scripts, $"{rules}/{name}",
                            reverseInputs: (i & 1) == 1, rulesId: rules)));
                    }
                }

                if (i < 128)
                {
                    foreach (var (name, make) in new (string, Func<ScriptedPlayer>)[]
                    {
                        ("corner", () => new CornerFarmer()),
                        ("lane", () => new LaneFarmer()),
                        ("evade", () => new RoutedSeeker(false, preference: 12, persona: offset)),
                    })
                    {
                        foreach (var slot in slots)
                        {
                            var mix = slots.Select(s => s == slot ? make() :
                                new RoutedSeeker(true, preference: 12, persona: (s + offset) % n)).ToArray();
                            writer.WriteLine(JsonSerializer.Serialize(Playtest.Run(seed, mix,
                                $"{MpRulesProfile.CurrentRulesId}/mix-{name}", rulesId: MpRulesProfile.CurrentRulesId)));
                        }
                    }
                }
            }
        }
    }

    [FairnessEvidenceFact("GHOSTLAP_NEAREST_OUT")]
    public void Nearest_only_holdout_varies_route_ties_without_changing_target_cost()
    {
        using var writer = File.CreateText(Environment.GetEnvironmentVariable("GHOSTLAP_NEAREST_OUT")!);
        for (var n = 2; n <= 4; n++)
        {
            for (var i = 0; i < 512; i++)
            {
                var seed = 91234567 + 104729L * i;
                var offset = (i / n + i % n) % n;
                foreach (var rules in new[] { MpRulesProfile.RulesId, MpRulesProfile.CurrentRulesId })
                {
                    var scripts = Enumerable.Range(0, n).Select(slot => new RoutedSeeker(true, (slot + offset) % n)).ToArray();
                    writer.WriteLine(JsonSerializer.Serialize(Playtest.Run(seed, scripts, $"{rules}/nearest", rulesId: rules)));
                }
            }
        }
    }

    private static (string Name, Func<int, ScriptedPlayer> Create)[] Policies() =>
    [
        ("routed", p => new RoutedSeeker(true, p, preference: 12)),
        ("perturbed", p => new RoutedSeeker(true, p, preference: 24)),
        ("greedy", p => new CandleSeeker(true, p)),
        ("original-greedy", _ => new CandleSeeker(true)),
    ];

}
