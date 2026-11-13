using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Security;

namespace Rbm.Gaming.PresentMon;

/// <summary>Asset de uma release do GitHub.</summary>
/// <param name="Sha256">Digest "sha256:..." informado pela API do GitHub (hex), quando disponível.</param>
public sealed record GitHubAsset(string Name, string DownloadUrl, long Size, string? Sha256);

public sealed record GitHubRelease(string TagName, string? Name, DateTimeOffset? PublishedAt, bool Prerelease, IReadOnlyList<GitHubAsset> Assets);

/// <summary>Resultado da instalação do PresentMon.</summary>
public sealed record PresentMonInstallResult(string Path, string Version, string Sha256, string? Signer, string SourceUrl, string ReleaseTag, bool AlreadyInstalled);

/// <summary>
/// Baixa a última release oficial do PresentMon (GitHub GameTechDev/PresentMon, endpoint releases/latest),
/// escolhe o executável console x64 ("PresentMon-X.Y.Z-x64.exe"), salva em RbmPaths.Tools e só aceita o arquivo se:
/// tamanho bater, SHA-256 bater com o digest da API (quando informado), e a assinatura Authenticode for válida
/// com assinante contendo "Intel". Caso contrário o arquivo é apagado. O SHA-256 é registrado em presentmon.json.
/// Operação SEM elevação (grava apenas no perfil do usuário).
/// </summary>
public sealed partial class PresentMonDownloader
{
    public const string LatestReleaseApi = "https://api.github.com/repos/GameTechDev/PresentMon/releases/latest";
    public const string AllowedDownloadPrefix = "https://github.com/GameTechDev/PresentMon/releases/download/";
    public const long MaxAssetBytes = 64L * 1024 * 1024;

    [GeneratedRegex(@"^PresentMon-(\d+(?:\.\d+){1,3})-x64\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleAssetName();

    private readonly HttpClient _http;
    private readonly string _toolsDir;

    public PresentMonDownloader(HttpClient http, string? toolsDirectory = null)
    {
        _http = http;
        _toolsDir = toolsDirectory ?? RbmPaths.Tools;
    }

    /// <summary>Faz o parse do JSON da API de releases (função pura).</summary>
    public static GitHubRelease ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string tag = r.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : throw new FormatException("Release sem tag_name.");
        string? name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        DateTimeOffset? pub = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        bool pre = r.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
        var assets = new List<GitHubAsset>();
        if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                string? an = a.TryGetProperty("name", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                string? digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToUpperInvariant() : null;
                if (an is not null && url is not null) assets.Add(new GitHubAsset(an, url, size, sha));
            }
        return new GitHubRelease(tag, name, pub, pre, assets);
    }

    /// <summary>Escolhe o asset console x64 (ignora .msi, símbolos, serviço). Função pura.</summary>
    public static GitHubAsset? SelectConsoleAsset(GitHubRelease release) =>
        release.Assets.Where(a => ConsoleAssetName().IsMatch(a.Name) && IsAllowedDownloadUrl(a.DownloadUrl))
            .OrderByDescending(a => Version.TryParse(ConsoleAssetName().Match(a.Name).Groups[1].Value, out var v) ? v : new Version(0, 0))
            .FirstOrDefault();

    /// <summary>Só aceita URLs https do repositório oficial.</summary>
    public static bool IsAllowedDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        url.StartsWith(AllowedDownloadPrefix, StringComparison.Ordinal) && !u.AbsolutePath.Contains("..");

    private static bool IsAllowedRedirectHost(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Consulta a última release (sem baixar).</summary>
    public async Task<OperationResult<GitHubRelease>> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
                AddHeaders(req);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                return req;
            }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return OperationResult<GitHubRelease>.Fail($"GitHub respondeu {(int)resp.StatusCode} ({resp.ReasonPhrase}). Tente novamente mais tarde (limite de requisições da API é 60/h sem login).", "http_error");
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return OperationResult<GitHubRelease>.Ok(ParseRelease(json));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
        { return OperationResult<GitHubRelease>.Fail($"Falha ao consultar o GitHub: {ex.Message}", "network_error"); }
    }

    /// <summary>Baixa, verifica e instala a última versão do PresentMon console x64.</summary>
    public async Task<OperationResult<PresentMonInstallResult>> DownloadLatestAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var rel = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
        if (!rel.Success) return OperationResult<PresentMonInstallResult>.Fail(rel.Message, rel.ErrorCode);
        var release = rel.Value!;
        var asset = SelectConsoleAsset(release);
        if (asset is null) return OperationResult<PresentMonInstallResult>.Fail($"A release {release.TagName} não tem o executável console x64 esperado.", "asset_not_found");
        if (asset.Size > MaxAssetBytes) return OperationResult<PresentMonInstallResult>.Fail("Asset maior que o limite de segurança.", "too_large");
        string version = ConsoleAssetName().Match(asset.Name).Groups[1].Value;

        Directory.CreateDirectory(_toolsDir);
        string finalPath = Path.Combine(_toolsDir, asset.Name);
        if (File.Exists(finalPath))
        {
            var existing = PresentMonLocator.Inspect(finalPath, _toolsDir);
            if (existing.Success && existing.Value!.IsTrusted && (asset.Sha256 is null || string.Equals(asset.Sha256, existing.Value.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                WriteRecord(asset, release, version, existing.Value.Sha256, existing.Value.Signature.Signer);
                return OperationResult<PresentMonInstallResult>.Ok(new PresentMonInstallResult(finalPath, version, existing.Value.Sha256, existing.Value.Signature.Signer, asset.DownloadUrl, release.TagName, true),
                    "PresentMon já está instalado e verificado.");
            }
        }

        string tmp = Path.Combine(_toolsDir, $".download-{Guid.NewGuid():N}.exe");
        try
        {
            {
                using var resp = await SendWithRetryAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
                    AddHeaders(req);
                    return req;
                }, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return OperationResult<PresentMonInstallResult>.Fail($"Download falhou: HTTP {(int)resp.StatusCode}.", "http_error");
                if (resp.RequestMessage?.RequestUri is { } finalUri && !IsAllowedRedirectHost(finalUri))
                    return OperationResult<PresentMonInstallResult>.Fail($"Redirecionamento para host inesperado ({finalUri.Host}); download recusado.", "bad_redirect");
                long? len = resp.Content.Headers.ContentLength;
                if (len > MaxAssetBytes) return OperationResult<PresentMonInstallResult>.Fail("Arquivo maior que o limite de segurança.", "too_large");

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buf = new byte[81920];
                long total = 0; int read;
                while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxAssetBytes) return OperationResult<PresentMonInstallResult>.Fail("Arquivo maior que o limite de segurança.", "too_large");
                    await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                    long expected = asset.Size > 0 ? asset.Size : len ?? 0;
                    if (expected > 0) progress?.Report(Math.Min(1.0, (double)total / expected));
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
                if (asset.Size > 0 && total != asset.Size)
                    return OperationResult<PresentMonInstallResult>.Fail($"Tamanho divergente ({total} bytes, esperado {asset.Size}).", "size_mismatch");
            }

            string sha;
            await using (var fs = File.OpenRead(tmp)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (asset.Sha256 is not null && !string.Equals(sha, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                return OperationResult<PresentMonInstallResult>.Fail("SHA-256 não confere com o informado pelo GitHub; arquivo descartado.", "hash_mismatch");

            var sig = SignatureVerifier.Verify(tmp);
            if (!PresentMonLocator.IsTrustedSignature(sig))
                return OperationResult<PresentMonInstallResult>.Fail($"Assinatura digital rejeitada ({sig.Display}); o arquivo foi descartado. Esperado: assinatura válida da Intel.", "bad_signature");

            File.Move(tmp, finalPath, overwrite: true);
            WriteRecord(asset, release, version, sha, sig.Signer);
            progress?.Report(1.0);
            return OperationResult<PresentMonInstallResult>.Ok(new PresentMonInstallResult(finalPath, version, sha, sig.Signer, asset.DownloadUrl, release.TagName, false),
                $"PresentMon {version} instalado e verificado (assinado por {sig.Signer}).");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        { return OperationResult<PresentMonInstallResult>.Fail($"Falha no download: {ex.Message}", "io_error"); }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void WriteRecord(GitHubAsset asset, GitHubRelease release, string version, string sha, string? signer)
    {
        var rec = new PresentMonInstallRecord(asset.Name, version, sha, signer, asset.DownloadUrl, release.TagName, DateTimeOffset.Now);
        string path = Path.Combine(_toolsDir, PresentMonLocator.InstallRecordFileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rec, PresentMonLocator.AgentJsonLike));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Envia com até 3 tentativas para falhas transitórias (5xx, 408, 429, erro de rede).</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> make, HttpCompletionOption completion, CancellationToken ct)
    {
        const int attempts = 3;
        for (int i = 1; ; i++)
        {
            using var req = make();
            HttpResponseMessage? resp = null;
            try
            {
                resp = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                bool transient = code >= 500 || code is 408 or 429;
                if (!transient || i == attempts) return resp;
            }
            catch (HttpRequestException) when (i < attempts) { }
            resp?.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(i * 2), ct).ConfigureAwait(false);
        }
    }

    private static void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("RBMOptimizer", "0.1"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }
}
