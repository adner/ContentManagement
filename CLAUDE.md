# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build ContextManagementApp/ContextManagementApp.csproj
dotnet run --project ContextManagementApp/ContextManagementApp.csproj
```

## Architecture

.NET 10 console app using [AgentFrameworkToolkit](https://github.com/rwjdk/AgentFrameworkToolkit) (an opinionated C# toolkit over Microsoft Agent Framework) with the OpenAI provider. The toolkit is split into a base package (`AgentFrameworkToolkit`) plus provider-specific packages (`AgentFrameworkToolkit.OpenAI`, `.AzureOpenAI`, `.Anthropic`, etc.).

Single project, top-level statements in `Program.cs`. No tests yet.

### Agent Pipeline

The agent is configured via `AgentOptions` on an `OpenAIAgentFactory`. Key options:

- **`ClientFactory`** — wraps the underlying `IChatClient` with middleware (e.g. `MessageLoggingChatClient`, a `DelegatingChatClient` that logs all messages sent to the LLM).
- **`ChatHistoryProviderFactory`** — provides an `InMemoryChatHistoryProvider` with an `IChatReducer` to manage context window size.
- **`Tools`** — registered via `AIFunctionFactory.Create()` from static methods decorated with `[AITool]` and `[Description]`.

### Chat History & Reducers

The `InMemoryChatHistoryProvider` stores the **full** conversation history. The `IChatReducer` is applied at **read-time** (`BeforeMessagesRetrieval` trigger by default) — it trims messages before sending to the LLM but does not modify the stored history. `session.GetService<IList<ChatMessage>>()` returns the full unreduced list.

`IChatReducer` implementations live in `Reducers/`, each with increasing sophistication:

- **`DummyReducer`** — no-op pass-through: returns all messages unchanged. Useful for testing or disabling reduction without removing the wiring.
- **`MyMessageCountingChatReducer`** — simple count-based: keeps the first system message + the last N non-system messages, dropping all function call/result messages entirely.
- **`ToolPreservingChatReducer`** — count-based but tool-aware: keeps tool call/result pairs together using safe-cut logic that walks backwards to find the matching `FunctionCallContent` by `CallId` so pairs are never orphaned.
- **`ContentAwareChatReducer`** — three-phase reducer designed for Dataverse workloads:
  1. Under `recentMessageCount` → no reduction.
  2. Historical messages have data tool results condensed (TOON format parsed into a summary like "Returned 5 records from 'account'") while schema/preserved tool results are kept intact.
  3. If still over `maxMessages` → oldest messages trimmed with the same safe-cut logic.

  Accepts a `preservedToolNames` set (defaults to schema tool names) to control which tool results are never condensed.

### Dataverse Integration

`DataverseTools` is a static class exposing FetchXML query execution and WhoAmI against a Power Platform Dataverse instance via `Microsoft.PowerPlatform.Dataverse.Client`. Connection is configured in `appsettings.json`.

## Important Notes

- API keys must not be hardcoded — use user secrets, environment variables, or configuration files.
- `appsettings.json` currently contains secrets and should not be committed. Consider adding it to `.gitignore` and using `dotnet user-secrets` instead.
- Package versions are prerelease (`1.0.0-preview.*`); APIs may change between versions.
