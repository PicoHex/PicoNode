// ── Pattern 2: Route attribute override + HttpGet with path ──
//
// Discovery:
//   File:     Controllers/ProductsController.cs          ← /Controllers/ 目录
//   Verb:     Get* prefix                                ← GET
//   Route:    [Route("api/v2/products")]                 ← 类级特性覆盖，不再用 /api/products
//             [HttpGet("{category}/{id}")]               ← 方法级特性覆盖，不再从方法名推导
//   Result:   GET /api/v2/products/{category}/{id}
//
// Note: The [Route] attribute on the class replaces the default /api/products prefix.
//       The [HttpGet("...")] on the method replaces the method-name-based derivation.

// Route/HttpGet attributes used by Controllers.Gen (matched by class name).
// The generator checks AttributeClass.Name, not namespace, so these minimal
// class definitions are sufficient.

namespace PicoWeb.Samples.Controllers;

// Attribute stubs for Controllers.Gen (matched by class name, not namespace).
// The generator checks AttributeClass.Name, so class name is all that matters.
public class RouteAttribute : Attribute
{
    public RouteAttribute(string path)
    {
        Path = path;
    }

    public string Path { get; }
}

public class HttpGetAttribute : Attribute
{
    public HttpGetAttribute(string path)
    {
        Path = path;
    }

    public string Path { get; }
}

[Route("/api/v2/products")]
public class ProductsController
{
    [HttpGet("{category}/{id}")]
    public ProductDto GetProduct(string category, int id) =>
        new ProductDto
        {
            Id = id,
            Category = category,
            Name = $"{category} #{id}",
        };
}

[PicoJsonSerializable]
public class ProductDto
{
    public int Id { get; set; }
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
}
