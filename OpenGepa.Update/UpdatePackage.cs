using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenGepa.Update;

public sealed record UpdateProfile(string Repository, string AssetPrefix, Dictionary<string, string> PublicKeys)
{
    public static UpdateProfile? Load(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream("OpenGepa.UpdateProfile.json");
        return stream is null ? null : JsonSerializer.Deserialize<UpdateProfile>(stream, UpdatePackage.Json);
    }
    public string AssetName(string version) => $"{AssetPrefix}-{version}-win-x64.zip";
}

public sealed record UpdateManifest(string Product, string Repository, string Version, string Runtime, string Asset, long Size, string Sha256);
public sealed record UpdateSignature(string Key, string Signature);

public static partial class UpdatePackage
{
    public const string ManifestName = "update-manifest.json";
    public const string SignatureName = "update-signatures.json";
    public const long MaximumZipBytes = 256L * 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
    [GeneratedRegex(@"\A(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\z")]
    private static partial Regex StableVersion();
    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();

    public static Version? ParseVersion(string? value) => value is not null && StableVersion().IsMatch(value) && Version.TryParse(value, out var version) ? version : null;
    public static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    public static string HashFile(string path) { using var stream = File.OpenRead(path); return Hash(stream); }

    public static UpdateManifest ReadManifest(byte[] bytes, byte[] signatures, UpdateProfile profile, string expectedVersion)
    {
        var proofs = JsonSerializer.Deserialize<UpdateSignature[]>(signatures, Json) ?? [];
        if (proofs.Length > 8 || !proofs.Any(proof => IsTrusted(proof, bytes, profile))) throw new InvalidDataException("更新マニフェストの署名を検証できません。");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json);
        if (manifest is not { Product: "OpenGepa", Runtime: "win-x64", Size: > 0 and <= MaximumZipBytes } ||
            ParseVersion(manifest.Version) is null || manifest.Version != expectedVersion ||
            !manifest.Repository.Equals(profile.Repository, StringComparison.OrdinalIgnoreCase) || manifest.Asset != profile.AssetName(expectedVersion) ||
            manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新マニフェストと配布対象が一致しません。");
        return manifest;
    }

    private static bool IsTrusted(UpdateSignature? proof, byte[] bytes, UpdateProfile profile)
    {
        if (proof?.Key is null || proof.Signature is null || !profile.PublicKeys.TryGetValue(proof.Key, out var encoded)) return false;
        try
        {
            using var rsa = RSA.Create(); var key = Convert.FromBase64String(encoded); rsa.ImportSubjectPublicKeyInfo(key, out var read);
            return read == key.Length && rsa.KeySize >= 2048 && rsa.VerifyData(bytes, Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    public static void VerifyZip(Stream stream, UpdateManifest manifest)
    {
        if (stream.Length != manifest.Size || !Hash(stream).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新ZIPのサイズまたはSHA-256が一致しません。");
        stream.Position = 0;
    }

    public static void Extract(Stream stream, string destination, string version)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
        if (zip.Entries.Count is < 1 or > 4096) throw new InvalidDataException("更新ZIPの項目数が不正です。");
        var prefix = $"app/versions/{version}/";
        var required = new HashSet<string>(StringComparer.Ordinal) { "OpenGepa.exe", "OpenGepa.Mcp.exe", "README.md", "RELEASE_NOTES.md", prefix + "OpenGepa.exe", prefix + "OpenGepa.dll", prefix + "OpenGepa.deps.json", prefix + "OpenGepa.runtimeconfig.json", prefix + "OpenGepa.Mcp.exe", prefix + "OpenGepa.Mcp.dll", prefix + "OpenGepa.Mcp.deps.json", prefix + "OpenGepa.Mcp.runtimeconfig.json", prefix + "opengepa.default.json", prefix + ".package-files.json" };
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName; var directory = name.EndsWith('/'); var relative = directory ? name[..^1] : name;
            ValidateEntry(entry, relative);
            var allowedDirectory = directory && (name == "app/" || name == "app/versions/" || name == prefix);
            var allowedFile = !directory && (required.Contains(name) || name.StartsWith(prefix, StringComparison.Ordinal));
            if (!allowedDirectory && !allowedFile) throw new InvalidDataException($"更新ZIPに想定外のファイルがあります。{name}");
            if (name.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || (name.StartsWith(prefix, StringComparison.Ordinal) && relative[prefix.Length..].Split('/').Any(part => part is "icon" or "iconSet" or "shortcut" || part.StartsWith("opengepa", StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException($"更新ZIPに利用者データが含まれています。{name}");
            if (directory) continue;
            if (entry.Length < 0 || entry.Length > MaximumZipBytes - total) throw new InvalidDataException("更新ZIPの展開サイズが上限を超えています。"); total += entry.Length;
            var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); input.CopyTo(output); output.Flush(); required.Remove(name);
        }
        if (required.Count > 0) throw new InvalidDataException("更新ZIPに必要なファイルがありません。");
    }

    private static void ValidateEntry(ZipArchiveEntry entry, string relative)
    {
        var type = (entry.ExternalAttributes >> 16) & 0xf000;
        if (relative.Length > 200 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || type is not (0 or 0x4000 or 0x8000) || relative.Split('/').Any(part => part.Length == 0 || part.Length > 120 || part.EndsWith('.') || part.EndsWith(' ') || ReservedName().IsMatch(part) || part.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c)))) throw new InvalidDataException($"更新ZIPに使用できないパスがあります。{entry.FullName}");
    }
}
