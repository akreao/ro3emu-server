namespace Ro3Emu.Server.Game;

/// <summary>
/// A won solo story run (an ordinary dungeon, not a multi/team one) sends its players back out once it is over. The
/// client has no story-scene exit script and shows no result window for these scenes, so without this the player had to
/// find the exit button on their own. Genesis capture 2026-10-25 (one run of each): no countdown and no notice; the
/// Egg Thief run (1021301) moved the player out 250 ms after its result, the Eddga run (1022001) about 3 s after its
/// last closing line. Genesis is a private server that differs from the official beta.
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>How long after the result a won story run with nothing left to say sends its players out (Genesis).</summary>
    private const int AutoLeaveMs = 250;
    /// <summary>How long after its last closing line a won story run sends its players out (Genesis).</summary>
    private const int AutoLeaveAfterLinesMs = 3000;

    private Task ScheduleAutoLeaveAsync(Attempt a)
    {
        if (a.Plan.Multi != null || a.GuildChallenge != null || a.Dracula != null || a.Room.Closed) return Task.CompletedTask;
        var members = Present(a).Where(a.Loaded.Contains).ToArray();
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(AutoLeaveDelayMs(a)), async () =>
        {
            foreach (var s in members)
            {
                // Only a player still in this run's room, settled and not loading, is sent out (the same path as
                // pressing the exit button).
                if (!ReferenceEquals(s.DungeonAttempt, a) || s.InstanceId != a.Room.Id || s.SceneLoading || s.Role == null) continue;
                try { await LeaveAsync(s); }
                catch (Exception e) { _log.LogWarning(e, "Dungeon auto leave failed for {Role}, instance {Instance}", s.Role?.Id, a.Room.Id); }
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>A story encounter's closing lines are said first; otherwise the players leave at once.</summary>
    private static int AutoLeaveDelayMs(Attempt a) => a.Encounter?.Def.WinLines is { Count: > 0 } lines
        ? lines.Max(l => l.Ms) + AutoLeaveAfterLinesMs
        : AutoLeaveMs;
}
