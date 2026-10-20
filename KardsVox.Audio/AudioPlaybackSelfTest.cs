using NAudio;
using NAudio.Wave;

namespace KardsVox.Audio;

// Exercises the reported Wine failure without requiring an audio device.
internal static class AudioPlaybackSelfTest
{
    internal static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kardsvox-audio-test-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
            {
                for (int i = 0; i < 48000 * 2; i++) writer.WriteSample(.5f);
            }
            var devices = new List<RejectHardwareVolume>();
            using var player = new IntroPlayer(() =>
            {
                var device = new RejectHardwareVolume();
                devices.Add(device);
                return device;
            });
            Check(player.Play(path) == TimeSpan.FromSeconds(2), "clip duration");
            Check(devices[^1].PlaybackState == PlaybackState.Playing && devices[^1].Peak() > 16000,
                "play without device volume");
            player.FadeOut(TimeSpan.FromMilliseconds(60));
            Thread.Sleep(180);
            Check(devices[^1].Peak() == 0, "software fade changes actual PCM samples");
            player.Play(path);
            Check(devices[^1].Peak() > 16000, "new clip restores software gain");
            player.FadeIn(TimeSpan.Zero);
            Check(devices[^1].Peak() > 16000, "immediate fade-in");
            player.FadeOut(TimeSpan.Zero);
            Check(devices[^1].Peak() == 0, "immediate fade-out");
            player.FadeIn(TimeSpan.FromMilliseconds(60));
            Thread.Sleep(180);
            Check(devices[^1].Peak() > 16000, "software fade-in");
            player.PlayWithTelegraph(path, ".-");
            Check(player.PlaybackState == PlaybackState.Playing && devices[^1].Peak() > 0, "telegraph plus speech");
            player.PlayTelegraph(".-");
            Check(player.PlaybackState == PlaybackState.Playing && devices[^1].Peak() > 0, "telegraph");
            Check(player.PlaySequence(path, path) == TimeSpan.FromSeconds(4), "sequence duration");
            Check(devices[^1].Peak() > 16000, "sequence output");
            var spoken = new NationSubtitleLine("test", "speaker", "original", "subtitle", path,
                TimeSpan.FromSeconds(2), "ready");
            var silent = spoken with { AudioStatus = "pending", AudioPath = "missing" };
            TimeSpan timed = player.PlayTimedSequence([spoken, silent]);
            Check(timed == spoken.AudioDuration + silent.ReadingDuration, "timed opening duration");
            Check(devices[^1].Peak() > 16000, "timed opening output");
            devices[^1].FailPosition = true;
            Check(player.GetInterruptionDelay(TimeSpan.FromMilliseconds(450)) == TimeSpan.Zero, "position failure isolation");
            devices[^1].FailStop = true;
            player.Stop();
            Check(player.PlaybackState == PlaybackState.Stopped && devices.All(d => d.Disposed), "cleanup after stop failure");

            foreach (string failure in new[] { "Init", "Play" })
            {
                var device = new RejectHardwareVolume { Failure = failure };
                using var broken = new IntroPlayer(() => device);
                Check(broken.Play(path) == TimeSpan.FromSeconds(2), $"{failure} keeps subtitle duration");
                Check(broken.PlaybackState == PlaybackState.Stopped && device.Disposed, $"{failure} cleanup");
                device.Failure = null;
                broken.Play(path);
                Check(broken.PlaybackState == PlaybackState.Playing, $"retry after {failure}");
                device.SignalFailure(); // Background output failures are logged, not thrown into the monitor.
            }
            Console.WriteLine("Audio regression passed: no hardware volume calls, all five playback paths, PCM fades, gain reset, device failure isolation and retry.");
        }
        finally { File.Delete(path); }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException("Audio regression failed: " + name);
    }

    private sealed class RejectHardwareVolume : IWavePlayer, IWavePosition
    {
        private IWaveProvider? source;
        public bool Disposed { get; private set; }
        public string? Failure { get; set; }
        public bool FailPosition { get; set; }
        public bool FailStop { get; set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => source!.WaveFormat;
        public float Volume
        {
            get => throw new MmException(MmResult.InvalidHandle, "waveOutGetVolume");
            set => throw new MmException(MmResult.InvalidHandle, "waveOutSetVolume");
        }
        public void Init(IWaveProvider provider)
        {
            if (Failure == "Init") throw new MmException(MmResult.InvalidHandle, "waveOutOpen");
            source = provider;
            Disposed = false;
        }
        public void Play()
        {
            if (Failure == "Play") throw new MmException(MmResult.InvalidHandle, "waveOutWrite");
            PlaybackState = PlaybackState.Playing;
        }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            if (FailStop) throw new MmException(MmResult.InvalidHandle, "waveOutReset");
        }
        public long GetPosition() => FailPosition ? throw new MmException(MmResult.InvalidHandle, "waveOutGetPosition") : 0;
        public void Dispose() { Disposed = true; PlaybackState = PlaybackState.Stopped; }
        public void SignalFailure() => PlaybackStopped?.Invoke(this,
            new StoppedEventArgs(new MmException(MmResult.InvalidHandle, "waveOutWrite")));
        public int Peak()
        {
            byte[] buffer = new byte[960];
            int count = source!.Read(buffer, 0, buffer.Length);
            Check(count > 0 && source.WaveFormat.BitsPerSample == 16, "PCM data present");
            int peak = 0;
            for (int i = 0; i < count; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(buffer, i)));
            return peak;
        }
    }
}
