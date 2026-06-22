# PicoWeb Endpoint Source Generators Design

**Goal:** Three-way endpoint registration (MapXX + Attributes + Convention) via Roslyn source generators, producing AOT-compatible endpoint stubs with compile-time type registration.

**Architecture:** Two source generators in `src/PicoWeb/Analyzers/` — `Controllers.Gen` (controller discovery) and `PicoWeb.Gen` (MapXX binding). Both run at user project compile time, generate `[PicoJsonSerializable]` declarations and endpoint stub code. No runtime reflection.

---

## Source Generator Projects

```
src/PicoWeb/Analyzers/
├── Controllers.Gen/
│   ├── Controllers.Gen.csproj      (netstandard2.0, Roslyn 4.x)
│   ├── ControllersGenerator.cs      (IIncrementalGenerator)
│   ├── Conventions.cs               (HTTP method convention, path convention)
│   └── RouteModel.cs                (discovered endpoint model)
│
├── PicoWeb.Gen/
│   ├── PicoWeb.Gen.csproj           (netstandard2.0, Roslyn 4.x)
│   ├── MapMethodGenerator.cs         (IIncrementalGenerator)
│   └── MapCallModel.cs              (MapGet/MapPost call model)
│
└── Directory.Build.props             (shared SG properties)
```

Both are packaged as `analyzers/dotnet/cs/` inside the `PicoWeb` NuGet package.

---

## 1. Controllers.Gen — Controller Discovery

### Discovery Order

```
           ┌─ 特性标注: [HttpGet] / [HttpPost] / [HttpDelete] / [HttpPut]
Controllers/ 下的类 ──┤
           └─ 约定: 方法名前缀 Get / Post / Delete / Put → HTTP 方法
```

### Convention Rules

| 方法名前缀 | HTTP 方法 | 示例路径 |
|-----------|:---------:|---------|
| `Get*` | GET | `GetUser(int id)` → `GET /users/{id}` |
| `Post*` | POST | `CreateUser(CreateDto dto)` → `POST /users` |
| `Put*` | PUT | `UpdateUser(int id, UpdateDto dto)` → `PUT /users/{id}` |
| `Delete*` | DELETE | `DeleteUser(int id)` → `DELETE /users/{id}` |
| `Patch*` | PATCH | `PatchUser(int id, PatchDto dto)` → `PATCH /users/{id}` |

### Path Convention

```
类名: UsersController → 路由前缀 /api/users
方法: GetUser(int id) → 路由后缀 /{id}
完整: GET /api/users/{id}

规则:
  类名去掉 Controller 后缀 → kebab-case → 前缀
  方法名去掉 HTTP 方法前缀 → kebab-case 参数化 → 后缀
    参数名作为 {param} 占位符
```

### Attribute Override

```csharp
// 特性覆盖约定
[Route("api/v2/users")]          // 覆盖类名推导的前缀
public class UsersController
{
    [HttpGet("{userId}")]         // 覆盖方法名推导的路由
    public UserDto GetUser(int id, int userId) { ... }
}
```

### Incremental Generator Pipeline

```csharp
[Generator(LanguageNames.CSharp)]
public sealed class ControllersGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Step 1: Collect syntax trees in Controllers/ folder
        var controllerFiles = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: (node, ct) => node is ClassDeclarationSyntax,
            transform: (ctx, ct) => ctx.Node)
            .Where(static node => IsInControllersFolder(node));

        // Step 2: Extract class symbols
        var controllerClasses = controllerFiles
            .Select((node, ct) => GetClassSymbol(node, ct))
            .Where(static symbol => symbol is not null);

        // Step 3: Extract endpoint models
        var endpointModels = controllerClasses
            .SelectMany((symbol, ct) => GetEndpointModels(symbol!, ct));

        // Step 4: Register source
        context.RegisterSourceOutput(endpointModels, GenerateStubs);
    }

    private static bool IsInControllersFolder(SyntaxNode node)
    {
        return node.SyntaxTree.FilePath.Contains("Controllers");
    }
}
```

### What Gets Generated

The source generator runs during user project compilation. The controller class is in the user project's source tree, so the generator can access both its syntax tree and semantic model.

For each discovered endpoint, `Controllers.Gen` generates:

```csharp
// 1. Serialization type registrations
[assembly: PicoJsonSerializable(typeof(UserDto))]
[assembly: PicoJsonSerializable(typeof(CreateUserDto))]

// 2. Endpoint stub (auto-generated, regenerated on each build)
//    The original method body is replicated into the stub.
//    This is the same approach as ASP.NET Core's RDG.
public static class UsersController_Endpoints
{
    public static void Register(WebApp app)
    {
        // GET /api/users/{id}
        app.MapGet("/api/users/{id}", async (WebContext ctx, int id) =>
        {
            var svc = (UserService)ctx.Services.GetService(typeof(UserService))!;
            var result = await svc.GetByIdAsync(id);
            var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(result);
            return Results.Json(200, bytes);
        });
    }
}
```

---

## 2. PicoWeb.Gen — MapXX Type Registration

PicoWeb.Gen does NOT rewrite `MapGet/MapPost` call sites. Source generators cannot modify existing source files — they can only add new generated files.

Instead, PicoWeb.Gen **scans** `MapGet/MapPost` calls with inline lambdas and generates `[assembly: PicoJsonSerializable(...)]` for the return types found in the lambda signatures. The user is responsible for serializing explicitly in their handler.

```csharp
// User writes:
app.MapGet("/api/users/{id}", (UserService svc, int id) =>
{
    var user = await svc.GetByIdAsync(id);
    var bytes = PicoJetson.JsonSerializer.SerializeToUtf8Bytes(user);
    return Results.Json(200, bytes);
});

// PicoWeb.Gen generates:
[assembly: PicoJsonSerializable(typeof(UserDto))]
```

The generator sees `SerializeToUtf8Bytes<UserDto>(user)` in the lambda and registers `UserDto`. Method references (e.g. `app.MapGet("/", SomeMethod)`) are not supported — the SG can only see lambda syntax trees.

### Why no `Results.Json<T>` rewriting

`Results.Json<T>(200, dto)` is a generic wrapper in the `PicoWeb` assembly. PicoJetson.Gen runs in the **user project** and cannot generate serializers for types that only appear as generic arguments in a referenced assembly. The user must call `SerializeToUtf8Bytes<T>(dto)` directly in their project to trigger PicoJetson.Gen.

---

## 3. Pipeline Integration

The `EndpointRegistrar` class is auto-generated and aggregates all controller endpoint registrations. The user explicitly calls it in their startup code after creating `WebApp`:

```csharp
// Generated: EndpointRegistrar.g.cs
public static class EndpointRegistrar
{
    public static void RegisterAll(WebApp app)
    {
        UsersController_Endpoints.Register(app);
    }
}

// User startup code:
var app = new WebApp(container);
EndpointRegistrar.RegisterAll(app);  // Register controller endpoints
app.Build();
```

`MapXX` endpoints need no registration — they are added directly by the user's `app.MapGet` calls in source code.

For `WebApiBuilder` users, the `EndpointRegistrar` call is added automatically inside `WebApiBuilder.Build()` since it knows about the generated registrations.

---

## 4. PicoJsonSerializable Registration Strategy

Register types at the **method signature level only**. No recursive property scanning — nested types are discovered when user code calls `SerializeToUtf8Bytes<NestedType>()` at a call site visible to PicoJetson.Gen.

| Source | Types to Register | Example |
|--------|------------------|---------|
| Return type | Unwrap `Task<T>`, `ValueTask<T>` → register `T` | `Task<UserDto>` → `UserDto` |
| Method parameters | Register POST body DTOs | `CreateUserDto` |
| Generic arguments | Register type arguments | `List<UserDto>` → `UserDto` |

```csharp
void RegisterEndpointTypes(IMethodSymbol method, HashSet<ITypeSymbol> types)
{
    var returnType = UnwrapTask(method.ReturnType);
    AddType(returnType, types);

    foreach (var param in method.Parameters)
    {
        if (IsComplexType(param.Type))
            AddType(param.Type, types);
    }
}

void AddType(ITypeSymbol type, HashSet<ITypeSymbol> types)
{
    if (type is INamedTypeSymbol named && !IsBuiltIn(named))
    {
        types.Add(named.OriginalDefinition ?? named);
        foreach (var arg in named.TypeArguments)
            AddType(arg, types);
    }
}
```

**Why no property recursion:** PicoJetson.Gen handles nested types automatically when `SerializeToUtf8Bytes<UserDto>()` is called in the generated stub. `UserDto.Address` is serialized because the `UserDto` serializer already includes property metadata. Property-level registration is unnecessary and adds compile-time overhead.

---

## 5. Files Created Per Project

| File | Generator | Content |
|------|:---------:|---------|
| `Controllers/{name}_Endpoints.g.cs` | Controllers.Gen | Endpoint stubs for each discovered controller action |
| `PicoJetson_Serializables.g.cs` | Controllers.Gen + PicoWeb.Gen | `[assembly: PicoJsonSerializable(...)]` declarations |
| `EndpointRegistrar.g.cs` | Controllers.Gen | Aggregates controller endpoint registrations |
| `WebApiApp.g.cs` | Controllers.Gen | Partial `WebApiApp` with `EndpointRegistrar.RegisterAll` call (when using WebApiBuilder) |

---

## 6. PicoWeb.csproj Changes

```xml
<!-- PicoWeb.csproj — ensure PicoJetson is transitively available to consumers -->
<ItemGroup>
  <!-- PicoJetson.Gen analyzer is bundled inside PicoJetson package -->
  <PackageReference Include="PicoJetson" Version="2026.2.1" />
</ItemGroup>

<!-- Source generators are packed as analyzers inside the PicoWeb NuGet package -->
<ItemGroup>
  <None Include="analyzers/**" Pack="true" PackagePath="analyzers/" Visible="false" />
</ItemGroup>
```

---

## 7. Test Strategy

| Test | Type | What it verifies |
|------|------|-----------------|
| `Controllers.Gen` generates stubs | Unit (Roslyn Test) | Given a controller source, verify generated output contains correct route + `[PicoJsonSerializable]` |
| `Controllers.Gen` convention routing | Unit | `GetUser` → `GET`, `PostUser` → `POST` |
| `Controllers.Gen` attribute override | Unit | `[HttpGet("{id}")]` overrides convention |
| `PicoWeb.Gen` registers types from MapXX | Unit | Given `MapGet` with lambda returning `UserDto`, verify `[PicoJsonSerializable(typeof(UserDto))]` generated |
| End-to-end AOT publish | Integration | Publish with `PublishAot=true`, run, verify HTTP response |
| Cross-assembly serialization | Integration | User calls `SerializeToUtf8Bytes(dto)` in own project, passes bytes to `Results.Json(200, bytes)` |
