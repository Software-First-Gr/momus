using System.Text.Json;
using System.Text.Json.Serialization;

namespace Momus.Core;

/// <summary>
/// Writes a subject as its compact text form (<c>table:public.orders</c>) rather than an object.
/// Consumers of the JSON report — scripts, agents, the server — match on one string instead of
/// two fields, and it is the same notation the docs and the UI use.
/// </summary>
public sealed class SubjectJsonConverter : JsonConverter<Subject>
{
    public override Subject Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Subject.Parse(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, Subject value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
