using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class DiscoveryInterfacePinFailureTests
{
    private sealed class FailingRawSocket : IDiscoveryRawSocket
    {
        internal bool FailSet { get; init; }
        internal bool FailGet { get; init; }
        internal int ReadLength { get; init; } = sizeof(int);
        internal bool Disposed { get; private set; }
        internal int SetCount { get; private set; }
        internal int GetCount { get; private set; }

        public void Set(int level, int option, byte[] value)
        {
            SetCount++;
            if (FailSet) throw new IOException("Synthetic set failure.");
        }

        public int Get(int level, int option, byte[] value)
        {
            GetCount++;
            if (FailGet) throw new IOException("Synthetic get failure.");
            BitConverter.GetBytes(7).CopyTo(value, 0);
            return ReadLength;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void SetFailureDisposesSocketWithoutReadingBack()
    {
        var socket = new FailingRawSocket { FailSet = true };
        Assert.Throws<IOException>(() => DiscoveryInterfacePin.Configure(socket, DiscoveryNativePlatform.Windows, 7));
        Assert.True(socket.Disposed);
        Assert.Equal(1, socket.SetCount);
        Assert.Equal(0, socket.GetCount);
    }

    [Fact]
    public void GetFailureDisposesSocket()
    {
        var socket = new FailingRawSocket { FailGet = true };
        Assert.Throws<IOException>(() => DiscoveryInterfacePin.Configure(socket, DiscoveryNativePlatform.Windows, 7));
        Assert.True(socket.Disposed);
        Assert.Equal(1, socket.SetCount);
        Assert.Equal(1, socket.GetCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ShortReadDisposesSocketEvenWhenBufferLooksCorrect(int readLength)
    {
        var socket = new FailingRawSocket { ReadLength = readLength };
        Assert.Throws<InvalidOperationException>(() => DiscoveryInterfacePin.Configure(socket, DiscoveryNativePlatform.Windows, 7));
        Assert.True(socket.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidInterfaceIndexDisposesSocketWithoutConfiguringIt(int index)
    {
        var socket = new FailingRawSocket();
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscoveryInterfacePin.Configure(socket, DiscoveryNativePlatform.Windows, index));
        Assert.True(socket.Disposed);
        Assert.Equal(0, socket.SetCount);
        Assert.Equal(0, socket.GetCount);
    }
}
