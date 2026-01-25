# TinyProxy.NET AGENTS

## 项目概述

使用 .NET 10 完整重写 [tinyproxy](https://github.com/tinyproxy/tinyproxy)，功能 1:1 复刻，采用最新 .NET 技术栈实现高性能、低内存、高吞吐的 HTTP/HTTPS 代理服务器。

**C 源码位置**: `../../tinyproxy/src/`

---

## 核心目标

- **功能对等**: 完全复刻 tinyproxy C 版本的所有核心功能
- **高性能**: 零拷贝、异步 I/O、最小化分配
- **低内存**: 使用池化、Span、结构体优化
- **现代架构**: 遵循 .NET 最佳实践，可测试、可维护

---

## C 源码模块映射

| C 文件 | 行数 | 功能 | .NET 映射 |
|--------|------|------|-----------|
| `main.c` | 426 | 入口、守护进程、信号处理 | `Program.cs` |
| `child.c` | 307 | 子进程池管理 | `Core/ProcessManager.cs` |
| `reqs.c` | 1760 | HTTP 请求处理（核心） | `Protocol/Http/RequestHandler.cs` |
| `conf.c` | 1135 | 配置文件解析 | `Config/Configuration.cs` |
| `buffer.c` | 313 | 缓冲区管理 | `Core/BufferPool.cs` |
| `network.c` | 319 | 网络操作 | `Core/Network.cs` |
| `sock.c` | 396 | Socket 操作 | `Core/SocketExtensions.cs` |
| `http-message.c` | 265 | HTTP 消息解析 | `Protocol/Http/HttpMessage.cs` |
| `acl.c` | 290 | 访问控制列表 | `Filter/AccessControl.cs` |
| `upstream.c` | 234 | 上游代理 | `Protocol/UpstreamProxy.cs` |
| `log.c` | 306 | 日志系统 | `Logging/Logger.cs` |
| `transparent-proxy.c` | - | 透明代理 | `Protocol/TransparentProxy.cs` |
| `reverse-proxy.c` | - | 反向代理 | `Protocol/ReverseProxy.cs` |
| `connect-ports.c` | - | CONNECT 端口控制 | `Filter/ConnectFilter.cs` |
| `filter.c` | - | URL 过滤 | `Filter/UrlFilter.cs` |
| `basicauth.c` | - | 基本认证 | `Security/BasicAuth.cs` |
| `html-error.c` | 315 | HTML 错误页面 | `Protocol/HtmlErrorPages.cs` |
| `loop.c` | - | 事件循环 | `Core/EventLoop.cs` |
| `conns.c` | - | 连接管理 | `Core/ConnectionManager.cs` |
| `stats.c` | - | 统计信息 | `Metrics/Stats.cs` |

---

## 核心功能清单

### 1. 代理协议
- [ ] HTTP/1.0 和 HTTP/1.1 代理 (`reqs.c`)
- [ ] HTTPS CONNECT 隧道 (`reqs.c`)
- [ ] 透明代理模式 (`transparent-proxy.c`)
- [ ] 反向代理模式 (`reverse-proxy.c`)
- [ ] Upstream Proxy 上游代理 (`upstream.c`)

### 2. 访问控制
- [ ] IP 白名单/黑名单 (`acl.c`)
- [ ] 基于正则的 URL 过滤 (`filter.c`)
- [ ] CONNECT 端口限制 (`connect-ports.c`)
- [ ] Via 头控制
- [ ] X-Tinyproxy 头注入

### 3. 配置管理 (`conf.c`)
- [ ] 配置文件解析 (类似 tinyproxy.conf)
- [ ] 命令行参数
- [ ] 运行时配置重载 (SIGHUP)

### 4. 日志与监控 (`log.c`, `stats.c`)
- [ ] 访问日志 (Access Log)
- [ ] 错误日志 (Error Log)
- [ ] 日志级别控制 (Critical/Error/Warning/Notice/Info/Connect)
- [ ] Syslog 支持
- [ ] 统计信息

### 5. 性能特性
- [ ] 连接池管理 (`child.c`, `conns.c`)
- [ ] 超时控制 (Connect/Request/Idle Timeout)
- [ ] 最大连接数限制
- [ ] Keep-Alive 支持

### 6. 安全特性
- [ ] 基本认证 (`basicauth.c`)
- [ ] 请求/响应过滤
- [ ] HTML 错误页面 (`html-error.c`)

---

## .NET 10 技术选型

### 高性能 I/O
| 技术 | 用途 | 对应 C 模块 |
|------|------|-------------|
| `System.IO.Pipelines` | 流处理、零拷贝缓冲 | `buffer.c` |
| `System.Net.Sockets` | 底层 Socket 操作 | `sock.c`, `network.c` |
| `MemoryPool<T>` / `ArrayPool<T>` | 缓冲区池化 | `buffer.c` |
| `ValueTask` | 减少异步分配 | 全局 |
| `SocketAsyncEventArgs` | 高性能 Socket | `sock.c` |

### 数据结构优化
| 技术 | 用途 |
|------|------|
| `Span<T>` / `Memory<T>` | 零拷贝切片 |
| `ReadOnlySequence<T>` | 管道数据遍历 |
| `struct` | 减少堆分配 |
| `ref struct` | 栈上缓冲区 |

### 并发模型
| 技术 | 用途 | 对应 C 模块 |
|------|------|-------------|
| `ThreadPool` | I/O 完成端口 | `loop.c`, `child.c` |
| `Channel<T>` | 无锁队列 | `conns.c` |
| `Interlocked` | 原子操作 | `stats.c` |
| `SemaphoreSlim` | 并发限流 | `child.c` |

---

## 架构设计原则

### 1. 零拷贝优先
```csharp
// ✅ 使用 Span 零拷贝解析
bool TryParseRequestLine(ReadOnlySpan<byte> buffer, out HttpMethod method, out Uri uri);

// ❌ 避免: 字符串分配
string ParseRequestLine(string buffer);
```

### 2. 异步全程
```csharp
// ✅ 所有 I/O 操作必须异步
await _socket.SendAsync(buffer, ct);
await _stream.ReadAsync(memory, ct);
```

### 3. 池化一切
```csharp
// ✅ 使用 ArrayPool 复用缓冲区
using var owner = MemoryPool<byte>.Shared.Rent(bufferSize);
```

### 4. 超时控制
```csharp
// ✅ 所有阻塞操作带超时
await _socket.SendAsync(buffer, SocketFlags.None, ct).AsTask().WaitAsync(timeout);
```

---

## 目录结构规划

```
src/TinyProxy/
├── Core/
│   ├── Connection.cs          # 连接管理 (conns.c)
│   ├── ConnectionManager.cs   # 连接池 (child.c)
│   ├── BufferPool.cs          # 缓冲区池 (buffer.c)
│   ├── EventLoop.cs           # 事件循环 (loop.c)
│   ├── Network.cs             # 网络操作 (network.c)
│   └── SocketExtensions.cs    # Socket 扩展 (sock.c)
├── Protocol/
│   ├── Http/
│   │   ├── HttpRequestParser.cs    # HTTP 请求解析 (http-message.c)
│   │   ├── HttpResponseWriter.cs   # HTTP 响应写入
│   │   ├── HttpMessage.cs          # HTTP 消息模型
│   │   └── RequestHandler.cs       # 请求处理主逻辑 (reqs.c)
│   ├── ConnectHandler.cs           # HTTPS CONNECT (reqs.c)
│   ├── TransparentProxy.cs         # 透明代理 (transparent-proxy.c)
│   ├── ReverseProxy.cs             # 反向代理 (reverse-proxy.c)
│   ├── UpstreamProxy.cs            # 上游代理 (upstream.c)
│   └── HtmlErrorPages.cs           # 错误页面 (html-error.c)
├── Config/
│   ├── Configuration.cs      # 配置模型 (conf.c)
│   ├── ConfigParser.cs       # 配置解析
│   └── ConfigValidator.cs    # 配置验证
├── Filter/
│   ├── AccessControl.cs      # IP 白/黑名单 (acl.c)
│   ├── UrlFilter.cs          # URL 过滤 (filter.c)
│   ├── ConnectFilter.cs      # CONNECT 端口控制 (connect-ports.c)
│   └── HeaderFilter.cs       # 头部处理
├── Logging/
│   ├── Logger.cs             # 日志抽象 (log.c)
│   ├── AccessLogger.cs       # 访问日志
│   ├── ErrorLogger.cs        # 错误日志
│   └── SyslogLogger.cs       # Syslog 支持
├── Security/
│   ├── BasicAuth.cs          # 基本认证 (basicauth.c)
│   └── AuthenticationProvider.cs  # 认证抽象
├── Metrics/
│   ├── Stats.cs              # 统计信息 (stats.c)
│   └── MetricsCollector.cs   # 指标收集
└── Program.cs                # 入口点 (main.c)
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

### 测试要求
- 单元测试覆盖率 > 80%
- 基准测试 (BenchmarkDotNet) 覆盖关键路径
- 压力测试验证并发场景

---

## 工作流程

当 AI Agent 接到任务时：

1. **分析任务类型**
   - 新功能 → 使用 `develop-feature` 技能
   - Bug 修复 → 使用 `fix-bug` 技能
   - 性能优化 → 使用 `refactor` 技能

2. **查看 C 源码参考**
   - 在 `../../tinyproxy/src/` 找到对应模块
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

## 技术债务

- [ ] 升级 TargetFramework 到 net10.0 (当前 net9.0)
- [ ] 添加 BenchmarkDotNet 基准测试项目
- [ ] 添加压力测试工具集成

---

## 参考资源

- [tinyproxy GitHub](https://github.com/tinyproxy/tinyproxy)
- [tinyproxy C 源码](../../tinyproxy/src/)
- [System.IO.Pipelines 文档](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)
- [.NET Performance Tips](https://learn.microsoft.com/en-us/dotnet/core/performance/)
