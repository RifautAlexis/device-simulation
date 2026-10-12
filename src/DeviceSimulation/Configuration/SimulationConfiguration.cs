using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSimulation.Devices;

namespace DeviceSimulation.Configuration;

public sealed class SimulationConfiguration
{
    public const int DefaultTickIntervalMilliseconds = 1000;
    public const int MaximumTickIntervalMilliseconds = 60000;

    public required List<DeviceConfiguration>? Devices { get; init; }
    public int TickIntervalMilliseconds { get; init; } = DefaultTickIntervalMilliseconds;

    public static SimulationConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Configuration file was not found: {path}");
        }

        SimulationConfiguration? configuration;
        
        try
        {
            configuration = JsonSerializer.Deserialize<SimulationConfiguration>( File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Configuration JSON is invalid or does not match the expected structure: {exception.Message}",
                exception);
        }

        if (configuration is null)
        {
            throw new InvalidDataException("Configuration must be a JSON object.");
        }

        Validate(configuration);
        return configuration;
    }

    public static void Validate(SimulationConfiguration configuration)
    {
        if (configuration.Devices is null)
        {
            throw new InvalidDataException("The required 'devices' property must be an array.");
        }

        if (configuration.TickIntervalMilliseconds is < 1 or > MaximumTickIntervalMilliseconds)
        {
            throw new InvalidDataException( $"'tickIntervalMilliseconds' must be between 1 and {MaximumTickIntervalMilliseconds}.");
        }

        var endpoints = new HashSet<(IPAddress Address, int Port)>();
        
        foreach (var device in configuration.Devices)
        {
            if (device is null)
            {
                throw new InvalidDataException("'devices' cannot contain null entries.");
            }

            if (!DeviceRegistry.IsSupportedKind(device.Kind))
            {
                throw new InvalidDataException($"Unsupported device kind {device.Kind}. Supported kinds are: {string.Join(", ", DeviceRegistry.SupportedKinds)}.");
            }

            if (!TryParseIpv4(device.IpAddress, out var address))
            {
                throw new InvalidDataException($"Device with IP address {device.IpAddress} has an invalid IPv4 'ipAddress': '{device.IpAddress}'.");
            }

            if (device.Port is < 1 or > 65535)
            {
                throw new InvalidDataException($"Device at {device.IpAddress} has an invalid port {device.Port}; expected 1–65535.");
            }

            if (!endpoints.Add((address, device.Port)))
            {
                throw new InvalidDataException($"The endpoint {device.IpAddress}:{device.Port} is configured more than once.");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static bool TryParseIpv4(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segments = value.Split('.');
        if (segments.Length != 4)
        {
            return false;
        }

        var octets = new byte[4];
        for (var index = 0; index < segments.Length; index++)
        {
            if (!byte.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out octets[index]))
            {
                return false;
            }
        }

        address = new IPAddress(octets);
        return true;
    }
}

public sealed class DeviceConfiguration
{
    public required int Kind { get; init; }
    public required string IpAddress { get; init; }
    public required int Port { get; init; }
}
