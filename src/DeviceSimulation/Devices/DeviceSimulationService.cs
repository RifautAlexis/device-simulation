using System.Text.Json;

namespace DeviceSimulation.Devices;

public sealed class DeviceSimulationService(
    IReadOnlyList<SimulatedDevice> devices,
    int tickIntervalMilliseconds) : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The simulation loop has already started.");
        }

        _cancellation = new CancellationTokenSource();
        _loop = RunAsync(_cancellation.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        long tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(tickIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            tick++;
            foreach (var device in devices)
            {
                device.Simulate(tick);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
    }
}
