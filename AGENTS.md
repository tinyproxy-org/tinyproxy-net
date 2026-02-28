# TinyProxy.NET AGENTS

## 项目概述

TinyProxy.NET 是对 [tinyproxy](https://github.com/tinyproxy/tinyproxy) 的 .NET 10 重写实现，目标是与 C 版本功能对齐，并保持高性能、低内存占用与可维护性。

**tinyproxy C 源码位置（已修正）**: `~/Repos/tinyproxy-org/tinyproxy/src`

---

## 最新状态（2026-02-28）

- 运行时: `net10.0`
- 当前分支: `main`
- 代码形态: 核心代理链路已落地（HTTP/HTTPS CONNECT、ACL、过滤、认证、反向/透明代理、上游代理、统计页、热重载）
- 测试状态: `dotnet test tests/TinyProxy.Tests/TinyProxy.Tests.csproj -v minimal` 通过，`Passed: 160, Failed: 0`
- 工程结构: `tinyproxy.sln` 当前仅包含 `src/TinyProxy/TinyProxy.csproj`；测试和基准项目独立维护

---

## C 模块映射（按当前代码对齐）

| C 文件 | 功能 | .NET 映射（当前） | 状态 |
|--------|------|-------------------|------|
| `main.c` | 入口、启动与退出 | `Program.cs` | ✅ |
| `child.c` | 并发连接管理 | `Core/ConnectionManager.cs` | ✅ |
| `reqs.c` | 请求主流程与转发 | `Core/Connection.cs`, `Protocol/Http/HttpForwarder.cs`, `Protocol/ConnectHandler.cs` | ✅ |
| `conf.c` | 配置解析 | `Config/ConfigParser.cs`, `Config/Configuration.cs` | ✅ |
| `buffer.c` | 缓冲策略 | `ArrayPool<byte>` 在 `Connection`/`HttpForwarder`，`Core/StringBuilderCache.cs` | ✅ |
| `sock.c` | Socket 扩展操作 | `Core/SocketExtensions.cs` | ✅ |
| `http-message.c` | HTTP 报文解析 | `Protocol/Http/HttpRequestParser.cs`, `Protocol/Http/HttpRequest.cs` | ✅ |
| `acl.c` | 访问控制 | `Filter/AccessControl.cs` | ✅ |
| `upstream.c` | 上游代理 | `Protocol/Http/HttpForwarder.cs`, `Protocol/ConnectHandler.cs`, `Protocol/SocksUpstreamProxy.cs` | ✅ |
| `log.c` | 日志系统 | `Core/ConsoleLogger.cs`, `Logging/AccessLogger.cs`, `Logging/SyslogLogger.cs` | ✅ |
| `transparent-proxy.c` | 透明代理 | `Protocol/TransparentProxy.cs` | ✅ |
| `reverse-proxy.c` | 反向代理 | `Protocol/ReverseProxy.cs` | ✅ |
| `connect-ports.c` | CONNECT 端口限制 | `Filter/ConnectFilter.cs` | ✅ |
| `filter.c` | URL 过滤 | `Filter/UrlFilter.cs` | ✅ |
| `basicauth.c` | 基本认证 | `Security/BasicAuth.cs` | ✅ |
| `html-error.c` | HTML 错误页 | `Protocol/HtmlErrorPages.cs` | ✅ |
| `loop.c` | 事件循环与环路检测 | `Core/EventLoop.cs`, `Core/LoopDetector.cs` | ✅ |
| `conns.c` | 单连接生命周期 | `Core/Connection.cs` | ✅ |
| `stats.c` | 统计计数与展示 | `Metrics/Stats.cs`, `Protocol/StatsHandler.cs` | ✅ |
| `anonymous.c` | 匿名模式头过滤 | `Filter/AnonymousFilter.cs` | ✅ |
| `socks.c` | SOCKS 上游 | `Protocol/SocksUpstreamProxy.cs` | ✅ |

---

## 功能状态清单

### 1. 代理协议
- [x] HTTP/1.0 / HTTP/1.1 转发
- [x] HTTPS CONNECT 隧道
- [x] 透明代理
- [x] 反向代理
- [x] HTTP 上游代理
- [x] SOCKS4/SOCKS5 上游代理

### 2. 访问控制与过滤
- [x] IP Allow/Deny（含 CIDR / 通配）
- [x] URL 过滤（regex / glob）
- [x] CONNECT 端口限制
- [x] Via / X-Tinyproxy 头控制
- [x] 匿名模式头白名单
- [x] Basic Auth（单用户 + 多用户）

### 3. 配置与运行时
- [x] tinyproxy.conf 风格解析
- [x] `-c` 配置路径参数
- [x] 配置热重载（文件监控）
- [x] PID 文件
- [x] 优雅停止（Ctrl+C / 进程退出）

### 4. 观测能力
- [x] Access Log
- [x] 控制台日志
- [x] Syslog（RFC 5424 over UDP）
- [x] `StatHost` 统计页面
- [x] 统计计数器（连接、请求、流量、拒绝、失败）
- [~] `Metrics/PrometheusMetrics.cs` 与 `Metrics/HealthCheck.cs` 已实现类，默认启动流程尚未接线

---

## 当前目录结构（精简）

```text
src/TinyProxy/
├── Core/
│   ├── ConfigReloader.cs
│   ├── Connection.cs
│   ├── ConnectionManager.cs
│   ├── ConsoleLogger.cs
│   ├── Daemon.cs
│   ├── EventLoop.cs
│   ├── ILogger.cs
│   ├── LoopDetector.cs
│   ├── PidFileManager.cs
│   ├── ProxyConstants.cs
│   ├── SocketExtensions.cs
│   ├── StringBuilderCache.cs
│   └── TextUtils.cs
├── Config/
│   ├── ConfigParser.cs
│   └── Configuration.cs
├── Filter/
│   ├── AccessControl.cs
│   ├── AnonymousFilter.cs
│   ├── ConnectFilter.cs
│   └── UrlFilter.cs
├── Logging/
│   ├── AccessLogger.cs
│   ├── ConsoleStructuredLogger.cs
│   ├── StructuredLogger.cs
│   └── SyslogLogger.cs
├── Metrics/
│   ├── HealthCheck.cs
│   ├── PrometheusMetrics.cs
│   └── Stats.cs
├── Protocol/
│   ├── Http/
│   │   ├── ChunkedTransferHandler.cs
│   │   ├── HttpForwarder.cs
│   │   ├── HttpMethod.cs
│   │   ├── HttpProtocolHandler.cs
│   │   ├── HttpRequest.cs
│   │   ├── HttpRequestParser.cs
│   │   └── HttpResponseProcessor.cs
│   ├── Https/HttpsProtocolHandler.cs
│   ├── ConnectHandler.cs
│   ├── HtmlErrorPages.cs
│   ├── IProtocolHandler.cs
│   ├── ReverseProxy.cs
│   ├── SocksUpstreamProxy.cs
│   ├── StatsHandler.cs
│   └── TransparentProxy.cs
├── Security/BasicAuth.cs
└── Program.cs
```

---

## 开发与验证约定

1. 参考 C 实现时，统一使用路径: `~/Repos/tinyproxy-org/tinyproxy/src`
2. 任何功能变更至少执行:
   - `dotnet build src/TinyProxy/TinyProxy.csproj`
   - `dotnet test tests/TinyProxy.Tests/TinyProxy.Tests.csproj`
3. 性能相关改动额外执行:
   - `dotnet run -c Release --project benchmarks/TinyProxy.Benchmarks/TinyProxy.Benchmarks.csproj`

---

## 参考资源

- [tinyproxy GitHub](https://github.com/tinyproxy/tinyproxy)
- [tinyproxy C 源码](~/Repos/tinyproxy-org/tinyproxy/src)
- [System.IO.Pipelines 文档](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)
- [.NET 性能文档](https://learn.microsoft.com/en-us/dotnet/core/performance/)
