using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Display;

namespace Podish.Cli.Display;

internal sealed class FramebufferDisplayHost(
    ILoggerFactory loggerFactory,
    FramebufferDevice framebuffer) : IDisposable
{
    private readonly DisplayInputForwarder _input = new(framebuffer.Input);
    private SdlDisplayBackend? _backend;
    private IDisplayOutput? _output;

    public void Start()
    {
        _backend = new SdlDisplayBackend(loggerFactory.CreateLogger<SdlDisplayBackend>());
        _output = _backend.CreateOutput(new DisplayOutputOptions(
            framebuffer.MaximumWidth, framebuffer.MaximumHeight, "Podish Framebuffer",
            DisplayVSyncMode.Disabled, Resizable: false, HideSystemCursor: true));
    }

    public void Dispose()
    {
        _output?.Dispose();
        _backend?.Dispose();
    }

    public void RunMainLoop(Task runtimeTask)
    {
        int width = framebuffer.MaximumWidth;
        int height = framebuffer.MaximumHeight;
        var pixels = new byte[checked(width * height * 4)];
        var logger = loggerFactory.CreateLogger<FramebufferDisplayHost>();
        var output = _output ?? throw new InvalidOperationException("Framebuffer display was not started.");
        IDisplayTexture? texture = null;

        try
        {
            while (!runtimeTask.IsCompleted)
            {
                output.PumpEvents();
                if (output.IsClosed)
                    break;

                _input.Drain(output);

                if (framebuffer.TryCopyFrame(pixels, out int frameWidth, out int frameHeight))
                {
                    bool modeChanged = texture == null || texture.Width != frameWidth || texture.Height != frameHeight;
                    if (modeChanged)
                    {
                        texture?.Dispose();
                        texture = output.Renderer.CreateTexture(new DisplayTextureDescriptor(
                            frameWidth, frameHeight, DisplayPixelFormat.Xrgb8888));
                    }

                    texture!.Update(pixels.AsSpan(0, frameWidth * frameHeight * 4), frameWidth * 4);
                    output.Renderer.Blit(texture);
                    output.Present();
                    if (modeChanged)
                        logger.LogDebug("Presented framebuffer mode {Width}x{Height}", frameWidth, frameHeight);
                }

                Thread.Sleep(16);
            }
        }
        finally
        {
            texture?.Dispose();
            _input.Release();
        }
    }
}
