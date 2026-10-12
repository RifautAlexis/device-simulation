using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DeviceSimulation.Devices;

public sealed class DeviceHttpServer(SimulatedDevice device) : IAsyncDisposable
{
    public const int MaximumDelayMilliseconds = 60000;
    private WebApplication? _application;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(DeviceHttpServer).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Parse(device.IpAddress), device.Port));

        var application = builder.Build();
        application.MapGet("/device", GetDataAsync);
        application.MapPatch("/device/data", PatchDataAsync);
        application.MapPut("/device/availability", SetAvailabilityAsync);
        application.MapPut("/device/delay", SetDelayAsync);

        try
        {
            await application.StartAsync(cancellationToken);
            _application = application;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    private Task<IResult> GetDataAsync(CancellationToken cancellationToken)
    {
        var snapshot = device.Read();
        if (snapshot is null)
        {
            return Task.FromResult(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        return RespondWithDelayAsync(snapshot, cancellationToken);
    }

    private static async Task<IResult> RespondWithDelayAsync(
        DeviceReadSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.DelayMilliseconds > 0)
        {
            await Task.Delay(snapshot.DelayMilliseconds, cancellationToken);
        }

        return Results.Json(new
        {
            id = snapshot.Device.Id,
            kind = snapshot.Device.Kind,
            data = snapshot.Device.Data
        }, DeviceRegistry.SerializationOptions);
    }

    private async Task<IResult> PatchDataAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            device.ApplyPatch(document.RootElement);
            var snapshot = device.Read();
            return snapshot is null
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.Json(new
                {
                    id = snapshot.Device.Id,
                    kind = snapshot.Device.Kind,
                    data = snapshot.Device.Data
                }, DeviceRegistry.SerializationOptions);
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = $"Invalid JSON request body: {exception.Message}" });
        }
        catch (InvalidDataException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private async Task<IResult> SetAvailabilityAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (!TryReadSingleProperty(document.RootElement, "available", out var value) ||
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Results.BadRequest(new { error = "Request body must contain one boolean 'available' property." });
            }

            device.SetAvailability(value.GetBoolean());
            return Results.Ok();
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = $"Invalid JSON request body: {exception.Message}" });
        }
    }

    private async Task<IResult> SetDelayAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (!TryReadSingleProperty(document.RootElement, "delayMilliseconds", out var value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var delay) ||
                delay is < 0 or > MaximumDelayMilliseconds)
            {
                return Results.BadRequest(new
                {
                    error = $"'delayMilliseconds' must be an integer between 0 and {MaximumDelayMilliseconds}."
                });
            }

            device.SetDelay(delay);
            return Results.Ok();
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = $"Invalid JSON request body: {exception.Message}" });
        }
    }

    private static bool TryReadSingleProperty(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name != name)
        {
            return false;
        }

        value = properties[0].Value;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
