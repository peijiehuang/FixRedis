$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& dotnet build (Join-Path $projectRoot 'FixRedis.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw '构建失败，未运行真实服务测试。' }
$testExe = Join-Path $projectRoot 'tests\FixRedis.Tests\bin\Release\net8.0-windows\FixRedis.Tests.exe'
$reportRoot = Join-Path $projectRoot 'artifacts\service-tests'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
Write-Output '即将测试本机 Redis 服务：完整备份后停服注入故障，结束后恢复原配置及数据。'
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    & $testExe --local-service $reportRoot
    if ($LASTEXITCODE -ne 0) { throw "实机测试或恢复失败，请查看 $reportRoot 中最新报告。" }
} else {
    $process = Start-Process -FilePath $testExe -ArgumentList @('--local-service', ('"' + $reportRoot + '"')) -Verb RunAs -WindowStyle Hidden -WorkingDirectory $projectRoot -PassThru
    Write-Output "管理员测试进程 PID=$($process.Id)，报告持续写入 $reportRoot。"
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "实机测试或恢复失败，请查看 $reportRoot 中最新报告。" }
}
Write-Output "实机测试通过，原服务恢复成功。报告：$reportRoot"
