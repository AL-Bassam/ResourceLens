# Builds dist\ResourceLens-Setup.exe: the trimmed self-contained app, gzip-compressed inside a small
# .NET Framework 4.8 installer (Framework 4.8 ships with Windows 10/11, so the setup needs nothing installed).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content ResourceLens.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force dist\payload | Out-Null

dotnet publish ResourceLens.csproj -c Release -o publish -nologo -v q
if ($LASTEXITCODE) { throw 'app publish failed' }

$exe = Resolve-Path publish\ResourceLens.exe
$gz = Join-Path $PSScriptRoot 'dist\payload\ResourceLens.exe.gz'
$in = [IO.File]::OpenRead($exe)
$out = [IO.File]::Create($gz)
$z = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
$in.CopyTo($z); $z.Dispose(); $out.Dispose(); $in.Dispose()

dotnet build setup\Setup.csproj -c Release -nologo -v q "-p:Version=$version" "-p:PayloadPath=$gz" -o dist\setup-build
if ($LASTEXITCODE) { throw 'setup build failed' }
Copy-Item dist\setup-build\ResourceLens-Setup.exe dist\ResourceLens-Setup.exe -Force

'app      {0,6:0.0} MB  publish\ResourceLens.exe' -f ((Get-Item $exe).Length / 1MB)
'setup    {0,6:0.0} MB  dist\ResourceLens-Setup.exe (v{1})' -f ((Get-Item dist\ResourceLens-Setup.exe).Length / 1MB), $version
