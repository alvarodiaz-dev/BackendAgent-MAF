using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BasicAgent.Tools;

namespace BasicAgent.Infrastructure
{
    internal static class SkillsSynchronizer
    {
        public static async Task<string> SyncAsync(IEnumerable<string>? skillsToLoad = null)
        {
            var repoUrl = Environment.GetEnvironmentVariable("SKILLS_REPO_URL");
            var branch = Environment.GetEnvironmentVariable("SKILLS_REPO_BRANCH") ?? "main";

            if (string.IsNullOrWhiteSpace(repoUrl))
            {
                Console.WriteLine("[Skills] No se detecto SKILLS_REPO_URL. Usando carpeta local 'skills'.");
                return Path.Combine(ProjectPaths.GetProjectRootDirectory(), "skills");
            }

            var targetPath = Path.Combine(Path.GetTempPath(), "gemini-agent-skills");
            var skillsList = skillsToLoad?.ToList() ?? new List<string>();

            try
            {
                if (!Directory.Exists(targetPath))
                {
                    Directory.CreateDirectory(targetPath);
                    Console.WriteLine($"[Skills] Inicializando descarga parcial (Sparse Checkout) en {targetPath}...");
                    
                    await ShellCommandTool.RunShellCommand("git init", targetPath);
                    await ShellCommandTool.RunShellCommand($"git remote add origin {repoUrl}", targetPath);
                    await ShellCommandTool.RunShellCommand("git config core.sparseCheckout true", targetPath);
                }

                // Actualizar la lista de carpetas a descargar
                if (skillsList.Any())
                {
                    string sparsePath = Path.Combine(targetPath, ".git", "info", "sparse-checkout");
                    // Escribimos las carpetas que queremos descargar (una por linea)
                    await File.WriteAllLinesAsync(sparsePath, skillsList);
                    Console.WriteLine($"[Skills] Configurando filtros: {string.Join(", ", skillsList)}");
                }

                Console.WriteLine($"[Skills] Sincronizando desde rama {branch}...");
                // Pull solo de las carpetas filtradas
                await ShellCommandTool.RunShellCommand($"git pull origin {branch}", targetPath);

                return targetPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Error al sincronizar skills: {ex.Message}. Usando fallback local.");
                return Path.Combine(ProjectPaths.GetProjectRootDirectory(), "skills");
            }
        }
    }
}
