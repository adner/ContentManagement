using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// A chat reducer that limits messages while preserving tool calls and their results.
/// Unlike MyMessageCountingChatReducer, this reducer keeps FunctionCallContent and
/// FunctionResultContent messages to maintain conversation coherence.
/// </summary>
/// <remarks>
/// The reducer ensures that:
/// - Tool call/result pairs are kept together (never orphaned)
/// - The most recent messages up to the target count are retained
/// </remarks>
public sealed class ToolPreservingChatReducer : IChatReducer
{
    private readonly int _targetCount;
    private readonly ILogger<ToolPreservingChatReducer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolPreservingChatReducer"/> class.
    /// </summary>
    /// <param name="targetCount">The maximum number of messages to retain.</param>
    /// <param name="logger">Logger instance for reporting reduction activity.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when targetCount is less than or equal to 0.</exception>
    public ToolPreservingChatReducer(int targetCount, ILogger<ToolPreservingChatReducer> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetCount, 0);
        _targetCount = targetCount;
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

        if (messageList.Count <= _targetCount)
        {
            return messageList;
        }

        // Find a safe cut point that doesn't orphan tool results
        var proposedCutIndex = messageList.Count - _targetCount;
        var cutIndex = FindSafeCutIndex(messageList, proposedCutIndex);

        var droppedMessages = messageList.Take(cutIndex).ToList();
        int droppedTotal = droppedMessages.Count;
        int droppedFunctionCalls = droppedMessages.Count(m => m.Contents.Any(c => c is FunctionCallContent));
        int droppedFunctionResults = droppedMessages.Count(m => m.Contents.Any(c => c is FunctionResultContent));

        _logger.LogInformation(
            "Chat history reduced: {TotalMessages} messages → {RetainedMessages} retained (target: {TargetCount}). " +
            "Dropped {DroppedTotal} messages (cut at index {CutIndex}).",
            messageList.Count,
            messageList.Count - droppedTotal,
            _targetCount,
            droppedTotal,
            cutIndex);

        if (droppedFunctionCalls > 0 || droppedFunctionResults > 0)
        {
            _logger.LogWarning(
                "Dropped tool messages from context: {DroppedFunctionCalls} function call(s), " +
                "{DroppedFunctionResults} function result(s). Tool context has been lost for these interactions.",
                droppedFunctionCalls,
                droppedFunctionResults);
        }

        return messageList.Skip(cutIndex);
    }

    /// <summary>
    /// Finds a safe index to start keeping messages from, ensuring we don't
    /// start with an orphaned tool result (FunctionResultContent without its call).
    /// </summary>
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

        // If we're starting with a tool result, back up to include its corresponding call
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
                // Can't find the call, skip this orphaned result
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
}
