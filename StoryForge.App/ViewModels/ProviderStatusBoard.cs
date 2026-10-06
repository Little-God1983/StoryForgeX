using StoryForge.Client;

namespace StoryForge.App.ViewModels;

/// <summary>
/// The latest provider statuses, shared by the top-bar pills and the Settings cards so both always
/// show the same thing. Checks never overlap: a refresh asked for while one runs adds exactly one
/// more run after it, so a check that started before a save is never the last word.
/// </summary>
public sealed class ProviderStatusBoard(IStoryForgeClient client)
{
    private Dictionary<ProviderId, ProviderStatus> _statuses = [];
    private Task? _running;
    private bool _again;

    public event EventHandler? StatusesChanged;

    public ProviderStatus? Get(ProviderId id) => _statuses.GetValueOrDefault(id);

    /// <summary>False until the first check has come back; until then nothing is known.</summary>
    public bool HasChecked { get; private set; }

    public Task RefreshAsync()
    {
        // Checked by IsCompleted rather than cleared in a finally: a client that answers
        // synchronously finishes RunAsync before the assignment below happens.
        if (_running is { IsCompleted: false })
        {
            _again = true;
            return _running;
        }
        return _running = RunAsync();
    }

    private async Task RunAsync()
    {
        do
        {
            _again = false;
            var statuses = await client.GetProviderStatusesAsync();
            _statuses = statuses.ToDictionary(s => s.Id);
            HasChecked = true;
            StatusesChanged?.Invoke(this, EventArgs.Empty);
        }
        while (_again);
    }
}
