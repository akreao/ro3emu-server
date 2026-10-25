using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The Fly Wing's random spots (MapWorld.RandomSpots) stay inside the part of the map players can walk to.</summary>
public sealed class FlyWingSpotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-flywing-" + Guid.NewGuid().ToString("N"));

    public FlyWingSpotTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task SpotsOnGroundCutOffFromTheArrivalsAreLeftOut()
    {
        // x 0-7 is the play area; x 8 is a wall; x 9-19 is a walkable strip behind it, like Payon Forest's north edge.
        var nav = Grid(20, 10, (x, _) => x == 8 ? null : 0f);
        var map = Map(nav, new MapPoint(2, 0, 2, 0), new MapPoint(5, 0, 5, 0), new MapPoint(15, 0, 5, 0));
        Assert.Equal([new MapPoint(5, 0, 5, 0)], await map.WarmRandomSpots());
        Assert.Equal([new MapPoint(5, 0, 5, 0)], map.RandomSpots);
    }

    /// <summary>#274: the flood fill runs off the game loop; until it has finished every walkable spot is used.</summary>
    [Fact]
    public async Task BeforeTheFloodFillFinishesEveryWalkableSpotCounts()
    {
        var nav = Grid(20, 10, (x, _) => x == 8 ? null : 0f);
        var map = Map(nav, new MapPoint(2, 0, 2, 0), new MapPoint(5, 0, 5, 0), new MapPoint(15, 0, 5, 0), new MapPoint(8, 0, 5, 0));
        Assert.Equal([new MapPoint(5, 0, 5, 0), new MapPoint(15, 0, 5, 0)], map.RandomSpots);
        Assert.Same(map.WarmRandomSpots(), map.WarmRandomSpots()); // started once
        await map.WarmRandomSpots();
        Assert.Equal([new MapPoint(5, 0, 5, 0)], map.RandomSpots);
    }

    [Fact]
    public async Task ACliffTallerThanAStepCutsTheGroundOff()
    {
        // The strip is walkable and touches the play area, but 3 m up: more than the 2 m a walk can climb.
        var nav = Grid(20, 10, (x, _) => x < 10 ? 0f : 3f);
        var map = Map(nav, new MapPoint(2, 0, 2, 0), new MapPoint(5, 0, 5, 0), new MapPoint(15, 3, 5, 0));
        Assert.Equal([new MapPoint(5, 0, 5, 0)], await map.WarmRandomSpots());

        // A slope of 1 m steps joins them.
        var slope = Map(Grid(20, 10, (x, _) => x < 10 ? 0f : Math.Min(3f, x - 9)), new MapPoint(2, 0, 2, 0),
            new MapPoint(5, 0, 5, 0), new MapPoint(15, 3, 5, 0));
        Assert.Equal(2, (await slope.WarmRandomSpots()).Count);
    }

    [Fact]
    public async Task WithoutAnArrivalOnWalkableGroundEverySpotCounts()
    {
        var nav = Grid(20, 10, (x, _) => x == 8 ? null : 0f);
        var map = Map(nav, new MapPoint(8, 0, 2, 0), new MapPoint(5, 0, 5, 0), new MapPoint(15, 0, 5, 0));
        Assert.Equal(2, (await map.WarmRandomSpots()).Count);
    }

    /// <summary>The grid never changes, so seeds on the same layers reuse one flood fill (every test server shares it).</summary>
    [Fact]
    public void SeedsOnTheSameLayersShareOneFloodFill()
    {
        var nav = Grid(20, 10, (x, _) => x == 8 ? null : 0f);
        var reach = nav.Reachable([(2f, 0f, 2f), (5.5f, 0f, 5.5f)]);
        Assert.Same(reach, nav.Reachable([(5.2f, 0f, 5.7f), (2.9f, 0f, 2.1f), (2.5f, 0f, 2.5f)]));
        Assert.NotSame(reach, nav.Reachable([(15f, 0f, 5f)]));
        Assert.True(nav.Reachable([(15f, 0f, 5f)]).Contains(12, 0, 3));
        Assert.False(reach.Contains(12, 0, 3));
    }

    [ClientDataFact]
    public async Task PayonForestNeverLandsOnItsNorthernEdge()
    {
        var options = Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") });
        var world = new WorldData(options, new GameData(options, NullLogger<GameData>.Instance), NullLogger<WorldData>.Instance);
        var spots = await world.Map(10220)!.WarmRandomSpots(); // started by the load
        Assert.NotEmpty(spots);
        // The cut-off strip holds about 30 authored points at z 398-399.
        Assert.DoesNotContain(spots, p => p.Z > 397);
    }

    private static MapWorld Map(NavGrid nav, MapPoint born, params MapPoint[] points) => new()
    {
        SceneEntry = 1,
        Born = born,
        Nav = nav,
        Points = points.Select((p, i) => (p, i)).ToDictionary(t => (long)t.i + 1, t => t.p),
    };

    /// <summary>A one-block nav grid in the client's format (see <see cref="NavGrid"/>): a null height is unwalkable.</summary>
    private NavGrid Grid(int width, int height, Func<int, int, float?> ground)
    {
        const int header = 48, block = 30;
        var bytes = new byte[header + 16 + block * block * 4];
        BitConverter.TryWriteBytes(bytes.AsSpan(20), width);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), height);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), block);
        BitConverter.TryWriteBytes(bytes.AsSpan(header), header + 16);
        for (int x = 0; x < width; x++)
            for (int z = 0; z < height; z++)
            {
                int cell = header + 16 + (z * block + x) * 4;
                if (ground(x, z) is not { } h) continue;
                bytes[cell] = 0x80;
                BitConverter.TryWriteBytes(bytes.AsSpan(cell + 2), (short)(h * 100));
            }
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bytes");
        File.WriteAllBytes(path, bytes);
        return NavGrid.Load(path);
    }
}
