using UnityEngine;
using UnityEngine.SceneManagement;
using UnityRuntimeDebugger.Hosting;
using Object = UnityEngine.Object;

namespace UnityRuntimeDebugger.Mono;

/// <summary>
/// Scenes, GameObjects, components and textures through plain managed UnityEngine types. Main thread only.
///
/// Ported from the IL2CPP host: every <c>Il2CppType</c>/<c>TryCast</c>/<c>Il2CppStructArray</c> call is
/// replaced by its ordinary managed counterpart — under Mono a component already <em>is</em> its most
/// derived type, so the runtime-class lookup that IL2CPP needs to name a component has no equivalent
/// (and no need): <c>GetType().Name</c> is the real class.
/// </summary>
internal sealed class UnityGame : IGame
{
    private const int MaxComponentNames = 4;
    private const int SearchVisitLimit = 60000;

    public GameStatus Status()
    {
        var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name).ToList();
        return new GameStatus(
            Application.productName,
            Application.version,
            Application.unityVersion,
            Application.platform.ToString(),
            Time.smoothDeltaTime > 0 ? 1f / Time.smoothDeltaTime : 0f,
            Time.frameCount,
            Time.realtimeSinceStartup,
            Time.timeScale,
            Application.isFocused,
            Application.runInBackground,
            SceneManager.GetActiveScene().name,
            scenes);
    }

    public IReadOnlyList<SceneFacts> Scenes()
    {
        var result = new List<SceneFacts>();
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            var roots = scene.isLoaded ? scene.GetRootGameObjects().Select(Node).ToList() : new List<NodeFacts>();
            result.Add(new SceneFacts(scene.name, scene.buildIndex, scene.isLoaded, false, roots));
        }

        result.Add(new SceneFacts("DontDestroyOnLoad", -1, true, true, PersistentRoots().Select(Node).ToList()));
        return result;
    }

    public IReadOnlyList<NodeFacts> Children(object gameObject)
    {
        var transform = AsGameObject(gameObject)?.transform ?? throw new ArgumentException("not a GameObject");
        return Enumerable.Range(0, transform.childCount).Select(i => Node(transform.GetChild(i).gameObject)).ToList();
    }

    public IReadOnlyList<ComponentFacts> Components(object gameObject)
    {
        var go = AsGameObject(gameObject) ?? throw new ArgumentException("not a GameObject");
        return go.GetComponents<Component>()
            .Select(c => c == null
                ? new ComponentFacts(null, null)
                : new ComponentFacts(c, (c as Behaviour)?.enabled ?? (c as Renderer)?.enabled))
            .ToList();
    }

    public GameObjectFacts? GameObjectOf(object value)
    {
        var go = AsGameObject(value);
        if (go == null)
            return null;

        var ancestors = new List<object>();
        var names = new List<string>();
        for (var t = go.transform.parent; t != null; t = t.parent)
        {
            ancestors.Insert(0, t.gameObject);
            names.Insert(0, t.gameObject.name);
        }

        names.Add(go.name);
        var layer = LayerMask.LayerToName(go.layer);
        string? tag = null;
        try
        {
            tag = go.tag;
        }
        catch
        {
            // undefined tags throw
        }

        return new GameObjectFacts(go, go.name, string.Join("/", names), go.activeSelf, go.activeInHierarchy,
            string.IsNullOrEmpty(layer) ? go.layer.ToString() : $"{layer} ({go.layer})", tag, go.scene.name, ancestors);
    }

    public IReadOnlyList<NodeFacts> Search(string query, int limit)
    {
        var result = new List<NodeFacts>();
        var stack = new Stack<Transform>();
        for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded)
                foreach (var root in Enumerable.Reverse(scene.GetRootGameObjects()))
                    stack.Push(root.transform);
        }

        foreach (var root in PersistentRoots())
            stack.Push(root.transform);
        var visited = 0;
        while (stack.Count > 0 && result.Count < limit && visited++ < SearchVisitLimit)
        {
            var transform = stack.Pop();
            var go = transform.gameObject;
            if (go.name.Contains(query, StringComparison.OrdinalIgnoreCase))
                result.Add(Node(go) with { Path = ParentPath(transform) });
            for (var i = transform.childCount - 1; i >= 0; i--)
                stack.Push(transform.GetChild(i));
        }

        return result;
    }

    public bool IsUnityObjectType(Type type) => typeof(Object).IsAssignableFrom(type);

    public IReadOnlyList<object> FindObjects(Type type, int limit, out int total)
    {
        var found = Resources.FindObjectsOfTypeAll(type);
        total = found.Length;
        return found.Take(limit).Cast<object>().ToList();
    }

    public bool IsDestroyed(object value) => value is Object o && o == null;

    public string? NameOf(object value) => value is Object o ? o.name : null;

    public bool CanRender(object value) => value is Sprite or Texture;

    /// <summary>
    /// Blits a Sprite (its atlas rect) or any Texture into a temporary RenderTexture and reads it
    /// back, so textures that are not CPU-readable work too.
    /// </summary>
    public byte[] RenderPng(object value, int maxSize)
    {
        Texture source;
        Rect? crop = null;
        switch (value)
        {
            case Sprite sprite:
                source = sprite.texture;
                crop = sprite.textureRect;
                break;
            case Texture texture:
                source = texture;
                break;
            default:
                throw new ArgumentException("only Sprite and Texture render to PNG");
        }

        if (source == null)
            throw new InvalidOperationException("no texture");

        float srcW = source.width, srcH = source.height;
        var width = crop?.width ?? srcW;
        var height = crop?.height ?? srcH;
        var scale = Math.Min(1f, maxSize / Math.Max(width, height));
        var outW = Math.Max(1, (int)Math.Round(width * scale));
        var outH = Math.Max(1, (int)Math.Round(height * scale));

        var target = RenderTexture.GetTemporary(outW, outH, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        Texture2D? readback = null;
        try
        {
            RenderTexture.active = target;
            GL.Clear(true, true, Color.clear);
            if (crop is { } r)
            {
                // The plugin builds against BepInEx's UnityEngine 5.6 reference package, which has no
                // Graphics.Blit(Texture, RenderTexture, Vector2 scale, Vector2 offset) overload.
                // Draw a sprite-atlas sub-rect scaled into the target with DrawTexture instead; the
                // Texture overload takes a source Rect directly, which is exactly the crop we want.
                var pixels = new Rect(r.x, r.y, r.width, r.height);
                Graphics.DrawTexture(new Rect(0, 0, outW, outH), source, pixels, 0, 0, 0, 0);
            }
            else
            {
                Graphics.Blit(source, target);
            }

            RenderTexture.active = target;
            readback = new Texture2D(outW, outH, TextureFormat.RGBA32, false);
            readback.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
            readback.Apply();
            return EncodeToPng.Value(readback);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (readback != null)
                Object.Destroy(readback);
        }
    }

    // ImageConversion lives in its own module, outside the reference assemblies the plugin builds
    // against; the runtime carries it, so look it up by name and hand back a real managed byte[].
    private static readonly Lazy<Func<Texture2D, byte[]>> EncodeToPng = new(() =>
    {
        var method = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", throwOnError: true)!
            .GetMethod("EncodeToPNG", new[] { typeof(Texture2D) }) ?? throw new MissingMethodException("ImageConversion.EncodeToPNG");
        return texture => (byte[])method.Invoke(null, new object[] { texture })!;
    });

    private static NodeFacts Node(GameObject go)
    {
        var names = new List<string>();
        foreach (var component in go.GetComponents<Component>())
        {
            var name = component == null ? "Missing" : ShortName(component);
            if (name is null or "Transform")
                continue;
            names.Add(name);
            if (names.Count >= MaxComponentNames)
                break;
        }

        return new NodeFacts(go, go.name, go.activeSelf, go.activeInHierarchy, go.transform.childCount, names);
    }

    private static string ParentPath(Transform transform)
    {
        var names = new List<string>();
        for (var t = transform.parent; t != null; t = t.parent)
            names.Insert(0, t.gameObject.name);
        return string.Join("/", names);
    }

    /// <summary>
    /// The concrete type name. IL2CPP needed a native class lookup here because every component came
    /// back as a plain <c>Component</c> wrapper; under Mono the managed type already is the real one.
    /// </summary>
    private static string? ShortName(Component component)
    {
        try
        {
            return component.GetType().Name;
        }
        catch
        {
            return null;
        }
    }

    private static GameObject? AsGameObject(object value) => value switch
    {
        GameObject go => go,
        Component c => c.gameObject,
        _ => null,
    };

    /// <summary>
    /// The DontDestroyOnLoad scene cannot be looked up directly; a throwaway probe moved into it
    /// exposes the scene.
    /// </summary>
    private static List<GameObject> PersistentRoots()
    {
        var probe = new GameObject("UnityRuntimeDebugger probe");
        try
        {
            Object.DontDestroyOnLoad(probe);
            return probe.scene.GetRootGameObjects().Where(go => go.GetInstanceID() != probe.GetInstanceID()).ToList();
        }
        finally
        {
            Object.DestroyImmediate(probe);
        }
    }
}
