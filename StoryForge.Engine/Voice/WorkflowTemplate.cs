using System.Text.Json;
using System.Text.Json.Nodes;
using StoryForge.Client;
using StoryForge.Engine.Pipeline;

namespace StoryForge.Engine.Voice;

/// <summary>
/// A profile's workflow file: API format, as ComfyUI's Export (API) saves it. A profile's inputs say
/// where each value goes, e.g. "text" → "#11.text" (node 11, its input "text").
/// </summary>
internal static class WorkflowTemplate
{
    /// <exception cref="StageFailedException">No templates folder or file, not JSON, or not the API format.</exception>
    public static async Task<JsonObject> LoadAsync(string folder, string file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            throw new StageFailedException("The voice profile has no workflow. Pick one in Profiles → Voice.");
        }
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new StageFailedException("No ComfyUI templates folder is set. Set it in Settings → ComfyUI.");
        }
        var path = Path.Combine(folder, file);
        if (!File.Exists(path))
        {
            throw new StageFailedException($"The workflow {file} is not in the templates folder ({folder}).");
        }
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (JsonException ex)
        {
            throw new StageFailedException($"The workflow {file} is not valid JSON: {ex.Message}");
        }
        if (json is JsonObject ui && ui["nodes"] is JsonArray && ui["links"] is not null)
        {
            throw new StageFailedException(
                $"The workflow {file} is saved in ComfyUI's editor format. Open it in ComfyUI, use Workflow → Export (API), and pick that file in the profile.");
        }
        if (json is not JsonObject api || api.Count == 0 || api.Any(n => n.Value is not JsonObject node || node["class_type"] is null))
        {
            throw new StageFailedException($"The workflow {file} is not an API-format ComfyUI workflow.");
        }
        return api;
    }

    /// <summary>
    /// A copy of the workflow with each value in the node its key maps to. A key the profile does not
    /// map (or maps to nothing) is left out: the workflow keeps its own value there.
    /// </summary>
    /// <exception cref="StageFailedException">A mapping names a node the workflow lacks, or is not "#node.input".</exception>
    public static JsonObject Apply(JsonObject workflow, IReadOnlyList<WorkflowInput> inputs, IReadOnlyDictionary<string, JsonNode> values)
    {
        var copy = (JsonObject)workflow.DeepClone();
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Node) || !values.TryGetValue(input.Key.Trim(), out var value))
            {
                continue;
            }
            var (node, name) = Parse(input);
            if (copy[node] is not JsonObject target)
            {
                throw new StageFailedException($"The profile puts '{input.Key}' into node {node}, and the workflow has no node {node}.");
            }
            if (target["inputs"] is not JsonObject fields)
            {
                target["inputs"] = fields = [];
            }
            if (fields[name] is JsonArray { Count: > 0 } wire && value is not JsonArray)
            {
                throw new StageFailedException(Wired(copy, input, node, name, wire[0]?.ToString() ?? "?"));
            }
            fields[name] = value.DeepClone();
        }
        return copy;
    }

    /// <summary>Whether the profile maps <paramref name="key"/> to a node.</summary>
    public static bool Maps(IReadOnlyList<WorkflowInput> inputs, string key) =>
        inputs.Any(i => i.Key.Trim() == key && !string.IsNullOrWhiteSpace(i.Node));

    /// <summary>"…which the workflow wires from node 2 (LoadAudio). Map it to an input of that node instead, e.g. #2.audio."</summary>
    private static string Wired(JsonObject workflow, WorkflowInput input, string node, string name, string from)
    {
        var source = workflow[from] as JsonObject;
        var type = source?["class_type"]?.GetValue<string>();
        var example = (source?["inputs"] as JsonObject)?.FirstOrDefault(i => i.Value is not JsonArray).Key;
        return $"The profile puts '{input.Key}' into #{node}.{name}, which the workflow wires from node {from}{(type is null ? "" : $" ({type})")}. "
            + $"Map it to an input of that node instead{(example is null ? "" : $", e.g. #{from}.{example}")}.";
    }

    private static (string Node, string Input) Parse(WorkflowInput input)
    {
        var spec = input.Node.Trim().TrimStart('#');
        var dot = spec.LastIndexOf('.');
        if (dot <= 0 || dot == spec.Length - 1)
        {
            throw new StageFailedException($"The profile puts '{input.Key}' into '{input.Node}'; write it as #node.input, e.g. #11.text.");
        }
        return (spec[..dot], spec[(dot + 1)..]);
    }
}
