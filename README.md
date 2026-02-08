# Context Management Demo

A .NET 10 console app that demonstrates how different chat history reduction strategies affect an LLM agent's behavior and token usage when working with tools.

## What this is

When an LLM agent uses tools (like querying a database), the conversation history grows quickly — each tool call adds multiple messages. Eventually you hit context window limits or burn through tokens unnecessarily. A *chat reducer* trims the history before sending it to the LLM, but how you trim matters a lot.

This project implements four reducers with increasing sophistication and runs the same scripted conversation against each one, so you can directly compare the trade-offs: token cost vs. context retention vs. agent behavior.

## The reducers

| Reducer | Strategy | Trade-off |
|---|---|---|
| **DummyReducer** | Keep everything | Baseline. Token usage grows unbounded, but the agent never forgets anything. |
| **MyMessageCountingChatReducer** | Keep last N messages, drop all tool call/result messages | Cheapest, but the agent loses all tool context and has to re-query constantly. |
| **ToolPreservingChatReducer** | Keep last N messages, but preserve tool call/result pairs together | Better — tool pairs stay intact until they age out, then they're gone. |
| **ContentAwareChatReducer** | Three-phase: keep recent messages intact, condense older data tool results into summaries, preserve schema tool results fully | Best balance — the agent retains schema knowledge while older query results are compressed to one-line summaries. |

## The demo scenario

The agent connects to a Dataverse database (via MCP tools) containing astronauts, rockets, and missions. The scripted conversation asks the agent to discover the schema, query data, and then recall information from earlier in the conversation — which is where the differences between reducers become visible.

## Running it

```bash
# Interactive mode (default)
dotnet run --project ContextManagementApp/ContextManagementApp.csproj

# Demo mode — runs scripted questions against all 4 reducers
dotnet run --project ContextManagementApp/ContextManagementApp.csproj -- --demo

# Run only specific reducers
dotnet run --project ContextManagementApp/ContextManagementApp.csproj -- --demo --reducer dummy --reducer contentaware

# With full message logging
dotnet run --project ContextManagementApp/ContextManagementApp.csproj -- --demo --verbose
```

Reducer aliases: `dummy`, `counting`, `toolpreserving`, `contentaware`.

## AgentFrameworkToolkit

This project is built on [AgentFrameworkToolkit](https://github.com/rwjdk/AgentFrameworkToolkit), an opinionated C# toolkit that sits on top of Microsoft Agent Framework.

Microsoft Agent Framework gives you the plumbing for building LLM agents in .NET — chat clients, tool calling, chat history — but leaves a lot of wiring up to you. AgentFrameworkToolkit wraps this with provider-specific factories (`OpenAIAgentFactory`, `AnthropicAgentFactory`, etc.) that make the common path short. Creating an agent with tools, a specific model, reasoning effort, and a chat history provider is a single `CreateAgent(new AgentOptions { ... })` call instead of manually assembling chat clients, configuring tool bindings, and stitching together middleware.

The parts that matter for this project:

- **`AgentOptions.ChatHistoryProviderFactory`** — this is the hook where the `IChatReducer` plugs in. The toolkit's `InMemoryChatHistoryProvider` stores the full history and applies the reducer at read-time, so the reducer only affects what the LLM sees, not what's stored.
- **`AgentOptions.ClientFactory`** — lets you wrap the underlying `IChatClient` with middleware (like our `MessageLoggingChatClient` that logs what gets sent to the LLM after reduction).
- **`AgentOptions.Tools`** — accepts tools from any source, including MCP servers. No ceremony beyond passing the list.

Without it, wiring up the same agent with chat history reduction, logging middleware, and MCP tools would take significantly more code and a deeper understanding of the Microsoft Agent Framework internals.

## Stack

- .NET 10, C#
- [AgentFrameworkToolkit](https://github.com/rwjdk/AgentFrameworkToolkit) (over Microsoft Agent Framework)
- OpenAI (GPT-5-2)
- Power Platform Dataverse via Model Context Protocol (MCP)

## Setup

Requires `appsettings.json` with OpenAI API key, Dataverse connection string, and MCP server configuration. See `CLAUDE.md` for architecture details.
