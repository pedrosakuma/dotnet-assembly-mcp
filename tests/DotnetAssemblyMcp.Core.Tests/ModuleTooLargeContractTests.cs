using DotnetAssemblyMcp.Core.Errors;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Lightweight contract tests for the resource-budget exception introduced in #137.
/// The actual budget-exceeded path is exercised at integration-level via real
/// assemblies (no in-repo fixture exceeds 200_000 methods or 5M refs by design), but
/// these tests fence the public surface — error kind + message hygiene + recovery hint —
/// so a downstream refactor cannot quietly retire the ABI.
/// </summary>
public sealed class ModuleTooLargeContractTests
{
    [Fact]
    public void ErrorKind_is_published()
    {
        ErrorKinds.ModuleTooLarge.Should().Be("module_too_large");
    }

    [Fact]
    public void Recovery_hint_is_attached()
    {
        var err = new AssemblyError(ErrorKinds.ModuleTooLarge, "synthetic");
        var hint = AssemblyErrorRecovery.For(err);
        hint.Should().NotBeNull();
        hint!.NextTool.Should().Be("list_assemblies");
        hint.Reason.Should().Contain("per-module index budget");
    }

    [Fact]
    public void Exception_carries_limit_name_and_value()
    {
        var ex = new ModuleTooLargeException("MaxThings", 42);
        ex.LimitName.Should().Be("MaxThings");
        ex.Limit.Should().Be(42);
        ex.Message.Should().Contain("MaxThings").And.Contain("42");
    }
}
