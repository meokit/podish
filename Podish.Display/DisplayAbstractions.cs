namespace Podish.Display;

public enum DisplayVSyncMode
{
    Disabled,
    Enabled,
    Adaptive
}

public enum DisplayRendererKind
{
    Software,
    Accelerated
}

public enum DisplayTextureAccess
{
    Static,
    Streaming,
    RenderTarget
}

public enum DisplayPixelFormat
{
    Argb8888,
    Xrgb8888
}

public readonly record struct DisplayColor(byte R, byte G, byte B, byte A);

public readonly record struct DisplayRect(int X, int Y, int Width, int Height);
public readonly record struct DisplaySize(int Width, int Height);

public enum DisplayPointerButton : uint
{
    Left = 0x110,
    Right = 0x111,
    Middle = 0x112
}

public enum DisplayInputEventKind
{
    PointerMotion,
    PointerButton,
    PointerLeave,
    WindowFocusLost,
    WindowFocusGained,
    KeyboardKey,
    TextInput,
    TextEditing
}

public readonly record struct DisplayInputEvent(
    DisplayInputEventKind Kind,
    int X = 0,
    int Y = 0,
    uint Timestamp = 0,
    DisplayPointerButton Button = DisplayPointerButton.Left,
    bool Pressed = false,
    uint Key = 0,
    string? Text = null,
    int CursorBegin = 0,
    int CursorEnd = 0);

public readonly record struct DisplayOutputOptions(
    int Width,
    int Height,
    string Title,
    DisplayVSyncMode VSyncMode = DisplayVSyncMode.Enabled,
    bool AllowHighDpi = false,
    bool Resizable = true,
    bool Hidden = false);

public readonly record struct DisplayTextureDescriptor(
    int Width,
    int Height,
    DisplayPixelFormat Format,
    DisplayTextureAccess Access = DisplayTextureAccess.Streaming);

public readonly record struct DisplayCursorDescriptor(
    int Width,
    int Height,
    int HotspotX,
    int HotspotY,
    DisplayPixelFormat Format,
    byte[] Pixels,
    int Pitch);

public enum DisplaySystemCursor
{
    Arrow = 0,
    IBeam = 1,
    Wait = 2,
    Crosshair = 3,
    WaitArrow = 4,
    SizeNwse = 5,
    SizeNesw = 6,
    SizeWe = 7,
    SizeNs = 8,
    SizeAll = 9,
    No = 10,
    Hand = 11
}

public interface IDisplayBackend : IDisposable
{
    string Name { get; }

    IDisplayOutput CreateOutput(DisplayOutputOptions options);
}

public interface IDisplayOutput : IDisposable
{
    int Width { get; }
    int Height { get; }
    bool IsClosed { get; }
    DisplayVSyncMode VSyncMode { get; }
    IDisplayRenderer Renderer { get; }

    void PumpEvents();
    IReadOnlyList<DisplayInputEvent> DrainInputEvents();
    bool TryDequeueResize(out DisplaySize size);
    void StartTextInput();
    void StopTextInput();
    void SetTextInputRect(DisplayRect rect);
    void SetCursor(DisplayCursorDescriptor cursor);
    void SetSystemCursor(DisplaySystemCursor cursor);
    void ClearCursor();
    void Present();
}

public interface IDisplayRenderer : IDisposable
{
    DisplayRendererKind Kind { get; }

    IDisplayTexture CreateTexture(DisplayTextureDescriptor descriptor);
    void SetLogicalSize(int width, int height);
    void Clear(DisplayColor color);
    void FillRect(DisplayRect rect, DisplayColor color);
    void Blit(IDisplayTexture texture, DisplayRect? source = null, DisplayRect? destination = null);
    void Present();
}

public interface IDisplayTexture : IDisposable
{
    int Width { get; }
    int Height { get; }
    DisplayPixelFormat Format { get; }

    void Update(ReadOnlySpan<byte> pixels, int pitch);
    void Update(DisplayRect rect, ReadOnlySpan<byte> pixels, int pitch);
}
