using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// How a scripted step moves its players besides walking:
/// <list type="bullet">
/// <item>A slide (step "slide"): the client's own LC_Movement_Controller_Slide, which the player's movement
/// (LC_Movement_AStarAgentNew.SetCustomData) creates for the local player on its first slide custom data. The server
/// pushes Player_SlideSpeed (5400003, ListLong [side, down] in cm/s) and then Player_SlideState (5400001, Int 1) in
/// 31018 CustomDataChange; the client then slides the player downhill, the A/D keys steer and the position is
/// reported as walks every 0.1 s. State 0 stops it. The pushes are not stored as the role's custom data: a slide never
/// outlives its step.</item>
/// <item>A fallback (step "fallback"): the step completes by itself after its time, so a slide that never reaches its
/// region cannot block the run.</item>
/// </list>
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>LC_Movement_Controller_Slide's custom data ids (enum_CustomData Player_SlideState, Player_SlideSpeed).</summary>
    internal const long SlideStateData = 5400001, SlideSpeedData = 5400003;

    private async Task StartSlideAsync(Attempt a, ReconstructedDungeons.StepSlide slide)
    {
        foreach (var s in Present(a).Where(m => a.Loaded.Contains(m) && m.Role != null))
        {
            var speed = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong };
            speed.ListLongParam.Add([(long)MathF.Round(slide.Side * 100), (long)MathF.Round(slide.Down * 100)]);
            // Speed first: the controller enters the slide on state 1 with the speed it already has.
            await SendSlideAsync(a, s, new CustomData { Id = SlideSpeedData, CustomParam = speed }, SlideState(1));
            a.Sliding.Add(s);
        }
    }

    private static CustomData SlideState(long state) =>
        new() { Id = SlideStateData, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Int, IntParam = state } };

    private async Task SendSlideAsync(Attempt a, GameSession s, params CustomData[] values)
    {
        var change = new CustomDataChange { EntityId = s.Role!.Id, CustomDatas = { values } };
        await SendQuietAsync(s, WorldCoreService.CustomChange, change);
        foreach (var viewer in Present(a).Where(v => v != s && a.Loaded.Contains(v)))
            await SendQuietAsync(viewer, WorldCoreService.CustomChange, change);
    }

    private async Task StopSlideAsync(Attempt a, GameSession s)
    {
        if (!a.Sliding.Remove(s) || s.Role == null) return;
        await SendSlideAsync(a, s, SlideState(0));
    }

    /// <summary>A step ends: its slides stop.</summary>
    private async Task EndStepMovementAsync(Attempt a)
    {
        foreach (var s in a.Sliding.ToArray()) await StopSlideAsync(a, s);
    }

    /// <summary>A participant leaves the room: nothing of the run's movement follows them out.</summary>
    private Task EndMovementAsync(Attempt a, GameSession s) => StopSlideAsync(a, s);

    private void ScheduleFallback(Attempt a, ReconstructedDungeons.StepFallback fallback)
    {
        int wave = a.Wave;
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(fallback.Ms), async () =>
        {
            if (a.Wave != wave || a.StepDone || a.Settled || a.Room.Closed || Step(a) is not { } step) return;
            if (Present(a).FirstOrDefault(a.Loaded.Contains) is not { } s) return;
            _log.LogInformation("Scripted scene {Scene} step {Step}: fallback after {Ms} ms", a.Plan.SceneEntry, wave + 1, fallback.Ms);
            await EndStepMovementAsync(a);
            if (fallback.Teleport != 0 && _catalog.ScenePoint(a.Plan.SceneEntry, fallback.Teleport) is { } to)
                foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading))
                    await _scenes.TeleportInMapAsync(member, to);
            var t = step.Complete[0];
            await StepEventAsync(a, s, [(t.Kind, t.Id)]);
        });
    }
}
