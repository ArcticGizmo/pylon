using Postern.Client;

namespace Pylon.Broker.Monitors;

public enum PosternState { Unknown, NotRunning, WaitingForApproval, Approved, Denied, Error }

/// <summary>
/// Pylon as an ordinary Postern app. The scopes it asks for come from the enabled monitors; it re-registers only when
/// they're no longer covered by what's already approved, so removing a monitor never prompts and adding a repo from
/// an owner already approved never prompts either (D5).
/// <para>A denial or a prompt left unanswered isn't retried on its own: <see cref="AskAgain"/> is the user's call.</para>
/// </summary>
public sealed class PosternGateway : IDisposable
{
    readonly PosternClient _client;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    IReadOnlyList<string> _refused = [];

    public PosternGateway(PosternOptions options) => _client = new PosternClient(options);

    public PosternState State { get; private set; }

    public string? Detail { get; private set; }

    /// <summary>The scopes Postern last reported as approved for Pylon.</summary>
    public IReadOnlyList<string> Approved { get; private set; } = [];

    public string PipeName => _client.Options.PipeName;

    public event Action? Changed;

    public async Task EnsureAsync(IReadOnlyList<string> wanted, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_client.IsAvailable)
            {
                Set(PosternState.NotRunning, $"Postern isn't running on pipe '{PipeName}'. {PosternClient.NotRunningHint}");
                return;
            }

            if (_client.HasToken)
            {
                var status = await _client.GetStatusAsync(cancellationToken);
                if (status.App is { } app && app.Status.Equals("approved", StringComparison.OrdinalIgnoreCase))
                    Approved = app.Scopes;
                else
                {
                    _client.ForgetToken();
                    Approved = [];
                }
            }

            var missing = wanted.Where(s => !Approved.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count == 0)
            {
                Set(PosternState.Approved, wanted.Count == 0 ? "No monitor needs Postern yet." : $"{Approved.Count} scope(s) approved.");
                return;
            }
            if (State is PosternState.Denied && _refused.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
                return;

            // Ask for everything still wanted, not just what's missing: approval replaces the app's scopes.
            Set(PosternState.WaitingForApproval, $"Waiting for you to approve Pylon in Postern ({missing.Count} new scope(s)).");
            var result = await _client.RegisterAsync(wanted, cancellationToken: cancellationToken);
            switch (result.Outcome)
            {
                case RegistrationOutcome.Approved:
                    Approved = wanted;
                    Set(PosternState.Approved, $"{wanted.Count} scope(s) approved.");
                    break;
                default:
                    _refused = wanted;
                    Set(PosternState.Denied, result.Outcome == RegistrationOutcome.TimedOut
                        ? "Postern's prompt wasn't answered in time. Use 'Ask Postern again'."
                        : $"Postern refused: {result.Detail}");
                    break;
            }
        }
        catch (PosternVerificationException ex)
        {
            Set(PosternState.Error, ex.Message);
        }
        catch (Exception ex) when (ex is PosternUnavailableException or HttpRequestException or IOException or TimeoutException)
        {
            Set(PosternState.NotRunning, $"Couldn't reach Postern: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lets the next <see cref="EnsureAsync"/> prompt again after a denial or timeout.</summary>
    public void AskAgain()
    {
        _refused = [];
        if (State == PosternState.Denied)
            Set(PosternState.Unknown, "Asking again…");
    }

    public HttpClient HttpFor(string connection)
    {
        lock (_clients)
        {
            if (!_clients.TryGetValue(connection, out var client))
                _clients[connection] = client = _client.CreateHttpClient(connection);
            return client;
        }
    }

    void Set(PosternState state, string detail)
    {
        var changed = state != State || detail != Detail;
        State = state;
        Detail = detail;
        if (changed)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_clients)
        {
            foreach (var client in _clients.Values)
                client.Dispose();
            _clients.Clear();
        }
        _client.Dispose();
        _gate.Dispose();
    }
}
