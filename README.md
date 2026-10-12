# Device Simulation

A cross-platform .NET application that simulates RF devices for ECMS when physical devices are unavailable.

The application must run on Windows and Linux as an executable and read config.json from the same directory as the executable.

## 1. Configuration

The configuration file defines the devices to simulate.

Example `config.json`:

```json
{
  "tickIntervalMilliseconds": 1000,
  "devices": [
    {
      "kind": 1,
      "ipAddress": "127.0.0.1",
      "port": 5001
    },
    {
      "kind": 2,
      "ipAddress": "127.0.0.1",
      "port": 5002
    },
    {
      "kind": 3,
      "ipAddress": "127.0.0.1",
      "port": 5003
    }
  ]
}
```

### Properties

- `devices`: required array of devices to simulate. An empty array is valid. Multiple devices may share the same `kind`.
- `tickIntervalMilliseconds`: optional simulation interval in milliseconds (default `1000`, allowed range `1–60000`).
- `kind`: required integer identifying the device type. Only supported values are accepted.
- `ipAddress`: required valid IPv4 address on which the device API must listen.
- `port`: required integer in the TCP port range `1–65535`.

Each device must have a unique `ipAddress` and `port` combination.

The example `config.json` is copied beside the executable during build and publish. Edit that file to choose the device endpoints. The application binds only to the configured IPv4 addresses; those addresses must be assigned to a local network interface. If a port is already in use or an address cannot be bound, startup fails with an error.

The application must validate the configuration before starting. If the file is missing, contains invalid JSON, or violates the expected structure or property constraints, the application must report meaningful errors and terminate.

The application must also handle address binding failures, such as an IP address not assigned to a local network interface or a port already in use.

## 3. Device identification and generation

Generate one simulated device for each configuration entry.

Each device must have:

- `id`: unique integer assigned deterministically according to configuration order, starting at `1`.
- `kind`: device type defined in the configuration.
- `ipAddress`: configured IPv4 address.
- `port`: configured listening port.
- `data`: current simulated data matching the model associated with `kind`.
- `available`: availability state, initially `true`.
- `delayMilliseconds`: artificial response delay, initially `0`.

Device IDs identify individual simulated instances. The `kind` property determines the data model and simulation behavior.

## 4. Supported device types

The application must internally register each supported `kind` with its corresponding data model and simulation logic.

Only the following device types are supported.

### Kind 1 — RfPowerSensor

```csharp
public record RfPowerSensor(
    double ForwardPower,
    double ReflectedPower,
    double Vswr,
    double Temperature
);
```

### Kind 2 — RfPowerAmplifier

```csharp
public record RfPowerAmplifier(
    double ForwardPower,
    double ReflectedPower,
    double Vswr,
    double Temperature,
    double SupplyVoltage,
    double Current,
    double RfInputPower,
    double Gain,
    double Efficiency,
    bool Enabled,
    bool RfOutputEnabled,
    bool PllLocked,
    bool OverTemperature,
    bool OverCurrent,
    bool VswrAlarm
);
```

### Kind 3 — RfMonitoringSystem

```csharp
public record RfMonitoringSystem(
    double Temperature,
    string SystemStatus,
    IReadOnlyList<RfMonitoringChannelData> Channels
);

public record RfMonitoringChannelData(
    double ForwardPower,
    double ReflectedPower,
    double Vswr,
    bool Enabled,
    bool Alarm
);
```

The application must reject any `kind` that does not correspond to a registered device type.

Each device must maintain data matching its associated model. The data must be serialized to JSON using camelCase property names.

## 5. Data simulation

Implement one shared simulation loop that updates all device states at each tick.

- The tick interval must be configurable.
- Each device type must have appropriate simulation rules.
- Numeric properties must evolve over time within suitable operating ranges.
- Boolean and status properties must change according to the simulated device behavior.
- Monitoring system channels must contain simulated channel data.
- HTTP requests must return the latest available state without waiting for the next tick.
- Simulation updates must not be blocked by HTTP requests or artificial response delays.

## 6. HTTP API

Each simulated device must expose its own HTTP API at its configured `ipAddress` and `port`.

### 6.1. Retrieve device data

`GET /device`

Returns the current state of the requested device.

Example response for a device of kind `3`:

```json
{
  "id": 2,
  "kind": 3,
  "data": {
    "temperature": 35.2,
    "systemStatus": "Normal",
    "channels": [
      {
        "forwardPower": 120.5,
        "reflectedPower": 2.1,
        "vswr": 1.18,
        "enabled": true,
        "alarm": false
      }
    ]
  }
}
```

The `data` object must match the model registered for the device's `kind`.

The response must contain exactly three top-level properties: `id`, `kind`, and `data`.

Expected responses:

- `200 OK`: device exists and is available.
- `404 Not Found`: requested device does not exist.
- `503 Service Unavailable`: device is unavailable.

### 6.2. Update device properties

`PATCH /device/data`

Updates one or more properties of the specified device's `data` object.

Example request for a device of kind `2`:

```json
{
  "forwardPower": 150.5,
  "enabled": false
}
```

Example request for a device of kind `3`, updating a property of a specific channel:

```json
{
  "channels": [
    {
      "forwardPower": 120.5
    }
  ]
}
```

Requirements:

- Accept one or more properties belonging to the device's registered data model.
- Validate property names, data types, and values.
- Reject unknown properties, invalid values, and properties incompatible with the device's `kind`.
- For nested data, support updating specific properties without unintentionally replacing unrelated properties.
- Apply the requested changes to the current device state.
- The updated values must not be lost or overwritten by a simulation tick that started before the HTTP update.
- Subsequent simulation ticks must continue evolving all properties according to their normal simulation rules. Values updated through this endpoint must not become fixed or require a separate reset.
- Synchronize concurrent HTTP updates and simulation ticks to prevent stale state from overwriting newer changes.
- Return `200 OK` when the update succeeds.
- Return `400 Bad Request` when the request is invalid.
- Return `404 Not Found` when the device does not exist.

## 7. Device availability

Each device has an `available` boolean property, initially set to `true`.

### 7.1. Set availability

`PUT /device/availability`

Request body:

```json
{
  "available": false
}
```

Expected behavior:

- `true`: the device responds normally.
- `false`: data requests return `503 Service Unavailable` without returning device data.
- `400 Bad Request`: invalid request body.
- `404 Not Found`: device does not exist.

Availability changes must affect only the specified device. The simulation loop must continue updating unavailable devices.

## 8. Response delay

Each device has a `delayMilliseconds` property, initially set to `0`.

### 8.1. Set response delay

`PUT /device/delay`

Request body:

```json
{
  "delayMilliseconds": 2000
}
```

Expected behavior:

- The delay must be a non-negative integer with a documented maximum.
- `0` disables the artificial delay.
- `2000` introduces a two-second delay before responding to subsequent data requests.
- The documented maximum delay is `60000` milliseconds (one minute).
- `400 Bad Request`: invalid delay.
- `404 Not Found`: device does not exist.

The delay must be applied asynchronously. It must not block the simulation loop or requests to other devices.

Changes apply to subsequent requests and must not alter requests already in progress.

Unavailable devices must return `503 Service Unavailable` without applying the artificial delay.

## 9. Testing requirements

Include automated tests covering:

- Configuration parsing and validation.
- Missing or invalid properties.
- Unsupported device kinds.
- Invalid IPv4 addresses and ports.
- Empty device lists and duplicate kinds.
- Deterministic device ID assignment.
- Device data models and JSON serialization.
- Data evolution across simulation ticks.
- API responses for existing and unknown devices.
- Property updates, including nested channel properties.
- Persistence of overridden values across simulation ticks.
- Validation of unknown properties and invalid values.
- Availability changes and unavailable-device responses.
- Valid and invalid response delays.
- Concurrent requests and independent device behavior.
- Continued simulation during delayed requests.

The application must build and run on Windows and Linux.

## Build and run

Install the .NET 8 SDK, then run:

```sh
dotnet build DeviceSimulation.sln
dotnet test DeviceSimulation.sln
dotnet run --project src/DeviceSimulation
```

Publish a self-contained executable with a platform-specific runtime identifier:

```sh
dotnet publish src/DeviceSimulation -c Release -r win-x64 --self-contained true
dotnet publish src/DeviceSimulation -c Release -r linux-x64 --self-contained true
```

`config.json` is published beside the executable and remains an external, editable configuration file.