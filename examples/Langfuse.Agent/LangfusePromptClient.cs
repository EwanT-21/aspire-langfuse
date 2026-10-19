using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Langfuse.Agent;

/// <summary>The example's small client for Langfuse's public prompt and LLM connection APIs.</summary>
public sealed class LangfusePromptClient(HttpClient http, string promptName, string label)
{
    public const string DefaultName = "aspire-health-assistant";
    public const string PlaygroundProvider = "aspire-ollama";
    public const string DefaultInstructions = "You are an operations assistant. Always call get_langfuse_health before answering a health question. Use the tool result to answer briefly. Do not invent service status.";

    public string? ProjectId { get; private set; }

    public async Task InitializeAsync(string model, bool createMissing, Uri? playgroundBaseUrl,
        CancellationToken cancellationToken)
    {
        using var projects = await http.GetAsync("api/public/projects", cancellationToken);
        projects.EnsureSuccessStatusCode();
        var projectData = await projects.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        ProjectId = projectData.GetProperty("data").EnumerateArray().First().GetProperty("id").GetString();

        if (createMissing)
        {
            // Check latest independently of the selected deployment label. Restarting must not
            // replace an edited prompt or move a label that the user deliberately reassigned.
            using var existing = await http.GetAsync(PromptPath("latest"), cancellationToken);
            if (existing.StatusCode == HttpStatusCode.NotFound)
            {
                using var created = await http.PostAsJsonAsync("api/public/v2/prompts", new
                {
                    name = promptName,
                    type = "chat",
                    prompt = new[]
                    {
                        new { role = "system", content = DefaultInstructions },
                        new { role = "user", content = "{{message}}" }
                    },
                    config = new { provider = PlaygroundProvider, temperature = 0, max_tokens = 512 },
                    labels = label == "latest" ? ["production"] : new[] { label },
                    tags = new[] { "aspire-example" },
                    commitMessage = "Initial prompt for the Aspire example"
                }, cancellationToken);
                created.EnsureSuccessStatusCode();
            }
            else
            {
                existing.EnsureSuccessStatusCode();
            }

            if (playgroundBaseUrl is not null)
            {
                // This example owns only this named connection. Refresh its endpoint on restart
                // because Aspire can assign a different port; leave other providers alone.
                using var connection = await http.PutAsJsonAsync("api/public/llm-connections", new
                {
                    provider = PlaygroundProvider,
                    adapter = "openai",
                    secretKey = "ollama",
                    baseURL = playgroundBaseUrl.AbsoluteUri.TrimEnd('/') + "/v1",
                    customModels = new[] { model },
                    withDefaultModels = false,
                    config = new { useResponsesApi = false }
                }, cancellationToken);
                connection.EnsureSuccessStatusCode();
            }
        }

        // Fail startup visibly for a bad label or inaccessible project instead of silently
        // falling back to instructions that differ from those shown in Langfuse.
        _ = await GetAsync(cancellationToken);
    }

    public async Task<ManagedPrompt> GetAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(PromptPath(label), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Create the chat prompt '{promptName}' with label '{label}' in Langfuse, or enable Agent:InitializeLangfuse for the example project.");
        }
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var prompt = new ManagedPrompt(data.GetProperty("name").GetString()!, data.GetProperty("version").GetInt32(),
            data.GetProperty("prompt").Clone(), data.TryGetProperty("config", out var config) ? config.Clone() : default);
        // Validate before returning a healthy startup status, as well as on each request.
        _ = prompt.Compile("Is Langfuse healthy?");
        return prompt;
    }

    public string ProjectUrl => new Uri(http.BaseAddress!, $"project/{Uri.EscapeDataString(ProjectId!)}").AbsoluteUri;
    public string PromptsUrl => ProjectUrl + "/prompts/prompt-detail?promptName=" + Uri.EscapeDataString(promptName) +
        "&label=" + Uri.EscapeDataString(label);
    public string PlaygroundUrl => ProjectUrl + "/playground";
    public string TraceUrl(string traceId) => ProjectUrl + "/traces/" + Uri.EscapeDataString(traceId);

    private string PromptPath(string requestedLabel) =>
        $"api/public/v2/prompts/{Uri.EscapeDataString(promptName)}?label={Uri.EscapeDataString(requestedLabel)}";
}

public sealed record ManagedPrompt(string Name, int Version, JsonElement Messages, JsonElement Config)
{
    public IReadOnlyList<ChatMessage> Compile(string message)
    {
        if (Messages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"The Langfuse prompt '{Name}' must be a chat prompt with system and user messages.");
        }

        var messages = new List<ChatMessage>();
        foreach (var item in Messages.EnumerateArray())
        {
            var role = item.GetProperty("role").GetString();
            if (role is not ("system" or "user" or "assistant"))
            {
                throw new InvalidOperationException("The example supports system, user, and assistant prompt messages.");
            }

            var content = item.GetProperty("content").GetString() ?? "";
            content = Regex.Replace(content, @"\{\{\s*(\w+)\s*\}\}", match =>
                match.Groups[1].Value == "message" ? message
                    : throw new InvalidOperationException($"Unknown prompt variable '{match.Groups[1].Value}'. This example provides the 'message' variable."));
            messages.Add(new ChatMessage(new ChatRole(role), content));
        }
        if (!messages.Any(item => item.Role == ChatRole.User))
        {
            throw new InvalidOperationException("Add a user message containing {{message}} to the Langfuse prompt.");
        }
        return messages;
    }

    public string Model(string fallback) => Config.ValueKind == JsonValueKind.Object &&
        Config.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
            ? model.GetString()! : fallback;

    public float Temperature => Number("temperature", 0, 0, 2);
    public int MaxOutputTokens => (int)Number("max_tokens", 512, 1, 8192);

    private float Number(string key, float fallback, float min, float max)
    {
        if (Config.ValueKind != JsonValueKind.Object || !Config.TryGetProperty(key, out var value))
        {
            return fallback;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number) || !float.IsFinite(number) || number < min || number > max ||
            key == "max_tokens" && number != MathF.Truncate(number))
        {
            throw new InvalidOperationException($"Prompt config '{key}' must be a number between {min} and {max}.");
        }
        return number;
    }
}
