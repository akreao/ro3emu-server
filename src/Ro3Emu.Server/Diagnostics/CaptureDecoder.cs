using System.Text.Json;
using Google.Protobuf;
using MessagePack;
using Phonest.Proto;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>
/// Turns a client-side capture (Ro3Emu.ClientHooks TrafficCapture: {"t","dir","ch","idx","b"} lines with raw Phonest
/// RPC bodies) into the <see cref="TrafficLog"/> line format: {"t","dir","ch","id","name","body"}. Rpc indexes follow
/// Phonest.Proto: on the game gate (<see cref="GameGateServer"/>) c2s 3 ConnectServer, 4 EntityMessageIn; s2c 1
/// ConnectResponse, 2 EntityMessageOut, 4 CreateChannelEntity, 5 DisconnectClient; on the micro gate c2s 33 MsRequest,
/// s2c 34 MsResponse, 35 MsPush. Both start with the encryption handshake (c2s 1/2, s2c 10/11), whose bodies are left
/// out, as is the login token of ConnectServer.
/// </summary>
public sealed class CaptureDecoder(MsgCatalog catalog)
{
    private enum Kind { Unknown, Game, Micro }

    private const ushort ConnectServer = 3, EntityMessageIn = 4;
    private const ushort ConnectResponse = 1, EntityMessageOut = 2, CreateChannelEntity = 4, DisconnectClient = 5;
    private const ushort MicroRequest = 33, MicroResponse = 34, MicroPush = 35;

    private readonly Dictionary<int, Kind> _channels = [];

    /// <summary>Messages per direction, channel kind and name, for the summary.</summary>
    public SortedDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    /// <summary>One decoded line per frame; channel announcements and unreadable lines are skipped.</summary>
    public IEnumerable<string> Decode(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            string? decoded;
            try { decoded = DecodeLine(line); }
            catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                decoded = null;
            }
            if (decoded != null) yield return decoded;
        }
    }

    private string? DecodeLine(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        string direction = root.GetProperty("dir").GetString()!;
        if (direction is not ("c2s" or "s2c")) return null;
        int channel = root.GetProperty("ch").GetInt32();
        ushort index = (ushort)root.GetProperty("idx").GetInt32();
        var body = Convert.FromBase64String(root.GetProperty("b").GetString()!);
        var kind = Classify(channel, direction, index);
        var (id, name, text) = kind switch
        {
            Kind.Game => Game(direction, index, body),
            Kind.Micro => Micro(direction, index, body),
            _ => (0u, $"rpc {index}", Json($"{body.Length} bytes")),
        };
        string channelName = kind.ToString().ToLowerInvariant();
        Counts[$"{direction}\t{channelName}\t{id}\t{name}"] = Counts.GetValueOrDefault($"{direction}\t{channelName}\t{id}\t{name}") + 1;
        return $"{{\"t\":{JsonSerializer.Serialize(root.GetProperty("t").GetString(), Readable)},\"dir\":\"{direction}\",\"ch\":\"{channelName}\",\"id\":{id},\"name\":{JsonSerializer.Serialize(name, Readable)},\"body\":{text}}}";
    }

    /// <summary>A channel's kind from the first frame only one gate uses.</summary>
    private Kind Classify(int channel, string direction, ushort index)
    {
        var kind = _channels.GetValueOrDefault(channel);
        if (kind != Kind.Unknown) return kind;
        kind = (direction, index) switch
        {
            ("c2s", ConnectServer or EntityMessageIn) or ("s2c", ConnectResponse or EntityMessageOut or CreateChannelEntity) => Kind.Game,
            ("c2s", MicroRequest) or ("s2c", MicroResponse or MicroPush) => Kind.Micro,
            _ => Kind.Unknown,
        };
        if (kind != Kind.Unknown) _channels[channel] = kind;
        return kind;
    }

    private (uint Id, string Name, string Body) Game(string direction, ushort index, byte[] body) => (direction, index) switch
    {
        ("c2s", EntityMessageIn) or ("s2c", EntityMessageOut) => Entity(direction, EntityMessage.Parser.ParseFrom(body)),
        ("c2s", ConnectServer) => (0, "connectServer", Json(ConnectServerReq.Parser.ParseFrom(body).Type.ToString())),
        ("s2c", ConnectResponse) => (0, "connectResponse", JsonFormatter.Default.Format(ConnectServerResp.Parser.ParseFrom(body))),
        ("s2c", CreateChannelEntity) => (0, "createChannelEntity", Json(ChannelEntityInfo.Parser.ParseFrom(body).Type.ToStringUtf8())),
        ("s2c", DisconnectClient) => (0, "disconnectClient", Json($"{body.Length} bytes")),
        _ => (0, $"rpc {index}", Json($"{body.Length} bytes")),
    };

    /// <summary>Raw game messages are uint32 LE msgId + protobuf; named ones are msgpack RPCs ("EZAdaptorCS"(msgId, bin) c2s).</summary>
    private (uint, string, string) Entity(string direction, EntityMessage message)
    {
        if (!message.HasMethodName)
        {
            var parameters = message.Parameters.Memory;
            if (parameters.Length < 4) return (0, "entity", Json($"{parameters.Length} bytes"));
            uint msgId = BitConverter.ToUInt32(parameters.Span);
            return (msgId, catalog.Name(msgId), GameBody(direction, msgId, parameters[4..]));
        }
        string method = message.MethodName.ToStringUtf8();
        if (method == "EZAdaptorCS")
        {
            var reader = new MessagePackReader(message.Parameters.Memory);
            uint msgId = reader.ReadUInt32();
            var bytes = reader.ReadBytes();
            byte[] body = bytes.HasValue ? System.Buffers.BuffersExtensions.ToArray(bytes.Value) : [];
            return (msgId, catalog.Name(msgId), GameBody(direction, msgId, body));
        }
        return (0, method, Json(Args(message.Parameters.Memory)));
    }

    /// <summary>c2s bodies are the message's request type; s2c bodies its response type, or the request type of a push.</summary>
    private string GameBody(string direction, uint msgId, ReadOnlyMemory<byte> body)
    {
        var entry = catalog.Get(msgId);
        var type = direction == "c2s" ? entry?.Request : entry?.Response ?? entry?.Request;
        if (type == null || type.FullName == "romsg.NullArg") return Json($"{body.Length} bytes");
        try { return JsonFormatter.Default.Format(type.Parser.ParseFrom(body.Span)); }
        catch (InvalidProtocolBufferException) { return Json($"{body.Length} bytes (unparsable as {type.Name})"); }
    }

    private static (uint, string, string) Micro(string direction, ushort index, byte[] body)
    {
        switch (direction, index)
        {
            case ("c2s", MicroRequest):
                var request = MsRequest.Parser.ParseFrom(body);
                return (0, $"{request.Service}.{request.Method}", Json($"rid {request.Rid}: {Args(request.Params.Memory)}"));
            case ("s2c", MicroResponse):
                var response = MsResponse.Parser.ParseFrom(body);
                return (0, "response", Json($"rid {response.Rid}: {Args(response.Params.Memory)}"));
            case ("s2c", MicroPush):
                var push = MsPush.Parser.ParseFrom(body);
                return (0, $"{push.Service}.{push.Method}", Json(Args(push.Params.Memory)));
            default:
                return (0, $"rpc {index}", Json($"{body.Length} bytes"));
        }
    }

    /// <summary>msgpack arguments, readable, with login secrets redacted (<see cref="MicroServiceRouter.Describe"/>).</summary>
    private static string Args(ReadOnlyMemory<byte> packed)
    {
        if (packed.IsEmpty) return "nil";
        try
        {
            var values = new List<object?>();
            var reader = new MessagePackReader(packed);
            while (!reader.End) values.Add(MessagePackSerializer.Deserialize<object>(ref reader, MicroServiceRouter.Untrusted));
            return string.Join(", ", values.Select(v => MicroServiceRouter.Describe(v)));
        }
        catch (MessagePackSerializationException) { return $"{packed.Length} bytes (not msgpack)"; }
    }

    /// <summary>A JSON string; a log for people, so &lt;, &gt; and non-ASCII text stay as they are.</summary>
    private static string Json(string text) => JsonSerializer.Serialize(text, Readable);

    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
