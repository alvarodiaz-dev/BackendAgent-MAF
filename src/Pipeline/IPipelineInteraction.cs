using System.Threading;
using System.Threading.Tasks;

namespace BasicAgent.Pipeline
{
    internal interface IPipelineInteraction
    {
        void Log(string message);

        void UpdatePhase(string phase);

        Task<string> RequestUserInputAsync(string prompt, CancellationToken cancellationToken = default);
    }
}