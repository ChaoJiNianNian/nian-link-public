namespace Nian.Input.Windows;

public interface IKeyboardCapture : IDisposable
{
    bool IsRunning { get; }
    void Start(TimeSpan lease);
    bool RenewLease(TimeSpan lease);
    void Stop(string reason);
}

public interface ICaptureFactory
{
    IKeyboardCapture Create(Func<InputPacket, bool> enqueue, Action<string> stopped);
}
