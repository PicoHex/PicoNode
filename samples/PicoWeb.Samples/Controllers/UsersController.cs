// ── Pattern 1: Convention-based (file path + method prefix) ──
//
// Discovery:
//   File:     Controllers/UsersController.cs            ← /Controllers/ 目录
//   Verb:     Get* prefix                                ← GET
//   Route:    UsersController → /api/users              ← 类名去 Controller 后缀
//             GetUser(int id) → /user/{id}               ← 方法名去 Get 前缀，int 参数展开为 {id}
//   Result:   GET /api/users/user/{id}
//
// DTO must be marked [PicoJsonSerializable] for AOT serialization.

namespace PicoWeb.Samples.Controllers;

public class UsersController
{
    public UserDto GetUser(int id) => new UserDto { Id = id, Name = $"User {id}" };
}

[PicoJetson.PicoJsonSerializable]
public class UserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
