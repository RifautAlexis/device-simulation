using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using DeviceSimulation.Configuration;
using DeviceSimulation.Devices;
using DeviceSimulation.Models;

namespace DeviceSimulation.Tests;

public sealed class HttpApiTests : IAsyncLifetime
{
    private readonly List<DeviceHttpServer> _servers = [];
    private readonly HttpClient _client = new();
    private IReadOnlyList<SimulatedDevice> _devices = [];

    public async Task InitializeAsync()
    {
        var firstPort = FindFreePort();
        var secondPort = FindFreePort();
        _devices = DeviceFactory.Create(new SimulationConfiguration
        {
            Devices =
            [
                new DeviceConfiguration { Kind = 1, IpAddress = "127.0.0.1", Port = firstPort },
                new DeviceConfiguration { Kind = 3, IpAddress = "127.0.0.1", Port = secondPort }
            ]
        });

        foreach (var device in _devices)
        {
            var server = new DeviceHttpServer(device);
            await server.StartAsync();
            _servers.Add(server);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }

        _client.Dispose();
    }

    [Fact]
    public async Task GetReturnsExactlyIdKindAndDataAndUnknownIdsReturnNotFound()
    {
        var response = await _client.GetAsync(Address(_devices[0], "/device/1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "id", "kind", "data" },
            body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, body.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync(Address(_devices[0], "/device/99"))).StatusCode);
    }

    [Fact]
    public async Task PatchApiSupportsNestedUpdatesAndRejectsInvalidData()
    {
        var device = _devices[1];
        var response = await _client.PatchAsJsonAsync(Address(device, "/device/2/data"),
            new { channels = new[] { new { forwardPower = 333.5 } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(333.5, result.GetProperty("data").GetProperty("channels")[0].GetProperty("forwardPower").GetDouble());

        var bad = await _client.PatchAsJsonAsync(Address(device, "/device/2/data"),
            new { unsupported = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task AvailabilityAndDelayAffectOnlyTheirDevice()
    {
        var first = _devices[0];
        var second = _devices[1];
        var notBoolean = await _client.PutAsJsonAsync(Address(first, "/device/1/availability"),
            new { available = "no" });
        Assert.Equal(HttpStatusCode.BadRequest, notBoolean.StatusCode);

        var disable = await _client.PutAsJsonAsync(Address(first, "/device/1/availability"),
            new { available = false });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await _client.GetAsync(Address(first, "/device/1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.GetAsync(Address(second, "/device/2"))).StatusCode);

        var negativeDelay = await _client.PutAsJsonAsync(Address(second, "/device/2/delay"),
            new { delayMilliseconds = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, negativeDelay.StatusCode);
        var validDelay = await _client.PutAsJsonAsync(Address(second, "/device/2/delay"),
            new { delayMilliseconds = 300 });
        Assert.Equal(HttpStatusCode.OK, validDelay.StatusCode);

        var requestStartedAt = DateTime.UtcNow;
        var request = _client.GetAsync(Address(second, "/device/2"));
        await Task.Delay(60);
        Assert.False(request.IsCompleted);
        var startedAt = DateTime.UtcNow;
        var resetDelay = await _client.PutAsJsonAsync(Address(second, "/device/2/delay"),
            new { delayMilliseconds = 0 });
        Assert.Equal(HttpStatusCode.OK, resetDelay.StatusCode);
        var laterRequest = await _client.GetAsync(Address(second, "/device/2"));
        Assert.Equal(HttpStatusCode.OK, laterRequest.StatusCode);
        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromMilliseconds(150));
        Assert.Equal(HttpStatusCode.OK, (await request).StatusCode);
        Assert.True(DateTime.UtcNow - requestStartedAt >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task DelayedRequestsDoNotBlockTheSharedSimulationLoop()
    {
        var device = _devices[0];
        device.SetDelay(250);
        var before = Assert.IsType<RfPowerSensor>(device.Read()!.Device.Data);
        await using var simulation = new DeviceSimulationService(_devices, 20);
        simulation.Start();

        var delayedGet = _client.GetAsync(Address(device, "/device/1"));
        await Task.Delay(100);
        var duringDelay = Assert.IsType<RfPowerSensor>(device.Read()!.Device.Data);
        Assert.NotEqual(before.ForwardPower, duringDelay.ForwardPower);
        Assert.Equal(HttpStatusCode.OK, (await delayedGet).StatusCode);
    }

    [Fact]
    public async Task UnavailableDeviceReturnsImmediatelyWithoutItsConfiguredDelay()
    {
        var device = _devices[0];
        device.SetDelay(1000);
        device.SetAvailability(false);
        var start = DateTime.UtcNow;
        var response = await _client.GetAsync(Address(device, "/device/1"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task BindingFailureIsReportedWhenAnEndpointIsAlreadyInUse()
    {
        var boundDevice = _devices[0];
        var conflictingDevice = new SimulatedDevice(
            3,
            new DeviceConfiguration
            {
                Kind = 1,
                IpAddress = boundDevice.IpAddress,
                Port = boundDevice.Port
            },
            new DeviceRegistry());
        await using var conflictingServer = new DeviceHttpServer(conflictingDevice);

        var exception = await Record.ExceptionAsync(() => conflictingServer.StartAsync());
        Assert.NotNull(exception);
        Assert.Contains("address", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Address(SimulatedDevice device, string path) =>
        $"http://{device.IpAddress}:{device.Port}{path}";

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
