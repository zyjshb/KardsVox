using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KardsVox.Audio;

internal sealed class IntroPlayer : IDisposable
{
    private const int SampleRate = 48000;
    private readonly object gate = new();
    private readonly List<IDisposable> activeReaders = [];
    private IWaveProvider? activeProvider;
    private WaveOutEvent? output;

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
            activeProvider = source;
            output = CreateOutput();
            output.Init(activeProvider);
            output.Play();
            return source.TotalTime;
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
            activeProvider = new SequentialWaveProvider([telegraph, source]);
            output = CreateOutput();
            output.Init(activeProvider);
            output.Play();
            return telegraph.Duration + source.TotalTime;
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
            activeProvider = new SequentialWaveProvider(providers.ToArray());
            output = CreateOutput();
            output.Init(activeProvider);
            output.Play();
            return duration;
        }
    }

    public void Stop()
    {
        lock (gate) ResetPlayback();
    }

    private void ResetPlayback()
    {
        output?.Stop();
        output?.Dispose();
        output = null;
        activeProvider = null;
        foreach (IDisposable source in activeReaders) source.Dispose();
        activeReaders.Clear();
    }

    private static WaveOutEvent CreateOutput() => new()
    {
        DesiredLatency = 50,
        NumberOfBuffers = 2,
        Volume = 1f
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

    public void Dispose() => Stop();
}
