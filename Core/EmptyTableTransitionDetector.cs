using System.Drawing;

namespace KardsVox.Core;

/// <summary>
/// Recognizes the dark, empty wooden table shown between matchmaking and the
/// opening hand. It deliberately uses coarse color statistics rather than OCR
/// or input hooks, so localized UI text and resolution changes do not matter.
/// </summary>
internal static class EmptyTableTransitionDetector
{
    internal readonly record struct Evidence(bool IsEmptyTable, double GlobalLuma,
        double CenterLuma, double CenterRed, double CenterGreen, double CenterBlue);

    public static Evidence Analyze(Bitmap image)
    {
        var global = Average(image, new Rectangle(0, 0, image.Width, image.Height));
        var center = Average(image, Rectangle.FromLTRB(
            (int)(image.Width * .20), (int)(image.Height * .20),
            (int)(image.Width * .80), (int)(image.Height * .75)));

        bool darkWoodTable = global.Luma is >= 4 and <= 34 &&
            center.Luma is >= 14 and <= 58 &&
            center.R >= center.G + 4 && center.G >= center.B + 3;
        return new Evidence(darkWoodTable, global.Luma, center.Luma, center.R, center.G, center.B);
    }

    private static (double R, double G, double B, double Luma) Average(Bitmap image, Rectangle area)
    {
        long red = 0, green = 0, blue = 0, count = 0;
        int step = Math.Max(4, image.Width / 180);
        for (int y = area.Top; y < area.Bottom; y += step)
        for (int x = area.Left; x < area.Right; x += step)
        {
            Color color = image.GetPixel(x, y);
            red += color.R;
            green += color.G;
            blue += color.B;
            count++;
        }
        if (count == 0) return default;
        double r = (double)red / count, g = (double)green / count, b = (double)blue / count;
        return (r, g, b, (r + g + b) / 3.0);
    }
}
