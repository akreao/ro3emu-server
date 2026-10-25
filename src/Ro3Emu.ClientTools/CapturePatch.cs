using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Capture-only patch: the client keeps talking to whatever server it was built for, and Ro3Emu.ClientHooks
/// TrafficCapture records every RPC frame it sends and receives (decrypted) in &lt;client&gt;\Logs\capture-*.jsonl.
/// Only Phonest.Rpc.dll changes, with one call at the start of each of two methods:
/// <list type="bullet">
/// <item>ProtobufChannel.OnParse(byte[] data, int size) -> TrafficCapture.Received(this, data, size);</item>
/// <item>RpcChannel.SendChannelData(byte[] data, int start, int count, bool pooled) -> TrafficCapture.Sent(this, data,
/// start, count).</item>
/// </list>
/// The original is kept in ro3emu-backup\capture (apart from <see cref="ClientPatcher"/>'s backups, which this patch
/// never touches); <see cref="Remove"/> puts it back. A Phonest.Rpc.dll that is neither ours nor the backup is a game
/// update and becomes the new backup.
/// </summary>
public static class CapturePatch
{
    private const string RpcFile = "Phonest.Rpc.dll";
    private const string HooksFile = "Ro3Emu.ClientHooks.dll";
    private const string HookType = "Ro3Emu.ClientHooks.TrafficCapture";

    public static void Install(string clientDir, string? hooksDll = null)
    {
        var managed = ClientPatcher.ManagedDir(clientDir);
        hooksDll ??= ClientPatcher.DefaultHooksDll();
        if (!File.Exists(hooksDll)) throw new FileNotFoundException("build src/Ro3Emu.ClientHooks first", hooksDll);
        var original = BackUp(clientDir);
        var target = Path.Combine(managed, RpcFile);
        var staged = target + ClientPatcher.StagedSuffix;
        try
        {
            Patch(original, staged, managed, hooksDll);
            File.Copy(hooksDll, Path.Combine(managed, HooksFile), overwrite: true);
            File.Move(staged, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
        Console.WriteLine($"capture hook installed: traffic goes to {Path.Combine(clientDir, "Logs")}{Path.DirectorySeparatorChar}capture-*.jsonl");
    }

    /// <summary>Puts the original Phonest.Rpc.dll back; true when there was one to restore.</summary>
    public static bool Remove(string clientDir)
    {
        var saved = Path.Combine(BackupDir(clientDir), RpcFile);
        if (!File.Exists(saved)) return false;
        ClientPatcher.CopyAtomically(saved, Path.Combine(ClientPatcher.ManagedDir(clientDir), RpcFile));
        File.Delete(saved);
        Console.WriteLine($"restored {RpcFile} (capture hook removed)");
        return true;
    }

    public static bool Installed(string clientDir) => IsOurs(Path.Combine(ClientPatcher.ManagedDir(clientDir), RpcFile));

    /// <summary>The pristine Phonest.Rpc.dll, backed up first if needed.</summary>
    private static string BackUp(string clientDir)
    {
        var current = Path.Combine(ClientPatcher.ManagedDir(clientDir), RpcFile);
        var saved = Path.Combine(BackupDir(clientDir), RpcFile);
        bool ours = IsOurs(current);
        if (ours && !File.Exists(saved))
            throw new InvalidOperationException($"{RpcFile} carries the capture hook but there is no original in {BackupDir(clientDir)}; "
                + "repair the client with the publisher's launcher, then install the capture again");
        if (!ours)
        {
            Directory.CreateDirectory(BackupDir(clientDir));
            ClientPatcher.CopyAtomically(current, saved);
        }
        return saved;
    }

    private static void Patch(string source, string destination, string managed, string hooksDll)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var assembly = AssemblyDefinition.ReadAssembly(source, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
        using var hooks = AssemblyDefinition.ReadAssembly(hooksDll, new ReaderParameters { InMemory = true });
        var capture = hooks.MainModule.GetType(HookType) ?? throw new InvalidOperationException($"{HookType} not found in the hooks");
        var module = assembly.MainModule;

        var onParse = Method(module, "Phonest.Rpc.ProtobufChannel", "OnParse", 2);
        CallAtEntry(onParse, module.ImportReference(capture.Methods.Single(m => m.Name == "Received")), 2);
        var send = Method(module, "Phonest.Rpc.RpcChannel", "SendChannelData", 4);
        CallAtEntry(send, module.ImportReference(capture.Methods.Single(m => m.Name == "Sent")), 3);

        ClientPatcher.Mark(module);
        assembly.Write(destination);
    }

    private static MethodDefinition Method(ModuleDefinition module, string type, string name, int parameters) =>
        module.GetType(type)?.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Count == parameters && !m.IsStatic)
        ?? throw new InvalidOperationException($"{type}.{name} not found (a client build this patch does not know)");

    /// <summary>hook(this, arg1..argN) as the method's first instructions.</summary>
    private static void CallAtEntry(MethodDefinition method, MethodReference hook, int arguments)
    {
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        for (int i = 0; i < arguments; i++) il.InsertBefore(first, il.Create(OpCodes.Ldarg, method.Parameters[i]));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
    }

    private static bool IsOurs(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
        return ClientPatcher.IsMarked(assembly.MainModule);
    }

    private static string BackupDir(string clientDir) => Path.Combine(ClientPatcher.BackupDir(clientDir), "capture");
}
