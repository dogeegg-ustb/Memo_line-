<#
.SYNOPSIS
  卸载 install-vdd.ps1 装的 Virtual-Display-Driver：移除设备和驱动包。
  配置目录和注册表登记只在当初由 install-vdd.ps1 创建时才删除。需要管理员权限。
#>
param([string]$LogPath = "")
$ErrorActionPreference = "Stop"
$HardwareId = "Root\MttVDD"
$RegistryKey = "HKLM:\SOFTWARE\MikeTheTech\VirtualDisplayDriver"
$Marker = "memoline-installed.json"

if ($LogPath) { Start-Transcript -Path $LogPath -Force | Out-Null }
$code = 0
try {
    Add-Type -Path (Join-Path $PSScriptRoot "VddSetup.cs")
    $infs = @(Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.HardWareID -eq $HardwareId } |
              ForEach-Object { $_.InfName } | Where-Object { $_ } | Sort-Object -Unique)
    $removed = [VddSetup]::Remove($HardwareId)
    Write-Host "已移除 $removed 个设备"
    foreach ($inf in $infs) {
        pnputil /delete-driver $inf /uninstall /force | Out-Null
        Write-Host "已删除驱动包 $inf"
    }
    $configDir = (Get-ItemProperty $RegistryKey -Name VDDPATH -ErrorAction SilentlyContinue).VDDPATH
    $markerPath = if ($configDir) { Join-Path $configDir $Marker } else { "" }
    if ($markerPath -and (Test-Path $markerPath)) {
        $installed = Get-Content $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($installed.createdSettings) { Remove-Item (Join-Path $configDir "vdd_settings.xml") -Force -ErrorAction SilentlyContinue }
        Remove-Item $markerPath -Force
        if ($installed.createdRegistry) {
            Remove-Item $RegistryKey -Force
            if (-not (Get-ChildItem $configDir -Force)) { Remove-Item $configDir -Force }
        }
        Write-Host "已清理配置 $configDir"
    }
}
catch {
    Write-Host "卸载失败：$($_.Exception.Message)"
    $code = 1
}
finally {
    if ($LogPath) { Stop-Transcript | Out-Null }
}
exit $code
