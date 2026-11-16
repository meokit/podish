using Fiberish.Native;

namespace Fiberish.Core;

// Only failure paths allocate these exceptions. Preserve precise open_exec errno
// without encoding/parsing paths or messages on the successful loading path.
internal sealed class ExecFileException(Errno error, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public Errno Error { get; } = error;
}

internal sealed class FatalExecException(string message, Exception? inner = null) : IOException(message, inner);

internal enum ExecCommitCheckpoint
{
    BeforeFdUnshare,
    BeforeThreadTeardown,
    BeforeAddressSpaceSwitch,
    AfterAddressSpaceSwitch
}
