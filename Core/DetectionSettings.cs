using System.Drawing;
using System.Text.Json;

namespace KardsVox.Core;

internal sealed class NormalizedRoi
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Rectangle ToPixels(Size client)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 1 || Y + Height > 1)
            throw new InvalidDataException("ROI must be inside the client area with values from 0 to 1");
        int left = (int)Math.Round(X * client.Width);
        int top = (int)Math.Round(Y * client.Height);
        int right = (int)Math.Round((X + Width) * client.Width);
        int bottom = (int)Math.Round((Y + Height) * client.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }
}

internal sealed class DetectionSettings
{
    public bool AutoEnabled { get; set; } = true;
    public bool SubtitlesEnabled { get; set; } = true;
    public string SelectedVoicePack { get; set; } = VoicePackRegistry.Japan;
    public int PollMilliseconds { get; set; } = 400;
    public double BoardThreshold { get; set; } = 0.92;
    public double LobbyThreshold { get; set; } = 0.92;
    public int BoardConfirmFrames { get; set; } = 3;
    public int LobbyConfirmFrames { get; set; } = 3;
    public int OutsideArmFrames { get; set; } = 6;
    // Provisional HQ area. Adjust after inspecting an F10 screenshot.
    public NormalizedRoi BoardRoi { get; set; } = new() { X = 0.42, Y = 0.08, Width = 0.16, Height = 0.12 };
    public NormalizedRoi LobbyRoi { get; set; } = new() { X = 0.40, Y = 0.08, Width = 0.20, Height = 0.16 };

    public static DetectionSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new DetectionSettings();
            defaults.Save(path);
            return defaults;
        }

        var settings = JsonSerializer.Deserialize<DetectionSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Detection settings are empty");
        settings.Validate();
        return settings;
    }

    public void Save(string path)
    {
        Validate();
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Validate()
    {
        SelectedVoicePack = VoicePackRegistry.Get(SelectedVoicePack).Id;
        if (PollMilliseconds < 300 || PollMilliseconds > 500)
            throw new InvalidDataException("PollMilliseconds must be from 300 to 500");
        if (BoardThreshold is < 0.5 or > 1 || LobbyThreshold is < 0.5 or > 1)
            throw new InvalidDataException("Template thresholds must be from 0.5 to 1");
        if (BoardConfirmFrames is < 2 or > 10 || LobbyConfirmFrames is < 2 or > 10)
            throw new InvalidDataException("Confirm frames must be from 2 to 10");
        if (OutsideArmFrames is < 2 or > 30)
            throw new InvalidDataException("OutsideArmFrames must be from 2 to 30");
        BoardRoi.ToPixels(new Size(1920, 1080));
        LobbyRoi.ToPixels(new Size(1920, 1080));
    }
}
