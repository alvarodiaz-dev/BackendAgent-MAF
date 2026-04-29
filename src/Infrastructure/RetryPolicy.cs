using System;

namespace BasicAgent.Infrastructure
{
    internal static class RetryPolicy
    {
        private static readonly Random Jitter = new();

        public static TimeSpan ComputeExponentialBackoff(int attempt, int baseDelayMs = 800, int maxDelayMs = 8000)
        {
            if (attempt < 1)
            {
                attempt = 1;
            }

            var exponential = Math.Min(maxDelayMs, baseDelayMs * Math.Pow(2, attempt - 1));
            var jitter = Jitter.Next(100, 600);
            return TimeSpan.FromMilliseconds(exponential + jitter);
        }

        public static bool IsTransient(Exception ex)
        {
            var msg = ex.Message ?? string.Empty;
            return msg.Contains("429", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("503", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("connection", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("socket", StringComparison.OrdinalIgnoreCase);
        }
    }
}
