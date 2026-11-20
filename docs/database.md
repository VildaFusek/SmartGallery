# Katalog SQLite – kroky 1.1 až 1.6

Připraven je EF Core model, čtecí adaptéry, DI a design-time factory.
Uživatel vygeneroval InitialCatalog a oznámil dokončení Update-Database.
Migrační soubory a snapshot byly staticky zkontrolovány.
Balíčky `Microsoft.EntityFrameworkCore.Sqlite`, `Design` a `Tools` 10.0.15
byly ověřeny ve veřejném registračním API NuGet.
Instalaci/restore, build a všechny migrační příkazy provádí uživatel.

## Model

| Tabulka | Data |
| --- | --- |
| Libraries | Id, Name, RootPath, CreatedAt, FolderLayout, FileNaming |
| Assets | Id, LibraryId, ImportedAt |
| AssetFiles | Id, AssetId, RelativePath, SizeBytes, LastWriteTimeUtc |
| PhotoAnalyses | FileId, Caption, Truncated, SuggestedName, AnalyzedAtUtcTicks |

GUID přiděluje volající, nikoli databáze. Časy se převádějí na UTC ticks
typu INTEGER; původní časový offset se neuchovává, přesnost okamžiku ano.
Vazby Library → Asset → AssetFile mají zakázané kaskádové mazání.
Volitelný výsledek PhotoAnalyses je vázaný na ID kopie, takže zůstává po přejmenování
a odstraní se až při odebrání záznamu kopie z katalogu.
Existují indexy cizích klíčů dle konvencí EF Core, index RootPath a unikátní
index (AssetId, RelativePath).

Index RootPath zatím není unikátní: normalizace a porovnávání kořenových
cest Windows se doplní před zápisem knihoven. Tento model zatím negarantuje
jedinečnost cest podle pravidel souborového systému. Stejně tak před importem
zbývá validace cest, velikostí a identifikátorů.

`SqliteLibraryCatalog` získává pro každé čtení samostatný kontext z
`IDbContextFactory`, používá no-tracking, CancellationToken a asynchronní I/O.
Vrací pouze seznam knihoven, nikoli všechny snímky.

## Konfigurace a bezpečnost

App registruje `IDbContextFactory<CatalogDbContext>` a `ILibraryCatalog`.
Databáze je v `%LOCALAPPDATA%\SmartGallery\catalog.db`, nikoli vedle EXE
nebo v kořenových složkách fotografií. Cesta je společná pro runtime i EF nástroje
a je určena pro aktuálního uživatele.

Při běžné práci runtime používá `Mode=ReadWrite`. Při prvním načtení, pokud
soubor chybí, aplikace použije existující EF migrace na dočasnou prázdnou
databázi ve stejné složce a po zavření ji přesune na finální cestu bez přepsání.
Pokud zůstaly pomocné soubory SQLite bez hlavní databáze, založení se zastaví.
Běžná spojení i nástroje EF mají zapnuté cizí klíče a timeout čekání na zámky
5 sekund. Existující katalog se při spuštění automaticky neaktualizuje.

## Aktualizace existujícího katalogu

1. Ukonči všechny instance SmartGallery a zajisti konzistentní zálohu katalogu.
   Soubory `catalog.db-wal` a `catalog.db-shm` svévolně nemaž.
2. Otevři zdroje odpovídající novému vydání ve Visual Studiu, obnov závislosti
   a otevři **Tools > NuGet Package Manager > Package Manager Console**.
3. V této konzoli spusť:

   ```powershell
   Update-Database -Context CatalogDbContext -Project SmartGallery.Infrastructure -StartupProject SmartGallery.Infrastructure
   ```

4. Spusť novou verzi a zkontroluj připojení katalogu a zobrazení knihovny.

`InitialCatalog`, `AddLibraryImportSettings` i `AddPhotoAnalyses` jsou již ve
zdrojích. Pro aktualizaci katalogu nespouštěj `Add-Migration`.

## Ověření připojení v UI – krok 1.3

Galerie přes `ICatalogStatusReader` zajistí prvotní založení, ověří čekající migrace a přečte
`PRAGMA journal_mode`, počet knihoven a přítomnost alespoň jednoho snímku.
U existujícího katalogu nemění schéma ani journal režim a neskenuje originály.
Dotazy běží mimo UI thread kvůli synchronní povaze SQLite async API.

Po spuštění očekávej „Katalog připojen. Počet knihoven: 0. SQLite journal: wal.“
pro novou prázdnou databázi. Jiný journal režim se zobrazí jako upozornění,
nezakryje se. Tlačítko „Znovu načíst stav katalogu“ zopakuje skutečné dotazy.
Selhání založení, neúplné schéma nebo chyba přístupu se zobrazí jako chyba,
nikoli jako prázdný katalog. Podrobnosti se zapisují do debug logu.

## Cílová složka spravované knihovny – krok 1.4

Uživatel potvrdil funkční zobrazení stavu katalogu a WAL.
Galerie nyní načítá také uložené knihovny. Když žádná není nastavena,
zpřístupní tlačítko „Zvolit cílovou složku galerie“.

Vyber existující **prázdnou** složku určenou pro budoucí kopie, ne adresář
se zdrojovými fotografiemi. Registrace ověří absolutní cestu, existenci,
prázdnost a odmítne kořen disku/sdílení nebo cestu přes reparse points.
Prázdnost je ověřena pouze při registraci; před importem bude nutná nová
kontrola cíle, přístupových práv a kolizí. Zapisovatelnost se zatím netestuje
vytvářením souborů. Aplikace nemění žádný obsah vybraného adresáře.

Do Libraries se uloží nový GUID, název adresáře, normalizovaná absolutní cesta
a UTC čas. Kontrola existující knihovny a zápis jsou v serializované SQLite
transakci, aby dva současné požadavky nevytvořily různé cíle.
Změna již nastaveného cíle zatím není podporována. Databáze nadále zůstává
v `%LOCALAPPDATA%\SmartGallery\catalog.db`.

Model tabulek se nemění: **není potřeba nová migrace**.

Ruční ověření:
1. Sestav a spusť Unpackaged. V novém katalogu očekávej 0 knihoven.
2. Vyzkoušej zrušení dialogu; záznam nesmí vzniknout.
3. Vyber neprázdnou složku; očekávej srozumitelnou chybu a žádné změny.
4. Vyber připravenou prázdnou složku; očekávej její cestu a 1 knihovnu.
5. Znovu načti stav a restartuj aplikaci; cesta musí zůstat uložena.
6. Tlačítko výběru má být po uložení zablokované. Cílová složka zůstává prázdná.

Tento krok zatím neprovádí analýzu ani samotné kopírování.
Import bude pouze kopírovat obsah bez změny originálů.

## Pravidla importu – krok 1.5

Volby se ukládají do Libraries jako TEXT (názvy enum hodnot):

| Volba | Hodnoty / modelový výsledek |
| --- | --- |
| FolderLayout (fotografie) | YearMonth → `2026\11\`, YearDay → `2026\2026-11-18\`, PreserveSourceFolders → `Dovolená\Den 1\` |
| FileNaming | KeepOriginal → `IMG_0001.jpg`, DatePrefix → `20261118_IMG_0001.jpg` |

Výchozí hodnoty jsou YearMonth a KeepOriginal, včetně již uložené knihovny.
Videa mají společné pravidlo pro všechny varianty FolderLayout: `ROK\Video`,
například `2026\Video\VID_0001.mp4`. FolderLayout vybírá strukturu fotografií,
FileNaming se vztahuje na fotografie i videa.
Názvy enum hodnot jsou součástí databázového formátu; nepřejmenovávat je
bez odpovídající datové migrace.

Ukázka v UI používá explicitně modelový soubor a čas, ne skutečná metadata.
Volby mají funkční uložení, ale ještě neprovádějí přejmenování ani vytváření
adresářů. Datum bez dostupných metadat, kolize názvů a bezpečná práce
s relativními cestami se vyřeší před skutečným náhledem/importem.
Zachování podadresářů bude relativní k vybranému zdroji, ne k absolutní cestě.

Pravidla lze nyní měnit jen u jedné nastavené knihovny bez importovaných snímků.
Zákaz změny po prvním snímku kontroluje i persistence v serializované transakci,
nikoli pouze UI. Změna pravidel nic fyzicky netřídí.
„Znovu načíst stav katalogu“ zahodí neuložené změny voleb a načte uložené.

### Migrace pravidel importu

Existující `AddLibraryImportSettings` přidává do `Libraries` dvě nenulové
položky typu TEXT: `FolderLayout` s výchozím `YearMonth` a `FileNaming`
s výchozím `KeepOriginal`. Pro starý katalog použij výše uvedený postup
[aktualizace existujícího katalogu](#aktualizace-existujícího-katalogu).

## Upřesnění hierarchie pro fotografie a videa – krok 1.5a

Uživatel potvrdil funkční uložení pravidel kroku 1.5.
Výchozí hierarchie budoucích kopií:

```text
Cílová složka/
└── 2026/
    ├── 01/
    │   └── IMG_0001.jpg
    ├── 11/
    │   └── IMG_0002.jpg
    └── Video/
        └── VID_0001.mp4
```

Měsíc fotografií je dvoumístné číslo 01–12. Video se nečlení podle měsíců.
Původní alternativy struktury fotografií zůstávají volitelné; nemění pravidlo
`ROK\Video`. Uložená volba se automaticky nemění ani nezahazuje.
Pokud chceš pro již nastavenou knihovnu výchozí strukturu, zvol
„Rok \ měsíc (doporučeno)“ a ulož pravidla.

UI má samostatné modelové ukázky pro fotografii a video; při změně pojmenování
se aktualizují obě. Změna nemění databázový model ani uložené enum hodnoty:
**není potřeba nová migrace**.
Zatím nejde o implementaci rozpoznávání médií, video metadat, přehrávání
nebo kopírování. Skutečný import musí tato společná pravidla teprve použít
a předem vyřešit neznámé datum, kolize a bezpečnost cest.

## Upřesnění importu a názvů – krok 1.6

Předimportní seznam není požadován a byl z UI odstraněn. Interní analýza
a výběr bezpečného jména se doplní spolu s importem. Průběh bude v kompaktním
okně se statistikou a rozbalovacím logem, nikoli v seznamu návrhů souborů.
Kompaktní okno přípravné analýzy je doplněno v kroku 1.7;
kopírování stále není implementováno.

DatePrefix přidává pouze datum `yyyyMMdd_`, nikoli čas.
Hodnoty enumů a tabulky se nemění: **nová migrace není potřeba**.
Předchozí uložená volba DatePrefix se zachová a nově bude znamenat pouze datum.
Žádný existující fyzický soubor se touto změnou nepřejmenovává.

Před skutečným importem se doplní čtení EXIF DateTimeOriginal / video metadat
a rozhodnutí pro chybějící nebo nespolehlivé datum. Čas vytvoření souboru
na disku ani čas poslední změny se nesmějí prezentovat jako skutečné datum
vzniku média. Scanner s LastWriteTimeUtc se používá pouze pro přípravnou
analýzu v kroku 1.7, nikoli ke skutečnému importu.

Ruční ověření: sestav a spusť Unpackaged, ověř nepřítomnost seznamu náhledu,
vyber „Datum + původní název“ a očekávej příklady `20261118_IMG_0001.jpg`
a `20261118_VID_0001.mp4`. Uložení pravidel a jejich zachování po restartu
mají zůstat funkční. Cílová složka zůstává beze změny.

## Importní plocha – krok 1.7

Schéma se nemění, nová migrace není potřeba. Po načtení knihovny a uložení
pravidel lze na importní plochu přetáhnout jednu složku z Průzkumníka
nebo ji vybrat tlačítkem „Importovat složku…“.
Jednotlivé soubory, více složek a virtuální položky bez dostupné cesty zatím
podporovány nejsou. Zdroj nesmí být totožný s cílem ani s ním být vnořený.

Otevře se kompaktní dialog přípravy. Zobrazuje skutečně prohlédnuté položky,
počet rozpoznaných médií (pouze podle přípony), ostatní soubory a na konci
kolize/blokované položky. Log je standardně sbalený a drží posledních 80 zpráv.
Příprava je omezená na 500 médií / 10 000 položek; při dosažení limitu
výslovně hlásí neúplný výsledek. Zdroj ani cíl se nemění, tabulky se neplní
snímky a počet zkopírovaných souborů je 0.

Ruční ověření:
1. Sestav a spusť Unpackaged bez zvýšených oprávnění. Ulož pravidla knihovny.
2. Tlačítkem vyber malou zdrojovou složku mimo cíl. Ověř fázi, statistiku,
   rozbalení/sbalení logu a sdělení, že kopírování není dostupné.
3. Totéž zopakuj přetažením jedné složky z Průzkumníka na vyznačenou plochu.
4. Ověř odmítnutí jednotlivého souboru, více složek a zdroje překrývajícího cíl.
5. Zavři dialog během větší analýzy; nesmí pokračovat jako skrytý import.
   Znovu otevři analýzu a ověř funkčnost.
6. Zkontroluj, že ve zdroji/cíli nepřibyly změny a počet snímků v DB se nezměnil.

Zavření žádá o kooperativní zrušení; blokující systémové I/O nemusí skončit
okamžitě. Windows může blokovat drop z nezvýšeného Průzkumníka do zvýšené aplikace.
Agent tyto kroky ani aplikaci nespouštěl.

## Skutečné kopírování a kompaktní formulář – krok 1.8

Tento oddíl nahrazuje omezení předchozího kroku 1.7: import nyní opravdu
kopíruje. **Nová migrace není potřeba**, existující Asset/AssetFile stačí.

- Nastavení je ve sbalené sekci, importní plocha je větší a zvýrazněná.
- Fotografie: Windows DateTaken. Video: metadata DateEncoded (datum kódování,
  nemusí odpovídat prvnímu vzniku videa). Bez metadat se pro zařazení a prefix
  použije místní čas poslední změny souboru nebo jeho vytvoření.
- Bez data: fotografie BezData, videa BezData\Video; u zachování podadresářů
  fotografií zůstává relativní struktura. Datumový prefix neobsahuje čas.
- Kolize názvů řeší číselná nebo unikátní přípona. Existující cíle se nepřepisují.
  Import stejných souborů znovu vytvoří nové kopie; deduplikace zatím není hotová.
- Kopie se ověřuje délkou a SHA-256, poté se zapisuje do katalogu.
- Skutečný import nemá pevný početní limit; plán ukládá do dočasné SQLite
  databáze. Chyba enumerace zastaví import před kopírováním. Blokované
  jednotlivé položky jsou vynechány.
- Katalog se nedělí: přibalené SQLite má výchozí maximum 4 294 967 294
  stránek (při stránce 4 KiB téměř 16 TiB). Média se ukládají mimo katalog;
  praktickými limity jsou místo na discích a RAM pro zobrazení celé časové osy.
- Obsahové přejmenování není součást importu. Samostatné tlačítko u vybrané
  fotografie po potvrzení přejmenuje kopii a aktualizuje `AssetFile.RelativePath`.

### Ruční ověření

1. Sestav solution a spusť Unpackaged. Nejprve použij **malou testovací složku**
   s několika fotografiemi a videem, včetně souboru bez metadat.
2. Vyber cílovou knihovnu, zkontroluj pravidla a spusť import tlačítkem.
   Zdroj nesmí být uvnitř cíle ani naopak.
3. Ověř fáze analýzy/kopírování, statistiku a log. Porovnej data/cesty
    s metadaty; u souboru bez metadat ověř datum poslední změny a zdroj v logu.
4. Ověř přítomnost kopií, jejich otevření a zachování originálů.
   Kopírování nemění obsah ani jméno zdroje.
5. Zopakuj import: původní cíle musí zůstat zachovány a nové kopie dostat
   číselný suffix. To není detekce duplicit.
6. Vyzkoušej zrušení během většího souboru. Dokončené kopie zůstávají,
   aktuální nepublikovaný temp soubor se má uklidit. Prázdné vytvořené složky
   mohou zůstat. Chybu úklidu nebo nezařazenou kopii řeš podle logu.
7. Restartuj App a ověř, že katalog obsahuje importované soubory.
   Samotné vizuální prohlížení médií zatím není implementováno.

Nově vytvořené soubory `.sg-import-*.tmp` se při běžném zrušení uklízejí,
ale po pádu procesu/výpadku mohou zůstat. Aplikace je automaticky plošně nemaže.
Pokud selže DB transakce po publikaci, hotová kopie zůstane na své cestě,
import se zastaví a statistika uvede nezařazenou kopii. Ruční odstranění
či další opravu automaticky neprováděj bez ověření.
Statistika při přerušení ukazuje poslední průběžné hodnoty, log drží 80 zpráv.
Výběr jednotlivých souborů, více složek, automatická obnova a lokální AI
pojmenování jsou navazující práce. Agent build, testy ani kopírování nespouštěl.
