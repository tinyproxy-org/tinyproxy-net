# TinyProxy.NET

This project is a .NET rewrite of the upstream [tinyproxy](https://github.com/tinyproxy/tinyproxy).

TinyProxy.NET is a lightweight HTTP/HTTPS proxy server implemented on .NET, focused on predictable performance and maintainable code.

## Features

- HTTP/1.0 and HTTP/1.1 forwarding
- HTTPS CONNECT tunneling
- Access control (ACL), URL filtering, and Basic Authentication
- Transparent proxy mode
- Reverse proxy routing
- Upstream proxy support (HTTP, SOCKS4, SOCKS5)

## Build, Test, Run

```bash
dotnet build src/TinyProxy/TinyProxy.csproj
dotnet test tests/TinyProxy.Tests/TinyProxy.Tests.csproj
dotnet run --project src/TinyProxy/TinyProxy.csproj -- -c tinyproxy.conf
```
