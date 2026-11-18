using System.Text;

namespace Podish.Core.Graphics;

internal static class ExternalImageShader
{
    internal static bool TryRewrite(ReadOnlySpan<byte> source, out byte[]? rewritten, out HashSet<string>? uniforms)
    {
        rewritten = null;
        uniforms = null;
        if (source.IndexOf("samplerExternalOES"u8) < 0) return false;

        var scanner = new Scanner(source);
        var edits = new List<(int Start, int Length, bool Directive)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var enabled = false;
        var uniform = false;
        while (true)
        {
            int previousEnd = scanner.LastTokenEnd;
            if (!scanner.Next(out var token, out var offset)) break;
            if (token.SequenceEqual("#"u8))
            {
                if (previousEnd != 0 && source[previousEnd..offset].IndexOfAny((byte)'\r', (byte)'\n') < 0)
                    return false;
                uniform = false;
                var line = source[offset..];
                int lineLength = line.IndexOfAny((byte)'\r', (byte)'\n');
                if (lineLength < 0) lineLength = line.Length;
                // Leave macro expansion, conditional compilation and continued directives to ANGLE.
                if (line[..lineLength].Contains((byte)'\\')) return false;
                var directive = new Scanner(line[1..lineLength]);
                if (!directive.Next(out var command, out _)) continue;
                if (command.SequenceEqual("version"u8) &&
                    (!directive.Next(out var version, out _) || !version.SequenceEqual("100"u8)))
                    return false;
                if (command.SequenceEqual("define"u8) || command.SequenceEqual("undef"u8) ||
                    command.StartsWith("if"u8) || command.SequenceEqual("else"u8) ||
                    command.SequenceEqual("elif"u8) || command.SequenceEqual("endif"u8))
                    return false;
                if (!command.SequenceEqual("extension"u8) || !directive.Next(out var extension, out _)) continue;
                if (extension.SequenceEqual("all"u8)) return false;
                if (!extension.SequenceEqual("GL_OES_EGL_image_external"u8)) continue;
                if (enabled || !directive.Read(":"u8) || !directive.Next(out var behavior, out _) ||
                    (!behavior.SequenceEqual("require"u8) && !behavior.SequenceEqual("enable"u8)) ||
                    directive.Next(out _, out _))
                    return false;
                enabled = true;
                // Replace only the directive tokens. Keep any trailing comment, including a multi-line comment.
                var directiveLength = 1 + directive.LastTokenEnd;
                edits.Add((offset, directiveLength, true));
                scanner.SkipTo(offset + directiveLength);
                continue;
            }

            if (token.SequenceEqual("samplerExternalOES"u8))
            {
                if (!enabled || !uniform || !ReadUniformNames(ref scanner, names)) return false;
                edits.Add((offset, token.Length, false));
                uniform = false;
                continue;
            }
            if (!token.SequenceEqual("lowp"u8) && !token.SequenceEqual("mediump"u8) && !token.SequenceEqual("highp"u8))
                uniform = token.SequenceEqual("uniform"u8);
        }

        if (names.Count == 0) return false;
        rewritten = source.ToArray();
        foreach (var (start, length, directive) in edits)
        {
            var range = rewritten.AsSpan(start, length);
            range.Fill((byte)' ');
            if (!directive) "sampler2D"u8.CopyTo(range);
        }
        uniforms = names;
        return true;
    }

    private static bool ReadUniformNames(ref Scanner scanner, HashSet<string> names)
    {
        while (scanner.Next(out var name, out _) && IsIdentifierStart(name[0]))
        {
            names.Add(Encoding.ASCII.GetString(name));
            if (!scanner.Next(out var next, out _)) return false;
            if (next.SequenceEqual("["u8))
            {
                if (!scanner.Next(out var size, out _) || !IsDigits(size) || !scanner.Read("]"u8) ||
                    !scanner.Next(out next, out _))
                    return false;
            }
            if (next.SequenceEqual(";"u8)) return true;
            if (!next.SequenceEqual(","u8)) return false;
        }
        return false;
    }

    private static bool IsDigits(ReadOnlySpan<byte> token)
    {
        foreach (var b in token)
            if (b is < (byte)'0' or > (byte)'9') return false;
        return true;
    }

    private static bool IsIdentifierStart(byte b) =>
        b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_';

    private ref struct Scanner(ReadOnlySpan<byte> source)
    {
        private readonly ReadOnlySpan<byte> _source = source;
        private int _position;
        internal int LastTokenEnd { get; private set; }

        internal void SkipTo(int position) => LastTokenEnd = _position = position;

        internal bool Read(ReadOnlySpan<byte> expected) => Next(out var token, out _) && token.SequenceEqual(expected);

        internal bool Next(out ReadOnlySpan<byte> token, out int start)
        {
            token = default;
            start = 0;
            while (_position < _source.Length)
            {
                byte b = _source[_position++];
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\v' or (byte)'\f') continue;
                if (b == '/' && _position < _source.Length && _source[_position] == '/')
                {
                    int end = _source[_position..].IndexOfAny((byte)'\r', (byte)'\n');
                    _position = end < 0 ? _source.Length : _position + end;
                    continue;
                }
                if (b == '/' && _position < _source.Length && _source[_position] == '*')
                {
                    int end = _source[(_position + 1)..].IndexOf("*/"u8);
                    _position = end < 0 ? _source.Length : _position + end + 3;
                    continue;
                }
                start = _position - 1;
                if (IsIdentifierStart(b) || b is >= (byte)'0' and <= (byte)'9')
                    while (_position < _source.Length &&
                           (IsIdentifierStart(_source[_position]) || _source[_position] is >= (byte)'0' and <= (byte)'9'))
                        _position++;
                token = _source[start.._position];
                LastTokenEnd = _position;
                return true;
            }
            return false;
        }
    }
}
