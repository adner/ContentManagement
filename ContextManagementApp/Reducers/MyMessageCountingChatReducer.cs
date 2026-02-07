// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Provides a chat reducer that limits the number of non-system messages in a conversation to a specified maximum
/// count, preserving the most recent messages and the first system message if present.
/// </summary>
/// <remarks>
/// <para>
/// This implementation is based on the
/// <see href="https://github.com/dotnet/extensions/blob/56292e7bc2a6ee4d163f716cca5c62056c6b5b4b/src/Libraries/Microsoft.Extensions.AI/ChatReduction/MessageCountingChatReducer.cs">MessageCountingChatReducer</see>
/// from Microsoft.Extensions.AI, with modifications made for illustrative purposes.
/// </para>
/// <para>
/// The reducer always includes the first encountered system message, if any, and then retains up to the specified
/// number of the most recent non-system messages. Messages containing function call or function result content are
/// excluded from the reduced output.
/// </para>
/// </remarks>
public sealed class MyMessageCountingChatReducer : IChatReducer
{
    private readonly int _targetCount;
    private readonly ILogger<MyMessageCountingChatReducer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageCountingChatReducer"/> class.
    /// </summary>
    /// <param name="targetCount">The maximum number of non-system messages to retain in the reduced output.</param>
    /// <param name="logger">Logger instance for reporting reduction activity.</param>
    public MyMessageCountingChatReducer(int targetCount, ILogger<MyMessageCountingChatReducer> logger)
    {
        _targetCount = targetCount;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {

        return Task.FromResult(GetReducedMessages(messages));
    }

    private IEnumerable<ChatMessage> GetReducedMessages(IEnumerable<ChatMessage> messages)
    {
        ChatMessage? systemMessage = null;
        Queue<ChatMessage> reducedMessages = new(capacity: _targetCount);
        int totalNonSystemCount = 0;
        int droppedFunctionCount = 0;

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                systemMessage ??= message;
            }
            else if (!message.Contents.Any(m => m is FunctionCallContent or FunctionResultContent))
            {
                totalNonSystemCount++;

                if (reducedMessages.Count >= _targetCount)
                {
                    _ = reducedMessages.Dequeue();
                }

                reducedMessages.Enqueue(message);
            }
            else
            {
                droppedFunctionCount++;
            }
        }

        int droppedByLimit = totalNonSystemCount - reducedMessages.Count;
        int totalDropped = droppedByLimit + droppedFunctionCount;

        if (totalDropped > 0)
        {
            _logger.LogInformation(
                "Chat history reduced: {TotalMessages} messages → {RetainedMessages} retained. " +
                "Dropped {DroppedByLimit} non-system messages exceeding limit of {TargetCount}, " +
                "dropped {DroppedFunctionMessages} function call/result messages.",
                totalNonSystemCount + droppedFunctionCount + (systemMessage is not null ? 1 : 0),
                reducedMessages.Count + (systemMessage is not null ? 1 : 0),
                droppedByLimit,
                _targetCount,
                droppedFunctionCount);
        }

        if (systemMessage is not null)
        {
            yield return systemMessage;
        }

        while (reducedMessages.Count > 0)
        {
            yield return reducedMessages.Dequeue();
        }
    }
}