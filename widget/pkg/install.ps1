# 小组件包安装脚本（需管理员：导入签名证书到本机受信任存储后安装 MSIX）
$ErrorActionPreference = "Continue"
Start-Transcript -Path "$PSScriptRoot\install-result.log" -Force

Write-Output "=== 1. 导入签名证书到 LocalMachine TrustedPeople ==="
Import-Certificate -FilePath "$PSScriptRoot\RonghuiEarbuds.cer" -CertStoreLocation "Cert:\LocalMachine\TrustedPeople"

Write-Output "=== 2. 安装小组件 MSIX 包 ==="
try {
    Add-AppxPackage -Path "$PSScriptRoot\RonghuiEarbuds.Widgets.msix" -ErrorAction Stop
    Write-Output "MSIX 安装成功"
} catch {
    Write-Output "MSIX 安装失败: $_"
}

Write-Output "=== 3. 已安装的包 ==="
Get-AppxPackage *RonghuiEarbuds* | Select-Object Name, Version, InstallLocation

Stop-Transcript
