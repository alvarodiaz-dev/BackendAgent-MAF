using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BasicAgent.Infrastructure;
using BasicAgent.Models;

namespace BasicAgent.Services
{
    internal static class RunOutputFactory
    {
        public static PipelineRunContext Create(string initialPrompt)
        {
            string projectRoot = ProjectPaths.GetProjectRootDirectory();
            string outputRoot = Path.Combine(projectRoot, "output");
            Directory.CreateDirectory(outputRoot);

            string slug = BuildSlug(initialPrompt);
            string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            string hash = BuildShortHash(initialPrompt);
            string baseRunId = $"output-{slug}-{timestamp}-{hash}";

            string runId = baseRunId;
            string runDir = Path.Combine(outputRoot, runId);
            int suffix = 1;
            while (Directory.Exists(runDir))
            {
                runId = $"{baseRunId}-{suffix}";
                runDir = Path.Combine(outputRoot, runId);
                suffix++;
            }

            Directory.CreateDirectory(runDir);

            string bmadOutputDir = Path.Combine(runDir, "_bmad-output");
            Directory.CreateDirectory(bmadOutputDir);

            string promptFilePath = Path.Combine(runDir, "prompt.txt");
            File.WriteAllText(promptFilePath, initialPrompt);

            string metadataPath = Path.Combine(runDir, "run-metadata.json");
            var startedAtUtc = DateTime.UtcNow;
            var metadata = new
            {
                runId,
                promptSlug = slug,
                startedAtUtc,
                runDirectory = runDir,
                bmadOutputDirectory = bmadOutputDir
            };

            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            return new PipelineRunContext
            {
                RunId = runId,
                RunDirectory = runDir,
                BmadOutputDirectory = bmadOutputDir,
                PromptSlug = slug,
                PromptFilePath = promptFilePath,
                MetadataFilePath = metadataPath,
                StartedAtUtc = startedAtUtc
            };
        }

        private static string BuildSlug(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "no-prompt";
            }

            var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (char ch in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                }
                else
                {
                    builder.Append('-');
                }
            }

            var normalized = builder.ToString();

            while (normalized.Contains("--", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
            }

            normalized = normalized.Trim('-');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "prompt";
            }

            return normalized.Length <= 50 ? normalized : normalized[..50].Trim('-');
        }

        private static string BuildShortHash(string text)
        {
            var data = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(data).ToLowerInvariant()[..8];
        }
    }
}
