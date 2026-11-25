using System.Reflection;
#if !NETSTANDARD2_0
using System.Runtime.Loader;
#endif
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityRuntimeDebugger.Hosting;

namespace UnityRuntimeDebugger.Scripting;

/// <summary>
/// Program mode: a complete compilation unit (usings, namespaces, types) whose
/// <c>public static object Execute()</c> (or <c>void Execute()</c>) runs once on the main thread.
/// Each program loads into its own collectible context and is unloaded afterwards, so state that
/// must outlive it (Harmony patches, event handlers) belongs in the console session or a hook.
///
/// On netstandard2.0 (the BepInEx 6 Mono build) there is no AssemblyLoadContext: programs load into
/// the default context and simply are not unloadable — they still compile and run.
/// </summary>
internal sealed class ProgramRunner
{
    private readonly MainThread _main;
    private int _count;

    public ProgramRunner(MainThread main) => _main = main;

    public Outcome Run(string code)
    {
        var compilation = CSharpCompilation.Create(
            $"urdbg-program-{Interlocked.Increment(ref _count)}",
            new[] { References.GlobalUsings(), CSharpSyntaxTree.ParseText(code) },
            References.All(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, optimizationLevel: OptimizationLevel.Debug));

        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        if (!emit.Success)
            return Outcome.NotCompiled(emit.Diagnostics);

        image.Position = 0;
#if NETSTANDARD2_0
        return Execute(Assembly.Load(image.ToArray()), emit, null);
#else
        var context = CreateContext();
        try
        {
            return Execute(context?.LoadFromStream(image) ?? Assembly.Load(image.ToArray()), emit, context);
        }
        finally
        {
            // Completes only once nothing references the program anymore.
            context?.Unload();
        }
#endif
    }

    private Outcome Execute(Assembly assembly, Microsoft.CodeAnalysis.Emit.EmitResult emit, object? context)
    {
        var entry = FindEntry(assembly);
        var output = new StringWriter();
        try
        {
            var value = _main.Run(() => ConsoleCapture.Run(output, () => entry.Invoke(null, null)), ScriptSession.Timeout);
            return new Outcome(value, entry.ReturnType == typeof(void), output.ToString(), emit.Diagnostics, null);
        }
        catch (TargetInvocationException ex)
        {
            return new Outcome(null, true, output.ToString(), emit.Diagnostics, ex.InnerException ?? ex);
        }
    }

    private static MethodInfo FindEntry(Assembly assembly)
    {
        var entries = assembly.GetTypes()
            .Select(t => t.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null))
            .Where(m => m != null)
            .ToList();

        return entries.Count switch
        {
            1 => entries[0]!,
            0 => throw new MissingMethodException("program mode needs an entry point: public static object Execute() in a public class"),
            _ => throw new AmbiguousMatchException("more than one public static Execute() in the program"),
        };
    }

#if !NETSTANDARD2_0
    /// <summary>Collectible contexts are not available on every runtime; fall back to the default one.</summary>
    private static AssemblyLoadContext? CreateContext()
    {
        try
        {
            return new AssemblyLoadContext(null, isCollectible: true);
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
#endif
}
