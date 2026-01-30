using System.Buffers;
using System.Text;
using TinyProxy.Core;

namespace TinyProxy.Protocol.Http;

/// <summary>
/// HTTP request parser using Span<byte>.
/// </summary>
public sealed class HttpRequestParser
{
    private readonly ILogger _logger;
    private const byte CR = (byte)'\r';
    private const byte LF = (byte)'\n';
    private const byte Space = (byte)' ';
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

        if (buffer.IsEmpty)
        {
            return false;
        }

        // Find end of headers (CRLF CRLF or LF LF for compatibility)
        if (!FindHeadersEnd(buffer, out var headersEndPosition))
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);

        // Parse request line: METHOD SP URI SP VERSION CRLF
        if (!TryReadToSpan(ref reader, Space, out var methodSpan)) return false;
        if (!TryReadToSpan(ref reader, Space, out var uriSpan)) return false;
        if (!TryReadToSpan(ref reader, CR, out var versionSpan)) return false;
        if (!reader.TryRead(out var next) || next != LF) return false;

        var method = HttpMethodParser.Parse(methodSpan);
        if (method == HttpMethod.None)
        {
            _logger.LogWarning($"Unknown HTTP method: {Encoding.ASCII.GetString(methodSpan.ToArray())}");
            return false;
        }

        var uri = Encoding.ASCII.GetString(uriSpan.ToArray());
        var version = Encoding.ASCII.GetString(versionSpan.ToArray());

        // Parse headers
        var headers = new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.OrdinalIgnoreCase);
        string? host = null;
        string? userAgent = null;
        string? contentType = null;
        long? contentLength = null;

        while (!reader.End)
        {
            // Check for end of headers (empty line)
            if (reader.TryRead(out next) && next == CR)
            {
                if (!reader.TryRead(out next) || next != LF)
                {
                    return false; // Malformed
                }

                // End of headers
                var consumed = buffer.Slice(0, buffer.GetPosition(0, reader.Position));
                buffer = buffer.Slice(reader.Position);

                request = new HttpRequest
                {
                    Method = method,
                    Uri = uri,
                    Version = version,
                    Headers = headers,
                    Host = host,
                    UserAgent = userAgent,
                    ContentType = contentType,
                    ContentLength = contentLength,
                    Body = buffer
                };

                return true;
            }

            // Unread the byte we just peeked
            reader.Rewind(1);

            // Parse header: Name: Value CRLF
            if (!TryReadToSpan(ref reader, Colon, out var headerNameSpan)) return false;

            // Optional space after colon
            if (reader.TryRead(out next) && next != Space)
            {
                reader.Rewind(1);
            }

            if (!TryReadToSpan(ref reader, CR, out var headerValueSpan)) return false;
            if (!reader.TryRead(out next) || next != LF) return false;

            var headerName = Encoding.ASCII.GetString(headerNameSpan.ToArray());
            // Zero-copy: create ReadOnlySequence directly from span
            var headerValue = new ReadOnlySequence<byte>(headerValueSpan.ToArray());
            headers[headerName] = headerValue;

            // Parse common headers
            ParseCommonHeader(headerName, headerValue, ref host, ref userAgent, ref contentType, ref contentLength);
        }

        // Need more data
        return false;
    }

    private static bool TryReadToSpan(ref SequenceReader<byte> reader, byte delimiter, out ReadOnlySpan<byte> value)
    {
        value = ReadOnlySpan<byte>.Empty;

        if (!reader.TryReadTo(out ReadOnlySpan<byte> span, delimiter))
        {
            return false;
        }

        value = span;
        return true;
    }

    private static void ParseCommonHeader(
        string name,
        ReadOnlySequence<byte> value,
        ref string? host,
        ref string? userAgent,
        ref string? contentType,
        ref long? contentLength)
    {
        if (value.Length == 0) return;

        var valueStr = Encoding.ASCII.GetString(value.ToArray());
        var trimmedValue = valueStr.Trim();

        switch (name.ToUpperInvariant())
        {
            case "HOST":
                host = trimmedValue;
                break;
            case "USER-AGENT":
                userAgent = trimmedValue;
                break;
            case "CONTENT-TYPE":
                contentType = trimmedValue;
                break;
            case "CONTENT-LENGTH":
                if (long.TryParse(trimmedValue, out var cl))
                {
                    contentLength = cl;
                }
                break;
        }
    }

    /// <summary>
    /// Finds the end of HTTP headers (CRLF CRLF or LF LF).
    /// Aligns with tinyproxy C's CHECK_CRLF which supports both \r\n and single \n.
    /// </summary>
    private static bool FindHeadersEnd(ReadOnlySequence<byte> buffer, out SequencePosition position)
    {
        position = default;

        if (buffer.Length < 2)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);

        while (reader.Remaining >= 2)
        {
            if (reader.TryRead(out var b) && b == '\r')
            {
                // Check for CRLF CRLF
                if (reader.TryRead(out var b2) && b2 == '\n')
                {
                    if (reader.TryRead(out var b3) && b3 == '\r')
                    {
                        if (reader.TryRead(out var b4) && b4 == '\n')
                        {
                            position = reader.Position;
                            return true;
                        }
                    }
                }
            }
            else if (b == '\n')
            {
                // Check for LF LF (non-standard but allowed by tinyproxy)
                var nextPos = reader.Position;
                if (reader.TryRead(out var b2) && b2 == '\n')
                {
                    position = nextPos;
                    return true;
                }
            }
        }

        return false;
    }
}
