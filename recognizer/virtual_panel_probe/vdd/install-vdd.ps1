<#
.SYNOPSIS
  安装上游原版 Virtual-Display-Driver（MIT，SignPath Foundation 签名），给 CSP 浮动面板收纳用。

.DESCRIPTION
  需要管理员权限。已经装有同一驱动（硬件 ID Root\MttVDD）时不重复安装。
  驱动包按固定版本下载（或用 -PackageZip 指定本地文件），校验 SHA-256 和签名后安装。
  配置写到 -ConfigDir（默认 %ProgramData%\MemoLine\VirtualDisplayDriver），并登记到驱动读取的
  HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver\VDDPATH；已有登记时沿用原目录和原配置。
  安装时 Windows 可能询问是否信任发布者“SignPath Foundation”，选“安装”。

  只配置 1 块虚拟屏：同时接入两块时，本机实测桌面合成每秒卡住近 1 秒。分辨率给一组档位，
  收纳程序按要放的面板选够用的最小一档。

  -Reconfigure：驱动已装时，把配置改写成本脚本的档位并重启虚拟显示设备。只改由本脚本创建的配置，
  别人的配置要加 -Force。
#>
param(
    [string]$PackageZip = "",
    [string]$ConfigDir = (Join-Path $env:ProgramData "MemoLine\VirtualDisplayDriver"),
    [string[]]$Resolutions = @("1024x768", "1280x800", "1536x960", "1920x1080", "1920x1200", "2048x1280",
                               "2560x1440", "2560x1600", "3072x1920", "3840x2160", "3840x2400"),
    [int]$RefreshRate = 60,
    [switch]$Reconfigure,
    [switch]$Force,
    [string]$LogPath = ""
)
$ErrorActionPreference = "Stop"
$Version = "25.7.23"
$Url = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/$Version/VirtualDisplayDriver-x86.Driver.Only.zip"
$Sha256 = "E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A"
$HardwareId = "Root\MttVDD"
$DisplayClass = [Guid]"4D36E968-E325-11CE-BFC1-08002BE10318"
$RegistryKey = "HKLM:\SOFTWARE\MikeTheTech\VirtualDisplayDriver"
$Marker = "memoline-installed.json"

function Write-Settings([string]$template, [string]$path) {
    [xml]$xml = Get-Content $template -Raw -Encoding UTF8
    $root = $xml.DocumentElement
    $root.SelectSingleNode("monitors/count").InnerText = "1"
    $global = $root.SelectSingleNode("global")
    $global.RemoveAll()
    $rate = $xml.CreateElement("g_refresh_rate")
    $rate.InnerText = "$RefreshRate"
    [void]$global.AppendChild($rate)
    $list = $root.SelectSingleNode("resolutions")
    $list.RemoveAll()
    foreach ($text in $Resolutions) {
        $width, $height = $text -split "x"
        $resolution = $xml.CreateElement("resolution")
        foreach ($pair in @(@("width", $width), @("height", $height), @("refresh_rate", $RefreshRate))) {
            $element = $xml.CreateElement($pair[0])
            $element.InnerText = "$($pair[1])"
            [void]$resolution.AppendChild($element)
        }
        [void]$list.AppendChild($resolution)
    }
    $xml.Save($path)
    Write-Host "已写入配置 $path（1 块，$($Resolutions -join ' / ')，${RefreshRate}Hz）"
}

if ($LogPath) { Start-Transcript -Path $LogPath -Force | Out-Null }
$work = Join-Path $env:TEMP ("memoline-vdd-" + [Guid]::NewGuid().ToString("N"))
$code = 0
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "需要以管理员身份运行"
    }
    Add-Type -Path (Join-Path $PSScriptRoot "VddSetup.cs")
    $existing = (Get-ItemProperty $RegistryKey -Name VDDPATH -ErrorAction SilentlyContinue).VDDPATH

    if ([VddSetup]::Count($HardwareId) -gt 0) {
        if (-not $Reconfigure) {
            Write-Host "虚拟显示驱动已安装（$HardwareId 设备已存在），不做改动；要改配置请加 -Reconfigure"
            exit 0  # the finally block still runs
        }
        $configDir = if ($existing) { $existing } else { "C:\VirtualDisplayDriver" }
        $settings = Join-Path $configDir "vdd_settings.xml"
        if (-not (Test-Path (Join-Path $configDir $Marker)) -and -not $Force) {
            throw "$settings 不是本脚本创建的，加 -Force 才改写"
        }
        Write-Settings $settings $settings
        $device = Get-PnpDevice -Class Display | Where-Object { $_.HardwareID -contains $HardwareId } | Select-Object -First 1
        pnputil /restart-device "$($device.InstanceId)" | Out-Null
        Start-Sleep -Seconds 2
        $device = Get-PnpDevice -InstanceId $device.InstanceId
        Write-Host "已重启 $($device.FriendlyName) $($device.InstanceId) 状态 $($device.Status)"
        exit 0
    }

    New-Item -ItemType Directory $work | Out-Null
    if (-not $PackageZip) {
        $PackageZip = Join-Path $work "driver.zip"
        Write-Host "下载 Virtual-Display-Driver $Version …"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest $Url -OutFile $PackageZip -UseBasicParsing
    }
    $hash = (Get-FileHash $PackageZip -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) { throw "驱动包 SHA-256 不符：$hash" }
    Expand-Archive $PackageZip $work -Force
    $driverDir = Join-Path $work "VirtualDisplayDriver"
    foreach ($file in "mttvdd.cat", "MttVDD.dll") {
        $signature = Get-AuthenticodeSignature (Join-Path $driverDir $file)
        if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "CN=SignPath Foundation") {
            throw "$file 签名无效：$($signature.Status) $($signature.SignerCertificate.Subject)"
        }
    }

    if ($existing) {
        $ConfigDir = $existing
        Write-Host "沿用已登记的配置目录 $ConfigDir"
    }
    New-Item -ItemType Directory -Force $ConfigDir | Out-Null
    # The driver runs in a UMDF host as LOCAL SERVICE and only reads this directory.
    icacls $ConfigDir /grant "*S-1-5-19:(OI)(CI)RX" | Out-Null
    $settings = Join-Path $ConfigDir "vdd_settings.xml"
    $createdSettings = -not (Test-Path $settings)
    if ($createdSettings) {
        Write-Settings (Join-Path $driverDir "vdd_settings.xml") $settings
    } else {
        Write-Host "保留已有配置 $settings"
    }
    if (-not $existing) {
        New-Item -Path $RegistryKey -Force | Out-Null
        Set-ItemProperty $RegistryKey -Name VDDPATH -Value $ConfigDir
    }
    [ordered]@{
        version = $Version; installedAt = (Get-Date).ToString("o"); configDir = $ConfigDir
        createdRegistry = -not $existing; createdSettings = $createdSettings
    } | ConvertTo-Json | Set-Content (Join-Path $ConfigDir $Marker) -Encoding UTF8

    Write-Host "安装驱动 …（如果 Windows 询问是否安装 SignPath Foundation 的设备软件，请选「安装」）"
    $reboot = [VddSetup]::Install($HardwareId, (Join-Path $driverDir "MttVDD.inf"), $DisplayClass, "Display")
    $device = Get-PnpDevice -Class Display | Where-Object { $_.HardwareID -contains $HardwareId } | Select-Object -First 1
    Write-Host "已安装：$($device.FriendlyName) $($device.InstanceId) 状态 $($device.Status)"
    if ($reboot) { Write-Host "Windows 要求重启后驱动才能生效" }
}
catch {
    Write-Host "失败：$($_.Exception.Message)"
    $code = 1
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    if ($LogPath) { Stop-Transcript | Out-Null }
}
exit $code
