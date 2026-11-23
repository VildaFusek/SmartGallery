# Vydání SmartGallery na GitHubu

První distribuční varianta: **Windows 11 x64, Unpackaged, rozbalovací ZIP**. Balík obsahuje .NET i Windows App SDK runtime. Neobsahuje model AI, katalog ani média. Není to MSIX ani instalátor; ARM64 a x86 zatím nevydávej bez vlastního ověření.

## Popisky pro GitHub

**Název repozitáře:** `SmartGallery`  
**About:** Lokální galerie fotografií a videí pro Windows 11 s AI popisky, vyhledáváním a detekcí duplicit.  
**Topics:** `winui3`, `windows`, `photo-gallery`, `dotnet`, `sqlite`, `offline-first`, `florence-2`

**Tag:** `v1.2`

**Zpráva commitu:** `SmartGallery 1.2: dávkové převody médií, analýza galerie a údržba katalogu`

## SmartGallery 1.2 – Windows x64

- **Videa:** přehrávejte video přímo v galerii nebo převeďte celý výběr do MP4, MOV, WebM či MKV. Dva ukazatele rozlišují průběh celé dávky a aktuálního videa. U videa se známou délkou vidíte, kolik záznamu je převedeno a kolik zbývá. Po zastavení dostanete stručný souhrn místo výpisu FFmpeg.
- **Fotografie:** jednotlivě i hromadně převádějte do JPEG, PNG nebo WebP. Při nahrazení kopie můžete předem porovnat velikost a rozměry výsledku. Zdrojové originály se nemění.
- **Analýza galerie:** v samostatném okně najdete shodné a podobné fotografie, možné screenshoty i návrhy na úsporu místa. Během analýzy můžete dál procházet galerii; změny se provedou až po potvrzení.
- **Správa knihovny:** kopie můžete přejmenovat přímo v galerii. Údržba katalogu po záloze odstraní záznamy o chybějících souborech; dočasně nedostupné složky pouze skryje.

**Stažení:** rozbalte celý `SmartGallery-win-x64.zip` a spusťte `SmartGallery.exe`. FFmpeg a model pro lokální AI popisky lze stáhnout samostatně přes **Potřebné soubory**.

## Publikace z Windows

Příkazy jsou pro správce vydání. Použij x64 Developer PowerShell ve Visual Studiu 2026 s podporou WinUI 3, Windows SDK a SDK .NET podporujícím cílový `net10.0`. Publikuj z kořene repozitáře do nového výstupního adresáře:

```powershell
$output = Join-Path $PWD 'artifacts\publish\win-x64'
$zip = Join-Path $PWD 'artifacts\SmartGallery-win-x64.zip'
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $zip)) {
    throw 'Výstup už existuje. Zvol nový adresář a název ZIPu nebo předchozí výstup archivuj.'
}

dotnet publish .\SmartGallery\SmartGallery.csproj -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 -p:PublishProfile=win-x64 -p:PublishTrimmed=false -p:PublishReadyToRun=false `
    -o $output
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $output 'SmartGallery.exe'))) {
    throw 'Publikace selhala nebo ve výstupu chybí SmartGallery.exe.'
}

tar.exe -a -c -f $zip -C $output .
if ($LASTEXITCODE -ne 0) { throw 'Vytvoření ZIPu selhalo.' }
Get-FileHash -LiteralPath $zip -Algorithm SHA256
```

Distribuuj **celý ZIP**, ne jen EXE. První ZIP ověř po rozbalení na čistých Windows 11 x64 bez Visual Studia a bez předem připraveného katalogu. Otestuj:

1. Spuštění bez internetu, vytvoření katalogu a výběr prázdné cílové složky.
2. Import malé sady, zachování originálů, zobrazení kopie a načtení stejné knihovny po restartu.
3. S internetem stažení modelu a vytvoření popisku na CPU.
4. Spuštění s existujícím katalogem; aktuální schéma zůstane dostupné, starší schéma se samo nepřepíše.

ZIP bez důvěryhodného podpisu může vyvolat upozornění Windows SmartScreen. Pro podepsanou distribuci použij vlastní certifikát mimo repozitář; podpis musí předcházet vytvoření finálního ZIPu a výpočtu kontrolního součtu.

## Zveřejnění zdrojů a vydání

1. Zvol licenci zdrojového kódu a před zveřejněním doplň `LICENSE`, pokud chceš kód licencovat k dalšímu použití. Zkontroluj také redistribuční podmínky přibalených závislostí; licence AI modelu je samostatná.
2. Založ Git repozitář a před prvním commitem zkontroluj seznam souborů i veřejné údaje vydavatele v `SmartGallery/Package.appxmanifest` (unpackaged vydání tento MSIX manifest nepoužívá). Do repozitáře nepatří `.vs`, `bin`, `obj`, uživatelské konfigurace, podpisové klíče, místní katalogy, fotografie, videa nebo stažené modely. Z `models/` patří do Gitu jen `manifest.json`.
3. Zveřejni zdrojový commit na GitHubu. V **Releases → Draft a new release** vytvoř tag `v<verze>` nad odpovídajícím commitem, vlož připravený text, přilož otestovaný ZIP a doplň jeho SHA-256 do poznámek. První neověřenou nebo experimentální verzi neoznačuj jako stabilní.
