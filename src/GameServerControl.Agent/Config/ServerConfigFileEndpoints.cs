namespace GameServerControl.Agent.Config;

public sealed record ServerConfigFileValidationRequest(string Path, string Content);

public static class ServerConfigFileEndpoints
{
    public static RouteGroupBuilder MapServerConfigFiles(this RouteGroupBuilder api)
    {
        api.MapGet("/servers/{id}/files", async (string id, ServerConfigFileService files, CancellationToken ct) =>
        {
            try { return Results.Ok(await files.ListAsync(id, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Server not found." }); }
            catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapGet("/servers/{id}/files/content", async (string id, string path, ServerConfigFileService files, CancellationToken ct) =>
        {
            try { return Results.Ok(await files.ReadAsync(id, path, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Server not found." }); }
            catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or DirectoryNotFoundException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/servers/{id}/files/validate", (string id, ServerConfigFileValidationRequest request, ServerRegistry registry) =>
        {
            if (registry.Get(id) is null) return Results.NotFound(new { error = "Server not found." });
            try
            {
                ServerConfigSyntaxValidator.Validate(request.Path, request.Content ?? string.Empty);
                return Results.Ok(new { valid = true });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { valid = false, error = ex.Message });
            }
        });

        api.MapPut("/servers/{id}/files/content", async (string id, ServerConfigFileWriteRequest request, ServerConfigFileService files, CancellationToken ct) =>
        {
            try
            {
                ServerConfigSyntaxValidator.Validate(request.Path, request.Content ?? string.Empty);
                return Results.Ok(await files.WriteAsync(id, request, ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Server not found." }); }
            catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ServerConfigFileConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or DirectoryNotFoundException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization("admin");

        api.MapGet("/servers/{id}/files/history", async (string id, string path, ServerConfigFileService files, CancellationToken ct) =>
        {
            try { return Results.Ok(await files.HistoryAsync(id, path, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Server not found." }); }
            catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or DirectoryNotFoundException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/servers/{id}/files/restore", async (string id, ServerConfigFileRestoreRequest request, ServerConfigFileService files, CancellationToken ct) =>
        {
            try { return Results.Ok(await files.RestoreAsync(id, request, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Server not found." }); }
            catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ServerConfigFileConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or DirectoryNotFoundException)
            { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization("admin");

        return api;
    }
}
