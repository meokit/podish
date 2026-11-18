using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Display;

namespace Podish.Cli.Display;

internal sealed class FramebufferDisplayHost(
    ILoggerFactory loggerFactory,
    FramebufferDevice framebuffer)
{
    private readonly HashSet<uint> _pressedKeys = [];
    private int _buttons;

    public void RunMainLoop(Task runtimeTask)
    {
        int width = framebuffer.MaximumWidth;
        int height = framebuffer.MaximumHeight;
        var pixels = new byte[checked(width * height * 4)];
        var logger = loggerFactory.CreateLogger<FramebufferDisplayHost>();
        using var backend = new SdlDisplayBackend(loggerFactory.CreateLogger<SdlDisplayBackend>());
        using var output = backend.CreateOutput(new DisplayOutputOptions(
            width, height, "Podish Framebuffer", DisplayVSyncMode.Disabled, Resizable: false, HideSystemCursor: true));
        IDisplayTexture? texture = null;

        try
        {
            while (!runtimeTask.IsCompleted)
            {
                output.PumpEvents();
                if (output.IsClosed)
                    break;

                foreach (DisplayInputEvent input in output.DrainInputEvents())
                    ForwardInput(input);

                if (framebuffer.TryCopyFrame(pixels, out int frameWidth, out int frameHeight))
                {
                    if (texture == null || texture.Width != frameWidth || texture.Height != frameHeight)
                    {
                        texture?.Dispose();
                        texture = output.Renderer.CreateTexture(new DisplayTextureDescriptor(
                            frameWidth, frameHeight, DisplayPixelFormat.Xrgb8888));
                        logger.LogDebug("Presenting framebuffer mode {Width}x{Height}", frameWidth, frameHeight);
                    }

                    texture.Update(pixels.AsSpan(0, frameWidth * frameHeight * 4), frameWidth * 4);
                    output.Renderer.Blit(texture);
                    output.Present();
                }

                Thread.Sleep(16);
            }
        }
        finally
        {
            texture?.Dispose();
            ReleaseInput();
        }
    }

    private void ForwardInput(DisplayInputEvent input)
    {
        switch (input.Kind)
        {
            case DisplayInputEventKind.KeyboardKey:
                if (input.Pressed)
                    _pressedKeys.Add(input.Key);
                else
                    _pressedKeys.Remove(input.Key);
                framebuffer.Input.PublishKey(input.Key, input.Pressed);
                break;
            case DisplayInputEventKind.PointerMotion:
                framebuffer.Input.PublishPointer(input.DeltaX, input.DeltaY, _buttons);
                break;
            case DisplayInputEventKind.PointerButton:
                int button = input.Button switch
                {
                    DisplayPointerButton.Left => 1,
                    DisplayPointerButton.Right => 2,
                    DisplayPointerButton.Middle => 4,
                    _ => 0
                };
                _buttons = input.Pressed ? _buttons | button : _buttons & ~button;
                framebuffer.Input.PublishPointer(0, 0, _buttons);
                break;
            case DisplayInputEventKind.WindowFocusLost:
                ReleaseInput();
                break;
        }
    }

    private void ReleaseInput()
    {
        foreach (uint key in _pressedKeys)
            framebuffer.Input.PublishKey(key, false);
        _pressedKeys.Clear();

        if (_buttons != 0)
        {
            _buttons = 0;
            framebuffer.Input.PublishPointer(0, 0, 0);
        }
    }
}
