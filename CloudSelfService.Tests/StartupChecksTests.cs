using System.Security.Claims;
using CloudSelfService.Endpoints;
using CloudSelfService.Models;
using CloudSelfService.Services;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Identity;
using CloudSelfService.Services.Transfers;
using CloudSelfService.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSelfService.Tests;

public class StartupChecksTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UsePostgres_NoConnectionStringOutsideDevelopment_Throws(string? cs)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StartupChecks.UsePostgres(isDevelopment: false, cs));

        Assert.Equal("ConnectionStrings:Default is required outside Development.", ex.Message);
    }

    [Fact]
    public void UsePostgres_NoConnectionStringInDevelopment_InMemory() =>
        Assert.False(StartupChecks.UsePostgres(isDevelopment: true, null));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UsePostgres_ConnectionStringSet_Postgres(bool isDevelopment) =>
        Assert.True(StartupChecks.UsePostgres(isDevelopment, "Host=db;Database=x"));

    [Fact]
    public void Program_ChoosesStoresThroughTheGuard()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
        var program = File.ReadAllText(Path.Combine(dir!.FullName, "Program.cs"));

        Assert.Contains("StartupChecks.UsePostgres(builder.Environment.IsDevelopment(), builder.Configuration.GetConnectionString(\"Default\"))", program);
        Assert.Contains("StartupChecks.OwnersWarning(", program);
        Assert.Contains("UploadSessionService.MaxUploadBytesFrom(", program);
    }

    [Fact]
    public void DevelopmentFactory_WithoutConnectionString_StillStarts()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateAnonymousClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void OwnersWarning_NoneOutsideDevelopment_Warns() =>
        Assert.Equal("No Owners configured; nobody can assign staff roles.",
            StartupChecks.OwnersWarning(isDevelopment: false, new OwnerConfig("")));

    [Fact]
    public void OwnersWarning_ConfiguredOrDevelopment_Silent()
    {
        Assert.Null(StartupChecks.OwnersWarning(isDevelopment: false, new OwnerConfig("a@advancedutility.com")));
        Assert.Null(StartupChecks.OwnersWarning(isDevelopment: true, new OwnerConfig("")));
    }

    [Theory]
    [InlineData(null, UploadSessionService.DefaultMaxUploadBytes, UploadSessionService.DefaultMaxUploadBytes)]
    [InlineData("1000", 1000L, 1000L)]
    [InlineData("-5", 0L, 0L)]
    [InlineData("209715200001", 209_715_200_001L, 209_715_200_001L)]
    [InlineData("1677721600000", 1_677_721_600_000L, 1_677_721_600_000L)]
    public void MaxUploadBytesFrom_WithinCeiling_Unchanged(string? configured, long effective, long reported)
    {
        var (e, c) = UploadSessionService.MaxUploadBytesFrom(Config(configured));

        Assert.Equal(effective, e);
        Assert.Equal(reported, c);
    }

    [Fact]
    public void MaxUploadBytesFrom_AboveCeiling_Clamped()
    {
        var over = ChunkMath.MaxBlobBytes + 1;

        var (e, c) = UploadSessionService.MaxUploadBytesFrom(Config(over.ToString()));

        Assert.Equal(ChunkMath.MaxBlobBytes, e);
        Assert.Equal(over, c);
    }

    private static IConfiguration Config(string? maxUploadBytes) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transfers:MaxUploadBytes"] = maxUploadBytes,
        }).Build();
}

public class RefusedTokenAuditTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "oidc"));

    [Fact]
    public void RefusedToken_WrongTenant_DeniedSignInRow()
    {
        var p = Principal(("oid", "o1"), ("tid", "11111111-1111-1111-1111-111111111111"), ("preferred_username", "eve@other.example"));

        var e = AuthEndpoints.RefusedTokenEvent(p, AuthEndpoints.WrongTenantReason, new AuditContext("203.0.113.9", "corr"), At);

        Assert.Equal(IdentityResolver.SignInOperation, e.Operation);
        Assert.Equal(AuditResult.Denied, e.Result);
        Assert.Equal("eve@other.example", e.UserDisplay);
        Assert.Null(e.UserId);
        Assert.Equal("203.0.113.9", e.SourceIp);
        Assert.Equal("corr", e.CorrelationId);
        Assert.StartsWith(AuthEndpoints.WrongTenantReason, e.Error);
        Assert.Contains("11111111-1111-1111-1111-111111111111", e.Error);
        Assert.Equal(At, e.At);
    }

    [Fact]
    public void RefusedToken_MissingOid_FallsBackToEmail()
    {
        var p = Principal(("email", " eve@other.example "));

        var e = AuthEndpoints.RefusedTokenEvent(p, AuthEndpoints.MissingOidReason, new AuditContext("", ""), At);

        Assert.Equal("eve@other.example", e.UserDisplay);
        Assert.Equal(AuthEndpoints.MissingOidReason, e.Error);
        Assert.Equal(AuditResult.Denied, e.Result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnTokenValidated_RefusedBeforeAdmission_WritesDeniedSignIn(bool missingOid)
    {
        const string ourTenant = "22222222-2222-2222-2222-222222222222";
        var store = new InMemoryAuditStore();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Entra:TenantId"] = ourTenant,
        }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .AddSingleton(new AuditService(store, NullLogger<AuditService>.Instance))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        var principal = missingOid
            ? Principal(("tid", ourTenant), ("preferred_username", "eve@other.example"))
            : Principal(("oid", "o1"), ("tid", "33333333-3333-3333-3333-333333333333"), ("preferred_username", "eve@other.example"));
        var scheme = new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler));
        var ctx = new TokenValidatedContext(http, scheme, new OpenIdConnectOptions(), principal, new AuthenticationProperties());

        await AuthEndpoints.OnTokenValidatedAsync(ctx);

        var row = Assert.Single(await store.QueryAsync(new AuditQuery()));
        Assert.Equal(IdentityResolver.SignInOperation, row.Operation);
        Assert.Equal(AuditResult.Denied, row.Result);
        Assert.Equal("eve@other.example", row.UserDisplay);
        Assert.Equal("203.0.113.7", row.SourceIp);
        Assert.StartsWith(missingOid ? AuthEndpoints.MissingOidReason : AuthEndpoints.WrongTenantReason, row.Error);
        Assert.Equal("/access-denied?reason=unknown", http.Response.Headers.Location.ToString());
    }

    [Fact]
    public void RefusedToken_NoPrincipal_StillWritesARow()
    {
        var e = AuthEndpoints.RefusedTokenEvent(null, AuthEndpoints.MissingOidReason, new AuditContext("198.51.100.1", "c"), At);

        Assert.Equal("", e.UserDisplay);
        Assert.Equal("198.51.100.1", e.SourceIp);
    }
}
