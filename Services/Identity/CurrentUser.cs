using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloudSelfService.Services.Identity;

/// <summary>
/// The signed-in user's record, re-read from the store on every call and never cached: a circuit
/// lives for hours, and a role change or deactivation has to reach it on the next request.
/// </summary>
public sealed class CurrentUser
{
    private readonly AuthenticationStateProvider _auth;
    private readonly IUserStore _users;

    public CurrentUser(AuthenticationStateProvider auth, IUserStore users)
    {
        _auth = auth;
        _users = users;
    }

    public async Task<AppUser?> GetAsync(CancellationToken ct = default)
    {
        var state = await _auth.GetAuthenticationStateAsync();
        return AppClaims.UserIdOf(state.User) is { } id ? await _users.GetAsync(id, ct) : null;
    }
}
