using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;

namespace KardsVox.Core;

internal sealed class ScreenTemplate
{
	private const int SampleSize = 48;

	private readonly double[] values;

	private readonly double mean;

	private readonly double spread;

	public ScreenTemplate(string path)
	{
		using Bitmap bitmap = new Bitmap(path);
		(values, mean, spread) = Sample(bitmap);
		if (spread < 10.0)
		{
			throw new InvalidDataException("Template is too uniform: " + path);
		}
	}

	public double Compare(Bitmap current)
	{
		var (other, otherMean, otherSpread) = Sample(current);
		if (otherSpread < 10.0)
		{
			return -1.0;
		}
		double numerator = 0.0;
		for (int i = 0; i < values.Length; i++)
		{
			numerator += (values[i] - mean) * (other[i] - otherMean);
		}
		return numerator / ((double)values.Length * spread * otherSpread);
	}

	private static (double[] values, double mean, double spread) Sample(Bitmap source)
	{
		using Bitmap scaled = new Bitmap(48, 48);
		using (Graphics graphics = Graphics.FromImage(scaled))
		{
			graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
			graphics.DrawImage(source, 0, 0, 48, 48);
		}
		double[] values = new double[2304];
		double sum = 0.0;
		for (int y = 0; y < 48; y++)
		{
			for (int x = 0; x < 48; x++)
			{
				Color color = scaled.GetPixel(x, y);
				sum += (values[y * 48 + x] = 0.2126 * (double)(int)color.R + 0.7152 * (double)(int)color.G + 0.0722 * (double)(int)color.B);
			}
		}
		double mean = sum / (double)values.Length;
		double variance = values.Sum((double v) => (v - mean) * (v - mean)) / (double)values.Length;
		return (values: values, mean: mean, spread: Math.Sqrt(variance));
	}
}
