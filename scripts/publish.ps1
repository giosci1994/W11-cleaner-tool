# Costruisce gli exe x64 e ARM64 e scrive SHA256SUMS.txt.
# Lo usano i workflow di GitHub Actions; si può lanciare anche a mano su Windows con .NET 10 SDK.
param(
    [string]$Version = "",   # versione completa, per le release (es. 0.1.0)
    [string]$Suffix = "",    # suffisso, per le build normali (es. ci.12)
    [string]$OutDir = "artifacts"
)

$ErrorActionPreference = "Stop"
$project = "src/W11CleanerTool/W11CleanerTool.csproj"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }
elseif ($Suffix) { $versionArgs += "-p:VersionSuffix=$Suffix" }

foreach ($arch in @("x64", "arm64")) {
    $publishDir = "publish/win-$arch"
    dotnet publish $project -c Release -r "win-$arch" --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        @versionArgs `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish win-$arch non riuscito" }
    Copy-Item "$publishDir/W11CleanerTool.exe" "$OutDir/W11-cleaner-tool-$arch.exe" -Force
}

Copy-Item "scripts/VERIFY.ps1" "$OutDir/VERIFY.ps1" -Force

$sums = Join-Path $OutDir "SHA256SUMS.txt"
Get-ChildItem $OutDir -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content -Path $sums -Encoding ascii

Get-Content $sums
Get-ChildItem $OutDir -File | ForEach-Object { "{0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
