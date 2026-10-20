using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace KardsVox.Core;

internal sealed class ScreenTemplate
{
    private const int SampleSize = 48;
    private readonly double[] values;
    private readonly double mean;
    private readonly double spread;

    public ScreenTemplate(string path)
    {
        using var bitmap = new Bitmap(path);
        (values, mean, spread) = Sample(bitmap);
        if (spread < 10) throw new InvalidDataException($"Template is too uniform: {path}");
    }

    public double Compare(Bitmap current)
    {
        var (other, otherMean, otherSpread) = Sample(current);
        if (otherSpread < 10) return -1;
        double numerator = 0;
        for (int i = 0; i < values.Length; i++)
            numerator += (values[i] - mean) * (other[i] - otherMean);
        return numerator / (values.Length * spread * otherSpread);
    }

    private static (double[] values, double mean, double spread) Sample(Bitmap source)
    {
        using var scaled = new Bitmap(SampleSize, SampleSize);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, 0, 0, SampleSize, SampleSize);
        }
        var values = new double[SampleSize * SampleSize];
        double sum = 0;
        for (int y = 0; y < SampleSize; y++)
        for (int x = 0; x < SampleSize; x++)
        {
            var color = scaled.GetPixel(x, y);
            double luminance = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
            values[y * SampleSize + x] = luminance;
            sum += luminance;
        }
        double mean = sum / values.Length;
        double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Length;
        return (values, mean, Math.Sqrt(variance));
    }
}

internal static class ScreenCapture
{
    public static Bitmap Client(Rectangle screenBounds)
    {
        var bitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(screenBounds.Location, Point.Empty, screenBounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public static Bitmap Roi(Rectangle clientBounds, NormalizedRoi roi)
    {
        var local = roi.ToPixels(clientBounds.Size);
        var screen = new Rectangle(clientBounds.Left + local.Left, clientBounds.Top + local.Top,
            local.Width, local.Height);
        return Client(screen);
    }
}
