using System;

namespace BasicAgent.Models
{
    internal sealed class PipelineRunContext
    {
        public required string RunId { get; init; }

        public required string RunDirectory { get; init; }

        public required string BmadOutputDirectory { get; init; }

        public required string PromptSlug { get; init; }

        public required string PromptFilePath { get; init; }

        public required string MetadataFilePath { get; init; }

        public required DateTime StartedAtUtc { get; init; }
    }
}
