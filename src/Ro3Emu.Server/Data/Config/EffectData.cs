using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>A ground warning the client draws for a server-placed skill mark (EffectData m_kPoint.m_kWarningRange).</summary>
/// <param name="Circle">m_eShapeType 1: a ring of <paramref name="Radius"/>; otherwise a strip <paramref name="Length"/>
/// long and <paramref name="Width"/> wide from its position along its angle.</param>
/// <param name="DurationMs">m_fDuration: how long the warning shows.</param>
public sealed record WarningRange(bool Circle, float Radius, float Length, float Width, int DurationMs);

/// <summary>The client's Lua_Config_EffectData (data/skills/Lua_Config_EffectData.json: effect id -> definition).</summary>
public static class EffectData
{
    private const string Table = "Lua_Config_EffectData";

    public static WarningRange? Warning(GameData data, long effectId)
    {
        if (Point(data, effectId) is not { } point || !point.TryGetProperty("m_kWarningRange", out var w)
            || w.ValueKind != JsonValueKind.Object) return null;
        float F(string name) => w.TryGetProperty(name, out var v) && GameData.Number(v) is { } n ? (float)n : 0;
        return new WarningRange(F("m_eShapeType") == 1, F("m_fRadius"), F("m_fLength"), F("m_fWidth"),
            (int)Math.Round(F("m_fDuration") * 1000));
    }

    /// <summary>A displacement effect's move time (m_kPoint.m_iMovementTime, ms; 0: not a displacement).</summary>
    public static int MovementMs(GameData data, long effectId) =>
        Point(data, effectId) is { } point && point.TryGetProperty("m_bMovement", out var moves) && moves.ValueKind == JsonValueKind.True
            && point.TryGetProperty("m_iMovementTime", out var ms) && GameData.Number(ms) is { } n ? (int)n : 0;

    /// <summary>Whether the effect exists (any kind).</summary>
    public static bool Exists(GameData data, long effectId) =>
        data.SkillTemplateTable(Table).ContainsKey(effectId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static JsonElement? Point(GameData data, long effectId) =>
        data.SkillTemplateTable(Table).TryGetValue(effectId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var e)
            && e.TryGetProperty("m_kPoint", out var point) && point.ValueKind == JsonValueKind.Object ? point : null;
}
