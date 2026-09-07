using System.Text.Json;
using System.Text.Json.Serialization;
using Momus.Core;

namespace Momus.Cli;

public static class JsonReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), new SubjectJsonConverter() },
    };

    public static string Serialize(ScanReport report) => JsonSerializer.Serialize(report, Options);
}
