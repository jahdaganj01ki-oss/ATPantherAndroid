namespace ATPanther.Core.Auth;

public sealed record LoginResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public HttpClient? ApiClient { get; init; }
}
