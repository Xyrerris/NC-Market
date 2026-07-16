# NC-Market

Strumento per interrogare il mercato di **Nine Chronicles** e recuperare i prezzi degli
equipaggiamenti (Sword/Weapon, Armor, Belt, Necklace, Ring), con possibilità di
**storicizzare** i prezzi in un database SQLite locale per successive valutazioni.

## Fonte dati

NC-Market usa il **market service ufficiale di Planetarium** (repo
[planetarium/market-service](https://github.com/planetarium/market-service)), lo stesso
backend usato dal sito del mercato ufficiale. Gli endpoint pubblici, censiti nel
[planet registry](https://planets.nine-chronicles.com/planets/) alla chiave `market.rest`,
sono:

| Pianeta | Endpoint |
|---|---|
| Odin (mainnet principale) | `https://b.9capi.com/marketProviderOdin` |
| Heimdall | `https://b.9capi.com/marketProviderHeimdall` |

Rotta principale utilizzata:

```
GET /Market/products/items/{itemSubType}?limit={n}&offset={n}&order={ordine}
```

`itemSubType` segue l'enum `ItemSubType` di lib9c (`Lib9c/Model/Item/ItemType.cs`):

| Equipaggiamento | Valore |
|---|---|
| Weapon (Sword) | 6 |
| Armor | 7 |
| Belt | 8 |
| Necklace | 9 |
| Ring | 10 |

Ordinamenti supportati dal servizio: `price`, `price_desc`, `cp`, `cp_desc`, `grade`,
`grade_desc`, `level`, `level_desc`, `unit_price`, `unit_price_desc`, `opt_count`,
`opt_count_desc`, `crystal`, `crystal_desc`, `crystal_per_price`, `crystal_per_price_desc`.

Note operative rilevate sul servizio in produzione:

- il campo `totalCount` della risposta **non è valorizzato** (sempre 0): la paginazione
  si ferma alla prima pagina corta o vuota;
- il servizio non applica una chiave di ordinamento secondaria stabile: con ordinamenti
  ricchi di valori ripetuti (es. `unit_price`) le pagine adiacenti si sovrappongono fino
  al ~77% e alcune inserzioni vengono saltate. Per gli snapshot NC-Market pagina quindi
  con `cp_desc` (Combat Point quasi sempre distinti: sovrapposizione misurata 0%);
- pagine da 1000 elementi sono un buon compromesso (5000 richiede ~96 s e rischia
  timeout); su Odin le sole Weapon contano ~60.000 inserzioni.

I prezzi sono espressi in **NCG**. I nomi di item e skill vengono risolti scaricando (con
cache locale) i file `item_name.csv` e `skill_name.csv` dal repo ufficiale del client
([NineChronicles/.../Localization](https://github.com/planetarium/NineChronicles/tree/main/nekoyume/Assets/StreamingAssets/Localization)).
Gli id introdotti dopo l'ultimo aggiornamento dei CSV su `main` (item/skill molto recenti)
non hanno ancora un nome e vengono mostrati come valore numerico.

Ogni inserzione include le **statistiche dell'equipaggiamento**: per ogni stat (HP, ATK,
DEF, CRI, HIT, SPD, DRV, DRR, CDMG, ...) il valore base e il bonus delle opzioni di
crafting (`additional`), più le eventuali **skill** con categoria, elemento, probabilità,
potenza, ratio sulla stat di riferimento, colpi e cooldown.

## Architettura

```
NC-Market/
├── NCMarket.sln
├── README.md
└── src/
    ├── NCMarket.Core/          Libreria riusabile
    │   ├── Planet.cs           Registro pianeti/endpoint (Odin, Heimdall)
    │   ├── EquipmentType.cs    Enum equipaggiamenti + parsing
    │   ├── Models/             DTO della risposta del market service
    │   ├── MarketClient.cs     Client HTTP con paginazione automatica
    │   ├── NameProvider.cs     Risoluzione id -> nome per item e skill (cache locale)
    │   ├── ProductFormat.cs    Formattazione statistiche e skill delle inserzioni
    │   └── MarketDb.cs         Storicizzazione su SQLite + query analitiche
    └── NCMarket.Cli/           Applicazione console
        └── Program.cs          Comandi: fetch, snapshot, snapshots, history, stats
```

Scelte progettuali:

- **.NET 9 / C#**: coerente con l'ecosistema Nine Chronicles (lib9c, Libplanet) e con gli
  SDK già presenti sulla macchina.
- **Core separato dalla CLI**: la libreria `NCMarket.Core` è riusabile in step successivi
  (servizio schedulato, dashboard, motore di valutazione) senza toccare la CLI.
- **SQLite** (`Microsoft.Data.Sqlite`): zero amministrazione, file unico, adatto a
  storicizzazione e query aggregate. Percorso di default:
  `%LOCALAPPDATA%\NCMarket\ncmarket.db`, personalizzabile con `--db`.
- **Snapshot immutabili**: ogni esecuzione di `snapshot` salva l'intero listino corrente
  con timestamp; le analisi storiche confrontano gli snapshot tra loro.

## Schema del database

```sql
snapshots(
    id INTEGER PK,            -- progressivo
    planet TEXT,              -- odin | heimdall
    taken_at_utc TEXT,        -- ISO 8601
    item_sub_types TEXT,      -- sottotipi inclusi, es. "6,7,8,9,10"
    product_count INTEGER     -- prodotti salvati
)

products(
    snapshot_id INTEGER FK -> snapshots.id,
    product_id TEXT,          -- GUID dell'inserzione on-chain
    item_sub_type, item_id, icon_id, grade, level, combat_point, elemental_type,
    price REAL,               -- prezzo in NCG
    quantity REAL, unit_price REAL,
    crystal INTEGER, crystal_per_price INTEGER,
    option_count INTEGER, by_custom_craft INTEGER,
    seller_agent TEXT, seller_avatar TEXT,
    registered_block_index INTEGER, legacy INTEGER,
    stats_json TEXT,          -- statistiche (ATK, DEF, ...) in JSON
    skills_json TEXT,         -- skill in JSON
    PRIMARY KEY(snapshot_id, product_id)
)
```

`product_id` è stabile per tutta la vita di un'inserzione: confrontando snapshot
consecutivi è quindi possibile (step futuro) dedurre vendite e cancellazioni.

## Comandi CLI

```powershell
# Interrogazione live del mercato (senza salvare nulla); la tabella include
# statistiche (base + bonus opzioni) e skill di ogni inserzione
dotnet run --project src/NCMarket.Cli -- fetch --type weapon --order price --limit 20

# Scheda completa per inserzione: tutte le statistiche e il dettaglio delle skill
# (categoria, elemento, probabilità, potenza, cooldown)
dotnet run --project src/NCMarket.Cli -- fetch --type ring --order cp_desc --limit 5 --details

# Storicizza il listino completo dei 5 equipaggiamenti su Odin
dotnet run --project src/NCMarket.Cli -- snapshot

# Solo alcuni tipi, su Heimdall, limitando i prodotti per tipo
dotnet run --project src/NCMarket.Cli -- snapshot --types weapon,ring --planet heimdall --max-per-type 500

# Elenco degli snapshot salvati
dotnet run --project src/NCMarket.Cli -- snapshots

# Andamento storico dei prezzi di un item (min/media/max per snapshot)
dotnet run --project src/NCMarket.Cli -- history --item 10152001

# Statistiche aggregate per item sull'ultimo snapshot
dotnet run --project src/NCMarket.Cli -- stats --type weapon
```

Opzioni comuni: `--planet odin|heimdall` (default `odin`), `--db <percorso>` per il
database, `--no-names` per saltare la risoluzione dei nomi di item e skill.

## Piano di sviluppo

### Step 1 — Fondamenta (questo repository)
1. **Ricerca API** ✅ — individuato il market service ufficiale, le rotte, i modelli di
   risposta e la mappatura `ItemSubType` da lib9c.
2. **Client di mercato** ✅ — `MarketClient` con paginazione automatica e retry di base.
3. **Storicizzazione** ✅ — `MarketDb` con snapshot immutabili su SQLite.
4. **CLI** ✅ — comandi `fetch`, `snapshot`, `snapshots`, `history`, `stats`.
5. **Risoluzione nomi item** ✅ — cache di `item_name.csv` (TTL 7 giorni).
6. **Statistiche equipaggiamento** ✅ — stat (ATK, HP, DEF, ...) e skill di ogni
   inserzione nella tabella `fetch`, vista `--details`, nomi skill da `skill_name.csv`.

### Step 2 — Automazione e arricchimento (prossimi sviluppi)
- **Raccolta schedulata**: esecuzione periodica di `snapshot` (Task Scheduler di Windows
  o servizio worker .NET) per costruire una serie storica fitta.
- **Rilevazione vendite**: confronto tra snapshot consecutivi per distinguere item
  venduti da item ritirati (incrocio con le transazioni `BuyProduct` via 9cscan/mimir).
- **Filtri avanzati**: il servizio supporta anche `stat`, `itemIds[]`, `iconIds[]`,
  `isCustom` sulla stessa rotta — esporli nella CLI.

### Step 3 — Valutazioni (obiettivo finale)
- **Motore di pricing**: prezzo equo stimato per item/level/opzioni sulla base della
  serie storica (mediane mobili, percentili per grade/CP).
- **Segnalazione occasioni**: inserzioni sotto la valutazione stimata.
- **Reportistica**: export CSV/Excel e dashboard (es. grafici andamento prezzi).

## Requisiti

- .NET SDK 9.0+
- Accesso a internet verso `b.9capi.com` (market service) e `raw.githubusercontent.com`
  (nomi item, opzionale)

## Build

```powershell
dotnet build NC-Market/NCMarket.sln
```
