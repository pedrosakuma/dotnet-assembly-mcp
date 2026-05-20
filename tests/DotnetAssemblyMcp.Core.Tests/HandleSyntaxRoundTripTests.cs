using DotnetAssemblyMcp.Core.Handles;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Locks down the wire grammar owned by <see cref="HandleSyntax"/> (issue #80). Every
/// production code path that emits a handle uses one of these formatters; every parser
/// dispatches through <see cref="HandleSyntax.TryParseAny"/> or a per-kind TryParse.
/// </summary>
public sealed class HandleSyntaxRoundTripTests
{
    private static readonly Guid Mvid = new("12345678-1234-1234-1234-1234567890ab");

    [Fact]
    public void Assembly_round_trip()
    {
        var s = HandleSyntax.FormatAssembly(Mvid);
        s.Should().Be("a:12345678-1234-1234-1234-1234567890ab");
        HandleSyntax.TryParseAssembly(s, out var mvid).Should().BeTrue();
        mvid.Should().Be(Mvid);
    }

    [Theory]
    [InlineData(0x02000001)]
    [InlineData(0x06FFFFFE)]
    [InlineData(unchecked((int)0xFF000001))] // high bit set — must survive the int cast
    public void Method_round_trip_preserves_high_bit_tokens(int token)
    {
        var s = HandleSyntax.FormatMethod(Mvid, token);
        s.Should().StartWith("m:").And.Contain("0x");
        HandleSyntax.TryParseMethod(s, out var mvid, out var t).Should().BeTrue();
        mvid.Should().Be(Mvid);
        t.Should().Be(token);
    }

    [Fact]
    public void Per_kind_format_then_parse_round_trips()
    {
        var t = HandleSyntax.FormatType(Mvid, 0x02000010);
        HandleSyntax.TryParseType(t, out _, out var tt).Should().BeTrue();
        tt.Should().Be(0x02000010);

        var f = HandleSyntax.FormatField(Mvid, 0x04000003);
        HandleSyntax.TryParseField(f, out _, out var ff).Should().BeTrue();
        ff.Should().Be(0x04000003);

        var p = HandleSyntax.FormatProperty(Mvid, 0x17000002);
        HandleSyntax.TryParseProperty(p, out _, out var pp).Should().BeTrue();
        pp.Should().Be(0x17000002);

        var e = HandleSyntax.FormatEvent(Mvid, 0x14000001);
        HandleSyntax.TryParseEvent(e, out _, out var ee).Should().BeTrue();
        ee.Should().Be(0x14000001);
    }

    [Fact]
    public void Parameter_wire_format_is_pa_not_hash_param()
    {
        var s = HandleSyntax.FormatParameter(Mvid, 0x0600000A, 2);
        // Regression: previous emitter produced 'm:<mvid>:0x<token>#param=<seq>' which no
        // parser accepted. Issue #80 normalises to the parser-friendly 'pa:' form.
        s.Should().Be("pa:12345678-1234-1234-1234-1234567890ab:0x0600000A:2");
        s.Should().NotContain("#param=");

        HandleSyntax.TryParseParameter(s, out var mvid, out var method, out var seq).Should().BeTrue();
        mvid.Should().Be(Mvid);
        method.Should().Be(0x0600000A);
        seq.Should().Be(2);
    }

    [Fact]
    public void TryParseAny_dispatches_every_kind()
    {
        Check(HandleSyntax.FormatAssembly(Mvid), HandleKind.Assembly, 0, 0);
        Check(HandleSyntax.FormatType(Mvid, 0x02000010), HandleKind.Type, 0x02000010, 0);
        Check(HandleSyntax.FormatMethod(Mvid, 0x06000001), HandleKind.Method, 0x06000001, 0);
        Check(HandleSyntax.FormatParameter(Mvid, 0x06000001, 3), HandleKind.Parameter, 0x06000001, 3);
        Check(HandleSyntax.FormatField(Mvid, 0x04000005), HandleKind.Field, 0x04000005, 0);
        Check(HandleSyntax.FormatProperty(Mvid, 0x17000002), HandleKind.Property, 0x17000002, 0);
        Check(HandleSyntax.FormatEvent(Mvid, 0x14000004), HandleKind.Event, 0x14000004, 0);

        static void Check(string s, HandleKind expectedKind, int expectedToken, int expectedSeq)
        {
            HandleSyntax.TryParseAny(s, out var kind, out _, out var token, out var seq).Should().BeTrue();
            kind.Should().Be(expectedKind);
            token.Should().Be(expectedToken);
            seq.Should().Be(expectedSeq);
        }
    }

    [Fact]
    public void TryParseAny_rejects_unknown_or_garbled_prefixes()
    {
        HandleSyntax.TryParseAny("", out _, out _, out _, out _).Should().BeFalse();
        HandleSyntax.TryParseAny("xyz:foo", out _, out _, out _, out _).Should().BeFalse();
        HandleSyntax.TryParseAny("a:not-a-guid", out _, out _, out _, out _).Should().BeFalse();
        HandleSyntax.TryParseAny("t:12345678-1234-1234-1234-1234567890ab:notatoken",
            out _, out _, out _, out _).Should().BeFalse();
        // Property and parameter prefixes are disjoint — neither can be mistaken for the other.
        HandleSyntax.TryParseAny("pa:12345678-1234-1234-1234-1234567890ab:0x06000001:-1",
            out _, out _, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("0xZZZZ")]
    [InlineData("")]
    public void TryParseToken_rejects_sign_and_garbage(string raw)
    {
        HandleSyntax.TryParseToken(raw, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0x06000001", 0x06000001)]
    [InlineData("0xFF000001", unchecked((int)0xFF000001))]
    [InlineData("100663297", 0x06000001)] // decimal equivalent
    public void TryParseToken_accepts_hex_and_decimal(string raw, int expected)
    {
        HandleSyntax.TryParseToken(raw, out var token).Should().BeTrue();
        token.Should().Be(expected);
    }
}
