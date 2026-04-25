using System.Reflection;
using System.Runtime.InteropServices;

namespace Fiberish.SilkFS.Tests;

public class SilkSqliteDriverTests
{
    [Fact]
    public void Dispose_ClosesUnderlyingSafeHandles_BeforeFinalizerThreadRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"silkfs-sqlite-driver-{Guid.NewGuid():N}");
        try
        {
            var repo = new SilkRepository(SilkFsOptions.FromSource(root));
            repo.Initialize();

            SafeHandle? dbHandle;
            SafeHandle? stmtHandle;
            using (var conn = new SilkSqliteConnection(repo.Options.MetadataPath))
            {
                dbHandle = conn.Handle;
                using (var stmt = conn.Prepare("SELECT 1"u8, persistent: false))
                {
                    stmtHandle = GetStatementHandle(stmt);
                    Assert.False(stmtHandle.IsClosed);
                }

                Assert.True(stmtHandle!.IsClosed);
            }

            Assert.True(dbHandle!.IsClosed);

            // The regression here is that explicit Dispose must close the SafeHandle itself,
            // rather than leaving native sqlite cleanup to the finalizer thread.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.True(dbHandle.IsClosed);
            Assert.True(stmtHandle!.IsClosed);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static SafeHandle GetStatementHandle(SilkSqliteStatement statement)
    {
        var field = typeof(SilkSqliteStatement).GetField("_stmt", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<SafeHandle>(field!.GetValue(statement));
    }
}
