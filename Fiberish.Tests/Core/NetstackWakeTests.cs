using System.Text;
using Fiberish.Core.Net;
using Xunit;

namespace Fiberish.Tests.Core;

public class NetstackWakeTests
{
    [Fact]
    public void NotifyCallback_SetsEventOnStateChange()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var ev = new AutoResetEvent(false);
        var token = NetstackWakeRegistry.Register(ev);

        try
        {
            netns.BindWakeCallback(token);

            // Initially unset
            Assert.False(ev.WaitOne(0));

            // State change: Bind an active socket
            using var listener = netns.CreateTcpListener();
            listener.Listen(8080);

            // Should trigger notify from inside FFI since it changes state
            netns.Poll(0);

            Assert.True(ev.WaitOne(100), "AutoResetEvent should be set by Rust notify callback");
        }
        finally
        {
            netns.UnbindWakeCallback();
            NetstackWakeRegistry.Unregister(token);
        }
    }

    [Fact]
    public void NotifyCallback_CoalescesUntilClear()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var ev = new AutoResetEvent(false);
        var token = NetstackWakeRegistry.Register(ev);

        try
        {
            netns.BindWakeCallback(token);

            using var server = netns.CreateUdpSocket();
            server.Bind(19210);
            netns.Poll(0);

            // Consume the first edge
            Assert.True(ev.WaitOne(100));

            // Without ClearNotify, further sends shouldn't trigger the callback again
            var payload = Encoding.ASCII.GetBytes("udp-loopback");
            server.SendTo(0x7F000001u, 19210, payload);

            Assert.False(ev.WaitOne(0), "Should coalesce and not trigger again before ClearNotify");

            // Now clear
            netns.ClearNotify();

            // Poll to process the send, which should trigger a new state change
            netns.Poll(0);

            Assert.True(ev.WaitOne(100), "Should trigger again after ClearNotify");
        }
        finally
        {
            netns.UnbindWakeCallback();
            NetstackWakeRegistry.Unregister(token);
        }
    }

    [Fact]
    public void Unbind_PreventsFurtherCallbacks()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var ev = new AutoResetEvent(false);
        var token = NetstackWakeRegistry.Register(ev);

        try
        {
            netns.BindWakeCallback(token);

            using var listener = netns.CreateTcpListener();
            listener.Listen(9090);
            netns.Poll(0);

            Assert.True(ev.WaitOne(100));

            // Now unbind
            netns.UnbindWakeCallback();
            netns.ClearNotify();

            // Perform another state change
            using var client = netns.CreateTcpStream();
            client.Connect(0x0A590002u, 9090);
            netns.Poll(0);

            Assert.False(ev.WaitOne(100), "Should not trigger after UnbindWakeCallback");
        }
        finally
        {
            NetstackWakeRegistry.Unregister(token);
        }
    }
}