namespace PhoneDisplay.Host.Capture;

public interface IVideoCapturer : IDisposable
{
    int Width { get; }
    int Height { get; }
    string? LastCaptureError { get; }
    event Action<int, int>? ResolutionChanged;
    bool TryCaptureFrame(int timeoutMs, out ReadOnlySpan<byte> bgraFrame, out bool isNewDesktopImage);
}
