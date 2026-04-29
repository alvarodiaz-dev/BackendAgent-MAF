namespace BasicAgent.Api
{
    internal sealed record ChatRequest(string Prompt, bool AutoApprove = false, string? SessionId = null, string? UserId = null);

    internal sealed record ChatStartResponse(string RunId, string SessionId, string StatusUrl);

    internal sealed record ConfirmRequest(string RequestId, string Response);

    internal sealed record PendingConfirmationDto(string RequestId, string Prompt);

    internal sealed record ChatRunStatusResponse(
        string RunId,
        string Status,
        string? CurrentPhase,
        string? Error,
        string RunDirectory,
        PendingConfirmationDto? PendingConfirmation,
        System.Collections.Generic.IReadOnlyList<string> Logs);
}