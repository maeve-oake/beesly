using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Beesly;

internal sealed class AmiConnection(Stream stream) : IAsyncDisposable
{
    private readonly StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, string>>> pending = new();
    public Channel<Dictionary<string, string>> Events { get; } = Channel.CreateUnbounded<Dictionary<string, string>>();

    public async Task SendAsync(string action, Dictionary<string, string> fields, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var response = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = response;
        try
        {
            var lines = new List<string> { $"Action: {action}", $"ActionID: {id}" };
            foreach (var (key, value) in fields)
            {
                if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("AMI values cannot contain newlines.");
                lines.Add($"{key}: {value}");
            }
            await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n"), ct);
            var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (result.GetValueOrDefault("Response") != "Success")
                throw new IOException($"AMI {action}: {result.GetValueOrDefault("Message", "request rejected")}");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            var banner = await reader.ReadLineAsync(ct);
            if (banner?.StartsWith("Asterisk Call Manager/") != true) throw new IOException("Unexpected AMI greeting.");
            var message = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct) ?? throw new IOException("AMI connection closed.");
                if (line.Length == 0)
                {
                    if (message.TryGetValue("ActionID", out var id) && message.ContainsKey("Response") && pending.TryGetValue(id, out var response))
                        response.TrySetResult(message);
                    else if (message.ContainsKey("Event"))
                        Events.Writer.TryWrite(message);
                    message = new(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                var separator = line.IndexOf(':');
                if (separator > 0) message[line[..separator]] = line[(separator + 1)..].TrimStart();
            }
        }
        finally
        {
            foreach (var response in pending.Values) response.TrySetException(new IOException("AMI connection closed."));
            Events.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        reader.Dispose();
        return ValueTask.CompletedTask;
    }
}
