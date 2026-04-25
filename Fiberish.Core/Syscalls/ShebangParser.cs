namespace Fiberish.Syscalls;

public static class ShebangParser
{
    public static bool TryParse(ReadOnlySpan<byte> header, out byte[] interpreterPath, out byte[]? interpreterArg)
    {
        interpreterPath = [];
        interpreterArg = null;

        if (header.Length < 2 || header[0] != '#' || header[1] != '!')
            return false;

        var lineEnd = header[2..].IndexOf((byte)'\n');
        if (lineEnd < 0)
            lineEnd = header.Length - 2;

        var line = header.Slice(2, lineEnd);
        line = TrimAsciiWhiteSpace(line);
        if (line.IsEmpty)
            return false;

        var splitIdx = -1;
        for (var i = 0; i < line.Length; i++)
        {
            var b = line[i];
            if (b == ' ' || b == '\t')
            {
                splitIdx = i;
                break;
            }
        }

        if (splitIdx >= 0)
        {
            interpreterPath = line[..splitIdx].ToArray();
            var argSpan = TrimAsciiWhiteSpace(line[(splitIdx + 1)..]);
            if (!argSpan.IsEmpty)
                interpreterArg = argSpan.ToArray();
        }
        else
        {
            interpreterPath = line.ToArray();
        }

        return true;
    }

    private static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;

        while (start < end && IsAsciiWhiteSpace(value[start]))
            start++;
        while (end > start && IsAsciiWhiteSpace(value[end - 1]))
            end--;

        return value[start..end];
    }

    private static bool IsAsciiWhiteSpace(byte b)
    {
        return b == ' ' || b == '\t' || b == '\n' || b == '\r' || b == '\f' || b == '\v';
    }
}
