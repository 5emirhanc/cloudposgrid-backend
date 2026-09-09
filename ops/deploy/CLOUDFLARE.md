# CloudPosGrid — Cloudflare ile DDoS / sel koruması

Bu, **"tek bir kaynağın 100–250 bin isteklik ani seli"** ve daha büyük volumetrik
saldırılara karşı en güçlü katmandır. Cloudflare (ücretsiz plan yeterli) trafiği
kendi ağının kenarında karşılar; saldırı senin sunucuna **hiç ulaşmadan** elenir,
sunucunun gerçek IP'si gizlenir, statik içerik CDN'de cache'lenir.

> Sıra önemli: önce Cloudflare'i devreye al, **sonra** "Origin kilidi" adımını yap.
> Aksi halde kilit sırasında kendi erişimini kesebilirsin.

---

## 1. Siteyi Cloudflare'e ekle
1. cloudflare.com → ücretsiz hesap → **Add a site** → `cloudposgrid.com`.
2. Cloudflare iki **nameserver** verir. Alan adı sağlayıcında (kayıt firması)
   NS kayıtlarını bunlarla değiştir. Yayılma birkaç dakika–saat sürebilir.

## 2. DNS kayıtları — hepsi "Proxied" (turuncu bulut)
DNS sekmesinde şu A kayıtları sunucu IP'sine baksın ve **turuncu bulut (Proxied)** olsun:

```
cloudposgrid.com        A   <SUNUCU_IP>   Proxied
www                     A   <SUNUCU_IP>   Proxied
app                     A   <SUNUCU_IP>   Proxied
api                     A   <SUNUCU_IP>   Proxied
```

Turuncu bulut = trafik CF üzerinden akar (koruma aktif). Gri bulut = doğrudan
sunucuya (korumasız) — sadece ilk certbot alırken geçici gerekebilir.

## 3. TLS/SSL
- **SSL/TLS → Overview → Full (strict)** seç.
- Sunucuda geçerli sertifika olmalı. İki seçenek:
  - **A)** Certbot ile Let's Encrypt (bkz. DEPLOY.md §7). İlk alırken ilgili kaydı
    geçici **gri bulut** yap, sertifika alınınca turuncuya çevir.
  - **B)** Cloudflare **Origin Certificate** üret (SSL/TLS → Origin Server →
    Create Certificate), sunucuya kur, nginx'te kullan. 15 yıl geçerli, pratiktir.
- **Always Use HTTPS: On**, **Automatic HTTPS Rewrites: On**.

## 4. Rate-limiting kuralı (ücretsiz planda 1 kural)
**Security → WAF → Rate limiting rules → Create rule:**
- **If**: `URI Path` `contains` `/` (tüm site) — ya da API'ye odaklamak için
  hostname `api.cloudposgrid.com`.
- **When rate exceeds**: `100` requests per `10 seconds` — **per IP**.
- **Then**: `Block` (veya `Managed Challenge`) — süre `10 minutes`.

Bu, tek bir IP'nin 10 saniyede 100'den fazla istek atmasını CF kenarında keser.
"250 bin istek/saniye" senaryosu bu kuralla origin'e ulaşamadan biter.

## 5. Bot ve güvenlik ayarları
- **Security → Bots → Bot Fight Mode: On** (ücretsiz).
- **Security → Settings → Security Level: High**.
- **Under Attack Mode**: aktif bir saldırı anında **Security → Settings →
  Security Level → I'm Under Attack** yap. Her ziyaretçiye 5 sn'lik JS challenge
  koyar; botları eler. Saldırı geçince **High**'a geri al.

## 6. ⭐ Origin kilidi (KRİTİK) — CF'i baypaslamayı engelle
Saldırgan sunucunun gerçek IP'sini bulursa Cloudflare'i atlayıp doğrudan
saldırabilir. Bunu önlemek için sunucu **80/443'te SADECE Cloudflare IP'lerini**
kabul etsin:

```bash
# SSH erişimini KAYBETMEMEK için önce SSH'in açık olduğundan emin ol:
sudo ufw allow OpenSSH

# Cloudflare IP aralıklarını çek ve sadece onlardan web trafiğine izin ver:
for ip in $(curl -s https://www.cloudflare.com/ips-v4); do
  sudo ufw allow from $ip to any port 80,443 proto tcp
done
for ip in $(curl -s https://www.cloudflare.com/ips-v6); do
  sudo ufw allow from $ip to any port 80,443 proto tcp
done

# Herkese açık olan genel web kuralını kaldır (artık sadece CF girebilir):
sudo ufw delete allow 'Nginx Full'
sudo ufw reload
sudo ufw status numbered
```

> Bundan sonra gerçek ziyaretçi IP'si nginx'e `CF-Connecting-IP` başlığıyla gelir;
> `cloudposgrid-limits.conf` bunu zaten çözer (loglar ve rate-limit'ler gerçek IP'ye
> göre çalışır). CF IP listesi nadiren değişir; yılda bir bu adımı tazele.

## 7. (Opsiyonel) fail2ban'ı Cloudflare ile kullan
CF arkasında iptables banı gerçek saldırganı tutmaz (kaynak = CF edge IP).
Gerçek IP'yi CF seviyesinde banlamak istersen fail2ban'ın `cloudflare` action'ını
kullan: `/etc/fail2ban/action.d/cloudflare.conf` içine CF API token'ı gir ve
`jail.d/cloudposgrid.conf` içindeki `banaction = iptables-allports` satırını
`banaction = cloudflare` yap. Çoğu durumda **4. adımdaki CF rate-limit kuralı**
yeterlidir; fail2ban'ı sadece ek katman istersen kur.

---

## Özet — hangi katman neyi durdurur

| Katman | Durdurduğu |
|---|---|
| Cloudflare edge + rate-limit kuralı (4) | Volumetrik + L7 sel; origin'e ulaşmadan |
| Under Attack Mode (5) | Aktif saldırı anı; bot JS-challenge |
| Origin kilidi (6) | CF'i baypaslayıp doğrudan IP'ye saldırma |
| nginx `limit_req`/`limit_conn` | Kenardan sızan/CF'siz dönemde IP başına sel |
| ASP.NET GlobalLimiter | Uygulamaya ulaşan aşırı hacim (son bariyer) |
| fail2ban | Israrlı kaynağı komple banlama |

Bu altı katman birlikte, "bir kişinin 100–250 bin isteği"ni **fark edilmeden**
soğurur; büyük dağıtık saldırıda bile origin ayakta kalır.
