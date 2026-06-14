// Controllers/UsersController.cs
//
// This file is auto-discovered by Controllers.Gen source generator.
// Convention: Get* prefix → GET /api/users/{id}
// Method parameter `int id` → route parameter {id}
// The controller must be registered in DI (see Program.cs).

namespace PicoWeb.Samples.Controllers;

public class UsersController
{
    public string GetUser(int id) => $"User {id}";
}
