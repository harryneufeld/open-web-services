using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using OpenWebServices.Application.Tenants;

namespace OpenWebServices.Web.Services;

/// <summary>
/// Runs a use case in its own DI scope, acting as the signed-in user. Blazor Server scopes live as long as a browser
/// circuit, which would keep one DbContext (and stale tracked entities) for the whole session; a scope per call
/// avoids that and re-reads the user's access (tenant, role, admin) from the database every time.
/// </summary>
public sealed class UseCaseRunner(IServiceScopeFactory scopes, AuthenticationStateProvider authenticationState)
{
    public async Task<TResult> RunAsync<TUseCase, TResult>(Func<TUseCase, Task<TResult>> action)
        where TUseCase : notnull
    {
        await using var scope = await AccessScopes.CreateAsync(scopes, await UserAsync());
        return await action(scope.ServiceProvider.GetRequiredService<TUseCase>());
    }

    public async Task RunAsync<TUseCase>(Func<TUseCase, Task> action)
        where TUseCase : notnull
    {
        await using var scope = await AccessScopes.CreateAsync(scopes, await UserAsync());
        await action(scope.ServiceProvider.GetRequiredService<TUseCase>());
    }

    private async Task<ClaimsPrincipal> UserAsync() => (await authenticationState.GetAuthenticationStateAsync()).User;
}

public static class AccessScopes
{
    /// <summary>A new scope whose <see cref="OpenWebServices.Application.Abstractions.AccessContext"/> is the given user's.</summary>
    public static async Task<AsyncServiceScope> CreateAsync(IServiceScopeFactory scopes, ClaimsPrincipal user, CancellationToken ct = default)
    {
        var scope = scopes.CreateAsyncScope();
        try
        {
            if (Sessions.UserId(user) is { } userId)
                await scope.ServiceProvider.GetRequiredService<ResolveAccess>().ExecuteAsync(userId, Sessions.IsStrong(user), ct);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs a use case from a minimal API endpoint as the request's user.</summary>
    public static async Task<TResult> RunAsync<TUseCase, TResult>(this HttpContext http, Func<TUseCase, Task<TResult>> action)
        where TUseCase : notnull
    {
        await using var scope = await CreateAsync(
            http.RequestServices.GetRequiredService<IServiceScopeFactory>(), http.User, http.RequestAborted);
        return await action(scope.ServiceProvider.GetRequiredService<TUseCase>());
    }

    public static Task RunAsync<TUseCase>(this HttpContext http, Func<TUseCase, Task> action)
        where TUseCase : notnull =>
        http.RunAsync<TUseCase, bool>(async u =>
        {
            await action(u);
            return true;
        });
}

public static class Sessions
{
    /// <summary>The "amr" claim Identity sets: "pwd" for a password, "mfa" with an authenticator code, "passkey".</summary>
    public const string MethodClaim = "amr";

    public static Guid? UserId(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;

    /// <summary>The session was established with a second factor.</summary>
    public static bool IsStrong(ClaimsPrincipal user) =>
        user.FindAll(MethodClaim).Any(c => c.Value is "mfa" or "passkey");
}
