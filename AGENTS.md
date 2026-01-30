# TinyProxy.NET AGENTS

## 项目概述

使用 .NET 10 完整重写 [tinyproxy](https://github.com/tinyproxy/tinyproxy)，功能 1:1 复刻，采用最新 .NET 技术栈实现高性能、低内存、高吞吐的 HTTP/HTTPS 代理服务器。

**C 源码位置**: `~/Repos/tinyproxy/src/`

---

## 核心目标

- **功能对等**: 完全复刻 tinyproxy C 版本的所有核心功能
- **高性能**: 零拷贝、异步 I/O、最小化分配
- **低内存**: 使用池化、Span、结构体优化
- **现代架构**: 遵循 .NET 最佳实践，可测试、可维护

---

## C 源码模块映射

| C 文件 | 行数 | 功能 | .NET 映射 | 状态 |
|--------|------|------|-----------|------|
| `main.c` | 426 | 入口、守护进程、信号处理 | `Program.cs` | ✅ |
| `child.c` | 307 | 子进程池管理 | `Core/ConnectionManager.cs` | ✅ |
| `reqs.c` | 1760 | HTTP 请求处理（核心） | `Protocol/Http/HttpForwarder.cs` | ✅ |
| `conf.c` | 1135 | 配置文件解析 | `Config/Configuration.cs` | ✅ |
| `buffer.c` | 313 | 缓冲区管理 | `Core/ObjectPool.cs` | ✅ |
| `sock.c` | 396 | Socket 操作 | `Core/SocketExtensions.cs` | ✅ |
| `http-message.c` | 265 | HTTP 消息解析 | `Protocol/Http/HttpRequestParser.cs` | ✅ |
| `acl.c` | 290 | 访问控制列表 | `Filter/AccessControl.cs` | ✅ |
| `upstream.c` | 234 | 上游代理 | `Protocol/UpstreamProxy.cs` | ✅ |
| `log.c` | 306 | 日志系统 | `Logging/ConsoleLogger.cs` | ✅ |
| `transparent-proxy.c` | - | 透明代理 | `Protocol/TransparentProxy.cs` | ✅ |
| `reverse-proxy.c` | - | 反向代理 | `Protocol/ReverseProxy.cs` | ✅ |
| `connect-ports.c` | - | CONNECT 端口控制 | `Filter/ConnectFilter.cs` | ✅ |
| `filter.c` | - | URL 过滤 | `Filter/UrlFilter.cs` | ✅ |
| `basicauth.c` | - | 基本认证 | `Security/BasicAuth.cs` | ✅ |
| `html-error.c` | 315 | HTML 错误页面 | `Protocol/HtmlErrorPages.cs` | ✅ |
| `loop.c` | - | 事件循环 | `Core/EventLoop.cs` | ✅ |
| `conns.c` | - | 连接管理 | `Core/Connection.cs` | ✅ |
| `stats.c` | - | 统计信息 | `Metrics/Stats.cs` | ✅ |
| `anonymous.c` | - | 匿名模式 | `Filter/AnonymousFilter.cs` | ✅ |
| `socks.c` | - | SOCKS 支持 | `Protocol/SocksUpstreamProxy.cs` | ✅ |

---

## 核心功能清单

### 1. 代理协议
- [x] HTTP/1.0 和 HTTP/1.1 代理 (`reqs.c`)
- [x] HTTPS CONNECT 隧道 (`reqs.c`)
- [x] 透明代理模式 (`transparent-proxy.c`)
- [x] 反向代理模式 (`reverse-proxy.c`)
- [x] Upstream Proxy 上游代理 (`upstream.c`)
- [x] SOCKS4/SOCKS5 上游代理

### 2. 访问控制
- [x] IP 白名单/黑名单 (`acl.c`)
- [x] 基于正则的 URL 过滤 (`filter.c`)
- [x] CONNECT 端口限制 (`connect-ports.c`)
- [x] Via 头控制
- [x] X-Tinyproxy 头注入
- [x] 匿名模式 (header 过滤)

### 3. 配置管理 (`conf.c`)
- [x] 配置文件解析 (类似 tinyproxy.conf)
- [x] 命令行参数 (-c 指定配置文件)
- [x] 运行时配置重载 (文件监控，跨平台 SIGHUP 替代)

### 4. 日志与监控 (`log.c`, `stats.c`)
- [x] 访问日志 (Access Log)
- [x] 错误日志 (Error Log)
- [x] 日志级别控制 (Critical/Error/Warning/Notice/Info/Connect)
- [x] Syslog 支持 (RFC 5424)
- [x] 统计信息
- [x] 统计页面 (StatHost)

### 5. 性能特性
- [x] 连接池管理 (`child.c`, `conns.c`)
- [x] 超时控制 (Connect/Request/Idle Timeout)
- [x] 最大连接数限制
- [x] BindSame (绑定出站到入站 IP)

### 6. 运维特性
- [x] PID 文件管理
- [x] 优雅关闭

---

## 目录结构

```
src/TinyProxy/
├── Core/
│   ├── Connection.cs              # 连接管理 (conns.c)
│   ├── ConnectionManager.cs       # 连接池 (child.c)
│   ├── ObjectPool.cs              # 对象池化 (buffer.c)
│   ├── EventLoop.cs               # 事件循环 (loop.c)
│   ├── SocketExtensions.cs        # Socket 扩展 (sock.c)
│   ├── ConfigReloader.cs          # 配置热重载
│   ├── PidFileManager.cs          # PID 文件管理
│   └── ILogger.cs                 # 日志接口
├── Protocol/
│   ├── Http/
│   │   ├── HttpRequestParser.cs   # HTTP 请求解析 (http-message.c)
│   │   ├── HttpRequest.cs         # HTTP 请求模型
│   │   ├── HttpMethod.cs          # HTTP 方法枚举
│   │   ├── HttpForwarder.cs      # HTTP 请求转发 (reqs.c)
│   │   └── ResponseHandler.cs     # 响应处理
│   ├── ConnectHandler.cs          # HTTPS CONNECT (reqs.c)
│   ├── TransparentProxy.cs        # 透明代理 (transparent-proxy.c)
│   ├── ReverseProxy.cs            # 反向代理 (reverse-proxy.c)
│   ├── SocksUpstreamProxy.cs      # SOCKS4/5 上游代理
│   ├── UpstreamProxy.cs           # 上游代理 (upstream.c)
│   ├── StatsHandler.cs            # 统计页面 (stats.c)
│   └── HtmlErrorPages.cs          # 错误页面 (html-error.c)
├── Config/
│   ├── Configuration.cs           # 配置模型 (conf.c)
│   └── ConfigParser.cs            # 配置解析
├── Filter/
│   ├── AccessControl.cs           # IP 白/黑名单 (acl.c)
│   ├── UrlFilter.cs               # URL 过滤 (filter.c)
│   ├── ConnectFilter.cs           # CONNECT 端口控制 (connect-ports.c)
│   ├── HeaderFilter.cs            # 头部处理
│   └── AnonymousFilter.cs         # 匿名模式
├── Logging/
│   ├── ILogger.cs                 # 日志接口
│   ├── ConsoleLogger.cs           # 控制台日志
│   ├── AccessLogger.cs            # 访问日志
│   └── SyslogLogger.cs            # Syslog 支持
├── Security/
│   └── BasicAuth.cs               # 基本认证 (basicauth.c)
├── Metrics/
│   └── Stats.cs                   # 统计信息 (stats.c)
└── Program.cs                     # 入口点 (main.c)
```

---

## .NET 10 技术选型

### 高性能 I/O
| 技术 | 用途 | 对应 C 模块 |
|------|------|-------------|
| `System.IO.Pipelines` | 流处理、零拷贝缓冲 | `buffer.c` |
| `System.Net.Sockets` | 底层 Socket 操作 | `sock.c` |
| `ArrayPool<byte>` | 缓冲区池化 | `buffer.c` |
| `ValueTask` | 减少异步分配 | 全局 |

### 数据结构优化
| 技术 | 用途 |
|------|------|
| `Span<T>` / `Memory<T>` | 零拷贝切片 |
| `ReadOnlySequence<byte>` | 管道数据遍历 |
| `struct` | 减少堆分配 |

### 并发模型
| 技术 | 用途 | 对应 C 模块 |
|------|------|-------------|
| `ThreadPool` | 异步 I/O | `loop.c`, `child.c` |
| `Interlocked` | 原子操作 | `stats.c` |
| `SemaphoreSlim` | 并发限流 | `child.c` |

---

## 架构设计原则

### 1. 零拷贝优先
```csharp
// ✅ 使用 Span 零拷贝解析
bool TryParseRequestLine(ReadOnlySpan<byte> buffer, out HttpMethod method, out Uri uri);
```

### 2. 异步全程
```csharp
// ✅ 所有 I/O 操作必须异步
await _socket.SendAsync(buffer, SocketFlags.None, ct);
await _stream.ReadAsync(memory, ct);
```

### 3. 池化一切
```csharp
// ✅ 使用 ArrayPool 复用缓冲区
using var owner = MemoryPool<byte>.Shared.Rent(bufferSize);
```

---

## 开发规范

### 代码风格
- 遵循 .NET 编码约定
- 启用 Nullable 引用类型
- 使用文件局部命名空间 (`file-scoped`)
- 使用顶级语句 (Top-level statements)

### 性能要求
- 单连接吞吐量 > 1GB/s (本地回环)
- 内存占用 < 50MB (空闲状态，1000 连接池)
- CPU 占用 < 10% (单核心，1000 并发)

---

## 工作流程

当 AI Agent 接到任务时：

1. **分析任务类型**
   - 新功能 → 使用 `develop-feature` 技能
   - Bug 修复 → 使用 `fix-bug` 技能
   - 性能优化 → 使用 `refactor` 技能

2. **查看 C 源码参考**
   - 在 `~/Repos/tinyproxy/src/` 找到对应模块
   - 理解原始实现逻辑
   - 确保功能对等

3. **应用 .NET 最佳实践**
   - 优先选择零拷贝 API
   - 使用 Pipelines 处理流
   - 池化所有可复用资源

4. **验证性能**
   - 使用 BenchmarkDotNet 测试关键路径
   - 使用内存分析器检查分配

---

## 参考资源

- [tinyproxy GitHub](https://github.com/tinyproxy/tinyproxy)
- [tinyproxy C 源码](~/Repos/tinyproxy/src/)
- [System.IO.Pipelines 文档](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)
- [.NET Performance Tips](https://learn.microsoft.com/en-us/dotnet/core/performance/)

