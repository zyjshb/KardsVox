using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;

namespace KardsVox.Audio;

internal sealed class IntroPlayer : IDisposable
{
    private const int SampleRate = 48000;
    private readonly object gate = new();
    private readonly List<IDisposable> activeReaders = [];
    private IWaveProvider? activeProvider;
    private VolumeSampleProvider? softwareVolume;
    private IWavePlayer? output;
    private readonly Func<IWavePlayer> outputFactory;
    private readonly System.Threading.Timer volumeTimer;
    private readonly Dictionary<string, (DateTime Modified, SpeechPause[] Pauses)> pauseCache = new(StringComparer.OrdinalIgnoreCase);
    private SpeechPause[] activePauses = [];
    private TimeSpan activeDuration;
    private long fadeStarted;
    private TimeSpan fadeDuration;
    private float fadeFrom;
    private float fadeTo;
    private bool disposed;

    public IntroPlayer() : this(CreateOutput) { }

    internal IntroPlayer(Func<IWavePlayer> outputFactory)
    {
        this.outputFactory = outputFactory;
        volumeTimer = new System.Threading.Timer(_ => UpdateVolumeFade(), null,
            Timeout.Infinite, Timeout.Infinite);
    }

    public PlaybackState PlaybackState
    {
        get { lock (gate) return output?.PlaybackState ?? PlaybackState.Stopped; }
    }

    public TimeSpan Play(string audioPath)
    {
        lock (gate)
        {
            ResetPlayback();
            var source = new AudioFileReader(audioPath);
            activeReaders.Add(source);
            DateTime modified = File.GetLastWriteTimeUtc(audioPath);
            if (!pauseCache.TryGetValue(audioPath, out var cached) || cached.Modified != modified)
            {
                cached = (modified, SpeechPauseMap.Detect(source));
                pauseCache[audioPath] = cached;
                source.Position = 0;
            }
            activePauses = cached.Pauses;
            activeDuration = source.TotalTime;
            TimeSpan duration = source.TotalTime;
            StartOutput(source);
            return duration;
        }
    }

    public TimeSpan PlayWithTelegraph(string audioPath, string pattern)
    {
        lock (gate)
        {
            ResetPlayback();
            var source = new WaveFileReader(audioPath);
            activeReaders.Add(source);
            if (source.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
                source.WaveFormat.SampleRate != SampleRate || source.WaveFormat.Channels != 1 ||
                source.WaveFormat.BitsPerSample != 16)
                throw new InvalidDataException("Order voice clips must be 48 kHz mono 16-bit PCM WAV files.");

            var telegraph = new PcmBufferWaveProvider(CreateTelegraph(pattern));
            TimeSpan duration = telegraph.Duration + source.TotalTime;
            StartOutput(new SequentialWaveProvider([telegraph, source]));
            return duration;
        }
    }

    public TimeSpan PlayTelegraph(string pattern)
    {
        lock (gate)
        {
            ResetPlayback();
            var telegraph = new PcmBufferWaveProvider(CreateTelegraph(pattern));
            StartOutput(telegraph);
            return telegraph.Duration;
        }
    }

    public TimeSpan PlaySequence(params string[] audioPaths)
    {
        if (audioPaths.Length == 0) throw new ArgumentException("At least one audio path is required.");
        lock (gate)
        {
            ResetPlayback();
            var providers = new List<IWaveProvider>(audioPaths.Length);
            TimeSpan duration = TimeSpan.Zero;
            foreach (string path in audioPaths)
            {
                var source = new AudioFileReader(path);
                activeReaders.Add(source);
                duration += source.TotalTime;
                ISampleProvider samples = source;
                if (samples.WaveFormat.Channels == 2)
                    samples = new StereoToMonoSampleProvider(samples) { LeftVolume = .5f, RightVolume = .5f };
                else if (samples.WaveFormat.Channels != 1)
                    throw new InvalidDataException($"Unsupported channel count in {path}: {samples.WaveFormat.Channels}");
                if (samples.WaveFormat.SampleRate != SampleRate)
                    samples = new WdlResamplingSampleProvider(samples, SampleRate);
                providers.Add(new SampleToWaveProvider16(samples));
            }
            StartOutput(new SequentialWaveProvider(providers.ToArray()));
            return duration;
        }
    }

    // Missing intro recordings occupy their subtitle reading interval in memory.
    // No placeholder WAVs are written or counted as delivered recordings.
    public TimeSpan PlayTimedSequence(IReadOnlyList<NationSubtitleLine> lines)
    {
        lock (gate)
        {
            ResetPlayback();
            List<IWaveProvider> providers = [];
            TimeSpan duration = TimeSpan.Zero;
            foreach (var line in lines)
            {
                if (line.HasAudio)
                {
                    var source = new WaveFileReader(line.AudioPath);
                    activeReaders.Add(source);
                    providers.Add(source);
                    duration += source.TotalTime;
                }
                else
                {
                    int samples = (int)Math.Ceiling(line.ReadingDuration.TotalSeconds * SampleRate);
                    providers.Add(new PcmBufferWaveProvider(new byte[samples * 2]));
                    duration += TimeSpan.FromSeconds(samples / (double)SampleRate);
                }
            }
            activeDuration = duration;
            StartOutput(new SequentialWaveProvider(providers.ToArray()));
            return duration;
        }
    }

    public void Stop()
    {
        lock (gate) { if (!disposed) ResetPlayback(); }
    }

    public TimeSpan GetInterruptionDelay(TimeSpan maximumWait)
    {
        lock (gate)
        {
            if (output?.PlaybackState != PlaybackState.Playing || activeProvider == null)
                return TimeSpan.Zero;
            try
            {
                if (output is not IWavePosition position) return TimeSpan.Zero;
                double played = position.GetPosition() / (double)activeProvider.WaveFormat.AverageBytesPerSecond;
                return SpeechPauseMap.Delay(activePauses, TimeSpan.FromSeconds(played), activeDuration, maximumWait);
            }
            catch (Exception error) when (IsDeviceError(error))
            {
                Console.Error.WriteLine("Audio position unavailable: " + error.Message);
                return TimeSpan.Zero;
            }
        }
    }

    public void FadeOut(TimeSpan duration)
    {
        lock (gate) BeginVolumeFade(softwareVolume?.Volume ?? 1f, 0f, duration);
    }

    public void FadeIn(TimeSpan duration)
    {
        lock (gate) BeginVolumeFade(0f, 1f, duration);
    }

    private void BeginVolumeFade(float from, float to, TimeSpan duration)
    {
        if (softwareVolume == null) return;
        fadeFrom = from;
        fadeTo = to;
        fadeDuration = duration;
        fadeStarted = Stopwatch.GetTimestamp();
        softwareVolume.Volume = from;
        if (duration <= TimeSpan.Zero)
        {
            softwareVolume.Volume = to;
            volumeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }
        volumeTimer.Change(0, 12);
    }

    private void UpdateVolumeFade()
    {
        lock (gate)
        {
            if (softwareVolume == null) return;
            double progress = Math.Clamp(Stopwatch.GetElapsedTime(fadeStarted).TotalMilliseconds /
                Math.Max(1, fadeDuration.TotalMilliseconds), 0, 1);
            softwareVolume.Volume = fadeFrom + (fadeTo - fadeFrom) * (float)progress;
            if (progress >= 1) volumeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void ResetPlayback()
    {
        volumeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        IWavePlayer? previous = output;
        output = null;
        if (previous != null)
        {
            previous.PlaybackStopped -= OnPlaybackStopped;
            try { previous.Stop(); }
            catch (Exception error) when (IsDeviceError(error))
            { Console.Error.WriteLine("Audio stop failed: " + error.Message); }
            try { previous.Dispose(); }
            catch (Exception error) when (IsDeviceError(error))
            { Console.Error.WriteLine("Audio device cleanup failed: " + error.Message); }
        }
        softwareVolume = null;
        activeProvider = null;
        activePauses = [];
        activeDuration = TimeSpan.Zero;
        foreach (IDisposable source in activeReaders) source.Dispose();
        activeReaders.Clear();
    }

    private void StartOutput(IWaveProvider source)
    {
        // Wine may reject waveOutGet/SetVolume, including before Init opens a handle.
        // Apply gain to the samples for every playback path and never touch device volume.
        softwareVolume = new VolumeSampleProvider(source.ToSampleProvider());
        activeProvider = new SampleToWaveProvider16(softwareVolume);
        try
        {
            output = outputFactory();
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(activeProvider);
            output.Play();
        }
        catch (Exception error) when (IsDeviceError(error))
        {
            Console.Error.WriteLine("Audio output unavailable; keeping game monitoring and subtitle timing: " + error.Message);
            ResetPlayback();
        }
    }

    private static bool IsDeviceError(Exception error) =>
        error is NAudio.MmException or InvalidOperationException or System.Runtime.InteropServices.COMException;

    private static void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception != null)
            Console.Error.WriteLine("Audio playback stopped with an error: " + args.Exception.Message);
    }

    private static WaveOutEvent CreateOutput() => new()
    {
        DesiredLatency = 100,
        NumberOfBuffers = 3
    };

    private static byte[] CreateTelegraph(string pattern)
    {
        const double dotSeconds = .055;
        const double dashSeconds = .145;
        const double elementGapSeconds = .045;
        const double groupGapSeconds = .105;
        const double trailingSilenceSeconds = .18;
        var samples = new List<short>(SampleRate * 2);
        uint noise = 0x5A17u;

        foreach (char symbol in pattern)
        {
            if (symbol == ' ')
            {
                AddSilence(samples, groupGapSeconds);
                continue;
            }
            double seconds = symbol == '-' ? dashSeconds : dotSeconds;
            int count = (int)(seconds * SampleRate);
            for (int index = 0; index < count; index++)
            {
                double progress = index / (double)Math.Max(1, count - 1);
                double edge = Math.Min(progress / .12, (1 - progress) / .12);
                double envelope = Math.Clamp(edge, 0, 1);
                noise = noise * 1664525u + 1013904223u;
                double hiss = ((noise >> 8) / 16777215.0 - .5) * .028;
                double time = index / (double)SampleRate;
                double tone = Math.Sin(2 * Math.PI * 735 * time) * .52 +
                              Math.Sin(2 * Math.PI * 1470 * time) * .085;
                double value = Math.Clamp((tone + hiss) * envelope, -1, 1);
                samples.Add((short)Math.Round(value * short.MaxValue));
            }
            AddSilence(samples, elementGapSeconds);
        }
        AddSilence(samples, trailingSilenceSeconds);
        byte[] bytes = new byte[samples.Count * sizeof(short)];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void AddSilence(List<short> samples, double seconds)
    {
        int count = (int)(seconds * SampleRate);
        for (int index = 0; index < count; index++) samples.Add(0);
    }

    private sealed class PcmBufferWaveProvider(byte[] samples) : IWaveProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);
        public TimeSpan Duration => TimeSpan.FromSeconds(samples.Length / (double)WaveFormat.AverageBytesPerSecond);

        public int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(count, samples.Length - position);
            if (available <= 0) return 0;
            Buffer.BlockCopy(samples, position, buffer, offset, available);
            position += available;
            return available;
        }
    }

    private sealed class SequentialWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider[] providers;
        private int providerIndex;

        public WaveFormat WaveFormat { get; }

        public SequentialWaveProvider(IWaveProvider[] providers)
        {
            if (providers.Length == 0) throw new ArgumentException("At least one audio provider is required.");
            this.providers = providers;
            WaveFormat = providers[0].WaveFormat;
            if (providers.Any(item => item.WaveFormat.SampleRate != WaveFormat.SampleRate ||
                item.WaveFormat.Channels != WaveFormat.Channels ||
                item.WaveFormat.BitsPerSample != WaveFormat.BitsPerSample ||
                item.WaveFormat.Encoding != WaveFormat.Encoding))
                throw new InvalidDataException("Sequential audio providers must have the same PCM format.");
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count && providerIndex < providers.Length)
            {
                int read = providers[providerIndex].Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    providerIndex++;
                    continue;
                }
                total += read;
            }
            return total;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            ResetPlayback();
            disposed = true;
            volumeTimer.Dispose();
        }
    }

    internal void VerifyFades(string audioPath)
    {
        try
        {
            Play(audioPath);
            FadeOut(TimeSpan.FromMilliseconds(80));
            Thread.Sleep(180);
            lock (gate)
                if (output == null || softwareVolume == null || softwareVolume.Volume > .03f)
                    throw new InvalidDataException("Audio fade-out did not reach silence.");
            Play(audioPath);
            lock (gate)
                if (output == null || softwareVolume == null || softwareVolume.Volume < .97f)
                    throw new InvalidDataException("A new clip retained the previous clip's faded volume.");
            FadeIn(TimeSpan.FromMilliseconds(80));
            Thread.Sleep(180);
            lock (gate)
                if (output == null || softwareVolume == null || softwareVolume.Volume < .97f)
                    throw new InvalidDataException("Audio fade-in did not restore normal volume.");
        }
        finally { Stop(); }
    }
}
