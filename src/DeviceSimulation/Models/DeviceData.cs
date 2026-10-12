namespace DeviceSimulation.Models; 

public sealed class RfPowerSensor
{
    public double ForwardPower { get; init; } = 100;
    public double ReflectedPower { get; init; } = 2;
    public double Vswr { get; init; } = 1.1;
    public double Temperature { get; init; } = 35;
}

public sealed class RfPowerAmplifier
{
    public double ForwardPower { get; init; } = 100;
    public double ReflectedPower { get; init; } = 2;
    public double Vswr { get; init; } = 1.1;
    public double Temperature { get; init; } = 38;
    public double SupplyVoltage { get; init; } = 28;
    public double Current { get; init; } = 5;
    public double RfInputPower { get; init; } = 10;
    public double Gain { get; init; } = 20;
    public double Efficiency { get; init; } = 0.7;
    public bool Enabled { get; init; } = true;
    public bool RfOutputEnabled { get; init; } = true;
    public bool PllLocked { get; init; } = true;
    public bool OverTemperature { get; init; }
    public bool OverCurrent { get; init; }
    public bool VswrAlarm { get; init; }
}

public sealed record RfMonitoringSystem(
    double Temperature,
    string SystemStatus,
    IReadOnlyList<RfMonitoringChannelData> Channels);

public sealed record RfMonitoringChannelData(
    double ForwardPower,
    double ReflectedPower,
    double Vswr,
    bool Enabled,
    bool Alarm);
