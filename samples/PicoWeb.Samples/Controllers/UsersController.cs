// Controllers/UsersController.cs
//
// This file is auto-discovered by Controllers.Gen source generator.
// Convention: Get* prefix → GET /api/users/{id}
// Method parameter `int id` → route parameter {id}
// The controller must be registered in DI (see Program.cs).

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
