using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Way2.Todo.Api.Identity;
using Way2.Todo.Api.Infrastructure;

namespace Way2.Todo.Api.Tasks;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        // Leitura auxiliar (diagnóstico/demonstração de isolamento). O cliente offline usa push/pull (Fase B).
        app.MapGet("/api/tasks", ListActiveAsync).RequireAuthorization();
        return app;
    }

    private static async Task<List<TaskSnapshot>> ListActiveAsync(
        ClaimsPrincipal principal, TodoDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.UserId == userId && t.DeletedAt == null)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToListAsync(ct);
        return tasks.ConvertAll(TaskSnapshot.From);
    }
}
