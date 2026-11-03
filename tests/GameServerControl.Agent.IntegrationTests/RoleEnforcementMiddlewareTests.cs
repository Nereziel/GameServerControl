using System.Security.Claims;
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
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = "/api/servers/reload";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TokenAuthHandler.RoleClaim, nameof(TokenRole.ReadOnly))],
            authenticationType: "test"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }
}
