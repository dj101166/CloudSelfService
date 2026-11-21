using System.Reflection;
using CloudSelfService.Services.Data;
using Xunit;

namespace CloudSelfService.Tests;

public class PgSchemaTests
{
    [Fact]
    public void SchemaSql_HasOwnerTrigger()
    {
        Assert.Contains("grants_owner_check", PgsqlContext.SchemaSql);
    }

    [Theory]
    [InlineData("audit_events_append_only")]
    [InlineData("audit_events_no_change")]
    [InlineData("audit_events_no_truncate")]
    public void SchemaSql_HasAuditAppendOnlyObjects(string name) =>
        Assert.Contains(name, PgsqlContext.SchemaSql);

    [Fact]
    public void AuditStoreSql_NoUpdateOrDelete()
    {
        var sql = typeof(PgAuditStore)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(sql);
        foreach (var s in sql)
        {
            Assert.DoesNotContain("UPDATE", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", s, StringComparison.OrdinalIgnoreCase);
        }
    }
}
