using System.Text;

namespace KardsVox.Core;

internal sealed class SessionLog : IDisposable
{
    private readonly TextWriter originalOut;
    private readonly TextWriter originalError;
    private readonly LogSink sink;
    private bool disposed;

    private SessionLog(TextWriter originalOut, TextWriter originalError, LogSink sink)
    {
        this.originalOut = originalOut;
        this.originalError = originalError;
        this.sink = sink;
    }

    public static SessionLog? TryStart(string runtimeRoot)
    {
        string[] directories =
        [
            Path.Combine(runtimeRoot, "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KardsVox", "logs")
        ];
        string? lastError = null;
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            StreamWriter? writer = null;
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"kardsvox-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                var sink = new LogSink(writer);
                TextWriter originalOut = Console.Out;
                TextWriter originalError = Console.Error;
                var session = new SessionLog(originalOut, originalError, sink);
                Console.SetOut(new TeeTextWriter(originalOut, sink));
                Console.SetError(new TeeTextWriter(originalError, sink));
                Console.WriteLine($"Session log: {path}");
                return session;
            }
            catch (Exception ex)
            {
                writer?.Dispose();
                lastError = ex.Message;
            }
        }
        try { Console.Error.WriteLine($"Session logging disabled: {lastError}"); } catch { }
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Console.SetOut(originalOut);
        Console.SetError(originalError);
        sink.Dispose();
    }

    private sealed class LogSink(StreamWriter writer) : IDisposable
    {
        private readonly object gate = new();

        public void Write(string? value)
        {
            lock (gate) writer.Write(value);
        }

        public void WriteLine(string? value)
        {
            lock (gate) writer.WriteLine(value);
        }

        public void Flush()
        {
            lock (gate) writer.Flush();
        }

        public void Dispose()
        {
            lock (gate) writer.Dispose();
        }
    }

    private sealed class TeeTextWriter(TextWriter original, LogSink sink) : TextWriter
    {
        public override Encoding Encoding => original.Encoding;

        public override void Write(char value)
        {
            original.Write(value);
            sink.Write(value.ToString());
        }

        public override void Write(string? value)
        {
            original.Write(value);
            sink.Write(value);
        }

        public override void WriteLine(string? value)
        {
            original.WriteLine(value);
            sink.WriteLine(value);
        }

        public override void Flush()
        {
            original.Flush();
            sink.Flush();
        }
    }
}
