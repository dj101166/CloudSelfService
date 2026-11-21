using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace CloudSelfService.Shared;

/// <summary>The sidebar's Files link: active on the Files home and anywhere inside the file browser.</summary>
public sealed class FilesNavLink : NavLink
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    protected override bool ShouldMatch(string uriAbsolute)
    {
        var relative = this.Navigation.ToBaseRelativePath(uriAbsolute).Split('?', '#')[0];
        return relative.Length == 0 || relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase);
    }
}
