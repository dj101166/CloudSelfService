using System.Reflection;
using CloudSelfService.Authorization;
using CloudSelfService.Endpoints;
using CloudSelfService.Services;
using CloudSelfService.Services.Access;
using CloudSelfService.Services.Storage;
using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// The single storage gate, enforced on the compiled app: only the types listed here may hold or receive a raw storage
/// backend, its resolver or the client factory. Each caller outside the gate acts only after a StorageAccessService
/// decision and only on the target it returned. Adding a type here is a security review decision.
/// </summary>
public class StorageGateArchitectureTests
{
    private static readonly Type[] RawStorageTypes = { typeof(IStorageBackendResolver), typeof(IStorageBackend), typeof(StorageClientFactory) };

    private static readonly HashSet<Type> Allowed = new()
    {
        typeof(StorageAccessService),
        typeof(DownloadEndpoints),
        typeof(UploadSessionService),
        typeof(UploadCleanupService),
        typeof(AdminService),
        typeof(IStorageBackend),
        typeof(IStorageBackendResolver),
        typeof(StorageBackendResolver),
        typeof(FixedBackendResolver),
        typeof(FileShareBackend),
        typeof(BlobBackend),
        typeof(FakeStorageBackend),
        typeof(StorageClientFactory),
        // DI registration only.
        typeof(Program),
        // Development-only: seeds the in-memory fake for the customer personas; never resolves a real backend.
        typeof(DevSignIn),
    };

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool IsRaw(Type t)
    {
        if (t.IsByRef || t.IsArray || t.IsPointer) return IsRaw(t.GetElementType()!);
        if (RawStorageTypes.Any(r => r.IsAssignableFrom(t))) return true;
        return t.IsGenericType && t.GetGenericArguments().Any(IsRaw);
    }

    /// <summary>Compiler-generated closures and state machines are nested types; they count as their declaring type.</summary>
    private static Type Outermost(Type t)
    {
        while (t.DeclaringType is not null) t = t.DeclaringType;
        return t;
    }

    private static IEnumerable<string> RawUses(Type t)
    {
        foreach (var f in t.GetFields(All).Where(f => IsRaw(f.FieldType)))
            yield return $"field {f.Name}";
        foreach (var p in t.GetProperties(All).Where(p => IsRaw(p.PropertyType)))
            yield return $"property {p.Name}";
        foreach (var c in t.GetConstructors(All))
        foreach (var p in c.GetParameters().Where(p => IsRaw(p.ParameterType)))
            yield return $"constructor parameter {p.Name}";
        foreach (var m in t.GetMethods(All))
        foreach (var p in m.GetParameters().Where(p => IsRaw(p.ParameterType)))
            yield return $"{m.Name} parameter {p.Name}";
    }

    private static Dictionary<Type, List<string>> UsersOfRawStorage() =>
        typeof(Program).Assembly.GetTypes()
            .SelectMany(t => RawUses(t).Select(use => (Owner: Outermost(t), Use: $"{t.FullName}: {use}")))
            .GroupBy(x => x.Owner)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Use).ToList());

    [Fact]
    public void OnlyAllowListedTypes_HoldOrReceiveRawStorage()
    {
        var offenders = UsersOfRawStorage()
            .Where(kv => !Allowed.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Raw storage access outside the gate's allow-list:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Scanner_SeesTheKnownGatedCallers()
    {
        // Guards the scan itself: if it stopped finding these, the allow-list test would pass vacuously.
        var users = UsersOfRawStorage().Keys.ToHashSet();

        foreach (var expected in new[] { typeof(StorageAccessService), typeof(DownloadEndpoints), typeof(UploadSessionService),
                     typeof(UploadCleanupService), typeof(AdminService) })
            Assert.Contains(expected, users);
    }
}
