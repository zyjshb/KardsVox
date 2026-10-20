using System.Drawing;

namespace KardsVox.Core;

// Detects the small Japanese flag on a settled card in the friendly support row.
// This is deliberately narrow: a card in hand or a zoomed/selected card is not a deployment.
internal sealed class JapaneseUnitDetector
{
    private const int AnalysisWidth = 800;
    private const int MinimumArea = 12;
    private const int MaximumArea = 80;
    private const int ConfirmHits = 2;
    private static readonly TimeSpan CandidateWindow = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan RemovalWindow = TimeSpan.FromSeconds(8);

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
        confirmedCount = candidateCount = candidateHits = 0;
        lowerSince = null;
    }

    // Returns true only when the number of settled Japanese units increases reliably.
    public bool Observe(Bitmap screenshot, DateTimeOffset now)
    {
        int count = CountMarkers(screenshot);
        if (!initialized)
        {
            initialized = true;
            confirmedCount = count; // Starting during a match must stay silent.
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
            else candidateHits++;

            if (candidateHits < ConfirmHits) return false;
            confirmedCount = count;
            candidateCount = candidateHits = 0;
            return true;
        }

        if (count < confirmedCount)
        {
            lowerSince ??= now;
            if (now - lowerSince >= RemovalWindow)
            {
                confirmedCount = count;
                lowerSince = null;
            }
        }
        else lowerSince = null;

        if (candidateCount != 0 && now - candidateSince > CandidateWindow)
            candidateCount = candidateHits = 0;
        return false;
    }

    public static int CountMarkers(Bitmap screenshot)
    {
        int height = Math.Max(1, (int)Math.Round((double)screenshot.Height * AnalysisWidth / screenshot.Width));
        using var image = new Bitmap(screenshot, new Size(AnalysisWidth, height));
        int x0 = (int)(.25 * AnalysisWidth), x1 = (int)(.78 * AnalysisWidth);
        int y0 = (int)(.615 * height), y1 = (int)(.68 * height);
        int width = x1 - x0, rows = y1 - y0;
        if (width < 1 || rows < 1) return 0;

        var red = new bool[width * rows];
        var visited = new bool[red.Length];
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < width; x++)
        {
            var c = image.GetPixel(x0 + x, y0 + y);
            red[y * width + x] = c.R > 140 && c.R > c.G * 1.5 &&
                c.R > c.B * 1.5 && c.G < 130;
        }

        int found = 0;
        var queue = new Queue<int>();
        for (int start = 0; start < red.Length; start++)
        {
            if (!red[start] || visited[start]) continue;
            int area = 0, left = width, right = 0, top = rows, bottom = 0;
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int point = queue.Dequeue();
                int px = point % width, py = point / width;
                area++;
                left = Math.Min(left, px); right = Math.Max(right, px);
                top = Math.Min(top, py); bottom = Math.Max(bottom, py);
                Add(px - 1, py); Add(px + 1, py); Add(px, py - 1); Add(px, py + 1);
            }

            int markerWidth = right - left + 1, markerHeight = bottom - top + 1;
            if (area is < MinimumArea or > MaximumArea || markerWidth is < 4 or > 12 ||
                markerHeight is < 3 or > 12) continue;
            int centerX = x0 + (left + right + 1) / 2;
            int centerY = y0 + (top + bottom + 1) / 2;
            if (CreamFraction(image, centerX, centerY) >= .18) found++;
        }
        return found;

        void Add(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= rows) return;
            int index = y * width + x;
            if (red[index] && !visited[index])
            {
                visited[index] = true;
                queue.Enqueue(index);
            }
        }
    }

    public static void DrawRegion(Graphics graphics, Size size)
    {
        using var pen = new Pen(Color.LimeGreen, 3);
        graphics.DrawRectangle(pen, Rectangle.FromLTRB(
            (int)(.25 * size.Width), (int)(.615 * size.Height),
            (int)(.78 * size.Width), (int)(.68 * size.Height)));
    }

    private static double CreamFraction(Bitmap image, int centerX, int centerY)
    {
        int cream = 0, total = 0;
        for (int y = Math.Max(0, centerY - 10); y <= Math.Min(image.Height - 1, centerY + 10); y++)
        for (int x = Math.Max(0, centerX - 10); x <= Math.Min(image.Width - 1, centerX + 10); x++)
        {
            var c = image.GetPixel(x, y);
            if (c.R > 170 && c.G > 160 && c.B > 145) cream++;
            total++;
        }
        return total == 0 ? 0 : (double)cream / total;
    }
}
