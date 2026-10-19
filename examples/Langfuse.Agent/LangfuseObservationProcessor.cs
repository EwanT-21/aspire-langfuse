using System.Diagnostics;
using OpenTelemetry;

namespace Langfuse.Agent;

public sealed class LangfuseObservationProcessor : BaseProcessor<Activity>
{
    // Each asynchronous request keeps its own prompt, including overlapping agent calls.
    private static readonly AsyncLocal<ManagedPrompt?> ActivePrompt = new();

    public static IDisposable UsePrompt(ManagedPrompt prompt) => new PromptScope(prompt);

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
        if (type == "generation" && ActivePrompt.Value is { } prompt)
        {
            activity.SetTag("langfuse.prompt.name", prompt.Name);
            activity.SetTag("langfuse.prompt.version", prompt.Version);
        }
    }

    private sealed class PromptScope : IDisposable
    {
        private readonly ManagedPrompt? previous = ActivePrompt.Value;

        public PromptScope(ManagedPrompt prompt) => ActivePrompt.Value = prompt;
        public void Dispose() => ActivePrompt.Value = previous;
    }
}
