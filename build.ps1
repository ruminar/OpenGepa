$version = "0.4.1"
$publishDir = "artifacts\OpenGepa-$version-win-x64"
$zipPath = "artifacts\OpenGepa-$version-win-x64.zip"

Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

dotnet publish OpenGepa\OpenGepa.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $publishDir

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish OpenGepa.Mcp\OpenGepa.Mcp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $publishDir

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Path "$publishDir\iconSet" -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $publishDir "iconSet\README.txt"), "利用者が選んだOpenGepaアイコンセットを配置するディレクトリです。`r`n", [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath README.md -Destination "$publishDir\README.md"
Copy-Item -LiteralPath RELEASE_NOTES.md -Destination "$publishDir\RELEASE_NOTES.md"

Compress-Archive -Path "$publishDir\*" `
  -DestinationPath $zipPath `
  -CompressionLevel Optimal
