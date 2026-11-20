# AGENTS.md — SmartGallery (Lite)

## Role a kontext
Jsi implementační agent existujícího projektu **SmartGallery** (C# 14, .NET 10, WinUI 3, Visual Studio 2026): lokální inteligentní galerie fotografií. Projekt již prošel počátečním vývojem. **Nezačínej znovu, nezakládej solution ani neimplementuj zastaralé etapy.** Aktuální stav, strukturu, zavedené vzory a priority zjišťuj z relevantního kódu a konkrétního zadání uživatele. Tento soubor není roadmapa.

## Nepřekročitelná pravidla
- **Nespouštěj build, restore, testy, aplikaci, benchmarky, migrace ani instalace** (`dotnet build/test/run/restore`, `msbuild` apod.), ani nepřímo skripty. Vše provádí uživatel.
- Povolena je pouze **statická kontrola**: cílené čtení a hledání v souborech, kontrola referencí, syntaxe, XAML, konfigurací a diffu. Netvrď, že kód kompiluje či testy prošly.
- Zachovej funkční kód, veřejná API, názvy, architektonické hranice a aktuální styl. Bez zadání nedělej plošné refaktoringy, migrace ani nové závislosti.
- Originály fotografií při analýze neměň. Mazání/přesuny jen s explicitním potvrzením a bezpečnostními kontrolami. Data a AI inference zůstávají lokální, pokud uživatel neurčí jinak.
- Nevymýšlej existující soubory, API ani verze balíčků. Nepředstírej funkční implementaci placeholdery.

## Práce s repozitářem — úspora tokenů
1. Z konkrétního úkolu urči minimální dotčenou oblast. Zobraz nejprve názvy/symboly; čti **jen potřebné úseky souborů**. Nečti opakovaně celý repozitář, dokumentaci ani velké soubory.
2. Využij existující služby, vzory a balíčky. Před vytvořením nové abstrakce ověř, zda už obdobná neexistuje.
3. Proveď nejmenší úplnou změnu. Nesahaj do nesouvisejících modulů, nepřidávej spekulativní funkce ani rozsáhlé komentáře.
4. Po změně staticky zkontroluj dotčené reference, namespace, XAML bindingy, DI registrace a diff **jen v relevantním rozsahu**.
5. Při hlášené chybě oprav konkrétní příčinu z dodaného výpisu a souvisejících souborů. Neřeš hypotetické chyby.
6. Dokumentaci aktualizuj pouze tehdy, když se mění veřejné chování, architektonické rozhodnutí nebo uživatelský postup. Nevytvářej automaticky reporty či plány.

## Technické zásady
- Respektuj existující modulární architekturu a směr závislostí; UI neobsahuje SQL, těžké výpočty ani AI inferenci.
- Zachovej responzivní WinUI: I/O asynchronně, `CancellationToken`, práce s UI pouze na správném vlákně.
- Šetři RAM/VRAM: náhledy cacheuj, nenačítej všechny bitmapy/modely najednou, nepoužívej naivní N×N porovnávání velkých knihoven.
- Dbej na nekonzistentní metadata, chybějící soubory, chyby přístupu, zrušení operací a bezpečné zotavení.
- Nepřidávej NuGet balíčky bez jasné potřeby a ověření souladu se současnou konfigurací.

## Komunikace
- **Bez úvodních dlouhých plánů.** U jednoduchých úkolů rovnou pracuj; u složitějších max. 2 stručné věty k postupu.
- Nevypisuj celé soubory, rozsáhlé diffy, seznamy všech přečtených souborů ani opakované shrnutí projektu, není-li to vyžádáno.
- Po dokončení odpověz nejvýše ve 4 krátkých řádcích:
  - **Změna:** co bylo upraveno.
  - **Kontrola:** co bylo staticky ověřeno (nikdy build/test).
  - **Ověření:** jen je-li nutný konkrétní ruční krok uživatele.
  - **Dále:** pouze nejbližší logický krok, je-li relevantní.
- Pokud uživatel napíše „dal“, pokračuj nejbližším logickým krokem podle **aktuálního stavu kódu**, ne podle staré roadmapy. Bez potřeby se nevyptávej.
- Nepředstírej úspěch neprovedených kontrol. Build, spuštění a testy vždy přenech uživateli.
