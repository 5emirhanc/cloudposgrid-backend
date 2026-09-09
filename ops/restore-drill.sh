#!/usr/bin/env bash
# =====================================================================
# CloudPosGrid - YEDEKTEN GERI YUKLEME PROVASI (restore drill)
# =====================================================================
# NEDEN: Yedek ALMAK yetmez; yedegin gercekten GERI YUKLENEBILIR oldugu
# duzenli olarak kanitlanmalidir. Aksi halde felaket aninda ilk kez
# ogrenirsiniz. Bu script en guncel yedegi GECICI bir veritabanina
# yukler, saglik sorgulari calistirir ve gecici DB'yi siler.
# URETIM VERITABANINA DOKUNMAZ.
#
# Kurulum:
#   1) chmod +x /opt/cloudposgrid/ops/restore-drill.sh
#   2) Sifre root'un .pgpass dosyasinda olmali (backup-db.sh ile ayni):
#        echo "127.0.0.1:5432:*:postgres:<SIFRE>" >> ~/.pgpass && chmod 600 ~/.pgpass
#   3) Haftalik cron (her Pazar 04:00):
#        0 4 * * 0 /opt/cloudposgrid/ops/restore-drill.sh >> /var/log/cpg-restore-drill.log 2>&1
#
# Cikis kodu 0 = prova BASARILI (yedek geri yuklenebilir).
# Cikis kodu != 0 = DIKKAT: yedegin geri yuklenmesi basarisiz!
# =====================================================================
set -euo pipefail

DB_HOST="127.0.0.1"
DB_PORT="5432"
DB_USER="postgres"
BACKUP_DIR="/var/backups/cloudposgrid"
TEST_DB="cloudposgrid_restore_test"

log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"; }

# Gecici DB'yi her durumda temizle (basarili da olsa, hata da olsa).
cleanup() {
  dropdb -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" --if-exists --force "$TEST_DB" 2>/dev/null || true
}
trap cleanup EXIT

# 1) En guncel yedek dosyasini bul.
LATEST="$(ls -1t "$BACKUP_DIR"/cloudposgrid_*.dump 2>/dev/null | head -n1 || true)"
if [ -z "$LATEST" ]; then
  log "HATA: $BACKUP_DIR icinde yedek dosyasi bulunamadi."
  exit 1
fi
log "Prova edilecek yedek: $LATEST ($(du -h "$LATEST" | cut -f1))"

# 2) Temiz gecici DB olustur ve yedegi yukle.
cleanup
createdb -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" "$TEST_DB"
log "Gecici veritabani olusturuldu: $TEST_DB"

# --exit-on-error: en ufak hata provayi dusursun (sessiz kismi restore kabul edilmez).
pg_restore -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$TEST_DB" --no-owner --no-privileges --exit-on-error "$LATEST"
log "Geri yukleme tamamlandi."

# 3) Saglik sorgulari - veri gercekten orada mi?
q() { psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$TEST_DB" -tAc "$1"; }

TENANTS="$(q 'SELECT count(*) FROM public.tenants;')"
USERS="$(q 'SELECT count(*) FROM public.users;')"
SCHEMAS="$(q "SELECT count(*) FROM information_schema.schemata WHERE schema_name LIKE 'tenant\_%';")"

log "Master: $TENANTS isletme, $USERS kullanici, $SCHEMAS tenant semasi."

if [ "$TENANTS" -lt 1 ] || [ "$USERS" -lt 1 ]; then
  log "HATA: Yedek yuklendi ama master tablolari BOS. Yedek kullanilamaz!"
  exit 2
fi

# Ornek bir tenant semasinda tablo var mi (sema icerigi de gelmis mi)?
if [ "$SCHEMAS" -gt 0 ]; then
  SAMPLE="$(q "SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'tenant\_%' LIMIT 1;")"
  TABLES="$(q "SELECT count(*) FROM information_schema.tables WHERE table_schema = '$SAMPLE';")"
  log "Ornek sema $SAMPLE: $TABLES tablo."
  if [ "$TABLES" -lt 5 ]; then
    log "HATA: Tenant semasi bos/eksik gorunuyor."
    exit 3
  fi
fi

log "PROVA BASARILI - yedek geri yuklenebilir durumda."
exit 0
