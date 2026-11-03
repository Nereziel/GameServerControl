using System.Security.Claims;
using System.Text;
using GameServerControl.Agent.Auth;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class RoleEnforcementMiddlewareTests
{
    [Theory]
    [InlineData("post")]
    [InlineData("PuT")]
    [InlineData("delete")]
    [InlineData("pAtCh")]
    public async Task ReadOnly_mixed_case_write_methods_are_forbidden(string method)
    {
        var nextCalled = false;
        var middleware = new RoleEnforcementMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateReadOnlyContext(method, "/api/servers/reload");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task ReadOnly_trailing_slash_raw_file_content_is_forbidden()
    {
        var nextCalled = false;
        var middleware = new RoleEnforcementMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateReadOnlyContext(HttpMethods.Get, "/api/servers/alpha/files/content/");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task ReadOnly_trailing_slash_config_redacts_password_values()
    {
        var middleware = new RoleEnforcementMiddleware(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("{\"schema\":{\"sections\":[{\"fields\":[{\"type\":\"Password\",\"key\":\"AdminPassword\"}]}]},\"values\":{\"AdminPassword\":\"top-secret\",\"Visible\":\"ok\"}}");
        });
        var context = CreateReadOnlyContext(HttpMethods.Get, "/api/servers/alpha/config/");

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.DoesNotContain("top-secret", body, StringComparison.Ordinal);
        Assert.Contains("\"secretValues\":{\"AdminPassword\":true}", body, StringComparison.Ordinal);
        Assert.Contains("\"Visible\":\"ok\"", body, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateReadOnlyContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TokenAuthHandler.RoleClaim, nameof(TokenRole.ReadOnly))],
            authenticationType: "test"));
        return context;
    }
}
