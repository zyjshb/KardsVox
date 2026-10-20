using System;
using System.Collections.Generic;
using System.Drawing;

namespace KardsVox.Core;

internal static class BuiltInBoardDetector
{
	public readonly record struct Evidence(bool IsBoard, bool IsPreMatch, double TurnButtonRun, double UpperHqContrast, double LowerHqContrast, double UpperHqFill, double LowerHqFill, double BrightCutoff);

	public static Evidence Analyze(Bitmap image)
	{
		double cutoff = Math.Clamp(Percentile(image, new Rectangle(0, 0, image.Width, image.Height), 0.95, Math.Max(4, image.Width / 160)) * 0.65, 50.0, 160.0);
		double button = LongestBrightRun(image, Area(image, 0.88, 0.6, 0.99, 0.7), cutoff);
		double upper = Percentile(image, Area(image, 0.42, 0.11, 0.62, 0.34), 0.8, SampleStep(image)) - Percentile(image, Area(image, 0.25, 0.11, 0.35, 0.34), 0.5, SampleStep(image));
		double lower = Percentile(image, Area(image, 0.4, 0.61, 0.6, 0.84), 0.8, SampleStep(image)) - Percentile(image, Area(image, 0.25, 0.61, 0.35, 0.84), 0.5, SampleStep(image));
		double upperFill = CardFill(image, Area(image, 0.42, 0.11, 0.62, 0.34));
		double lowerFill = CardFill(image, Area(image, 0.4, 0.61, 0.6, 0.84));
		double middleLeft = CardFill(image, Area(image, 0.33, 0.36, 0.5, 0.72));
		double middleRight = CardFill(image, Area(image, 0.51, 0.36, 0.68, 0.72));
		double handFill = CardFill(image, Area(image, 0.3, 0.78, 0.7, 0.98));
		bool preMatch = button < 0.12 && middleLeft > 0.15 && middleRight > 0.15 && handFill > 0.1;
		return new Evidence(!preMatch && button >= 0.25 && upper >= 18.0 && lower >= 18.0 && upperFill >= 0.055 && lowerFill >= 0.055, preMatch, button, upper, lower, upperFill, lowerFill, cutoff);
	}

	public static void DrawRegions(Graphics graphics, Size size)
	{
		using Pen pen = new Pen(Color.Gold, 3f);
		using Pen left = new Pen(Color.Orange, 2f);
		graphics.DrawRectangle(pen, Area(size, 0.88, 0.6, 0.99, 0.7));
		graphics.DrawRectangle(pen, Area(size, 0.42, 0.11, 0.62, 0.34));
		graphics.DrawRectangle(pen, Area(size, 0.4, 0.61, 0.6, 0.84));
		graphics.DrawRectangle(left, Area(size, 0.25, 0.11, 0.35, 0.34));
		graphics.DrawRectangle(left, Area(size, 0.25, 0.61, 0.35, 0.84));
	}

	private static int SampleStep(Bitmap image)
	{
		return Math.Max(2, image.Width / 480);
	}

	private static Rectangle Area(Bitmap image, double x1, double y1, double x2, double y2)
	{
		return Area(image.Size, x1, y1, x2, y2);
	}

	private static Rectangle Area(Size size, double x1, double y1, double x2, double y2)
	{
		return Rectangle.FromLTRB((int)(x1 * (double)size.Width), (int)(y1 * (double)size.Height), (int)(x2 * (double)size.Width), (int)(y2 * (double)size.Height));
	}

	private static double Percentile(Bitmap image, Rectangle area, double fraction, int step)
	{
		List<double> values = new List<double>((area.Width / step + 1) * (area.Height / step + 1));
		for (int y = area.Top; y < area.Bottom; y += step)
		{
			for (int x = area.Left; x < area.Right; x += step)
			{
				values.Add(Luma(image.GetPixel(x, y)));
			}
		}
		values.Sort();
		return values[(int)(fraction * (double)(values.Count - 1))];
	}

	private static double LongestBrightRun(Bitmap image, Rectangle area, double cutoff)
	{
		int longest = 0;
		for (int y = area.Top; y < area.Bottom; y++)
		{
			int current = 0;
			for (int x = area.Left; x < area.Right; x++)
			{
				current = ((Luma(image.GetPixel(x, y)) > cutoff) ? (current + 1) : 0);
				if (current > longest)
				{
					longest = current;
				}
			}
		}
		return (double)longest / (double)area.Width;
	}

	private static double CardFill(Bitmap image, Rectangle area)
	{
		int light = 0;
		int total = 0;
		int step = SampleStep(image);
		for (int y = area.Top; y < area.Bottom; y += step)
		{
			for (int x = area.Left; x < area.Right; x += step)
			{
				Color color = image.GetPixel(x, y);
				if (color.R >= 170 && color.G >= 160 && color.B >= 145 && color.R - color.G < 75 && color.G - color.B < 75)
				{
					light++;
				}
				total++;
			}
		}
		if (total != 0)
		{
			return (double)light / (double)total;
		}
		return 0.0;
	}

	private static double Luma(Color color)
	{
		return (double)(color.R + color.G + color.B) / 3.0;
	}
}
