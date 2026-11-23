# SmartGallery

Lokální galerie fotografií a videí pro Windows 11. Uspořádej kopie médií na časové ose, vyhledej jejich obsah pomocí lokálních AI popisků a bezpečně vyřaď shodné fotografie bez zásahu do zdrojových originálů.

## Co umí

Novinky ve [SmartGallery 1.2](docs/release.md#smartgallery-12--windows-x64).

- **Import bez změny originálů:** vyber nebo přetáhni složku z Průzkumníka. SmartGallery zkopíruje média do spravované knihovny, ověří přenos pomocí SHA-256 a nepřepíše existující soubory. Opakovaný import vytváří další kopie.
- **Procházení a vyhledávání:** časová osa, náhledy, podrobnosti a kombinovatelné filtry podle typu média, dostupné přípony a stavu AI. Mimo časovou osu lze seřadit celou galerii podle názvu, velikosti nebo data pořízení oběma směry. Bez data v metadatech se použije dostupné datum z časové osy; u videí mohou metadata uvádět datum kódování. Kliknutím na název upravíš jméno kopie, přípona zůstane.
- **Přehrávání a převod videí (Windows x64):** kompatibilní videa včetně HEVC lze přehrát v miniatuře nebo v detailu, pokud má Windows potřebný dekodér. V detailu je dostupný také FFplay. V nabídce **Potřebné soubory** lze po potvrzení stáhnout ověřený balíček FFmpeg z Gyan.dev (GPLv3). Převod lze spustit i u přehratelných videí. Podle vstupu a místních možností [FFmpeg](https://ffmpeg.org/ffmpeg.html) lze vybrat MP4 (tři kvality), MOV, WebM, kopii kompatibilních stop bez překódování do MP4/MKV nebo archiv MKV/FFV1. Ve výchozím nastavení převod vytvoří novou kopii. S volbou **Nahradit původní kopie v galerii** ověřený výstup nahradí kopii v knihovně bez uložení do koše; název zůstane a přípona odpovídá výslednému formátu. Zdrojové originály se nezmění.
- **Další video:** v detailu přejdeš na další video v aktuálně zobrazeném pořadí galerie. Fotografie se přeskočí.
- **Převod fotografie:** v detailu zvol **Převést fotografii…**. [NetVips/libvips](https://github.com/kleisauke/net-vips) převádí lokálně do optimalizovaného PNG, JPEG a ztrátového i bezeztrátového WebP. U JPEG a ztrátového WebP nastavíš kvalitu; u všech formátů můžeš omezit délku delší strany. Pro jednosnímkové fotografie už neplatí limit 32 Mpx; WebP má limit 16 383 px na stranu. Orientace se promítne do pixelů, EXIF se nepřenáší. PNG a WebP zachovají průhlednost. Výchozí volba vytvoří novou kopii. Před nahrazením kopie v galerii uvidíš skutečnou velikost výsledku, rozměry a procentuální rozdíl.
- **Lokální AI popisky:** Florence-2 popíše vybrané nebo dosud neanalyzované fotografie na CPU. Popisky bývají anglické a mohou se mýlit. Návrhy názvů se použijí pouze po potvrzení a změní jen kopie v knihovně.
- **Vlastní koš:** tlačítka **Do koše…** a **Trvale smazat…** jsou přímo v galerii i v detailu videa. Obě akce vyžadují potvrzení; obnovit lze jen kopie přesunuté do koše.
- **Automatická údržba katalogu:** při spuštění, aktualizaci nebo po zjištění chybějící položky se zkontroluje celý katalog. Záznamy o potvrzeně chybějících souborech a složkách se odstraní až po záloze SQLite do `%LOCALAPPDATA%\SmartGallery\CatalogBackups`. Položky z nepřístupných složek galerie skryje bez mazání záznamů; až budou dostupné, vrať je do zobrazení přes **Soubor → Aktualizovat**. Soubory na disku se nemění.

- **Hromadný převod fotografií:** označ fotografie v galerii a zvol **Převést vybrané fotografie…**. Při nahrazování můžeš potvrdit stejnou volbu pro celý zbytek výběru. Spodní panel ukazuje procenta pro aktuální soubor i celou úlohu.
- **Nástroje → Analýza galerie…** otevře samostatné okno. Zvol celou galerii nebo vybrané fotografie a klikni na **Spustit kontrolu**. Okno ukazuje aktuální soubor s náhledem, fázi, celkový průběh a případné potíže. Během kontroly můžeš hlavní galerii procházet a filtrovat. Minimalizace kontrolu nepřeruší, zavření okna ji zastaví.
- Analýza s připraveným místním AI modelem doplní chybějící popisy. Pak vyhledá shodné kopie, podobné fotografie, možné screenshoty a úspory. Výsledky lze filtrovat podle kategorie; souhrn vždy ukazuje akce pro celý výběr. U screenshotů a bajtově shodných kopií je předvoleno **Do koše**, chráněné fotografie zůstanou ponechané. Volbu můžeš změnit jednotlivě nebo hromadně. Přesun i optimalizace proběhnou až po potvrzení. Optimalizace nahradí kopii jen menším souborem se shodnými rozměry, pixely a barevným profilem; EXIF se nepřenáší. Místo v koši se uvolní až po vysypání.

## Spuštění

1. V sekci **Releases** stáhni `SmartGallery-win-x64.zip`. Pokud tam ZIP ještě není, binární vydání zatím není zveřejněné.
2. Rozbal celý ZIP do jedné složky na Windows 11 x64. Spusť `SmartGallery.exe` z rozbalené složky; ostatní soubory ponech vedle něj.
3. Vyber prázdnou cílovou složku knihovny, nastav pravidla importu a jako první zkus malou sadu médií.

Vydání je připravené jako rozbalovací aplikace bez MSIX instalátoru. Nový katalog se zakládá při prvním spuštění do `%LOCALAPPDATA%\SmartGallery\catalog.db`. Při výměně vydání použij novou složku; katalog ani knihovna nejsou součástí ZIPu. Starší schéma katalogu se automaticky neaktualizuje: nejdřív [zálohuj a aktualizuj existující katalog](docs/database.md#aktualizace-existujícího-katalogu).

## Soukromí a místo na disku

Fotografie, jejich názvy a popisky se neodesílají do cloudové služby. Aplikace nemá vlastní telemetrii. Chybějící soubory modelu AI se stahují z Hugging Face (celkem přibližně 1,09 GB) až po potvrzení v nabídce **Potřebné soubory**; stahování lze zastavit. Import a galerie fungují bez modelu. [Podrobnosti o modelu a síťovém chování](docs/ai-models.md).

Spravovaná knihovna potřebuje místo na kopie médií. SQLite uchovává katalog a výsledky analýzy, nikoli fotografie nebo videa.

## Vývoj a vydání

Projekt používá C# 14, WinUI 3, Windows App SDK, .NET 10 jako cílovou platformu a SQLite. Otevři `SmartGallery.slnx` ve Visual Studiu 2026 s podporou WinUI 3 a použij **Debug | x64**, profil **SmartGallery (Unpackaged)**.

- [Publikace Windows x64 a texty pro GitHub](docs/release.md)
- [Architektura](docs/architecture.md)
- [Katalog SQLite](docs/database.md)
