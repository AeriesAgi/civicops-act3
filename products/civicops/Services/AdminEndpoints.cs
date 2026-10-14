using CivicOps.Models;
using Microsoft.AspNetCore.Mvc;

namespace CivicOps.Services;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapCivicOpsAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin").RequireAuthorization("admin").RequireRateLimiting("dispatch");
        group.MapGet("/overview", async (TenantStore tenants, BootstrapUserStore users, WorkforceStore workforce, CatalogueAdminStore catalogues, CancellationToken ct) => Results.Ok(new
        {
            tenant = await tenants.GetAsync(TenantContext.CurrentId, ct), tenants = await tenants.ListAsync(ct),
            users = await users.ListAsync(TenantContext.CurrentId, ct), crews = await workforce.ListCrewsAsync(ct), workers = await workforce.ListWorkersAsync(ct),
            catalogue = CatalogueAdminStore.View(await catalogues.GetAsync(ct))
        }));
        group.MapGet("/tenants", async (TenantStore tenants, CancellationToken ct) => Results.Ok(await tenants.ListAsync(ct)));
        group.MapPost("/tenants", async (Tenant tenant, HttpContext http, TenantStore tenants, CancellationToken ct) =>
        {
            if (TenantContext.CurrentId != TenantStore.DefaultTenantId) return Results.StatusCode(403);
            if (await tenants.GetAsync(tenant.Id, ct) is not null) return Results.Conflict(new { error = "Municipality already exists." });
            return await Mutation(async () => Results.Ok(await tenants.SaveAsync(tenant, Actor(http), ct)));
        });
        group.MapPut("/tenants/{id}", async (string id, Tenant tenant, HttpContext http, TenantStore tenants, CancellationToken ct) =>
        {
            if (id != TenantContext.CurrentId) return Results.NotFound();
            if (id != tenant.Id) return Results.BadRequest(new { error = "Path and record municipality ids must match." });
            return await Mutation(async () => Results.Ok(await tenants.SaveAsync(tenant, Actor(http), ct)));
        });
        group.MapGet("/catalogue", async (CatalogueAdminStore store, CancellationToken ct) => Results.Ok(CatalogueAdminStore.View(await store.GetAsync(ct))));
        group.MapPut("/catalogue", async (CatalogueEdit edit, HttpContext http, CatalogueAdminStore store, CancellationToken ct) =>
            await Mutation(async () => Results.Ok(CatalogueAdminStore.View(await store.EditAsync(edit.ExpectedVersion, edit.Catalogue, Actor(http), ct)))))
            .WithMetadata(new RequestSizeLimitAttribute(512 * 1024));
        group.MapGet("/crews", async (WorkforceStore workforce, CancellationToken ct) => Results.Ok(await workforce.ListCrewsAsync(ct)));
        group.MapGet("/workers", async (WorkforceStore workforce, CancellationToken ct) => Results.Ok(await workforce.ListWorkersAsync(ct)));
        group.MapPost("/crews", async (Crew crew, HttpContext http, WorkforceStore workforce, CancellationToken ct) =>
            await Mutation(async () => (await workforce.ListCrewsAsync(ct)).Any(value => value.Id == crew.Id)
                ? Results.Conflict(new { error = "Crew already exists. Edit the existing record." })
                : Results.Ok(await workforce.UpsertCrewAsync(crew, Actor(http), ct))));
        group.MapPut("/crews/{id}", async (string id, Crew crew, HttpContext http, WorkforceStore workforce, CancellationToken ct) =>
            id != crew.Id ? Results.BadRequest(new { error = "Path and record crew ids must match." })
                : !(await workforce.ListCrewsAsync(ct)).Any(value => value.Id == id) ? Results.NotFound()
                : await Mutation(async () => Results.Ok(await workforce.UpsertCrewAsync(crew, Actor(http), ct))));
        group.MapPost("/workers", async (Worker worker, HttpContext http, WorkforceStore workforce, CancellationToken ct) =>
            await Mutation(async () => await workforce.GetWorkerAsync(worker.Id, ct) is not null
                ? Results.Conflict(new { error = "Worker already exists. Edit the existing record." })
                : Results.Ok(await workforce.UpsertWorkerAsync(worker, Actor(http), ct))));
        group.MapPut("/workers/{id}", async (string id, Worker worker, HttpContext http, WorkforceStore workforce, CancellationToken ct) =>
            id != worker.Id ? Results.BadRequest(new { error = "Path and record worker ids must match." })
                : await workforce.GetWorkerAsync(id, ct) is null ? Results.NotFound()
                : await Mutation(async () => Results.Ok(await workforce.UpsertWorkerAsync(worker, Actor(http), ct))));
        return endpoints;
    }
    private static string Actor(HttpContext http) => http.User.Identity?.Name ?? "Authenticated administrator";
    private static async Task<IResult> Mutation(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (CatalogueConflictException error) { return Results.Conflict(new { error = error.Message }); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        { return Results.BadRequest(new { error = error.Message }); }
    }
}
