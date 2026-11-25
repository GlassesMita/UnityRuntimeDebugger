using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace UnityRuntimeDebugger.Mono;

/// <summary>
/// Version-agnostic assembly resolution.
///
/// Costura embeds the debugger's dependencies as <c>costura.&lt;name&gt;.dll.compressed</c> resources and
/// normally resolves them itself, but Unity's Mono does not unify assembly versions the way .NET does:
/// System.Text.Json asks for <c>System.Buffers 4.0.2.0</c> by exact version, the embedded/loaded copy is
/// 4.0.3.0, and the load fails with FileNotFoundException even though the assembly is right there.
///
/// This handler resolves by *simple name* only, which is what the BepInEx/Mono plugin ecosystem expects
/// (every plugin ships its own copy and they rarely agree on versions). It never overrides something
/// already loaded, so it cannot change the behaviour of a game or another plugin.
/// </summary>
internal static class AssemblyRedirect
{
    private const string ResourcePrefix = "costura.";
    private const string ResourceSuffix = ".dll.compressed";

    private static bool _installed;

    internal static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        AssemblyName requested;
        try
        {
            requested = new AssemblyName(args.Name);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(requested.Name))
            return null;

        // The name may already be loaded under a different version — prefer that over a second copy,
        // otherwise Mono ends up with two System.Buffers and type identity breaks.
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(loaded.GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase))
                return loaded;
        }

        return LoadEmbedded(requested.Name);
    }

    private static Assembly? LoadEmbedded(string simpleName)
    {
        try
        {
            var host = typeof(AssemblyRedirect).Assembly;
            using var resource = host.GetManifestResourceStream(ResourcePrefix + simpleName.ToLowerInvariant() + ResourceSuffix);
            if (resource == null)
                return null;

            using var deflate = new DeflateStream(resource, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            deflate.CopyTo(buffer);
            return Assembly.Load(buffer.ToArray());
        }
        catch
        {
            // Fall through to the next resolver (Costura's own, or default probing).
            return null;
        }
    }
}
