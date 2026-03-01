namespace TinyProxy.Protocol.Http;

/// <summary>
/// HTTP methods supported by the proxy.
/// </summary>
public enum HttpMethod
{
    Get,
    Post,
    Put,
    Delete,
    Head,
    Options,
    Patch,
    Trace,
    Connect,
    None
}

/// <summary>
/// HTTP method parser utilities.
/// </summary>
public static class HttpMethodParser
{
    /// <summary>
    /// Executes parse.
    /// </summary>
    public static HttpMethod Parse(ReadOnlySpan<byte> method)
    {
        if (method.Length == 0) return HttpMethod.None;

        // Fast path for common methods
        if (method.Length == 3)
        {
            if (method[0] == 'G' && method[1] == 'E' && method[2] == 'T') return HttpMethod.Get;
        }
        else if (method.Length == 4)
        {
            if (method[0] == 'P' && method[1] == 'O' && method[2] == 'S' && method[3] == 'T')
                return HttpMethod.Post;
            if (method[0] == 'H' && method[1] == 'E' && method[2] == 'A' && method[3] == 'D')
                return HttpMethod.Head;
        }
        else if (method.Length == 7)
        {
            if (method[0] == 'C' && method[1] == 'O' && method[2] == 'N' &&
                method[3] == 'N' && method[4] == 'E' && method[5] == 'C' && method[6] == 'T')
                return HttpMethod.Connect;
        }

        return method switch
        {
            _ when method.SequenceEqual("PUT"u8) => HttpMethod.Put,
            _ when method.SequenceEqual("DELETE"u8) => HttpMethod.Delete,
            _ when method.SequenceEqual("OPTIONS"u8) => HttpMethod.Options,
            _ when method.SequenceEqual("PATCH"u8) => HttpMethod.Patch,
            _ when method.SequenceEqual("TRACE"u8) => HttpMethod.Trace,
            _ => HttpMethod.None
        };
    }

    /// <summary>
    /// Executes to http string.
    /// </summary>
    public static string ToHttpString(HttpMethod method)
    {
        return method switch
        {
            HttpMethod.Get => "GET",
            HttpMethod.Post => "POST",
            HttpMethod.Put => "PUT",
            HttpMethod.Delete => "DELETE",
            HttpMethod.Head => "HEAD",
            HttpMethod.Options => "OPTIONS",
            HttpMethod.Patch => "PATCH",
            HttpMethod.Trace => "TRACE",
            HttpMethod.Connect => "CONNECT",
            _ => "UNKNOWN"
        };
    }
}