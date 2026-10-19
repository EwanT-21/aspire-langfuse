using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Langfuse.Agent;
using Microsoft.Extensions.AI;
using Xunit;

namespace Aspire.Langfuse.Tests;

public sealed class PromptIterationTests
{
    [Fact]
    public async Task StartupSeedsMissingPromptAndConfiguresOnlyTheExampleProvider()
    {
        using var api = new PromptApi();
        using var http = new HttpClient(api) { BaseAddress = new Uri("http://langfuse/") };
        var client = new LangfusePromptClient(http, "my-prompt", "latest");

        await client.InitializeAsync("qwen3.5:2b", true, new Uri("http://ollama:11434"), default);

        Assert.Equal(1, api.CreateCount);
        Assert.Equal("chat", api.CreatedPrompt.GetProperty("type").GetString());
        Assert.Equal("{{message}}", api.CreatedPrompt.GetProperty("prompt")[1].GetProperty("content").GetString());
        Assert.Equal("production", api.CreatedPrompt.GetProperty("labels")[0].GetString());
        Assert.Equal("aspire-ollama", api.Connection.GetProperty("provider").GetString());
        Assert.Equal("openai", api.Connection.GetProperty("adapter").GetString());
        Assert.Equal("http://ollama:11434/v1", api.Connection.GetProperty("baseURL").GetString());
        Assert.False(api.Connection.GetProperty("withDefaultModels").GetBoolean());
        Assert.False(api.Connection.GetProperty("config").GetProperty("useResponsesApi").GetBoolean());
        Assert.Equal("http://langfuse/project/project-id/prompts/prompt-detail?promptName=my-prompt&label=latest", client.PromptsUrl);
    }

    [Fact]
    public async Task RestartPreservesEditsAndEachFetchReadsTheSelectedLabelAgain()
    {
        using var api = new PromptApi { Prompt = Prompt("edited system instructions", 3) };
        using var http = new HttpClient(api) { BaseAddress = new Uri("http://langfuse/") };
        var client = new LangfusePromptClient(http, "folder/my-prompt", "development");

        await client.InitializeAsync("qwen3.5:2b", true, null, default);
        var first = await client.GetAsync(default);
        api.Prompt = Prompt("new instructions saved in the UI", 4);
        var second = await client.GetAsync(default);

        Assert.Equal(0, api.CreateCount);
        Assert.Equal(3, first.Version);
        Assert.Equal(4, second.Version);
        Assert.Equal("new instructions saved in the UI", second.Compile("hello")[0].Text);
        Assert.EndsWith("folder%2Fmy-prompt?label=development", api.LastPromptUrl);
    }

    [Fact]
    public async Task ExistingInstanceIsReadOnlyUnlessInitializationIsEnabled()
    {
        using var api = new PromptApi { Prompt = Prompt("already configured", 7) };
        using var http = new HttpClient(api) { BaseAddress = new Uri("http://langfuse/") };
        var client = new LangfusePromptClient(http, "my-prompt", "production");

        await client.InitializeAsync("qwen3.5:2b", false, new Uri("http://ollama:11434"), default);

        Assert.Equal(0, api.CreateCount);
        Assert.Equal(JsonValueKind.Undefined, api.Connection.ValueKind);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotCreateOrOverwriteAPrompt()
    {
        using var api = new PromptApi { PromptStatus = HttpStatusCode.Unauthorized };
        using var http = new HttpClient(api) { BaseAddress = new Uri("http://langfuse/") };
        var client = new LangfusePromptClient(http, "my-prompt", "latest");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.InitializeAsync("model", true, null, default));
        Assert.Equal(0, api.CreateCount);
    }

    [Fact]
    public void CompilationSubstitutesUserInputWithoutInterpretingItsContents()
    {
        var prompt = Parse(Prompt("Be concise.", 2));
        var messages = prompt.Compile("Keep {{literal}} intact.");

        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("Be concise.", messages[0].Text);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Equal("Keep {{literal}} intact.", messages[1].Text);
        Assert.Equal(0.2f, prompt.Temperature);
        Assert.Equal(256, prompt.MaxOutputTokens);
        Assert.Equal("test-model", prompt.Model("fallback"));
    }

    [Fact]
    public void UnsupportedPromptVariablesAndInvalidSettingsFailVisibly()
    {
        var unknownVariable = Parse(Prompt("Use {{customer}}.", 1));
        Assert.Throws<InvalidOperationException>(() => unknownVariable.Compile("hello"));
        var invalid = Parse(Prompt("Be concise.", 1) with { Config = new { temperature = 10, max_tokens = 2.5 } });
        Assert.Throws<InvalidOperationException>(() => invalid.Temperature);
        Assert.Throws<InvalidOperationException>(() => invalid.MaxOutputTokens);
    }

    [Fact]
    public async Task ConcurrentRequestsLinkGenerationsToTheirOwnPromptVersion()
    {
        using var processor = new LangfuseObservationProcessor();
        async Task<Activity> Generate(int version)
        {
            using var scope = LangfuseObservationProcessor.UsePrompt(Parse(Prompt("instructions", version)));
            await Task.Yield();
            var activity = new Activity("generation").SetTag("gen_ai.operation.name", "chat");
            processor.OnEnd(activity);
            return activity;
        }

        var activities = await Task.WhenAll(Generate(1), Generate(2));
        Assert.Equal(1, activities[0].GetTagItem("langfuse.prompt.version"));
        Assert.Equal(2, activities[1].GetTagItem("langfuse.prompt.version"));
        Assert.All(activities, activity => Assert.Equal("generation", activity.GetTagItem("langfuse.observation.type")));
        var unrelated = new Activity("generation").SetTag("gen_ai.operation.name", "chat");
        processor.OnEnd(unrelated);
        Assert.Null(unrelated.GetTagItem("langfuse.prompt.version"));
    }

    private static ManagedPrompt Parse(PromptResponse response) => new(response.Name, response.Version,
        JsonSerializer.SerializeToElement(response.Prompt, JsonSerializerOptions.Web),
        JsonSerializer.SerializeToElement(response.Config, JsonSerializerOptions.Web));

    private static PromptResponse Prompt(string instructions, int version) => new("my-prompt", version,
        [new("system", instructions), new("user", "{{message}}")],
        new { model = "test-model", temperature = 0.2, max_tokens = 256 });

    private sealed record PromptMessage(string Role, string Content);
    private sealed record PromptResponse(string Name, int Version, PromptMessage[] Prompt, object Config);

    private sealed class PromptApi : HttpMessageHandler
    {
        public PromptResponse? Prompt { get; set; }
        public HttpStatusCode PromptStatus { get; set; } = HttpStatusCode.OK;
        public int CreateCount { get; private set; }
        public JsonElement CreatedPrompt { get; private set; }
        public JsonElement Connection { get; private set; }
        public string? LastPromptUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/public/projects")
            {
                return Json(new { data = new[] { new { id = "project-id" } } });
            }
            if (request.Method == HttpMethod.Put)
            {
                Connection = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                return Json(new { });
            }
            if (request.Method == HttpMethod.Post)
            {
                CreateCount++;
                CreatedPrompt = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                Prompt = PromptIterationTests.Prompt(LangfusePromptClient.DefaultInstructions, 1);
                return Json(Prompt);
            }
            LastPromptUrl = request.RequestUri.AbsoluteUri;
            return PromptStatus != HttpStatusCode.OK ? new(PromptStatus)
                : Prompt is null ? new(HttpStatusCode.NotFound) : Json(Prompt);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
    }
}
