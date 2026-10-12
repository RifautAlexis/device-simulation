using System.Diagnostics;
using DeviceSimulation.Configuration;
using DeviceSimulation.Devices;

namespace DeviceSimulation;

public static class Program
{
    public static async Task<int> Main()
    {
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        SimulationConfiguration configuration;
        IReadOnlyList<SimulatedDevice> devices;
        
        try
        {
            configuration = SimulationConfiguration.Load(configurationPath);
            devices = DeviceFactory.Create(configuration);
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return 1;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        var servers = new List<DeviceHttpServer>();
        
        await using var simulation = new DeviceSimulationService(devices, configuration.TickIntervalMilliseconds);
        
        try
        {
            foreach (var device in devices)
            {
                var server = new DeviceHttpServer(device);
                try
                {
                    await server.StartAsync(shutdown.Token);
                    servers.Add(server);
                    Console.WriteLine($"Device {device.Id} (kind {device.Kind}) listening on http://{device.IpAddress}:{device.Port}");
                }
                catch
                {
                    await server.DisposeAsync();
                    throw;
                }
            }

            simulation.Start();
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"Failed to start device API: {exception.Message}");
            return 1;
        }
        finally
        {
            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }
        }
    }
}
