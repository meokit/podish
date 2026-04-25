namespace Podish.Wayland;

internal sealed class WaylandFocusManager
{
    private enum InteractiveMode
    {
        PassThrough,
        Move,
        Resize
    }

    private readonly WaylandServer _server;
    private ulong? _focusedPointerSceneSurfaceId;
    private ulong? _grabbedPointerSceneSurfaceId;
    private ulong? _focusedKeyboardSceneSurfaceId;
    private WaylandSceneHit _focusedSceneHit;
    private int _pressedPointerButtons;
    private InteractiveMode _interactiveMode;
    private int _pointerDesktopX;
    private int _pointerDesktopY;
    private int _grabOffsetX;
    private int _grabOffsetY;
    private WaylandSurfaceBounds _grabBounds;
    private XdgToplevelResizeEdge _resizeEdges;

    public WaylandFocusManager(WaylandServer server)
    {
        _server = server;
    }

    public ulong? FocusedPointerSceneSurfaceId => _focusedPointerSceneSurfaceId;
    public ulong? GrabbedPointerSceneSurfaceId => _grabbedPointerSceneSurfaceId;
    public ulong? FocusedKeyboardSceneSurfaceId => _focusedKeyboardSceneSurfaceId;

    public async ValueTask HandlePointerMotionAsync(int desktopX, int desktopY, uint time)
    {
        _pointerDesktopX = desktopX;
        _pointerDesktopY = desktopY;

        if (_interactiveMode == InteractiveMode.Move)
        {
            ProcessInteractiveMove();
            return;
        }

        if (_interactiveMode == InteractiveMode.Resize)
        {
            await ProcessInteractiveResizeAsync();
            return;
        }

        if (_server.FramePresenter is not IWaylandSceneView sceneView)
        {
            await ClearPointerFocusAsync();
            return;
        }

        if (_grabbedPointerSceneSurfaceId is ulong grabbedSceneSurfaceId &&
            _pressedPointerButtons > 0 &&
            sceneView.TryGetSurfaceBounds(grabbedSceneSurfaceId, out WaylandSurfaceBounds grabbedBounds) &&
            _server.TryGetSceneSurface(grabbedSceneSurfaceId, out WlSurfaceResource? grabbedSurface) &&
            !grabbedSurface.IsCursorRole)
        {
            _focusedPointerSceneSurfaceId = grabbedSceneSurfaceId;
            _focusedSceneHit = new WaylandSceneHit(
                grabbedSceneSurfaceId,
                WaylandSceneHitKind.Surface,
                desktopX - grabbedBounds.X,
                desktopY - grabbedBounds.Y);
            await SetCompositorCursorOverrideAsync(null);
            await DispatchPointerMotionAsync(grabbedSurface, desktopX - grabbedBounds.X, desktopY - grabbedBounds.Y, time);
            return;
        }

        if (!sceneView.TryGetSceneHitAt(desktopX, desktopY, out WaylandSceneHit hit) ||
            !_server.TryGetSceneSurface(hit.SceneSurfaceId, out WlSurfaceResource? surface))
        {
            await ClearPointerFocusAsync();
            return;
        }

        if (surface.IsCursorRole)
        {
            await ClearPointerFocusAsync();
            return;
        }

        _focusedPointerSceneSurfaceId = hit.SceneSurfaceId;
        _focusedSceneHit = hit;

        if (hit.Kind == WaylandSceneHitKind.Surface)
        {
            await SetCompositorCursorOverrideAsync(null);
            await DispatchPointerMotionAsync(surface, hit.SurfaceX, hit.SurfaceY, time);
            return;
        }

        await SetCompositorCursorOverrideAsync(MapCursorOverride(hit.Kind, hit.ResizeEdges));
        await ClearClientPointerFocusAsync();
    }

    public async ValueTask HandlePointerButtonAsync(uint button, bool pressed, uint time)
    {
        if (pressed && _focusedPointerSceneSurfaceId == null)
            await HandlePointerMotionAsync(_pointerDesktopX, _pointerDesktopY, time);

        if (pressed &&
            _focusedPointerSceneSurfaceId is ulong focusedSceneSurfaceId &&
            _focusedSceneHit.Kind != WaylandSceneHitKind.Surface &&
            _server.TryGetSceneSurface(focusedSceneSurfaceId, out WlSurfaceResource? surface))
        {
            await FocusKeyboardSurfaceAsync(focusedSceneSurfaceId);

            if (surface.XdgSurface?.Toplevel is XdgToplevelResource toplevel)
            {
                _pressedPointerButtons++;
                switch (_focusedSceneHit.Kind)
                {
                    case WaylandSceneHitKind.Titlebar:
                        await BeginInteractiveMoveAsync(toplevel);
                        return;
                    case WaylandSceneHitKind.ResizeBorder:
                        await BeginInteractiveResizeAsync(toplevel, _focusedSceneHit.ResizeEdges);
                        return;
                    case WaylandSceneHitKind.CloseButton:
                        await toplevel.SendCloseAsync();
                        return;
                    case WaylandSceneHitKind.MaximizeButton:
                        await toplevel.SetMaximizedAsync(!toplevel.IsMaximized);
                        return;
                    case WaylandSceneHitKind.MinimizeButton:
                        await toplevel.SetMinimizedAsync(true);
                        return;
                }
            }
        }

        if (pressed)
        {
            _pressedPointerButtons++;
            _grabbedPointerSceneSurfaceId ??= _focusedPointerSceneSurfaceId;
            if (_focusedPointerSceneSurfaceId is ulong focusedSurfaceId)
                await FocusKeyboardSurfaceAsync(focusedSurfaceId);
        }
        else if (_pressedPointerButtons > 0)
        {
            _pressedPointerButtons--;
            if (_pressedPointerButtons == 0)
            {
                _grabbedPointerSceneSurfaceId = null;
                _interactiveMode = InteractiveMode.PassThrough;
            }
        }

        if (_focusedSceneHit.Kind != WaylandSceneHitKind.Surface)
            return;

        foreach (WaylandClient client in _server.Clients)
        {
            foreach (WlPointerResource pointer in client.Objects.All.OfType<WlPointerResource>())
                await pointer.HandleButtonAsync(button, pressed, time);
        }
    }

    public async ValueTask HandleKeyboardKeyAsync(uint key, bool pressed, uint time)
    {
        if (_focusedKeyboardSceneSurfaceId is not ulong sceneSurfaceId ||
            !_server.TryGetSceneSurface(sceneSurfaceId, out WlSurfaceResource? surface))
            return;

        foreach (WaylandClient client in _server.Clients)
        {
            if (!ReferenceEquals(client, surface.Client))
                continue;

            foreach (WlKeyboardResource keyboard in client.Objects.All.OfType<WlKeyboardResource>())
                await keyboard.HandleKeyAsync(key, pressed, time);
        }
    }

    public async ValueTask ClearPointerFocusAsync()
    {
        if (_pressedPointerButtons > 0)
            return;

        _focusedPointerSceneSurfaceId = null;
        _grabbedPointerSceneSurfaceId = null;
        _focusedSceneHit = default;
        await SetCompositorCursorOverrideAsync(WaylandSystemCursorShape.Default);
        await ClearClientPointerFocusAsync();
    }

    public async ValueTask HandleSurfaceDestroyedAsync(ulong sceneSurfaceId)
    {
        bool affected = false;
        bool keyboardAffected = false;

        if (_focusedPointerSceneSurfaceId == sceneSurfaceId)
        {
            _focusedPointerSceneSurfaceId = null;
            _focusedSceneHit = default;
            affected = true;
        }

        if (_grabbedPointerSceneSurfaceId == sceneSurfaceId)
        {
            _grabbedPointerSceneSurfaceId = null;
            _pressedPointerButtons = 0;
            _interactiveMode = InteractiveMode.PassThrough;
            affected = true;
        }

        if (_focusedKeyboardSceneSurfaceId == sceneSurfaceId)
        {
            _focusedKeyboardSceneSurfaceId = null;
            keyboardAffected = true;
            foreach (WaylandClient client in _server.Clients)
            {
                foreach (WlKeyboardResource keyboard in client.Objects.All.OfType<WlKeyboardResource>())
                    await keyboard.ClearFocusAsync();
            }

            await _server.HandleKeyboardFocusSelectionChangedAsync();
            await _server.HandleKeyboardFocusTextInputChangedAsync();
        }

        if (affected)
        {
            await ClearPointerFocusAsync();
            await RefreshPointerFocusAsync();
        }

        if (keyboardAffected)
            await FocusTopmostRemainingToplevelAsync();
    }

    public async ValueTask BeginInteractiveMoveAsync(XdgToplevelResource toplevel)
    {
        if (_server.FramePresenter is not IWaylandDesktopSceneController desktopSceneController ||
            _server.FramePresenter is not IWaylandSceneView sceneView ||
            !sceneView.TryGetWindowBounds(toplevel.Surface.Surface.SceneSurfaceId, out WaylandSurfaceBounds bounds))
            return;

        await FocusKeyboardSurfaceAsync(toplevel.Surface.Surface.SceneSurfaceId);
        desktopSceneController.RaiseSurface(toplevel.Surface.Surface.SceneSurfaceId);

        _grabbedPointerSceneSurfaceId = toplevel.Surface.Surface.SceneSurfaceId;
        _interactiveMode = InteractiveMode.Move;
        _grabBounds = bounds;
        _grabOffsetX = _pointerDesktopX - bounds.X;
        _grabOffsetY = _pointerDesktopY - bounds.Y;
    }

    public async ValueTask BeginInteractiveResizeAsync(XdgToplevelResource toplevel, XdgToplevelResizeEdge edges)
    {
        if (_server.FramePresenter is not IWaylandDesktopSceneController desktopSceneController ||
            _server.FramePresenter is not IWaylandSceneView sceneView ||
            !sceneView.TryGetWindowBounds(toplevel.Surface.Surface.SceneSurfaceId, out WaylandSurfaceBounds bounds))
            return;

        await FocusKeyboardSurfaceAsync(toplevel.Surface.Surface.SceneSurfaceId);
        desktopSceneController.RaiseSurface(toplevel.Surface.Surface.SceneSurfaceId);

        _grabbedPointerSceneSurfaceId = toplevel.Surface.Surface.SceneSurfaceId;
        _interactiveMode = InteractiveMode.Resize;
        _resizeEdges = edges;
        _grabBounds = bounds;
        int borderX = bounds.X + ((edges & XdgToplevelResizeEdge.Right) != 0 ? bounds.Width : 0);
        int borderY = bounds.Y + ((edges & XdgToplevelResizeEdge.Bottom) != 0 ? bounds.Height : 0);
        _grabOffsetX = _pointerDesktopX - borderX;
        _grabOffsetY = _pointerDesktopY - borderY;
    }

    private async ValueTask FocusKeyboardSurfaceAsync(ulong sceneSurfaceId)
    {
        if (_focusedKeyboardSceneSurfaceId == sceneSurfaceId)
        {
            if (_server.FramePresenter is IWaylandDesktopSceneController desktopSceneController)
                desktopSceneController.RaiseSurface(sceneSurfaceId);
            return;
        }

        if (_focusedKeyboardSceneSurfaceId is ulong oldSceneSurfaceId &&
            _server.TryGetSceneSurface(oldSceneSurfaceId, out WlSurfaceResource? oldSurface) &&
            oldSurface.XdgSurface?.Toplevel is XdgToplevelResource oldToplevel)
            await oldToplevel.SetActivatedAsync(false);

        _focusedKeyboardSceneSurfaceId = sceneSurfaceId;

        if (_server.TryGetSceneSurface(sceneSurfaceId, out WlSurfaceResource? newSurface))
        {
            if (newSurface.XdgSurface?.Toplevel is XdgToplevelResource newToplevel)
                await newToplevel.SetActivatedAsync(true);

            foreach (WaylandClient client in _server.Clients)
            {
                bool ownsSurface = ReferenceEquals(client, newSurface.Client);
                foreach (WlKeyboardResource keyboard in client.Objects.All.OfType<WlKeyboardResource>())
                {
                    if (ownsSurface)
                        await keyboard.FocusAsync(newSurface.ObjectId);
                    else
                        await keyboard.ClearFocusAsync();
                }
            }
        }

        if (_server.FramePresenter is IWaylandDesktopSceneController sceneController)
            sceneController.RaiseSurface(sceneSurfaceId);

        await _server.HandleKeyboardFocusSelectionChangedAsync();
        await _server.HandleKeyboardFocusTextInputChangedAsync();
    }

    private async ValueTask DispatchPointerMotionAsync(WlSurfaceResource targetSurface, int surfaceX, int surfaceY, uint time)
    {
        foreach (WaylandClient client in _server.Clients)
        {
            bool ownsSurface = ReferenceEquals(client, targetSurface.Client);
            foreach (WlPointerResource pointer in client.Objects.All.OfType<WlPointerResource>())
            {
                if (ownsSurface)
                    await pointer.HandleMotionAsync(targetSurface.ObjectId, surfaceX, surfaceY, time);
                else
                    await pointer.ClearFocusAsync();
            }
        }
    }

    private async ValueTask ClearClientPointerFocusAsync()
    {
        foreach (WaylandClient client in _server.Clients)
        {
            foreach (WlPointerResource pointer in client.Objects.All.OfType<WlPointerResource>())
                await pointer.ClearFocusAsync();
        }
    }

    private async ValueTask RefreshPointerFocusAsync()
    {
        if (_pressedPointerButtons > 0 || _interactiveMode != InteractiveMode.PassThrough)
            return;

        await HandlePointerMotionAsync(_pointerDesktopX, _pointerDesktopY, 0);
    }

    private async ValueTask SetCompositorCursorOverrideAsync(WaylandSystemCursorShape? shape)
    {
        foreach (WaylandClient client in _server.Clients)
        {
            foreach (WlPointerResource pointer in client.Objects.All.OfType<WlPointerResource>())
                await pointer.SetCompositorCursorOverrideAsync(shape);
        }
    }

    private static WaylandSystemCursorShape MapCursorOverride(WaylandSceneHitKind kind, XdgToplevelResizeEdge edges)
    {
        return kind switch
        {
            WaylandSceneHitKind.Titlebar => WaylandSystemCursorShape.Default,
            WaylandSceneHitKind.CloseButton => WaylandSystemCursorShape.Pointer,
            WaylandSceneHitKind.MaximizeButton => WaylandSystemCursorShape.Pointer,
            WaylandSceneHitKind.MinimizeButton => WaylandSystemCursorShape.Pointer,
            WaylandSceneHitKind.ResizeBorder => edges switch
            {
                XdgToplevelResizeEdge.Left or XdgToplevelResizeEdge.Right => WaylandSystemCursorShape.EwResize,
                XdgToplevelResizeEdge.Top or XdgToplevelResizeEdge.Bottom => WaylandSystemCursorShape.NsResize,
                XdgToplevelResizeEdge.TopLeft or XdgToplevelResizeEdge.BottomRight => WaylandSystemCursorShape.NwseResize,
                XdgToplevelResizeEdge.TopRight or XdgToplevelResizeEdge.BottomLeft => WaylandSystemCursorShape.NeswResize,
                _ => WaylandSystemCursorShape.Default
            },
            _ => WaylandSystemCursorShape.Default
        };
    }

    private async ValueTask FocusTopmostRemainingToplevelAsync()
    {
        if (_server.FramePresenter is not IWaylandSceneDebugView debugView)
            return;

        foreach ((ulong sceneSurfaceId, _) in debugView.SnapshotSurfaceBounds().Reverse())
        {
            if (!_server.TryGetSceneSurface(sceneSurfaceId, out WlSurfaceResource? surface) ||
                surface.IsCursorRole ||
                surface.XdgSurface?.Toplevel is not XdgToplevelResource toplevel ||
                toplevel.IsMinimized)
            {
                continue;
            }

            await FocusKeyboardSurfaceAsync(sceneSurfaceId);
            return;
        }
    }

    private void ProcessInteractiveMove()
    {
        if (_grabbedPointerSceneSurfaceId is not ulong grabbedSceneSurfaceId ||
            _server.FramePresenter is not IWaylandDesktopSceneController desktopSceneController ||
            !_server.TryGetSceneSurface(grabbedSceneSurfaceId, out WlSurfaceResource? surface))
            return;

        var bounds = new WaylandSurfaceBounds(
            _pointerDesktopX - _grabOffsetX,
            _pointerDesktopY - _grabOffsetY,
            _grabBounds.Width,
            _grabBounds.Height);

        desktopSceneController.SetWindowBounds(grabbedSceneSurfaceId, bounds);
        desktopSceneController.RaiseSurface(grabbedSceneSurfaceId);
        surface.UpdateChildSubsurfacePlacements();
        _focusedPointerSceneSurfaceId = grabbedSceneSurfaceId;
    }

    private async ValueTask ProcessInteractiveResizeAsync()
    {
        if (_grabbedPointerSceneSurfaceId is not ulong grabbedSceneSurfaceId ||
            _server.FramePresenter is not IWaylandDesktopSceneController desktopSceneController ||
            !_server.TryGetSceneSurface(grabbedSceneSurfaceId, out WlSurfaceResource? surface))
            return;

        int newLeft = _grabBounds.X;
        int newRight = _grabBounds.X + _grabBounds.Width;
        int newTop = _grabBounds.Y;
        int newBottom = _grabBounds.Y + _grabBounds.Height;
        int borderX = _pointerDesktopX - _grabOffsetX;
        int borderY = _pointerDesktopY - _grabOffsetY;

        if ((_resizeEdges & XdgToplevelResizeEdge.Top) != 0)
            newTop = Math.Min(borderY, newBottom - 1);
        else if ((_resizeEdges & XdgToplevelResizeEdge.Bottom) != 0)
            newBottom = Math.Max(borderY, newTop + 1);

        if ((_resizeEdges & XdgToplevelResizeEdge.Left) != 0)
            newLeft = Math.Min(borderX, newRight - 1);
        else if ((_resizeEdges & XdgToplevelResizeEdge.Right) != 0)
            newRight = Math.Max(borderX, newLeft + 1);

        var bounds = new WaylandSurfaceBounds(newLeft, newTop, newRight - newLeft, newBottom - newTop);

        if (surface.XdgSurface?.Toplevel is XdgToplevelResource resizeToplevel &&
            _server.FramePresenter is IWaylandSceneView resizeSceneView &&
            resizeSceneView.TryGetSurfaceBounds(grabbedSceneSurfaceId, out WaylandSurfaceBounds currentContentBounds) &&
            resizeSceneView.TryGetWindowBounds(grabbedSceneSurfaceId, out WaylandSurfaceBounds currentWindowBounds))
        {
            int chromeLeft = currentContentBounds.X - currentWindowBounds.X;
            int chromeTop = currentContentBounds.Y - currentWindowBounds.Y;
            int chromeRight = (currentWindowBounds.X + currentWindowBounds.Width) -
                              (currentContentBounds.X + currentContentBounds.Width);
            int chromeBottom = (currentWindowBounds.Y + currentWindowBounds.Height) -
                               (currentContentBounds.Y + currentContentBounds.Height);

            int proposedContentWidth = Math.Max(1, bounds.Width - chromeLeft - chromeRight);
            int proposedContentHeight = Math.Max(1, bounds.Height - chromeTop - chromeBottom);
            (int clampedContentWidth, int clampedContentHeight) =
                resizeToplevel.ClampConfiguredContentSize(proposedContentWidth, proposedContentHeight);

            int clampedOuterWidth = clampedContentWidth + chromeLeft + chromeRight;
            int clampedOuterHeight = clampedContentHeight + chromeTop + chromeBottom;

            if ((_resizeEdges & XdgToplevelResizeEdge.Left) != 0)
                bounds = bounds with { X = newRight - clampedOuterWidth, Width = clampedOuterWidth };
            else if ((_resizeEdges & XdgToplevelResizeEdge.Right) != 0)
                bounds = bounds with { Width = clampedOuterWidth };

            if ((_resizeEdges & XdgToplevelResizeEdge.Top) != 0)
                bounds = bounds with { Y = newBottom - clampedOuterHeight, Height = clampedOuterHeight };
            else if ((_resizeEdges & XdgToplevelResizeEdge.Bottom) != 0)
                bounds = bounds with { Height = clampedOuterHeight };
        }

        desktopSceneController.SetWindowBounds(grabbedSceneSurfaceId, bounds);
        desktopSceneController.RaiseSurface(grabbedSceneSurfaceId);
        surface.UpdateChildSubsurfacePlacements();
        _focusedPointerSceneSurfaceId = grabbedSceneSurfaceId;

        if (surface.XdgSurface?.Toplevel is XdgToplevelResource toplevel &&
            _server.FramePresenter is IWaylandSceneView sceneView &&
            sceneView.TryGetSurfaceBounds(grabbedSceneSurfaceId, out WaylandSurfaceBounds contentBounds))
        {
            await toplevel.SendConfigureAsync(contentBounds.Width, contentBounds.Height, resizing: true);
        }
    }
}
