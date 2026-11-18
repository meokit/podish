namespace Podish.Core.Graphics;

/// <summary>The host window and native display owned by the UI thread.</summary>
public readonly record struct KmsEglWindow(nint Handle, nint Display = 0, int Platform = 0,
    Func<bool>? CanPresent = null);
