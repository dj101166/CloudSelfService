using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Files;
using CloudSelfService.Services.Identity;
using Microsoft.AspNetCore.Components;

namespace CloudSelfService.Shared;

/// <summary>
/// What the staff admin pages share: the signed-in user, the audit context for the circuit, and the places a store
/// failure must be caught. A database error becomes an inline alert and never ends the circuit.
/// </summary>
/// <remarks>
/// A page's policy only runs on navigation, so a role dropped while a circuit is open is not seen by it. Every read
/// and reload therefore goes through <see cref="StillAuthorizedAsync"/>, which re-reads the user; when it fails the page
/// shows Access Denied in place (never a navigation, which after an await crashes the host).
/// </remarks>
public abstract class AdminPageBase : ComponentBase
{
    public const string RefreshFailedSuffix = " The list could not be refreshed; reload the page.";

    [Inject] protected CurrentUser CurrentUser { get; set; } = default!;
    [Inject] protected OwnerConfig Owners { get; set; } = default!;
    [Inject] protected PageAuditContext PageAudit { get; set; } = default!;
    [Inject] protected AdminService Admin { get; set; } = default!;
    [Inject] protected ILoggerFactory Loggers { get; set; } = default!;

    /// <summary>The least role this page may serve.</summary>
    protected virtual StaffRole RequiredRole => StaffRole.StaffAdmin;

    protected AdminAlert? Alert { get; set; }
    protected bool Loaded { get; set; }
    protected bool Busy { get; set; }

    /// <summary>The signed-in role no longer reaches this page. Rendered as Access Denied in place.</summary>
    protected bool Denied { get; set; }

    /// <summary>The session no longer validates.</summary>
    protected bool SessionEnded { get; set; }

    /// <summary>
    /// Bumped after every reload. Keying a form control on it re-creates the element, so a dropdown whose change was
    /// refused shows the stored value again (Blazor does not re-send an attribute whose rendered value did not change).
    /// </summary>
    protected int Version { get; private set; }

    /// <summary>Re-reads the user; false (and the matching state set) when the session or role no longer qualifies.</summary>
    protected async Task<bool> StillAuthorizedAsync()
    {
        var user = await this.CurrentUser.GetAsync();
        if (user is null)
        {
            this.SessionEnded = true;
            this.Denied = false;
            return false;
        }
        this.SessionEnded = false;
        var ok = StaffAccess.Reaches(user, this.Owners, this.RequiredRole);
        this.Denied = !ok;
        return ok;
    }

    /// <summary>Called when authorization fails, so data the new role must not see is not left on screen.</summary>
    protected virtual void ClearData() => this.Loaded = false;

    /// <summary>
    /// Role check, then the work, with a store failure turned into the unavailable alert. Returns whether the work ran
    /// to completion.
    /// </summary>
    protected async Task<bool> RunGuardedAsync(Func<Task> work)
    {
        try
        {
            if (!await this.StillAuthorizedAsync())
            {
                this.ClearData();
                return false;
            }
            await work();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.Loggers.CreateLogger(this.GetType().FullName!).LogError(ex, "{Page} failed.", this.GetType().Name);
            this.Alert = AdminAlert.StoreDown();
            return false;
        }
    }

    protected async Task LoadGuardedAsync(Func<Task> load)
    {
        if (await this.RunGuardedAsync(load)) this.Loaded = true;
    }

    /// <summary>
    /// Runs one change as the signed-in user. The service re-checks the role itself; the page then re-checks it before
    /// reloading, so a refused action leaves the screen showing the truth and never reads data the user may no longer see.
    /// </summary>
    protected async Task ActAsync(Func<Guid, Models.AuditContext, Task<AdminAlert>> action, Func<Task> reload)
    {
        if (this.Busy) return;
        this.Busy = true;
        try
        {
            AppUser? user;
            try
            {
                user = await this.CurrentUser.GetAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.Loggers.CreateLogger(this.GetType().FullName!).LogError(ex, "Reading the user on {Page} failed.", this.GetType().Name);
                this.Alert = AdminAlert.StoreDown();
                return;
            }
            if (user is null)
            {
                this.SessionEnded = true;
                this.ClearData();
                return;
            }

            try
            {
                this.Alert = await action(user.Id, this.PageAudit.Next());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.Loggers.CreateLogger(this.GetType().FullName!).LogError(ex, "An admin action on {Page} failed.", this.GetType().Name);
                this.Alert = AdminAlert.StoreDown();
                return;
            }

            var actionAlert = this.Alert;
            if (!await this.RunGuardedAsync(reload) && !this.Denied && !this.SessionEnded && actionAlert is not null)
                this.Alert = actionAlert with { Text = actionAlert.Text + RefreshFailedSuffix };
        }
        finally
        {
            this.Version++;
            this.Busy = false;
        }
    }
}
