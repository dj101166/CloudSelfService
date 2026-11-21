using CloudSelfService.Services;
using CloudSelfService.Services.Files;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Components;

namespace CloudSelfService.Shared;

/// <summary>
/// What the staff admin pages share: the signed-in user, the audit context for the circuit, and the two places a store
/// failure must be caught. A database error becomes an inline alert and never ends the circuit.
/// </summary>
public abstract class AdminPageBase : ComponentBase
{
    [Inject] protected CurrentUser CurrentUser { get; set; } = default!;
    [Inject] protected PageAuditContext PageAudit { get; set; } = default!;
    [Inject] protected AdminService Admin { get; set; } = default!;
    [Inject] protected ILoggerFactory Loggers { get; set; } = default!;

    protected AdminAlert? Alert { get; set; }
    protected bool Loaded { get; set; }
    protected bool Busy { get; set; }

    /// <summary>Runs the page's data load; a failure shows the unavailable alert instead of the page.</summary>
    protected async Task LoadGuardedAsync(Func<Task> load)
    {
        try
        {
            await load();
            this.Loaded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.Loggers.CreateLogger(this.GetType().FullName!).LogError(ex, "Loading {Page} failed.", this.GetType().Name);
            this.Alert = AdminAlert.StoreDown();
        }
    }

    /// <summary>
    /// Runs one change as the signed-in user. The service re-checks the role itself; the page then reloads whatever the
    /// outcome, so a refused or stale action leaves the screen showing the truth.
    /// </summary>
    protected async Task ActAsync(Func<Guid, Models.AuditContext, Task<AdminAlert>> action, Func<Task> reload)
    {
        if (this.Busy) return;
        this.Busy = true;
        try
        {
            var user = await this.CurrentUser.GetAsync();
            this.Alert = user is null
                ? AdminAlert.StoreDown()
                : await action(user.Id, this.PageAudit.Next());
            await reload();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.Loggers.CreateLogger(this.GetType().FullName!).LogError(ex, "An admin action on {Page} failed.", this.GetType().Name);
            this.Alert = AdminAlert.StoreDown();
        }
        finally
        {
            this.Busy = false;
        }
    }
}
