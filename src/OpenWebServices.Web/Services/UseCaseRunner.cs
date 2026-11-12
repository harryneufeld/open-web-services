namespace OpenWebServices.Web.Services;

/// <summary>
/// Runs a use case in its own DI scope. Blazor Server scopes live as long as a browser circuit, which would
/// keep one DbContext (and stale tracked entities) for the whole session; a scope per call avoids that.
/// </summary>
public sealed class UseCaseRunner(IServiceScopeFactory scopes)
{
    public async Task<TResult> RunAsync<TUseCase, TResult>(Func<TUseCase, Task<TResult>> action)
        where TUseCase : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TUseCase>());
    }

    public async Task RunAsync<TUseCase>(Func<TUseCase, Task> action)
        where TUseCase : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TUseCase>());
    }
}
