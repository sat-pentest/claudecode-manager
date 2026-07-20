# publish single-file self-contained EXE
$ErrorActionPreference = "Stop"
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$out = Join-Path $root "dist"
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
dotnet publish "src/ClaudeCodeManager.App/ClaudeCodeManager.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out `
    --nologo
$exe = Join-Path $out "ClaudeCodeManager.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Output ""
    Write-Output ("[OK] " + $exe + "  (" + $size + " MB)")
} else {
    Write-Output "[FAIL] EXE not produced"
    exit 1
}
