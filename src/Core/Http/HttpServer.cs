using System.Net;
using System.Net.Sockets;

namespace UnityRuntimeDebugger.Http;

/// <summary>
/// Minimal HTTP/1.1 server on a raw socket (one request per connection). HttpListener needs
/// http.sys on Windows and is not available on every mobile runtime; this works everywhere.
/// </summary>
internal sealed class HttpServer : IDisposable
{
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpListener _listener;
    private readonly Func<HttpRequest, CancellationToken, Task<HttpResponse>> _handler;
    private readonly Action<string>? _onError;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Optional fully synchronous request handler. The Mono host supplies one so that a request never
    /// depends on an async continuation being scheduled by Unity's ThreadPool.
    /// </summary>
    internal Func<HttpRequest, HttpResponse>? SyncHandler { get; set; }

    public HttpServer(IPAddress address, int port, Func<HttpRequest, CancellationToken, Task<HttpResponse>> handler, Action<string>? onError)
    {
        _listener = new TcpListener(address, port);
        _handler = handler;
        _onError = onError;
    }

    /// <summary>The bound endpoint; with port 0 the system picks one.</summary>
    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public void Start()
    {
        _listener.Start();
#if NETSTANDARD2_0
        // Unity's Mono runtime schedules ThreadPool work only after the main thread reaches the
        // player loop, and a plugin's Awake runs before that: a Task.Run accept loop can sit queued
        // indefinitely and the port accepts TCP but never answers. A dedicated background thread
        // starts immediately and is not tied to the engine's scheduler.
        var thread = new Thread(AcceptLoopSync)
        {
            IsBackground = true,
            Name = "UnityRuntimeDebugger http",
        };
        thread.Start();
#else
        _ = Task.Run(AcceptLoop);
#endif
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
#if NETSTANDARD2_0
                // netstandard2.0 has no AcceptTcpClientAsync(CancellationToken); stop the listener
                // from the shutdown path to release this await instead.
                client = await _listener.AcceptTcpClientAsync();
#else
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
#endif
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _onError?.Invoke("accept failed: " + ex.Message);
                continue;
            }

#if NETSTANDARD2_0
            // Task.Run and await continuations both depend on Unity's ThreadPool, which does not
            // schedule reliably for a plugin that starts before the player loop is up: the request
            // parses, then the continuation after `await _handler(...)` never resumes and the client
            // waits forever. Give every connection its own thread and run the handler synchronously.
            var worker = new Thread(() => ServeSync(client)) { IsBackground = true, Name = "UnityRuntimeDebugger conn" };
            worker.Start();
#else
            _ = Task.Run(() => Serve(client));
#endif
        }
    }

#if NETSTANDARD2_0
    /// <summary>
    /// Blocking accept loop. Deliberately not async: an await here would resume on Unity's
    /// ThreadPool, which is exactly the scheduler that never runs for a pre-player-loop plugin.
    /// </summary>
    private void AcceptLoopSync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _onError?.Invoke("accept failed: " + ex.Message);
                continue;
            }

            var worker = new Thread(() => ServeSync(client)) { IsBackground = true, Name = "UnityRuntimeDebugger conn" };
            worker.Start();
        }
    }
#endif

#if NETSTANDARD2_0
    /// <summary>
    /// Synchronous request/response — no await anywhere on the connection thread, so nothing depends
    /// on a continuation being scheduled. Blob.BlockingHandler already runs handlers off-thread and
    /// blocks on the main thread itself, so completing the task here is safe.
    /// </summary>
    private void ServeSync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                headerTimeout.CancelAfter(HeaderTimeout);

                // The request parser is async because it awaits the socket; run it to completion on
                // this thread by pumping the returned task's own continuation.
                var request = HttpRequest.ReadSync(stream);
                if (request == null)
                    return;

                HttpResponse response;
                try
                {
                    if (SyncHandler != null)
                    {
                        response = SyncHandler(request);
                    }
                    else
                    {
                        var handlerTask = _handler(request, _stopping.Token);
                        response = handlerTask.GetAwaiter().GetResult();
                    }
                }
                catch (ApiException ex)
                {
                    response = HttpResponse.Json(ex.ToBody(), ex.Status);
                }

                response.WriteSync(stream, request);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // client went away or the server is stopping
            }
            catch (Exception ex)
            {
                _onError?.Invoke("request failed: " + ex);
            }
        }
    }
#endif

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                HttpRequest? request;
                using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
                {
                    headerTimeout.CancelAfter(HeaderTimeout);
                    request = await HttpRequest.ReadAsync(stream, headerTimeout.Token);
                }

                if (request == null)
                    return;
#if NETSTANDARD2_0
#endif

                HttpResponse response;
                try
                {
                    response = await _handler(request, _stopping.Token);
                }
                catch (ApiException ex)
                {
                    response = HttpResponse.Json(ex.ToBody(), ex.Status);
                }

                await response.WriteAsync(stream, request, _stopping.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // client went away or the server is stopping
            }
            catch (Exception ex)
            {
                _onError?.Invoke("request failed: " + ex);
            }
        }
    }
}
