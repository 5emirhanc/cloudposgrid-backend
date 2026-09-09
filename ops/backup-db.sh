#!/usr/bin/env bash
# =====================================================================
# CloudPosGrid - PostgreSQL otomatik yedek (Linux VPS / uretim)
# =====================================================================
# Kurulum (deploy gunu):
#   1) chmod +x /opt/cloudposgrid/ops/backup-db.sh
#   2) Sifreyi root'un .pgpass dosyasina koy (script icinde SIFRE YOK):
#        echo "127.0.0.1:5432:cloudposgrid:postgres:<SIFRE>" >> ~/.pgpass
#        chmod 600 ~/.pgpass
#   3) Cron'a ekle (her gece 03:00):
#        crontab -e
#        0 3 * * * /opt/cloudposgrid/ops/backup-db.sh >> /var/log/cpg-backup.log 2>&1
#
# SUNUCU DISI KOPYA (cok onemli - sunucu ele gecerse/cokerse tek kurtarici):
#   rclone kur (https://rclone.org), Cloudflare R2 veya Backblaze B2 remote'u tanimla,
#   asagidaki RCLONE_REMOTE degiskenini doldur. Bos birakilirsa sadece yerel yedek alinir.
#
# Geri yukleme:
#   pg_restore -h 127.0.0.1 -U postgres -d cloudposgrid --clean --if-exists <dosya.dump>
# =====================================================================
set -euo pipefail

DB_HOST="127.0.0.1"
DB_PORT="5432"
DB_NAME="cloudposgrid"
DB_USER="postgres"
BACKUP_DIR="/var/backups/cloudposgrid"
KEEP_DAYS=14
RCLONE_REMOTE=""   # ornek: "r2:cpg-yedekler" - bos ise atlanir

mkdir -p "$BACKUP_DIR"

STAMP="$(date +%Y-%m-%d_%H%M)"
FILE="$BACKUP_DIR/cloudposgrid_${STAMP}.dump"

# Tek dosyalik sikistirilmis yedek - tum semalar (master + tum tenant'lar) dahil.
pg_dump -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -Fc -Z 6 -f "$FILE"
echo "Yedek alindi: $FILE ($(du -h "$FILE" | cut -f1))"

# Sunucu disina kopya (rclone tanimliysa).
if [ -n "$RCLONE_REMOTE" ]; then
  rclone copy "$FILE" "$RCLONE_REMOTE/" --quiet
  echo "Buluta kopyalandi: $RCLONE_REMOTE/$(basename "$FILE")"
fi

# Rotasyon: KEEP_DAYS gunden eski yerel yedekleri sil.
find "$BACKUP_DIR" -name "cloudposgrid_*.dump" -mtime +"$KEEP_DAYS" -delete
