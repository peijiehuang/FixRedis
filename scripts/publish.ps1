$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src\FixRedis.WinForms\FixRedis.WinForms.csproj'
$publishPath = Join-Path $projectRoot 'artifacts\publish\win-x64'
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $publishPath
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\recovery.md') -Destination (Join-Path $publishPath '使用说明.md')
[xml]$versionProps = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$version = $versionProps.Project.PropertyGroup.Version
$releasePath = Join-Path $projectRoot 'artifacts\release'
New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishPath 'FixRedis.WinForms.exe') -Destination (Join-Path $releasePath "FixRedis-v$version-win-x64.exe")
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\recovery.md') -Destination (Join-Path $releasePath '修复与备份说明.md')
Write-Output "发布完成：$publishPath"
