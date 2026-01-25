# TinyProxy.NET 设计文档

**日期**: 2026-01-25
**状态**: 设计完成，待实现

---

## 1. 概述

TinyProxy.NET 是使用 .NET 10 完整重写 [tinyproxy](https://github.com/tinyproxy/tinyproxy) 的 HTTP/HTTPS 代理服务器，功能 1:1 复刻，采用现代 .NET 技术栈实现高性能、低内存、高吞吐。

### 核心目标

- **功能对等**: 完全复刻 tinyproxy C 版本的所有核心功能
- **高性能**: 零拷贝、异步 I/O、最小化分配
- **低内存**: 使用池化、Span、结构体优化
- **现代架构**: 遵循 .NET 最佳实践，可测试、可维护

---

## 2. 技术选型

| 技术 | 用途 |
|------|------|
| `System.IO.Pipelines` | 流处理、零拷贝缓冲 |
| `System.Net.Sockets` | 底层 Socket 操作 |
| `MemoryPool<T>` / `ArrayPool<T>` | 缓冲区池化 |
| `ValueTask` | 减少异步分配 |
| `ThreadPool` 异步直连 | 并发模型 |

---

## 3. 整体架构

系统采用**异步流式处理架构**，核心是 `System.IO.Pipelines` 驱动的请求处理管线。

### 主流程

1. `Program.Main` 启动服务，加载配置
2. `Core.EventLoop` 监听端口，接受连接
3. 每个 Socket 连接创建一个 `Connection` 实例，异步处理
4. `Protocol.Http.RequestHandler` 解析 HTTP 请求
5. 根据 HTTP 方法分发：
   - 普通 GET/POST 等 → `HttpForwarder` 转发请求到目标服务器
   - CONNECT 方法 → `ConnectHandler` 建立 HTTPS 隧道
6. 响应数据通过 Pipeline 回写给客户端
7. 请求/响应分别记录到 `Logging.AccessLogger`

### 关键设计点

- 使用 `MemoryPool<byte>.Shared` 池化缓冲区
- 使用 `ValueTask` 减少异步分配
- 所有 I/O 带 `CancellationToken` 和超时控制

---

## 4. 目录结构

```
src/TinyProxy/
├── Core/
│   ├── Connection.cs          # 单个连接的生命周期管理
│   ├── ConnectionManager.cs   # 连接池和并发限制
│   ├── BufferPool.cs          # ArrayPool 包装
│   ├── EventLoop.cs           # Socket 监听循环
│   └── SocketExtensions.cs    # Socket 扩展方法
├── Protocol/
│   ├── Http/
│   │   ├── HttpRequestParser.cs    # 解析请求行和头部
│   │   ├── HttpMessage.cs          # 请求/响应模型
│   │   ├── HttpForwarder.cs        # HTTP 请求转发
│   │   └── ResponseHandler.cs      # 响应处理
│   ├── ConnectHandler.cs           # HTTPS CONNECT 隧道
│   ├── UpstreamProxy.cs            # 上游代理支持
│   └── HtmlErrorPages.cs           # 错误页面生成
├── Config/
│   ├── Configuration.cs      # 配置模型
│   └── ConfigParser.cs       # tinyproxy.conf 格式解析
├── Filter/
│   ├── AccessControl.cs      # IP 白/黑名单
│   ├── UrlFilter.cs          # URL 正则过滤
│   ├── ConnectFilter.cs      # CONNECT 端口限制
│   └── HeaderFilter.cs       # 头部修改/过滤
├── Logging/
│   ├── Logger.cs             # 日志抽象
│   └── AccessLogger.cs       # 访问日志
├── Security/
│   └── BasicAuth.cs          # HTTP 基本认证
└── Metrics/
    └── Stats.cs              # 统计信息
```

---

## 5. HTTP 请求处理流程

### 处理管线

1. **接收阶段** (`Connection.ProcessAsync`)
   - Socket 连接后创建 `Pipe`
   - `PipeReader` 读取客户端数据
   - 循环调用 `HttpRequestParser.TryParseRequest`

2. **解析阶段** (`HttpRequestParser`)
   - 零拷贝解析请求行：`METHOD SP URI SP HTTP/1.x CRLF`
   - 解析头部到 `Dictionary<HeaderName, ReadOnlySequence<byte>>`
   - 提取：Host、Via、X-Forwarded-For 等

3. **验证阶段** (多层过滤)
   - `AccessControl.CheckClientIP()` - IP 白/黑名单
   - `BasicAuth.Validate()` - 基本认证（如配置）
   - `UrlFilter.CheckAllowed()` - URL 过滤
   - `ConnectFilter.CheckPort()` - CONNECT 端口限制
   - 任何失败 → `HtmlErrorPages.SendError()`

4. **转发阶段**
   - **HTTP**: 连接目标服务器，转发修改后的请求头
   - **HTTPS CONNECT**: 返回 `200 Connection Established`，后继数据直接透传

5. **响应阶段**
   - `ResponseHandler` 读取服务器响应
   - 修改/注入头部（Via、X-Tinyproxy）
   - 通过 `PipeWriter` 回写客户端

---

## 6. HTTPS CONNECT 隧道

### 处理流程

1. **握手阶段**
   ```
   CLIENT → PROXY: CONNECT example.com:443 HTTP/1.1
                   Host: example.com
   PROXY → CLIENT: HTTP/1.1 200 Connection Established
   ```

2. **隧道建立** (`ConnectHandler`)
   - 解析 CONNECT 请求中的 `host:port`
   - `ConnectFilter.CheckPort(port)` 验证端口允许
   - 异步连接目标服务器（带超时）
   - 连接成功后发送 `200` 响应给客户端

3. **数据透传**
   - 创建双向 `Pipe`：`Client ←→ Proxy ←→ Server`
   - 两个独立的 `Task` 并行运行：
     - `ClientToServer`: 读客户端 → 写服务器
     - `ServerToClient`: 读服务器 → 写客户端
   - 任一方向断开或出错 → 清理整个连接

4. **安全考虑**
   - CONNECT 端口默认仅允许 443（可配置）
   - 透传模式不解析/修改任何数据
   - 不记录 HTTPS 请求内容（只记录 CONNECT 请求行）

---

## 7. 配置管理

### Configuration 模型

```csharp
class Configuration
{
    // 监听配置
    string ListenAddress { get; }
    ushort ListenPort { get; }

    // 限制
    int MaxClients { get; }
    TimeSpan Timeout { get; }

    // 过滤
    HashSet<string> AllowIPs { get; }
    HashSet<string> DenyIPs { get; }
    List<Regex> FilterRegexes { get; }
    HashSet<ushort> AllowedConnectPorts { get; }

    // 上游
    UpstreamProxyConfig? UpstreamProxy { get; }

    // 日志
    string? LogLevel { get; }
    string? LogFile { get; }

    // 认证
    BasicAuthConfig? BasicAuth { get; }

    // 行为
    bool AddViaHeader { get; }
    bool AddXTinyproxyHeader { get; }
}
```

### 配置文件格式

兼容 tinyproxy.conf 风格：
```
Listen 127.0.0.1:8888
MaxClients 100
Timeout 30
Allow 127.0.0.1
Filter "/path/to/filter"
FilterURL "example.com"
ConnectPort 443
```

---

## 8. 错误处理与日志

### 错误处理策略

| 场景 | 响应 |
|------|------|
| 配置错误 | 启动失败 + 详细错误信息 |
| 客户端连接断开 | 静默清理资源 |
| 目标服务器连接失败 | `502 Bad Gateway` + HTML 错误页 |
| 超时 | `504 Gateway Timeout` |
| 访问拒绝 | `403 Forbidden` |
| 认证失败 | `407 Proxy Authentication Required` |
| 无效请求 | `400 Bad Request` |

### 日志系统

**AccessLogger** 格式（类似 Apache/Nginx）：
```
127.0.0.1 - - [25/Jan/2026:10:30:00 +0000] "GET http://example.com/ HTTP/1.1" 200 1234
```

**LogLevel** 层级：
- Critical - 服务无法继续
- Error - 请求失败但服务继续
- Warning - 异常但已处理
- Notice - 正常但值得注意
- Info - 常规信息
- Connect - 新连接建立

---

## 9. 性能优化

### 零拷贝技术

```csharp
// Span 零拷贝解析
bool TryParseRequestLine(ReadOnlySpan<byte> buffer,
    out HttpMethod method, out string host, out int port);

// Pipelines 自动缓冲管理
var reader = _pipe.Reader;
ReadResult result = await reader.ReadAsync(cancellationToken);
ReadOnlySequence<byte> buffer = result.Buffer;

// ArrayPool 复用
var buffer = ArrayPool<byte>.Shared.Rent(8192);
// ... use ...
ArrayPool<byte>.Shared.Return(buffer);
```

### 连接池管理

- `SemaphoreSlim` 限制最大并发连接数
- 活跃连接字典 `ConcurrentDictionary<int, Connection>`
- 定期清理僵尸连接

### 性能目标

| 指标 | 目标 |
|------|------|
| 单连接吞吐量 | > 1GB/s (本地回环) |
| 内存占用 | < 50MB (空闲，1000 连接池) |
| CPU 占用 | < 10% (单核心，1000 并发) |

---

## 10. 实现计划

### Phase 1 - 核心 HTTP 代理
- `Core.EventLoop`, `Core.Connection`
- `Protocol.Http.HttpRequestParser`, `HttpForwarder`
- `Config.Configuration`
- 基本错误响应

### Phase 2 - HTTPS CONNECT
- `Protocol.ConnectHandler`
- `Filter.ConnectFilter`
- 双向数据透传

### Phase 3 - 过滤与认证
- `Filter.AccessControl`, `UrlFilter`
- `Security.BasicAuth`
- `Filter.HeaderFilter`

### Phase 4 - 日志与统计
- `Logging.AccessLogger`
- `Metrics.Stats`

### Phase 5 - 高级特性
- `Protocol.UpstreamProxy`
- 配置热重载

---

## 11. 测试策略

- **单元测试**: xUnit，覆盖解析、过滤逻辑
- **集成测试**: 真实 HTTP/HTTPS 请求测试
- **性能测试**: BenchmarkDotNet，验证吞吐量目标
- **压力测试**: wrk/ab 工具验证并发能力
