using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seiza.App.Models;

internal static class ParallaxJson
{
    // Invalid draft numbers must remain editable/undoable. Native requests use
    // the strict context below; only structural draft comparisons allow NaN.
    private static readonly ParallaxJsonContext DraftContext = new(new JsonSerializerOptions(ParallaxJsonContext.Default.Options)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });
    internal static string SerializeRequest(ParallaxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteProperties(writer, JsonSerializer.SerializeToElement(request.Inputs, ParallaxJsonContext.Default.ParallaxInputs));
            WriteProperties(writer, JsonSerializer.SerializeToElement(request.Scene, ParallaxJsonContext.Default.ParallaxSceneSettings));
            WriteProperties(writer, JsonSerializer.SerializeToElement(request.Video, ParallaxJsonContext.Default.ParallaxVideoSettings));
            if (request.AutoTour is not null)
            {
                writer.WritePropertyName("autoTour");
                JsonSerializer.Serialize(writer, request.AutoTour, ParallaxJsonContext.Default.ParallaxTourOptions);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    // {"path": ...} for a dust map file, or {} for the dust counted from the stars.
    internal static string SerializeDustMapPath(string? path) => WriteObject(writer =>
    {
        if (path is not null) writer.WriteString("path", path);
    });

    // {"size": "WIDTHxHEIGHT", "autoTour": {targets, hold, motion}} for a tour
    // planned on a prepared scene.
    internal static string SerializeSceneTourSettings(string size, ParallaxTourOptions options) => WriteObject(writer =>
    {
        writer.WriteString("size", size);
        writer.WritePropertyName("autoTour");
        JsonSerializer.Serialize(writer, options, ParallaxJsonContext.Default.ParallaxTourOptions);
    });

    internal static string SerializeDustMapWrite(string path, bool counted) => WriteObject(writer =>
    {
        writer.WriteString("path", path);
        writer.WriteBoolean("counted", counted);
    });

    private static string WriteObject(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    internal static string SerializeVideo(ParallaxVideoSettings video) => JsonSerializer.Serialize(video, ParallaxJsonContext.Default.ParallaxVideoSettings);
    internal static string SerializePlan(ParallaxTourPlan plan) => JsonSerializer.Serialize(plan, ParallaxJsonContext.Default.ParallaxTourPlan);
    internal static string SerializeComposition(ParallaxComposition composition) => JsonSerializer.Serialize(composition, DraftContext.ParallaxComposition);
    internal static string SerializeScene(ParallaxSceneSettings scene) => JsonSerializer.Serialize(scene, DraftContext.ParallaxSceneSettings);

    internal static ParallaxTourPlan DeserializePlan(string json)
    {
        var result = JsonSerializer.Deserialize(json, ParallaxJsonContext.Default.ParallaxTourPlan)
            ?? throw new InvalidDataException("The tour plan was empty.");
        result.Validate();
        return result;
    }

    private static void WriteProperties(Utf8JsonWriter writer, JsonElement value)
    {
        foreach (JsonProperty property in value.EnumerateObject()) property.WriteTo(writer);
    }
}

// The core takes false, true, the text, or {text, image, corner, size, opacity}.
internal sealed class ParallaxWatermarkConverter : JsonConverter<ParallaxWatermark>
{
    private const string DefaultText = "Rendered with seiza.fyi";

    public override ParallaxWatermark Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return new(Text(ref reader));
        var watermark = ParallaxWatermark.Disabled;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();
            watermark = name switch
            {
                "text" => watermark with { Text = Text(ref reader) },
                "image" => watermark with { Image = reader.TokenType == JsonTokenType.Null ? null : reader.GetString() },
                "corner" => watermark with { Corner = reader.GetString() ?? ParallaxWatermark.DefaultCorner },
                "size" => watermark with { Size = reader.GetDouble() },
                "opacity" => watermark with { Opacity = reader.GetDouble() },
                _ => throw new JsonException($"A watermark has no {name} field."),
            };
        }
        return watermark;
    }

    private static string? Text(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.False or JsonTokenType.Null => null,
        JsonTokenType.True => DefaultText,
        JsonTokenType.String => reader.GetString(),
        _ => throw new JsonException("A watermark must be false, text or an object."),
    };

    public override void Write(Utf8JsonWriter writer, ParallaxWatermark value, JsonSerializerOptions options)
    {
        if (value.Plain)
        {
            if (value.Text is null) writer.WriteBooleanValue(false);
            else writer.WriteStringValue(value.Text);
            return;
        }
        writer.WriteStartObject();
        if (value.Text is not null) writer.WriteString("text", value.Text);
        if (value.Image is not null) writer.WriteString("image", value.Image);
        writer.WriteString("corner", value.Corner);
        writer.WriteNumber("size", value.Size);
        writer.WriteNumber("opacity", value.Opacity);
        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ParallaxInputs))]
[JsonSerializable(typeof(ParallaxSceneSettings))]
[JsonSerializable(typeof(ParallaxVideoSettings))]
[JsonSerializable(typeof(ParallaxTourOptions))]
[JsonSerializable(typeof(ParallaxTourPlan))]
[JsonSerializable(typeof(ParallaxComposition))]
[JsonSerializable(typeof(ParallaxSummary))]
[JsonSerializable(typeof(ParallaxEvent))]
internal sealed partial class ParallaxJsonContext : JsonSerializerContext;
