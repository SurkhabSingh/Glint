using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.System.UserProfile;
using Windows.Storage.Streams;
using WinRT;
using static Vortice.Direct3D11.D3D11;

namespace Glint.Phase0.Core;

public interface IOcrCaptureService
{
    Task<OcrCaptureResult> CaptureAndRecognizeAsync(
        ForegroundWindowInfo window,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One captured frame, held only in memory. The picture check reads its
/// signature first; text is recognized only when that check says the screen
/// changed in a way worth reading.
/// </summary>
public interface IScreenFrame : IDisposable
{
    FrameSignature Signature { get; }

    TimeSpan CaptureElapsed { get; }

    /// <param name="cropScreenBounds">
    /// Screen rectangle to keep text from, such as a browser's page area; null
    /// keeps the whole window.
    /// </param>
    /// <param name="ignoreCells">
    /// Picture-check cells (32 x 32 over the frame) whose text is ignored:
    /// the parts of the screen that keep moving on their own, like a playing
    /// video, whose frames and subtitles are not the page.
    /// </param>
    Task<OcrCaptureResult> RecognizeAsync(
        WindowBounds? cropScreenBounds,
        bool[]? ignoreCells = null,
        CancellationToken cancellationToken = default);
}

public interface IFrameCaptureService
{
    Task<IScreenFrame> CaptureFrameAsync(
        ForegroundWindowInfo window,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsGraphicsCaptureService : IOcrCaptureService, IFrameCaptureService
{
    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private readonly bool _forceSoftwareDevice;

    public WindowsGraphicsCaptureService(bool forceSoftwareDevice = false)
    {
        _forceSoftwareDevice = forceSoftwareDevice;
    }

    public async Task<OcrCaptureResult> CaptureAndRecognizeAsync(
        ForegroundWindowInfo window,
        CancellationToken cancellationToken = default)
    {
        using var frame = await CaptureFrameAsync(window, cancellationToken).ConfigureAwait(false);
        return await frame.RecognizeAsync(null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IScreenFrame> CaptureFrameAsync(
        ForegroundWindowInfo window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        var item = CreateItemForWindow(window.Handle);
        var captureSize = item.Size;
        if (captureSize.Width <= 0 || captureSize.Height <= 0)
        {
            captureSize = new Windows.Graphics.SizeInt32(
                window.Bounds.Width,
                window.Bounds.Height);
        }
        if (captureSize.Width <= 0 || captureSize.Height <= 0)
        {
            throw new InvalidOperationException(
                "Windows Graphics Capture reported an empty target size.");
        }

        using var nativeDevice = CreateNativeDevice(_forceSoftwareDevice);
        using var nativeContext = nativeDevice.ImmediateContext;
        using var dxgiDevice = nativeDevice.QueryInterface<IDXGIDevice>();
        var winRtDevice = CreateWinRtDevice(dxgiDevice.NativePointer);
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            captureSize);
        using var session = framePool.CreateCaptureSession(item);
        TryDisableCaptureBorder(session);
        TryDisableCursorCapture(session);

        var captureTimer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Direct3D11CaptureFrame frame;
        try
        {
            frame = await WaitForFrameAsync(framePool, session, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Windows Graphics Capture did not deliver a frame within five seconds.");
        }

        using (frame)
        {
            captureTimer.Stop();
            var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface,
                BitmapAlphaMode.Ignore);
            try
            {
                var signature = SignatureOf(bitmap);
                return new WindowsScreenFrame(bitmap, signature, captureTimer.Elapsed, window.Bounds);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }

    private static FrameSignature SignatureOf(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var pointer, out var capacity);
        var available = capacity > (uint)plane.StartIndex ? capacity - (uint)plane.StartIndex : 0u;
        var length = (int)Math.Min(available, (uint)(plane.Stride * plane.Height));
        var pixels = new byte[length];
        Marshal.Copy(pointer + plane.StartIndex, pixels, 0, length);
        return FrameSignature.FromBgra(pixels, plane.Width, plane.Height, plane.Stride);
    }

    private sealed class WindowsScreenFrame(
        SoftwareBitmap bitmap,
        FrameSignature signature,
        TimeSpan captureElapsed,
        WindowBounds windowBounds) : IScreenFrame
    {
        public FrameSignature Signature { get; } = signature;

        public TimeSpan CaptureElapsed { get; } = captureElapsed;

        public async Task<OcrCaptureResult> RecognizeAsync(
            WindowBounds? cropScreenBounds,
            bool[]? ignoreCells = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ocrBitmap = await ResizeForOcrIfNeededAsync(bitmap).ConfigureAwait(false);
            var keep = CropInOcrPixels(cropScreenBounds, ocrBitmap);
            var ocrTimer = Stopwatch.StartNew();
            var recognized = await RecognizeWithBestInstalledLanguageAsync(
                    ocrBitmap,
                    keep,
                    ignoreCells is { Length: FrameSignature.CellCount } && ignoreCells.Any(cell => cell) ? ignoreCells : null)
                .ConfigureAwait(false);
            ocrTimer.Stop();
            return new(
                recognized.Text,
                ocrBitmap.PixelWidth,
                ocrBitmap.PixelHeight,
                CaptureElapsed,
                ocrTimer.Elapsed,
                recognized.Error,
                recognized.LanguageTag);
        }

        /// <summary>
        /// Maps a screen rectangle onto the OCR bitmap. Lines are kept by their
        /// centre, so the few pixels of border Windows adds around
        /// GetWindowRect never matter.
        /// </summary>
        private Rect? CropInOcrPixels(WindowBounds? crop, SoftwareBitmap ocrBitmap)
        {
            if (crop is not { } area || windowBounds.Width == 0 || windowBounds.Height == 0)
            {
                return null;
            }

            var scaleX = (double)ocrBitmap.PixelWidth / windowBounds.Width;
            var scaleY = (double)ocrBitmap.PixelHeight / windowBounds.Height;
            var left = (area.Left - windowBounds.Left) * scaleX;
            var top = (area.Top - windowBounds.Top) * scaleY;
            var width = area.Width * scaleX;
            var height = area.Height * scaleY;
            return width < 40 || height < 40 ? null : new Rect(left, top, width, height);
        }

        public void Dispose() => bitmap.Dispose();
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out nint buffer, out uint capacity);
    }

    private static void TryDisableCursorCapture(GraphicsCaptureSession session)
    {
        try
        {
            // The pointer moving is not the screen changing.
            session.IsCursorCaptureEnabled = false;
        }
        catch
        {
            // Older Windows builds lack the property; the picture check
            // tolerates a cursor as a cell or two of movement.
        }
    }

    public static async Task<bool> RequestBorderlessAccessAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            return false;
        }

        var status = await GraphicsCaptureAccess.RequestAccessAsync(
            GraphicsCaptureAccessKind.Borderless);
        return status == AppCapabilityAccessStatus.Allowed;
    }

    private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            return;
        }

        try
        {
            session.IsBorderRequired = false;
        }
        catch
        {
            // Consent or package capability may be unavailable; bordered capture still works.
        }
    }

    public static GraphicsDeviceCompatibility ProbeGraphicsDevices()
    {
        var hardware = TryCreateNativeDevice(DriverType.Hardware);
        hardware.Device?.Dispose();
        var software = TryCreateNativeDevice(DriverType.Warp);
        software.Device?.Dispose();
        return new(
            hardware.Device is not null,
            software.Device is not null,
            hardware.Error,
            software.Error);
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint handle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var pointer = interop.CreateForWindow(handle, GraphicsCaptureItemGuid);
        if (pointer == 0)
        {
            throw new InvalidOperationException("CreateForWindow returned no capture item.");
        }

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static ID3D11Device CreateNativeDevice(bool forceSoftwareDevice)
    {
        if (!forceSoftwareDevice)
        {
            var hardware = TryCreateNativeDevice(DriverType.Hardware);
            if (hardware.Device is not null)
            {
                return hardware.Device;
            }
        }

        var software = TryCreateNativeDevice(DriverType.Warp);
        return software.Device
            ?? throw new InvalidOperationException(
                $"D3D11 device creation failed. {software.Error}");
    }

    private static (ID3D11Device? Device, string? Error) TryCreateNativeDevice(
        DriverType driverType)
    {
        try
        {
            var result = D3D11CreateDevice(
                null,
                driverType,
                DeviceCreationFlags.BgraSupport,
                null,
                out var device);
            result.CheckError();
            return device is null
                ? (null, $"{driverType} returned no D3D11 device.")
                : (device, null);
        }
        catch (Exception error)
        {
            return (null, $"{driverType}: {error.Message}");
        }
    }

    private static IDirect3DDevice CreateWinRtDevice(nint dxgiDevice)
    {
        var result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private static Task<Direct3D11CaptureFrame> WaitForFrameAsync(
        Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<Direct3D11CaptureFramePool, object>? handler = null;
        handler = (sender, _) =>
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is not null && completion.TrySetResult(frame))
                {
                    sender.FrameArrived -= handler;
                }
                else
                {
                    frame?.Dispose();
                }
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        };

        framePool.FrameArrived += handler;
        var registration = cancellationToken.Register(() =>
        {
            framePool.FrameArrived -= handler;
            completion.TrySetCanceled(cancellationToken);
        });
        _ = completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        session.StartCapture();
        return completion.Task;
    }

    private static async Task<SoftwareBitmap> ResizeForOcrIfNeededAsync(SoftwareBitmap bitmap)
    {
        var maxDimension = OcrEngine.MaxImageDimension;
        if (bitmap.PixelWidth <= maxDimension && bitmap.PixelHeight <= maxDimension)
        {
            return SoftwareBitmap.Copy(bitmap);
        }

        var scale = Math.Min(
            (double)maxDimension / bitmap.PixelWidth,
            (double)maxDimension / bitmap.PixelHeight);
        var width = Math.Max(1u, checked((uint)Math.Floor(bitmap.PixelWidth * scale)));
        var height = Math.Max(1u, checked((uint)Math.Floor(bitmap.PixelHeight * scale)));

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }

    private static async Task<(string Text, string? LanguageTag, string? Error)>
        RecognizeWithBestInstalledLanguageAsync(SoftwareBitmap bitmap, Rect? keep = null, bool[]? ignoreCells = null)
    {
        var availableTags = OcrEngine.AvailableRecognizerLanguages
            .Select(language => language.LanguageTag)
            .ToArray();
        if (availableTags.Length == 0)
        {
            return (
                string.Empty,
                null,
                "No Windows OCR language is installed; UI Automation remains available.");
        }

        var candidateTags = OcrLanguageSelector.CandidateTags(
            availableTags,
            GlobalizationPreferences.Languages);
        var bestText = string.Empty;
        string? bestTag = null;
        var bestScore = 0;
        foreach (var tag in candidateTags)
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
            if (engine is null)
            {
                continue;
            }

            var result = await engine.RecognizeAsync(bitmap);
            var text = string.Join(
                Environment.NewLine,
                result.Lines
                    .Where(line => keep is not { } area || CentreInside(line, area))
                    .Where(line => ignoreCells is null || !InIgnoredCell(line, ignoreCells, bitmap.PixelWidth, bitmap.PixelHeight))
                    .Select(line => line.Text)
                    .Where(line => !string.IsNullOrWhiteSpace(line)));
            var score = OcrLanguageSelector.ScoreRecognizedText(text);
            if (score > bestScore)
            {
                bestScore = score;
                bestText = text;
                bestTag = tag;
            }
        }

        if (bestTag is null)
        {
            return (
                string.Empty,
                null,
                "Windows OCR could not create an engine for the installed languages.");
        }

        return (bestText, bestTag, null);
    }

    private static bool InIgnoredCell(OcrLine line, bool[] ignoreCells, int width, int height)
    {
        if (line.Words.Count == 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        var first = line.Words[0].BoundingRect;
        var last = line.Words[^1].BoundingRect;
        var x = (first.Left + last.Right) / 2;
        var y = (first.Top + last.Bottom) / 2;
        var column = Math.Clamp((int)(x / width * FrameSignature.GridSize), 0, FrameSignature.GridSize - 1);
        var row = Math.Clamp((int)(y / height * FrameSignature.GridSize), 0, FrameSignature.GridSize - 1);
        return ignoreCells[(row * FrameSignature.GridSize) + column];
    }

    private static bool CentreInside(OcrLine line, Rect area)
    {
        if (line.Words.Count == 0)
        {
            return false;
        }

        var first = line.Words[0].BoundingRect;
        var last = line.Words[^1].BoundingRect;
        var x = (first.Left + last.Right) / 2;
        var y = (first.Top + last.Bottom) / 2;
        return x >= area.Left && x <= area.Right && y >= area.Top && y <= area.Bottom;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, in Guid iid);

        nint CreateForMonitor([In] nint monitor, in Guid iid);
    }

    [DllImport(
        "d3d11.dll",
        EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);
}
