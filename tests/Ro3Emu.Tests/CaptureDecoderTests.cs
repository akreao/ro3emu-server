using Google.Protobuf;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phonest.Proto;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class CaptureDecoderTests : IDisposable
{
    private const uint SceneLoadedId = 40001; // invented id for the test's own message table
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-capture-" + Guid.NewGuid().ToString("N"));

    private CaptureDecoder Create()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "msgtable.tsv"),
            $"{SceneLoadedId}\tSCENE_LOADED\t{SceneLoadedReq.Descriptor.FullName}\t{SceneLoadedResp.Descriptor.FullName}\n");
        return new CaptureDecoder(new MsgCatalog(Options.Create(new ServerOptions { DataDir = _dir }), NullLogger<MsgCatalog>.Instance));
    }

    private static string Frame(string dir, int channel, int index, IMessage body) =>
        $"{{\"t\":\"2026-10-25T15:00:00.000Z\",\"dir\":\"{dir}\",\"ch\":{channel},\"idx\":{index},\"b\":\"{Convert.ToBase64String(body.ToByteArray())}\"}}";

    private static byte[] Raw(uint msgId, IMessage body) => [.. BitConverter.GetBytes(msgId), .. body.ToByteArray()];

    [Fact]
    public void GameMessagesAreNamedAndParsedByDirection()
    {
        var decoder = Create();
        var lines = decoder.Decode([
            "{\"t\":\"2026-10-25T15:00:00.000Z\",\"dir\":\"channel\",\"ch\":1,\"type\":\"Phonest.Rpc.ProtobufChannel\"}",
            Frame("c2s", 1, 4, new EntityMessage { Parameters = ByteString.CopyFrom(Raw(SceneLoadedId, new SceneLoadedReq { SceneId = 10101 })) }),
            Frame("s2c", 1, 2, new EntityMessage { Parameters = ByteString.CopyFrom(Raw(SceneLoadedId, new SceneLoadedResp())) }),
            Frame("s2c", 1, 2, new EntityMessage { MethodName = ByteString.CopyFromUtf8("OnQueueFinishedNotify") }),
        ]).ToList();

        Assert.Equal(3, lines.Count);
        Assert.Contains("\"dir\":\"c2s\",\"ch\":\"game\",\"id\":40001,\"name\":\"SCENE_LOADED\"", lines[0]);
        Assert.Matches("\"sceneId\": \"?10101", lines[0]);
        Assert.Contains("\"dir\":\"s2c\",\"ch\":\"game\",\"id\":40001", lines[1]);
        Assert.Contains("\"name\":\"OnQueueFinishedNotify\",\"body\":\"nil\"", lines[2]);
        Assert.Equal(1, decoder.Counts["c2s\tgame\t40001\tSCENE_LOADED"]);
    }

    [Fact]
    public void MicroCallsShowServiceAndMethodWithSecretsRedacted()
    {
        var decoder = Create();
        var args = MessagePackSerializer.Serialize(new Dictionary<string, object> { ["Token"] = "secret", ["Lv"] = 3 });
        var line = Assert.Single(decoder.Decode([
            Frame("c2s", 2, 33, new MsRequest { Rid = 7, Service = "FriendService", Method = "GetList", Params = ByteString.CopyFrom(args) }),
        ]));
        Assert.Contains("\"ch\":\"micro\",\"id\":0,\"name\":\"FriendService.GetList\"", line);
        Assert.Contains("Token=<redacted>", line);
        Assert.DoesNotContain("secret", line);
    }

    [Fact]
    public void BrokenLinesAndHandshakeFramesNeverStopTheDecode()
    {
        var decoder = Create();
        var lines = decoder.Decode(["not json", "{\"dir\":\"c2s\",\"ch\":3,\"idx\":1,\"b\":\"%%\"}", Frame("c2s", 3, 1, new Phonest.Proto.Void())]).ToList();
        Assert.Contains("\"ch\":\"unknown\",\"id\":0,\"name\":\"rpc 1\"", Assert.Single(lines));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
