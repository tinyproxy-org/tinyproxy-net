using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyProxy.Config;
using TinyProxy.Core;

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
    public async ValueTask<Socket> ConnectAsync(string targetHost, int targetPort, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        try
        {
            // Connect to SOCKS proxy with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_timeout);

            await socket.ConnectAsync(_config.Host, _config.Port, cts.Token).ConfigureAwait(false);

            // Perform SOCKS handshake based on type
            if (_config.Type == UpstreamProxyType.Socks4)
                await Socks4HandshakeAsync(socket, targetHost, targetPort, cts.Token).ConfigureAwait(false);
            else // SOCKS5
                await Socks5HandshakeAsync(socket, targetHost, targetPort, cts.Token).ConfigureAwait(false);

            return socket;
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new InvalidOperationException($"Failed to connect to SOCKS proxy {_config.Host}:{_config.Port}", ex);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"SOCKS proxy connection timed out after {_timeout}", ex);
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
            ipAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException($"SOCKS4 requires IPv4 address, got: {targetHost}");

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

            await socket.SendAllAsync(request.AsMemory(0, requestLength), cts.Token).ConfigureAwait(false);

            // SOCKS4 response:
            // +----+----+----+----+----+----+----+----+
            // | VN | CD | DSTPORT |      DSTIP        |
            // +----+----+----+----+----+----+----+----+
            //    1    1      2           4

            var response = ArrayPool<byte>.Shared.Rent(8);
            try
            {
                await socket.ReceiveExactlyAsync(response.AsMemory(0, 8), cts.Token).ConfigureAwait(false);

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
        //  1    1     1 to 255

        var useUsernameAuth = !string.IsNullOrEmpty(_config.Username);
        var greeting = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            greeting[0] = 5; // SOCKS version 5

            int greetingLength;
            if (useUsernameAuth)
            {
                // Username/password authentication
                greeting[1] = 2; // 2 methods
                greeting[2] = 0x00; // No authentication
                greeting[3] = 0x02; // Username/password
                greetingLength = 4;
            }
            else
            {
                // No authentication
                greeting[1] = 1; // 1 method
                greeting[2] = 0x00; // No authentication
                greetingLength = 3;
            }

            await socket.SendAllAsync(greeting.AsMemory(0, greetingLength), cts.Token).ConfigureAwait(false);

            // Server response:
            // +----+--------+
            // |VER | METHOD |
            // +----+--------+
            //   1      1

            var greetingResponse = ArrayPool<byte>.Shared.Rent(2);
            try
            {
                await socket.ReceiveExactlyAsync(greetingResponse.AsMemory(0, 2), cts.Token).ConfigureAwait(false);

                if (greetingResponse[0] != 5) throw new InvalidOperationException($"SOCKS5: Invalid version {greetingResponse[0]}");

                var method = greetingResponse[1];
                if (method == 0xFF) throw new InvalidOperationException("SOCKS5: No acceptable authentication method");

                // If username/password auth required
                if (method == 0x02) await PerformUsernamePasswordAuthAsync(socket, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(greetingResponse);
            }

            // CONNECT request:
            // +----+-----+-------+------+----------+----------+
            // |VER | CMD |  RSV  | ATYP | DST.ADDR | DST.PORT |
            // +----+-----+-------+------+----------+----------+
            //   1    1     1       1    Variable      2

            var connectRequest = BuildConnectRequest(targetHost, targetPort);
            await socket.SendAllAsync(connectRequest, cts.Token).ConfigureAwait(false);

            // CONNECT response:
            // +----+-----+-------+------+----------+----------+
            // |VER | REP |  RSV  | ATYP | BND.ADDR | BND.PORT |
            // +----+-----+-------+------+----------+----------+
            //   1    1     1       1    Variable      2

            var connectResponse = ArrayPool<byte>.Shared.Rent(4 + 1 + 255 + 2);
            try
            {
                await socket.ReceiveExactlyAsync(connectResponse.AsMemory(0, 4), cts.Token).ConfigureAwait(false);

                if (connectResponse[0] != 5) throw new InvalidOperationException($"SOCKS5: Invalid version {connectResponse[0]}");

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
                    await socket.ReceiveExactlyAsync(connectResponse.AsMemory(4, 6), cts.Token).ConfigureAwait(false);
                }
                else if (atyp == 3) // Domain: 1 byte length + domain
                {
                    await socket.ReceiveExactlyAsync(connectResponse.AsMemory(4, 1), cts.Token).ConfigureAwait(false);
                    var domainLen = connectResponse[4];
                    await socket.ReceiveExactlyAsync(connectResponse.AsMemory(5, domainLen + 2), cts.Token).ConfigureAwait(false);
                }
                else if (atyp == 4) // IPv6: 16 bytes
                {
                    await socket.ReceiveExactlyAsync(connectResponse.AsMemory(4, 18), cts.Token).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException($"SOCKS5: Unsupported address type {atyp}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(connectResponse);
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
        if (username.Length > byte.MaxValue || password.Length > byte.MaxValue)
            throw new InvalidOperationException("SOCKS5: Username/password too long");

        // +----+------+----------+------+----------+
        // |VER | ULEN |  UNAME   | PLEN |  PASSWD  |
        // +----+------+----------+------+----------+
        //  1    1     variable    1     variable

        var authRequest = new byte[3 + username.Length + password.Length];
        var userBytes = Encoding.ASCII.GetBytes(username);
        var passBytes = Encoding.ASCII.GetBytes(password);

        try
        {
            authRequest[0] = 1; // Username/password authentication version
            authRequest[1] = (byte)username.Length;
            Array.Copy(userBytes, 0, authRequest, 2, userBytes.Length);
            authRequest[2 + userBytes.Length] = (byte)password.Length;
            Array.Copy(passBytes, 0, authRequest, 3 + userBytes.Length, passBytes.Length);

            await socket.SendAllAsync(authRequest, token).ConfigureAwait(false);

            // Server response:
            // +----+--------+
            // |VER | STATUS |
            // +----+--------+
            //   1      1

            var authResponse = ArrayPool<byte>.Shared.Rent(2);
            try
            {
                await socket.ReceiveExactlyAsync(authResponse.AsMemory(0, 2), token).ConfigureAwait(false);

                if (authResponse[1] != 0) throw new InvalidOperationException("SOCKS5: Authentication failed");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(authResponse);
            }
        }
        finally
        {
            // Clear sensitive data from memory
            CryptographicOperations.ZeroMemory(authRequest);
            CryptographicOperations.ZeroMemory(userBytes);
            CryptographicOperations.ZeroMemory(passBytes);
        }
    }

    /// <summary>
    /// Builds a SOCKS5 CONNECT request for the target host and port.
    /// </summary>
    private static ReadOnlyMemory<byte> BuildConnectRequest(string targetHost, int targetPort)
    {
        using var ms = new MemoryStream();

        // Version and command
        ms.WriteByte(5); // SOCKS version
        ms.WriteByte(1); // CONNECT command
        ms.WriteByte(0); // Reserved

        // Address type and address
        if (System.Net.IPAddress.TryParse(targetHost, out var ipAddress))
        {
            if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
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
            if (hostBytes.Length > byte.MaxValue)
                throw new InvalidOperationException("SOCKS5: Target host too long");
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
