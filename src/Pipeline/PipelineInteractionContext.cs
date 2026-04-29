using System;
using System.Threading;

namespace BasicAgent.Pipeline
{
    internal static class PipelineInteractionContext
    {
        private static readonly AsyncLocal<IPipelineInteraction?> CurrentInteraction = new();

        public static IPipelineInteraction? Current => CurrentInteraction.Value;

        public static IDisposable Use(IPipelineInteraction interaction)
        {
            var previous = CurrentInteraction.Value;
            CurrentInteraction.Value = interaction;
            return new Scope(() => CurrentInteraction.Value = previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action _onDispose;
            private bool _disposed;

            public Scope(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _onDispose();
                _disposed = true;
            }
        }
    }
}