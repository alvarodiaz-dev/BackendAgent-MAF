using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using BasicAgent.Infrastructure;

namespace BasicAgent.Infrastructure
{
    internal static class SupabaseStorageService
    {
        private static readonly HttpClient HttpClient = new();

        public static async Task<string?> UploadZipAsync(string zipPath, string runId)
        {
            try
            {
                var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL") 
                                 ?? Environment.GetEnvironmentVariable("NEXT_PUBLIC_SUPABASE_URL");
                
                var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY") 
                                 ?? Environment.GetEnvironmentVariable("SUPABASE_KEY")
                                 ?? Environment.GetEnvironmentVariable("NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY");

                if (string.IsNullOrEmpty(supabaseUrl) || string.IsNullOrEmpty(supabaseKey))
                {
                    Console.WriteLine("[Storage] Error: No se encontraron las credenciales de Supabase (URL/KEY).");
                    return null;
                }

                string bucketName = "pipeline-runs";
                string fileName = $"{runId}.zip";
                // URL: {supabaseUrl}/storage/v1/object/{bucket}/{path}
                string url = $"{supabaseUrl.TrimEnd('/')}/storage/v1/object/{bucketName}/{fileName}";

                using var fileStream = File.OpenRead(zipPath);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                
                request.Headers.Add("apikey", supabaseKey);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", supabaseKey);
                
                var content = new StreamContent(fileStream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                request.Content = content;

                var response = await HttpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Storage] Exito: Archivo subido a Supabase: {fileName}");
                    return url;
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[Storage] Error al subir a Supabase: {response.StatusCode} - {error}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Error excepcional: {ex.Message}");
                return null;
            }
        }
    }
}
