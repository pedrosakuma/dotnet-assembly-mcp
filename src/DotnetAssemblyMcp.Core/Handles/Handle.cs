using System.Globalization;

// CA1716: 'Handles' is a VB.NET keyword. Suppressed because the directory + namespace pair
// is the deliberate API surface introduced by issue #80 to consolidate the wire grammar; no
// VB consumer is in scope for this server.
#pragma warning disable CA1716

namespace DotnetAssemblyMcp.Core.Handles;

/// <summary>
/// Discriminator returned by <see cref="HandleSyntax.TryParseAny"/>.
/// </summary>
public enum HandleKind
{
    Assembly,
    Type,
    Method,
    Parameter,
    Field,
    Property,
    Event,
}

/// <summary>
/// Single source of truth for the public handle alphabet that flows across the MCP wire:
/// <c>a:</c> assembly, <c>t:</c> type, <c>m:</c> method, <c>pa:</c> parameter, <c>f:</c> field,
/// <c>p:</c> property, <c>e:</c> event.
/// </summary>
/// <remarks>
/// Wire format per kind (chosen to round-trip through every server tool that accepts the
/// corresponding handle):
/// <list type="bullet">
/// <item><description><c>a:&lt;mvid&gt;</c></description></item>
/// <item><description><c>t:&lt;mvid&gt;:0x&lt;token&gt;</c> (TypeDef token, table 0x02)</description></item>
/// <item><description><c>m:&lt;mvid&gt;:0x&lt;token&gt;</c> (MethodDef token, table 0x06)</description></item>
/// <item><description><c>pa:&lt;mvid&gt;:0x&lt;methodToken&gt;:&lt;sequence&gt;</c> (parameter — sequence 0 is the return value)</description></item>
/// <item><description><c>f:&lt;mvid&gt;:0x&lt;token&gt;</c> (FieldDef token, table 0x04)</description></item>
/// <item><description><c>p:&lt;mvid&gt;:0x&lt;token&gt;</c> (PropertyDef token, table 0x17)</description></item>
/// <item><description><c>e:&lt;mvid&gt;:0x&lt;token&gt;</c> (EventDef token, table 0x14)</description></item>
/// </list>
/// Extracted from the previous split between <c>HandleFormat</c> (Core, format side) and
/// <c>AssemblyTools.TryParse*</c> (Server, parse side) under issue #80 to eliminate a latent
/// bug where the producer emitted <c>m:&lt;mvid&gt;:0x&lt;token&gt;#param=&lt;seq&gt;</c> while
/// the parser only accepted <c>pa:&lt;mvid&gt;:0x&lt;token&gt;:&lt;seq&gt;</c>. The
/// <c>pa:</c> form wins because it was the only form the parser ever understood.
/// </remarks>
public static class HandleSyntax
{
    // -------- Format --------

    public static string FormatAssembly(Guid mvid) => $"a:{mvid:D}";
    public static string FormatType(Guid mvid, int token) => $"t:{mvid:D}:0x{token:X8}";
    public static string FormatMethod(Guid mvid, int token) => $"m:{mvid:D}:0x{token:X8}";
    public static string FormatField(Guid mvid, int token) => $"f:{mvid:D}:0x{token:X8}";
    public static string FormatProperty(Guid mvid, int token) => $"p:{mvid:D}:0x{token:X8}";
    public static string FormatEvent(Guid mvid, int token) => $"e:{mvid:D}:0x{token:X8}";

    /// <summary>
    /// Format for a single parameter (1-based sequence) of a method-definition handle. Sequence
    /// 0 designates the method's return value, matching ECMA-335 §II.22.33 row ordering.
    /// </summary>
    public static string FormatParameter(Guid mvid, int methodToken, int sequence)
        => $"pa:{mvid:D}:0x{methodToken:X8}:{sequence}";

    // -------- TryParse (per kind) --------

    public static bool TryParseAssembly(string handle, out Guid mvid)
    {
        mvid = default;
        var s = handle?.Trim() ?? string.Empty;
        if (!s.StartsWith("a:", StringComparison.Ordinal)) return false;
        return Guid.TryParse(s.AsSpan(2), out mvid);
    }

    public static bool TryParseType(string handle, out Guid mvid, out int token)
        => TryParsePrefixed(handle, "t:", out mvid, out token);

    public static bool TryParseMethod(string handle, out Guid mvid, out int token)
        => TryParsePrefixed(handle, "m:", out mvid, out token);

    public static bool TryParseField(string handle, out Guid mvid, out int token)
        => TryParsePrefixed(handle, "f:", out mvid, out token);

    public static bool TryParseProperty(string handle, out Guid mvid, out int token)
        => TryParsePrefixed(handle, "p:", out mvid, out token);

    public static bool TryParseEvent(string handle, out Guid mvid, out int token)
        => TryParsePrefixed(handle, "e:", out mvid, out token);

    public static bool TryParseParameter(string handle, out Guid mvid, out int methodToken, out int sequence)
    {
        mvid = default;
        methodToken = 0;
        sequence = 0;
        var s = handle?.Trim() ?? string.Empty;
        if (!s.StartsWith("pa:", StringComparison.Ordinal)) return false;
        var rest = s.AsSpan(3);
        var sep1 = rest.IndexOf(':');
        if (sep1 < 0) return false;
        if (!Guid.TryParse(rest[..sep1], out mvid)) return false;
        var after = rest[(sep1 + 1)..];
        var sep2 = after.IndexOf(':');
        if (sep2 < 0) return false;
        if (!TryParseToken(after[..sep2].ToString(), out methodToken)) return false;
        return int.TryParse(after[(sep2 + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence)
               && sequence >= 0;
    }

    /// <summary>
    /// Dispatch on the prefix and parse into a uniform shape: every kind populates <paramref name="mvid"/>;
    /// kinds with a token populate <paramref name="token"/> (0 for assembly); <see cref="HandleKind.Parameter"/>
    /// also populates <paramref name="sequence"/>. Order matters internally — <c>pa:</c> is tested before
    /// <c>p:</c> because the parameter prefix is a superstring of the property prefix.
    /// </summary>
    public static bool TryParseAny(string handle, out HandleKind kind, out Guid mvid, out int token, out int sequence)
    {
        kind = default;
        mvid = default;
        token = 0;
        sequence = 0;
        var s = handle?.Trim() ?? string.Empty;
        if (s.Length < 2) return false;

        if (s.StartsWith("pa:", StringComparison.Ordinal))
        {
            if (!TryParseParameter(s, out mvid, out token, out sequence)) return false;
            kind = HandleKind.Parameter;
            return true;
        }
        if (s.StartsWith("a:", StringComparison.Ordinal))
        {
            if (!TryParseAssembly(s, out mvid)) return false;
            kind = HandleKind.Assembly;
            return true;
        }
        if (s.StartsWith("t:", StringComparison.Ordinal))
        {
            if (!TryParseType(s, out mvid, out token)) return false;
            kind = HandleKind.Type;
            return true;
        }
        if (s.StartsWith("m:", StringComparison.Ordinal))
        {
            if (!TryParseMethod(s, out mvid, out token)) return false;
            kind = HandleKind.Method;
            return true;
        }
        if (s.StartsWith("f:", StringComparison.Ordinal))
        {
            if (!TryParseField(s, out mvid, out token)) return false;
            kind = HandleKind.Field;
            return true;
        }
        if (s.StartsWith("p:", StringComparison.Ordinal))
        {
            if (!TryParseProperty(s, out mvid, out token)) return false;
            kind = HandleKind.Property;
            return true;
        }
        if (s.StartsWith("e:", StringComparison.Ordinal))
        {
            if (!TryParseEvent(s, out mvid, out token)) return false;
            kind = HandleKind.Event;
            return true;
        }
        return false;
    }

    // -------- Shared low-level token parser --------

    /// <summary>
    /// Parses a metadata token from decimal or hex (with optional <c>0x</c> prefix). Metadata
    /// tokens are unsigned 32-bit values (table id &lt;&lt; 24 | rid); the result is the
    /// bit-identical signed cast so the high bit is preserved for tables &gt; 0x7F. Explicit
    /// sign characters are rejected — <c>-1</c> is not a token.
    /// </summary>
    public static bool TryParseToken(string raw, out int token)
    {
        token = 0;
        var s = raw?.Trim() ?? string.Empty;
        if (s.Length == 0) return false;
        if (s[0] == '-' || s[0] == '+') return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                return false;
            token = unchecked((int)u);
            return true;
        }
        if (!uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u2))
            return false;
        token = unchecked((int)u2);
        return true;
    }

    private static bool TryParsePrefixed(string handle, string prefix, out Guid mvid, out int token)
    {
        mvid = default;
        token = 0;
        var s = handle?.Trim() ?? string.Empty;
        if (!s.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = s.AsSpan(prefix.Length);
        var sep = rest.IndexOf(':');
        if (sep < 0) return false;
        if (!Guid.TryParse(rest[..sep], out mvid)) return false;
        return TryParseToken(rest[(sep + 1)..].ToString(), out token);
    }
}
