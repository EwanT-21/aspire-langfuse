using System.Data.Common;
using System.Diagnostics;
using System.Text;
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
using var langfuseHttp = new HttpClient { BaseAddress = langfuseUrl, Timeout = TimeSpan.FromSeconds(30) };

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", model = modelName }));
app.MapPost("/run", async (AgentRequest request, CancellationToken cancellationToken) =>
{
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
                Instructions = "You are an operations assistant. Always call get_langfuse_health before answering a health question. Use the tool result to answer briefly. Do not invent service status.",
                Tools = [AIFunctionFactory.Create(GetLangfuseHealth, "get_langfuse_health", "Check the live Langfuse instance and return its health status.")],
                Temperature = 0,
                MaxOutputTokens = 512,
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

        var result = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
        answer = result.Text;
        run.SetTag("langfuse.observation.output", answer);
        run.SetTag("agent.tool_calls", toolCalls);
    }

    if (!telemetry.ForceFlush(30_000))
    {
        throw new InvalidOperationException("Timed out exporting agent telemetry to Langfuse.");
    }

    return Results.Ok(new { answer, traceId, model = modelName, toolCalls });
});
app.Run();

internal sealed record AgentRequest(string? Prompt);

internal sealed class LangfuseObservationProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        var type = activity.GetTagItem("gen_ai.operation.name")?.ToString() switch
        {
            "invoke_agent" => "agent",
            "chat" or "text_completion" => "generation",
            "execute_tool" => "tool",
            _ => null
        };
        if (type is not null)
        {
            activity.SetTag("langfuse.observation.type", type);
        }
    }
}
