using System;
using System.Drawing;
using System.IO;

namespace KardsVox.Core;

internal sealed class NormalizedRoi
{
	public double X { get; set; }

	public double Y { get; set; }

	public double Width { get; set; }

	public double Height { get; set; }

	public Rectangle ToPixels(Size client)
	{
		if (X < 0.0 || Y < 0.0 || Width <= 0.0 || Height <= 0.0 || X + Width > 1.0 || Y + Height > 1.0)
		{
			throw new InvalidDataException("ROI must be inside the client area with values from 0 to 1");
		}
		int left = (int)Math.Round(X * (double)client.Width);
		int top = (int)Math.Round(Y * (double)client.Height);
		int right = (int)Math.Round((X + Width) * (double)client.Width);
		int bottom = (int)Math.Round((Y + Height) * (double)client.Height);
		return Rectangle.FromLTRB(left, top, right, bottom);
	}
}
