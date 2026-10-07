using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace StoryForge.Engine.Research;

/// <summary>
/// What the model hands back: the facts without ids, weights or left-out marks (the engine numbers
/// them, and weights and leaving out are yours). The JSON schema Claude CLI enforces is generated
/// from these records.
/// </summary>
internal sealed record ResearchOutput(
    [property: Description("The facts, one claim each.")] IReadOnlyList<ResearchFact> Facts);

internal sealed record ResearchFact(
    [property: Description("The fact in one or two short plain sentences, in the language the prompt names.")] string Statement,
    [property: Description("The URL of the fetched page the fact comes from, as the fetch answer gave it after Page.")] string SourceUrl,
    [property: Description("The passage on that page the fact rests on, copied word for word.")] string Quote);

internal static class ResearchSchema
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>The schema as one line of JSON, for --json-schema.</summary>
    public static string Json { get; } = Build();

    public static ResearchOutput? Read(JsonElement element) => element.Deserialize<ResearchOutput>(Options);

    private static string Build()
    {
        var schema = Options.GetJsonSchemaAsNode(typeof(ResearchOutput), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                var description = context.PropertyInfo?.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
                    .OfType<DescriptionAttribute>().FirstOrDefault()?.Description;
                if (description is not null && node is JsonObject property)
                {
                    property.Insert(0, "description", description);
                }
                return node;
            },
        });
        return schema.ToJsonString();
    }
}
