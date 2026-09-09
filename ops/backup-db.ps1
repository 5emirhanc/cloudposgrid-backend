# =====================================================================
# CloudPosGrid - PostgreSQL otomatik yedek (Windows / gelistirme makinesi)
# =====================================================================
# Tek dosyalik sikistirilmis yedek alir (pg_dump custom format).
# Schema-per-tenant mimaride TUM isletme semalari tek veritabaninda
# oldugu icin bu tek dump her seyi kapsar (master + tum tenant'lar).
#
# Yedekler ..\backups\ altina yazilir. Bu klasor OneDrive icinde oldugu
# icin otomatik olarak buluta da kopyalanir (makine cokse bile yedek kalir).
#
# SIFRE: Script icinde sifre YOKTUR. Su ortam degiskenlerinden biri gerekli:
#   PGPASSWORD  veya  CPG_DB_PASSWORD
#
# Elle calistirma:
#   $env:PGPASSWORD="<sifre>"; powershell -File ops\backup-db.ps1
#
# Zamanlanmis gorev (her gece 03:00) - BIR KEZ calistir (sifreyi kendi girersin):
#   schtasks /Create /TN "CloudPosGrid Yedek" /TR "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\Users\SEVIL\OneDrive\Desktop\CloudPosGrid-backend\ops\backup-db.ps1\"" /SC DAILY /ST 03:00
#   (Gorevde sifre icin: Gorev Zamanlayici > gorev > Eylemler > ortam degiskeni
#    yerine kullanici ortam degiskenine CPG_DB_PASSWORD ekle: sistemde bir kez
#    [Environment]::SetEnvironmentVariable("CPG_DB_PASSWORD","<sifre>","User") )
#
# Geri yukleme (ornek):
#   pg_restore -h 127.0.0.1 -U postgres -d cloudposgrid --clean --if-exists <dosya.dump>
# =====================================================================

$ErrorActionPreference = "Stop"

# --- Ayarlar ---
$DbHost   = "127.0.0.1"
$DbPort   = 5432
$DbName   = "cloudposgrid"
$DbUser   = "postgres"
$KeepLast = 14   # son 14 yedek saklanir (gunluk calisirsa ~2 hafta)

# --- pg_dump'i bul (en yeni PostgreSQL surumu) ---
$pgRoot = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $pgRoot) { throw "PostgreSQL kurulumu bulunamadi (C:\Program Files\PostgreSQL)." }
$pgDump = Join-Path $pgRoot.FullName "bin\pg_dump.exe"
if (-not (Test-Path $pgDump)) { throw "pg_dump bulunamadi: $pgDump" }

# --- Sifre: ortam degiskeninden (script icine SIFRE YAZMA) ---
if (-not $env:PGPASSWORD) {
    if ($env:CPG_DB_PASSWORD) { $env:PGPASSWORD = $env:CPG_DB_PASSWORD }
    else { throw "Sifre yok. PGPASSWORD veya CPG_DB_PASSWORD ortam degiskenini ayarla." }
}

# --- Yedek klasoru ---
$backupDir = Join-Path (Split-Path $PSScriptRoot -Parent) "backups"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

# --- Yedek al ---
$stamp = Get-Date -Format "yyyy-MM-dd_HHmm"
$file  = Join-Path $backupDir "cloudposgrid_$stamp.dump"

& $pgDump -h $DbHost -p $DbPort -U $DbUser -d $DbName -Fc -Z 6 -f $file
if ($LASTEXITCODE -ne 0) { throw "pg_dump hata kodu: $LASTEXITCODE" }

$size = [math]::Round((Get-Item $file).Length / 1KB, 1)
Write-Output "Yedek alindi: $file ($size KB)"

# --- Rotasyon: en yeni $KeepLast dosya kalsin ---
Get-ChildItem $backupDir -Filter "cloudposgrid_*.dump" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -Skip $KeepLast |
    ForEach-Object { Remove-Item $_.FullName -Force -Confirm:$false; Write-Output "Eski yedek silindi: $($_.Name)" }
