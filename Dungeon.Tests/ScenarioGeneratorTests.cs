using Dungeon.Core;
using Dungeon.Entities;

namespace Dungeon.Tests;

public class ScenarioGeneratorTests
{
    private static readonly int[] Seeds = Enumerable.Range(1, 1000).ToArray();

    public static TheoryData<string> Tiers => new() { "tier1", "tier2", "tier3" };

    private static ScenarioOptions Tier(string name) => name switch
    {
        "tier1" => ScenarioOptions.Tier1,
        "tier2" => ScenarioOptions.Tier2,
        _ => ScenarioOptions.Tier3
    };

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Every_seed_places_the_requested_obstacles(string tier)
    {
        var o = Tier(tier);
        var failures = new List<string>();
        foreach (var seed in Seeds)
        {
            try
            {
                var s = ScenarioGenerator.Generate(seed, o);
                if (s.Guards.Count != o.Guards) failures.Add($"{seed}: guards {s.Guards.Count}");
                if (s.Doors.Count(d => d.Kind == DoorKind.Locked) != o.LockedDoors) failures.Add($"{seed}: locked");
                if (s.Doors.Count(d => d.Kind == DoorKind.Jammed) != o.JammedDoors) failures.Add($"{seed}: jammed");
            }
            catch (InvalidOperationException e)
            {
                failures.Add(e.Message);
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} failures, e.g. {string.Join("; ", failures.Take(5))}");
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void Every_obstacle_actually_separates_start_from_exit(string tier)
    {
        foreach (var seed in Seeds)
        {
            var s = ScenarioGenerator.Generate(seed, Tier(tier));
            var obstacles = s.Doors.Select(d => d.Position).Concat(s.Guards.Select(g => g.Position));
            foreach (var cell in obstacles)
                Assert.DoesNotContain(s.Map.Exit,
                    ScenarioGenerator.Reachable(s.Map, s.Map.Start, new HashSet<Cell> { cell }));
        }
    }

    // Solvability by construction: walking obstacles in order of distance, the item each
    // one needs must be reachable while it and all later obstacles are still closed.
    [Theory]
    [MemberData(nameof(Tiers))]
    public void Required_items_are_reachable_before_their_obstacle(string tier)
    {
        foreach (var seed in Seeds)
        {
            var s = ScenarioGenerator.Generate(seed, Tier(tier));
            var ordered = ScenarioGenerator.ChokepointsInOrder(s.Map);
            var obstacles = s.Doors.Select(d => d.Position).Concat(s.Guards.Select(g => g.Position))
                .OrderBy(ordered.IndexOf).ToList();

            for (var i = 0; i < obstacles.Count; i++)
            {
                var region = ScenarioGenerator.Reachable(s.Map, s.Map.Start, obstacles.Skip(i).ToHashSet());
                var door = s.Doors.FirstOrDefault(d => d.Position == obstacles[i]);
                if (door is { Kind: DoorKind.Locked })
                {
                    var key = s.Items.Single(it => it.Opens == door.Id);
                    Assert.True(region.Contains(key.Position!.Value), $"seed {seed}: {key} behind {door}");
                }
                if (s.Guards.Any(g => g.Position == obstacles[i]))
                    Assert.True(s.Items.Any(it => it.Type == ItemType.Food && region.Contains(it.Position!.Value)),
                        $"seed {seed}: no food before guard");
            }
        }
    }

    [Fact]
    public void Items_do_not_share_cells_with_each_other_or_obstacles()
    {
        foreach (var seed in Seeds)
        {
            var s = ScenarioGenerator.Generate(seed, ScenarioOptions.Tier3);
            var cells = s.Items.Select(i => i.Position!.Value)
                .Concat(s.Doors.Select(d => d.Position))
                .Concat(s.Guards.Select(g => g.Position))
                .Append(s.Map.Start).Append(s.Map.Exit)
                .ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count());
        }
    }

    [Fact]
    public void Same_seed_gives_identical_scenario()
    {
        foreach (var seed in Seeds.Take(50))
        {
            var a = ScenarioGenerator.Generate(seed, ScenarioOptions.Tier3);
            var b = ScenarioGenerator.Generate(seed, ScenarioOptions.Tier3);
            Assert.Equal(Describe(a), Describe(b));
        }
    }

    [Fact]
    public void Guard_traits_vary_across_seeds_within_ranges()
    {
        var o = ScenarioOptions.Tier1;
        var traits = Seeds.Take(200).Select(s => ScenarioGenerator.Generate(s, o).Guards.Single().Traits).ToList();
        Assert.All(traits, t => Assert.InRange(t.CalmChance, o.CalmMin, o.CalmMax));
        Assert.All(traits, t => Assert.InRange(t.MaxPatience, o.MinPatience, o.MaxPatience));
        Assert.True(traits.Select(t => t.CalmChance).Distinct().Count() > 150);
    }

    // Scripted oracle: proves the generated tier-2 dungeons are solvable through the real
    // world API, not only by reachability. Picks up everything, opens doors with keys,
    // pushes jammed doors until they give.
    [Fact]
    public void Scripted_solver_clears_every_tier2_seed()
    {
        foreach (var seed in Seeds.Take(200))
        {
            var world = new DungeonWorld(ScenarioGenerator.Generate(seed, ScenarioOptions.Tier2), seed);
            ScriptedSolver.Run(world, maxTicks: 2000);
            Assert.True(world.Status == EpisodeStatus.Succeeded, $"seed {seed}: {world.Status} at tick {world.Tick}");
        }
    }

    private static string Describe(Scenario s) =>
        string.Join("|", s.Doors.Select(d => $"{d.Id}@{d.Position}:{d.Kind}:{d.PickChance}:{d.PushChance}")
            .Concat(s.Guards.Select(g => $"{g.Id}@{g.Position}:{g.Traits}"))
            .Concat(s.Items.Select(i => $"{i.Id}@{i.Position}:{i.Opens}")));
}
