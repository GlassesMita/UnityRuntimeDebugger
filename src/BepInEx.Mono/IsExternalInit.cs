// Shim for netstandard2.0: the C# compiler emits a call to IsExternalInit for `init` accessors and
// positional records, and that type only exists from .NET 5 on. BepInEx 6 Mono loads plugins into the
// game's .NET Framework 4.x Mono runtime, so the plugin must target netstandard2.0 and carry the type
// itself. Declaring it in the plugin assembly is the standard workaround; it is inert at runtime.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
