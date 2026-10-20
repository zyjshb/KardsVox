using System;
using System.Drawing;

namespace KardsVox.Core;

internal static class EmptyTableTransitionDetector
{
	internal readonly record struct Evidence(bool IsEmptyTable, double GlobalLuma, double CenterLuma, double CenterRed, double CenterGreen, double CenterBlue);

	public static Evidence Analyze(Bitmap image)
	{
		(double, double, double, double) global = Average(image, new Rectangle(0, 0, image.Width, image.Height));
		(double, double, double, double) center = Average(image, Rectangle.FromLTRB((int)((double)image.Width * 0.2), (int)((double)image.Height * 0.2), (int)((double)image.Width * 0.8), (int)((double)image.Height * 0.75)));
		double item = global.Item4;
		int isEmptyTable;
		if (item >= 4.0 && item <= 34.0)
		{
			item = center.Item4;
			if (item >= 14.0 && item <= 58.0 && center.Item1 >= center.Item2 + 4.0)
			{
				isEmptyTable = ((center.Item2 >= center.Item3 + 3.0) ? 1 : 0);
				goto IL_00e2;
			}
		}
		isEmptyTable = 0;
		goto IL_00e2;
		IL_00e2:
		return new Evidence((byte)isEmptyTable != 0, global.Item4, center.Item4, center.Item1, center.Item2, center.Item3);
	}

	private static (double R, double G, double B, double Luma) Average(Bitmap image, Rectangle area)
	{
		long red = 0L;
		long green = 0L;
		long blue = 0L;
		long count = 0L;
		int step = Math.Max(4, image.Width / 180);
		for (int y = area.Top; y < area.Bottom; y += step)
		{
			for (int x = area.Left; x < area.Right; x += step)
			{
				Color color = image.GetPixel(x, y);
				red += color.R;
				green += color.G;
				blue += color.B;
				count++;
			}
		}
		if (count == 0L)
		{
			return default((double, double, double, double));
		}
		double r = (double)red / (double)count;
		double g = (double)green / (double)count;
		double b = (double)blue / (double)count;
		return (R: r, G: g, B: b, Luma: (r + g + b) / 3.0);
	}
}
