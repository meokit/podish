using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Fiberish.X86.Tests.Isolation;

public sealed class TestArchitectureTests
{
    [Fact]
    public void SuiteDoesNotDeclareMutableStaticTestState()
    {
        var violations = typeof(TestArchitectureTests).Assembly.GetTypes()
            // The compiler caches noncapturing delegates and iterator metadata.
            // Those are implementation details, not author-owned test fixtures.
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Public |
                                               BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(field => !field.IsLiteral &&
                            !field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Select(field => $"{field.DeclaringType!.FullName}.{field.Name}")
            .OrderBy(name => name)
            .ToArray();
        Assert.True(violations.Length == 0,
            "Test state must be instance-owned (readonly arrays are mutable too): " + string.Join(", ", violations));
    }

    [Fact]
    public void SuiteDoesNotReferenceLinuxRuntimeOrPodish()
    {
        var forbidden = typeof(TestArchitectureTests).Assembly.GetReferencedAssemblies()
            .Where(assembly => assembly.Name == "Fiberish.Core" ||
                               assembly.Name?.StartsWith("Podish", StringComparison.Ordinal) == true)
            .Select(assembly => assembly.FullName)
            .ToArray();
        Assert.Empty(forbidden);
    }
}
