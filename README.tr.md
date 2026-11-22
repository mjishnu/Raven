<p align="center">
  <img src="Raven/Assets/Raven.ico" alt="Raven Logo" width="128" height="128">
</p>

<h1 align="center">Raven</h1>

<p align="center">
  <b>Windows için ücretsiz, açık kaynaklı alternatif bir Microsoft Store istemcisi</b>
</p>

<p align="center">
  <a href="README.md">English</a> | <b>Türkçe</b>
</p>

<p align="center">
  <a href="https://github.com/mjishnu/Raven/releases"><img src="https://img.shields.io/github/v/release/mjishnu/Raven?style=flat-square&color=blue" alt="GitHub Release"></a>
  <a href="https://github.com/mjishnu/Raven/blob/main/LICENSE"><img src="https://img.shields.io/github/license/mjishnu/Raven?style=flat-square&color=green" alt="License"></a>
  <a href="https://github.com/mjishnu/Raven/stargazers"><img src="https://img.shields.io/github/stars/mjishnu/Raven?style=flat-square" alt="Stars"></a>
  <a href="https://github.com/mjishnu/Raven/issues"><img src="https://img.shields.io/github/issues/mjishnu/Raven?style=flat-square" alt="Issues"></a>
  <a href="https://discord.gg/9eeN2Wve4T"><img src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fdiscord.com%2Fapi%2Fv10%2Finvites%2F9eeN2Wve4T%3Fwith_counts%3Dtrue&query=%24.approximate_member_count&label=Discord&logo=discord&logoColor=white&color=5865F2&style=flat-square&suffix=%20members&cacheSeconds=3600" alt="Discord"></a>
</p>

---

Raven, Microsoft Store'a tam donanımlı bir alternatif sunan modern ve yerel bir Windows uygulamasıdır. Resmi mağazanın yaptığı her şeyi — **uygulama arama, indirme, yükleme ve güncelleştirme** — yapabilir; ayrıca **harici UWP/MSIX paketlerini yan yükleme**, **Store uygulamalarını çevrimdışı kullanım için dışa aktarma** ve **bant genişliğinden tasarruf sağlayan delta indirmeler** gibi güçlü yetenekler de ekler.

**WinUI 3** ve **.NET 10** ile geliştirilen Raven, Windows 10 ve 11'de yerini doğal biçimde alan temiz ve akıcı bir arayüz sunar.

<img width="996" height="543" alt="raven" src="https://github.com/user-attachments/assets/40229b36-df5e-419d-aea7-ca374f6e2108" />

## ✨ Özellikler

### 🔍 Arama ve Göz Atma
- **Store Araması** — Microsoft Store kataloğunda doğrudan başlık çubuğundan, uygulama simgeleri ve başlıklarıyla birlikte gerçek zamanlı otomatik öneriler eşliğinde arama yapın.
- **Gelişmiş Arama** — uygulamaları sonuçlar üzerinde daha ayrıntılı denetimle filtreleyin ve sorgulayın.
- **Uygulama Ayrıntıları** — açıklamalar, ekran görüntüleri, sürüm bilgileri ve bağımlılık listeleriyle eksiksiz uygulama ayrıntı sayfaları.
- **Pazar ve Dil Seçimi** — Store'a istediğiniz bölge/dil birleşimiyle göz atın.

### ⬇️ İndirme ve Dışa Aktarma
- **Store Uygulamalarını İndirme** — çevrimdışı yükleme veya arşivleme için herhangi bir uygulama paketini doğrudan Microsoft'un CDN'inden indirin.
- **Delta İndirmeler** — akıllı blok düzeyinde delta indirmelerle bant genişliğinden tasarruf edin. BlockMap karşılaştırması sayesinde yalnızca değişen bloklar alınır ve güncelleştirme boyutları büyük ölçüde azalır.
- **Paketleri Dışa Aktarma** — harici kullanım, yedekleme veya başka bilgisayarlara dağıtım için `.appx`, `.msix`, `.appxbundle` ve `.msixbundle` dosyalarını indirin.
- **İndirme Yöneticisi** — ilerleme takibi, duraklatma/sürdürme ve durum animasyonlarıyla tam bir indirme kuyruğu.

### 📦 Yükleme ve Yan Yükleme
- **Store Uygulamalarını Yükleme** — indirilen paketleri tıpkı Microsoft Store gibi doğrudan yükleyin.
- **Harici Paketleri Yan Yükleme** — yalnızca Store'dan değil, her yerden `.appx`, `.msix`, `.appxbundle` veya `.msixbundle` dosyalarını yükleyin. Sürükleyip bırakın veya göz atarak seçin.
- **Bağımlılık Çözümleme** — gerekli çerçeve bağımlılıklarının otomatik algılanması ve yüklenmesi.
- **Zorla Yükleme** — daha yeni bir sürüm zaten mevcutken paketleri zorla yeniden yükleme veya sürüm düşürme seçeneği.

### 🔄 Güncelleştirmeler
- **Güncelleştirme Denetimi** — Store tarafından imzalanmış, yüklü tüm paketlenmiş uygulamaları tarar ve en son kullanılabilir sürümlerle karşılaştırır.
- **Delta Güncelleştirmeler** — indirme boyutlarını en aza indirmek için blok düzeyinde farksal güncelleştirmeler uygular.
- **Toplu ve Tekil Güncelleştirmeler** — tüm uygulamaları tek seferde güncelleştirin veya hangilerinin güncelleştirileceğini kendiniz seçin.
- **Sürüm Karşılaştırma** — mimari ve işletim sistemi derlemesi başına en son kullanılabilir sürümü akıllıca belirler.

### ⚙️ Genel
- **Paketlenmemiş Dağıtım** — MSIX paketlemesi veya Uygulama Yükleyici gerektirmeden bağımsız bir `.exe` olarak çalışır.
- **Tema Desteği** — açık, koyu ve sistem varsayılanı temaları arasında sorunsuz geçiş.
- **Yapılandırılmış Günlük Kaydı** — Serilog ile çalışma zamanı olayları, yüklemeler ve çökmeler için ayrı günlük dosyaları.
- **Yerelleştirmeye Hazır** — arayüz metinleri kaynak tabanlı bir yerelleştirme sistemi (`x:Uid`) kullanır.
- **Kendini Güncelleştirme Denetimi** — Raven'ın kendisinin daha yeni sürümleri için GitHub'ı denetler.

## 🛑 Sistem gereksinimleri

- **Windows 10** Sürüm 2004, Derleme 19041+
- [**Windows App SDK Çalışma Zamanı**](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads#windows-app-sdk) (Kendi kendine yeten sürüm için gerekli değildir)

## 🌐 Nasıl çalıştırılır
#### 1. Sürümler
- Tüm [sistem gereksinimlerini](#-sistem-gereksinimleri) karşılayın (kendi kendine yeten sürümü çalıştırıyorsanız çalışma zamanları hariç).
- Sistem mimarinize uygun Raven'ın en son sürümünü [sürümler](https://github.com/mjishnu/Raven/releases) sayfasından indirin.
- Zip dosyasının içeriğini ayıklayın ve `raven.exe` dosyasını çalıştırın.
- Yanlış pozitif bir virüsten koruma uyarısıyla karşılaşırsanız [sürümler](https://github.com/mjishnu/Raven/releases) sayfasından `raven_cert.zip` dosyasını indirin, içeriğini ayıklayın ve `raven.cer` dosyasını yükleyin ya da `install_raven_cert.bat` dosyasını çalıştırın.

#### 2. Winget
- [winget](https://learn.microsoft.com/en-us/windows/package-manager/winget/) yükleyin ve şunu çalıştırın:
```ps1
winget install mjishnu.raven
```
- Yanlış pozitif bir virüsten koruma uyarısıyla karşılaşırsanız [sürümler](https://github.com/mjishnu/Raven/releases) sayfasından `raven_cert.zip` dosyasını indirin, içeriğini ayıklayın ve `raven.cer` dosyasını yükleyin ya da `install_raven_cert.bat` dosyasını çalıştırın.
- Başlat menüsünde `raven` araması yapın ve çalıştırın.

### ▶️ Video Kılavuzu
[<img width="996" height="543" alt="IMM-WZsVCt5_E" src="https://github.com/user-attachments/assets/80fd984c-8587-49a0-ae13-07bee72d9a9d" />
](https://www.youtube.com/watch?v=ZX__BaD6kr0)


## 🏗️ Mimari

Raven, **MVVM desenini** izler ve `Microsoft.Extensions.Hosting` aracılığıyla bağımlılık ekleme kullanır.

```
Raven.slnx
├── Raven/                    # WinUI 3 Uygulaması (arayüz katmanı)
│   ├── Views/                # XAML sayfaları: Shell, Search, App Details,
│   │                         #   Downloads, Installations, Updates, Settings
│   ├── ViewModels/           # MVVM görünüm modelleri (CommunityToolkit.Mvvm)
│   ├── Services/             # Uygulama düzeyi hizmetler: gezinme, indirmeler,
│   │                         #   paket yükleme, güncelleştirme denetimi
│   ├── Helpers/              # Yardımcılar: delta indirmeler, BlockMap ayrıştırma,
│   │                         #   indirme URL'si çözümleme, sürüm karşılaştırma
│   ├── Models/               # Veri modelleri: AppInfo, DownloadItem, UpdateItem
│   ├── Contracts/            # Hizmet arayüzleri
│   ├── Layouts/              # Özel WinUI düzenleri (VirtualGridLayout)
│   ├── Styles/               # XAML kaynak sözlükleri
│   └── Strings/              # Yerelleştirilmiş metin kaynakları (en-us, hu-HU, ko-kr, tr-TR)
│
├── Raven.Updater/            # Kendini güncelleştirme yardımcı yürütülebilir dosyası
│   └── Program.cs            # Güncelleştirme yükünü kopyalar ve Raven'ı yeniden başlatır
│
└── StoreListings/            # Git alt modülü — Microsoft Store API sarmalayıcısı
    └── StoreListings.Library/
        ├── StoreEdgeFDProduct.cs   # Store ürün sorguları
        ├── DCATPackage.cs          # Bağımlılık kataloğu aramaları
        └── FE3Handler.cs           # Paket indirme bağlantısı çözümleme
```

### Temel Bağımlılıklar

| Paket | Amaç |
|---|---|
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | WinUI 3 çerçevesi |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM kaynak oluşturucuları ve yardımcıları |
| [CommunityToolkit.WinUI](https://github.com/CommunityToolkit/Windows) | WinUI medya denetimleri ve efektleri |
| [Downloader](https://github.com/bezzad/Downloader) | Çok parçalı dosya indirme altyapısı |
| [WinUIEx](https://github.com/dotMorten/WinUIEx) | Pencere yönetimi uzantıları |
| [Serilog](https://serilog.net/) | Yapılandırılmış günlük kaydı |
| [StoreListings](https://github.com/mjishnu/StoreListings) | Microsoft Store API sarmalayıcısı (alt modül) |

## 📋 Derleme Önkoşulları

- **.NET 10 SDK**
- Aşağıdaki iş yüklerine sahip **Visual Studio 2026**:
  - .NET masaüstü geliştirme
  - Windows App SDK / WinUI geliştirme
  - Windows 10 SDK (26100)
  - C++ ile masaüstü geliştirme (Raven, NativeAOT olarak yayımlandığı için yayımlama sırasında MSVC bağlayıcısı gerekir)

## 🚀 Başlarken

### 1. Depoyu Klonlayın

```bash
git clone --recurse-submodules https://github.com/mjishnu/Raven.git
cd Raven
```

> Depoyu `--recurse-submodules` olmadan zaten klonladıysanız alt modülü el ile başlatın:
> ```bash
> git submodule update --init --recursive
> ```

### 2. Derleyin ve Çalıştırın

**Visual Studio'dan:**
1. `Raven.slnx` dosyasını açın
2. `Raven` projesini başlangıç projesi olarak ayarlayın
3. Hedef platformunuzu seçin (`x64`, `x86` veya `arm64`)
4. Derleyip çalıştırmak için **F5** tuşuna basın

**Komut satırından:**
```bash
dotnet build Raven.slnx -c Debug -p:Platform=x64
dotnet run --project Raven -c Debug
```

### Desteklenen Platformlar

| Mimari | Durum |
|---|---|
| x64 | ✅ Destekleniyor |
| x86 | ✅ Destekleniyor |
| ARM64 | ✅ Destekleniyor |

## 🤝 Katkıda Bulunma

Katkılarınızı bekliyoruz! Nasıl yardımcı olabileceğiniz:

1. Depoyu **çatallayın (fork)**
2. Özelliğiniz veya düzeltmeniz için bir **dal oluşturun** (`git checkout -b feature/my-feature`)
3. Değişikliklerinizi **işleyin (commit)** (`git commit -m "Add my feature"`)
4. Dalınızı **gönderin (push)** (`git push origin feature/my-feature`)
5. Bir **Çekme İsteği (Pull Request) açın**

### Kod Stili

- XAML'de `x:Uid` tabanlı yerelleştirilmiş metinler kullanın — sabit kodlanmış metin kullanmayın
- Mevcut MVVM desenlerini ve bağımlılık ekleme kurallarını izleyin

## ⭐ Teşekkürler

- Microsoft Store API sarmalayıcısı için [StoreListings](https://github.com/dongle-the-gadget/StoreListings).
- Bu projenin öncülü olan [Alt App Installer](https://github.com/mjishnu/alt-app-installer).

## 📜 Lisans

Bu proje **Apache License 2.0** kapsamında lisanslanmıştır — ayrıntılar için [LICENSE](LICENSE) dosyasına bakın.
