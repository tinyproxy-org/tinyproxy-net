using System.Buffers.Text;

namespace TinyProxy.Protocol.Http;

/// <summary>
/// HTTP request parser using Span<byte> with optimized string handling.
/// Uses array pooling and zero-copy techniques to minimize allocations.
/// </summary>
public sealed class HttpRequestParser
{
    private readonly ILogger _logger;

    // Character codes for fast comparison
    private const byte CR = (byte)'\r';
    private const byte LF = (byte)'\n';
    private const byte Colon = (byte)':';

    public HttpRequestParser(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Tries to parse an HTTP request from the buffer.
    /// Returns true if complete request received, false if more data needed.
    /// </summary>
    public bool TryParseRequest(ref ReadOnlySequence<byte> buffer, out HttpRequest? request)
    {
        request = null;

        if (buffer.IsEmpty) return false;

        // Find end of headers (CRLF CRLF or LF LF for compatibility)
        if (!FindHeadersEnd(buffer, out _)) return false;

        var reader = new SequenceReader<byte>(buffer);

        // Parse request line: METHOD SP URI SP VERSION (CR)LF.
        // tinyproxy C skips leading blank lines before the actual request line.
        byte[]? requestLineBytes = null;
        while (true)
        {
            if (!TryReadLine(ref reader, out var requestLine)) return false;
            if (requestLine.IsEmpty) continue;
            requestLineBytes = requestLine.ToArray();
            break;
        }

        if (!TryParseRequestLine(requestLineBytes, out var methodSpan, out var uriSpan, out var versionSpan))
            return false;

        var rawMethod = GetAsciiString(methodSpan);
        var method = HttpMethodParser.Parse(methodSpan);
        if (method == HttpMethod.None) _logger.LogWarning($"Unknown HTTP method: {rawMethod}");

        // tinyproxy C only accepts 2-token request lines for HTTP/0.9 GET.
        if (versionSpan.SequenceEqual("HTTP/0.9"u8) && method != HttpMethod.Get)
            return false;

        var uri = GetAsciiString(uriSpan);
        var version = GetAsciiString(versionSpan);

        // Parse headers
        var headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase);
        var headerLines = new List<KeyValuePair<string, ReadOnlySequence<byte>>>(16);
        string? host = null;
        string? userAgent = null;
        string? contentType = null;
        long? contentLength = null;
        string? currentHeaderName = null;
        ArrayBufferWriter<byte>? currentHeaderValue = null;
        var headerLineCount = 0;

        bool CommitCurrentHeader()
        {
            if (currentHeaderName == null || currentHeaderValue == null) return true;

            var valueBytes = currentHeaderValue.WrittenMemory.ToArray();
            var headerValueSequence = new ReadOnlySequence<byte>(valueBytes);
            headerLines.Add(new KeyValuePair<string, ReadOnlySequence<byte>>(currentHeaderName, headerValueSequence));

            if (!headers.ContainsKey(currentHeaderName))
            {
                headers[currentHeaderName] = headerValueSequence;
                ParseCommonHeader(
                    currentHeaderName,
                    currentHeaderValue.WrittenSpan,
                    ref host,
                    ref userAgent,
                    ref contentType,
                    ref contentLength);
            }

            currentHeaderName = null;
            currentHeaderValue = null;
            return true;
        }

        while (true)
        {
            if (!TryReadLine(ref reader, out var headerLine)) return false;
            if (++headerLineCount > ProxyConstants.MaxHeaders) return false;

            if (headerLine.IsEmpty)
            {
                if (!CommitCurrentHeader()) return false;

                // End of headers
                buffer = buffer.Slice(reader.Position);

                request = new HttpRequest
                {
                    Method = method,
                    RawMethod = rawMethod,
                    Uri = uri,
                    Version = version,
                    Headers = headers,
                    HeaderLines = headerLines,
                    Host = host,
                    UserAgent = userAgent,
                    ContentType = contentType,
                    ContentLength = contentLength,
                    Body = buffer
                };

                return true;
            }

            // Header continuation (folding): append to previous header if present.
            if (IsHeaderContinuationLine(headerLine))
            {
                if (currentHeaderValue != null)
                {
                    var continuationValue = TextUtils.Trim(headerLine);
                    if (!continuationValue.IsEmpty)
                    {
                        var separator = currentHeaderValue.GetSpan(1);
                        separator[0] = (byte)' ';
                        currentHeaderValue.Advance(1);
                        AppendBytes(currentHeaderValue, continuationValue);
                    }
                }

                continue;
            }

            if (!CommitCurrentHeader()) return false;

            // Parse header: Name: Value.
            // tinyproxy C ignores malformed headers instead of rejecting the entire request.
            var colonIndex = headerLine.IndexOf(Colon);
            if (colonIndex <= 0) continue;

            var headerNameSpan = TextUtils.Trim(headerLine[..colonIndex]);
            if (headerNameSpan.IsEmpty) continue;

            var headerValueSpan = TextUtils.Trim(headerLine[(colonIndex + 1)..]);

            currentHeaderName = GetAsciiString(headerNameSpan);
            currentHeaderValue = new ArrayBufferWriter<byte>(Math.Max(headerValueSpan.Length, 16));
            AppendBytes(currentHeaderValue, headerValueSpan);
        }
    }

    private static bool TryReadLine(ref SequenceReader<byte> reader, out ReadOnlySpan<byte> line)
    {
        line = ReadOnlySpan<byte>.Empty;

        // Read to LF and trim optional CR.
        if (!reader.TryReadTo(out ReadOnlySpan<byte> rawLine, LF)) return false;
        if (!rawLine.IsEmpty && rawLine[^1] == CR)
            line = rawLine[..^1];
        else
            line = rawLine;

        return true;
    }

    private static bool TryParseRequestLine(
        ReadOnlySpan<byte> requestLine,
        out ReadOnlySpan<byte> method,
        out ReadOnlySpan<byte> uri,
        out ReadOnlySpan<byte> version)
    {
        method = ReadOnlySpan<byte>.Empty;
        uri = ReadOnlySpan<byte>.Empty;
        version = ReadOnlySpan<byte>.Empty;

        var firstSpace = requestLine.IndexOf((byte)' ');
        if (firstSpace <= 0) return false;

        var secondSpaceRelative = requestLine[(firstSpace + 1)..].IndexOf((byte)' ');
        if (secondSpaceRelative < 0)
        {
            method = requestLine[..firstSpace];
            uri = requestLine[(firstSpace + 1)..];
            version = "HTTP/0.9"u8;
            return !uri.IsEmpty;
        }

        if (secondSpaceRelative == 0) return false;

        var secondSpace = firstSpace + 1 + secondSpaceRelative;

        method = requestLine[..firstSpace];
        uri = requestLine[(firstSpace + 1)..secondSpace];
        version = requestLine[(secondSpace + 1)..];
        return !version.IsEmpty;
    }

    private static void ParseCommonHeader(
        string name,
        ReadOnlySpan<byte> value,
        ref string? host,
        ref string? userAgent,
        ref string? contentType,
        ref long? contentLength)
    {
        if (value.Length == 0) return;

        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            host = GetAsciiString(value);
            return;
        }

        if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
        {
            userAgent = GetAsciiString(value);
            return;
        }

        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            contentType = GetAsciiString(value);
            return;
        }

        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
            Utf8Parser.TryParse(value, out long cl, out var consumed) &&
            consumed == value.Length &&
            cl >= 0)
            contentLength = cl;
    }

    private static bool IsHeaderContinuationLine(ReadOnlySpan<byte> line)
    {
        return !line.IsEmpty && (line[0] == (byte)' ' || line[0] == (byte)'\t');
    }

    private static void AppendBytes(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return;
        source.CopyTo(writer.GetSpan(source.Length));
        writer.Advance(source.Length);
    }

    /// <summary>
    /// Finds the end of HTTP headers (CRLF CRLF or LF LF).
    /// Aligns with tinyproxy C's CHECK_CRLF which supports both \r\n and single \n.
    /// </summary>
    private static bool FindHeadersEnd(ReadOnlySequence<byte> buffer, out SequencePosition position)
    {
        position = default;

        if (buffer.Length < 2) return false;

        var reader = new SequenceReader<byte>(buffer);
        byte p3 = 0, p2 = 0, p1 = 0;

        while (reader.TryRead(out var b))
        {
            // Check for LF LF (non-standard but allowed by tinyproxy)
            if (p1 == LF && b == LF)
            {
                position = reader.Position;
                return true;
            }

            // Check for CRLF CRLF
            if (p3 == CR && p2 == LF && p1 == CR && b == LF)
            {
                position = reader.Position;
                return true;
            }

            p3 = p2;
            p2 = p1;
            p1 = b;
        }

        return false;
    }

    /// <summary>
    /// Converts a byte span to ASCII string.
    /// </summary>
    private static string GetAsciiString(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0) return string.Empty;

        return Encoding.ASCII.GetString(span);
    }
}
