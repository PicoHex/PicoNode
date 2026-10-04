# PicoNode Rate Limit Policy — 分层限流组件设计

> 状态：spec review 已处理（B1–B5 阻塞项与次要项已修入本文）；实现已启动——Task 1（对端地址链路）落地于 `0838e11`
> 日期：2026-10-02（rev5：修正 spec↔实现偏差与自审缺陷——`Not` 示例陷阱、`TokenMatch` 分配/null 语义、赋值点行号、字面路径匹配、桶增长边界；rev4 = b449863）
> 修订：扩展 `docs/superpowers/specs/2026-06-22-ratelimit-middleware-design.md`（令牌桶/store/头/边界仍是基线；本文新增**策略、分类、组合、声明**层，并纠正两处 spec↔实现偏差）
> 原则：PicoHex 零外部依赖、AOT-first、**机制在框架 / 策略在应用**

---

## 1. Problem

### 1.1 现场（2026-10-02 实测，PicoAgent :8080 部署）

> **测量基线**：以下数据来自 **`a94103c8` 之前**的部署。该提交已把 tokenless 预算临时调为 300/30（见 §5 过渡），所以现在复测 100 并发会得到 `100×200`——要复现 §1.1 需回到旧配置或临时把预算改回 60/1。

删除 workspace 时前端日志出现连续的

```
ERROR [web] Unhandled exception: GET /fragments/workspaces/{id}/row?confirm=1
EXCEPTION: PicoAgent.Client.AcpClientException: HTTP 429 (rate-limited) (code 429)
```

| 测量 | 结果 |
|---|---|
| daemon 直连并发 100 | **60×200 + 40×429**，`Retry-After: 1`、`X-RateLimit-Limit: 60` |
| 前端并发 100 次 `/row` | **10×200 + 90×500**（429 冒成未处理异常） |
| 前端串行 80 次 | 80×200，但耗时 **68.5s**（桶空后靠 1 token/s 回填 + 客户端重试） |
| 一次删除的 daemon 调用 | **≈8 次**：row 1 + delete 1 + list/tags（列表渲染）2 + list/tags（`#recent-section` OOB）2 + sidebar 刷新 2 |

根因链：

1. 实例级全局令牌桶 **60 burst / 1 per second**（W-04）保护的是"打 PBKDF2 的匿名洪水"；
2. 但该部署 **没有配置 token**（`IsAuthorized` 在 `options.Token` 为空时信任所有调用者），限流器在这里不是安全边界，只是自伤保护；
3. 可信前端的正常操作（一次删除 ≈8 次调用）在持续批量操作时把桶吃干；429 又被 `DaemonHttpClient` 以最多 3 次重试放大；
4. `WorkspaceRowAsync` / `WorkspacePaneAsync` / 删除后的列表刷新**没有捕获** `AcpClientException` → 未处理 500。

### 1.2 框架能力缺口（实测）

| 缺口 | 现状 |
|---|---|
| 分类预算 | `InMemoryRateLimitStore` 一个实例一套预算；`RateLimitOptions.KeySelector` 只能选桶键，无法表达"不同类不同预算" |
| 组合/豁免 | `RateLimitMiddleware` 只能单实例；无 skip 谓词；链式会重复 `X-RateLimit-*` 头 |
| peer 地址 | 传输层有 `ITcpConnectionContext.RemoteEndPoint`（`src/Network/PicoNode.Abs/ITcpConnectionContext.cs:9`），但 `HttpRequest`/`WebContext` 都没有它 |
| 声明面/观测 | 应用只能硬编码预算；无命名策略、无"哪个桶拒的"信息、无 `OnRejected` |

### 1.3 对 2026-06-22 spec 的两处更正

| 旧 spec | 实现/现状 | 本文处理 |
|---|---|---|
| §2 架构图：`AuthMiddleware → RateLimitMiddleware` | W-04 改为 **limiter → auth**（拒绝必须早于 PBKDF2；键不可被伪造请求铸造） | 明示：限流器的位置是**应用的策略**，框架不默认先后 |
| §7.1/§8：`KeySelector` 抛异常 → 键 `"error"` | 实现是 **bypass（fail-open 放行）**（`RateLimitMiddleware.InvokeCoreAsync` 的 catch 注释） | 新 API 用 `Func<WebContext, string?>`：**null = 该档不适用**；不再用异常做控制流 |

令牌桶算法、`IRateLimitStore`、`InMemoryRateLimitStore`、响应头、单桶边界用例仍按 2026-06-22 spec 执行。

---

## 2. Goals / Non-Goals

**Goals**

1. 在 `PicoNode.Web` 内把限流补成**完整组件**（零外部依赖）：分类预算、链式组合、豁免、分类器、peer、命名策略、观测。
2. 应用以**一张策略表**声明限流；框架负责求值、头、状态、日志钩子。
3. AOT-first：无反射、热路径低分配、委托与结构体、`for` 遍历。

**Non-Goals（本次明确不做）**

- 转发头（`X-Forwarded-For`）的信任与解析 → 独立中间件（.NET 亦为独立 ForwardedHeaders）。
- 路由级 metadata / `RequireRateLimit("name")` / `Controllers.Gen` 的 `[RateLimit]` → 接口预留，见 §7。
- 新算法（SlidingWindow / Concurrency）与分布式 store → `IRateLimitStore` 保持可插拔，不在本次。

---

## 3. Design

### 3.1 数据模型（①）

```csharp
namespace PicoNode.Web;

/// <summary>一个令牌桶的参数（构造期映射到 InMemoryRateLimitStore）。</summary>
/// <remarks>回填是"按经过时间比例"的平滑回填（见 2026-06-22 spec §5.1）：
/// PerMinute(60, 60) 是"60 burst + 约 1/s"，不是"每分钟一次性补 60"。</remarks>
public readonly struct RateLimitBudget
{
    public int MaxTokens { get; }
    public int RefillRate { get; }
    public TimeSpan RefillInterval { get; }
    public static RateLimitBudget PerSecond(int burst, int perSecond);
    public static RateLimitBudget PerMinute(int burst, int perMinute);
}

/// <summary>一个预算 + 一个键选择器；Key 返回 null = 该档不适用此请求。</summary>
public sealed class RateLimitTier
{
    public string Name { get; }
    public RateLimitBudget Budget { get; }
    public Func<WebContext, string?> Key { get; }
    public Predicate<WebContext>? Applies { get; }   // 档级限定，可空
}

public sealed class RateLimitPolicy : IDisposable
{
    public string Name { get; }
    public bool FailOpen { get; }                    // 默认 false（盾语义；见 §3.2.6）
    public Predicate<WebContext>? Applies { get; }   // false = 整策略豁免
    public IReadOnlyList<RateLimitTier> Tiers { get; }   // 数组私有，构建后冻结
    public Action<RateLimitRejection>? OnRejected { get; }
    public static RateLimitPolicyBuilder Create(string name);
    public void Dispose();                           // 释放各档的 store（宿主负责，见 §3.1.1）
    internal bool IsExempt(WebContext ctx);          // Exempt 白名单（internal；不作为公共属性暴露，避免进 API 基线）
}

public enum RateLimitRejectionReason { LimitReached, StoreError }

public readonly struct RateLimitRejection
{
    public string Policy { get; }
    public string Tier { get; }
    public RateLimitRejectionReason Reason { get; }
    public RateLimitResult Result { get; }           // StoreError 时为零值
    public WebContext Context { get; }
}

public sealed class RateLimitPolicyBuilder
{
    public RateLimitPolicyBuilder Tier(string name, RateLimitBudget budget, Func<WebContext, string?> key, Predicate<WebContext>? applies = null);
    public RateLimitPolicyBuilder Exempt(string pathPrefix);            // 段边界前缀，见 §3.4
    public RateLimitPolicyBuilder Applies(Predicate<WebContext> predicate);  // 与 RateLimitPolicy.Applies 对称
    public RateLimitPolicyBuilder OnRejected(Action<RateLimitRejection> handler);
    public RateLimitPolicyBuilder FailOpen();                           // 默认 false（盾）；显式开启才 fail-open
    public RateLimitPolicy Build();                                     // 零档/重名档 → throw（重名策略由 UseRateLimit 的注册表检测，见 §3.4）
}
```

要点：

- 预算与档是**值类型/不可变对象**，委托在构造期创建一次；`Tiers` 对外 `IReadOnlyList`、内部数组，请求路径 `for` 遍历（无 LINQ、无枚举器、无每请求闭包）。
- 每档一个 `InMemoryRateLimitStore`（**现有成员签名零改动**——旧构造改为链到新重载，校验随之收敛其中、`ArgumentOutOfRangeException.ParamName` 前缀变化；一预算一 store；只新增一个构造重载）：
  `InMemoryRateLimitStore(in RateLimitBudget budget, TimeSpan? cleanupInterval = null)`，避免给 store 传假的 `KeySelector`；`CleanupInterval` 取策略级默认 5 分钟。
- **测试缝**：`RateLimitPolicyBuilder` 提供一个 **internal** `TimeProvider` 属性（测试用，非公开 API），让政策级测试注入 **可注入时钟**——store 的 `internal TimeProvider` 已有，但 store 由 `Build()` 内部创建，没有这个缝就只能做时间依赖测试。`PicoNode.Web.Tests` 对 `PicoNode.Web` 已有 IVT。**时钟必须覆盖 `GetUtcNow`**：现有 `ManualTimeProvider` 只覆盖 `GetTimestamp`/`CreateTimer`/`TimestampFrequency`（供 keep-alive 计时器），而 store 读 `GetUtcNow`（`InMemoryRateLimitStore.cs:54`）⇒ 测试须自建时钟（如 `StoreClock`）。
- **构造器可见性**：类类型（`RateLimitPolicy`/`RateLimitTier`/`RateLimitPolicyBuilder`）显式 `internal` 构造器（工厂/builder 是唯一入口）——否则隐式 public 无参构造会进入 `api/PicoNode.Web.public.txt`（现有基线里就有 `RateLimitMiddleware..ctor()` 这类条目），与 §8 直接冲突。struct（`RateLimitBudget`/`RateLimitRejection`）的 `default` 无法隐藏，改由 `Build()` 校验拒绝（§3.4）。
- **`RateLimitRejection` 只作同步消费**：它携带 `WebContext` 引用；`OnRejected` 回调不得跨请求持有该引用。

#### 3.1.1 Dispose 归属

- **宿主负责**：`UseRateLimit` 不接管所有权；宿主以 `using var policy = …`（或宿主服务的 `Dispose`）释放，与今天 daemon 的 `using var rateStore = …` 同模式。理由：`WebApp` 目前没有 dispose 语义（`WebServer.DisposeAsync` 只关节点），给 `WebApp` 加 `IDisposable` 是比本组件更大的改动。
- 释放后 `TryConsumeTokenAsync` 抛 `ObjectDisposedException`：`FailOpen=false` 时按 store 异常路径变成 429——这是"宿主忘记释放前就 shut down"的正常终态，写进 §4 测试。

### 3.2 求值语义（②）

新增 `RateLimitMiddleware.Create(RateLimitPolicy policy)` 重载；现有 `Create(store, options)` 原样保留。

```
if (!ShouldApply(policy, ctx)) return await next(ctx, ct);   // (Applies is null || Applies(ctx)) && !IsExempt(ctx)（见 §3.4）
RateLimitResult? winner = null;  RateLimitTier? winnerTier = null;
for i in policy.Tiers:
    if (tier[i].Applies is { } t && !t(ctx)) continue;
    key = tier[i].Key(ctx);            if (key is null) continue;      // 档不适用
    key = SanitizeKey(key);            // span：256 截断 + CR/LF→'_'（兼容旧行为）
    try    { result = await store[i].TryConsumeTokenAsync(key, ct); }
    catch when (policy.FailOpen) { continue; }
    catch  { NotifyRejected(policy, tier[i], RateLimitRejectionReason.StoreError, default, ctx);
             return RateLimitResponses.RejectedOnStoreError(tier[i].Budget.MaxTokens); }
    if (!result.Allowed) { NotifyRejected(policy, tier[i], RateLimitRejectionReason.LimitReached, result, ctx);
                           return RateLimitResponses.Rejected(result); }
    if (winner is null) { winner = result; winnerTier = tier[i]; }     // 先声明者胜（见 §3.2.4）
if (winner is not null) ctx.Items[WebContextKeys.RateLimitState] = new RateLimitState
{
    Limit = winner.Value.Limit, Remaining = winner.Value.Remaining,
    NextAvailableAt = winner.Value.NextAvailableAt,
    Policy = policy.Name, Tier = winnerTier!.Name,
};
var response = await next(ctx, ct);
if (winner is not null) RateLimitResponses.AddHeaders(response, winner.Value);  // 组级判空（见 §3.2.5）
return response;
```

`OnRejected` 的调用留在策略循环里（`NotifyRejected`）；`RateLimitResponses` 只负责构造响应（参数只取旧路径也有的最小集），不反向调用策略回调。

1. **链式**：所有匹配档都要放行；任一拒绝即返回，后续档不再消耗。
2. **档的互斥靠键表达，不靠假设**：需要"非此即彼"时用 `RateLimitKeys.Not(...)`（§3.3）。**默认要求**：声明互斥档时，后一档的键必须显式排除前一档的命中集——`Constant("instance")` 永不返回 null，单独使用会让**每个请求同时命中所有档**（B1 修复点；§4 有专门测试）。
3. **令牌不退**：先放行、后被更后面的档拒绝时，前面已消耗的令牌**不退还**（令牌桶无 refund；.NET `CreateChained` 对令牌桶同理）。含 `FailOpen` 跳档（`catch when` → `continue`）时同样成立：**被跳过的档不退前面已耗的令牌**——两条都要契约测试。
4. **头与 state**：**先声明者胜**（第一个匹配且放行的档），拒绝取拒绝档。**上报的档不保证是 binding 档**（不同档的 Limit 不可比，取"Remaining 最小"没有意义）。**声明顺序统一为"最紧/最细在前"**：这样上报的头指向常见情况下的 binding 档，且细档拒绝时不会先浪费粗档令牌（见 §6）。头名与 2026-06-22 spec 完全一致。`Items` 只在放行路径写。
5. **头写入规则**：`HttpHeaderCollection` 的**写入**只有追加 `Add`，判空用 `TryGetValue`（**无 `Remove`、无覆盖写**，`src/Http/PicoNode.Http/HttpHeaderCollection.cs`）。
   - `AddHeaders` 做**组级判空**：响应已存在任一 `X-RateLimit-*` → 三个头**整体跳过**。这一条同时解决三件事：内层策略的 429 自带 `X-RateLimit-Limit` → 外层不会补上自己的 `Remaining`/`Reset`（不串档）；下游应用自己写的限流头不被追加重复项；混装旧中间件时"首写者赢"。
   - **不另设"429 不加头"规则**：本策略自己的 429 在 `next` 之前返回、根本不经过 `AddHeaders`；内层/下游的 429 由上面的组级判空拦住。少一条特例 = 少一条行为差异。
   - 多条**策略**可以并存（注册顺序 = 外层到内层）；旧单桶 `RateLimitMiddleware` 改为调用同一 helper 后**也遵守组级判空**，所以混装时"首写者赢"成立（§4 有用例）。但混装仍有真实差异，文档写明：旧中间件**不分档**（`RateLimitState` 无 `Policy`/`Tier`，且**每次放行都写** state——`RateLimitMiddleware.cs:110-116`；策略只在**有档命中**时写，未命中则完全不写）、`KeySelector` 抛异常走 **bypass**（`:53-59`）、`FailOpen` 默认 **true**（`RateLimitOptions.cs:16`）。注：`WebMiddleware` 是裸委托（`WebMiddleware.cs:3`）、`WebApp._middlewares` 只是 `List<WebMiddleware>`（`WebApp.cs:7`），**无法在启动期识别"链上已有旧中间件"**，因此这一条只能是文档纪律（评审建议的启动期 throw 在现有管线下不可实现；进程级标记方案是跨 WebApp/跨测试的假阳性制造机）。
6. **`FailOpen` 默认 false**（与旧单桶中间件的默认 true 刻意不同）：策略是盾，失败即拒绝；应用可显式开启。store 异常路径复用旧行为（`Retry-After: 60` + `X-RateLimit-Limit`），并触发 `OnRejected(StoreError)`。
7. **`OnRejected` 触发面**：`LimitReached` 与 `StoreError` 两条 429 路径**都调用且各恰好一次**；放行/豁免/直通路径不调用。`RateLimitRejection.Reason` 区分两种 429。
8. **共用 helper**：抽出 `RateLimitResponses.Rejected(RateLimitResult) / RejectedOnStoreError(int limit) / AddHeaders(HttpResponse, RateLimitResult)`（internal，参数只取旧路径也有的最小集），旧中间件改为调用它——429 构造与头行为单一来源。**旧路径的唯一行为变化**：下游已写过 `X-RateLimit-*` 时不再追加重复头（组级判空）；其余（含 store 异常路径的 `Retry-After: 60` + `X-RateLimit-Limit: options.MaxTokens`、拒绝路径的 `result.Limit`）完全一致。`Retry-After` 的时间源**保持 `DateTimeOffset.UtcNow`**（旧实现 `RateLimitMiddleware.cs:89-90` 的 `Math.Max(result.NextAvailableAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1)`），不要顺手换成 store 的 `TimeProvider`，否则"唯一行为变化"不成立。`OnRejected` 的调用留在策略循环里（helper 不反向调用策略回调）。
9. **`RateLimitResult` 改为 `readonly record struct`**（纯 DTO、无身份语义）：消除每档求值的小分配。该类型在 **`PicoNode.Web`**（`PackageId=PicoNode.Web`；`PicoWeb` 只是依赖者），破坏性变更记在 `PicoNode.Web` 名下，并按 §5 刷新公共 API 基线。`RateLimitState` 保持 class（`Items` 为 `IDictionary<string, object?>`）。

### 3.3 分类器与 peer（③）

**peer 通路（P2）**

| 位置 | 改动 |
|---|---|
| `HttpRequest`（`src/Http/PicoNode.Http/HttpRequest.cs`） | 新增 `public IPEndPoint? RemoteEndPoint { get; internal set; }`（与 `RemoteCloseToken` 同款 `internal set` 先例，`HttpRequest.cs:18`） |
| `WebContext` | 新增计算属性 `RemoteEndPoint => Request.RemoteEndPoint`、`RemoteAddress => Request.RemoteEndPoint?.Address`（零分配） |
| 赋值点（3 处，`RemoteEndPoint` 赋值行——紧接 `RemoteCloseToken` 赋值行） | `src/Http/PicoNode.Http/Internal/ConnectionRuntime/Http1ConnectionProcessor.cs:154`、`src/Http/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.cs:372`、`src/Http/PicoNode.Http/Internal/ConnectionRuntime/Http2StreamHandler.Frames.cs:363`，统一 `request.RemoteEndPoint = connection.RemoteEndPoint as IPEndPoint;`（接口静态类型是抽象 `EndPoint`；HTTP 走 TCP，cast 必成功。行号为 `0838e11` 落地后的实际行号，插入前分别少 1 行） |
| **测试可见性（B3，写死）** | `src/Http/PicoNode.Http/PicoNode.Http.csproj` 增加 `InternalsVisibleTo Include="PicoNode.Web.Tests"`——分类器测试在 `PicoNode.Web.Tests`，而 `internal set` 只对 `PicoNode.Http.Tests` 开放；不采用 `init`-only（那要改 `HttpRequestParser`/`HttpBodyParser` 的构造签名，成本更大） |

默认 null，现有测试构造 `HttpRequest` 不受影响。**边界**：反代场景 peer 是代理地址（转发头为独立后续项）。

**分类器（`RateLimitKeys`）**

| 分类器 | 返回 | 分配 | 说明 |
|---|---|---|---|
| `Constant(key)` | 固定键 | 0 | 构造期预计算 |
| `TokenMatch(string? expected, key = "trusted")` | 匹配 → `key`；否则 `null`；`expected` 为 `null`/空 → **恒 `null`**（恒不匹配，不抛异常） | 带 `Authorization` 头时每请求 2~3 小串外加 `Split(' ', 2)` 的结果数组（`"Bearer"`、余下串；逗号后缀再多一个切片——**与现状一致、非新增分配**）；无头则 0；比较本身不分配 | 复用 `AuthMiddleware` 的 Bearer 解析（抽 `internal TryGetBearerToken`，与认证同源）；**常量时间逐字符比较**（长度不等→不等；相等时 XOR 累积）。`expected=null` 恒不匹配，对应 daemon 自身持有的 `options.Token` 的"null/空 = 无 token 模式"（§3.4 示例、§5.4）——参数是 `string?`，tokenless 配置直接传 `options.Token` 不触发可空警告 |
| `Not(inner, fallback)` | `inner` 返回非 null → `null`；否则 `fallback` | 0 | **互斥组合器**（B1）：`Not(TokenMatch(token), "instance")` = "非 token 请求"档。语义是"inner 不命中"，**不是**"值不在名单里"。**只在 `inner` 可能返回 null 时才有意义**（`TokenMatch`；`Header(name)` 默认 `fallback=null` 也可）：`RemoteAddress`/`Identity`/`Path`/`Constant` **永不返回 null**（前两者有非 null fallback，后两者恒有值）⇒ 包进 `Not` 里**恒为 `null`**、该档永不适用、等于不限流。要表达"没有 peer"或"未认证"须用谓词：`tier.Applies(ctx => ctx.RemoteAddress is null)` / `tier.Applies(ctx => AuthMiddleware.GetIdentity(ctx) is null)` |
| `RemoteAddress(fallback = "unknown")` | `ip?.ToString() ?? fallback`，其中 `ip = ctx.RemoteAddress` 且 **IPv4-mapped IPv6 先归一化**（`IsIPv4MappedToIPv6 → MapToIPv4`） | 每请求 1 小串（opt-in） | **键是地址、不是 `IPEndPoint`**（B4）：绝不能用 `IPEndPoint.ToString()`（含端口 → 每连接一个桶）。无 peer 时共用 fallback 桶（fail-closed），不是跳过 |
| `Identity(fallback = "anonymous")` | `AuthMiddleware.GetIdentity(ctx)?.UserId ?? fallback` | 0 | **只能 post-auth**；pre-auth 误放时落 fallback 桶（全局限流、可见），不是静默不限 |
| `Header(name, fallback = null)` | 头值或 fallback | 0 | `fallback=null` 表示"无此头则档不适用"（显式 opt-out） |
| `Path()` | `ctx.Path`（首次物化 1 次分配，`WebContext.Path => _pathString ??= PathMemory.ToString()`，`WebContext.cs:27`） | 1（首次） | **`RateLimitPath.Prefix/Exact` 的匹配走 `PathMemory.Span`（零分配）**；`Path()` 返回 string 必然物化，故**不推荐**做预算键（路径维度分桶爆炸） |

pre/post-auth 矩阵：`Constant / TokenMatch / Not / RemoteAddress / Header / Path` 可 pre-auth；`Identity` 必须 post-auth。fallback 规则：凡"取不到值"是**常态**的分类器（`RemoteAddress`/`Identity`/`Header`）都带 fallback；`fallback=null` 是显式的"档不适用"。

### 3.4 命名策略、挂载与观测（④）

**现实**：`WebApp.Build()` 把中间件链包在 router（terminal）之外，**中间件先于路由**，拿不到"已匹配路由"的 metadata。因此本期挂载 = **按策略谓词挂载**；名字用于声明、复用、观测与将来的挂载句柄。

```csharp
var token = options.Token;   // 明文 owner token；null/空 = 无 token 模式（此时 TokenMatch 恒不匹配、trusted 档不适用；tokenless 场景应改用 Constant 档，见 §5.4）
var web = RateLimitPolicy.Create("web")
    .Exempt("/api/health")
    // 盾档：路径范围 = AuthMiddleware 的路径范围（全局，见下），不得按 /api/ 收窄
    .Tier("trusted",   RateLimitBudget.PerSecond(300, 30), RateLimitKeys.TokenMatch(token))
    .Tier("anonymous", RateLimitBudget.PerSecond(60, 1),   RateLimitKeys.Not(RateLimitKeys.TokenMatch(token), "instance"))
    // 拒绝回调：计数/采样后写日志——盾档在洪水下是稳态速率，逐条 Warning/审计写入会被放大；
    // 也不要在这里物化 Context.Path（它会把路径串物化一次）
    .OnRejected(r => metrics.CountRejection(r.Policy, r.Tier, r.Reason))
    .Build();

app.UseRateLimit(web);      // 必须注册在 AuthMiddleware 之前（W-04 的 pre-auth 盾）
```

- **盾档的路径范围 = `AuthMiddleware` 的路径范围（B2）**：`AuthMiddleware` 是无路径范围的全局中间件（`DaemonHostSvc.cs:127/133`），任何带 `Authorization: Bearer x` 的请求——包括 `/acp`（`AcpHostingExtensions.cs:52`）、`/acp/events`（`:62`）、`/acp/ticket`（`:71`）与未知路径——都会先跑 PBKDF2。因此盾档**不得**按 `/api/` 收窄；只允许 `Exempt(...)` 这类显式豁免。若确实要按路径分档，**每一档必须显式包含 `/acp*` 与未知路径**。
- `WebAppRateLimitExtensions.UseRateLimit(this WebApp, RateLimitPolicy)` → 返回 `WebApp`（与 `Use` 一致的链式）；每策略一层（注册顺序 = 外层到内层）；`WebApp` 内加内部注册表（name→policy）用于**重名检测**（启动期 throw）与将来的路由挂载。
- **`Build()` 校验**：零档、**重名档**（档名进观测与 `RateLimitRejection.Tier`）、**非法预算**（`MaxTokens ≤ 0` 或 `RefillInterval ≤ 0`；`RefillRate = 0` 合法=固定窗）→ throw；**重名策略**由 `UseRateLimit` 的注册表在启动期 throw（`Build()` 看不到其它策略）。
- `RateLimitPath.Prefix/Exact/Any`：span 匹配（Ordinal），无正则、AOT-safe。`Prefix` 按**路径段边界**匹配（前缀末尾是 `/`，或其后紧跟 `/` 或路径结束）——`/api` 不匹配 `/apix`；`Exempt` 同语义；单端点用 `Exact`。匹配是**字面**的：不做 `..`/`.` 归一化、不做百分号解码，与路由（`RadixTree` 的 Ordinal 字面匹配）一致；外层若有归一化网关，需自行对齐。
- 两层"适用"分清，且**组合语义写死**：`Exempt(...)` 多次调用**累积为 OR 白名单**；`Applies(predicate)` 与"不在白名单"取 **AND**——即 `ShouldApply(ctx) = (Applies is null || Applies(ctx)) && !IsExempt(ctx)`（可共存，不互相覆盖）。`tier.Applies` / `tier.Key == null` = 该档不适用。
- **`OnRejected`**：见 §3.2.7；同步、AOT-safe、未设置时零开销。
- **观测**：`RateLimitState` 增加可空 `Policy`/`Tier`；放行请求在下游也能看到命中桶名。

### 3.5 AOT-first 合规

- 无反射、无 `dynamic`、无运行时属性消费（未来 `[RateLimit]` 必须由源生成器编译期消费）。
- 热路径：`for` 遍历冻结数组；键清理与路径匹配用 span；`TokenMatch` 常量时间比较（**比较本身不分配**；复用认证同源解析，`Split(' ', 2)` 的那次分配与现状一致——不新增分配，也不谎报 0；不用 `Encoding.GetBytes`/`FixedTimeEquals`）。
- 不用异常做控制流：**新 API 不再如此**（取代旧的"`KeySelector` 抛异常 bypass"）；旧中间件保留其 bypass（见 §3.6），不要按本条去删旧路径的 catch。
- 验证：`tests/PicoWeb.AotVerify` + `scripts/test-aot-publish.ps1`（CI `ci.yml:101` 的 native publish+run）；分配数据进 `tests/PicoNode.PerfHarness`（不做跨运行时脆断言）。

### 3.6 与既有类型的关系

| 类型 | 处理 |
|---|---|
| `RateLimitMiddleware.Create(store, options)` | 保留（sample 与旧接线可用）；改为调用抽出的 `RateLimitResponses` helper |
| `RateLimitOptions` / `InMemoryRateLimitStore` | 保留；store 新增 budget 构造重载 |
| `RateLimitResult` | class → `readonly record struct`（`PicoNode.Web` 的破坏性变更，见 §3.2.9 与 §5） |
| `RateLimitState` | 增加可空 `Policy`/`Tier` |
| `IRateLimitStore` | 不变（仍单方法） |

---

## 4. Testing

**单测（`tests/PicoNode.Web.Tests/`，用 §3.1 的 `internal TimeProvider` 缝做确定性回填）**

- 策略构建：零档 throw、重名策略 throw、**重名档 throw**、**非法预算 throw**（`MaxTokens≤0`/`RefillInterval≤0`；`RefillRate=0` 合法）、`Exempt/Prefix/Exact` 语义（段边界：`/api` 不匹配 `/apix`、不匹配 `/api2`；`Exempt("/api/health")` 不匹配 `/api/healthz`）、**`Exempt` 与 `Applies` 共存时的 AND 语义**（白名单累积 OR）、档 `Applies` 与 `Key=null` 的组合；
- 求值：单档放行（头/state 含 Policy/Tier）/拒绝（429+Retry-After+body、不写 state）；链式（A 放行 B 拒绝→报 B，且 **A 的令牌已耗**）；**`FailOpen` 跳档时前面档的令牌同样不退**；无档匹配直通；策略豁免直通；`OnRejected` 恰好一次（`LimitReached` 与 `StoreError` 各一条用例，并断言 `Reason`）；多策略按注册顺序；**先声明者胜**的头/state；`FailOpen` true/false 的 store 抛错路径与旧行为一致；每档 store 并发小测；**Dispose 后按 store 异常路径处理**（FailOpen=false → 429）；**时间缝注记**：注入手动时钟（覆盖 `GetUtcNow` 的那个，见 §3.1）时 429 用例的 `Retry-After` **数值**无意义（手动时钟的 `NextAvailableAt` 与墙上时钟相减），只断言存在性/格式；
- **B1**：合法 token 的请求**不得扣减 anonymous 档**（`Not` 组合器语义）；`Not` 的三种输入（命中/未命中/空 token）各一条；
- **B2**：`/acp` 与未知路径**与 `/api/...` 落在同一盾档内**（不得绕过）；
- **B5**：**内层策略 429 上不出现外层策略的 `Remaining`/`Reset`**（组级判空）；与旧中间件同装时首写者赢（组级）；
- 分类器：`TokenMatch`（正确/错/长短/缺头/非 Bearer/逗号后缀/大小写/**双空格 `"Bearer  abc"` 与尾空格 `"Bearer abc "`——认证侧不 Trim，限流侧必须同样不匹配**/**`expected` 为 `null`/空 → 恒不匹配、不抛异常**）、`RemoteAddress`（有/无 peer→fallback；**IPv4-mapped IPv6 归一化**；键不含端口）、`Identity`（pre-auth→fallback、post-auth→UserId）、`Header`、`Path`、`Constant`；`Not` 的"inner 不命中"边界（含 inner 返回 fallback 值的情形）；
- `TryGetBearerToken` 与 `AuthMiddleware` 同源：改后原有认证测试必须**保持全绿**。

**AOT**：`tests/PicoWeb.AotVerify/Program.cs` 增注册策略 + 豁免路径，跑"允许/拒绝(429)/豁免(200)"三请求。

**性能**：`tests/PicoNode.PerfHarness` 记录"常量键单档放行"路径的每请求分配。

---

## 5. 迁移与发布

**PicoAgent 是本组件的第一个消费者**（跨仓库永远走 NuGet：PicoAgent `Directory.Build.props:27-29`；当前 pin `PicoWeb 2026.4.6`）。顺序：

1. PicoNode 实现 + 测试 + AotVerify → **刷新公共 API 基线**：`api/PicoNode.Web.public.txt`（新类型 + `RateLimitResult` 的 class→struct 会以 removed/added 出现）与 `api/PicoNode.Http.public.txt`（`HttpRequest.RemoteEndPoint`）；`scripts/release.ps1:250+` 会自动 diff 并按 x+1 推出新版本；
2. `scripts/release.ps1` 发版（工作流发布 NuGet）；
3. PicoAgent 升 `Directory.Packages.props` 的 `PicoWeb` pin；
4. `DaemonHostSvc` 接入（取代当前临时预算）：
   - **注册位置钉死**：`app.UseRateLimit(policy)` 必须在 `AuthMiddleware` **之前**（pre-auth 分类 + 屏蔽 PBKDF2）——这是本设计成立的前提；
   - 无 token：策略 `web`，档 `instance`（`Constant("instance")`，300/30）+ `Exempt("/api/health")` + `OnRejected` = **每档限流日志**（daemon 只有 ILogger、无 metrics：首条 + 之后每档每 10s 一条，带累计计数；不逐条、不物化 `Context.Path`——盾档在洪水下是稳态速率）；
   - 有 token：档 `trusted`（`TokenMatch(options.Token)`，300/30）+ `anonymous`（`Not(TokenMatch(options.Token), "instance")`，60/1）+ `Exempt("/api/health")` + 同上 OnRejected；
   - **设备凭据（`deviceId:secret`）落在 anonymous 档**：`TokenMatch` 只比 owner token（明文），设备凭据由 `DeviceCredentials.Verify` 单独校验（`DeviceCredentials.cs:101`）——若配对设备的预算不够，要么把设备档显式加进策略，要么按设备流量调 anonymous 预算。**限流的"可信"= 明文比对；认证的"可信"= PBKDF2 哈希校验：两套实现**（写进 §6）；
   - `FailOpen=false` 保持；策略由宿主 `using` 释放（§3.1.1）；
5. 回归：daemon 套件 + `AotSmokeTests` + AOT 重发布 + 部署 + 重跑 §1.1 复现（tokenless 下 100 并发 `/row`、批量删除不得 429；token 模式 61 并发匿名仍 429；桶耗尽时 `/api/health` 不 429）。

> 过渡：PicoAgent 当前已把 tokenless 预算临时调为 300/30（commit `a94103c8`）以解线上 429；组件发版后由上面的策略声明取代。

---

## 6. Risks & Boundaries

| 风险 | 处置 |
|---|---|
| 链式/`FailOpen` 跳档时令牌不退 | 契约测试固定；**声明顺序统一"最紧/最细在前"**（见 §3.2.4：头指向 binding 档、细档拒绝不先浪费粗档令牌）；PicoAgent 的 trusted/anonymous 用 `Not` 互斥，不触发 |
| 上报的桶不保证 binding | 文档明示；要精确就声明在最前或用 `OnRejected` |
| per-IP 键每请求 1 小串；双栈可能产生 `::ffff:1.2.3.4`/`1.2.3.4` 两键 | 文档明示；分类器做 IPv4-mapped 归一化；仅 per-IP 档 opt-in |
| 桶数量无上限：`Header(...)`/`RemoteAddress()` 作键时，客户端可控的高基数值会持续造桶（仅 `2×CleanupInterval` 后回收，默认 5 min 窗口） | 文档明示：不要用客户端可控的高基数头做键；框架**不声称**有桶上限，硬上限（LRU/计数封顶）列为后续（§7） |
| 反代下 peer=代理 | 转发头为独立后续项；文档写明 |
| 无路由 metadata | 谓词挂载；`RequireRateLimit(name)` 列为后续（§7） |
| `RateLimitResult` class→struct | `PicoNode.Web` 破坏性变更；发布说明 + API 基线刷新（§5） |
| `FailOpen` 默认差异（策略 false / 旧中间件 true） | 文档与测试固定；应用显式选择 |
| `TokenMatch` 闭包持有明文 token | 与 daemon 自身持有 `options.Token` 同等级；策略生命周期 = 应用生命周期，不额外扩散 |
| 限流"可信"与认证"可信"是两套判定 | §5.4 明示；设备凭据走 anonymous 档，预算按需调整 |

---

## 7. Follow-ups

1. router metadata + `RequireRateLimit("name")`（真正的按路由挂载；需"路由解析先行"的管线阶段）。
2. `Controllers.Gen` 的 `[RateLimit("name")]`（源生成器，编译期消费）。
3. 转发头中间件（`X-Forwarded-For` 信任链）→ 之后 `RemoteAddress` 分类器可切换为"有效客户端地址"。
4. 新算法（SlidingWindow / Concurrency）作为额外 store 实现；`IRateLimitStore` 若增长到 3+ 方法再抽 `PicoNode.Web.RateLimit.Abs`（沿用 2026-06-22 spec §4.4 的约定）。
5. `RateLimitKeys.Not(inner, Func<WebContext,string?> fallback)` 重载——"非 token 请求 → 按 IP 分桶"这类诉求，常量 fallback 表达不了。
6. 每个 store 的桶数量上限（`ConcurrentDictionary` 的 LRU/计数封顶）——高基数键（`Header(...)`、多客户端 `RemoteAddress()`）下的内存边界（§6）。

---

## 8. Acceptance

- [x] `RateLimitPolicy/Tier/Budget/Keys/Path` + `RateLimitMiddleware.Create(policy)` + `UseRateLimit` 落地，公共 API 与本文一致；
- [x] §4 单测全绿（含 B1/B2/B5 专项）；原有 `RateLimitMiddlewareTests`/`AuthMiddleware` 测试**保持全绿**；
- [x] `api/PicoNode.Web.public.txt` 与 `api/PicoNode.Http.public.txt` 基线刷新，`scripts/release.ps1` 的 diff 通过；
- [x] `scripts/test-aot-publish.ps1`（AotVerify）在 CI 通过；
- [x] `PerfHarness` 记录常量键单档路径分配（数值入档，不做脆断言）；
- [x] 既有 sample（`ShowcaseApp`）不改一行仍编译通过；
- [ ] PicoAgent 接入后：§5 第 5 步的实机复现全过。

**验收记录（2026-10-03）**：前六项通过——1203 个测试全绿（`PicoNode.Web.Tests` 379 项含限流全量用例）；`ShowcaseApp` 未改动仍随解决方案编译通过（`dotnet build PicoNode.slnx -c Release`，0 warning / 0 error）；`ci.yml` 的 AOT publish+run（`scripts/test-aot-publish.ps1`）在 main 上通过；`PerfHarness` 的 `RateLimitPolicyAllocationTests` 已记录常量键单档分配。第 7 项依赖 PicoAgent 侧接入（§5.3–5.5），属独立后续计划，保持未勾。

**发布偏差记录**：本特性给 `PicoNode.Web` 增加 10 个公开类型，并在同一 store 契约上留下破坏性变更（`RateLimitResult` class→`readonly record struct`），`PicoNode.Http` 增加 `HttpRequest.RemoteEndPoint`；相对 v2026.4.6 的基线差异共 61 行。因此 §5 与实现计划 Task 10 Step 1 都要求 **x+1**。但基线在发布前已被刷新（`db07d38`），发布门禁据此报 `API changed : false`，包先以 **v2026.4.7（y 位）** 发出，属版本误标。纠正做法：把 `api/` 恢复为上一发布的公开面后重跑发布流程，让门禁重新看到 x 跳；`v2026.4.7` 建议在 NuGet 上 unlist，避免消费方把它当补丁级更新接受该破坏性变更。
