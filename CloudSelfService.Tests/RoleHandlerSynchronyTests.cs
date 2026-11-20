using System.Security.Claims;
using CloudSelfService.Authorization;
using CloudSelfService.Models;
using CloudSelfService.Services.Data;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// Pins the unidentified path through <see cref="AppRoleHandler"/> as synchronous.
/// <para>
/// This is coupled to the router's NotAuthorized branch in a way nothing else states. When the
/// handler completes synchronously for a principal with no user id, the fragment renders on the
/// renderer's first non-streaming pass, where a navigation becomes a real 302. Put ANY await
/// above that early return - a log line, a cache read, an audit write - and the anonymous path
/// renders on an async completion instead, where a navigation recurses and kills the host
/// process. That edit would be made in the handler's file, which never mentions the router.
/// </para>
/// <para>See NotAuthorizedRoutingTests for the other end of this coupling.</para>
/// </summary>
public class RoleHandlerSynchronyTests
{
    [Fact]
    public void Handle_NoUserIdClaim_ReturnsCompletedTask()
    {
        var handler = AppRoleHandlerTests.HandlerFor(new InMemoryUserStore(), new InMemoryCustomerStore());
        var context = AppRoleHandlerTests.ContextFor(new ClaimsPrincipal(new ClaimsIdentity()), StaffRole.Staff);

        Task task = handler.HandleAsync(context);

        // Already completed - NOT merely "completes eventually". If this fails, an await was
        // added above the early return and the host-killing crash is re-armed.
        Assert.True(task.IsCompleted);
        Assert.False(context.HasSucceeded);
    }
}
