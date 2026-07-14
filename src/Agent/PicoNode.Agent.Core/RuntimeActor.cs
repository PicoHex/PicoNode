using PicoNode.AI;
using PicoNode.AI.Types;
using System.Threading.Channels;
using ActorBase = PicoNode.Actor.Abs.Actor;

namespace PicoNode.Agent.Domain;

public sealed class RuntimeActor : ActorBase
{
    private AgentConfigSnapshot? _loadedConfig;

    public ILlmClient? LlmClient { get; set; }
    public IToolRunner? ToolRunner { get; set; }
    public IActorSystem? SessionSystem { get; set; }

    public RuntimeActor() { }

    protected override ValueTask<object?> OnMessageAsync(ICommand command)
    {
        return command switch
        {
            LoadAgentCmd c => HandleLoadAgent(c),
            GetLoadedConfigQuery => new ValueTask<object?>(_loadedConfig),
            RunTurnCmd c => HandleRunTurn(c),
            _ => throw new DomainInvariantException($"Unknown: {command.GetType().Name}")
        };
    }

    private async ValueTask<object?> HandleLoadAgent(LoadAgentCmd c)
    {
        if (System is null)
            throw new InvalidOperationException("Runtime requires an ActorSystem");

        _loadedConfig = await System.AskAsync<AgentConfigSnapshot>(
            c.AgentId, new GetConfigQuery());
        return default;
    }

    private async ValueTask<object?> HandleRunTurn(RunTurnCmd c)
    {
        if (_loadedConfig is null || LlmClient is null || ToolRunner is null || System is null || SessionSystem is null)
            throw new InvalidOperationException("Runtime not fully wired");

        var turnId = Guid.CreateVersion7().ToString("N")[..8];
        var cts = new CancellationTokenSource();
        var tools = _loadedConfig.Tools;
        var ctx = new List<Message>(c.Context.Messages);

        try
        {
            var userMsg = new Message
            {
                Role = "user",
                Content = c.Message,
                ContentBlocks = [new ContentBlock { Type = "text", Text = c.Message }],
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            SessionSystem.Send(c.SessionId, new AppendMessage(new MessageEntry { Message = userMsg }));

            for (var iteration = 0; iteration < 100; iteration++)
            {
                var response = await StreamSingleTurnAsync(turnId, tools, ctx, cts.Token);

                var toolCalls = (response.ContentBlocks ?? [])
                    .Where(cb => cb.Type == "tool_call")
                    .ToArray();

                SessionSystem.Send(c.SessionId, new AppendMessage(new MessageEntry { Message = response }));
                ctx.Add(response);

                if (response.StopReason == "error")
                {
                    WriteOutput("error", response.ErrorMessage, null, null, turnId);
                    break;
                }

                if (toolCalls.Length == 0)
                    break;

                var toolResults = await ExecuteToolsAsync(toolCalls, c.SessionId, cts.Token);
                ctx.AddRange(toolResults);
            }

            WriteOutput("done", null, null, null, turnId);
        }
        catch (OperationCanceledException) when (!StopToken.IsCancellationRequested)
        {
            WriteOutput("cancelled", null, null, null, turnId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteOutput("error", $"Turn failed: {ex.Message}", null, null, turnId);
        }
        finally
        {
            cts.Dispose();
        }

        return default;
    }

    private async Task<Message> StreamSingleTurnAsync(
        string turnLabel, List<Tool> tools, List<Message> ctx, CancellationToken ct)
    {
        if (_loadedConfig is null || LlmClient is null)
            throw new InvalidOperationException("Runtime not configured");

        var prompt = SystemPromptBuilder.Build(tools.ToArray(), _loadedConfig.Skills);
        ctx.Insert(0, new Message { Role = "system", Content = prompt });

        var assembler = new TurnResponseAssembler(turnLabel);
        var currentLlm = _loadedConfig.Llms[0];

        await foreach (var evt in LlmClient.StreamAsync(currentLlm, ctx, tools, ct))
        {
            WriteOutput(evt.Type, evt.Content, evt.ToolCallId, evt.ToolName, turnLabel);
            if (evt.Type == "done")
                break;
            if (evt.Type == "error")
                return ErrorAssistantMessage(evt.Content ?? "LLM error");
            assembler.Handle(evt);
        }

        return assembler.Build();
    }

    private static Message ErrorAssistantMessage(string errorMessage) =>
        new()
        {
            Role = "assistant",
            StopReason = "error",
            ErrorMessage = errorMessage,
            ContentBlocks = [],
        };

    private async Task<Message[]> ExecuteToolsAsync(
        ContentBlock[] toolCalls, Guid sessionId, CancellationToken ct)
    {
        if (ToolRunner is null)
            throw new InvalidOperationException("ToolRunner not wired");

        var tasks = toolCalls.Select(tc => ExecuteOneToolAsync(tc, ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var msg in results)
        {
            SessionSystem!.Send(sessionId, new AppendMessage(new MessageEntry { Message = msg }));
        }
        return results;
    }

    private async Task<Message> ExecuteOneToolAsync(ContentBlock tc, CancellationToken ct)
    {
        string toolResult;
        try
        {
            toolResult = await ToolRunner!.ExecuteAsync(tc.Name ?? "", tc.Arguments, ct);
        }
        catch (Exception ex)
        {
            toolResult = $"[ToolError: {ex.GetType().Name}] {ex.Message}";
        }
        WriteOutput("tool_result", toolResult, tc.Id, tc.Name, null);
        return new Message
        {
            Role = "toolResult",
            ToolCallId = tc.Id,
            ToolName = tc.Name,
            ContentBlocks = [new ContentBlock { Type = "text", Text = toolResult }],
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }
}
