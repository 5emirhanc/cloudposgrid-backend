# =====================================================================
# CloudPosGrid - YEDEKTEN GERI YUKLEME PROVASI (Windows)
# =====================================================================
# NEDEN: Yedek ALMAK yetmez; yedegin gercekten GERI YUKLENEBILIR oldugu
# duzenli olarak kanitlanmalidir. Bu script en guncel yedegi GECICI bir
# veritabanina yukler, saglik sorgulari calistirir, sonra gecici DB'yi siler.
# URETIM/GELISTIRME VERITABANINA DOKUNMAZ.
#
# Elle calistirma:
#   $env:PGPASSWORD="<sifre>"; powershell -File ops\restore-drill.ps1
#
# Cikis kodu 0 = prova BASARILI. 0 disi = yedek geri yuklenemedi (DIKKAT).
# =====================================================================

# DIKKAT: PostgreSQL araclari bilgi mesajlarini (ornegin dropdb --if-exists "NOTICE: ... skipping")
# stderr'e yazar. ErrorActionPreference=Stop bunlari HATA sayip scripti dusururdu. Bu yuzden
# "Continue" kullanilir ve basari/basarisizlik native cikis kodundan ($LASTEXITCODE) anlasilir.
$ErrorActionPreference = "Continue"

$DbHost = "127.0.0.1"
$DbPort = 5432
$DbUser = "postgres"
$TestDb = "cloudposgrid_restore_test"

# --- PostgreSQL araclarini bul ---
$pgRoot = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
          Sort-Object Name -Descending | Select-Object -First 1
if (-not $pgRoot) { throw "PostgreSQL kurulumu bulunamadi." }
$bin        = Join-Path $pgRoot.FullName "bin"
$pgRestore  = Join-Path $bin "pg_restore.exe"
$psql       = Join-Path $bin "psql.exe"
$createdb   = Join-Path $bin "createdb.exe"
$dropdb     = Join-Path $bin "dropdb.exe"

# --- Sifre ---
if (-not $env:PGPASSWORD) {
    if ($env:CPG_DB_PASSWORD) { $env:PGPASSWORD = $env:CPG_DB_PASSWORD }
    else { throw "Sifre yok. PGPASSWORD veya CPG_DB_PASSWORD ortam degiskenini ayarla." }
}

# --- En guncel yedek ---
$backupDir = Join-Path (Split-Path $PSScriptRoot -Parent) "backups"
$latest = Get-ChildItem $backupDir -Filter "cloudposgrid_*.dump" -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $latest) { throw "Yedek dosyasi bulunamadi: $backupDir" }
Write-Output "Prova edilecek yedek: $($latest.FullName)"

# dropdb "NOTICE: ... skipping" yazabilir; cikis kodu onemli degil (yoksa da sorun yok).
function Remove-TestDb {
    & $dropdb -h $DbHost -p $DbPort -U $DbUser --if-exists --force $TestDb | Out-Null
}

try {
    # --- Temiz gecici DB + geri yukleme ---
    Remove-TestDb
    & $createdb -h $DbHost -p $DbPort -U $DbUser $TestDb
    if ($LASTEXITCODE -ne 0) { throw "createdb hata kodu: $LASTEXITCODE" }
    Write-Output "Gecici veritabani olusturuldu: $TestDb"

    # --exit-on-error: sessiz kismi restore kabul edilmez
    & $pgRestore -h $DbHost -p $DbPort -U $DbUser -d $TestDb --no-owner --no-privileges --exit-on-error $latest.FullName
    if ($LASTEXITCODE -ne 0) { throw "pg_restore hata kodu: $LASTEXITCODE" }
    Write-Output "Geri yukleme tamamlandi."

    # --- Saglik sorgulari ---
    function Query([string]$sql) {
        $out = & $psql -h $DbHost -p $DbPort -U $DbUser -d $TestDb -tAc $sql
        if ($LASTEXITCODE -ne 0) { throw "psql hata kodu: $LASTEXITCODE ($sql)" }
        return "$out".Trim()
    }

    $tenants = [int](Query 'SELECT count(*) FROM public.tenants;')
    $users   = [int](Query 'SELECT count(*) FROM public.users;')
    $schemas = [int](Query "SELECT count(*) FROM information_schema.schemata WHERE schema_name LIKE 'tenant\_%';")
    Write-Output "Master: $tenants isletme, $users kullanici, $schemas tenant semasi."

    if ($tenants -lt 1 -or $users -lt 1) { throw "Yedek yuklendi ama master tablolari BOS. Yedek kullanilamaz!" }

    if ($schemas -gt 0) {
        $sample = Query "SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'tenant\_%' LIMIT 1;"
        $tables = [int](Query "SELECT count(*) FROM information_schema.tables WHERE table_schema = '$sample';")
        Write-Output "Ornek sema $sample`: $tables tablo."
        if ($tables -lt 5) { throw "Tenant semasi bos/eksik gorunuyor." }
    }

    Write-Output "PROVA BASARILI - yedek geri yuklenebilir durumda."
    exit 0
}
finally {
    Remove-TestDb   # basarili da olsa hata da olsa gecici DB temizlenir
}
