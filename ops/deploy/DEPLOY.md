# CloudPosGrid — Üretim Kurulum Rehberi

Bu rehber, temiz bir **Ubuntu 22.04/24.04 VPS** üzerinde üç bileşeni yayına alır:

| Bileşen | Alan adı | Teknoloji | Sunucudaki yer |
|---|---|---|---|
| Tanıtım sitesi | `cloudposgrid.com`, `www.` | Astro (statik) | `/var/www/cloudposgrid-www` |
| Uygulama | `app.cloudposgrid.com` | Angular (statik + PWA) | `/var/www/cloudposgrid-app` |
| API | `api.cloudposgrid.com` | .NET 9 (systemd) | `/var/www/cloudposgrid-api` |

**Mimari:** nginx her üç alan adını karşılar; API isteklerini `127.0.0.1:5080`'e ters proxy'ler. PostgreSQL ve API yalnızca localhost dinler (dışarı kapalı). TLS Let's Encrypt ile.

> **Sır yönetimi:** Hiçbir şifre/anahtar git'e girmez. Tümü sunucuda `/etc/cloudposgrid/api.env` içinde durur (bkz. `api.env.example`).

---

## 0. DNS

Alan adı sağlayıcında (veya Cloudflare'de) şu **A kayıtlarını** sunucunun IP'sine yönlendir:

```
cloudposgrid.com        A   <SUNUCU_IP>
www.cloudposgrid.com    A   <SUNUCU_IP>
app.cloudposgrid.com    A   <SUNUCU_IP>
api.cloudposgrid.com    A   <SUNUCU_IP>
```

> **Cloudflare (şiddetle önerilir):** Kayıtları "Proxied" (turuncu bulut) yaparsan ücretsiz DDoS koruması + CDN + gizli sunucu IP'si elde edersin. Bu, "tek kaynaktan 100–250 bin isteklik ani sel" gibi saldırıların asıl kalkanıdır. Adım adım kurulum (rate-limit kuralı + Under Attack Mode + origin kilidi dahil): **`CLOUDFLARE.md`**.

---

## 1. Sunucu hazırlığı

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y nginx postgresql ufw

# Güvenlik duvarı: sadece SSH + web
sudo ufw allow OpenSSH
sudo ufw allow 'Nginx Full'
sudo ufw enable

# .NET 9 ASP.NET Core runtime (uygulamayı çalıştırmak için; SDK gerekmez)
sudo apt install -y aspnetcore-runtime-9.0
# (Paket bulunamazsa Microsoft deposunu ekle: https://learn.microsoft.com/dotnet/core/install/linux-ubuntu)

# Uygulama için yetkisiz sistem kullanıcısı
sudo useradd -r -s /usr/sbin/nologin cloudposgrid
```

---

## 2. PostgreSQL

```bash
sudo -u postgres psql <<'SQL'
CREATE USER cloudposgrid WITH PASSWORD 'GUCLU_BIR_SIFRE_SEC';
CREATE DATABASE cloudposgrid OWNER cloudposgrid;
GRANT ALL PRIVILEGES ON DATABASE cloudposgrid TO cloudposgrid;
SQL
```

> Şema/tablo oluşturmana gerek yok — API ilk açılışta master göçlerini (migration) otomatik uygular, her yeni işletme kendi şemasını kendisi kurar.

---

## 3. Uygulamayı derle (geliştirme makinende, Windows)

```powershell
# CloudPosGrid-backend klasöründe:
powershell -File ops\deploy\publish.ps1
```

Bu; API + Angular + Astro'yu derleyip `deploy-artifacts\{api,app,www,ops}` üretir (`ops/` = nginx/systemd/env/yedek dosyaları). Sunucuya kopyala:

```bash
# Windows'ta (scp) veya WinSCP ile:
scp -r deploy-artifacts/* kullanici@SUNUCU_IP:/tmp/cpg/
```

---

## 4. Dosyaları yerleştir

```bash
# API
sudo mkdir -p /var/www/cloudposgrid-api
sudo cp -r /tmp/cpg/api/* /var/www/cloudposgrid-api/

# Angular
sudo mkdir -p /var/www/cloudposgrid-app
sudo cp -r /tmp/cpg/app/* /var/www/cloudposgrid-app/

# Tanıtım
sudo mkdir -p /var/www/cloudposgrid-www
sudo cp -r /tmp/cpg/www/* /var/www/cloudposgrid-www/

# Yüklenen görseller için KALICI dizin (redeploy'da silinmesin) + symlink
sudo mkdir -p /var/lib/cloudposgrid/uploads
sudo rm -rf /var/www/cloudposgrid-api/wwwroot/uploads
sudo mkdir -p /var/www/cloudposgrid-api/wwwroot
sudo ln -s /var/lib/cloudposgrid/uploads /var/www/cloudposgrid-api/wwwroot/uploads

# API log dizini
sudo mkdir -p /var/www/cloudposgrid-api/logs

# Sahiplik
sudo chown -R cloudposgrid:cloudposgrid /var/www/cloudposgrid-api /var/lib/cloudposgrid
sudo chown -R www-data:www-data /var/www/cloudposgrid-app /var/www/cloudposgrid-www
```

---

## 5. Sırları ayarla (api.env)

```bash
sudo mkdir -p /etc/cloudposgrid
sudo cp /tmp/cpg/ops/deploy/api.env.example /etc/cloudposgrid/api.env
sudo nano /etc/cloudposgrid/api.env        # değerleri doldur (DB şifresi, JWT, admin, SMTP)
sudo chown root:cloudposgrid /etc/cloudposgrid/api.env
sudo chmod 640 /etc/cloudposgrid/api.env
```

Zorunlu değerler: `ConnectionStrings__Default` (üretim DB şifresiyle, **"Include Error Detail" YOK**), `Jwt__Secret` (`openssl rand -base64 48`), `Platform__AdminEmail`, `Platform__AdminPassword`, **`App__PublicApiUrl`** (`https://api.cloudposgrid.com`). SMTP boş bırakılırsa e-postalar sadece loglanır.

> **`App__PublicApiUrl` neden zorunlu?** Yüklenen ürün görsellerinin mutlak URL'i ve Trendyol ilan görselleri bundan üretilir. Boş bırakılırsa nginx ters proxy arkasındaki **iç host** (`127.0.0.1`) yansır; görseller dışarıdan açılmaz ve Trendyol ilan görseli yüklemesi sessizce kırılır.

---

## 6. systemd servisi (API)

```bash
sudo cp /tmp/cpg/ops/deploy/cloudposgrid-api.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now cloudposgrid-api

# Doğrula (açılışta master göçleri uygulanır)
systemctl status cloudposgrid-api
curl -s http://127.0.0.1:5080/health          # -> Healthy
journalctl -u cloudposgrid-api -f             # canlı log
```

---

## 7. nginx + HTTPS

```bash
# Önce paylaşılan rate-limit + Cloudflare gerçek-IP dosyasını conf.d'ye koy
# (http {} bağlamı — site config'lerinden önce yüklenmeli):
sudo cp /tmp/cpg/ops/deploy/nginx/cloudposgrid-limits.conf /etc/nginx/conf.d/

# 3 site config'ini kopyala (limits.conf'u sites-available'a KOYMA)
sudo cp /tmp/cpg/ops/deploy/nginx/*.cloudposgrid.com.conf /etc/nginx/sites-available/
sudo ln -s /etc/nginx/sites-available/api.cloudposgrid.com.conf /etc/nginx/sites-enabled/
sudo ln -s /etc/nginx/sites-available/app.cloudposgrid.com.conf /etc/nginx/sites-enabled/
sudo ln -s /etc/nginx/sites-available/www.cloudposgrid.com.conf /etc/nginx/sites-enabled/
sudo rm -f /etc/nginx/sites-enabled/default

sudo nginx -t && sudo systemctl reload nginx

# Let's Encrypt sertifikaları (443 + http->https yönlendirmesini otomatik ekler)
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d cloudposgrid.com -d www.cloudposgrid.com
sudo certbot --nginx -d app.cloudposgrid.com
sudo certbot --nginx -d api.cloudposgrid.com
# Otomatik yenileme zaten kurulu: systemctl list-timers | grep certbot
```

---

## 8. Otomatik yedek (kritik!)

```bash
sudo mkdir -p /opt/cloudposgrid
sudo cp /tmp/cpg/ops/backup-db.sh /opt/cloudposgrid/backup-db.sh
sudo chmod +x /opt/cloudposgrid/backup-db.sh
# DB şifresini root'un .pgpass'ine koy (script içinde sır yok):
echo "127.0.0.1:5432:cloudposgrid:cloudposgrid:DB_SIFRESI" | sudo tee -a /root/.pgpass
sudo chmod 600 /root/.pgpass
# Her gece 03:00 cron:
echo "0 3 * * * /opt/cloudposgrid/backup-db.sh >> /var/log/cpg-backup.log 2>&1" | sudo crontab -
```

> **Sunucu dışı kopya (şiddetle önerilir):** `backup-db.sh` içindeki `RCLONE_REMOTE`'u Cloudflare R2 / Backblaze B2'ye ayarla. Sunucu tümden giderse tek kurtarıcın budur.

### 8.1 Geri yükleme provası (restore drill)

Yedek **almak** yetmez — geri **yüklenebildiği** düzenli kanıtlanmalı. Aksi halde felaket anında ilk kez öğrenirsin.
`restore-drill.sh` en güncel yedeği **geçici** bir veritabanına yükler, sağlık sorguları çalıştırır, sonra geçici DB'yi siler. **Üretim veritabanına dokunmaz.**

```bash
sudo cp /tmp/cpg/ops/restore-drill.sh /opt/cloudposgrid/restore-drill.sh
sudo chmod +x /opt/cloudposgrid/restore-drill.sh

# Elle bir kez çalıştır (çıkış kodu 0 = yedek geri yüklenebilir):
sudo /opt/cloudposgrid/restore-drill.sh

# Haftalık cron (her Pazar 04:00) — mevcut crontab'a EKLE:
( sudo crontab -l; echo "0 4 * * 0 /opt/cloudposgrid/restore-drill.sh >> /var/log/cpg-restore-drill.log 2>&1" ) | sudo crontab -
```

Kontroller: master `tenants`/`users` dolu mu, `tenant_*` şemaları geldi mi, örnek şemada tablolar var mı.
Başarısızlıkta çıkış kodu ≠ 0 ve log'a `HATA:` yazar → **yedeğin bozuk demektir, hemen müdahale et.**

> Windows geliştirme makinesinde aynı prova: `$env:PGPASSWORD="<sifre>"; powershell -File ops\restore-drill.ps1`

---

## 9. Son kontrol (smoke test)

- [ ] `https://cloudposgrid.com` → tanıtım açılıyor, yeşil kilit
- [ ] `https://app.cloudposgrid.com/giris` → giriş ekranı
- [ ] `https://app.cloudposgrid.com/demo` → tek tık demo işletme açılıyor
- [ ] `https://api.cloudposgrid.com/health` → `Healthy`
- [ ] Yeni kayıt → doğrulama kodu e-postası geliyor (SMTP ayarlandıysa)
- [ ] Süper-admin: `app.cloudposgrid.com/yonetim/giris` → admin e-posta/şifre ile giriş
- [ ] Bir ürün + görsel yükle → görsel `api.cloudposgrid.com/uploads/...` üzerinden görünüyor

---

## 10. Saldırı / kötüye kullanım sertleştirme (rate-limit + fail2ban + Cloudflare)

Katmanlı koruma — "tek kaynaktan 100–250 bin isteklik sel" ve daha büyüğüne karşı:

**a) Kenar rate-limit (nginx)** — §7'de `cloudposgrid-limits.conf` zaten conf.d'ye
kopyalandı; API'de IP başına 20 istek/sn, statik sitelerde 40/sn tavan uygulanır.
Fazlası .NET'e/DB'ye dokunmadan 429 ile elenir. (Uygulama içinde ayrıca GlobalLimiter
= kullanıcı/IP başına dk'da 600 son bariyer olarak çalışır.)

**b) fail2ban** — ısrarla limiti döven IP'yi komple banlar:

```bash
sudo apt install -y fail2ban
sudo cp /tmp/cpg/ops/deploy/fail2ban/filter.d/cloudposgrid-nginx.conf /etc/fail2ban/filter.d/
sudo cp /tmp/cpg/ops/deploy/fail2ban/jail.d/cloudposgrid.conf         /etc/fail2ban/jail.d/
sudo systemctl enable --now fail2ban
sudo fail2ban-client status                    # jail'ler
```

**c) Cloudflare** — asıl kalkan (volumetrik saldırıyı origin'e ulaşmadan soğurur,
IP'yi gizler, edge rate-limit + Under Attack Mode + origin kilidi). Adım adım:
**`CLOUDFLARE.md`**.

> ⚠️ Cloudflare arkasındayken iptables banı gerçek saldırganı tutmaz (kaynak = CF IP).
> O durumda `CLOUDFLARE.md`'deki **origin kilidi** + CF rate-limit kuralı esastır;
> fail2ban'ı CF API action'ıyla kullanmak opsiyoneldir. CF yokken iptables banı tam çalışır.

---

## Güncelleme (yeni sürüm yayınlama)

```powershell
# 1) Geliştirme makinende yeniden derle
powershell -File ops\deploy\publish.ps1
scp -r deploy-artifacts/* kullanici@SUNUCU_IP:/tmp/cpg/
```
```bash
# 2) Sunucuda dosyaları değiştir (uploads symlink'i ve api.env korunur)
sudo systemctl stop cloudposgrid-api
sudo rsync -a --delete --exclude wwwroot/uploads /tmp/cpg/api/ /var/www/cloudposgrid-api/
sudo rsync -a --delete /tmp/cpg/app/ /var/www/cloudposgrid-app/
sudo rsync -a --delete /tmp/cpg/www/ /var/www/cloudposgrid-www/
sudo chown -R cloudposgrid:cloudposgrid /var/www/cloudposgrid-api
sudo systemctl start cloudposgrid-api
curl -s http://127.0.0.1:5080/health
```

> DB göçleri (master + tenant şema) açılışta otomatik uygulanır — elle migration çalıştırmana gerek yok.

---

## Sorun giderme

| Belirti | Bak |
|---|---|
| API açılmıyor | `journalctl -u cloudposgrid-api -n 50` — çoğunlukla `Jwt:Secret` eksik/kısa veya DB bağlanamıyor |
| 502 Bad Gateway | API çalışmıyor (`systemctl status`) veya 5080 dinlemiyor |
| Görseller kayboluyor | uploads symlink'i doğru mu (`ls -l /var/www/cloudposgrid-api/wwwroot/uploads`) |
| E-posta gitmiyor | `api.env` SMTP değerleri; loglarda `[E-POSTA / DEV]` görünüyorsa Host boş |
| CORS hatası | Uygulama `app.cloudposgrid.com`'da mı; `appsettings.Production.json` CORS listesi doğru mu |
| Kayıt/giriş 429 | Rate limit (IP başına dk'da 10 auth isteği) — normal; Cloudflare arkasında gerçek IP için ForwardedHeaders zaten açık |
