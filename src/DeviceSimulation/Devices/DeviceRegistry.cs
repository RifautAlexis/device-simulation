using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeviceSimulation.Configuration;
using DeviceSimulation.Models;

namespace DeviceSimulation.Devices;

public enum DeviceKind
{
    RfPowerSensor = 1,
    RfPowerAmplifier = 2,
    RfMonitoringSystem = 3
}

public sealed class DeviceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IReadOnlyDictionary<int, IDeviceModel> _models= new Dictionary<int, IDeviceModel>
        {
            [(int)DeviceKind.RfPowerSensor] = new DeviceModel<RfPowerSensor>(
                new RfPowerSensor(),
                SimulateSensor,
                ValidateSensor),
            [(int)DeviceKind.RfPowerAmplifier] = new DeviceModel<RfPowerAmplifier>(
                new RfPowerAmplifier(),
                SimulateAmplifier,
                ValidateAmplifier),
            [(int)DeviceKind.RfMonitoringSystem] = new DeviceModel<RfMonitoringSystem>(
                new RfMonitoringSystem(
                    34, 
                    "Normal",
                    CreateChannels()),
                SimulateMonitoringSystem,
                ValidateMonitoringSystem),
        };

    public static bool IsSupportedKind(int kind) => Enum.IsDefined(typeof(DeviceKind), kind);

    public static IReadOnlyList<int> SupportedKinds => Enum.GetValues(typeof(DeviceKind)).Cast<int>().ToList();

    public object CreateInitialData(int kind) => GetModel(kind).InitialData;

    public object Simulate(int kind, object data, long tick)
    {
        var model = GetModel(kind);
        return model.Simulate(data, tick);
    }

    public object ApplyPatch(int kind, object currentData, JsonElement patch)
    {
        var model = GetModel(kind);
        if (patch.ValueKind != JsonValueKind.Object || !patch.EnumerateObject().Any())
        {
            throw new InvalidDataException("The patch body must be a non-empty JSON object.");
        }

        try
        {
            var current = JsonSerializer.SerializeToNode(currentData, currentData.GetType(), JsonOptions)
                ?? throw new InvalidDataException("Current device data could not be serialized.");
            var update = JsonNode.Parse(patch.GetRawText()) as JsonObject
                ?? throw new InvalidDataException("The patch body must be a JSON object.");
            Merge(current, update, "data");
            var result = current.Deserialize(model.DataType, JsonOptions)
                ?? throw new InvalidDataException("The patch produced empty device data.");
            model.Validate(result);
            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid device data: {exception.Message}", exception);
        }
    }

    public static JsonSerializerOptions SerializationOptions => JsonOptions;

    public static void ValidateConfiguration(SimulationConfiguration configuration) =>
        SimulationConfiguration.Validate(configuration);

    private IDeviceModel GetModel(int kind) =>
        _models.TryGetValue(kind, out var model)
            ? model
            : throw new InvalidDataException($"Unsupported device kind {kind}.");

    private static void Merge(JsonNode target, JsonNode update, string path)
    {
        if (target is JsonObject targetObject && update is JsonObject updateObject)
        {
            foreach (var (key, value) in updateObject)
            {
                if (value is null)
                {
                    throw new InvalidDataException($"'{path}.{key}' cannot be null.");
                }

                if (!targetObject.TryGetPropertyValue(key, out var existing))
                {
                    targetObject[key] = value.DeepClone();
                }
                else if (existing is JsonObject existingObject && value is JsonObject valueObject)
                {
                    Merge(existingObject, valueObject, $"{path}.{key}");
                }
                else if (existing is JsonArray existingArray && value is JsonArray valueArray)
                {
                    if (valueArray.Count == 0 || valueArray.Count > existingArray.Count)
                    {
                        throw new InvalidDataException($"'{path}.{key}' must contain one or more existing channel updates.");
                    }

                    for (var index = 0; index < valueArray.Count; index++)
                    {
                        if (valueArray[index] is not JsonObject channelUpdate ||
                            existingArray[index] is not JsonObject existingChannel)
                        {
                            throw new InvalidDataException($"'{path}.{key}[{index}]' must be a JSON object.");
                        }

                        if (channelUpdate.Count == 0 && index == valueArray.Count - 1)
                        {
                            throw new InvalidDataException("A channel update must contain at least one property.");
                        }

                        Merge(existingChannel, channelUpdate, $"{path}.{key}[{index}]");
                    }
                }
                else
                {
                    if (existing is JsonArray)
                    {
                        throw new InvalidDataException($"'{path}.{key}' must be an array of channel updates.");
                    }

                    targetObject[key] = value.DeepClone();
                }
            }

            return;
        }

        throw new InvalidDataException($"'{path}' has an incompatible JSON structure.");
    }

    private static IReadOnlyList<RfMonitoringChannelData> CreateChannels() =>
    [
        new(110, 2.2, 1.15, true, false),
        new(95, 1.5, 1.1, true, false),
        new(0, 0, 1, false, false),
        new(75, 1.2, 1.08, true, false)
    ];

    private static RfPowerSensor SimulateSensor(RfPowerSensor current, long tick)
    {
        var phase = tick * 0.23;
        var forward = 100 + 18 * Math.Sin(phase);
        var reflected = Math.Max(0, 2.5 + 1.2 * Math.Sin(phase * 0.71));
        return Update(current, data =>
        {
            data["forwardPower"] = JsonValue.Create(forward);
            data["reflectedPower"] = JsonValue.Create(reflected);
            data["vswr"] = JsonValue.Create(1 + reflected / Math.Max(forward, 1) * 2);
            data["temperature"] = JsonValue.Create(35 + 4 * Math.Sin(phase * 0.17));
        });
    }

    private static RfPowerAmplifier SimulateAmplifier(RfPowerAmplifier current, long tick)
    {
        var enabled = tick % 40 != 0;
        var phase = tick * 0.19;
        var input = enabled ? 3 + 0.5 * Math.Sin(phase) : 0;
        var forward = enabled ? 120 + 20 * Math.Sin(phase) : 0;
        var highVswr = enabled && tick % 113 is >= 45 and <= 47;
        var overTemperature = tick % 170 is >= 90 and <= 94;
        var overCurrent = enabled && tick % 137 is >= 70 and <= 72;
        var reflected = enabled
            ? highVswr ? 100 : 2 + 0.8 * Math.Sin(phase * 0.63)
            : 0;
        var temperature = 38 + 12 * Math.Max(0, Math.Sin(phase * 0.11)) + (overTemperature ? 30 : 0);
        var vswr = enabled ? 1 + reflected / Math.Max(forward, 1) * 2 : 1;
        var currentDraw = enabled ? overCurrent ? 25 : 3.2 + input * 0.4 : 0;
        return Update(current, data =>
        {
            data["forwardPower"] = JsonValue.Create(forward);
            data["reflectedPower"] = JsonValue.Create(reflected);
            data["vswr"] = JsonValue.Create(vswr);
            data["temperature"] = JsonValue.Create(temperature);
            data["supplyVoltage"] = JsonValue.Create(48 + 0.5 * Math.Sin(phase * 0.3));
            data["current"] = JsonValue.Create(currentDraw);
            data["rfInputPower"] = JsonValue.Create(input);
            data["gain"] = JsonValue.Create(enabled ? forward / Math.Max(input, 0.1) : 0);
            data["efficiency"] = JsonValue.Create(enabled ? Math.Clamp(forward / (48 * Math.Max(currentDraw, 0.1)) * 100, 0, 100) : 0);
            data["enabled"] = JsonValue.Create(enabled);
            data["rfOutputEnabled"] = JsonValue.Create(enabled && tick % 120 >= 2);
            data["pllLocked"] = JsonValue.Create(tick % 100 != 0);
            data["overTemperature"] = JsonValue.Create(overTemperature);
            data["overCurrent"] = JsonValue.Create(overCurrent);
            data["vswrAlarm"] = JsonValue.Create(highVswr);
        });
    }

    private static RfMonitoringSystem SimulateMonitoringSystem(RfMonitoringSystem current, long tick)
    {
        var channels = current.Channels.Select((channel, index) =>
        {
            var phase = tick * 0.16 + index * 1.4;
            var enabled = index != 2 && tick % 80 >= index;
            var forward = enabled ? Math.Max(0, 90 + index * 12 + 14 * Math.Sin(phase)) : 0;
            var alarm = enabled && tick % 173 is >= 100 and <= 102;
            var reflected = enabled
                ? alarm ? 70 : Math.Max(0, 1.5 + 0.7 * Math.Sin(phase * 0.8))
                : 0;
            var vswr = enabled ? 1 + reflected / Math.Max(forward, 1) * 2 : 1;
            return Update(channel, data =>
            {
                data["forwardPower"] = JsonValue.Create(forward);
                data["reflectedPower"] = JsonValue.Create(reflected);
                data["vswr"] = JsonValue.Create(vswr);
                data["enabled"] = JsonValue.Create(enabled);
                data["alarm"] = JsonValue.Create(enabled && (alarm || vswr > 1.8 || forward > 500));
            });
        }).ToArray();

        var temperature = 34 + 9 * Math.Max(0, Math.Sin(tick * 0.025));
        var status = temperature > 42 || channels.Any(channel => channel.Alarm) ? "Warning" : "Normal";
        return Update(current, data =>
        {
            data["temperature"] = JsonValue.Create(temperature);
            data["systemStatus"] = JsonValue.Create(status);
            data["channels"] = JsonSerializer.SerializeToNode(channels, JsonOptions);
        });
    }

    private static T Update<T>(T current, Action<JsonObject> update) where T : class
    {
        var data = JsonSerializer.SerializeToNode(current, JsonOptions) as JsonObject
            ?? throw new InvalidDataException("Current device data could not be serialized.");
        update(data);
        return data.Deserialize<T>(JsonOptions)
            ?? throw new InvalidDataException("Updated device data could not be deserialized.");
    }

    private static void ValidateSensor(RfPowerSensor value)
    {
        CheckRange(value.ForwardPower, 0, 5000, "forwardPower");
        CheckRange(value.ReflectedPower, 0, 5000, "reflectedPower");
        CheckRange(value.Vswr, 1, 100, "vswr");
        CheckRange(value.Temperature, -50, 200, "temperature");
    }

    private static void ValidateAmplifier(RfPowerAmplifier value)
    {
        CheckRange(value.ForwardPower, 0, 5000, "forwardPower");
        CheckRange(value.ReflectedPower, 0, 5000, "reflectedPower");
        CheckRange(value.Vswr, 1, 100, "vswr");
        CheckRange(value.Temperature, -50, 200, "temperature");
        CheckRange(value.SupplyVoltage, 0, 100, "supplyVoltage");
        CheckRange(value.Current, 0, 1000, "current");
        CheckRange(value.RfInputPower, 0, 5000, "rfInputPower");
        CheckRange(value.Gain, 0, 200, "gain");
        CheckRange(value.Efficiency, 0, 100, "efficiency");
    }

    private static void ValidateMonitoringSystem(RfMonitoringSystem value)
    {
        CheckRange(value.Temperature, -50, 200, "temperature");
        if (value.SystemStatus is not ("Normal" or "Warning" or "Critical"))
        {
            throw new InvalidDataException("'systemStatus' must be 'Normal', 'Warning', or 'Critical'.");
        }

        if (value.Channels is null || value.Channels.Count != 4)
        {
            throw new InvalidDataException("'channels' must contain exactly four channel entries.");
        }

        foreach (var (channel, index) in value.Channels.Select((item, index) => (item, index)))
        {
            CheckRange(channel.ForwardPower, 0, 5000, $"channels[{index}].forwardPower");
            CheckRange(channel.ReflectedPower, 0, 5000, $"channels[{index}].reflectedPower");
            CheckRange(channel.Vswr, 1, 100, $"channels[{index}].vswr");
        }
    }

    private static void CheckRange(double value, double minimum, double maximum, string property)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new InvalidDataException($"'{property}' must be between {minimum} and {maximum}.");
        }
    }

    private interface IDeviceModel
    {
        Type DataType { get; }
        object InitialData { get; }
        object Simulate(object current, long tick);
        void Validate(object data);
    }

    private sealed class DeviceModel<T>(
        T initialData,
        Func<T, long, T> simulator,
        Action<T> validator) : IDeviceModel where T : class
    {
        public Type DataType => typeof(T);
        public object InitialData => initialData;
        public object Simulate(object current, long tick) => simulator((T)current, tick);
        public void Validate(object data) => validator((T)data);
    }
}
