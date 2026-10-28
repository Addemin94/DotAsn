using TelemetryProtocol;

namespace Asn1.Benchmarks;

/// <summary>
/// The benchmark message: a telemetry <c>Message</c> carrying a <c>MeasurementBatch</c> with <c>count</c> fully
/// populated measurements (every OPTIONAL component present, including the extension addition).
/// </summary>
internal static class TelemetryMessages
{
    public static Message Create(int count)
    {
        var batch = new MeasurementBatch { DeviceId = [1, 2, 3, 4, 5, 6, 7, 8] };
        for (var i = 0; i < count; i++)
        {
            batch.Measurements.Add(new Measurement
            {
                Timestamp = 1_700_000_000u + (uint)i,
                Temperature = 215,
                Humidity = 40,
                BatteryMv = 3700,
                Position = new Position { Latitude = 593293230, Longitude = 180685810, AltitudeM = 30, AccuracyM = 5 },
                PressurePa = 101325,
            });
        }

        // protocolVersion differs from its DEFAULT so that no encoder can omit it.
        return new Message
        {
            ProtocolVersion = 2,
            MessageType = MessageType.MeasurementBatch,
            Payload = new Payload.MeasurementBatch(batch),
            Crc = [0, 0, 0, 0],
        };
    }
}
