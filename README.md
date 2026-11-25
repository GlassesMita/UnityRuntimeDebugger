# Unity Runtime Debugger

Unity 游戏运行时调试器：场景/对象检查器、C# 控制台、Harmony 钩子，以及通过本地 HTTP API 提供的 Web UI。

本仓库是 **BepInEx 6 Mono** 版本。

## 该用哪个版本？

| 你的游戏 | 使用 |
| --- | --- |
| Mono（存在 `Managed/Assembly-CSharp.dll`、`mscorlib.dll`） | **本仓库** |
| IL2CPP（存在 `GameAssembly.dll`） | [MetaMikuAI/UnityRuntimeDebugger](https://github.com/MetaMikuAI/UnityRuntimeDebugger) |

两个加载器**不通用**。BepInEx 6 IL2CPP 把插件跑在 Il2CppInterop 的 .NET 6 运行时里；BepInEx 6 Mono 把插件跑在游戏自己的 Unity Mono 运行时里，那是 .NET Framework 4.x / .NET Standard 2.0 的配置。为其中一个编译的插件在另一个里加载不起来 —— 原因见 [Mono 版为什么不一样](#mono-版为什么不一样)。

## 安装

1. 给游戏装好 BepInEx 6（Mono）。
2. 把 `src/BepInEx.Mono/bin/Release/netstandard2.0/` 下的 **`UnityRuntimeDebugger.Mono.dll` 和它旁边的所有 DLL** 一起复制到 `BepInEx/plugins/`。

   那些依赖 DLL 属于安装的一部分：`System.Text.Json` 及其依赖链，Unity 和 BepInEx 都不自带。
3. 启动游戏。日志会打印 token 和控制台地址：

```
[Warning:Unity Runtime Debugger] UnityRuntimeDebugger token: <token>
[Warning:Unity Runtime Debugger] UnityRuntimeDebugger web console: http://127.0.0.1:18765/#token=<token>
```

## 配置

`BepInEx/config/UnityRuntimeDebugger.Mono.cfg`：

| 配置项 | 默认值 | 含义 |
| --- | --- | --- |
| `[Server] ListenAddress` | `127.0.0.1` | 改成 `0.0.0.0` 可允许局域网访问。 |
| `[Server] Port` | `18765` | HTTP API 与 Web UI 端口。 |
| `[Security] Enabled` | `true` | 该调试器会执行任意 C#。 |
| `[Security] AcknowledgeRisk` | `true` | 必须与 `Enabled` 同时为 true。 |
| `[Security] Token` | 空 | 留空则每次启动随机生成。 |

> 调试器会在游戏进程内执行任意代码。除非你清楚暴露后果，否则请让 `ListenAddress` 保持在回环地址。

## 构建

```bash
dotnet build src/BepInEx.Mono
```

在 `local.props` 里设置 `BepInExDir`，可顺便把插件复制进游戏的 `BepInEx/plugins`：

```xml
<Project>
  <PropertyGroup>
    <BepInExDir>C:\Games\MyGame\BepInEx</BepInExDir>
  </PropertyGroup>
</Project>
```

Web UI 由 `web/` 构建（Vite）；首次构建时 `src/Core` 会自动执行 `npm ci && npm run build`。传 `-p:SkipWeb=true` 可改为直接嵌入已有的 `web/dist`。

## Mono 版为什么不一样

以下每一条都是"跑在 Unity Mono 里、而不是 .NET 6 里"的直接后果。

- **目标框架是 `netstandard2.0`。** `net6.0` 的插件会被链式加载器列出，然后在 `RunModuleConstructor` 阶段失败：`Could not load file or assembly 'System.Runtime, Version=6.0.0.0'`。
- **基类是 `BaseUnityPlugin`，不是 `BasePlugin`。** Mono 的插件基类本身是个 `MonoBehaviour`，所以启动走 `Awake`、收尾走 `OnDestroy`，没有 `Load`/`Unload` 可重写。
- **没有互操作层。** 组件本来就是它最派生的托管类型，所以 `IGame` 直接用 `GetComponents<Component>()` 和 `GetType().Name`；`IRuntime` 沿用 Core 现成的 `ManagedRuntime`。
- **没有类型注入，也没有原生 detour。** 主线程泵只是一个普通的 `AddComponent`，因此 IL2CPP 版需要的 `ClassInjector` / Dobby 那套在这里完全不存在。
- **HTTP 是同步的。** Unity 的 Mono 不会在"插件加载线程"上调度第一个 `await` 之后的续体（此时播放循环还没起来、线程池空转），于是请求解析完之后会**永久挂住**。Mono 版改为阻塞式 accept 循环，并在同一线程内联完成 handler。
- **依赖以独立 DLL 形式随插件分发。** Unity 的 Mono **不做程序集版本统一**：`System.Text.Json` 按精确版本索要 `System.Buffers 4.0.2.0`，版本对不上就直接拒绝加载，所以嵌进资源里的做法会失败。遗留情况由 `AssemblyRedirect` 按简单名兜底解析。
- **少量 BCL 兼容垫片。** `src/Core/NetStandardShims.cs` 提供 `Math.Clamp`、`MathF.Round`、`Dictionary.TryAdd`/`GetValueOrDefault`/`Deconstruct` 与 `Environment.TickCount64`，这些 netstandard2.0 都没有声明。

## 许可证

AGPL-3.0-only，继承自上游项目。见 [LICENSE](LICENSE)。
