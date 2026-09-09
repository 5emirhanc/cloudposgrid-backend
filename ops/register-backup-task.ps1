# =====================================================================
# CloudPosGrid - Gecelik yedek + haftalik geri-yukleme provasi gorevlerini kaydeder (Windows)
# =====================================================================
# NEDEN: backup-db.ps1 ve restore-drill.ps1 hazir, ama Gorev Zamanlayici'ya
# KAYDEDILMEZSE hicbir zaman calismaz. Bu script o kaydi yapar (idempotent).
#
# BIR KEZ, YONETICI olarak calistir:
#   powershell -NoProfile -ExecutionPolicy Bypass -File ops\register-backup-task.ps1
#
# ONCESINDE: DB sifresini kalici kullanici ortam degiskenine koy (script icinde SIFRE YOK):
#   [Environment]::SetEnvironmentVariable("CPG_DB_PASSWORD","<sifre>","User")
#   (Yeni bir PowerShell penceresi acmadan gorev bunu goremez.)
#
# Kaldirmak icin:
#   schtasks /Delete /TN "CloudPosGrid Yedek" /F
#   schtasks /Delete /TN "CloudPosGrid Restore Provasi" /F
# =====================================================================

$ErrorActionPreference = "Stop"

if (-not [Environment]::GetEnvironmentVariable("CPG_DB_PASSWORD", "User")) {
    Write-Warning "CPG_DB_PASSWORD kullanici ortam degiskeni YOK. Gorevler calisirken sifre bulamaz."
    Write-Warning 'Once sunu calistir:  [Environment]::SetEnvironmentVariable("CPG_DB_PASSWORD","<sifre>","User")'
}

$opsDir      = $PSScriptRoot
$backupPs1   = Join-Path $opsDir "backup-db.ps1"
$drillPs1    = Join-Path $opsDir "restore-drill.ps1"

foreach ($p in @($backupPs1, $drillPs1)) {
    if (-not (Test-Path $p)) { throw "Script bulunamadi: $p" }
}

function Register-CpgTask {
    param([string]$Name, [string]$ScriptPath, [string[]]$ScheduleArgs)

    # Idempotent: varsa once sil (schtasks /Create /F de yapar ama acik olalim).
    schtasks /Query /TN $Name 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Output "Mevcut gorev siliniyor: $Name"
        schtasks /Delete /TN $Name /F | Out-Null
    }

    $action = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`""
    schtasks /Create /TN $Name /TR $action /F @ScheduleArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Gorev kaydedilemedi: $Name (cikis kodu $LASTEXITCODE)" }
    Write-Output "Gorev kaydedildi: $Name"
}

# Her gece 03:00 -> yedek al
Register-CpgTask -Name "CloudPosGrid Yedek" -ScriptPath $backupPs1 -ScheduleArgs @("/SC","DAILY","/ST","03:00")

# Her Pazar 04:00 -> yedegin geri yuklenebilirligini kanitla
Register-CpgTask -Name "CloudPosGrid Restore Provasi" -ScriptPath $drillPs1 -ScheduleArgs @("/SC","WEEKLY","/D","SUN","/ST","04:00")

Write-Output ""
Write-Output "Tamam. Kontrol:  schtasks /Query /TN `"CloudPosGrid Yedek`" /V /FO LIST"
Write-Output "Hemen test:      schtasks /Run /TN `"CloudPosGrid Yedek`""
