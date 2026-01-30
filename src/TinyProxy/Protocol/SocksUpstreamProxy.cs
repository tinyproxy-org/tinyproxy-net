using System.Buffers;
using System.Net.Sockets;
using System.Text;
using TinyProxy.Config;
using TinyProxy.Core;
using TinyProxy.Logging;

namespace TinyProxy.Protocol;

/// <summary>
/// Handles SOCKS4 and SOCKS5 upstream proxy connections.
/// Aligns with tinyproxy C's upstream.c SOCKS support.
/// </summary>
public sealed class SocksUpstreamProxy
{
    private readonly ILogger _logger;
    private readonly UpstreamProxyConfig _config;
    private readonly TimeSpan _timeout;

    public SocksUpstreamProxy(ILogger logger, UpstreamProxyConfig config, TimeSpan timeout)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _timeout = timeout;
    }

    /// <summary>
    /// Connects to the target host through the SOCKS proxy.
    /// </summary>
    public async ValueTask<Socket> ConnectAsync(string targetHost, int targetPort, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        try
        {
            // Connect to SOCKS proxy
            await socket.ConnectAsync(_config.Host, _config.Port, _timeout, cancellationToken).ConfigureAwait(false);

            // Perform SOCKS handshake based on type
            if (_config.Type == UpstreamProxyType.Socks4)
            {
                await Socks4HandshakeAsync(socket, targetHost, targetPort, cancellationToken).ConfigureAwait(false);
            }
            else // SOCKS5
            {
                await Socks5HandshakeAsync(socket, targetHost, targetPort, cancellationToken).ConfigureAwait(false);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Performs SOCKS4 handshake.
    /// Aligns with SOCKS4 protocol specification.
    /// </summary>
    private async ValueTask Socks4HandshakeAsync(Socket socket, string targetHost, int targetPort, CancellationToken token)
    {
        // SOCKS4 only supports IPv4 addresses
        if (!System.Net.IPAddress.TryParse(targetHost, out var ipAddress) ||
            ipAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new NotSupportedException($"SOCKS4 requires IPv4 address, got: {targetHost}");
        }

        var ipBytes = ipAddress.GetAddressBytes();

        // SOCKS4 connect request:
        // +----+----+----+----+----+----+----+----+----+----+....+----+
        // | VN | CD | DSTPORT |      DSTIP        | USERID       |NULL|
        // +----+----+----+----+----+----+----+----+----+----+....+----+
        //    1    1      2           4           variable       1

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_timeout);

        var request = ArrayPool<byte>.Shared.Rent(9 + (_config.Username?.Length ?? 0) + 1);

        try
        {
            request[0] = 4; // SOCKS version 4
            request[1] = 1; // CONNECT command

            // Port (big-endian)
            request[2] = (byte)((targetPort >> 8) & 0xFF);
            request[3] = (byte)(targetPort & 0xFF);

            // IP address
            Array.Copy(ipBytes, 0, request, 4, 4);

            // User ID (if provided)
            if (!string.IsNullOrEmpty(_config.Username))
            {
                var userBytes = Encoding.ASCII.GetBytes(_config.Username);
                Array.Copy(userBytes, 0, request, 8, userBytes.Length);
                request[8 + userBytes.Length] = 0; // Null terminator
            }
            else
            {
                request[8] = 0; // Null terminator for empty user ID
            }

            var requestLength = 8 + (_config.Username?.Length ?? 0) + 1;

            await socket.SendAsync(request.AsMemory(0, requestLength), SocketFlags.None, cts.Token).ConfigureAwait(false);

            // SOCKS4 response:
            // +----+----+----+----+----+----+----+----+
            // | VN | CD | DSTPORT |      DSTIP        |
            // +----+----+----+----+----+----+----+----+
            //    1    1      2           4

            var response = ArrayPool<byte>.Shared.Rent(8);
            try
            {
                var received = await socket.ReceiveAsync(response, SocketFlags.None, cts.Token).ConfigureAwait(false);
                if (received < 8)
                {
                    throw new InvalidOperationException("SOCKS4: Incomplete response");
                }

                if (response[1] != 90)
                {
                    var error = response[1] switch
                    {
                        91 => "Request rejected or failed",
                        92 => "Request rejected because SOCKS server cannot connect to identd",
                        93 => "Request rejected because the client program and identd report different user-ids",
                        _ => "Unknown error"
                    };
                    throw new InvalidOperationException($"SOCKS4: {error} (code {response[1]})");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(response);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(request);
        }
    }

    /// <summary>
    /// Performs SOCKS5 handshake.
    /// Aligns with SOCKS5 protocol specification (RFC 1928).
    /// </summary>
    private async ValueTask Socks5HandshakeAsync(Socket socket, string targetHost, int targetPort, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_timeout);

        // SOCKS5 greeting:
        // +----+----------+----------+
        // |VER | NMETHODS | METHODS  |
        // +----+----------+----------+
        //  1      1       1 to 255

        var authRequired = !string.IsNullOrEmpty(_config.Username);
        var numMethods = authRequired ? 2 : 1;

        var greeting = ArrayPool<byte>.Shared.Rent(3);
        try
        {
            greeting[0] = 5; // SOCKS version 5
            greeting[1] = (byte)numMethods;

            if (authRequired)
            {
                greeting[2] = 0x00; // No authentication
                greeting[3] = 0x02; // Username/password authentication
            }
            else
            {
                greeting[2] = 0x00; // No authentication
            }

            await socket.SendAsync(greeting.AsMemory(0, 2 + numMethods), SocketFlags.None, cts.Token).ConfigureAwait(false);

            // Server response:
            // +----+--------+
            // |VER | METHOD |
            // +----+--------+
            //   1      1

            var greetingResponse = ArrayPool<byte>.Shared.Rent(2);
            try
            {
                var received = await socket.ReceiveAsync(greetingResponse, SocketFlags.None, cts.Token).ConfigureAwait(false);
                if (received < 2)
                {
                    throw new InvalidOperationException("SOCKS5: Incomplete greeting response");
                }

                if (greetingResponse[0] != 5)
                {
                    throw new InvalidOperationException($"SOCKS5: Invalid version {greetingResponse[0]}");
                }

                var method = greetingResponse[1];

                // Handle authentication if required
                if (method == 0x02)
                {
                    if (!authRequired)
                    {
                        throw new InvalidOperationException("SOCKS5: Server requires authentication but none provided");
                    }

                    await PerformUsernamePasswordAuthAsync(socket, cts.Token).ConfigureAwait(false);
                }
                else if (method != 0x00)
                {
                    throw new InvalidOperationException($"SOCKS5: Server rejected authentication method (code {method})");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(greetingResponse);
            }

            // SOCKS5 connect request:
            // +----+-----+-------+------+----------+----------+
            // |VER | CMD |  RSV  | ATYP | DST.ADDR | DST.PORT |
            // +----+-----+-------+------+----------+----------+
            //  1    1      1       1    variable      2

            var connectRequest = BuildConnectRequest(targetHost, targetPort);
            try
            {
                await socket.SendAsync(connectRequest, SocketFlags.None, cts.Token).ConfigureAwait(false);

                // SOCKS5 response:
                // +----+-----+-------+------+----------+----------+
                // |VER | REP |  RSV  | ATYP | BND.ADDR | BND.PORT |
                // +----+-----+-------+------+----------+----------+
                //  1    1      1       1    variable      2

                var connectResponse = ArrayPool<byte>.Shared.Rent(10); // Minimum size
                try
                {
                    var received = await socket.ReceiveAsync(connectResponse, SocketFlags.None, cts.Token).ConfigureAwait(false);

                    if (received < 4)
                    {
                        throw new InvalidOperationException("SOCKS5: Incomplete connect response");
                    }

                    if (connectResponse[0] != 5)
                    {
                        throw new InvalidOperationException($"SOCKS5: Invalid version {connectResponse[0]}");
                    }

                    if (connectResponse[1] != 0)
                    {
                        var error = connectResponse[1] switch
                        {
                            1 => "General SOCKS server failure",
                            2 => "Connection not allowed by ruleset",
                            3 => "Network unreachable",
                            4 => "Host unreachable",
                            5 => "Connection refused",
                            6 => "TTL expired",
                            7 => "Command not supported",
                            8 => "Address type not supported",
                            _ => "Unknown error"
                        };
                        throw new InvalidOperationException($"SOCKS5: {error} (code {connectResponse[1]})");
                    }

                    // Read remaining response if needed based on ATYP
                    var atyp = connectResponse[3];
                    if (atyp == 1) // IPv4: 4 bytes
                    {
                        // Already have 6 more bytes (4 IP + 2 port)
                        if (received < 10)
                        {
                            await socket.ReceiveAsync(
                                connectResponse.AsMemory(received, 10 - received),
                                SocketFlags.None,
                                cts.Token).ConfigureAwait(false);
                        }
                    }
                    else if (atyp == 3) // Domain: 1 byte length + domain
                    {
                        var domainLen = connectResponse[4];
                        if (received < 5 + domainLen + 2)
                        {
                            var remaining = new byte[5 + domainLen + 2 - received];
                            await socket.ReceiveAsync(remaining, SocketFlags.None, cts.Token).ConfigureAwait(false);
                        }
                    }
                    else if (atyp == 4) // IPv6: 16 bytes
                    {
                        if (received < 18)
                        {
                            var remaining = new byte[18 - received];
                            await socket.ReceiveAsync(remaining, SocketFlags.None, cts.Token).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(connectResponse);
                }
            }
            finally
            {
                // connectRequest is a rented array that needs to be returned
                // But we can't return a ReadOnlyMemory directly, so we track the length
                // The caller should handle this, but for now we'll skip returning
                // as it's small and will be GC'd
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(greeting);
        }
    }

    /// <summary>
    /// Performs SOCKS5 username/password authentication.
    /// Aligns with RFC 1929.
    /// </summary>
    private async ValueTask PerformUsernamePasswordAuthAsync(Socket socket, CancellationToken token)
    {
        var username = _config.Username ?? string.Empty;
        var password = _config.Password ?? string.Empty;

        // +----+------+----------+------+----------+
        // |VER | ULEN |  UNAME   | PLEN |  PASSWD  |
        // +----+------+----------+------+----------+
        //  1    1     variable    1     variable

        var authRequest = new byte[3 + username.Length + password.Length];
        authRequest[0] = 1; // Username/password authentication version
        authRequest[1] = (byte)username.Length;

        var userBytes = Encoding.ASCII.GetBytes(username);
        Array.Copy(userBytes, 0, authRequest, 2, userBytes.Length);

        authRequest[2 + userBytes.Length] = (byte)password.Length;

        var passBytes = Encoding.ASCII.GetBytes(password);
        Array.Copy(passBytes, 0, authRequest, 3 + userBytes.Length, passBytes.Length);

        await socket.SendAsync(authRequest, SocketFlags.None, token).ConfigureAwait(false);

        // Server response:
        // +----+--------+
        // |VER | STATUS |
        // +----+--------+
        //   1      1

        var authResponse = ArrayPool<byte>.Shared.Rent(2);
        try
        {
            var received = await socket.ReceiveAsync(authResponse, SocketFlags.None, token).ConfigureAwait(false);
            if (received < 2)
            {
                throw new InvalidOperationException("SOCKS5: Incomplete auth response");
            }

            if (authResponse[1] != 0)
            {
                throw new InvalidOperationException("SOCKS5: Authentication failed");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(authResponse);
        }
    }

    /// <summary>
    /// Builds a SOCKS5 CONNECT request for the target host and port.
    /// </summary>
    private ReadOnlyMemory<byte> BuildConnectRequest(string targetHost, int targetPort)
    {
        using var ms = new System.IO.MemoryStream();

        // Version and command
        ms.WriteByte(5); // SOCKS version
        ms.WriteByte(1); // CONNECT command
        ms.WriteByte(0); // Reserved

        // Address type and address
        if (System.Net.IPAddress.TryParse(targetHost, out var ipAddress))
        {
            if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // IPv4
                ms.WriteByte(1); // ATYP = IPv4
                ms.Write(ipAddress.GetAddressBytes(), 0, 4);
            }
            else
            {
                // IPv6
                ms.WriteByte(4); // ATYP = IPv6
                ms.Write(ipAddress.GetAddressBytes(), 0, 16);
            }
        }
        else
        {
            // Domain name
            var hostBytes = Encoding.ASCII.GetBytes(targetHost);
            ms.WriteByte(3); // ATYP = Domain
            ms.WriteByte((byte)hostBytes.Length);
            ms.Write(hostBytes, 0, hostBytes.Length);
        }

        // Port (big-endian)
        ms.WriteByte((byte)((targetPort >> 8) & 0xFF));
        ms.WriteByte((byte)(targetPort & 0xFF));

        return new ReadOnlyMemory<byte>(ms.ToArray());
    }
}
