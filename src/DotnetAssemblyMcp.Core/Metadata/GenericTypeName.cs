using System.Collections.Immutable;
using System.Text;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Canonical wire format for generic type arguments crossing the MethodIdentity handoff
/// (see <c>docs/handoff-contract.md §3.5</c>). The format is CLR reflection-style full
/// names <strong>without assembly qualification</strong>:
/// <list type="bullet">
///   <item>Namespace + name with backtick arity: <c>System.Collections.Generic.List`1</c>.</item>
///   <item>Nested types separated by <c>+</c>: <c>Outer+Inner</c>.</item>
///   <item>Closed generic args bracketed and recursive: <c>Dictionary`2[System.Int32,System.String]</c>.</item>
///   <item>Arrays: <c>T[]</c> (SZ), <c>T[,]</c> (rank-2), <c>T[*]</c> (MD rank-1 with non-zero lower bound).</item>
///   <item>By-ref / pointer: <c>T&amp;</c>, <c>T*</c>.</item>
/// </list>
/// Open type parameters (<c>!N</c>, <c>!!N</c>) are rejected — instantiations on the wire
/// MUST be closed.
/// </summary>
public abstract record GenericTypeName
{
    public abstract void Format(StringBuilder sb);

    public string Format()
    {
        var sb = new StringBuilder();
        Format(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Parses a canonical type-arg string. Returns the parsed tree, or an
    /// <see cref="Errors.ErrorKinds"/> code in <paramref name="errorKind"/> and a human-readable
    /// <paramref name="errorMessage"/> on failure. Never throws on malformed input.
    /// </summary>
    public static bool TryParse(string input, out GenericTypeName? result, out string? errorKind, out string? errorMessage)
    {
        result = null;
        errorKind = null;
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            errorKind = Errors.ErrorKinds.InvalidArgument;
            errorMessage = "Type name is empty.";
            return false;
        }
        var parser = new Parser(input);
        if (!parser.TryParseTypeSpec(out var node, out errorKind, out errorMessage))
            return false;
        if (!parser.AtEnd)
        {
            errorKind = Errors.ErrorKinds.InvalidArgument;
            errorMessage = $"Unexpected trailing input at position {parser.Position}: '{parser.Remainder}'.";
            return false;
        }
        result = node;
        return true;
    }

    /// <summary>A named (possibly nested, possibly closed-generic) type reference.</summary>
    public sealed record Named(
        ImmutableArray<string> NamespaceSegments,
        ImmutableArray<string> NameChain,
        ImmutableArray<GenericTypeName> TypeArguments) : GenericTypeName
    {
        /// <summary>
        /// CLR-style full name without generic args, e.g. <c>System.Collections.Generic.Dictionary`2</c>
        /// or <c>Outer+Inner</c>. Used by <see cref="MetadataIndex"/> for TypeDef/TypeRef lookup.
        /// </summary>
        public string ClrFullName
        {
            get
            {
                var sb = new StringBuilder();
                for (int i = 0; i < NamespaceSegments.Length; i++)
                {
                    if (i > 0) sb.Append('.');
                    sb.Append(NamespaceSegments[i]);
                }
                for (int i = 0; i < NameChain.Length; i++)
                {
                    sb.Append(i == 0 ? (NamespaceSegments.Length > 0 ? "." : "") : "+");
                    sb.Append(NameChain[i]);
                }
                return sb.ToString();
            }
        }

        public override void Format(StringBuilder sb)
        {
            sb.Append(ClrFullName);
            if (!TypeArguments.IsDefaultOrEmpty)
            {
                sb.Append('[');
                for (int i = 0; i < TypeArguments.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    TypeArguments[i].Format(sb);
                }
                sb.Append(']');
            }
        }
    }

    /// <summary>An SZ array (rank 1, zero lower bound).</summary>
    public sealed record SzArray(GenericTypeName Element) : GenericTypeName
    {
        public override void Format(StringBuilder sb) { Element.Format(sb); sb.Append("[]"); }
    }

    /// <summary>A multi-dimensional array (rank ≥ 2, or rank 1 with non-zero lower bound when <see cref="LowerBoundNonZero"/>).</summary>
    public sealed record MdArray(GenericTypeName Element, int Rank, bool LowerBoundNonZero) : GenericTypeName
    {
        public override void Format(StringBuilder sb)
        {
            Element.Format(sb);
            sb.Append('[');
            if (Rank == 1 && LowerBoundNonZero)
            {
                sb.Append('*');
            }
            else
            {
                for (int i = 1; i < Rank; i++) sb.Append(',');
            }
            sb.Append(']');
        }
    }

    public sealed record ByRefType(GenericTypeName Element) : GenericTypeName
    {
        public override void Format(StringBuilder sb) { Element.Format(sb); sb.Append('&'); }
    }

    public sealed record PointerType(GenericTypeName Element) : GenericTypeName
    {
        public override void Format(StringBuilder sb) { Element.Format(sb); sb.Append('*'); }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) { _s = s; _i = 0; }
        public bool AtEnd => _i >= _s.Length;
        public int Position => _i;
        public string Remainder => _s[_i..];

        public bool TryParseTypeSpec(out GenericTypeName? result, out string? errorKind, out string? errorMessage)
        {
            result = null;
            errorKind = null;
            errorMessage = null;
            if (!TryParseNamed(out var named, out errorKind, out errorMessage))
                return false;
            GenericTypeName current = named!;
            while (!AtEnd)
            {
                char c = _s[_i];
                if (c == '[')
                {
                    // could be array suffix — generic args were already consumed inside TryParseNamed.
                    if (!TryParseArraySuffix(current, out current!, out errorKind, out errorMessage))
                        return false;
                }
                else if (c == '&')
                {
                    _i++;
                    current = new ByRefType(current);
                }
                else if (c == '*')
                {
                    _i++;
                    current = new PointerType(current);
                }
                else
                {
                    break;
                }
            }
            result = current;
            return true;
        }

        private bool TryParseNamed(out Named? result, out string? errorKind, out string? errorMessage)
        {
            result = null;
            errorKind = null;
            errorMessage = null;

            if (!AtEnd && (_s[_i] == '!' || _s[_i] == '\0'))
            {
                errorKind = Errors.ErrorKinds.GenericInstantiationOpen;
                errorMessage = $"Open type parameter at position {_i}: '{Remainder}'. Wire instantiations must be closed.";
                return false;
            }

            var segments = new List<string>();
            var name = ReadIdentifier();
            if (name.Length == 0)
            {
                errorKind = Errors.ErrorKinds.InvalidArgument;
                errorMessage = $"Expected identifier at position {_i}: '{Remainder}'.";
                return false;
            }
            segments.Add(name);
            while (!AtEnd && _s[_i] == '.')
            {
                _i++;
                var next = ReadIdentifier();
                if (next.Length == 0)
                {
                    errorKind = Errors.ErrorKinds.InvalidArgument;
                    errorMessage = $"Expected identifier after '.' at position {_i}.";
                    return false;
                }
                segments.Add(next);
            }

            var nameChain = new List<string> { segments[^1] };
            segments.RemoveAt(segments.Count - 1);

            while (!AtEnd && _s[_i] == '+')
            {
                _i++;
                var nested = ReadIdentifier();
                if (nested.Length == 0)
                {
                    errorKind = Errors.ErrorKinds.InvalidArgument;
                    errorMessage = $"Expected identifier after '+' at position {_i}.";
                    return false;
                }
                nameChain.Add(nested);
            }

            ImmutableArray<GenericTypeName> typeArgs = ImmutableArray<GenericTypeName>.Empty;
            if (!AtEnd && _s[_i] == '[' && LooksLikeGenericArgList())
            {
                if (!TryParseGenericArgList(out typeArgs, out errorKind, out errorMessage))
                    return false;
            }

            result = new Named(
                segments.ToImmutableArray(),
                nameChain.ToImmutableArray(),
                typeArgs);
            return true;
        }

        /// <summary>
        /// Disambiguates <c>List`1[Int32]</c> (generic args) from <c>Int32[]</c> (SZ array) — the
        /// first character after '[' tells: a ']' or ',' or '*' means array; anything else means
        /// the start of a nested type spec, i.e. a generic-arg list.
        /// </summary>
        private bool LooksLikeGenericArgList()
        {
            if (_i + 1 >= _s.Length) return false;
            char next = _s[_i + 1];
            return next != ']' && next != ',' && next != '*';
        }

        private bool TryParseGenericArgList(out ImmutableArray<GenericTypeName> args, out string? errorKind, out string? errorMessage)
        {
            args = ImmutableArray<GenericTypeName>.Empty;
            errorKind = null;
            errorMessage = null;
            // consume '['
            _i++;
            var list = new List<GenericTypeName>();
            while (true)
            {
                if (!TryParseTypeSpec(out var arg, out errorKind, out errorMessage))
                    return false;
                list.Add(arg!);
                if (AtEnd)
                {
                    errorKind = Errors.ErrorKinds.InvalidArgument;
                    errorMessage = "Unterminated generic argument list.";
                    return false;
                }
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == ']') { _i++; break; }
                errorKind = Errors.ErrorKinds.InvalidArgument;
                errorMessage = $"Expected ',' or ']' in generic argument list at position {_i}: '{Remainder}'.";
                return false;
            }
            args = list.ToImmutableArray();
            return true;
        }

        private bool TryParseArraySuffix(GenericTypeName element, out GenericTypeName? result, out string? errorKind, out string? errorMessage)
        {
            result = null;
            errorKind = null;
            errorMessage = null;
            // already at '['
            _i++;
            if (AtEnd)
            {
                errorKind = Errors.ErrorKinds.InvalidArgument;
                errorMessage = "Unterminated array suffix.";
                return false;
            }
            if (_s[_i] == ']')
            {
                _i++;
                result = new SzArray(element);
                return true;
            }
            if (_s[_i] == '*')
            {
                _i++;
                if (AtEnd || _s[_i] != ']')
                {
                    errorKind = Errors.ErrorKinds.InvalidArgument;
                    errorMessage = $"Expected ']' after '*' in array suffix at position {_i}.";
                    return false;
                }
                _i++;
                result = new MdArray(element, 1, LowerBoundNonZero: true);
                return true;
            }
            int rank = 1;
            while (!AtEnd && _s[_i] == ',') { rank++; _i++; }
            if (AtEnd || _s[_i] != ']')
            {
                errorKind = Errors.ErrorKinds.InvalidArgument;
                errorMessage = $"Expected ']' to close array suffix at position {_i}.";
                return false;
            }
            _i++;
            result = new MdArray(element, rank, LowerBoundNonZero: false);
            return true;
        }

        private string ReadIdentifier()
        {
            int start = _i;
            while (!AtEnd)
            {
                char c = _s[_i];
                if (c == '.' || c == '+' || c == '[' || c == ']' || c == ',' || c == '&' || c == '*' || c == '!')
                    break;
                _i++;
            }
            return _s.Substring(start, _i - start);
        }
    }
}
