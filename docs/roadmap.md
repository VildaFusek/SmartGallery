# Roadmapa

Každý krok se ověřuje uživatelem; žádná další etapa nezačne bez jeho souhlasu.

0. **Základ:** uživatel potvrdil úspěšné spuštění v režimu Unpackaged
   a schválil pokračování.
1. **Katalog a galerie:**
   - 1.1 Připraven EF Core model SQLite a čtecí adaptér knihoven.
   - 1.2 Připravena DI konfigurace a design-time factory. Uživatel vygeneroval
     InitialCatalog; migrace a snapshot byly staticky zkontrolovány.
     Uživatel následně oznámil dokončení Update-Database.
   - 1.3 Uživatel potvrdil zobrazení stavu katalogu v UI, včetně WAL.
   - 1.4 Výběr a uložení jedné prázdné cílové složky spravované knihovny;
     uživatel schválil pokračování.
   - Upřesnění: knihovna spravuje vlastní kopie. Import zdrojové soubory
     nikdy nemění ani nemaže. Přejmenování a zařazení se týká pouze nových kopií.
   - 1.5 Uživatel potvrdil uložení pravidel po migraci AddLibraryImportSettings.
   - 1.5a Upřesněna hierarchie: výchozí fotografie ROK\MĚSÍC,
     videa vždy ROK\Video. Přidány oddělené modelové ukázky, bez změny schématu.
     Skutečný import a rozpoznávání typů médií zatím nejsou implementovány.
   - 1.6 Změna zadání: bez uživatelského seznamu náhledu importu.
     Rozpracované UI bylo odstraněno, interní kontroly cest zachovány
     jako nezapojený základ. Prefix názvu používá pouze datum `yyyyMMdd_`.
   - 1.7 Připravena importní plocha pro přetažení jedné složky nebo výběr
     tlačítkem. Kompaktní dialog provádí skutečnou přípravnou analýzu,
     ukazuje statistiku a sbalený log. Kopírování ještě není dostupné.
      Čeká na uživatelské ověření.
   - 1.8 Připraven kompaktnější formulář s velkou importní plochou,
     metadata Windows a skutečné kopírování přes vlastní dočasný soubor,
     ověření SHA-256, číselné řešení kolizí a zápis Asset/AssetFile.
     Bez data se používá BezData. Čeká na uživatelský build a ruční ověření.
     Kopírování je záměrně omezené velikostí úplného plánu (500 médií).
   - Obsahové přejmenování je požadováno, ale čeká na funkční lokální inferenci;
     nefunkční volba je jasně označená jako plánovaná.
   - AI.1 Uživatel schválil Florence-2-base-ft, komunitní ONNX převod.
     Připraven manifest FP32 a lokální kontrola velikostí/SHA-256.
     Uživatel potvrdil stažení a ověření 14/14.
   - AI.2 Připravena automatická příprava modelu na pozadí po startu,
     spodní stavová lišta, zastavení/opakování a připravenost katalogu/importu.
     Uživatel potvrdil funkčnost.
   - AI.3 Zapojeno skutečné načítání čtyř ONNX grafů na CPU z existujícího
     Windows ML runtime a kontrola jejich vstupů/výstupů. Uživatel potvrdil 4/4 bez chyb.
   - AI.4 Připraven trvalý záznam ověření a skutečný lokální popisek vybrané
     fotografie (preprocessing, BPE, encoder/decoder/KV cache). Čeká na uživatelský
     build a ověření výsledků. Automatické názvy kopií ještě nejsou zapnuté.
   - Další samostatné kroky: inkrementální sken, metadata, náhledy,
     virtuální grid a detail.
2. **Pozadí:** persistentní Jobs, fronty, progress, pause/cancel/retry a obnova.
3. **Duplicity:** SHA-256, vizuální hashe, kandidáti a porovnání bez mazání.
4. **AI runtime:** lokální modely, ověřený adaptér, CPU fallback.
5. **Vyhledávání:** FTS5, embeddingy a kombinované filtry.
6. **Osoby a kvalita:** lokální tváře, pojmenování a hodnocení.
7. **Alba a geografie:** chytrá alba, časová osa a offline mapa.
8. **Organizace:** potvrzované operace, audit a bezpečné undo.
9. **Optimalizace a distribuce:** podle uživatelských měření.

Kromě výslovně označených připravených částí jsou body 1–9 plán,
ne aktuálně dostupné funkce.
