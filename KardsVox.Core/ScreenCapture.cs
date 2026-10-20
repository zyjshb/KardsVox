using System.Drawing;
using System.Drawing.Imaging;

namespace KardsVox.Core;

internal static class ScreenCapture
{
	public static Bitmap Client(Rectangle screenBounds)
	{
		Bitmap bitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format24bppRgb);
		try
		{
			using Graphics graphics = Graphics.FromImage(bitmap);
			graphics.CopyFromScreen(screenBounds.Location, Point.Empty, screenBounds.Size, CopyPixelOperation.SourceCopy);
			return bitmap;
		}
		catch
		{
			bitmap.Dispose();
			throw;
		}
	}

	public static Bitmap Roi(Rectangle clientBounds, NormalizedRoi roi)
	{
		Rectangle local = roi.ToPixels(clientBounds.Size);
		return Client(new Rectangle(clientBounds.Left + local.Left, clientBounds.Top + local.Top, local.Width, local.Height));
	}
}
