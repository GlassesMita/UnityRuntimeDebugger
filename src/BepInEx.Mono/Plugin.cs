using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using BepInEx.Unity.Mono.Bootstrap;
using BepInEx.Logging;
using UnityEngine;
using UnityRuntimeDebugger.Hosting;
using PluginInfo = UnityRuntimeDebugger.Hosting.PluginInfo;

namespace UnityRuntimeDebugger.Mono;

/// <summary>
/// BepInEx 6 Mono entry point.
///
/// Differences from the IL2CPP host this was forked from, all forced by the loader:
///   * Mono's plugin base is <see cref="BaseUnityPlugin"/> (a <see cref="MonoBehaviour"/>), not
///     <c>BepInEx.Unity.IL2CPP.BasePlugin</c> — there is no <c>Load()</c>/<c>Unload()</c> to override,
///     so startup happens in <see cref="Awake"/> and shutdown in <see cref="OnDestroy"/>.
///   * No type injection: a plain <c>AddComponent</c> of a managed type is enough for the pump, so
///     the ClassInjector/Dobby detour the IL2CPP host needed for main-thread work is unnecessary.
///   * No <c>Il2CppChainloader</c>; the plugin list comes from BepInEx's own chainloader.
///   * <see cref="IRuntime"/> stays on Core's <c>ManagedRuntime</c>, which is already the Mono path.
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public sealed class Plugin : BaseUnityPlugin
{
    private Debugger? _debugger;
    private LogListener? _logs;

    private void Awake()
    {
        // Must run before anything touches System.Text.Json (or any embedded dependency): Unity's Mono
        // does not unify assembly versions, so this supplies the by-name fallback.
        AssemblyRedirect.Install();

        var address = Config.Bind("Server", "ListenAddress", "127.0.0.1", "0.0.0.0 for LAN access.");
        var port = Config.Bind("Server", "Port", 18765, new ConfigDescription("", new AcceptableValueRange<int>(1, 65535)));
        var enabled = Config.Bind("Security", "Enabled", true, "Runs arbitrary C# for anyone with the token.");
        var acknowledged = Config.Bind("Security", "AcknowledgeRisk", true, "Required together with Enabled.");
        var fixedToken = Config.Bind("Security", "Token", "", "Empty: random on every start.");

        if (!enabled.Value || !acknowledged.Value)
        {
            Logger.LogWarning("UnityRuntimeDebugger is disabled ([Security] Enabled, AcknowledgeRisk).");
            return;
        }

        var token = string.IsNullOrWhiteSpace(fixedToken.Value) ? Guid.NewGuid().ToString("N") : fixedToken.Value.Trim();
        try
        {
            _debugger = Debugger.Start(new DebuggerOptions(token, new UnityGame(), new LoaderInfo(
                    "BepInEx 6 Mono",
                    Paths.BepInExVersion.ToString(),
                    Plugins))
                {
                    ListenAddress = address.Value,
                    Port = port.Value,
                    // Runtime stays at its default (ManagedRuntime): plain managed objects need no
                    // unboxing, pointer identity or native method resolution.
                    LogFile = Path.Combine(Paths.BepInExRootPath, "LogOutput.log"),
                    OnError = message => Logger.LogError(message),
                });
            _debugger.MainThread.Error += ex => Logger.LogError(ex);

            _logs = new LogListener(_debugger.Logs);
            BepInEx.Logging.Logger.Listeners.Add(_logs);

            MainThreadPump.Install(_debugger.MainThread);
        }
        catch (Exception ex)
        {
            Logger.LogError($"UnityRuntimeDebugger failed to start: {ex}");
            return;
        }

        Logger.LogWarning($"UnityRuntimeDebugger token: {token}");
        foreach (var url in _debugger.Urls())
            Logger.LogWarning($"UnityRuntimeDebugger web console: {url}/#token={token}");
    }

    private void OnDestroy()
    {
        if (_logs != null)
            BepInEx.Logging.Logger.Listeners.Remove(_logs);
        _debugger?.Dispose();
        _debugger = null;
    }

    private static IReadOnlyList<PluginInfo> Plugins()
        => UnityChainloader.Instance.Plugins.Values
            .Select(p => new PluginInfo(p.Metadata.GUID, p.Metadata.Name, p.Metadata.Version.ToString()))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
