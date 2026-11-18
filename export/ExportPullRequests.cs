#!/usr/bin/env dotnet
#:property PublishAot=false
// Exporta métricas de Pull Requests de Azure DevOps a CSV (para la web de pr-reviewer).
//
// Uso (desde la raíz del repo, requiere .NET 10 o superior):
//   export AZDO_PAT=xxxxxxxx
//   dotnet run export/ExportPullRequests.cs [ruta/config.json]
//
// - El PAT se lee de la variable de entorno AZDO_PAT (alcance mínimo: Code > Read).
// - La configuración (organización, proyectos, autores…) va en export/config.json, que NO se sube a git.
//   Copia export/config.example.json como punto de partida.
// - Los detalles de cada PR (commits, archivos, líneas) se guardan en export/.cache/ para que las
//   siguientes ejecuciones solo consulten las PRs nuevas o activas.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
};
var inv = CultureInfo.InvariantCulture;

// ==========================================
// 1. Configuración
// ==========================================
string configPath = args.Length > 0 ? args[0]
    : File.Exists(Path.Combine("export", "config.json")) ? Path.Combine("export", "config.json")
    : "config.json";

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"❌ No encuentro {configPath}. Copia export/config.example.json a export/config.json y rellénalo.");
    return 1;
}

var cfg = JsonSerializer.Deserialize<ExportConfig>(File.ReadAllText(configPath), jsonOptions) ?? new ExportConfig();
if (string.IsNullOrWhiteSpace(cfg.Organization) || cfg.Projects.Length == 0)
{
    Console.Error.WriteLine("❌ config.json debe indicar \"organization\" y al menos un proyecto en \"projects\".");
    return 1;
}

string? pat = Environment.GetEnvironmentVariable("AZDO_PAT");
if (string.IsNullOrWhiteSpace(pat))
{
    Console.Error.WriteLine("❌ Falta la variable de entorno AZDO_PAT con tu Personal Access Token (alcance Code > Read).");
    return 1;
}

DateTime cutoffDate = DateTime.UtcNow.AddYears(-Math.Max(1, cfg.YearsBack));
var excludeRegexes = cfg.ExcludePatterns.Select(GlobToRegex).ToArray();
string cacheDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, ".cache");
string cachePath = Path.Combine(cacheDir, "pr-details.json");
// Si cambian las exclusiones, los recuentos cacheados ya no valen
string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", cfg.ExcludePatterns))))[..16];

// ==========================================
// 2. Cliente HTTP
// ==========================================
using var http = new HttpClient
{
    BaseAddress = new Uri($"{cfg.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(cfg.Organization)}/"),
    Timeout = TimeSpan.FromSeconds(100),
};
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

// Reintenta ante 429 (límite de peticiones) y errores 5xx, respetando Retry-After.
async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest)
{
    for (int attempt = 0; ; attempt++)
    {
        var resp = await http.SendAsync(makeRequest());
        bool retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
        if (retryable && attempt < 5)
        {
            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
            resp.Dispose();
            await Task.Delay(wait);
            continue;
        }
        return resp;
    }
}

async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, string what)
{
    using (resp)
    {
        // Con un PAT inválido Azure DevOps puede responder 203 con la página HTML de login
        bool html = resp.Content.Headers.ContentType?.MediaType?.Contains("html") == true;
        if (!resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NonAuthoritativeInformation || html)
            throw new ApiException(resp.StatusCode, $"{what}: HTTP {(int)resp.StatusCode}{(html ? " (¿PAT inválido o caducado?)" : "")}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}

Task<JsonElement> GetJsonAsync(string url) =>
    SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url)).ContinueWith(t => ReadJsonAsync(t.Result, url)).Unwrap();

Task<JsonElement> PostJsonAsync(string url, object body) =>
    SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    }).ContinueWith(t => ReadJsonAsync(t.Result, url)).Unwrap();

// ==========================================
// 3. Listar PRs (igual que el script original)
// ==========================================
Console.WriteLine($"🚀 Extrayendo PRs desde {cutoffDate:yyyy-MM-dd} y generando {cfg.Output}...\n");
var prs = new List<(string Project, PullRequest Pr)>();

foreach (string project in cfg.Projects)
{
    Console.WriteLine($"Listando PRs del proyecto: {project}");
    string encodedProject = Uri.EscapeDataString(project);
    int skip = 0;
    const int top = 1000;
    bool stopPagination = false;

    while (!stopPagination)
    {
        string url = $"{encodedProject}/_apis/git/pullrequests?searchCriteria.status={Uri.EscapeDataString(cfg.Status)}&$top={top}&$skip={skip}&api-version=7.1";
        JsonElement json;
        try { json = await GetJsonAsync(url); }
        catch (ApiException ex) { Console.Error.WriteLine($"  ⚠️ {project}: {ex.Message}"); break; }

        var page = json.Deserialize<AdoResponse>(jsonOptions);
        if (page?.Value == null || page.Value.Count == 0) break;

        foreach (var pr in page.Value)
        {
            if (pr.CreationDate < cutoffDate) { stopPagination = true; break; }
            string author = pr.CreatedBy?.DisplayName ?? "Desconocido";
            // Si la lista de autores está vacía, pasan todos
            if (cfg.Authors.Length > 0 && !cfg.Authors.Any(t => author.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;
            prs.Add((project, pr));
        }

        if (page.Value.Count < top) break;
        skip += top;
    }
}
Console.WriteLine($"\n{prs.Count} PRs encontradas. Obteniendo commits, archivos y líneas de cada una...\n");

// ==========================================
// 4. Detalles por PR: commits, archivos y líneas
// ==========================================
var cache = LoadCache(cachePath, cacheKey);
var details = new PrDetails?[prs.Count];
int processed = 0, fromCache = 0, viaFileDiffs = 0, viaContent = 0, failed = 0;
bool fileDiffsUnavailable = false; // si la API FileDiffs no responde, se deja de intentar

await Parallel.ForEachAsync(Enumerable.Range(0, prs.Count),
    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, cfg.MaxConcurrency) },
    async (i, _) =>
    {
        var (project, pr) = prs[i];
        string key = $"{pr.Repository?.Id}/{pr.PullRequestId}";
        bool immutable = !string.Equals(pr.Status, "active", StringComparison.OrdinalIgnoreCase);

        if (immutable && cache.Items.TryGetValue(key, out var cached))
        {
            details[i] = cached;
            Interlocked.Increment(ref fromCache);
        }
        else
        {
            try
            {
                var d = await GetDetailsAsync(Uri.EscapeDataString(project), pr.Repository?.Id ?? "", pr.PullRequestId);
                details[i] = d;
                if (d.LinesAdded == null) Interlocked.Increment(ref failed);
                if (immutable) lock (cache) cache.Items[key] = d;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"  ⚠️ PR {pr.PullRequestId} ({pr.Repository?.Name}): {ex.Message}");
            }
        }

        int n = Interlocked.Increment(ref processed);
        if (n % 50 == 0 || n == prs.Count) Console.WriteLine($"  {n}/{prs.Count} PRs procesadas");
    });

SaveCache(cachePath, cache);

async Task<PrDetails> GetDetailsAsync(string projectEnc, string repoId, int prId)
{
    string repoBase = $"{projectEnc}/_apis/git/repositories/{repoId}";
    string prBase = $"{repoBase}/pullRequests/{prId}";

    // 4.1 Última iteración: commit de la rama origen y commit común con la rama destino
    var iterations = Items(await GetJsonAsync($"{prBase}/iterations?api-version=7.1"));
    var last = iterations.OrderByDescending(it => Int(it, "id") ?? 0).FirstOrDefault();
    if (last.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("la PR no tiene iteraciones");
    int iterationId = Int(last, "id") ?? 1;
    string? sourceCommit = Str(Prop(last, "sourceRefCommit"), "commitId");
    string? baseCommit = Str(Prop(last, "commonRefCommit"), "commitId");

    // 4.2 Archivos cambiados (comparando con el commit común: $compareTo=0)
    var files = new List<FileChange>();
    int skip = 0;
    while (true)
    {
        var page = await GetJsonAsync($"{prBase}/iterations/{iterationId}/changes?$top=2000&$skip={skip}&$compareTo=0&api-version=7.1");
        foreach (var c in Items(Prop(page, "changeEntries")))
        {
            var item = Prop(c, "item");
            string? path = Str(item, "path");
            if (path == null || Bool(item, "isFolder") == true || Str(item, "gitObjectType") == "tree") continue;
            string changeType = (Str(c, "changeType") ?? "").ToLowerInvariant();
            bool isAdd = changeType.Contains("add") && !changeType.Contains("rename");
            bool isDelete = changeType.Contains("delete");
            string originalPath = Str(c, "originalPath") ?? Str(c, "sourceServerItem") ?? path;
            if (IsExcluded(path)) continue;
            files.Add(new FileChange(path, originalPath, isAdd, isDelete));
        }
        int nextSkip = Int(page, "nextSkip") ?? 0;
        if (nextSkip <= 0 || nextSkip == skip) break;
        skip = nextSkip;
    }

    // 4.3 Líneas añadidas / borradas
    int? added = null, deleted = null;
    if (files.Count == 0) { added = 0; deleted = 0; }
    else if (sourceCommit != null && baseCommit != null)
    {
        var counted = await CountLinesAsync(repoBase, baseCommit, sourceCommit, files);
        if (counted != null) (added, deleted) = counted.Value;
    }

    // 4.4 Nº de commits
    int? commits = null;
    try { commits = Items(await GetJsonAsync($"{prBase}/commits?$top=1000&api-version=7.1")).Count; }
    catch (ApiException ex) { Console.Error.WriteLine($"  ⚠️ PR {prId}: no se pudieron leer los commits ({ex.Message})"); }

    return new PrDetails(commits, files.Count, added, deleted);
}

async Task<(int Added, int Deleted)?> CountLinesAsync(string repoBase, string baseCommit, string sourceCommit, List<FileChange> files)
{
    // Opción A: API FileDiffs (la usa la propia web de Azure DevOps; no figura en la documentación pública)
    if (!Volatile.Read(ref fileDiffsUnavailable))
    {
        try
        {
            int a = 0, d = 0;
            foreach (var batch in files.Chunk(Math.Max(1, cfg.FileDiffBatchSize)))
            {
                var body = new
                {
                    baseVersionCommit = baseCommit,
                    targetVersionCommit = sourceCommit,
                    fileDiffParams = batch.Select(f => new
                    {
                        path = f.IsDelete ? null : f.Path,
                        originalPath = f.IsAdd ? null : f.OriginalPath,
                    }).ToArray(),
                };
                foreach (var fileDiff in Items(await PostJsonAsync($"{repoBase}/FileDiffs?api-version=7.1", body)))
                    foreach (var block in Items(Prop(fileDiff, "lineDiffBlocks")))
                    {
                        string type = BlockType(Prop(block, "changeType"));
                        if (type is "add" or "edit") a += Int(block, "modifiedLinesCount") ?? 0;
                        if (type is "delete" or "edit") d += Int(block, "originalLinesCount") ?? 0;
                    }
            }
            Interlocked.Increment(ref viaFileDiffs);
            return (a, d);
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed)
        {
            if (!fileDiffsUnavailable)
                Console.Error.WriteLine($"  ℹ️ La API FileDiffs no está disponible ({ex.Message}); se cuentan líneas comparando el contenido de cada archivo.");
            Volatile.Write(ref fileDiffsUnavailable, true);
        }
    }

    // Opción B (documentada): descargar cada archivo antes y después y comparar sus líneas.
    // Cuenta líneas añadidas/borradas como diferencia de multiconjuntos: coincide con git salvo
    // en líneas que solo cambian de sitio (que aquí no cuentan).
    try
    {
        int a = 0, d = 0;
        foreach (var f in files)
        {
            string[] oldLines = f.IsAdd ? [] : await GetFileLinesAsync(repoBase, f.OriginalPath, baseCommit);
            string[] newLines = f.IsDelete ? [] : await GetFileLinesAsync(repoBase, f.Path, sourceCommit);
            var counts = new Dictionary<string, int>();
            foreach (var l in oldLines) counts[l] = counts.GetValueOrDefault(l) + 1;
            foreach (var l in newLines) counts[l] = counts.GetValueOrDefault(l) - 1;
            foreach (var v in counts.Values) { if (v > 0) d += v; else a -= v; }
        }
        Interlocked.Increment(ref viaContent);
        return (a, d);
    }
    catch (ApiException ex)
    {
        Console.Error.WriteLine($"  ⚠️ No se pudieron contar líneas: {ex.Message}");
        return null;
    }
}

async Task<string[]> GetFileLinesAsync(string repoBase, string path, string commit)
{
    string url = $"{repoBase}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.version={commit}" +
                 "&versionDescriptor.versionType=commit&includeContent=true&api-version=7.1";
    JsonElement item;
    try { item = await GetJsonAsync(url); }
    catch (ApiException ex) when (ex.Status == HttpStatusCode.NotFound) { return []; }
    if (Bool(Prop(item, "contentMetadata"), "isBinary") == true) return [];
    string content = (Str(item, "content") ?? "").Replace("\r\n", "\n");
    if (content.Length == 0) return [];
    var lines = content.Split('\n');
    return lines[^1] == "" ? lines[..^1] : lines; // un salto de línea final no es una línea más
}

bool IsExcluded(string path)
{
    string p = path.TrimStart('/');
    return excludeRegexes.Any(r => r.IsMatch(p));
}

// ==========================================
// 5. Escribir CSV
// ==========================================
using (var csv = new StreamWriter(cfg.Output, false, new UTF8Encoding(true)))
{
    csv.WriteLine("Project,Repository,PullRequestId,Title,Status,Author,TargetBranch,CreationDate,ClosedDate,HoursOpen,Url,CommitCount,FilesChanged,LinesAdded,LinesDeleted");
    for (int i = 0; i < prs.Count; i++)
    {
        var (project, pr) = prs[i];
        var d = details[i];
        string repoName = pr.Repository?.Name ?? "Desconocido";
        string targetBranch = pr.TargetRefName?.Replace("refs/heads/", "") ?? "";
        string prUrl = $"{cfg.BaseUrl.TrimEnd('/')}/{cfg.Organization}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repoName)}/pullrequest/{pr.PullRequestId}";
        string creationStr = pr.CreationDate.ToString("yyyy-MM-dd HH:mm:ss", inv);
        string closedStr = pr.ClosedDate?.ToString("yyyy-MM-dd HH:mm:ss", inv) ?? "";
        double hoursOpen = ((pr.ClosedDate ?? DateTime.UtcNow) - pr.CreationDate).TotalHours;

        csv.WriteLine(string.Join(",",
            EscapeCsv(project), EscapeCsv(repoName), pr.PullRequestId.ToString(inv), EscapeCsv(pr.Title ?? ""),
            EscapeCsv(pr.Status ?? ""), EscapeCsv(pr.CreatedBy?.DisplayName ?? "Desconocido"), EscapeCsv(targetBranch),
            creationStr, closedStr, hoursOpen.ToString("0.00", inv), EscapeCsv(prUrl),
            Num(d?.CommitCount), Num(d?.FilesChanged), Num(d?.LinesAdded), Num(d?.LinesDeleted)));
    }
}

Console.WriteLine($"""

    ✅ Exportadas {prs.Count} PRs a {cfg.Output}
       Desde caché: {fromCache} · Líneas vía FileDiffs: {viaFileDiffs} · vía comparación de contenido: {viaContent} · sin líneas: {failed}
    """);
return 0;

// ==========================================
// 6. Funciones auxiliares
// ==========================================
string Num(int? v) => v?.ToString(inv) ?? "";

static string EscapeCsv(string text)
{
    if (string.IsNullOrEmpty(text)) return "";
    if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        return $"\"{text.Replace("\"", "\"\"")}\"";
    return text;
}

// Glob estilo .gitignore: "**/" = cualquier carpeta, "*" = cualquier texto sin "/", "?" = un carácter.
// Un patrón sin "/" se aplica en cualquier carpeta.
static Regex GlobToRegex(string glob)
{
    string g = glob.Trim().Replace('\\', '/').TrimStart('/');
    if (!g.Contains('/')) g = "**/" + g;
    var sb = new StringBuilder("^");
    for (int i = 0; i < g.Length; i++)
    {
        char c = g[i];
        if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
        {
            bool slash = i + 2 < g.Length && g[i + 2] == '/';
            sb.Append(slash ? "(?:.*/)?" : ".*");
            i += slash ? 2 : 1;
        }
        else if (c == '*') sb.Append("[^/]*");
        else if (c == '?') sb.Append("[^/]");
        else sb.Append(Regex.Escape(c.ToString()));
    }
    return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

// Lectura tolerante de JSON (la respuesta puede ser un array o un objeto { count, value })
static List<JsonElement> Items(JsonElement e) =>
    e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList()
    : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList()
    : [];
static JsonElement Prop(JsonElement e, string name) =>
    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
static string? Str(JsonElement e, string name) =>
    Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
static int? Int(JsonElement e, string name) =>
    Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int n) ? n : null;
static bool? Bool(JsonElement e, string name) =>
    Prop(e, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;
// LineDiffBlockChangeType: puede venir como texto ("add") o como número (0 none, 1 add, 2 delete, 3 edit)
static string BlockType(JsonElement e) => e.ValueKind switch
{
    JsonValueKind.String => (e.GetString() ?? "").ToLowerInvariant(),
    JsonValueKind.Number => e.GetInt32() switch { 1 => "add", 2 => "delete", 3 => "edit", _ => "none" },
    _ => "none",
};

DetailsCache LoadCache(string path, string key)
{
    try
    {
        if (File.Exists(path))
        {
            var c = JsonSerializer.Deserialize<DetailsCache>(File.ReadAllText(path), jsonOptions);
            if (c != null && c.ExcludeKey == key) return c;
            Console.WriteLine("ℹ️ Han cambiado las exclusiones: se recalculan todas las PRs.");
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"⚠️ Caché ilegible, se ignora ({ex.Message})."); }
    return new DetailsCache { ExcludeKey = key };
}

void SaveCache(string path, DetailsCache c)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(c));
}

// ==========================================
// 7. Modelos
// ==========================================
public sealed class ExportConfig
{
    public string Organization { get; set; } = "";
    public string[] Projects { get; set; } = [];
    public string[] Authors { get; set; } = [];
    public int YearsBack { get; set; } = 2;
    public string Status { get; set; } = "all";
    public string Output { get; set; } = "PullRequests_Metrics.csv";
    public int MaxConcurrency { get; set; } = 4;
    public int FileDiffBatchSize { get; set; } = 20;
    public string[] ExcludePatterns { get; set; } = [];
    public string BaseUrl { get; set; } = "https://dev.azure.com";
}

public sealed class DetailsCache
{
    public string ExcludeKey { get; set; } = "";
    public Dictionary<string, PrDetails> Items { get; set; } = new();
}

public record PrDetails(int? CommitCount, int? FilesChanged, int? LinesAdded, int? LinesDeleted);
public record FileChange(string Path, string OriginalPath, bool IsAdd, bool IsDelete);

public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public record AdoResponse(int Count, List<PullRequest> Value);
public record PullRequest(
    string Status,
    int PullRequestId,
    string Title,
    string TargetRefName,
    DateTime CreationDate,
    DateTime? ClosedDate,
    IdentityRef CreatedBy,
    RepositoryRef Repository
);
public record IdentityRef(string DisplayName, string UniqueName);
public record RepositoryRef(string Id, string Name);
