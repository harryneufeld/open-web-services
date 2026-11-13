using Microsoft.Extensions.DependencyInjection;
using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Background;

internal static class SystemScopes
{
    /// <summary>A scope for background work that acts for the platform across all tenants.</summary>
    public static AsyncServiceScope CreateSystemScope(this IServiceScopeFactory scopes)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AccessContext>().BecomeSystem();
        return scope;
    }
}
