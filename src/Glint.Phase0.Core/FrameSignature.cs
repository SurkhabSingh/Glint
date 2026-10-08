namespace Glint.Phase0.Core;

/// <summary>
/// A tiny fingerprint of one frame: the average brightness of each cell in a
/// 32 x 32 grid. Cheap enough to take on every look, and enough to answer
/// "did anything on screen move, and where?" before any text is read.
/// </summary>
public sealed class FrameSignature
{
    public const int GridSize = 32;
    public const int CellCount = GridSize * GridSize;

    /// Brightness difference (0-255) below which a cell counts as unchanged.
    /// Above video-compression and anti-aliasing noise, below any real edit.
    internal const int CellThreshold = 6;

    /// A cell that changed on at least this many recent looks is "always
    /// moving" for this page: a ticker, a clock, a playing video.
    internal const int LiveThreshold = 4;

    private const int LiveCountCap = 8;

    private readonly byte[] _cells;

    public FrameSignature(byte[] cells, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Length != CellCount)
        {
            throw new ArgumentException($"A signature has exactly {CellCount} cells.", nameof(cells));
        }

        _cells = cells;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlySpan<byte> Cells => _cells;

    /// <summary>
    /// Builds a signature from BGRA8 pixels, sampling a sparse lattice inside
    /// each cell so a 4K frame costs the same as a small one.
    /// </summary>
    public static FrameSignature FromBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame is empty.");
        }

        var cells = new byte[CellCount];
        const int samplesPerAxis = 6;
        for (var gy = 0; gy < GridSize; gy++)
        {
            var y0 = gy * height / GridSize;
            var y1 = Math.Max(y0 + 1, (gy + 1) * height / GridSize);
            for (var gx = 0; gx < GridSize; gx++)
            {
                var x0 = gx * width / GridSize;
                var x1 = Math.Max(x0 + 1, (gx + 1) * width / GridSize);
                long sum = 0;
                var count = 0;
                for (var sy = 0; sy < samplesPerAxis; sy++)
                {
                    var y = Math.Min(height - 1, y0 + ((y1 - y0) * sy / samplesPerAxis));
                    var row = y * stride;
                    for (var sx = 0; sx < samplesPerAxis; sx++)
                    {
                        var x = Math.Min(width - 1, x0 + ((x1 - x0) * sx / samplesPerAxis));
                        var offset = row + (x * 4);
                        if (offset + 2 >= pixels.Length)
                        {
                            continue;
                        }

                        // BGRA: integer Rec. 601 luma.
                        sum += ((pixels[offset + 2] * 299) + (pixels[offset + 1] * 587) + (pixels[offset] * 114)) / 1000;
                        count++;
                    }
                }

                cells[(gy * GridSize) + gx] = count == 0 ? (byte)0 : (byte)(sum / count);
            }
        }

        return new FrameSignature(cells, width, height);
    }

    public string ToBase64() =>
        $"{Width}x{Height}:{Convert.ToBase64String(_cells)}";

    public static FrameSignature? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var colon = value.IndexOf(':');
        var x = value.IndexOf('x');
        if (colon <= 0 || x <= 0 || x > colon)
        {
            return null;
        }

        try
        {
            var width = int.Parse(value[..x], System.Globalization.CultureInfo.InvariantCulture);
            var height = int.Parse(value[(x + 1)..colon], System.Globalization.CultureInfo.InvariantCulture);
            var cells = Convert.FromBase64String(value[(colon + 1)..]);
            return cells.Length == CellCount ? new FrameSignature(cells, width, height) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares two looks at the same page and updates which cells this page
    /// keeps moving on its own.
    /// </summary>
    /// <param name="previous">The page's last signature, or null on a first look.</param>
    /// <param name="current">This look.</param>
    /// <param name="liveCounts">Per-cell history for this page (0-8), or null.</param>
    public static FrameDiff Compare(
        FrameSignature? previous,
        FrameSignature current,
        byte[]? liveCounts)
    {
        ArgumentNullException.ThrowIfNull(current);
        var counts = liveCounts is { Length: CellCount }
            ? (byte[])liveCounts.Clone()
            : new byte[CellCount];

        // A first look, or the window was resized: everything is new, and the
        // old movement history no longer lines up with the cells.
        if (previous is null
            || previous.Width != current.Width
            || previous.Height != current.Height)
        {
            return new FrameDiff(CellCount, false, true, new byte[CellCount]);
        }

        var changed = 0;
        var allLive = true;
        for (var index = 0; index < CellCount; index++)
        {
            var moved = Math.Abs(previous._cells[index] - current._cells[index]) >= CellThreshold;
            if (moved)
            {
                changed++;
                if (counts[index] < LiveThreshold)
                {
                    allLive = false;
                }

                counts[index] = (byte)Math.Min(LiveCountCap, counts[index] + 1);
            }
            else if (counts[index] > 0)
            {
                counts[index]--;
            }
        }

        return new FrameDiff(changed, changed > 0 && allLive, false, counts);
    }
}

/// <param name="ChangedCells">How many of the 1,024 cells moved.</param>
/// <param name="OnlyLiveCells">Something moved, but only where this page always moves.</param>
/// <param name="FirstLook">There was nothing comparable to compare with.</param>
/// <param name="LiveCounts">The page's updated movement history.</param>
public sealed record FrameDiff(
    int ChangedCells,
    bool OnlyLiveCells,
    bool FirstLook,
    byte[] LiveCounts)
{
    /// Cells this page keeps moving on its own, for OCR to skip.
    public bool[] LiveCells() =>
        LiveCounts.Select(count => count >= FrameSignature.LiveThreshold).ToArray();

    /// A blinking caret or a hover highlight is one or two cells; real
    /// movement is more.
    public bool Moved => FirstLook || ChangedCells >= 3;

    public bool NothingMoved => !FirstLook && ChangedCells == 0;
}
