namespace Beesly;

// One repeat loop per displayed screen. Different phones get different screen IDs.
public sealed class BrightnessHoldService(HomeAssistantService ha, ILogger<BrightnessHoldService> logger)
    : IHostedService, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Hold> holds = [];
    private readonly CancellationTokenSource shutdown = new();

    private sealed class Hold(string view, int step, CancellationTokenSource cancellation)
    {
        public string View { get; } = view;
        public int Step { get; } = step;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    public void Press(Guid screen, string view, int step, IReadOnlyList<HomeAssistantEntity> targets)
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested) return;
            holds.TryGetValue(screen, out var previous);
            // Notify can retry a POST; do not start a second loop or extend its deadline.
            if (previous is not null && previous.View == view && previous.Step == step && !previous.Cancellation.IsCancellationRequested)
                return;
            previous?.Cancellation.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(10));
            var hold = new Hold(view, step, cancellation);
            holds[screen] = hold;
            hold.Completion = Task.Run(() => RepeatAsync(screen, hold, targets, previous?.Completion ?? Task.CompletedTask));
        }
    }

    public void Release(Guid screen, string view, int? step = null)
    {
        lock (gate)
            if (holds.TryGetValue(screen, out var hold) && hold.View == view && (step is null || hold.Step == step))
                hold.Cancellation.Cancel();
    }

    private async Task RepeatAsync(Guid screen, Hold hold, IReadOnlyList<HomeAssistantEntity> targets, Task previous)
    {
        var ct = hold.Cancellation.Token;
        try
        {
            // A direction change waits for the cancelled request before sending the opposite step.
            await previous;
            ct.ThrowIfCancellationRequested();
            await ha.AdjustBrightnessAsync(targets, hold.Step, ct);
            await Task.Delay(400, ct);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await ha.AdjustBrightnessAsync(targets, hold.Step, ct);
                await Task.Delay(70, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Brightness hold stopped for view {View}", hold.View);
        }
        finally
        {
            lock (gate)
            {
                if (holds.GetValueOrDefault(screen) == hold) holds.Remove(screen);
                hold.Cancellation.Dispose();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (gate)
        {
            shutdown.Cancel();
            pending = holds.Values.Select(hold => hold.Completion).ToArray();
        }
        await Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    public void Dispose() => shutdown.Dispose();
}
