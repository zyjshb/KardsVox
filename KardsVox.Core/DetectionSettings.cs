using System.Drawing;
using System.IO;
using System.Text.Json;

namespace KardsVox.Core;

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

	public NormalizedRoi BoardRoi { get; set; } = new NormalizedRoi
	{
		X = 0.42,
		Y = 0.08,
		Width = 0.16,
		Height = 0.12
	};

	public NormalizedRoi LobbyRoi { get; set; } = new NormalizedRoi
	{
		X = 0.4,
		Y = 0.08,
		Width = 0.2,
		Height = 0.16
	};

	public static DetectionSettings Load(string path)
	{
		if (!File.Exists(path))
		{
			DetectionSettings detectionSettings = new DetectionSettings();
			detectionSettings.Save(path);
			return detectionSettings;
		}
		DetectionSettings? obj = JsonSerializer.Deserialize<DetectionSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Detection settings are empty");
		obj.Validate();
		return obj;
	}

	public void Save(string path)
	{
		Validate();
		File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
		{
			WriteIndented = true
		}));
	}

	private void Validate()
	{
		SelectedVoicePack = VoicePackRegistry.Get(SelectedVoicePack).Id;
		if (PollMilliseconds < 300 || PollMilliseconds > 500)
		{
			throw new InvalidDataException("PollMilliseconds must be from 300 to 500");
		}
		double boardThreshold = BoardThreshold;
		bool flag = ((boardThreshold < 0.5 || boardThreshold > 1.0) ? true : false);
		bool flag2 = flag;
		if (!flag2)
		{
			double lobbyThreshold = LobbyThreshold;
			bool flag3 = ((lobbyThreshold < 0.5 || lobbyThreshold > 1.0) ? true : false);
			flag2 = flag3;
		}
		if (flag2)
		{
			throw new InvalidDataException("Template thresholds must be from 0.5 to 1");
		}
		int boardConfirmFrames = BoardConfirmFrames;
		flag2 = ((boardConfirmFrames < 2 || boardConfirmFrames > 10) ? true : false);
		flag = flag2;
		if (!flag)
		{
			int lobbyConfirmFrames = LobbyConfirmFrames;
			bool flag3 = ((lobbyConfirmFrames < 2 || lobbyConfirmFrames > 10) ? true : false);
			flag = flag3;
		}
		if (flag)
		{
			throw new InvalidDataException("Confirm frames must be from 2 to 10");
		}
		boardConfirmFrames = OutsideArmFrames;
		if ((boardConfirmFrames < 2 || boardConfirmFrames > 30) ? true : false)
		{
			throw new InvalidDataException("OutsideArmFrames must be from 2 to 30");
		}
		BoardRoi.ToPixels(new Size(1920, 1080));
		LobbyRoi.ToPixels(new Size(1920, 1080));
	}
}
