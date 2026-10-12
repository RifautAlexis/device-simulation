using System.Text.Json;
using DeviceSimulation.Configuration;

namespace DeviceSimulation.Devices;

public sealed record DeviceSnapshot(int Id, int Kind, object Data);
public sealed record DeviceReadSnapshot(DeviceSnapshot Device, int DelayMilliseconds);

public sealed class SimulatedDevice
{
    private readonly object _sync = new();
    private readonly DeviceRegistry _registry;
    private object _data;
    private bool _available = true;
    private int _delayMilliseconds;

    public SimulatedDevice(int id, DeviceConfiguration configuration, DeviceRegistry registry)
    {
        Id = id;
        Kind = configuration.Kind;
        IpAddress = configuration.IpAddress;
        Port = configuration.Port;
        _registry = registry;
        _data = registry.CreateInitialData(Kind);
    }

    public int Id { get; }
    public int Kind { get; }
    public string IpAddress { get; }
    public int Port { get; }

    public DeviceReadSnapshot? Read()
    {
        lock (_sync)
        {
            return _available
                ? new DeviceReadSnapshot(new DeviceSnapshot(Id, Kind, _data), _delayMilliseconds)
                : null;
        }
    }

    public bool IsAvailable
    {
        get { lock (_sync) return _available; }
    }

    public void SetAvailability(bool available)
    {
        lock (_sync)
        {
            _available = available;
        }
    }

    public void SetDelay(int delayMilliseconds)
    {
        if (delayMilliseconds is < 0 or > DeviceHttpServer.MaximumDelayMilliseconds)
        {
            throw new InvalidDataException(
                $"'delayMilliseconds' must be between 0 and {DeviceHttpServer.MaximumDelayMilliseconds}.");
        }

        lock (_sync)
        {
            _delayMilliseconds = delayMilliseconds;
        }
    }

    public void ApplyPatch(JsonElement patch)
    {
        lock (_sync)
        {
            _data = _registry.ApplyPatch(Kind, _data, patch);
        }
    }

    public void Simulate(long tick)
    {
        lock (_sync)
        {
            _data = _registry.Simulate(Kind, _data, tick);
        }
    }
}

public static class DeviceFactory
{
    public static IReadOnlyList<SimulatedDevice> Create(
        SimulationConfiguration configuration,
        DeviceRegistry? registry = null)
    {
        SimulationConfiguration.Validate(configuration);
        
        registry ??= new DeviceRegistry();
        
        return configuration.Devices!
            .Select((device, index) => new SimulatedDevice(index + 1, device, registry))
            .ToArray();
    }
}
