$version = "0.4.2"
$publishDir = "artifacts\OpenGepa-$version-win-x64-self-contained"
$zipPath = "artifacts\OpenGepa-$version-win-x64-self-contained.zip"

Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

dotnet publish OpenGepa\OpenGepa.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $publishDir

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish OpenGepa.Mcp\OpenGepa.Mcp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $publishDir

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Path "$publishDir\iconSet" -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $publishDir "iconSet\README.txt"), "利用者が選んだOpenGepaアイコンセットを配置するディレクトリです。`r`n", [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath README.md -Destination "$publishDir\README.md"
Copy-Item -LiteralPath RELEASE_NOTES.md -Destination "$publishDir\RELEASE_NOTES.md"

$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
foreach ($licenseName in @("LICENSE.txt", "ThirdPartyNotices.txt")) {
  $source = Join-Path $dotnetRoot $licenseName
  if (-not (Test-Path -LiteralPath $source)) { throw ".NETライセンス文書が見つかりません: $source" }
  Copy-Item -LiteralPath $source -Destination (Join-Path $publishDir $licenseName)
}

$readmePath = Join-Path $publishDir "README.md"
$readme = [System.IO.File]::ReadAllText($readmePath)
$runtimeRequirement = "- [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) がインストール済みであること"
$frameworkDescription = "配布物はフレームワーク依存型です。ZIPに.NETランタイムは含まれません。"
if (-not $readme.Contains($runtimeRequirement) -or -not $readme.Contains($frameworkDescription)) { throw "READMEの自己完結版向け案内を作成できません。" }
$readme = $readme.Replace($runtimeRequirement, "- .NET 10 Windows Desktop Runtime x64の事前インストールは不要（自己完結版）")
$readme = $readme.Replace($frameworkDescription, "このZIPは自己完結型です。.NETランタイムを同梱しているため、展開したフォルダだけで実行できます。")
[System.IO.File]::WriteAllText($readmePath, $readme, [System.Text.UTF8Encoding]::new($false))

Compress-Archive -Path "$publishDir\*" `
  -DestinationPath $zipPath `
  -CompressionLevel Optimal

$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $zipPath))
try {
  $names = @($archive.Entries | ForEach-Object FullName)
  $required = @(
    "OpenGepa.exe",
    "OpenGepa.dll",
    "OpenGepa.deps.json",
    "OpenGepa.runtimeconfig.json",
    "OpenGepa.Mcp.exe",
    "OpenGepa.Mcp.dll",
    "OpenGepa.Mcp.deps.json",
    "OpenGepa.Mcp.runtimeconfig.json",
    "opengepa.default.json",
    "LICENSE.txt",
    "ThirdPartyNotices.txt",
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll"
  )
  $missing = @($required | Where-Object { $_ -notin $names })
  if ($missing.Count -gt 0) { throw "自己完結版に必要なファイルがありません: $($missing -join ', ')" }
}
finally { $archive.Dispose() }

Write-Output "自己完結版を生成しました: $zipPath"
Write-Output "検証済み: OpenGepa.exe、OpenGepa.Mcp.exe、および.NET実行ランタイムをZIPへ同梱しています。"
