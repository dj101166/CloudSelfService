using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloudSelfService.Services.Identity;

/// <summary>
/// The signed-in user's record, re-validated against the store on every call and never cached: a
/// circuit lives for hours, and a deactivation has to reach it on the next request. Null for any
/// session <see cref="SessionValidator"/> would refuse, so a page cannot render for one.
/// </summary>
public sealed class CurrentUser
{
    private readonly AuthenticationStateProvider _auth;
    private readonly IUserStore _users;
    private readonly ICustomerStore _customers;
    private readonly SessionLifetime _options;
    private readonly TimeProvider _time;

    public CurrentUser(AuthenticationStateProvider auth, IUserStore users, ICustomerStore customers,
        IConfiguration config, TimeProvider time)
    {
        _auth = auth;
        _users = users;
        _customers = customers;
        _options = SessionLifetime.FromConfiguration(config);
        _time = time;
    }

    public async Task<AppUser?> GetAsync(CancellationToken ct = default)
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var result = await SessionValidator.ValidateAsync(state.User, _users, _customers, _time.GetUtcNow(), _options, ct);
        return result.Valid ? result.User : null;
    }
}
