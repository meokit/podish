using Fiberish.VFS;
using Podish.Display;

namespace Podish.Cli.Display;

internal sealed class DisplayInputForwarder(VirtualConsoleInput input)
{
    private readonly HashSet<uint> _pressedKeys = [];
    private int _buttons;

    internal void Drain(IDisplayOutput output)
    {
        foreach (var item in output.DrainInputEvents())
        {
            var position = (item.X, item.Y, output.Width, output.Height);
            switch (item.Kind)
            {
                case DisplayInputEventKind.KeyboardKey:
                    if (item.Pressed) _pressedKeys.Add(item.Key);
                    else _pressedKeys.Remove(item.Key);
                    input.PublishKey(item.Key, item.Pressed);
                    break;
                case DisplayInputEventKind.PointerMotion:
                    input.PublishPointer(item.DeltaX, item.DeltaY, _buttons, position);
                    break;
                case DisplayInputEventKind.PointerWheel:
                    input.PublishWheel(item.DeltaX, item.DeltaY);
                    break;
                case DisplayInputEventKind.PointerButton:
                    int button = item.Button switch
                    {
                        DisplayPointerButton.Left => 1,
                        DisplayPointerButton.Right => 2,
                        DisplayPointerButton.Middle => 4,
                        _ => 0
                    };
                    _buttons = item.Pressed ? _buttons | button : _buttons & ~button;
                    input.PublishPointer(0, 0, _buttons, position);
                    break;
                case DisplayInputEventKind.WindowFocusLost:
                    Release();
                    break;
            }
        }
    }

    internal void Release()
    {
        foreach (uint key in _pressedKeys)
            input.PublishKey(key, false);
        _pressedKeys.Clear();

        if (_buttons == 0) return;
        _buttons = 0;
        input.PublishPointer(0, 0, 0);
    }
}
