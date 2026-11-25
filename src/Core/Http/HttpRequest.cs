using System.Text;

namespace UnityRuntimeDebugger.Http;

public sealed class HttpRequest
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffered;
    private byte[]? _body;

    private HttpRequest(string method, string target, Dictionary<string, string> headers, Stream stream, byte[] buffered)
    {
        Method = method;
        Target = target;
        Headers = headers;
        _stream = stream;
        _buffered = buffered;

        var q = target.IndexOf('?');
        Path = Uri.UnescapeDataString(q < 0 ? target : target.Substring(0, q));
        Query = ParseQuery(q < 0 ? "" : target.Substring(q + 1));
    }

    public string Method { get; }

    /// <summary>The request target as sent, query string included.</summary>
    public string Target { get; }

    /// <summary>Decoded path without the query string.</summary>
    public string Path { get; }

    public IReadOnlyDictionary<string, string> Query { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public int ContentLength => int.TryParse(Header("Content-Length"), out var length) ? length : 0;

    /// <summary>Reads the body on first use, so unauthenticated requests never make us buffer one.</summary>
    public async Task<byte[]> ReadBodyAsync(CancellationToken cancellation)
    {
        if (_body != null)
            return _body;

        var length = ContentLength;
        if (length <= 0)
            return _body = Array.Empty<byte>();
        if (length > MaxBodyBytes)
            throw new ApiException(413, "too_large", $"request bodies are limited to {MaxBodyBytes / 1048576} MB");

        var body = new byte[length];
        var filled = Math.Min(_buffered.Length, length);
        Array.Copy(_buffered, body, filled);
        while (filled < length)
        {
            var read = await _stream.ReadAsync(body, filled, length - filled, cancellation);
            if (read == 0)
                throw new ApiException(400, "bad_request", "connection closed before the body was complete");
            filled += read;
        }

        return _body = body;
    }

    public async Task<string> ReadTextAsync(CancellationToken cancellation)
        => Encoding.UTF8.GetString(await ReadBodyAsync(cancellation));

    /// <summary>Parses the request line and headers; null when the client sent nothing usable.</summary>
    internal static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var buffer = new byte[8192];
        var length = 0;
        int end;
        while ((end = HeaderEnd(buffer, length)) < 0)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= MaxHeaderBytes)
                    return null;
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = await stream.ReadAsync(buffer, length, buffer.Length - length, cancellation);
            if (read == 0)
                return null;
            length += read;
        }

        return Parse(buffer, end, length, stream);
    }

    /// <summary>Builds the request from a complete header block already sitting in <paramref name="buffer"/>.</summary>
    private static HttpRequest? Parse(byte[] buffer, int end, int length, Stream stream)
    {
        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }

        var bodyStart = end + 4;
        return new HttpRequest(requestLine[0].ToUpperInvariant(), requestLine[1], headers, stream, Slice(buffer, bodyStart, length - bodyStart));
    }

    /// <summary>netstandard2.0 has no Range indices; copy the requested span instead.</summary>
    private static byte[] Slice(byte[] source, int start, int count)
    {
        var result = new byte[Math.Max(0, count)];
        Array.Copy(source, start, result, 0, Math.Max(0, count));
        return result;
    }

#if NETSTANDARD2_0
    /// <summary>
    /// Fully synchronous parse. The async path awaits the socket, and on Unity's Mono that
    /// continuation is scheduled on a ThreadPool that does not run for a plugin loaded before the
    /// player loop — so the connection thread must never depend on one.
    /// </summary>
    internal static HttpRequest? ReadSync(Stream stream)
    {
        var buffer = new byte[8192];
        var length = 0;
        int end;
        while ((end = HeaderEnd(buffer, length)) < 0)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= MaxHeaderBytes)
                    return null;
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
                return null;
            length += read;
        }

        return Parse(buffer, end, length, stream);
    }
#endif

    private static int HeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i] == '\n' && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r')
                return i - 3;
        }

        return -1;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Decode(eq < 0 ? pair : pair.Substring(0, eq));
            result[key] = eq < 0 ? "" : Decode(pair.Substring(eq + 1));
        }

        return result;
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
