# SmartGallery

Lokální galerie fotografií a videí pro Windows 11. Uspořádej kopie médií na časové ose, vyhledej jejich obsah pomocí lokálních AI popisků a bezpečně vyřaď shodné fotografie bez zásahu do zdrojových originálů.

## Co umí

- **Import bez změny originálů:** vyber nebo přetáhni složku z Průzkumníka. SmartGallery zkopíruje média do spravované knihovny, ověří přenos pomocí SHA-256 a nepřepíše existující soubory. Opakovaný import vytváří další kopie.
- **Procházení a vyhledávání:** časová osa po letech a měsících, samostatné skupiny videí, náhledy, textové podrobnosti a filtry podle typu média nebo stavu AI analýzy.
- **Lokální AI popisky:** Florence-2 popíše vybrané nebo dosud neanalyzované fotografie na CPU. Popisky bývají anglické a mohou se mýlit. Návrhy názvů se použijí pouze po potvrzení a změní jen kopie v knihovně.
- **Duplicity a vlastní koš:** vyhledávání shodných pixelů, ne podobných snímků. Vybrané kopie lze po potvrzení přesunout do koše galerie a obnovit.

## Spuštění

1. V sekci **Releases** stáhni `SmartGallery-win-x64.zip`. Pokud tam ZIP ještě není, binární vydání zatím není zveřejněné.
2. Rozbal celý ZIP do jedné složky na Windows 11 x64. Spusť `SmartGallery.exe` z rozbalené složky; ostatní soubory ponech vedle něj.
3. Vyber prázdnou cílovou složku knihovny, nastav pravidla importu a jako první zkus malou sadu médií.

Vydání je připravené jako rozbalovací aplikace bez MSIX instalátoru. Nový katalog se zakládá při prvním spuštění do `%LOCALAPPDATA%\SmartGallery\catalog.db`. Při výměně vydání použij novou složku; katalog ani knihovna nejsou součástí ZIPu. Starší schéma katalogu se automaticky neaktualizuje: nejdřív [zálohuj a aktualizuj existující katalog](docs/database.md#aktualizace-existujícího-katalogu).

## Soukromí a místo na disku

Fotografie, jejich názvy a popisky se neodesílají do cloudové služby. Aplikace nemá vlastní telemetrii. Pro funkce AI se chybějící soubory modelu po startu automaticky stahují z Hugging Face (přibližně 1,09 GB); stahování lze zastavit. Import a galerie fungují bez modelu. [Podrobnosti o modelu a síťovém chování](docs/ai-models.md).

Spravovaná knihovna potřebuje místo na kopie médií. SQLite uchovává katalog a výsledky analýzy, nikoli fotografie nebo videa.

## Vývoj a vydání

Projekt používá C# 14, WinUI 3, Windows App SDK, .NET 10 jako cílovou platformu a SQLite. Otevři `SmartGallery.slnx` ve Visual Studiu 2026 s podporou WinUI 3 a použij **Debug | x64**, profil **SmartGallery (Unpackaged)**.

- [Publikace Windows x64 a texty pro GitHub](docs/release.md)
- [Architektura](docs/architecture.md)
- [Katalog SQLite](docs/database.md)
