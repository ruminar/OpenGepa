[CmdletBinding()]
param(
  [ValidateSet('Local', 'Release')][string]$Distribution = 'Local',
  [string]$SigningCertificate,
  [string]$PackageVersion,
  [string]$UpdateRepository = 'ruminar/OpenGepa',
  [string]$AssetPrefix = 'OpenGepa'
)
$ErrorActionPreference = 'Stop'
$runtime = 'win-x64'; $project = Join-Path $PSScriptRoot 'OpenGepa\OpenGepa.csproj'
$sourceVersion = dotnet msbuild $project -nologo '-getProperty:Version' '-p:Configuration=Release' "-p:RuntimeIdentifier=$runtime"
if ($LASTEXITCODE -ne 0 -or $sourceVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'OpenGepaのVersionを取得できません。' }
$version = if ($PackageVersion) { $PackageVersion } else { $sourceVersion }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '-PackageVersionはmajor.minor.patch形式で指定してください。' }
if ($UpdateRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw '-UpdateRepositoryはowner/repository形式で指定してください。' }
if ($AssetPrefix -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$') { throw '-AssetPrefixに使用できない文字があります。' }
$assetBase = "$AssetPrefix-$version-win-x64"; $artifacts = Join-Path $PSScriptRoot 'artifacts'; $publishDir = Join-Path $artifacts $assetBase; $zipPath = Join-Path $artifacts "$assetBase.zip"; $staging = Join-Path $artifacts ('.build-' + [guid]::NewGuid().ToString('N')); $profilePath = $null; $signer = $null
try {
  New-Item -ItemType Directory -Path $staging -Force | Out-Null
  if ($Distribution -eq 'Release') {
    if (!$SigningCertificate) { throw 'Releaseでは-SigningCertificateにCurrentUser/Myの証明書拇印を指定してください。' }
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser'); try { $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly); $found = $store.Certificates.Find('FindByThumbprint', $SigningCertificate, $false); if ($found.Count -ne 1) { throw '更新署名証明書がCurrentUser/Myにありません。' }; $signer = $found[0] } finally { $store.Dispose() }
    $private = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($signer); $public = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($signer)
    try { if (!$private -or $private.KeySize -lt 2048) { throw '2048bit以上のRSA秘密鍵が必要です。' }; $key = $public.ExportSubjectPublicKeyInfo(); $keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($key)).ToLowerInvariant(); $profilePath = Join-Path $staging 'update-profile.json'; [IO.File]::WriteAllText($profilePath, (ConvertTo-Json ([ordered]@{ repository = $UpdateRepository; assetPrefix = $AssetPrefix; publicKeys = [ordered]@{ $keyId = [Convert]::ToBase64String($key) } }) -Compress), [Text.UTF8Encoding]::new($false)) } finally { $private.Dispose(); $public.Dispose() }
  }
  Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
  $appOut = Join-Path $publishDir "app\versions\$version"; $publish = @('-c','Release','-r',$runtime,'--self-contained','false','-p:DebugType=None','-p:DebugSymbols=false',"-p:Version=$version","-p:AssemblyVersion=$version.0","-p:FileVersion=$version.0"); if ($profilePath) { $publish += "-p:UpdateProfileFile=$profilePath" }
  dotnet publish OpenGepa\OpenGepa.csproj @publish -o $appOut; if ($LASTEXITCODE -ne 0) { throw 'OpenGepaの発行に失敗しました。' }
  dotnet publish OpenGepa.Mcp\OpenGepa.Mcp.csproj @publish -o $appOut; if ($LASTEXITCODE -ne 0) { throw 'OpenGepa.Mcpの発行に失敗しました。' }
  dotnet publish OpenGepa.Launcher\OpenGepa.Launcher.csproj @publish -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -o (Join-Path $staging 'launcher'); if ($LASTEXITCODE -ne 0) { throw '起動窓口の発行に失敗しました。' }
  dotnet publish OpenGepa.Mcp.Launcher\OpenGepa.Mcp.Launcher.csproj @publish -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -o (Join-Path $staging 'mcp-launcher'); if ($LASTEXITCODE -ne 0) { throw 'MCP窓口の発行に失敗しました。' }
  Copy-Item -LiteralPath (Join-Path $staging 'launcher\OpenGepa.exe') -Destination $publishDir; Copy-Item -LiteralPath (Join-Path $staging 'mcp-launcher\OpenGepa.Mcp.exe') -Destination $publishDir
  $inventory = @(Get-ChildItem -LiteralPath $appOut -File -Recurse | ForEach-Object { $_.FullName.Substring($appOut.Length + 1).Replace('\','/') }); [IO.File]::WriteAllText((Join-Path $appOut '.package-files.json'), (ConvertTo-Json $inventory -Compress), [Text.UTF8Encoding]::new($false))
  Copy-Item README.md (Join-Path $publishDir 'README.md'); Copy-Item RELEASE_NOTES.md (Join-Path $publishDir 'RELEASE_NOTES.md'); Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
  if ($Distribution -eq 'Release') {
    $releaseDir = if ($AssetPrefix -eq 'OpenGepa') { Join-Path $artifacts "release\$version" } else { Join-Path $artifacts "release\$AssetPrefix\$version" }; Remove-Item -LiteralPath $releaseDir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null; $asset = Split-Path -Leaf $zipPath; Copy-Item $zipPath (Join-Path $releaseDir $asset)
    $manifestPath = Join-Path $releaseDir 'update-manifest.json'; $manifest = [ordered]@{ product = 'OpenGepa'; repository = $UpdateRepository; version = $version; runtime = $runtime; asset = $asset; size = (Get-Item $zipPath).Length; sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() }; [IO.File]::WriteAllText($manifestPath, (ConvertTo-Json $manifest -Compress), [Text.UTF8Encoding]::new($false))
    $private = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($signer); $public = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($signer); try { $keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($public.ExportSubjectPublicKeyInfo())).ToLowerInvariant(); $signature = [Convert]::ToBase64String($private.SignData([IO.File]::ReadAllBytes($manifestPath), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)); [IO.File]::WriteAllText((Join-Path $releaseDir 'update-signatures.json'), (ConvertTo-Json @([ordered]@{ key = $keyId; signature = $signature }) -Compress), [Text.UTF8Encoding]::new($false)) } finally { $private.Dispose(); $public.Dispose() }
  }
  Write-Output "Package validated: $zipPath"
} finally { if ($signer) { $signer.Dispose() }; if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force } }
