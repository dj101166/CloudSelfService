using System.Reflection;
using System.Reflection.Emit;
using CloudSelfService.Services;
using CloudSelfService.Services.Data;
using CloudSelfService.Services.Transfers;
using Xunit;

namespace CloudSelfService.Tests;

/// <summary>
/// The upload limit override is written only by AdminService, behind its StaffAdmin check and audit row. Enforced on the
/// compiled app: only the types listed here may hold, receive, resolve or construct an <see cref="IUploadLimitStore"/>.
/// UploadLimitService holds it to read. Adding a type here is a security review decision.
/// </summary>
public class UploadLimitWriteArchitectureTests
{
    private static readonly HashSet<Type> Allowed = new()
    {
        typeof(AdminService),
        typeof(UploadLimitService),
        typeof(IUploadLimitStore),
        typeof(InMemoryUploadLimitStore),
        typeof(PgUploadLimitStore),
        // DI registration only.
        typeof(Program),
    };

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                     | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static bool IsStore(Type t)
    {
        if (t.IsByRef || t.IsArray || t.IsPointer) return IsStore(t.GetElementType()!);
        if (typeof(IUploadLimitStore).IsAssignableFrom(t)) return true;
        return t.IsGenericType && t.GetGenericArguments().Any(IsStore);
    }

    private static Type Outermost(Type t)
    {
        while (t.DeclaringType is not null) t = t.DeclaringType;
        return t;
    }

    private static IEnumerable<string> StoreUses(Type t)
    {
        foreach (var f in t.GetFields(All).Where(f => IsStore(f.FieldType)))
            yield return $"field {f.Name}";
        foreach (var p in t.GetProperties(All).Where(p => IsStore(p.PropertyType)))
            yield return $"property {p.Name}";
        foreach (var m in t.GetConstructors(All).Cast<MethodBase>().Concat(t.GetMethods(All)))
        {
            foreach (var p in m.GetParameters().Where(p => IsStore(p.ParameterType)))
                yield return $"{m.Name} parameter {p.Name}";
            foreach (var called in MethodsReferencedBy(m).Where(Touches))
                yield return $"{m.Name} calls {called.DeclaringType?.Name}.{called.Name}";
        }
    }

    // A constructor of a store, or a generic call over one (GetRequiredService<IUploadLimitStore>, ActivatorUtilities).
    private static bool Touches(MethodBase m) =>
        (m.IsConstructor && m.DeclaringType is { } d && IsStore(d))
        || (m.IsGenericMethod && m.GetGenericArguments().Any(IsStore))
        || (m.DeclaringType is { IsGenericType: true } g && g.GetGenericArguments().Any(IsStore));

    private static IEnumerable<MethodBase> MethodsReferencedBy(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        var genericTypeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        var genericMethodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[i++]));
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break;
            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                    var token = BitConverter.ToInt32(il, i);
                    MethodBase? resolved = null;
                    try { resolved = method.Module.ResolveMethod(token, genericTypeArgs, genericMethodArgs); }
                    catch (ArgumentException) { }
                    if (resolved is not null) yield return resolved;
                    i += 4;
                    break;
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8 or OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                default: i += 4; break;
            }
        }
    }

    private static Dictionary<Type, List<string>> UsersOfTheStore() =>
        typeof(Program).Assembly.GetTypes()
            .SelectMany(t => StoreUses(t).Select(use => (Owner: Outermost(t), Use: $"{t.FullName}: {use}")))
            .GroupBy(x => x.Owner)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Use).ToList());

    [Fact]
    public void OnlyAllowListedTypes_TakeOrConstructUploadLimitStore()
    {
        var offenders = UsersOfTheStore()
            .Where(kv => !Allowed.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Upload limit store used outside AdminService and UploadLimitService:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Scanner_SeesTheKnownUsers()
    {
        // Guards the scan itself: if it stopped finding these, the allow-list test would pass vacuously.
        var users = UsersOfTheStore();

        Assert.Contains(typeof(AdminService), users.Keys);
        Assert.Contains(typeof(UploadLimitService), users.Keys);
        Assert.Contains(users[typeof(Program)], use => use.Contains("calls"));
    }

    [Fact]
    public void UploadLimitService_HasNoWriteMethods()
    {
        var methods = typeof(UploadLimitService).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                                            | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        Assert.DoesNotContain(methods, n => n.Contains("Set", StringComparison.Ordinal) || n.Contains("Clear", StringComparison.Ordinal));
    }
}
