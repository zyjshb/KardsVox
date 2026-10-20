using NAudio.Wave;

namespace KardsVox.Audio;

internal readonly record struct SpeechPause(double Start, double End);

internal static class SpeechPauseMap
{
    // Detect short quiet gaps in the actual recording rather than relying on
    // punctuation or on how far a buffered reader has read ahead.
    internal static SpeechPause[] Detect(ISampleProvider source)
    {
        int samplesPerSecond = source.WaveFormat.SampleRate * source.WaveFormat.Channels;
        float[] window = new float[Math.Max(1, samplesPerSecond / 100)];
        List<SpeechPause> result = [];
        long samples = 0;
        double? quietStart = null;
        int count;
        while ((count = source.Read(window, 0, window.Length)) > 0)
        {
            double energy = 0;
            for (int i = 0; i < count; i++) energy += window[i] * window[i];
            double start = samples / (double)samplesPerSecond;
            samples += count;
            if (Math.Sqrt(energy / count) < .018)
                quietStart ??= start;
            else if (quietStart is double quiet)
            {
                if (start - quiet >= .09) result.Add(new SpeechPause(quiet, start));
                quietStart = null;
            }
        }
        double end = samples / (double)samplesPerSecond;
        if (quietStart is double last && end - last >= .09) result.Add(new SpeechPause(last, end));
        return result.ToArray();
    }

    internal static TimeSpan Delay(SpeechPause[] pauses, TimeSpan position, TimeSpan duration, TimeSpan maximumWait)
    {
        double current = position.TotalSeconds;
        double limit = current + maximumWait.TotalSeconds;
        foreach (SpeechPause pause in pauses)
        {
            double target = Math.Max(current, pause.Start + .02);
            if (target <= pause.End - .02 && target <= limit)
                return TimeSpan.FromSeconds(target - current);
        }
        return TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds - current, 0, maximumWait.TotalSeconds));
    }

    internal static void Verify()
    {
        const int rate = 48000;
        float[] samples = Enumerable.Repeat(.2f, rate).Concat(new float[rate / 5])
            .Concat(Enumerable.Repeat(.2f, rate)).ToArray();
        SpeechPause[] pauses = Detect(new ArraySamples(samples, rate));
        if (pauses.Length != 1 || Math.Abs(pauses[0].Start - 1) > .02 || Math.Abs(pauses[0].End - 1.2) > .02 ||
            Math.Abs(Delay(pauses, TimeSpan.FromSeconds(.8), TimeSpan.FromSeconds(2.2), TimeSpan.FromSeconds(.45)).TotalSeconds - .22) > .02 ||
            Delay(pauses, TimeSpan.FromSeconds(1.1), TimeSpan.FromSeconds(2.2), TimeSpan.FromSeconds(.45)) != TimeSpan.Zero ||
            Delay(pauses, TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(2.2), TimeSpan.FromSeconds(.45)) != TimeSpan.FromSeconds(.45))
            throw new InvalidDataException("Speech pause detection or bounded interruption timing failed.");
    }

    private sealed class ArraySamples(float[] samples, int rate) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, samples.Length - position);
            Array.Copy(samples, position, buffer, offset, available);
            position += available;
            return available;
        }
    }
}
