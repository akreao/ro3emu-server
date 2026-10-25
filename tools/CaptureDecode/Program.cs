using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Game;

// Decodes a client capture (Ro3Emu.ClientTools capture) into <capture>.decoded.jsonl, the server's traffic log format,
// and <capture>.summary.tsv, the message counts by direction and name.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CaptureDecode <capture-*.jsonl> <folder with msgtable.tsv>");
    return 1;
}
var catalog = new MsgCatalog(Options.Create(new ServerOptions { DataDir = args[1] }), NullLogger<MsgCatalog>.Instance);
var decoder = new CaptureDecoder(catalog);
string stem = Path.ChangeExtension(args[0], null);
File.WriteAllLines(stem + ".decoded.jsonl", decoder.Decode(File.ReadLines(args[0])));
File.WriteAllLines(stem + ".summary.tsv", decoder.Counts.Select(c => $"{c.Value}\t{c.Key}").Prepend("count\tdir\tchannel\tid\tname"));
Console.WriteLine($"{stem}.decoded.jsonl, {stem}.summary.tsv: {decoder.Counts.Values.Sum()} messages, {decoder.Counts.Count} kinds");
return 0;
