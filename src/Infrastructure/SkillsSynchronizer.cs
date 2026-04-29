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

            var syncRoot = Path.Combine(ProjectPaths.GetProjectRootDirectory(), ".skills-sync");
            var skillsList = skillsToLoad?.ToList() ?? new List<string>();

            try
            {
                if (!Directory.Exists(syncRoot))
                {
                    Directory.CreateDirectory(syncRoot);
                }

                if (!Directory.Exists(Path.Combine(syncRoot, ".git")))
                {
                    Console.WriteLine($"[Skills] Inicializando repositorio de skills en {syncRoot}...");
                    
                    await ShellCommandTool.RunShellCommand("git init", syncRoot);
                    await ShellCommandTool.RunShellCommand($"git remote add origin {repoUrl}", syncRoot);
                    await ShellCommandTool.RunShellCommand("git config core.sparseCheckout true", syncRoot);
                }

                // Actualizar la lista de carpetas a descargar
                if (skillsList.Any())
                {
                    string sparsePath = Path.Combine(syncRoot, ".git", "info", "sparse-checkout");
                    await File.WriteAllLinesAsync(sparsePath, skillsList);
                    Console.WriteLine($"[Skills] Filtros: {string.Join(", ", skillsList)}");
                }

                Console.WriteLine($"[Skills] Sincronizando rama {branch}...");
                await ShellCommandTool.RunShellCommand($"git pull origin {branch}", syncRoot);

                // Devolvemos la subcarpeta 'skills' dentro del repo sincronizado
                return Path.Combine(syncRoot, "skills");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Error al sincronizar: {ex.Message}. Usando fallback local.");
                return Path.Combine(ProjectPaths.GetProjectRootDirectory(), "skills");
            }
        }
    }
}
