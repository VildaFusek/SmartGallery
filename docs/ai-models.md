# Lokální obsahové pojmenování – kroky AI.1 až AI.4

Uživatel schválil komunitní ONNX převod Florence-2-base-ft.
Připraveno je ověření, automatické získání souborů a lokální generování popisku
jedné vybrané fotografie. Automatické obsahové názvy při importu ještě nejsou zapnuté.
Import kopírováním funguje nezávisle na modelu.
Uživatel potvrdil ruční kontrolu 14/14, ale požaduje automatickou přípravu.

## Běžné použití – automaticky po startu

Po otevření aplikace běží příprava schváleného Florence-2 v BackgroundService.
Nečeká se na ni při otevření okna ani při práci s katalogem.
Ve spodní liště je ověřování/stahování, počty souborů, přenesené MiB a stav modulů.
Model s platnými 14/14 soubory se **nestahuje znovu a příprava nepotřebuje síť**.
Od AI.4 se při nezměněných souborech používá uložený záznam místo opakovaného
hashování a načítání čtyř grafů.

Pokud soubor chybí, aplikace jej automaticky stáhne ze zdroje připnutého
manifestem, ověří velikost/SHA-256 a teprve potom publikuje pod finálním názvem.
Celý chybějící balíček má přibližně 1,09 GB. Během přípravy se neposílají
fotografie, názvy fotografií ani biometrická data; nejde o cloudovou inferenci.

Lišta má **Zastavit** a **Zkusit znovu**, pouze pro přípravu modelu.
Bez připojení chyba přípravy nezastaví galerii/import; další pokus je ruční
nebo při příštím startu. Neprobíhá nekonečné opakování downloadu.
Měřené připojení zatím není automaticky detekováno, přenos lze zastavit.

Stávající soubor s nesprávnou velikostí/hashem se nepřepisuje ani nemaže.
Lišta ohlásí chybu a požaduje jeho ruční kontrolu. Stahování používá vlastní
nový dočasný soubor; běžné zrušení jej uklidí a hotové ověřené soubory zachová.
Přerušený soubor se při dalším pokusu stahuje od začátku, HTTP resume zatím není.
Po tvrdém pádu může zůstat `.download` soubor, který se plošně automaticky nemaže.

„Soubory ověřeny“ není samo o sobě důkazem funkčního captioningu.
Od kroku AI.3 po souborech následuje skutečné načtení čtyř ONNX grafů na CPU.
Samostatný popisek je od AI.4 dostupný tlačítkem až po přípravě grafů.
Katalog ukazuje výsledek skutečného připojení; připravenost importu znamená
načtenou knihovnu/uložená pravidla, ne předběžný důkaz zapisovatelnosti každého cíle.

## Zvolený balíček

- Původní model: https://huggingface.co/microsoft/Florence-2-base-ft
- ONNX převod: https://huggingface.co/onnx-community/Florence-2-base-ft
- Připnutá revize: `e88a44eaf3791a35eae0c5a47b3dbcd36e67eb6f`.
- Licence: MIT. Balíček zahrnuje LICENSE.txt z připnuté revize původního modelu.
- Výchozí varianta: FP32, určená k následnému ověření CPU inference.
  Kompatibilita grafů, rychlost a RAM/VRAM zatím nejsou ověřeny.

`models/manifest.json` uvádí přesné velikosti a SHA-256 všech 14 souborů.
Čtyři grafy jsou vision_encoder, embed_tokens, encoder_model a decoder_model_merged.
Celkem jde přibližně o **1,09 GB**; nejde o nárok na RAM při běhu.
Nestahuj všechny varianty repozitáře, zabírají výrazně více místa.

SHA-256 grafů pocházejí z LFS metadat připnuté revize Hugging Face.
Hash a velikost malých konfigurací/tokenizeru/licence byly zjištěny ze skutečných
bajtů přes HTTPS. Žádné váhy modelu agent nestahoval, neinstaloval ani nespouštěl.
Hash ověřuje shodu souborů s manifestem, nikoli správnost popisků nebo
bezpečnost/důvěryhodnost celého komunitního převodu.

## Umístění a ověření

```text
%LOCALAPPDATA%\SmartGallery\models\florence-2-base-ft-fp32\
├── onnx\
│   ├── vision_encoder.onnx
│   ├── embed_tokens.onnx
│   ├── encoder_model.onnx
│   └── decoder_model_merged.onnx
├── ...konfigurace a tokenizer dle manifestu...
└── LICENSE.txt
```

AI knihovna má manifest vestavěný jako resource. Samotný manifest se nestahuje ze sítě
a nepoužívá uživatelem upravený externí manifest pro ověření.
Volitelná **Podrobná kontrola souborů modelu** v rozbaleném nastavení:
- kontroluje velikosti a SHA-256 streamovaným čtením mimo UI thread;
- odmítá reparse points a cesty mimo složku modelu;
- neprovádí inferenci a modelové váhy nepřepisuje;
- nevytváří síťová spojení a nečte fotografie.

Podrobná kontrola vždy znovu počítá SHA-256 a aktualizuje aplikační záznam
ověření. Při problému záznam zneplatní; nejedná se už o čistě read-only operaci
vůči vlastní cache, ale zůstává read-only vůči modelovým vahám a fotografiím.

„14/14 ověřeno“ znamená jen shodu souborů. Neaktivuje obsahové přejmenování.
Před budoucím načtením musí runtime integritu znovu ověřit, soubory se mohou
od poslední kontroly změnit. Runtime se nyní přebírá z existujícího Windows ML,
nepřidává se druhý samostatný balíček Microsoft.ML.OnnxRuntime.

## Ruční stažení – volitelný pokročilý postup

Pro běžné použití stačí spustit aplikaci; PowerShell již není nutný.
Pokud přesto chceš model získat ručně, nejprve aplikaci zavři, aby nestahovala
souběžně. Potom můžeš v PowerShellu **z kořene repozitáře** spustit tyto příkazy.
Stahují přibližně 1,09 GB; jde o jednorázové získání modelu, ne cloudovou inferenci.
Existující správné soubory přeskočí, neshodné automaticky nepřepisují.

```powershell
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath '.\models\manifest.json' -Raw | ConvertFrom-Json
$root = Join-Path $env:LOCALAPPDATA ('SmartGallery\models\' + $manifest.modelId)
$base = 'https://huggingface.co/' + $manifest.repository + '/resolve/' + $manifest.revision + '/'
foreach ($file in $manifest.files) {
    $target = Join-Path $root ($file.path.Replace('/', '\'))
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target).Length -eq $file.bytes -and
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ieq $file.sha256) {
            continue
        }
        throw "Existující soubor neodpovídá manifestu; ručně jej zkontroluj: $target"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    $url = if ($file.PSObject.Properties.Name -contains 'url') { $file.url } else { $base + $file.path }
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $file.sha256) {
        throw "Stažený soubor neodpovídá SHA-256: $target"
    }
}
```

Agent tyto stahovací příkazy nespouštěl. Při přerušení může zůstat neúplný soubor;
další pokus ho nepřepíše bez tvého ručního rozhodnutí.
Nepoužívej soubory jiných revizí ani Git LFS textové ukazatele místo vah.

## Ruční ověření ve VS 2026

1. Sestav a spusť Unpackaged se současnými 14 soubory. Okno má být použitelné
   ihned; lišta ověří model a přejde na 14/14 bez nového stahování.
2. Vyzkoušej totéž bez internetu – s platným kompletním modelem má kontrola projít.
3. Chybějící soubor ověřuj na testovacím profilu nebo při zavřené aplikaci
   nedestruktivně odlož malý `generation_config.json` pod jiný název.
   Po startu se má stáhnout pouze chybějící soubor, ne všechny grafy.
4. Ověř Zastavit/Zkusit znovu, zachování hotových souborů a dostupnost katalogu.
   Při neplatném existujícím souboru očekávej chybu bez jeho přepsání.
5. Po kontrole souborů čekej v kroku AI.3 také načtení grafů podle oddílu níže.
   Popisky ani obsahové názvy se zatím nevytvářejí.

Agent aplikaci, build ani automatické stahování nespouštěl.

## Skutečné načtení ONNX grafů – krok AI.3

V existujících závislostech App již je Microsoft.WindowsAppSDK.ML 2.7.0,
který přináší Microsoft.Windows.AI.MachineLearning 2.5.96 a jeho ONNX Runtime
(balíček uvádí ORT 1.28.3). AI knihovna používá explicitní referenci na stejný
Microsoft.WindowsAppSDK.ML, ne druhou kopii onnxruntime.dll z jiného NuGetu.
AI je proto Windows knihovna `net10.0-windows10.0.26100.0`.

Po 14/14 Worker přes ICaptionRuntimeValidator:
1. Vypne telemetrii ORT před vytvořením sessions.
2. Pro každý graf znovu ověří SHA-256 a během načítání drží read-only handle
   zakazující běžné změny/smazání souboru.
3. Vytvoří skutečnou CPU InferenceSession, zkontroluje názvy/typy/ranky/fixní
   rozměry vstupů a výstupů a session uvolní před dalším grafem.
4. Výsledek a verzi runtime zaznamená do debug logu a do spodní lišty.

Smlouvy byly určeny **statickým čtením protobuf metadat lokálních souborů ONNX**,
nikoli spuštěním modelu:

| Graf | Vstupy | Výstupy |
| --- | --- | --- |
| vision_encoder | pixel_values, float, B×3×H×W | image_features, float, B×S×768 |
| embed_tokens | input_ids, int64, B×S | inputs_embeds, float, B×S×768 |
| encoder_model | attention_mask + inputs_embeds | last_hidden_state, B×S×768 |
| decoder_model_merged | 28 vstupů včetně 24 KV cache a bool use_cache_branch[1] | logits B×S×51289 a 24 present cache |

Decoder má šest vrstev, 12 attention heads a rozměr hlavy 64.
Neznámé délky sekvencí jsou při kontrole dynamické, nikoli napevno odhadnuté.
Neprovádí se Session.Run, tokenizace ani captioning. Sessions nejsou trvale
držené v RAM; čtyři grafy se v tomto kroku nenačítají současně.
Nepoužívá se GPU/NPU, provider download, Python ani custom ops.

Ruční ověření:
- Obnov balíčky/sestav solution ve VS 2026 a spusť Unpackaged.
- Po 14/14 očekávej „CPU ONNX: …“ a poté **ONNX: 4/4 · popisky čekají**.
- Ověř dostupnost galerie/importu během načítání. Načítání může zabrat čas
  a používá RAM; velikost a výkon nejsou agentem měřeny.
- Při chybě zkopíruj celý text z tooltipu spodní lišty a debug výstupu VS.
  Chyba ONNX nesmí ukončit galerii ani zapnout obsahové názvy.

Zrušení je kontrolováno před a po nativním konstruktoru InferenceSession;
tento synchronní konstruktor se v této etapě nepřerušuje uprostřed operace.
Agent ONNX Runtime, sessions, build ani aplikaci nespouštěl. Statická kontrola
není potvrzením funkčnosti CPU načítání na uživatelově stroji.

## Záznam ověření a skutečný popisek – krok AI.4

Uživatel potvrdil úspěšné načtení 4/4 grafů. Nová verze ukládá
`%LOCALAPPDATA%\SmartGallery\models\florence-2-base-ft-fp32\.smartgallery-verified.json`.
Při prvním startu této verze je nutná jedna úplná kontrola a vytvoření záznamu.
Další starty kontrolují pouze velikost, čas vytvoření/změny, cestu, manifest
a verzi runtime/smlouvy grafů. Nezměněné soubory se nehashují a grafy se
nenačítají jen kvůli opakovanému ověření. Lišta výslovně uvede použití záznamu.

Změna souboru/manifestu/revize nebo runtime záznam odpovídajícím způsobem
zneplatní. Poškozená cache není důkaz integrity a vede k nové kontrole.
Cache není kryptografický bezpečnostní pečetící mechanismus: úmyslnou změnu
obsahu se zachováním všech metadat nemusí odhalit. Pro pochybnosti je k dispozici
Podrobná kontrola souborů. Při změně validačních pravidel musí vývojář změnit
verzi smlouvy v CaptionModelVerificationCache.

**Vygenerovat popisek fotografie…**:
- vybere jeden obrázek přes Windows picker, nic nekopíruje ani nepřejmenovává;
- Imaging převede obraz do RGB CHW 768×768, rescale 1/255 a normalizace podle
  připnutého preprocessor_config; respektuje EXIF orientaci;
- dekódování přes Windows může záviset na nainstalovaných kodecích, limit vstupu
  je 100 milionů pixelů; bicubická interpolace Windows není bitově ověřena vůči PIL;
- AI používá ByteLevel BPE + RobertaProcessing z ověřeného tokenizer.json,
  pevný CAPTION prompt „What does the image describe?“ a skutečné čtyři ONNX grafy;
- decoder používá KV cache, deterministické greedy dekódování, forced BOS,
  no-repeat trigram a nejvýše 64 nových tokenů (ne beam search 3 z referenční ukázky);
- výsledek je skutečný výstup modelu, typicky anglický; může být chybný nebo neúplný.

Čtyři sessions jednoho Florence modelu se načtou líně až pro první popisek,
poté se používají znovu do ukončení App. První popisek proto trvá déle
a model potřebuje podstatně více paměti než samotný soubor na disku.
Měření výkonu/RAM agent neprováděl. Zpracovává se vždy jen jedna fotografie;
žádné další AI modely se nepřidávají. RunOptions.Terminate předává zrušení
probíhající inferenci, nativní konstruktor session má nadále omezení z AI.3.

Dialog drží výsledek jen v paměti. Fotografie ani popisky se neposílají do cloudu.
Při zavírání App se nejprve ruší a dokončuje caption operace, až potom se
uvolňují sessions a host.

Ruční ověření:
1. Sestav a spusť Unpackaged. Nech vytvořit ověřovací záznam.
2. Restartuj App – lišta má uvést „použit uložený záznam ověření“ bez opakovaného
   hashování modelu a bez načítání čtyř sessions pro pouhou kontrolu.
3. Po připravení zkus tlačítko popisku s běžnou malou JPEG/PNG fotografií.
4. Ověř neprázdný související popisek, druhou fotografii a zrušení generování.
   Vyzkoušej také chybový obrázek/nenainstalovaný kodek – nesmí vzniknout falešný popis.
5. Zdrojové fotografie ani názvy souborů se nesmějí změnit.

Agent generátor, model, build ani testy nespouštěl. Kvalita popisků a
kompatibilita nové inference musí být potvrzena uživatelským spuštěním.

## Co následuje

Po uživatelském ověření skutečných popisků lze zapojit bezpečné zkrácení textu
na názvy kopií při importu. Volba názvů zatím zůstává neaktivní.
Florence-2 může popisovat scénu chybně a český výstup není ověřen.
Ani budoucí popisek není zdrojem skutečného data vzniku fotografie.
