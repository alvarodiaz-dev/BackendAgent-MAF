using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using BasicAgent.Models;

namespace BasicAgent.Api
{
    internal sealed class PipelineRunStore
    {
        private readonly IPipelinePersistence _persistence;
        private readonly ConcurrentDictionary<string, PipelineRunState> _runs = new(StringComparer.OrdinalIgnoreCase);

        public PipelineRunStore(IPipelinePersistence persistence)
        {
            _persistence = persistence;
        }

        public PipelineRunState Create(PipelineRunContext context, Guid sessionId)
        {
            var state = new PipelineRunState(context.RunId, context.RunDirectory, sessionId, _persistence);
            _runs[context.RunId] = state;
            return state;
        }

        public bool TryGet(string runId, out PipelineRunState? state) =>
            _runs.TryGetValue(runId, out state);
    }

    internal sealed class PipelineRunState
    {
        private readonly object _sync = new();
        private readonly List<string> _logs = new();
        private PendingConfirmation? _pending;
        private readonly IPipelinePersistence _persistence;

        public PipelineRunState(string runId, string runDirectory, Guid sessionId, IPipelinePersistence persistence)
        {
            RunId = runId;
            RunDirectory = runDirectory;
            SessionId = sessionId;
            Status = "queued";
            _persistence = persistence;
        }

        public string RunId { get; }

        public Guid SessionId { get; }

        public string RunDirectory { get; }

        public string Status { get; private set; }

        public string? CurrentPhase { get; private set; }

        public string? Error { get; private set; }

        public Task PersistCreatedAsync(string prompt, bool autoApprove)
        {
            return _persistence.CreateRunAsync(RunId, SessionId, prompt, Status, CurrentPhase ?? "queued", RunDirectory, autoApprove);
        }

        public void MarkRunning(string phase)
        {
            string status;
            string currentPhase;
            lock (_sync)
            {
                Status = "running";
                CurrentPhase = phase;
                Error = null;
                status = Status;
                currentPhase = CurrentPhase;
            }

            PersistSafe(() => _persistence.UpdateRunAsync(RunId, status, currentPhase, null));
        }

        public void UpdatePhase(string phase)
        {
            string status;
            string currentPhase;
            lock (_sync)
            {
                CurrentPhase = phase;
                if (Status == "queued")
                {
                    Status = "running";
                }

                status = Status;
                currentPhase = CurrentPhase;
            }

            PersistSafe(() => _persistence.UpdateRunAsync(RunId, status, currentPhase, Error));
            PersistSafe(() => _persistence.AddCheckpointAsync(
                RunId,
                currentPhase,
                "phase_update",
                JsonSerializer.Serialize(new { phase = currentPhase, atUtc = DateTime.UtcNow })));
        }

        public void MarkCompleted()
        {
            string status;
            string? currentPhase;
            lock (_sync)
            {
                Status = "completed";
                CurrentPhase = "done";
                _pending = null;
                status = Status;
                currentPhase = CurrentPhase;
            }

            PersistSafe(() => _persistence.UpdateRunAsync(RunId, status, currentPhase, null));
            PersistSafe(() => _persistence.AppendEventAsync(RunId, "run_completed", "{}"));
        }

        public void MarkCanceled(string reason)
        {
            string status;
            string? currentPhase;
            lock (_sync)
            {
                Status = "canceled";
                Error = reason;
                _pending = null;
                status = Status;
                currentPhase = CurrentPhase;
            }

            PersistSafe(() => _persistence.UpdateRunAsync(RunId, status, currentPhase, reason));
            PersistSafe(() => _persistence.AppendEventAsync(RunId, "run_canceled", JsonSerializer.Serialize(new { reason })));
        }

        public void MarkFailed(string error)
        {
            string status;
            string? currentPhase;
            lock (_sync)
            {
                Status = "failed";
                Error = error;
                _pending = null;
                status = Status;
                currentPhase = CurrentPhase;
            }

            PersistSafe(() => _persistence.UpdateRunAsync(RunId, status, currentPhase, error));
            PersistSafe(() => _persistence.AppendEventAsync(RunId, "run_failed", JsonSerializer.Serialize(new { error })));
        }

        public void AddLog(string message)
        {
            lock (_sync)
            {
                _logs.Add($"[{DateTime.UtcNow:O}] {message}");
            }

            PersistSafe(() => _persistence.AppendEventAsync(RunId, "log", JsonSerializer.Serialize(new { message })));
        }

        public (string requestId, System.Threading.Tasks.Task<string> waitTask) WaitForConfirmation(string prompt)
        {
            lock (_sync)
            {
                var requestId = Guid.NewGuid().ToString("N");
                var tcs = new System.Threading.Tasks.TaskCompletionSource<string>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = new PendingConfirmation(requestId, prompt, tcs);
                Status = "waiting_confirmation";
                _logs.Add($"[{DateTime.UtcNow:O}] Confirmation requested: {prompt}");

                PersistSafe(() => _persistence.UpdateRunAsync(RunId, Status, CurrentPhase, Error));
                PersistSafe(() => _persistence.UpsertConfirmationAsync(RunId, requestId, prompt, "pending", null));
                PersistSafe(() => _persistence.AppendEventAsync(
                    RunId,
                    "confirmation_requested",
                    JsonSerializer.Serialize(new { requestId, prompt })));
                return (requestId, tcs.Task);
            }
        }

        public bool TrySubmitConfirmation(string requestId, string response)
        {
            lock (_sync)
            {
                if (_pending == null || !string.Equals(_pending.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string prompt = _pending.Prompt;
                _pending.TaskSource.TrySetResult(response);
                _pending = null;
                Status = "running";
                _logs.Add($"[{DateTime.UtcNow:O}] Confirmation submitted: {response}");

                PersistSafe(() => _persistence.UpsertConfirmationAsync(RunId, requestId, prompt, "answered", response));
                PersistSafe(() => _persistence.UpdateRunAsync(RunId, Status, CurrentPhase, Error));
                PersistSafe(() => _persistence.AppendEventAsync(
                    RunId,
                    "confirmation_submitted",
                    JsonSerializer.Serialize(new { requestId, response })));
                return true;
            }
        }

        public void CancelPendingConfirmation(string reason)
        {
            lock (_sync)
            {
                if (_pending != null)
                {
                    _pending.TaskSource.TrySetCanceled();
                    _pending = null;
                }

                Status = "canceled";
                Error = reason;
                _logs.Add($"[{DateTime.UtcNow:O}] {reason}");

                PersistSafe(() => _persistence.UpdateRunAsync(RunId, Status, CurrentPhase, Error));
                PersistSafe(() => _persistence.AppendEventAsync(RunId, "confirmation_canceled", JsonSerializer.Serialize(new { reason })));
            }
        }

        public ChatRunStatusResponse ToResponse()
        {
            lock (_sync)
            {
                PendingConfirmationDto? pending = null;
                if (_pending != null)
                {
                    pending = new PendingConfirmationDto(_pending.RequestId, _pending.Prompt);
                }

                return new ChatRunStatusResponse(
                    RunId,
                    Status,
                    CurrentPhase,
                    Error,
                    RunDirectory,
                    pending,
                    _logs.ToArray());
            }
        }

        private sealed class PendingConfirmation
        {
            public PendingConfirmation(string requestId, string prompt, System.Threading.Tasks.TaskCompletionSource<string> taskSource)
            {
                RequestId = requestId;
                Prompt = prompt;
                TaskSource = taskSource;
            }

            public string RequestId { get; }

            public string Prompt { get; }

            public System.Threading.Tasks.TaskCompletionSource<string> TaskSource { get; }
        }

        private static void PersistSafe(Func<System.Threading.Tasks.Task> operation)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await operation();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Persistence Warning] {ex.Message}");
                }
            });
        }
    }
}