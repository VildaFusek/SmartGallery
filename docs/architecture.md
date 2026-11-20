# Architektura

Modulární monolit. Jediný desktopový proces, .NET 10 / C# 14, WinUI 3.
Spouštění je unpackaged (`WindowsPackageType=None`), bez vývojářského režimu.
Windows App SDK je self-contained; [publikační profil x64](release.md) přibaluje i .NET runtime.
MSIX tooling a podpora spouštění s balíčkovou identitou přes WinApp jsou vypnuté.

## Hranice závislostí

| Modul | Reference / účel |
| --- | --- |
| SmartGallery (role App) | Application, Infrastructure, Worker; WinUI, MVVM, composition root |
| Domain | Bez projektových a balíčkových závislostí; Library, Asset, AssetFile |
| Application | Domain; kontrakt ILibraryCatalog |
| Infrastructure | Application; EF Core/SQLite adaptér a konfigurace, budoucí souborové adaptéry |
| Imaging | Application; Windows metadata, budoucí dekódování a náhledy |
| AI | Application; budoucí lokální inference |
| Worker | Application, Hosting.Abstractions; příprava modelu v procesu App, budoucí persistentní úlohy |

Infrastructure obsahuje SQLite katalog a import kopií, Imaging čte Windows metadata,
AI spravuje soubory lokálního modelu a Worker hostuje jeho přípravu na pozadí.
Při prvním spuštění se migracemi založí nový katalog; existující schéma vyžaduje ruční aktualizaci.
`ILibraryCatalog` je registrován v DI a používán při načítání knihovny v UI.
UI čte souhrnný stav přes `ICatalogStatusReader`: počet knihoven, přítomnost
snímků a journal režim. SQLite dotazy zůstávají v Infrastructure.
Výběr cíle obstarává WinUI služba `IFolderPicker`; ViewModel předá cestu
aplikační hranici `IManagedLibraryRegistration`. Její SQLite implementace
ověřuje cílový adresář a ukládá Library. UI neprovádí souborové operace.
Pravidla složek a pojmenování jsou typované enum hodnoty na Library.
Přes `ILibraryImportSettings` se ukládají jako TEXT s výchozími hodnotami.
Úpravy jsou zatím povoleny pouze před prvním importovaným snímkem.
Prázdná galerie nevydává prázdný výsledek za načtená data.
Žádné testovací, benchmarkové ani modelové placeholder projekty se nevytvářejí.

## UI a životní cyklus

Existující MainWindow a jeho Frame navigují na MainPage. MainPage získává
GalleryViewModel z DI; používá explicitní vlastnosti bez MVVM generátorů.
Při Loaded se spustí asynchronní příkaz pro načtení stavu, při Unloaded se
požádá o zrušení. Tlačítko umožňuje načtení zopakovat; za běhu je příkaz
blokovaný proti souběžnému spuštění. Chyby jsou viditelné a logované.
SQLite může provádět async operace synchronně, proto souhrnný dotaz běží
na thread poolu; změny ViewModelu se vracejí na UI thread.
Neexistují jiné obrazovky ani ovládání plánovaných funkcí.

App vytvoří a asynchronně spustí host před vytvořením hlavního okna.
Host nemá výchozí konfigurační zdroje; logging směřuje do debug výstupu.
Při zavírání okna se zavření dočasně zruší, host dostane požadavek na zastavení
s desetisekundovým cancellation tokenem, následně se uvolní a okno zavře.
Budoucí služby musejí respektovat zrušení; token nevynucuje ukončení služby,
která ho ignoruje. Životní cyklus vyžaduje ruční ověření ve VS.
Chyba inicializace se vypíše do debug výstupu a zobrazí samostatné chybové okno.

## Minimální doména a budoucí persistence

Neměnné datové záznamy identifikují knihovnu, logický snímek a fyzický soubor.
Library nese kořenovou cestu; AssetFile relativní cestu, velikost a čas změny.
Domain cesty pouze uchovává, nepoužívá souborový systém ani Windows API.
Validace importních vstupů, mapování a databázové invarianty přijdou s katalogem.

SQLite/EF Core model a existující migrace jsou součástí zdrojů. Při zakládání
nového katalogu se nejdřív připraví dočasná databáze, která se po uzavření
přesune na finální cestu bez přepsání. Ruční aktualizaci popisuje [katalog SQLite](database.md).
Podrobnosti a omezení modelu jsou v `database.md`.
Originály zůstanou při indexaci read-only.
AI ani biometrická data nepůjdou do cloudových služeb.

## Spravovaná knihovna – upřesněný směr

Uživatel vybírá cílovou složku pro vlastní kopie galerie, nikoli zdroj skenování.
Import bude výhradně kopírovat; zdroje se nikdy nepřejmenovávají, nepřesouvají
ani nemažou. Analýza, pojmenování a zařazení podle zvolené struktury budou
navazující kroky. Existující cílové soubory se nesmějí automaticky přepisovat.

V kroku 1.4 se pouze uloží jedna existující prázdná cílová složka. Cílový adresář
se nevytváří, neplní ani nepřejmenovává. Registrace odmítá kořen disku/sdílení
a konzervativně i reparse points na celé cestě. Změna cíle a více knihoven
nejsou zatím podporovány. SQLite katalog zůstává v lokálních datech aplikace;
vybraná složka je úložiště budoucích kopií, nikoli přenosná databáze.

Krok 1.5 přidává skutečné uložení pravidel a jasně označenou modelovou ukázku.
Ukázka není plánovač cest ani analýza fotografie. Před kopírováním musí
následovat implementace práce s metadaty, neznámým datem, validací cest
a kolizemi bez přepisování; aktuální ukázka se pro souborové operace nepoužívá.

Upřesnění 1.5a: výchozí struktura fotografií je `ROK\MĚSÍC` (01–12),
pro videa platí `ROK\Video` ve všech variantách. FolderLayout řídí pouze
fotografie; pravidlo názvu je společné oběma typům médií. Dosavadní varianty
fotografických složek i uložené enum hodnoty jsou zachovány.
Typ média se nyní rozlišuje příponou; dostupná metadata čte Windows adaptér.

## Import bez seznamu náhledu – upřesnění kroku 1.6

Uživatel nepotřebuje předimportní seznam souborů. UI tohoto seznamu
bylo odstraněno. Aplikace si možnosti pojmenování a kolize posoudí interně;
bez seznamu náhledu stále platí zákaz přepisování existujících cílových souborů.
Zdroje zůstanou read-only a import bude výhradně kopírovat.

Kompaktní okno má od kroku 1.8 funkční analýzu i kopírování,
statistiku a standardně sbalený rozbalovací log.

`IImportPreviewService` a FolderImportPreviewService jsou v kroku 1.7 zapojeny
pro interní přípravnou analýzu, bez zobrazení seznamu souborů.
Samostatný diagnostický náhled má limit 500 návrhů / 10 000 položek.
Náhled i import používají IMediaDateReader z Imaging; čas změny není datum
pořízení. Persistentní obnova importu zatím není dostupná.

Pro skutečné datum pořízení fotografie se má číst EXIF DateTimeOriginal;
pro video příslušná metadata záznamu. Ani metadata nejsou neomylná a mohou
chybět nebo obsahovat chybně nastavené datum fotoaparátu. CreationTime na disku
ani LastWriteTime nedokazují okamžik vzniku fotografie. Původní datum naskenované
papírové fotografie nelze odvodit z data skenování.

DatePrefix nyní přidává jen `yyyyMMdd_`, bez hodin/minut/sekund.
Uložený enum název zůstává zachován, nová migrace není potřeba.
Původní název se nezkracuje, takže případný čas již obsažený ve zdrojovém
názvu zůstává jeho součástí; aplikace sama nový čas nepřidává.

## Importní plocha a dialog – krok 1.7

Drag-and-drop obsluhuje Page pomocí StorageItems a deferralu, který ukončí
před otevřením dialogu. Přijímá jednu lokálně dostupnou složku; jiné vstupy
odmítne s vysvětlením. Tlačítko používá již existující WindowsFolderPicker.
Oba vstupy spouštějí stejný MVVM příkaz a `IImportDialogService`.
Vstup je zakázán bez načtené jediné knihovny, při neuložených pravidlech
nebo za běhu jiné operace.

ContentDialog je ukotven k XamlRoot hlavního okna. Má samostatný ViewModel,
skutečné čítače přípravy a Expander logu s nejvýše 80 posledními záznamy.
IProgress je vytvořen na UI threadu; scanner pracuje na thread poolu
a hlásí stav po 25 položkách a na konci. Pro neznámý celkový počet
se nepředstírá procentní průběh.

Zavření dialogu žádá o zrušení operace; služba před dokončením počká
na její ukončení. Unloaded stránky rovněž ruší importní příkaz.
Od kroku 1.8 dialog provádí skutečné kopírování a zobrazuje i jeho výsledky.
Neúplný nebo chybný průchod se nesmí označit jako bezchybný dokončený import.

## Metadata a kopírování – krok 1.8

Imaging je Windows knihovna (`net10.0-windows10.0.26100.0`), reference z App
slouží pouze composition rootu. WindowsMediaDateReader čte ImageProperties.DateTaken
a System.Media.DateEncoded. Datum kódování videa není důkazem původního záznamu;
výsledek obsahuje zdroj údaje. Bez použitelného data z metadat plán importu
použije místní čas poslední změny, případně vytvoření souboru. Náhradní datum
slouží jen k zařazení a pojmenování, ne jako datum pořízení. Pokud není použitelný
ani čas souboru, cíl je BezData / BezData\Video. Dostupnost metadat závisí na
Windows property handlerech a instalovaných kodecích.

FolderImportService běží mimo UI. Úplný plán a rezervace adresářů ukládá do
soukromé dočasné SQLite databáze, kterou SQLite odstraní při zavření spojení.
Plán čte po dávkách bez pevného limitu počtu položek; katalog zůstává jediný.
Zdroj otevírá jen pro čtení se sdílením Read.
V cíli vytváří vlastní nový `.sg-import-{guid}.tmp`, kopíruje a porovnává
SHA-256 přečtených dat se znovu přečtenou kopií. Po ověření a flush publikuje
soubor přejmenováním vlastního temp souboru s overwrite=false.
Při kolizi zkouší číselnou a pak unikátní příponu; respektuje i adresáře
vyžadované jinými položkami.
Před prací znovu ověřuje reparse points, nadřazené adresáře a příslušnost k cíli.
Zdrojový strom a cílový strom nesmějí být vzájemně vnořené.

Teprve publikovanou kopii zapisuje transakcí do Asset/AssetFile. Tyto tabulky
se nemění, migrace není potřeba. Po publikaci dokončí krátkou DB transakci
i při uživatelském zrušení. Pokud DB zápis selže, kopii nemaže, hlásí její cestu
jako nezařazenou a další kopírování zastaví. Již dokončené kopie se nevracejí zpět.
Při běžné chybě/zrušení se odstraňuje pouze vlastní čerstvě vytvořený temp soubor.

Při zavírání App se nejprve požádá o zrušení importu a čeká se na jeho ukončení,
teprve potom se zastaví host. Tvrdý pád/výpadek může ponechat temp soubor nebo
kopii bez DB záznamu; persistentní joby a obnova ještě nejsou implementovány.
Kontroly cest nejsou bezpečnostní sandbox proti souběžným úmyslným změnám,
neřeší všechny aliasy cest; uživatel nemá během importu měnit zdroj ani cíl.

Obsahové pojmenování čeká na skutečnou lokální inferenci. Není nahrazeno
vymyšlenými popisy scén ani cloudovým API. UI ho nabízí pouze jako jasně
označenou plánovanou/neaktivní variantu.

## Florence-2 – krok AI.1

Uživatel schválil komunitní ONNX převod. AI knihovna implementuje
ILocalCaptionModelInspector z Application, nezávisle na WinUI a databázi.
App jej skládá v DI. Vestavěný manifest připíná revizi, velikosti a SHA-256.
Kontrola je lokální, read-only, streamovaná, zrušitelná a běží mimo UI thread.
JSON používá source-generated metadata, nikoli reflektivní deserializaci.

Model se hledá v lokálních datech uživatele. Od kroku AI.2 se chybějící soubory
získávají automaticky, ale neprovádí se Python/custom remote code.
Ověření souborů není načtení ONNX grafů ani potvrzení funkčního captioningu.
Generování obsahových názvů zůstává zakázané.
Detaily a licenční podmínky viz `ai-models.md`.

## Automatická příprava modelu – krok AI.2

ModelPreparationWorker je BackgroundService knihovny Worker hostovaný v App.
Závisí pouze na aplikačním ILocalModelProvisioner a Hosting.Abstractions.
Zachována je uživatelská verze Hosting 11.0.0; Abstractions 11.0.0 byla ověřena
v lokální NuGet cache včetně podpory net10.0.

Worker spustí jeden pokus po startu a další jen na vyžádání přes omezený Channel.
Výjimka přípravy nezastavuje host ani galerii. Stav je neměnný snapshot
a událost; ModelFooterViewModel přenáší změny přes DispatcherQueue na UI thread
a při dispose se odhlašuje. Stav katalogu/importu publikuje UI ze skutečných
výsledků načtení a nastavené knihovny, nezávisle na stahování.

LocalModelProvisioner v AI čte vestavěný manifest, streamuje HTTPS přes sdílený
HttpClient, ověřuje délku/SHA-256 a publikuje vlastní nový temp soubor bez overwrite.
Síť se použije jen pro chybějící soubory. Neplatný existující soubor automaticky
neopravuje přepsáním. Povoleny jsou počáteční HTTPS adresy Hugging Face;
běžné HTTPS přesměrování na jejich CDN zajišťuje HttpClient.
Zrušení uklízí pouze vlastní temp; hotové soubory zůstávají. Výpadek procesu
může zanechat temp, HTTP resume a automatický úklid starých temp zatím nejsou.
Modelové soubory nejsou součástí operací s originálními fotografiemi.

Spodní lišta odlišuje FilesVerified, validaci ONNX runtime a dosud nezapojený captioning.
Automatický download je uživatelem výslovně požadovaný, ale nezahrnuje
cloudovou inferenci, telemetrii ani stahování dalších neschválených modelů.

## CPU ONNX runtime – krok AI.3

AI knihovna je nyní Windows-specific, stejně jako Imaging. Přes explicitní
Microsoft.WindowsAppSDK.ML 2.7.0 používá runtime již přítomný v závislostech App,
takže se nepřidává konkurenční managed/native Microsoft.ML.OnnxRuntime balíček.
Domain, Application a Worker zůstávají platformně neutrální.

FlorenceCpuRuntimeValidator implementuje aplikační ICaptionRuntimeValidator.
Worker jej volá až po úspěšné přípravě souborů. Validátor na thread poolu
vytvoří a postupně zlikviduje čtyři reálné CPU sessions, zkontroluje metadata
vůči staticky ověřené smlouvě a vrátí popis grafů bez závislostí ORT v Application.
Používá CPU, základní optimalizaci, omezené threads a vypnutou telemetrii/profilování;
neukládá optimalizované modely a nevolá Run.

RuntimeLoading / RuntimeValidated / RuntimeFailed jsou samostatné stavy lišty.
Chyba nativní knihovny či grafu zůstane ohraničena Workerem a nesmí ukončit App.
Po ověření 4/4 se neaktivuje automatické obsahové pojmenování.
Podrobnosti kontraktů, paměťové hranice a omezení zrušení viz `ai-models.md`.

## Ověřovací záznam a captioning – krok AI.4

CaptionModelVerificationCache ukládá aplikační záznam odděleně od vah.
Je navázán na celý manifest, cestu, metadata souborů, runtime a verzi smlouvy.
Nezměněné modely se nehashují a runtime validator vrací dříve ověřené signatury.
První kontrola této verze záznam teprve vytvoří. Cache není bezpečnostní sandbox;
podrobný explicitní hash check zůstává dostupný.

FlorenceImageCaptionGenerator implementuje IImageCaptionGenerator z Application,
Imaging poskytuje ICaptionImagePreprocessor. AI nezná WinUI ani databázi.
App poskytuje picker a kompaktní read-only dialog. Pevný prompt, BPE tokenizer,
encoder, decoder a KV cache tvoří skutečnou lokální inference pipeline.
Jeden model/sessions se znovu používá, jeden obrázek se zpracovává v daný okamžik.
Výsledek se neukládá do DB a zatím neovlivňuje souborové operace.

Generátor se při ukončení App ruší a vyčkává se na operaci před dispose sessions.
Katalog/import zůstává na AI nezávislý. Na rozdíl od validačního kroku AI.3
potřebuje běžící captioning současně čtyři grafy jednoho Florence modelu.
