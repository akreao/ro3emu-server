using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Ground marks of a story boss skill (story-encounters.json "marks" and "moves"), as the Genesis capture of Payon Forest
/// shows Eddga's: the server places each mark and sends its warning (31109), the client draws the EffectData
/// m_kWarningRange ring or strip, and the hit lands in that shape when the warning ends (or at the mark's hitMs).
/// </summary>
public sealed partial class ReconstructedDungeons
{
    /// <summary>What a mark's angles turn with: the boss's facing, the cast's target, or a random enemy in the fight.</summary>
    public enum MarkAim { Facing, Target, Random }

    /// <summary>
    /// <paramref name="Count"/> marks <paramref name="Ms"/> after the cast starts, each with warning effect
    /// <paramref name="Warn"/>: placed <paramref name="Distance"/> m from the boss (or from mark <paramref name="Around"/>)
    /// at <paramref name="Angle"/> degrees, a strip pointing <paramref name="Dir"/> degrees; both relative to the
    /// <paramref name="Aim"/> direction (to north with <paramref name="World"/>), and turned by <paramref name="Step"/> per
    /// mark. The hit comes <paramref name="HitMs"/> later (0: when the warning ends) with the damage effect
    /// <paramref name="Show"/> (0: none).
    /// </summary>
    public sealed record EncounterMark(int Ms, int Warn, int Show, int HitMs, int Around, float Distance, float Angle, float Dir,
        bool World, MarkAim Aim, int Count, float Step);

    /// <summary>The boss dashes to mark <paramref name="To"/>'s first position <paramref name="Ms"/> after the cast starts
    /// (at most <paramref name="Distance"/> m of the way; 0: all of it).</summary>
    public sealed record EncounterMove(int Ms, int To, float Distance);

    private static EncounterMark[] Marks(JsonElement skill) => skill.TryGetProperty("marks", out var list)
        ? [.. list.EnumerateArray().Select(m => new EncounterMark(JInt(m, "ms"), JInt(m, "warn"), JInt(m, "show"), JInt(m, "hitMs"),
            JInt(m, "around", -1), JFloat(m, "distance"), JFloat(m, "angle"), JFloat(m, "dir"),
            m.TryGetProperty("world", out var world) && world.GetBoolean(),
            m.TryGetProperty("aim", out var aim) ? Enum.Parse<MarkAim>(aim.GetString()!, ignoreCase: true) : MarkAim.Facing,
            JInt(m, "count", 1), JFloat(m, "step")))] : [];

    private static EncounterMove[] Moves(JsonElement skill) => skill.TryGetProperty("moves", out var list)
        ? [.. list.EnumerateArray().Select(m => new EncounterMove(JInt(m, "ms"), JInt(m, "to"), JFloat(m, "distance")))] : [];

    private static int JInt(JsonElement e, string name, int fallback = 0) => e.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;

    private static float JFloat(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetSingle() : 0;
}
