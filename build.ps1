$version = "0.2.0"
$publishDir = "artifacts\OpenGepa-$version-win-x64"
$zipPath = "artifacts\OpenGepa-$version-win-x64.zip"

Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

dotnet publish OpenGepa\OpenGepa.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o $publishDir

Compress-Archive -Path "$publishDir\*" `
  -DestinationPath $zipPath `
  -CompressionLevel Optimal