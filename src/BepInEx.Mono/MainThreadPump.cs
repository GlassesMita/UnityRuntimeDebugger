using UnityEngine;
using UnityRuntimeDebugger.Hosting;
using Object = UnityEngine.Object;

namespace UnityRuntimeDebugger.Mono;

/// <summary>
/// Pumps the main-thread queue from a plain MonoBehaviour's Update.
///
/// The IL2CPP host needed <c>ClassInjector.RegisterTypeInIl2Cpp</c> before a component of a plugin
/// type could exist, plus a Dobby detour on <c>EventSystem.Update</c> for loaders where the injected
/// component never got Update. Under Mono the type is just a normal managed type that the engine can
/// instantiate, so neither is needed.
/// </summary>
public sealed class MainThreadPump : MonoBehaviour
{
    private static MainThread? _queue;

    internal static void Install(MainThread queue)
    {
        _queue = queue;
        var host = new GameObject("UnityRuntimeDebugger");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<MainThreadPump>();
    }

    private void Update()
    {
        _queue?.Pump();
    }
}
