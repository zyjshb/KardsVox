using System.Drawing;

namespace KardsVox.Core;

// Requires both HQ card bodies and the turn control. A bright hand or a pair of
// cards in the opening selection screen must not be treated as a battle.
// It reads pixels only; no OCR, game process access, or input injection.
internal static class BuiltInBoardDetector
{
    public readonly record struct Evidence(bool IsBoard, bool IsPreMatch, double TurnButtonRun,
        double UpperHqContrast, double LowerHqContrast, double UpperHqFill,
        double LowerHqFill, double BrightCutoff);

    public static Evidence Analyze(Bitmap image)
    {
        double global95 = Percentile(image, new Rectangle(0, 0, image.Width, image.Height), 0.95,
            Math.Max(4, image.Width / 160));
        double cutoff = Math.Clamp(global95 * 0.65, 50, 160);
        double button = LongestBrightRun(image, Area(image, .88, .60, .99, .70), cutoff);

        double upper = Percentile(image, Area(image, .42, .11, .62, .34), .80, SampleStep(image))
            - Percentile(image, Area(image, .25, .11, .35, .34), .50, SampleStep(image));
        double lower = Percentile(image, Area(image, .40, .61, .60, .84), .80, SampleStep(image))
            - Percentile(image, Area(image, .25, .61, .35, .84), .50, SampleStep(image));

        double upperFill = CardFill(image, Area(image, .42, .11, .62, .34));
        double lowerFill = CardFill(image, Area(image, .40, .61, .60, .84));
        double middleLeft = CardFill(image, Area(image, .33, .36, .50, .72));
        double middleRight = CardFill(image, Area(image, .51, .36, .68, .72));
        double handFill = CardFill(image, Area(image, .30, .78, .70, .98));

        bool preMatch = button < .12 &&
            middleLeft > .15 && middleRight > .15 && handFill > .10;
        bool board = !preMatch && button >= .25 && upper >= 18 && lower >= 18 &&
            upperFill >= .055 && lowerFill >= .055;
        return new Evidence(board, preMatch, button, upper, lower, upperFill, lowerFill, cutoff);
    }

    public static void DrawRegions(Graphics graphics, Size size)
    {
        using var pen = new Pen(Color.Gold, 3);
        using var left = new Pen(Color.Orange, 2);
        graphics.DrawRectangle(pen, Area(size, .88, .60, .99, .70));
        graphics.DrawRectangle(pen, Area(size, .42, .11, .62, .34));
        graphics.DrawRectangle(pen, Area(size, .40, .61, .60, .84));
        graphics.DrawRectangle(left, Area(size, .25, .11, .35, .34));
        graphics.DrawRectangle(left, Area(size, .25, .61, .35, .84));
    }

    private static int SampleStep(Bitmap image) => Math.Max(2, image.Width / 480);

    private static Rectangle Area(Bitmap image, double x1, double y1, double x2, double y2)
        => Area(image.Size, x1, y1, x2, y2);

    private static Rectangle Area(Size size, double x1, double y1, double x2, double y2)
        => Rectangle.FromLTRB((int)(x1 * size.Width), (int)(y1 * size.Height),
            (int)(x2 * size.Width), (int)(y2 * size.Height));

    private static double Percentile(Bitmap image, Rectangle area, double fraction, int step)
    {
        var values = new List<double>((area.Width / step + 1) * (area.Height / step + 1));
        for (int y = area.Top; y < area.Bottom; y += step)
        for (int x = area.Left; x < area.Right; x += step)
            values.Add(Luma(image.GetPixel(x, y)));
        values.Sort();
        return values[(int)(fraction * (values.Count - 1))];
    }

    private static double LongestBrightRun(Bitmap image, Rectangle area, double cutoff)
    {
        int longest = 0;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            int current = 0;
            for (int x = area.Left; x < area.Right; x++)
            {
                current = Luma(image.GetPixel(x, y)) > cutoff ? current + 1 : 0;
                if (current > longest) longest = current;
            }
        }
        return (double)longest / area.Width;
    }

    private static double CardFill(Bitmap image, Rectangle area)
    {
        int light = 0, total = 0;
        int step = SampleStep(image);
        for (int y = area.Top; y < area.Bottom; y += step)
        for (int x = area.Left; x < area.Right; x += step)
        {
            var color = image.GetPixel(x, y);
            if (color.R >= 170 && color.G >= 160 && color.B >= 145 &&
                color.R - color.G < 75 && color.G - color.B < 75) light++;
            total++;
        }
        return total == 0 ? 0 : (double)light / total;
    }

    private static double Luma(Color color) => (color.R + color.G + color.B) / 3.0;
}
