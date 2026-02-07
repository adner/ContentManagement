using AgentFrameworkToolkit;
using AgentFrameworkToolkit.OpenAI;
using AgentFrameworkToolkit.Tools;
using Microsoft.Agents.AI;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.ComponentModel;
using System.Reflection.Metadata.Ecma335;
using System.ServiceModel.Channels;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();

var apiKey = configuration["OpenAI:ApiKey"]
    ?? throw new InvalidOperationException("OpenAI:ApiKey not found in appsettings.json");

OpenAIAgentFactory agentFactory = new(apiKey);

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

var mcpSection = configuration.GetSection("McpServer");
await using var mcpClient = await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Name = mcpSection["Name"] ?? throw new InvalidOperationException("McpServer:Name not found in appsettings.json"),
    Command = mcpSection["Command"] ?? throw new InvalidOperationException("McpServer:Command not found in appsettings.json"),
    Arguments = mcpSection.GetSection("Arguments").Get<string[]>() ?? [],
}));

IList<McpClientTool> tools = await mcpClient.ListToolsAsync();

const string agentInstructions = "You are an agent that helps the user retrieve information about my space program from Dataverse. An 'astronaut' is a contact in Dataverse. If you have called the tools 'list_tables' or 'describe_table' once and have received the full result, you don't have to call them again since the result will always be identical. ";

// --- Mode selection ---
bool demoMode = args.Contains("--demo");
bool verbose = args.Contains("--verbose");

// Collect --reducer arguments (e.g. --reducer dummy --reducer contentaware)
var requestedReducers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--reducer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        requestedReducers.Add(args[++i]);
    }
}

if (demoMode)
{
    await RunDemoMode();
}
else
{
    await RunInteractiveMode();
}

// =====================================================================
// Interactive mode (original behavior)
// =====================================================================

async Task RunInteractiveMode()
{
    var reducer = new DummyReducer();

    OpenAIAgent agent = agentFactory.CreateAgent(new AgentOptions
    {
        Instructions = agentInstructions,
        Model = OpenAIChatModels.Gpt52,
        ReasoningEffort = OpenAIReasoningEffort.None,
        Tools = [.. tools],
        ClientFactory = client => new MessageLoggingChatClient(client),
        ChatHistoryProviderFactory = (context, token) => ValueTask.FromResult<ChatHistoryProvider>(new InMemoryChatHistoryProvider(reducer, context.SerializedState, context.JsonSerializerOptions))
    });

    AgentSession session = await agent.CreateSessionAsync();

    while (true)
    {
        Console.Write("> ");
        string input = Console.ReadLine() ?? string.Empty;
        AgentResponse response = await agent.RunAsync(input, session);
        Console.WriteLine(response);

        response.Usage.OutputAsInformation();
        IList<ChatMessage> messagesInSession = session.GetService<IList<ChatMessage>>()!;
        Console.WriteLine("- Number of messages in session: " + messagesInSession.Count());
        Console.WriteLine("---");
    }
}

// =====================================================================
// Demo mode — run scripted questions against all reducers
// =====================================================================

async Task RunDemoMode()
{
    string[] scriptedQuestions =
    [
        "List all the astronauts in the system and their specialization!",
        "What rockets are available?",
        "Which astronaut is leading the most missions?",
        "What is the specialization of Astrid Lindqvist?",
        "Earlier you listed the astronauts. Can you recall who the first one was?",
        "What was the specialization of Alan?",
    ];

    var allReducerConfigs = new (string Name, string[] Aliases, string Description, IChatReducer Reducer)[]
    {
        ("DummyReducer",
         ["dummy"],
         "No reduction - all messages retained",
         new DummyReducer()),

        ("MyMessageCountingChatReducer",
         ["counting", "messagecounting"],
         "Keeps system + last 6 non-system messages, drops ALL tool messages",
         new MyMessageCountingChatReducer(5, loggerFactory.CreateLogger<MyMessageCountingChatReducer>())),

        ("ToolPreservingChatReducer",
         ["toolpreserving", "tool"],
         "Keeps last 6 messages with safe-cut logic for tool pairs",
         new ToolPreservingChatReducer(10, loggerFactory.CreateLogger<ToolPreservingChatReducer>())),

        ("ContentAwareChatReducer",
         ["contentaware", "content"],
         "Condenses historical data results, preserves schema tools, max 20 msgs",
         new ContentAwareChatReducer(maxMessages: 20, recentMessageCount: 5, logger: loggerFactory.CreateLogger<ContentAwareChatReducer>()))
    };

    // Filter by --reducer arguments if any were provided
    var reducerConfigs = requestedReducers.Count > 0
        ? allReducerConfigs.Where(r =>
            requestedReducers.Contains(r.Name) ||
            r.Aliases.Any(a => requestedReducers.Contains(a))).ToArray()
        : allReducerConfigs;

    if (reducerConfigs.Length == 0)
    {
        Console.WriteLine("No matching reducers found. Available names/aliases:");
        foreach (var r in allReducerConfigs)
            Console.WriteLine($"  {r.Name} (aliases: {string.Join(", ", r.Aliases)})");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("================================================================");
    Console.WriteLine("  CONTEXT REDUCER COMPARISON DEMO");
    Console.WriteLine($"  Running {scriptedQuestions.Length} scripted questions against {reducerConfigs.Length} reducers");
    if (verbose) Console.WriteLine("  Verbose mode: full message logging enabled for all turns");
    Console.WriteLine("================================================================");
    Console.WriteLine();

    var results = new List<DemoRunResult>();

    for (int i = 0; i < reducerConfigs.Length; i++)
    {
        var (name, _, description, reducer) = reducerConfigs[i];
        PrintReducerHeader(i + 1, reducerConfigs.Length, name, description);

        var result = await RunScriptWithReducer(reducer, name, description, scriptedQuestions);
        results.Add(result);

        if (i < reducerConfigs.Length - 1)
        {
            Console.WriteLine();
            Console.WriteLine("  Starting next reducer in 2 seconds...");
            await Task.Delay(2000);
            Console.WriteLine();
        }
    }

    PrintComparisonTable(results);
}

async Task<DemoRunResult> RunScriptWithReducer(
    IChatReducer reducer,
    string reducerName,
    string reducerDescription,
    string[] questions)
{
    MessageLoggingChatClient? loggingClient = null;

    OpenAIAgent agent = agentFactory.CreateAgent(new AgentOptions
    {
        Instructions = agentInstructions,
        Model = OpenAIChatModels.Gpt52,
        ReasoningEffort = OpenAIReasoningEffort.None,
        Tools = [.. tools],
        ClientFactory = client =>
        {
            loggingClient = new MessageLoggingChatClient(client) { Silent = false };
            return loggingClient;
        },
        ChatHistoryProviderFactory = (context, token) =>
            ValueTask.FromResult<ChatHistoryProvider>(
                new InMemoryChatHistoryProvider(reducer, context.SerializedState, context.JsonSerializerOptions))
    });

    AgentSession session = await agent.CreateSessionAsync();
    var turns = new List<TurnResult>();
    int cumulativeInput = 0;
    int cumulativeOutput = 0;

    for (int i = 0; i < questions.Length; i++)
    {
        int turnNumber = i + 1;

        Console.WriteLine();
        Console.WriteLine($"── Turn {turnNumber} ──────────────────────────────────────────────────");
        Console.WriteLine($"  Q: \"{questions[i]}\"");

        AgentResponse response = await agent.RunAsync(questions[i], session);

        int inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        int outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);
        cumulativeInput += inputTokens;
        cumulativeOutput += outputTokens;

        IList<ChatMessage> messagesInSession = session.GetService<IList<ChatMessage>>()!;
        int sentToLlm = loggingClient?.LastRequestMessageCount ?? 0;

        var turn = new TurnResult(
            turnNumber,
            questions[i],
            response.ToString() ?? "",
            inputTokens,
            outputTokens,
            messagesInSession.Count,
            sentToLlm
        );
        turns.Add(turn);
        PrintTurnSummary(turn, cumulativeInput, cumulativeOutput);
    }

    return new DemoRunResult(
        reducerName,
        reducerDescription,
        turns,
        cumulativeInput,
        cumulativeOutput,
        turns[^1].SessionMessageCount
    );
}

// =====================================================================
// Display helpers
// =====================================================================

void PrintReducerHeader(int index, int total, string name, string description)
{
    Console.WriteLine("================================================================");
    Console.WriteLine($"  REDUCER {index}/{total}: {name}");
    Console.WriteLine($"  {description}");
    Console.WriteLine("================================================================");
    Console.WriteLine();
}

void PrintTurnSummary(TurnResult turn, int cumulativeInput, int cumulativeOutput)
{
    string responsePreview = turn.Response.Length > 200
        ? turn.Response[..200] + "..."
        : turn.Response;
    responsePreview = responsePreview.Replace("\n", " ").Replace("\r", "");

    Console.WriteLine($"  A: \"{responsePreview}\"");
    Console.WriteLine();
    Console.WriteLine($"  Tokens:      Input: {turn.InputTokens,6:N0}  |  Output: {turn.OutputTokens,6:N0}");
    Console.WriteLine($"  Cumulative:  Input: {cumulativeInput,6:N0}  |  Output: {cumulativeOutput,6:N0}");
    Console.WriteLine($"  Session messages: {turn.SessionMessageCount}  |  Sent to LLM: {turn.SentToLlmCount}");
    Console.WriteLine("────────────────────────────────────────────────────────────────");
}

void PrintComparisonTable(List<DemoRunResult> results)
{
    Console.WriteLine();
    Console.WriteLine("========================== COMPARISON SUMMARY ==========================");
    Console.WriteLine($"{"Reducer",-32} | {"Total In",10} | {"Total Out",10} | {"Final Msgs",10} | {"Last LLM",10}");
    Console.WriteLine(new string('-', 84));

    foreach (var result in results)
    {
        int lastLlmMsgs = result.Turns[^1].SentToLlmCount;
        Console.WriteLine(
            $"{result.ReducerName,-32} | {result.TotalInputTokens,10:N0} | {result.TotalOutputTokens,10:N0} | {result.FinalMessageCount,10} | {lastLlmMsgs,10}");
    }

    Console.WriteLine("=========================================================================");
    Console.WriteLine();
    Console.WriteLine("  * 'Final Msgs' = total messages in session (unreduced)");
    Console.WriteLine("  * 'Last LLM'   = messages sent to LLM on final turn (after reduction)");
    Console.WriteLine();
    Console.WriteLine("  Key observations:");
    Console.WriteLine("  - DummyReducer: Highest token usage but perfect context retention.");
    Console.WriteLine("  - MyMessageCountingChatReducer: Lowest tokens but loses all tool context.");
    Console.WriteLine("  - ToolPreservingChatReducer: Balanced - retains recent tool pairs, drops older ones.");
    Console.WriteLine("  - ContentAwareChatReducer: Smart condensation preserves schema while saving tokens.");
    Console.WriteLine();
}

// =====================================================================
// Supporting types
// =====================================================================

public class MessageLoggingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public bool Silent { get; set; }
    public int LastRequestMessageCount { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        LastRequestMessageCount = messageList.Count;

        var response = await base.GetResponseAsync(messageList, options, cancellationToken);

        if (!Silent)
        {
            foreach (var message in response.Messages)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionCallContent call)
                    {
                        var argsPreview = call.Arguments is { Count: > 0 }
                            ? string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={Truncate(kv.Value?.ToString(), 80)}"))
                            : "";
                        Console.WriteLine($"  [Tool Call] {call.Name}({argsPreview})");
                    }
                }
            }
        }

        return response;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}

public static class UsageDetailsExtensions
{
    extension(UsageDetails? usageDetails)
    {
        public void OutputAsInformation()
        {
            if (usageDetails == null)
            {
                return;
            }

            Console.WriteLine($"- Input Tokens: {usageDetails.InputTokenCount}");
            var output = $"- Output Tokens: {usageDetails.OutputTokenCount} " + $"({usageDetails.ReasoningTokenCount ?? 0} was used for reasoning)";
            Console.WriteLine(output);
        }
    }
}

record DemoRunResult(
    string ReducerName,
    string Description,
    List<TurnResult> Turns,
    int TotalInputTokens,
    int TotalOutputTokens,
    int FinalMessageCount);

record TurnResult(
    int TurnNumber,
    string Question,
    string Response,
    int InputTokens,
    int OutputTokens,
    int SessionMessageCount,
    int SentToLlmCount);
