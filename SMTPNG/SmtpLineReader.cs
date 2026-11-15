using System.Text;

namespace DummySmtp;

// Bound memory even when a client never sends a line terminator.
internal sealed class SmtpLineReader(Stream stream) : IDisposable
{
    private readonly StreamReader reader = new(stream, Encoding.Latin1, false, 4096, true);
    private readonly char[] buffer = new char[4096];
    private int start, end;
    internal async Task<string?> ReadLineAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (start == end)
            {
                end = await reader.ReadAsync(buffer.AsMemory(), token); start = 0;
                if (end == 0) return line.Length == 0 ? null : line.ToString();
            }
            int newline = Array.IndexOf(buffer, '\n', start, end - start);
            int count = (newline < 0 ? end : newline) - start;
            if (line.Length + count > 65536) throw new IOException("SMTP line exceeds 64 KiB limit.");
            line.Append(buffer, start, count); start += count;
            if (newline >= 0)
            {
                start++;
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return line.ToString();
            }
        }
    }
    public void Dispose() => reader.Dispose();
}
