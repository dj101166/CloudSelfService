using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace CloudSelfService.Authorization;

/// <summary>
/// Re-checks a live circuit's principal on a timer. The cookie check runs only on HTTP requests,
/// and an interactive circuit makes almost none, so without this a deactivated user keeps a working
/// circuit until they navigate. When it returns false the circuit's state becomes anonymous and the
/// router sends the user to /login - an interactive render, where that navigation is safe.
/// </summary>
public sealed class SessionRevalidatingStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeSpan _interval;

    public SessionRevalidatingStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopes, IConfiguration config)
        : base(loggerFactory)
    {
        _scopes = scopes;
        _interval = TimeSpan.FromSeconds(Math.Max(1, config.GetValue<int?>("Auth:RevalidateSeconds") ?? 60));
    }

    protected override TimeSpan RevalidationInterval => _interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await SessionValidator.ValidateAsync(authenticationState.User, sp.GetRequiredService<IUserStore>(),
            sp.GetRequiredService<ICustomerStore>(), sp.GetRequiredService<TimeProvider>().GetUtcNow(),
            sp.GetRequiredService<SessionLifetime>(), cancellationToken);
        return result.Valid;
    }

    internal Task<bool> ValidateForTestAsync(AuthenticationState state) =>
        ValidateAuthenticationStateAsync(state, CancellationToken.None);
}
