using Microsoft.Extensions.AI;

/// <summary>
/// A no-op chat reducer that returns messages unchanged.
/// </summary>
public sealed class DummyReducer : IChatReducer
{
    /// <inheritdoc />
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(messages);
    }
}
