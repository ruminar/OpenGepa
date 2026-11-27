using System.Net;
using System.Text.Json;

namespace OpenGepa.Update;

public sealed record UpdateAsset(string Name, long Size, string? Digest);
public sealed record UpdateCandidate(string Version, string Tag, string Notes, UpdateAsset Zip, UpdateAsset Manifest, UpdateAsset Signatures);

public sealed class GitHubUpdates(UpdateProfile profile) : IDisposable
{
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public async Task<UpdateCandidate?> CheckAsync(Version current, CancellationToken token)
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenGepa-Updater/1.0");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _client.GetAsync($"https://api.github.com/repos/{profile.Repository}/releases/latest", timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token); if (bytes.Length > 1024 * 1024) throw new InvalidDataException("更新情報が大きすぎます。");
        using var doc = JsonDocument.Parse(bytes); var item = doc.RootElement;
        if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) return null;
        var tag = item.GetProperty("tag_name").GetString() ?? ""; var version = tag.StartsWith('v') ? tag[1..] : tag;
        if (UpdatePackage.ParseVersion(version) is not { } next || next <= current) return null;
        var assets = item.GetProperty("assets").EnumerateArray().ToArray(); var notes = item.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new(version, tag, notes.Length > 2500 ? notes[..2500] + "…" : notes, Find(assets, profile.AssetName(version), UpdatePackage.MaximumZipBytes), Find(assets, UpdatePackage.ManifestName, 1024 * 1024), Find(assets, UpdatePackage.SignatureName, 1024 * 1024));
    }
    public async Task StageAsync(UpdateCandidate candidate, UpdateLayout layout, CancellationToken token)
    {
        var work = layout.NewWorkDirectory();
        try
        {
            var manifest = await DownloadAsync(candidate.Tag, candidate.Manifest, token); var signatures = await DownloadAsync(candidate.Tag, candidate.Signatures, token); var verified = UpdatePackage.ReadManifest(manifest, signatures, profile, candidate.Version);
            if (candidate.Zip.Size != verified.Size || !Digest(candidate.Zip.Digest, verified.Sha256)) throw new InvalidDataException("GitHub Releasesと署名済みmanifestが一致しません。");
            var zip = await DownloadAsync(candidate.Tag, candidate.Zip, token); using (var file = new FileStream(Path.Combine(work, "package.zip"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(zip); file.Flush(true); }
            using (var file = File.OpenRead(Path.Combine(work, "package.zip"))) UpdatePackage.VerifyZip(file, verified);
            File.WriteAllBytes(Path.Combine(work, UpdatePackage.ManifestName), manifest); File.WriteAllBytes(Path.Combine(work, UpdatePackage.SignatureName), signatures); layout.WritePending(new(candidate.Version, Path.GetFileName(work)));
        }
        catch { try { layout.DeleteWorkDirectory(work); } catch { } throw; }
    }
    private static UpdateAsset Find(JsonElement[] assets, string name, long limit) { var match = assets.Where(x => x.GetProperty("name").GetString() == name).ToArray(); if (match is not [var item] || item.GetProperty("state").GetString() != "uploaded" || !item.GetProperty("size").TryGetInt64(out var size) || size <= 0 || size > limit) throw new InvalidDataException($"配布物を確認できません。{name}"); return new(name, size, item.TryGetProperty("digest", out var digest) ? digest.GetString() : null); }
    private async Task<byte[]> DownloadAsync(string tag, UpdateAsset asset, CancellationToken token)
    {
        var uri = new Uri($"https://github.com/{profile.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset.Name)}");
        for (var attempts = 0; attempts < 5; attempts++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("配布ファイルのリダイレクト先が許可されていません。");
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode) { if (response.Content.Headers.ContentLength != asset.Size) throw new InvalidDataException("配布ファイルのサイズが不正です。"); var bytes = await response.Content.ReadAsByteArrayAsync(token); if (bytes.LongLength != asset.Size || !Digest(asset.Digest, UpdatePackage.Hash(new MemoryStream(bytes)))) throw new InvalidDataException("配布物のハッシュが一致しません。"); return bytes; }
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location) throw new InvalidDataException("配布ファイルを取得できません。");
            uri = new Uri(uri, location);
        }
        throw new InvalidDataException("配布ファイルのリダイレクト回数が上限を超えました。");
    }
    private static bool Digest(string? expected, string actual) => expected is null || expected.Equals("sha256:" + actual, StringComparison.OrdinalIgnoreCase);
    public void Dispose() => _client.Dispose();
}
