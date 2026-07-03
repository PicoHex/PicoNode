// ── Pattern 3: HTTP verb attributes + mixed route ──
//
// Discovery:
//   File:     Controllers/PostsController.cs              ← /Controllers/ 目录
//   Verb:     [HttpPost], [HttpDelete], [HttpPatch]       ← 特性指定，方法名前缀失效
//   Route:    PostsController → /api/posts                ← 默认约定
//             Post* method name → /post                   ← 方法名去前缀
//             GetPosts() → /posts                         ← 无参数
//             [HttpPost("{id}/restore")]                   ← 特性覆盖路径
//   Result:   POST /api/posts
//             DELETE /api/posts/{id}
//             PATCH /api/posts/{id}
//             POST /api/posts/{id}/restore
//
// Note: When an HTTP verb attribute is present, the method name prefix is ignored
//       for verb detection but still used for route derivation.

namespace PicoWeb.Samples.Controllers;

public class HttpPostAttribute : Attribute
{
    public HttpPostAttribute(string? path = null)
    {
        Path = path;
    }

    public string? Path { get; }
}

public class HttpDeleteAttribute : Attribute
{
    public HttpDeleteAttribute(string? path = null)
    {
        Path = path;
    }

    public string? Path { get; }
}

public class HttpPatchAttribute : Attribute
{
    public HttpPatchAttribute(string? path = null)
    {
        Path = path;
    }

    public string? Path { get; }
}

public class PostsController
{
    // Convention: Get* → GET /api/posts/list
    public PostDto GetList() => new PostDto { Id = 0, Title = "All posts" };

    // [HttpPost] → POST /api/posts
    // Method name "Create" has no prefix match → full name kebab: /create
    // No route params (title is complex/body-type, not simple URL param)
    [HttpPost]
    public PostDto Create(string title) => new PostDto { Id = 42, Title = title };

    // [HttpDelete] → DELETE /api/posts/{id}
    [HttpDelete]
    public void Delete(int id) { }

    // [HttpPatch] → PATCH /api/posts/{id}
    [HttpPatch]
    public PostDto Patch(int id) => new PostDto { Id = id, Title = "patched" };

    // [HttpPost("{id}/restore")] → POST /api/posts/{id}/restore
    [HttpPost("{id}/restore")]
    public PostDto Restore(int id) => new PostDto { Id = id, Title = "restored" };
}

[PicoJsonSerializable]
public class PostDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
}
