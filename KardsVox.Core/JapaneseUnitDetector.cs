using System;
using System.Collections.Generic;
using System.Drawing;

namespace KardsVox.Core;

internal sealed class JapaneseUnitDetector
{
	private const int AnalysisWidth = 800;

	private const int MinimumArea = 12;

	private const int MaximumArea = 80;

	private const int ConfirmHits = 2;

	private static readonly TimeSpan CandidateWindow = TimeSpan.FromMilliseconds(1600.0);

	private static readonly TimeSpan RemovalWindow = TimeSpan.FromSeconds(8.0);

	private bool initialized;

	private int confirmedCount;

	private int candidateCount;

	private int candidateHits;

	private DateTimeOffset candidateSince;

	private DateTimeOffset? lowerSince;

	public int ConfirmedCount => confirmedCount;

	public void Reset()
	{
		initialized = false;
		confirmedCount = (candidateCount = (candidateHits = 0));
		lowerSince = null;
	}

	public bool Observe(Bitmap screenshot, DateTimeOffset now)
	{
		int count = CountMarkers(screenshot);
		if (!initialized)
		{
			initialized = true;
			confirmedCount = count;
			return false;
		}
		if (count > confirmedCount)
		{
			lowerSince = null;
			if (count != candidateCount || now - candidateSince > CandidateWindow)
			{
				candidateCount = count;
				candidateHits = 1;
				candidateSince = now;
			}
			else
			{
				candidateHits++;
			}
			if (candidateHits < 2)
			{
				return false;
			}
			confirmedCount = count;
			candidateCount = (candidateHits = 0);
			return true;
		}
		if (count < confirmedCount)
		{
			DateTimeOffset valueOrDefault = lowerSince.GetValueOrDefault();
			if (!lowerSince.HasValue)
			{
				valueOrDefault = now;
				lowerSince = valueOrDefault;
			}
			valueOrDefault = now;
			DateTimeOffset? dateTimeOffset = lowerSince;
			if (valueOrDefault - dateTimeOffset >= RemovalWindow)
			{
				confirmedCount = count;
				lowerSince = null;
			}
		}
		else
		{
			lowerSince = null;
		}
		if (candidateCount != 0 && now - candidateSince > CandidateWindow)
		{
			candidateCount = (candidateHits = 0);
		}
		return false;
	}

	public static int CountMarkers(Bitmap screenshot)
	{
		int height = Math.Max(1, (int)Math.Round((double)screenshot.Height * 800.0 / (double)screenshot.Width));
		using Bitmap image = new Bitmap(screenshot, new Size(800, height));
		int x0 = 200;
		int x1 = 624;
		int y0 = (int)(0.615 * (double)height);
		int y1 = (int)(0.68 * (double)height);
		int width = x1 - x0;
		int rows = y1 - y0;
		if (width < 1 || rows < 1)
		{
			return 0;
		}
		bool[] red = new bool[width * rows];
		bool[] visited = new bool[red.Length];
		for (int i = 0; i < rows; i++)
		{
			for (int j = 0; j < width; j++)
			{
				Color c = image.GetPixel(x0 + j, y0 + i);
				red[i * width + j] = c.R > 140 && (double)(int)c.R > (double)(int)c.G * 1.5 && (double)(int)c.R > (double)(int)c.B * 1.5 && c.G < 130;
			}
		}
		int found = 0;
		Queue<int> queue = new Queue<int>();
		for (int start = 0; start < red.Length; start++)
		{
			if (!red[start] || visited[start])
			{
				continue;
			}
			int area = 0;
			int left = width;
			int right = 0;
			int top = rows;
			int bottom = 0;
			visited[start] = true;
			queue.Enqueue(start);
			while (queue.Count > 0)
			{
				int num = queue.Dequeue();
				int px = num % width;
				int py = num / width;
				area++;
				left = Math.Min(left, px);
				right = Math.Max(right, px);
				top = Math.Min(top, py);
				bottom = Math.Max(bottom, py);
				Add(px - 1, py);
				Add(px + 1, py);
				Add(px, py - 1);
				Add(px, py + 1);
			}
			int markerWidth = right - left + 1;
			int markerHeight = bottom - top + 1;
			bool flag = ((area < 12 || area > 80) ? true : false);
			bool flag2 = flag;
			if (!flag2)
			{
				bool flag3 = ((markerWidth < 4 || markerWidth > 12) ? true : false);
				flag2 = flag3;
			}
			bool flag4 = flag2;
			if (!flag4)
			{
				bool flag3 = ((markerHeight < 3 || markerHeight > 12) ? true : false);
				flag4 = flag3;
			}
			if (!flag4)
			{
				int centerX = x0 + (left + right + 1) / 2;
				int centerY = y0 + (top + bottom + 1) / 2;
				if (CreamFraction(image, centerX, centerY) >= 0.18)
				{
					found++;
				}
			}
		}
		return found;
		void Add(int num2, int num3)
		{
			if (num2 >= 0 && num2 < width && num3 >= 0 && num3 < rows)
			{
				int index = num3 * width + num2;
				if (red[index] && !visited[index])
				{
					visited[index] = true;
					queue.Enqueue(index);
				}
			}
		}
	}

	public static void DrawRegion(Graphics graphics, Size size)
	{
		using Pen pen = new Pen(Color.LimeGreen, 3f);
		graphics.DrawRectangle(pen, Rectangle.FromLTRB((int)(0.25 * (double)size.Width), (int)(0.615 * (double)size.Height), (int)(0.78 * (double)size.Width), (int)(0.68 * (double)size.Height)));
	}

	private static double CreamFraction(Bitmap image, int centerX, int centerY)
	{
		int cream = 0;
		int total = 0;
		for (int y = Math.Max(0, centerY - 10); y <= Math.Min(image.Height - 1, centerY + 10); y++)
		{
			for (int x = Math.Max(0, centerX - 10); x <= Math.Min(image.Width - 1, centerX + 10); x++)
			{
				Color c = image.GetPixel(x, y);
				if (c.R > 170 && c.G > 160 && c.B > 145)
				{
					cream++;
				}
				total++;
			}
		}
		if (total != 0)
		{
			return (double)cream / (double)total;
		}
		return 0.0;
	}
}
