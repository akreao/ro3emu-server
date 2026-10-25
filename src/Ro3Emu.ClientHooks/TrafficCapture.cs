using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Records every Phonest RPC frame the client sends and receives, after decryption and inflation, as JSON lines in
    /// &lt;client&gt;\Logs\capture-yyyyMMdd-HHmmss.jsonl. Installed by the capture patch (Ro3Emu.ClientTools
    /// CapturePatch), which calls it from Phonest.Rpc:
    /// <list type="bullet">
    /// <item><see cref="Received"/> at the start of ProtobufChannel.OnParse(byte[] data, int size): data[0..2] is the
    /// rpc index (uint16 LE), the rest the protobuf body.</item>
    /// <item><see cref="Sent"/> at the start of RpcChannel.SendChannelData(byte[] data, int start, int count, bool
    /// pooled): int32 LE length, uint16 LE rpc index, body, before the RC4/zlib Convertor.</item>
    /// </list>
    /// A line: {"t":"2026-10-25T15:00:00.123Z","dir":"s2c"|"c2s","ch":1,"idx":2,"b":"&lt;base64 body&gt;"}. The first
    /// frame of a channel is preceded by {"dir":"channel","ch":1,"type":"&lt;channel and handler types&gt;"}.
    /// The network threads only copy the bytes into a queue; a background thread formats and writes them. Nothing here
    /// may throw into the client's network code.
    /// </summary>
    public static class TrafficCapture
    {
        private sealed class ChannelId
        {
            public int Id;
        }

        private readonly struct Frame
        {
            public Frame(DateTime at, string direction, int channel, int index, byte[] body, string note)
            {
                At = at;
                Direction = direction;
                Channel = channel;
                Index = index;
                Body = body;
                Note = note;
            }

            public DateTime At { get; }
            public string Direction { get; }
            public int Channel { get; }
            public int Index { get; }
            public byte[] Body { get; }
            public string Note { get; }
        }

        private static readonly ConditionalWeakTable<object, ChannelId> Channels = new ConditionalWeakTable<object, ChannelId>();
        private static readonly ConcurrentQueue<Frame> Queue = new ConcurrentQueue<Frame>();
        private static readonly AutoResetEvent Pending = new AutoResetEvent(false);
        private static readonly object StartLock = new object();
        private static int _nextChannel;
        private static Thread _writer;

        public static void Received(object channel, byte[] data, int size)
        {
            try
            {
                if (data == null || size < 2) return;
                Enqueue(channel, "s2c", data, 0, size);
            }
            catch (Exception)
            {
                // Logging must never break the client's receive path.
            }
        }

        public static void Sent(object channel, byte[] data, int start, int count)
        {
            try
            {
                if (data == null || count < 6) return;
                Enqueue(channel, "c2s", data, start + 4, count - 4);
            }
            catch (Exception)
            {
                // Logging must never break the client's send path.
            }
        }

        /// <summary>data[offset..offset+length] is uint16 LE rpc index + body; copied now (the buffer is pooled).</summary>
        private static void Enqueue(object channel, string direction, byte[] data, int offset, int length)
        {
            int index = data[offset] | data[offset + 1] << 8;
            var body = new byte[length - 2];
            Buffer.BlockCopy(data, offset + 2, body, 0, body.Length);
            var id = ChannelOf(channel);
            Queue.Enqueue(new Frame(DateTime.UtcNow, direction, id.Id, index, body, null));
            EnsureWriter();
            Pending.Set();
        }

        /// <summary>The channel's number; a new channel is announced with its types first.</summary>
        private static ChannelId ChannelOf(object channel)
        {
            if (Channels.TryGetValue(channel, out var id)) return id;
            lock (StartLock)
            {
                if (Channels.TryGetValue(channel, out id)) return id;
                id = new ChannelId { Id = ++_nextChannel };
                Queue.Enqueue(new Frame(DateTime.UtcNow, "channel", id.Id, 0, null, Describe(channel)));
                Channels.Add(channel, id);
                return id;
            }
        }

        /// <summary>The channel's type and its handler's (Phonest.Rpc.Channel._handler): tells the gate from the micro gate.</summary>
        private static string Describe(object channel)
        {
            string type = channel?.GetType().FullName ?? "?";
            for (var t = channel?.GetType(); t != null; t = t.BaseType)
            {
                var field = t.GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly);
                if (field == null) continue;
                var handler = field.GetValue(channel);
                if (handler != null) type += " " + handler.GetType().FullName;
                break;
            }
            return type;
        }

        private static void EnsureWriter()
        {
            if (_writer != null) return;
            lock (StartLock)
            {
                if (_writer != null) return;
                var thread = new Thread(WriteLoop) { IsBackground = true, Name = "Ro3Emu.TrafficCapture" };
                thread.Start();
                _writer = thread;
            }
        }

        private static void WriteLoop()
        {
            StreamWriter writer;
            try
            {
                var dir = LogsDir();
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");
                writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false), 1 << 16);
                UnityEngine.Debug.Log("[Ro3Emu.Capture] writing " + path);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Ro3Emu.Capture] cannot open the capture file: " + e.Message);
                return;
            }
            var line = new StringBuilder(256);
            while (true)
            {
                Pending.WaitOne(1000);
                try
                {
                    bool wrote = false;
                    while (Queue.TryDequeue(out var frame))
                    {
                        writer.WriteLine(Format(frame, line));
                        wrote = true;
                    }
                    if (wrote) writer.Flush(); // once per drained batch: a crash loses at most the last second
                }
                catch (Exception)
                {
                    // A full disk or a closed file: drop and keep the client running.
                }
            }
        }

        private static string Format(in Frame f, StringBuilder line)
        {
            line.Clear();
            line.Append("{\"t\":\"").Append(f.At.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append("\",\"dir\":\"").Append(f.Direction)
                .Append("\",\"ch\":").Append(f.Channel);
            if (f.Note != null)
                line.Append(",\"type\":\"").Append(f.Note.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            else
                line.Append(",\"idx\":").Append(f.Index).Append(",\"b\":\"").Append(Convert.ToBase64String(f.Body)).Append('"');
            return line.Append('}').ToString();
        }

        /// <summary>ro3_Data\Managed\..\..\Logs: the folder Player.log is written to.</summary>
        private static string LogsDir()
        {
            var managed = Path.GetDirectoryName(typeof(TrafficCapture).Assembly.Location);
            return string.IsNullOrEmpty(managed)
                ? Path.Combine(Environment.CurrentDirectory, "Logs")
                : Path.GetFullPath(Path.Combine(managed, "..", "..", "Logs"));
        }
    }
}
