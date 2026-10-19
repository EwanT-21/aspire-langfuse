using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Langfuse.Agent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

const string ActivitySourceName = "Langfuse.OllamaAgent";
const string DefaultPrompt = "Use get_langfuse_health to check the Langfuse instance, then briefly tell me whether it is healthy.";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
var connection = new DbConnectionStringBuilder
{
    ConnectionString = builder.Configuration.GetConnectionString("chat")
        ?? throw new InvalidOperationException("Reference the Aspire Ollama model named 'chat'.")
};
var ollamaUrl = new Uri(connection["Endpoint"].ToString()!);
var modelName = connection["Model"].ToString()!;
var langfuseUrl = new Uri(builder.Configuration["LANGFUSE_BASE_URL"]
    ?? throw new InvalidOperationException("Reference a Langfuse resource from the AppHost."));
var publicKey = builder.Configuration["LANGFUSE_PUBLIC_KEY"]
    ?? throw new InvalidOperationException("Configure Langfuse project API keys in the AppHost.");
var secretKey = builder.Configuration["LANGFUSE_SECRET_KEY"]
    ?? throw new InvalidOperationException("Configure Langfuse project API keys in the AppHost.");

using var activities = new ActivitySource(ActivitySourceName);
using var telemetry = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("langfuse-ollama-agent"))
    .SetSampler(new AlwaysOnSampler())
    .AddSource(ActivitySourceName)
    .AddProcessor(new LangfuseObservationProcessor())
    .AddOtlpExporter(options =>
    {
        options.Endpoint = new Uri($"{langfuseUrl.AbsoluteUri.TrimEnd('/')}/api/public/otel/v1/traces");
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Headers = $"Authorization=Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}"))},x-langfuse-ingestion-version=4";
    })
    .Build();

using var ollamaHttp = new HttpClient { BaseAddress = ollamaUrl, Timeout = TimeSpan.FromMinutes(5) };
using IChatClient modelClient = ((IChatClient)new OllamaApiClient(ollamaHttp, modelName))
    .AsBuilder()
    .UseOpenTelemetry(sourceName: ActivitySourceName, configure: options => options.EnableSensitiveData = true)
    .Build();
using var langfuseHttp = new HttpClient
{
    BaseAddress = new Uri(langfuseUrl.AbsoluteUri.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(30)
};
langfuseHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}")));
var promptName = builder.Configuration["Langfuse:PromptName"] ?? LangfusePromptClient.DefaultName;
var promptLabel = builder.Configuration["Langfuse:PromptLabel"] ?? "latest";
var prompts = new LangfusePromptClient(langfuseHttp, promptName, promptLabel);

var app = builder.Build();
using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
{
    var playgroundUrl = builder.Configuration["Langfuse:PlaygroundBaseUrl"];
    await prompts.InitializeAsync(modelName,
        builder.Configuration.GetValue("Langfuse:Initialize", false),
        string.IsNullOrWhiteSpace(playgroundUrl) ? null : new Uri(playgroundUrl), startup.Token);
}
app.Logger.LogInformation("Using Langfuse prompt {PromptName} ({PromptLabel}). Edit it at {PromptsUrl}",
    promptName, promptLabel, prompts.PromptsUrl);
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy", model = modelName, promptName, promptLabel,
    promptsUrl = prompts.PromptsUrl, playgroundUrl = prompts.PlaygroundUrl
}));
app.MapPost("/run", async (AgentRequest request, CancellationToken cancellationToken) =>
{
    ManagedPrompt managedPrompt;
    IReadOnlyList<ChatMessage> messages;
    string selectedModel;
    float temperature;
    int maxOutputTokens;
    try
    {
        // Fetch every time so saving a new latest version in Langfuse affects the next run.
        managedPrompt = await prompts.GetAsync(cancellationToken);
        messages = managedPrompt.Compile(string.IsNullOrWhiteSpace(request.Prompt) ? DefaultPrompt : request.Prompt);
        selectedModel = managedPrompt.Model(modelName);
        temperature = managedPrompt.Temperature;
        maxOutputTokens = managedPrompt.MaxOutputTokens;
    }
    catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
    {
        app.Logger.LogError(error, "Could not load the managed prompt {PromptName}", promptName);
        return Results.Problem(detail: "Could not load the prompt. Check its messages, config, and selected label in Langfuse.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    using var promptScope = LangfuseObservationProcessor.UsePrompt(managedPrompt);
    var toolCalls = 0;
    var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? DefaultPrompt : request.Prompt;
    string answer;
    string traceId;
    using (var run = activities.StartActivity("ollama-agent-run"))
    {
        if (run is null)
        {
            throw new InvalidOperationException("Agent tracing is not configured.");
        }

        traceId = run.TraceId.ToHexString();
        run.SetTag("langfuse.observation.type", "chain");
        run.SetTag("langfuse.trace.name", "ollama-langfuse-health-check");
        run.SetTag("langfuse.observation.input", prompt);
        run.SetTag("langfuse.observation.metadata.prompt_name", managedPrompt.Name);
        run.SetTag("langfuse.observation.metadata.prompt_version", managedPrompt.Version);

        async Task<string> GetLangfuseHealth(CancellationToken token)
        {
            Interlocked.Increment(ref toolCalls);
            using var tool = activities.StartActivity("get_langfuse_health");
            tool?.SetTag("langfuse.observation.type", "tool");
            tool?.SetTag("gen_ai.operation.name", "execute_tool");
            using var response = await langfuseHttp.GetAsync("/api/public/health", token);
            response.EnsureSuccessStatusCode();
            var health = await response.Content.ReadAsStringAsync(token);
            tool?.SetTag("langfuse.observation.output", health);
            return health;
        }

        var agent = new ChatClientAgent(modelClient, new ChatClientAgentOptions
        {
            Name = "LangfuseHealthAgent",
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(GetLangfuseHealth, "get_langfuse_health", "Check the live Langfuse instance and return its health status.")],
                ModelId = selectedModel,
                Temperature = temperature,
                MaxOutputTokens = maxOutputTokens,
                RawRepresentationFactory = _ => new ChatRequest
                {
                    Think = false,
                    Options = new RequestOptions { NumCtx = 2048 }
                }
            }
        })
            .AsBuilder()
            .UseOpenTelemetry(ActivitySourceName, configure: options => options.EnableSensitiveData = true)
            .Build();

        var result = await agent.RunAsync(messages, cancellationToken: cancellationToken);
        answer = result.Text;
        run.SetTag("langfuse.observation.output", answer);
        run.SetTag("agent.tool_calls", toolCalls);
    }

    if (!telemetry.ForceFlush(30_000))
    {
        throw new InvalidOperationException("Timed out exporting agent telemetry to Langfuse.");
    }

    return Results.Ok(new
    {
        answer, traceId, model = selectedModel, toolCalls,
        prompt = new { name = managedPrompt.Name, version = managedPrompt.Version, label = promptLabel },
        traceUrl = prompts.TraceUrl(traceId), promptsUrl = prompts.PromptsUrl
    });
});
await app.RunAsync();

internal sealed record AgentRequest(string? Prompt);
