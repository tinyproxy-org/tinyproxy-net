# TinyProxy.NET

TinyProxy.NET 是对 [tinyproxy](https://github.com/tinyproxy/tinyproxy) 的 .NET 实现，目标是保持轻量、高性能和可维护性。

## 当前能力

- HTTP/1.0、HTTP/1.1 转发
- HTTPS CONNECT 隧道
- ACL、URL 过滤、Basic Auth
- 透明代理、反向代理、上游代理（HTTP / SOCKS）

## 快速开始

```bash
dotnet build src/TinyProxy/TinyProxy.csproj
dotnet test tests/TinyProxy.Tests/TinyProxy.Tests.csproj
dotnet run --project src/TinyProxy/TinyProxy.csproj -- -c tinyproxy.conf
```
