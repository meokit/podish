namespace Podish.PerfTools;

internal static class PerfWorkloadCases
{
    public const string Compress = "compress";
    public const string Compile = "compile";
    public const string GccCompile = "gcc_compile";
    public const string Run = "run";
    public const string Grep10M = "grep_10m";

    private static readonly HashSet<string> SupportedCaseSet =
    [
        Compress,
        Compile,
        GccCompile,
        Run,
        Grep10M
    ];

    public static readonly string[] SupportedCases =
    [
        Compress,
        Compile,
        GccCompile,
        Run,
        Grep10M
    ];

    public static readonly string[] DefaultRunnerCases =
    [
        Compress,
        Compile,
        Run
    ];

    public static bool IsSupported(string caseName)
    {
        return SupportedCaseSet.Contains(caseName);
    }

    public static string SupportedCasesText()
    {
        return string.Join(", ", SupportedCases);
    }

    public static int DefaultProfileIterations(string caseName)
    {
        EnsureSupported(caseName);
        return caseName switch
        {
            Grep10M => 40,
            _ => 30000
        };
    }

    public static int DefaultRunnerIterations(string caseName)
    {
        EnsureSupported(caseName);
        return caseName switch
        {
            Grep10M => 1,
            _ => 30000
        };
    }

    public static int DefaultProfileTimeLimitSeconds(string caseName)
    {
        EnsureSupported(caseName);
        return caseName switch
        {
            Grep10M => 90,
            _ => 18
        };
    }

    public static int DefaultRunnerTimeoutSeconds(string caseName)
    {
        EnsureSupported(caseName);
        return caseName switch
        {
            Grep10M => 300,
            _ => 1800
        };
    }

    public static string BuildGuestScript(string caseName, int iterations, string? markerBegin = null,
        string? markerEnd = null)
    {
        EnsureSupported(caseName);

        var begin = FormatMarker(markerBegin);
        var end = FormatMarker(markerEnd);
        var compileCommand =
            $"make PORT_DIR=linux ITERATIONS={iterations} XCFLAGS=\"-O3 -DPERFORMANCE_RUN=1\" REBUILD=1 compile";

        return caseName switch
        {
            Compress => $"""
                         set -eu
                         rm -rf /tmp/coremark.tar /tmp/coremark.tar.gz /tmp/coremark-restored.tar /tmp/coremark-unpack
                         mkdir -p /tmp/coremark-unpack
                         sync >/dev/null 2>&1 || true
                         {begin}
                         tar -C / -cf /tmp/coremark.tar coremark
                         gzip -1 -c /tmp/coremark.tar > /tmp/coremark.tar.gz
                         gzip -dc /tmp/coremark.tar.gz > /tmp/coremark-restored.tar
                         tar -C /tmp/coremark-unpack -xf /tmp/coremark-restored.tar
                         test -f /tmp/coremark-unpack/coremark/Makefile
                         {end}
                         """,
            Compile or GccCompile => $"""
                                     set -eu
                                     cd /coremark
                                     make clean >/dev/null 2>&1 || true
                                     sync >/dev/null 2>&1 || true
                                     {begin}
                                     {compileCommand}
                                     test -x /coremark/coremark.exe
                                     {end}
                                     """,
            Run => $"""
                    set -eu
                    cd /coremark
                    test -x ./coremark.exe || {compileCommand} >/dev/null
                    sync >/dev/null 2>&1 || true
                    {begin}
                    ./coremark.exe 0x0 0x0 0x66 {iterations}
                    {end}
                    """,
            Grep10M => $"""
                        set -eu
                        grep_file=/tmp/podish-grep-10m.txt
                        /bin/busybox rm -f "$grep_file"
                        /bin/busybox yes 'aaaaaaaaaaaaaaaaaaaaaaaa needle bbbbbbbbbbbbbbbbbbbbbbbb' | /bin/busybox head -c 10485760 > "$grep_file"
                        /bin/busybox grep -F -c 'needle' "$grep_file" >/dev/null
                        sync >/dev/null 2>&1 || true
                        {begin}
                        i=0
                        while [ "$i" -lt {iterations} ]; do
                          /bin/busybox grep -F -c 'needle' "$grep_file" >/dev/null
                          i=$((i + 1))
                        done
                        {end}
                        """,
            _ => throw new ArgumentException($"unknown bench case: {caseName}")
        };
    }

    private static string FormatMarker(string? marker)
    {
        return string.IsNullOrWhiteSpace(marker) ? "" : $"echo {marker}";
    }

    private static void EnsureSupported(string caseName)
    {
        if (!IsSupported(caseName))
            throw new ArgumentException($"unknown bench case: {caseName}");
    }
}
