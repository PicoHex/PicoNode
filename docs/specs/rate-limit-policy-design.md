# PicoNode Rate Limit Policy — 分层限流组件设计

> 状态：待 spec review（设计已逐节确认：① 数据模型 ② 求值语义 ③ 分类器与 peer ④ 命名策略 ⑤ 测试与迁移）
> 日期：2026-10-02
> 修订：扩展 `docs/superpowers/specs/2026-06-22-ratelimit-middleware-design.md`（令牌桶/store/头/边界仍是基线；本文新增**策略、分类、组合、声明**层，并纠正两处 spec↔实现偏差）
> 原则：PicoHex 零外部依赖、AOT-first、**机制在框架 / 策略在应用**

---

## 1. Problem

### 1.1 现场（2026-10-02 实测，PicoAgent :8080 部署）

删除 workspace 时前端日志出现连续的

```
ERROR [web] Unhandled exception: GET /fragments/workspaces/{id}/row?confirm=1
EXCEPTION: PicoAgent.Client.AcpClientException: HTTP 429 (rate-limited) (code 429)
```

测量（对运行中的实例）：

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
    public Predicate<WebContext>? Applies { get; }   // 档级路径限定，可空
}

public sealed class RateLimitPolicy : IDisposable
{
    public string Name { get; }
    public bool FailOpen { get; }                    // 默认 false（盾语义；见 §3.2.6）
    public Predicate<WebContext>? Applies { get; }   // false = 整策略豁免
    public RateLimitTier[] Tiers { get; }            // 构建后冻结为数组
    public Action<RateLimitRejection>? OnRejected { get; }
    public static RateLimitPolicyBuilder Create(string name);
    public void Dispose();                           // 释放各档的 store（宿主负责）
}

public readonly struct RateLimitRejection             // string Policy; string Tier; RateLimitResult Result; WebContext Context

public sealed class RateLimitPolicyBuilder
{
    public RateLimitPolicyBuilder Tier(string name, RateLimitBudget budget, Func<WebContext, string?> key, Predicate<WebContext>? applies = null);
    public RateLimitPolicyBuilder Exempt(string pathPrefix);
    public RateLimitPolicyBuilder OnRejected(Action<RateLimitRejection> handler);
    public RateLimitPolicyBuilder FailOpen();          // 默认 false（盾）；显式开启才 fail-open
    public RateLimitPolicy Build();                    // 零档 → throw
}
```

要点：

- 预算与档是**值类型/不可变对象**，委托在构造期创建一次；`Tiers` 冻结为数组，请求路径 `for` 遍历（无 LINQ、无枚举器、无每请求闭包）。
- 每档一个 `InMemoryRateLimitStore`（**现有类型零改动**，一预算一 store）：新增构造重载
  `InMemoryRateLimitStore(in RateLimitBudget budget, TimeSpan? cleanupInterval = null)`，避免给 store 传假的 `KeySelector`；`CleanupInterval` 取策略级默认 5 分钟。
- 策略 `Dispose()` 释放全部 store（`Timer`）；宿主在应用生命周期内持有（与今天 `using var rateStore = …` 同模式）。

### 3.2 求值语义（②）

新增 `RateLimitMiddleware.Create(RateLimitPolicy policy)` 重载；现有 `Create(store, options)` 原样保留。

```
if (policy.Applies is { } applies && !applies(ctx)) return await next(ctx, ct);   // 策略豁免
RateLimitResult? winner = null;
for i in policy.Tiers:
    if (tier[i].Applies is { } t && !t(ctx)) continue;
    key = tier[i].Key(ctx);            if (key is null) continue;      // 档不适用
    key = SanitizeKey(key);            // span：256 截断 + CR/LF→'_'（兼容旧行为）
    try    { result = await store[i].TryConsumeTokenAsync(key, ct); }
    catch when (policy.FailOpen) { continue; }
    catch  { return RateLimitResponses.RejectedOnStoreError(tier[i].Budget.MaxTokens); }
    if (!result.Allowed) return RateLimitResponses.Rejected(result, tier[i]);   // 拒绝者胜，停止后续消耗
    if (winner is null || result.Remaining < winner.Remaining) winner = result;  // 最紧者胜；并列取先声明
if (winner is not null) ctx.Items[WebContextKeys.RateLimitState] = new RateLimitState { Limit, Remaining, NextAvailableAt, Policy, Tier };
var response = await next(ctx, ct);
if (winner is not null) RateLimitResponses.AddHeaders(response, winner);        // 首写者赢
return response;
```

1. **链式**：所有匹配档都要放行；任一拒绝即返回，后续档不再消耗。
2. **令牌不退**：先放行、后被更后面的档拒绝时，前面已消耗的令牌**不退还**（令牌桶无 refund；.NET `CreateChained` 对令牌桶同理）。互斥档（trusted/anonymous 按 token 二选一）不触发；仅叠加兜底档时出现，文档与测试都固定该契约。
3. **无档匹配** → 直接 `next`，不写 state/headers（档级豁免）。
4. **头与 state**：成功取所有匹配档中 `Remaining` 最小者（并列取先声明），拒绝取拒绝档；头名与 2026-06-22 spec 完全一致（`X-RateLimit-Limit/Remaining/Reset`，拒绝加 `Retry-After` + JSON body）。`Items` 只在放行路径写。
5. **叠加规则 = 首写者赢**：`HttpHeaderCollection` 只有 `Add`/`TryGetValue`（无 Remove/覆盖写，`src/Http/PicoNode.Http/HttpHeaderCollection.cs`），所以写头前先 `TryGetValue`，已存在则跳过 → 多条策略并存时内层头优先。文档写明：**不要**把旧的单桶 `RateLimitMiddleware` 与新策略中间件同时装在一条管线上（旧中间件不认识该规则）；多条**策略**之间可以并存（注册顺序 = 外层到内层）。
6. **`FailOpen` 默认 false**（与旧单桶中间件的默认 true 刻意不同）：策略是盾，失败即拒绝；应用可显式开启。store 异常路径复用旧行为（`Retry-After: 60` + `X-RateLimit-Limit`）。
7. **共用 helper**：抽出 `RateLimitResponses.Rejected / RejectedOnStoreError / AddHeaders`（internal），旧中间件改为调用它——429/头行为单一来源、零行为变化。
8. **`RateLimitResult` 改为 `readonly record struct`**（纯 DTO、无身份语义）：消除每档求值的小分配；对 NuGet 上的 `PicoWeb` 属破坏性变更，按发布纪律记入版本说明。`RateLimitState` 保持 class（`Items` 为 `IDictionary<string, object?>`）。

### 3.3 分类器与 peer（③）

**peer 通路（P2）**

| 位置 | 改动 |
|---|---|
| `HttpRequest`（`src/Http/PicoNode.Http/HttpRequest.cs`） | 新增 `public IPEndPoint? RemoteEndPoint { get; internal set; }`（与 `RemoteCloseToken` 同款 `internal set` 先例） |
| `WebContext` | 新增计算属性 `RemoteEndPoint => Request.RemoteEndPoint`、`RemoteAddress => Request.RemoteEndPoint?.Address`（零分配） |
| 赋值点（3 处） | `Http1ConnectionProcessor`（`HttpRequestParser.Parse` 成功后）、`Http2StreamHandler.cs:361`、`Http2StreamHandler.Frames.cs:329`，统一 `request.RemoteEndPoint = connection.RemoteEndPoint as IPEndPoint;`（接口静态类型是抽象 `EndPoint`；HTTP 走 TCP，cast 必成功） |

默认 null，现有测试构造 `HttpRequest` 不受影响。**边界**：反代场景 peer 是代理地址（转发头为独立后续项）。

**分类器（`RateLimitKeys`）**

| 分类器 | 返回 | 分配 | 说明 |
|---|---|---|---|
| `Constant(key)` | 固定键 | 0 | 构造期预计算 |
| `TokenMatch(expected, key = "trusted")` | 匹配 → `key`；否则 `null` | 0 | 复用 `AuthMiddleware` 的 Bearer 解析（抽 `internal TryGetBearerToken`，与认证同源）；**常量时间逐字符比较**（长度不等→不等；相等时 XOR 累积） |
| `RemoteAddress(fallback = "unknown")` | `RemoteAddress?.ToString() ?? fallback` | 每请求 1 小串（opt-in） | 无 peer 时共用 fallback 桶（fail-closed），不是跳过 |
| `Identity(fallback = "anonymous")` | `AuthMiddleware.GetIdentity(ctx)?.UserId ?? fallback` | 0 | **只能 post-auth**；pre-auth 误放时落 fallback 桶（全局限流、可见），不是静默不限 |
| `Header(name, fallback = null)` | 头值或 fallback | 0 | fallback=null 表示"无此头则档不适用" |
| `Path()` | `ctx.Path` | 0 | 应急/调试；**不推荐**做预算键（路径维度分桶爆炸） |

pre/post-auth 矩阵：`Constant / TokenMatch / RemoteAddress / Header / Path` 可 pre-auth；`Identity` 必须 post-auth。所有"可能取不到值"的分类器都带 fallback，默认让档保持生效。

### 3.4 命名策略、挂载与观测（④）

**现实**：`WebApp.Build()` 把中间件链包在 router（terminal）之外，**中间件先于路由**，拿不到"已匹配路由"的 metadata。因此本期挂载 = **按策略谓词挂载**；名字用于声明、复用、观测与将来的挂载句柄。

```csharp
var web = RateLimitPolicy.Create("web")
    .Exempt("/api/health")
    .Tier("trusted",   RateLimitBudget.PerSecond(300, 30), RateLimitKeys.TokenMatch(token), RateLimitPath.Prefix("/api/"))
    .Tier("anonymous", RateLimitBudget.PerSecond(60, 1),   RateLimitKeys.Constant("instance"), RateLimitPath.Prefix("/api/"))
    .OnRejected(rejection => logger.Warning($"[rate-limit] {rejection.Policy}/{rejection.Tier} rejected {rejection.Context.Path}"))
    .Build();

app.UseRateLimit(web);
```

- `WebAppRateLimitExtensions.UseRateLimit(this WebApp, RateLimitPolicy)`：每策略一层（沿用 `WebApp.Use` 合成语义），注册顺序 = 外层到内层；`WebApp` 内加内部注册表（name→policy）用于**重名检测**（启动期 throw）与将来的路由挂载。
- **零档策略** `Build()` 时 throw（响亮，不静默）。
- `RateLimitPath.Prefix/Exact/Any`：span 匹配（Ordinal），无正则、AOT-safe。`Prefix` 按**路径段边界**匹配（前缀末尾是 `/`，或其后紧跟 `/` 或路径结束）——`/api` 不匹配 `/apix`；单端点用 `Exact`。
- 两层"适用"分清：`policy.Applies/Exempt` = 整策略是否参与；`tier.Key == null` = 该档不适用。
- **`OnRejected`**：`Action<RateLimitRejection>`，在返回 429 前调用（429 路径不经过 `next`，应用无法在别处观测"哪个桶拒的"）；同步、AOT-safe、未设置时零开销。
- **观测**：`RateLimitState` 增加可空 `Policy`/`Tier`；放行请求在下游也能看到命中桶名。

### 3.5 AOT-first 合规

- 无反射、无 `dynamic`、无运行时属性消费（未来 `[RateLimit]` 必须由源生成器编译期消费）。
- 热路径：`for` 遍历冻结数组；键清理用 span（无变化不新建字符串）；`TokenMatch` 零分配常量时间比较（不用 `Encoding.GetBytes`/`FixedTimeEquals`）。
- 不用异常做控制流（取代旧的"`KeySelector` 抛异常 bypass"）。
- 验证：`tests/PicoWeb.AotVerify` + `scripts/test-aot-publish.ps1`（CI `ci.yml:101` 的 native publish+run）；分配数据进 `tests/PicoNode.PerfHarness`（不做跨运行时脆断言）。

### 3.6 与既有类型的关系

| 类型 | 处理 |
|---|---|
| `RateLimitMiddleware.Create(store, options)` | 保留（sample 与旧接线可用）；改为调用抽出的 `RateLimitResponses` helper |
| `RateLimitOptions` / `InMemoryRateLimitStore` | 保留；store 新增 budget 构造重载 |
| `RateLimitResult` | class → `readonly record struct`（破坏性，见 §3.2.8） |
| `RateLimitState` | 增加可空 `Policy`/`Tier` |
| `IRateLimitStore` | 不变（仍单方法） |

---

## 4. Testing

**单测（`tests/PicoNode.Web.Tests/`，沿用 store 的 `TimeProvider` 测试缝）**

- 策略构建：重名策略 throw、零档 throw、`Exempt/Prefix/Exact` 语义、档 `Applies` 与 `Key=null` 的组合；
- 求值：单档放行（头/state 含 Policy/Tier）/拒绝（429+Retry-After+body、不写 state）；链式（A 放行 B 拒绝→报 B，且 **A 的令牌已耗**）；无档匹配直通；策略豁免直通；`OnRejected` 恰好一次；多策略按注册顺序；`X-RateLimit-*` 取最紧；与旧中间件同装首写者赢；`FailOpen` true/false 的 store 抛错路径与旧行为一致；每档 store 并发小测；
- 分类器：`TokenMatch`（正确/错/长短/缺头/非 Bearer/逗号后缀/大小写）、`RemoteAddress`（有/无 peer→fallback）、`Identity`（pre-auth→fallback、post-auth→UserId）、`Header`、`Path`、`Constant`；`Prefix` 段边界（`/api` 不匹配 `/apix`、不匹配 `/api2`）；
- `TryGetBearerToken` 与 `AuthMiddleware` 同源：改后原有认证测试必须全绿。

**AOT**：`tests/PicoWeb.AotVerify/Program.cs` 增注册策略 + 豁免路径，跑"允许/拒绝(429)/豁免(200)"三请求。

**性能**：`tests/PicoNode.PerfHarness` 记录"常量键单档放行"路径的每请求分配。

---

## 5. 迁移与发布

**PicoAgent 是本组件的第一个消费者**（跨仓库永远走 NuGet：PicoAgent `Directory.Build.props:27-29`；当前 pin `PicoWeb 2026.4.6`）。顺序：

1. PicoNode 实现 + 测试 + AotVerify → `scripts/release.ps1` 发版（工作流发布 NuGet）；
2. PicoAgent 升 `Directory.Packages.props` 的 `PicoWeb` pin；
3. `DaemonHostSvc` 接入（取代当前临时预算，见下）：
   - 无 token：策略 `web`，档 `instance`（`Constant("instance")`，300/30）+ `Exempt("/api/health")` + `OnRejected`→logger；
   - 有 token：档 `trusted`（`TokenMatch(options.Token)`，300/30）+ `anonymous`（`Constant("instance")`，60/1）+ `Exempt("/api/health")` + `OnRejected`；
   - `FailOpen=false` 保持；
4. 回归：daemon 套件 + `AotSmokeTests` + AOT 重发布 + 部署 + 重跑 §1.1 复现（tokenless 下 100 并发 `/row`、批量删除不得 429；token 模式 61 并发匿名仍 429；桶耗尽时 `/api/health` 不 429）。

> 过渡：PicoAgent 当前已把 tokenless 预算临时调为 300/30（commit `a94103c8`）以解线上 429；组件发版后由上面的策略声明取代。

---

## 6. Risks & Boundaries

| 风险 | 处置 |
|---|---|
| 链式拒绝时令牌不退 | 契约测试固定；档顺序建议"从粗到细"；PicoAgent 的档互斥，不触发 |
| per-IP 键每请求 1 小串 | 文档明示；仅 per-IP 档 opt-in；必要时后续给 store 加 `IPAddress` 键重载 |
| 反代下 peer=代理 | 转发头为独立后续项；文档写明 |
| 无路由 metadata | 谓词挂载；`RequireRateLimit(name)` 列为后续（§7） |
| `RateLimitResult` class→struct | 破坏性；发布说明记录 |
| `FailOpen` 默认差异（策略 false / 旧中间件 true） | 文档与测试固定；应用显式选择 |
| `TokenMatch` 闭包持有明文 token | 与 daemon 自身持有 `options.Token` 同等级；策略生命周期 = 应用生命周期，不额外扩散 |

---

## 7. Follow-ups

1. router metadata + `RequireRateLimit("name")`（真正的按路由挂载；需"路由解析先行"的管线阶段）。
2. `Controllers.Gen` 的 `[RateLimit("name")]`（源生成器，编译期消费）。
3. 转发头中间件（`X-Forwarded-For` 信任链）→ 之后 `RemoteAddress` 分类器可切换为"有效客户端地址"。
4. 新算法（SlidingWindow / Concurrency）作为额外 store 实现；`IRateLimitStore` 若增长到 3+ 方法再抽 `PicoNode.Web.RateLimit.Abs`（沿用 2026-06-22 spec §4.4 的约定）。

---

## 8. Acceptance

- [ ] `RateLimitPolicy/Tier/Budget/Keys/Path` + `RateLimitMiddleware.Create(policy)` + `UseRateLimit` 落地，公共 API 与本文一致；
- [ ] §4 单测全绿；原有 `RateLimitMiddlewareTests`/`AuthMiddleware` 测试不变绿；
- [ ] `scripts/test-aot-publish.ps1`（AotVerify）在 CI 通过；
- [ ] `PerfHarness` 记录常量键单档路径分配（数值入档，不做脆断言）；
- [ ] 既有 sample（`ShowcaseApp`）不改一行仍编译通过；
- [ ] PicoAgent 接入后：§5 第 4 步的实机复现全过。
