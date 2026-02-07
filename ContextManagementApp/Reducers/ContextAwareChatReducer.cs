using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// A content-aware chat reducer that preserves schema tool results while condensing
/// older data tool results to save tokens.
/// </summary>
/// <remarks>
/// Three-phase reduction:
/// <list type="number">
///   <item>Under <c>recentMessageCount</c> messages: return everything as-is.</item>
///   <item>Between <c>recentMessageCount</c> and <c>maxMessages</c>: condense historical
///         data tool results while keeping schema tool results fully intact.</item>
///   <item>Over <c>maxMessages</c> after condensation: trim the oldest messages
///         using safe-cut logic that preserves tool call/result pairs.</item>
/// </list>
/// </remarks>

public sealed partial class ContentAwareChatReducer : IChatReducer
{
    private readonly int _maxMessages;
    private readonly int _recentMessageCount;
    private readonly HashSet<string> _preservedToolNames;
    private readonly ILogger<ContentAwareChatReducer>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentAwareChatReducer"/> class.
    /// </summary>
    /// <param name="maxMessages">Hard cap on total messages after reduction.</param>
    /// <param name="recentMessageCount">Number of most recent messages to keep at full fidelity.</param>
    /// <param name="preservedToolNames">
    /// Tool names whose results are always kept fully (e.g. schema tools).
    /// Defaults to SearchDataverseSchemaAsync and GetTableSchemaAsync.
    /// </param>
    public ContentAwareChatReducer(
        int maxMessages = 40,
        int recentMessageCount = 10,
        IEnumerable<string>? preservedToolNames = null,
        ILogger<ContentAwareChatReducer>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMessages, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(recentMessageCount, 0);

        _maxMessages = maxMessages;
        _recentMessageCount = Math.Min(recentMessageCount, maxMessages);
        _preservedToolNames = preservedToolNames?.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal)
            {
                "describe_table",
                "list_tables"
            };
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return Task.FromResult(GetReducedMessages(messages));
    }

    private IEnumerable<ChatMessage> GetReducedMessages(IEnumerable<ChatMessage> messages)
    {
        var messageList = messages.ToList();

        // Phase 1: Under recent count, no reduction needed
        if (messageList.Count <= _recentMessageCount)
        {
            _logger?.LogInformation(
                "Phase 1: {MessageCount} messages <= recentMessageCount ({RecentMessageCount}), no reduction needed.",
                messageList.Count, _recentMessageCount);
            return messageList;
        }

        // Phase 2: Split into historical + recent, condense historical data tool results
        var recentStart = messageList.Count - _recentMessageCount;
        var historical = messageList.Take(recentStart).ToList();
        var recent = messageList.Skip(recentStart).ToList();

        _logger?.LogInformation(
            "Phase 2: {MessageCount} messages split into {HistoricalCount} historical + {RecentCount} recent. Condensing historical data tool results.",
            messageList.Count, historical.Count, recent.Count);

        var condensedHistorical = CondenseHistorical(historical);
        var result = condensedHistorical.Concat(recent).ToList();

        // Phase 3: If still over max, trim oldest with safe-cut logic
        if (result.Count > _maxMessages)
        {
            var cutIndex = FindSafeCutIndex(result, result.Count - _maxMessages);
            _logger?.LogInformation(
                "Phase 3: {ResultCount} messages exceeds maxMessages ({MaxMessages}). Trimming {CutCount} oldest messages (safe-cut at index {CutIndex}).",
                result.Count, _maxMessages, cutIndex, cutIndex);
            result = result.Skip(cutIndex).ToList();
        }
        else
        {
            _logger?.LogInformation(
                "Phase 3: {ResultCount} messages within maxMessages ({MaxMessages}), no trimming needed.",
                result.Count, _maxMessages);
        }

        _logger?.LogInformation("Reduction complete: {OriginalCount} → {FinalCount} messages.", messageList.Count, result.Count);

        return result;
    }

    private List<ChatMessage> CondenseHistorical(List<ChatMessage> messages)
    {
        // Track CallId → function name as we iterate (calls always precede results)
        var callIdToName = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            // Register function call names as we encounter them
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                if (!string.IsNullOrEmpty(call.CallId) && !string.IsNullOrEmpty(call.Name))
                {
                    callIdToName[call.CallId] = call.Name;
                }
            }

            var functionResults = message.Contents.OfType<FunctionResultContent>().ToList();

            if (functionResults.Count == 0)
            {
                result.Add(message);
                continue;
            }

            // If all results are for preserved tools, keep the message fully
            if (functionResults.All(fr => IsPreservedTool(fr.CallId, callIdToName)))
            {
                foreach (var fr in functionResults)
                {
                    callIdToName.TryGetValue(fr.CallId, out var toolName);
                    _logger?.LogInformation("  Preserved tool result: {ToolName} ({CallId})", toolName ?? "unknown", fr.CallId);
                }
                result.Add(message);
                continue;
            }

            // Build condensed contents: preserve schema results, condense data results
            var condensedContents = new List<AIContent>();

            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent frc && !IsPreservedTool(frc.CallId, callIdToName))
                {
                    callIdToName.TryGetValue(frc.CallId, out var toolName);
                    var summary = CondenseToonResult(frc.Result?.ToString() ?? string.Empty);
                    _logger?.LogInformation("  Condensed tool result: {ToolName} ({CallId}) → {Summary}", toolName ?? "unknown", frc.CallId, summary);
                    condensedContents.Add(new FunctionResultContent(frc.CallId, summary));
                }
                else
                {
                    condensedContents.Add(content);
                }
            }

            result.Add(new ChatMessage(message.Role, condensedContents));
        }

        return result;
    }

    private bool IsPreservedTool(string callId, Dictionary<string, string> callIdToName)
    {
        return !string.IsNullOrEmpty(callId)
            && callIdToName.TryGetValue(callId, out var name)
            && _preservedToolNames.Contains(name);
    }

    /// <summary>
    /// Parses TOON format header to produce a compact summary.
    /// Falls back to truncation if the format is not recognized.
    /// </summary>
    internal static string CondenseToonResult(string toonResult)
    {
        if (string.IsNullOrWhiteSpace(toonResult))
        {
            return "[Condensed: Empty result]";
        }

        string? entityName = null;
        string? moreRecords = null;
        string? entityCount = null;

        // Parse the TOON header (first few lines)
        var lines = toonResult.Split('\n');
        foreach (var line in lines.Take(5))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("entityName:", StringComparison.Ordinal))
            {
                entityName = trimmed["entityName:".Length..].Trim();
            }
            else if (trimmed.StartsWith("moreRecords:", StringComparison.Ordinal))
            {
                moreRecords = trimmed["moreRecords:".Length..].Trim();
            }
            else if (trimmed.StartsWith("entities[", StringComparison.Ordinal))
            {
                var match = EntityCountPattern().Match(trimmed);
                if (match.Success)
                {
                    entityCount = match.Groups[1].Value;
                }
            }
        }

        if (entityName != null && entityCount != null)
        {
            var moreInfo = moreRecords == "true" ? " (more records available)" : "";
            return $"[Condensed: Returned {entityCount} records from '{entityName}'{moreInfo}. See the assistant response for details.]";
        }

        // Fallback: truncate if not recognizable TOON format
        const int maxLength = 200;
        if (toonResult.Length <= maxLength)
        {
            return toonResult;
        }

        return string.Concat(toonResult.AsSpan(0, maxLength), "... [truncated]");
    }

    [GeneratedRegex(@"entities\[(\d+)\]")]
    private static partial Regex EntityCountPattern();

    #region Safe-cut logic (from ToolPreservingChatReducer)

    private static int FindSafeCutIndex(List<ChatMessage> messages, int proposedCutIndex)
    {
        if (proposedCutIndex <= 0)
        {
            return 0;
        }

        if (proposedCutIndex >= messages.Count)
        {
            return messages.Count;
        }

        var cutIndex = proposedCutIndex;

        while (cutIndex < messages.Count && HasToolResult(messages[cutIndex]))
        {
            var callIndex = FindToolCallIndex(messages, cutIndex);
            if (callIndex >= 0 && callIndex < cutIndex)
            {
                cutIndex = callIndex;
                break;
            }
            else
            {
                cutIndex++;
            }
        }

        return cutIndex;
    }

    private static bool HasToolResult(ChatMessage message)
    {
        return message.Contents.Any(c => c is FunctionResultContent);
    }

    private static int FindToolCallIndex(List<ChatMessage> messages, int resultIndex)
    {
        var resultCallIds = messages[resultIndex].Contents
            .OfType<FunctionResultContent>()
            .Select(r => r.CallId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet();

        if (resultCallIds.Count == 0)
        {
            return -1;
        }

        for (var i = resultIndex - 1; i >= 0; i--)
        {
            var hasMatchingCall = messages[i].Contents
                .OfType<FunctionCallContent>()
                .Any(c => resultCallIds.Contains(c.CallId));

            if (hasMatchingCall)
            {
                return i;
            }
        }

        return -1;
    }

    #endregion
}
