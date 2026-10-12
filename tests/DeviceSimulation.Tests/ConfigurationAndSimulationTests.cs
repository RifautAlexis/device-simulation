using System.Net;
using System.Text.Json;
using DeviceSimulation.Configuration;
using DeviceSimulation.Devices;
using DeviceSimulation.Models;

namespace DeviceSimulation.Tests;

public sealed class ConfigurationAndSimulationTests
{
    [Fact]
    public void MissingConfigurationFileProducesMeaningfulError()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SimulationConfiguration.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));

        Assert.Contains("not found", exception.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"devices\":[{\"ipAddress\":\"127.0.0.1\",\"port\":1234}]}")]
    [InlineData("{\"devices\":[{\"kind\":99,\"ipAddress\":\"127.0.0.1\",\"port\":1234}]}")]
    [InlineData("{\"devices\":[{\"kind\":1,\"ipAddress\":\"localhost\",\"port\":1234}]}")]
    [InlineData("{\"devices\":[{\"kind\":1,\"ipAddress\":\"127.1\",\"port\":1234}]}")]
    [InlineData("{\"devices\":[{\"kind\":1,\"ipAddress\":\"127.0.0.1\",\"port\":0}]}")]
    [InlineData("{\"devices\":[{\"kind\":1,\"ipAddress\":\"127.0.0.1\",\"port\":65536}]}")]
    [InlineData("{\"devices\":[{\"kind\":1,\"ipAddress\":\"127.0.0.1\",\"port\":1234},{\"kind\":2,\"ipAddress\":\"127.0.0.1\",\"port\":1234}]}")]
    [InlineData("{\"tickIntervalMilliseconds\":60001,\"devices\":[]}")]
    public void InvalidConfigurationIsRejected(string json)
    {
        var path = WriteTemporaryConfiguration(json);
        try
        {
            Assert.Throws<InvalidDataException>(() => SimulationConfiguration.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmptyDeviceListAndDuplicateKindsAreAccepted()
    {
        var empty = CreateConfiguration([]);
        Assert.Empty(DeviceFactory.Create(empty));

        var sameKind = CreateConfiguration(
            new DeviceConfiguration { Kind = 1, IpAddress = "127.0.0.1", Port = 7001 },
            new DeviceConfiguration { Kind = 1, IpAddress = "127.0.0.1", Port = 7002 });
        var devices = DeviceFactory.Create(sameKind);

        Assert.Equal(new[] { 1, 2 }, devices.Select(device => device.Id));
        Assert.All(devices, device => Assert.IsType<RfPowerSensor>(device.Read()!.Device.Data));
    }

    [Fact]
    public void ConfigurationLoadsOptionalTickInterval()
    {
        var path = WriteTemporaryConfiguration(
            "{\"tickIntervalMilliseconds\":25,\"devices\":[]}");
        try
        {
            var configuration = SimulationConfiguration.Load(path);
            Assert.Equal(25, configuration.TickIntervalMilliseconds);
            Assert.Empty(configuration.Devices!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeviceModelsSerializeWithCamelCaseProperties()
    {
        var device = DeviceFactory.Create(CreateConfiguration(
            new DeviceConfiguration { Kind = 3, IpAddress = "127.0.0.1", Port = 7003 }))[0];
        var json = JsonSerializer.Serialize(device.Read()!.Device.Data, DeviceRegistry.SerializationOptions);

        Assert.Contains("\"temperature\"", json);
        Assert.Contains("\"systemStatus\"", json);
        Assert.Contains("\"forwardPower\"", json);
        Assert.DoesNotContain("\"Temperature\"", json);
    }

    [Fact]
    public void SimulationUpdatesAllKindsAndMaintainsValidModelValues()
    {
        var devices = DeviceFactory.Create(CreateConfiguration(
            new DeviceConfiguration { Kind = 1, IpAddress = "127.0.0.1", Port = 7004 },
            new DeviceConfiguration { Kind = 2, IpAddress = "127.0.0.1", Port = 7005 },
            new DeviceConfiguration { Kind = 3, IpAddress = "127.0.0.1", Port = 7006 }));
        var originalSensor = Assert.IsType<RfPowerSensor>(devices[0].Read()!.Device.Data);

        foreach (var device in devices)
        {
            device.Simulate(1);
        }

        var sensor = Assert.IsType<RfPowerSensor>(devices[0].Read()!.Device.Data);
        var amplifier = Assert.IsType<RfPowerAmplifier>(devices[1].Read()!.Device.Data);
        var monitoring = Assert.IsType<RfMonitoringSystem>(devices[2].Read()!.Device.Data);
        Assert.NotEqual(originalSensor.ForwardPower, sensor.ForwardPower);
        Assert.InRange(amplifier.Efficiency, 0, 100);
        Assert.Equal(4, monitoring.Channels.Count);
        Assert.All(monitoring.Channels, channel => Assert.InRange(channel.Vswr, 1, 100));
    }

    [Fact]
    public async Task TimedSimulationUpdatesSensorAndAmplifierData()
    {
        var devices = DeviceFactory.Create(CreateConfiguration(
            new DeviceConfiguration { Kind = 1, IpAddress = "127.0.0.1", Port = 7010 },
            new DeviceConfiguration { Kind = 2, IpAddress = "127.0.0.1", Port = 7011 }));
        var initialSensor = Assert.IsType<RfPowerSensor>(devices[0].Read()!.Device.Data);
        var initialAmplifier = Assert.IsType<RfPowerAmplifier>(devices[1].Read()!.Device.Data);

        await using (var simulation = new DeviceSimulationService(devices, 10))
        {
            simulation.Start();
            await Task.Delay(75);
        }

        var updatedSensor = Assert.IsType<RfPowerSensor>(devices[0].Read()!.Device.Data);
        var updatedAmplifier = Assert.IsType<RfPowerAmplifier>(devices[1].Read()!.Device.Data);
        Assert.NotEqual(initialSensor.ForwardPower, updatedSensor.ForwardPower);
        Assert.NotEqual(initialAmplifier.ForwardPower, updatedAmplifier.ForwardPower);
        Assert.NotEqual(initialAmplifier.SupplyVoltage, updatedAmplifier.SupplyVoltage);
    }

    [Fact]
    public void PatchChangesOnlyRequestedPropertiesAndValidatesValues()
    {
        var sensor = DeviceFactory.Create(CreateConfiguration(
            new DeviceConfiguration { Kind = 1, IpAddress = "127.0.0.1", Port = 7007 }))[0];
        using var patch = JsonDocument.Parse("{\"forwardPower\":222.5}");
        sensor.ApplyPatch(patch.RootElement);
        var data = Assert.IsType<RfPowerSensor>(sensor.Read()!.Device.Data);
        Assert.Equal(222.5, data.ForwardPower);
        Assert.Equal(2, data.ReflectedPower);

        using var unknown = JsonDocument.Parse("{\"mystery\":1}");
        Assert.Throws<InvalidDataException>(() => sensor.ApplyPatch(unknown.RootElement));
        using var invalidType = JsonDocument.Parse("{\"forwardPower\":\"high\"}");
        Assert.Throws<InvalidDataException>(() => sensor.ApplyPatch(invalidType.RootElement));
        using var invalidValue = JsonDocument.Parse("{\"vswr\":0.5}");
        Assert.Throws<InvalidDataException>(() => sensor.ApplyPatch(invalidValue.RootElement));
    }

    [Fact]
    public void NestedPatchKeepsUnchangedChannelsAndSimulationContinues()
    {
        var device = DeviceFactory.Create(CreateConfiguration(
            new DeviceConfiguration { Kind = 3, IpAddress = "127.0.0.1", Port = 7008 }))[0];
        using var patch = JsonDocument.Parse("{\"channels\":[{\"forwardPower\":222.5}]}");
        device.ApplyPatch(patch.RootElement);
        var patched = Assert.IsType<RfMonitoringSystem>(device.Read()!.Device.Data);
        Assert.Equal(222.5, patched.Channels[0].ForwardPower);
        Assert.Equal(2.2, patched.Channels[0].ReflectedPower);
        Assert.Equal(4, patched.Channels.Count);

        device.Simulate(1);
        var nextTick = Assert.IsType<RfMonitoringSystem>(device.Read()!.Device.Data);
        Assert.NotEqual(222.5, nextTick.Channels[0].ForwardPower);
        Assert.Equal(4, nextTick.Channels.Count);
    }

    [Fact]
    public async Task ConcurrentUpdatesAndSimulationRemainSynchronized()
    {
        var device = DeviceFactory.Create(CreateConfiguration(
            new DeviceConfiguration { Kind = 1, IpAddress = "127.0.0.1", Port = 7009 }))[0];
        var updates = Enumerable.Range(1, 100).Select(value => Task.Run(() =>
        {
            using var patch = JsonDocument.Parse($"{{\"forwardPower\":{value}}}");
            device.ApplyPatch(patch.RootElement);
        }));
        var ticks = Enumerable.Range(1, 100).Select(value => Task.Run(() => device.Simulate(value)));

        await Task.WhenAll(updates.Concat(ticks));
        var data = Assert.IsType<RfPowerSensor>(device.Read()!.Device.Data);
        Assert.InRange(data.ForwardPower, 0, 5000);
    }

    private static SimulationConfiguration CreateConfiguration(params DeviceConfiguration[] devices) =>
        new() { Devices = devices.ToList() };

    private static string WriteTemporaryConfiguration(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
