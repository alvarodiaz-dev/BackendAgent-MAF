using System;
using System.Threading.Tasks;

namespace BasicAgent.Api
{
    internal interface IPipelinePersistence
    {
        bool IsEnabled { get; }

        Task EnsureSchemaAsync();

        Task UpsertSessionAsync(Guid sessionId, string? title);

        Task InsertMessageAsync(Guid sessionId, string role, string content, string? runId = null);

        Task CreateRunAsync(string runId, Guid sessionId, string prompt, string status, string currentPhase, string runDirectory, bool autoApprove);

        Task UpdateRunAsync(string runId, string status, string? currentPhase, string? error);

        Task AppendEventAsync(string runId, string eventType, string payloadJson);

        Task AddCheckpointAsync(string runId, string phase, string stepName, string stateJson);

        Task UpsertConfirmationAsync(string runId, string requestId, string prompt, string status, string? response);
    }

    internal sealed class NoOpPipelinePersistence : IPipelinePersistence
    {
        public bool IsEnabled => false;

        public Task EnsureSchemaAsync() => Task.CompletedTask;

        public Task UpsertSessionAsync(Guid sessionId, string? title) => Task.CompletedTask;

        public Task InsertMessageAsync(Guid sessionId, string role, string content, string? runId = null) => Task.CompletedTask;

        public Task CreateRunAsync(string runId, Guid sessionId, string prompt, string status, string currentPhase, string runDirectory, bool autoApprove) => Task.CompletedTask;

        public Task UpdateRunAsync(string runId, string status, string? currentPhase, string? error) => Task.CompletedTask;

        public Task AppendEventAsync(string runId, string eventType, string payloadJson) => Task.CompletedTask;

        public Task AddCheckpointAsync(string runId, string phase, string stepName, string stateJson) => Task.CompletedTask;

        public Task UpsertConfirmationAsync(string runId, string requestId, string prompt, string status, string? response) => Task.CompletedTask;
    }
}