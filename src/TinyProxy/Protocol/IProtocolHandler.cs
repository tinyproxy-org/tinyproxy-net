namespace TinyProxy.Protocol;

/// <summary>
/// Interface for handling different proxy protocols.
/// </summary>
public interface IProtocolHandler
{
    /// <summary>
    /// Gets protocol name.
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Processes a connection asynchronously.
    /// </summary>
    /// <param name="connection">The connection to process.</param>
    /// <param name="request">The parsed HTTP request.</param>
    /// <param name="token">Cancellation token for async operations.</param>
    /// <returns>Processing result with status code and bytes transferred.</returns>
    ValueTask<ProcessingResult> ProcessAsync(
        Connection connection,
        HttpRequest request,
        CancellationToken token);
}

/// <summary>
/// Result of protocol processing.
/// </summary>
public record ProcessingResult
{
    /// <summary>
    /// Whether processing was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// HTTP status code returned to client.
    /// </summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// Number of bytes transferred to client.
    /// </summary>
    public long BytesTransferred { get; init; }
}