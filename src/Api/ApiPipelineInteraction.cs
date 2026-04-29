using System;
using System.Threading;
using System.Threading.Tasks;
using BasicAgent.Pipeline;

namespace BasicAgent.Api
{
    internal sealed class ApiPipelineInteraction : IPipelineInteraction
    {
        private readonly PipelineRunState _state;
        private readonly bool _autoApprove;

        public ApiPipelineInteraction(PipelineRunState state, bool autoApprove)
        {
            _state = state;
            _autoApprove = autoApprove;
        }

        public void Log(string message)
        {
            Console.WriteLine(message);
            _state.AddLog(message);
        }

        public void UpdatePhase(string phase)
        {
            Console.WriteLine($"[Phase] {phase}");
            _state.UpdatePhase(phase);
            _state.AddLog($"Phase: {phase}");
        }

        public async Task<string> RequestUserInputAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (_autoApprove)
            {
                Console.WriteLine($"[Confirmation] Auto-approve enabled: {prompt}");
                _state.AddLog("Auto-approve enabled. Returning 'y'.");
                return "y";
            }

            Console.WriteLine($"[Confirmation] Waiting for user input: {prompt}");
            var (requestId, waitTask) = _state.WaitForConfirmation(prompt);
            _state.AddLog($"Waiting for confirmation requestId={requestId}");

            using var registration = cancellationToken.Register(() =>
            {
                _state.CancelPendingConfirmation("Canceled while waiting for confirmation.");
            });

            return await waitTask;
        }
    }
}