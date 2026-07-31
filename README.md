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
| Heimdall (pianeta di default) | `https://b.9capi.com/marketProviderHeimdall` |
| Odin (mainnet principale) | `https://b.9capi.com/marketProviderOdin` |

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
non hanno ancora un nome: per gli **item** si usa il nome della serie dedotto dalla rarità
— grado 7 = "Valkyrie …", grado 8 = "Transcendent …" (es. "Transcendent Sword" per una
weapon di grado 8) — mentre le **skill** restano con l'id numerico.

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
    │   ├── Planet.cs           Registro pianeti/endpoint (Odin, Heimdall; default Heimdall)
    │   ├── EquipmentType.cs    Enum equipaggiamenti + parsing
    │   ├── Models/             DTO della risposta del market service
    │   ├── MarketClient.cs     Client HTTP con paginazione automatica
    │   ├── NameProvider.cs     Risoluzione id -> nome per item e skill (cache locale)
    │   ├── ProductFormat.cs    Formattazione statistiche e skill delle inserzioni
    │   ├── SnapshotCsvExporter.cs  Export CSV flat di uno snapshot
    │   ├── DealFinder.cs       Rilevazione occasioni (confronto con le mediane storiche)
    │   └── MarketDb.cs         Storicizzazione su SQLite + query analitiche
    └── NCMarket.Cli/           Applicazione console
        └── Program.cs          Comandi: fetch, snapshot, snapshots, history, stats, deals, export, prune
```

Scelte progettuali:

- **.NET 9 / C#**: coerente con l'ecosistema Nine Chronicles (lib9c, Libplanet) e con gli
  SDK già presenti sulla macchina.
- **Core separato dalla CLI**: la libreria `NCMarket.Core` è riusabile in step successivi
  (servizio schedulato, dashboard, motore di valutazione) senza toccare la CLI.
- **SQLite** (`Microsoft.Data.Sqlite`): zero amministrazione, file unico, adatto a
  storicizzazione e query aggregate. Percorso di default:
  `%LOCALAPPDATA%\NCMarket\ncmarket.db`, personalizzabile con `--db`.
- **Snapshot immutabili, archiviazione deduplicata**: ogni esecuzione di `snapshot`
  registra l'intero listino corrente con timestamp, ma ogni inserzione (`product_id`,
  i cui attributi non cambiano mai: un cambio prezzo crea un nuovo prodotto) è salvata
  una sola volta nella tabella `listings`; l'appartenenza ai singoli snapshot è
  tracciata dalla tabella `sightings` (due interi per riga). Le analisi storiche
  confrontano gli snapshot tra loro come prima.

## Schema del database (v2)

```sql
snapshots(
    id INTEGER PK,            -- progressivo
    planet TEXT,              -- odin | heimdall
    taken_at_utc TEXT,        -- ISO 8601
    item_sub_types TEXT,      -- sottotipi inclusi, es. "6,7,8,9,10"
    product_count INTEGER     -- inserzioni osservate al momento della cattura
)

listings(                     -- una riga per inserzione unica, scritta una volta sola
    id INTEGER PK,
    product_id TEXT UNIQUE,   -- GUID dell'inserzione on-chain
    planet TEXT,
    item_sub_type, item_id, icon_id, grade, level, combat_point, elemental_type,
    price REAL,               -- prezzo in NCG
    quantity REAL, unit_price REAL,
    crystal INTEGER, crystal_per_price INTEGER,
    option_count INTEGER, by_custom_craft INTEGER,
    seller_agent TEXT, seller_avatar TEXT,
    registered_block_index INTEGER, legacy INTEGER,
    stats_json TEXT,          -- statistiche (ATK, DEF, ...) in JSON
    skills_json TEXT,         -- skill in JSON
    first_seen_snapshot_id INTEGER,  -- primo e ultimo snapshot in cui è apparsa
    last_seen_snapshot_id INTEGER,
    last_seen_at_utc TEXT     -- per la finestra --days e per la retention
)

sightings(                    -- appartenenza inserzione <-> snapshot: due interi
    snapshot_id INTEGER FK -> snapshots.id  ON DELETE CASCADE,
    listing_id INTEGER FK -> listings.id    ON DELETE CASCADE,
    PRIMARY KEY(snapshot_id, listing_id)
)
```

`product_id` è stabile e immutabile per tutta la vita di un'inserzione (un cambio di
prezzo crea un nuovo prodotto): gli attributi vengono quindi salvati una sola volta e
uno snapshot che riosserva un'inserzione già nota aggiunge solo una riga di
`sightings` (~20 byte) e aggiorna il marcatore *last seen*. Confrontando snapshot
consecutivi resta possibile (step futuro) dedurre vendite e cancellazioni.

I database creati con lo schema v1 (una copia completa di ogni inserzione per
snapshot) vengono **migrati automaticamente** alla prima apertura: viene lasciata una
copia di sicurezza `<db>.v1.bak` accanto al file originale e il database viene
compattato con `VACUUM`. Il database usa il journal WAL, quindi accanto al file
possono comparire i file di servizio `-wal` e `-shm`.

## Comandi CLI

```powershell
# Interrogazione live del mercato (senza salvare nulla); la tabella include
# statistiche (base + bonus opzioni) e skill di ogni inserzione
dotnet run --project src/NCMarket.Cli -- fetch --type weapon --order price --limit 20

# Scheda completa per inserzione: tutte le statistiche e il dettaglio delle skill
# (categoria, elemento, probabilità, potenza, cooldown)
dotnet run --project src/NCMarket.Cli -- fetch --type ring --order cp_desc --limit 5 --details

# Storicizza il listino completo dei 5 equipaggiamenti su Heimdall (pianeta di default)
dotnet run --project src/NCMarket.Cli -- snapshot

# Solo alcuni tipi, su Odin, limitando i prodotti per tipo
dotnet run --project src/NCMarket.Cli -- snapshot --types weapon,ring --planet odin --max-per-type 500

# Elenco degli snapshot salvati
dotnet run --project src/NCMarket.Cli -- snapshots

# Andamento storico dei prezzi di un item (min/media/max per snapshot)
dotnet run --project src/NCMarket.Cli -- history --item 10152001

# Statistiche aggregate per item sull'ultimo snapshot
dotnet run --project src/NCMarket.Cli -- stats --type weapon

# Occasioni: inserzioni correnti (mercato live) a prezzo conveniente rispetto agli
# storici del database. Il confronto avviene tra item comparabili (stesso item e
# livello) sulle inserzioni distinte viste negli snapshot; la metrica primaria è il
# rapporto NCG/CP (un CP alto a basso prezzo è un'occasione), lo sconto sul prezzo
# puro è mostrato come colonna secondaria
dotnet run --project src/NCMarket.Cli -- deals --discount 30

# Occasioni sull'ultimo snapshot (senza download live), con soglie personalizzate
dotnet run --project src/NCMarket.Cli -- deals --type ring --from-snapshot --min-samples 3 --days 14

# Solo le rarità indicate (numero 1-8 o nome lib9c: normal, rare, epic, unique,
# legendary, divinity, mythic, transcendent)
dotnet run --project src/NCMarket.Cli -- deals --grade legendary,mythic

# Export CSV "flat" di uno snapshot: una riga per inserzione, statistiche in colonne
# <stat>_base/<stat>_bonus (hp, atk, def, cri, hit, spd, drv, drr, cdmg, armorpen,
# thorn) e skill in colonne skill1_*/skill2_* (id, nome, categoria, elemento,
# probabilità, potenza, ratio, colpi, cooldown)
dotnet run --project src/NCMarket.Cli -- export                       # ultimo snapshot
dotnet run --project src/NCMarket.Cli -- export --snapshot 2 --type weapon --sep ";"

# Retention: elimina le inserzioni non più viste da N giorni (default: 365) con i
# relativi avvistamenti e gli snapshot rimasti vuoti, poi compatta il file con VACUUM
dotnet run --project src/NCMarket.Cli -- prune --dry-run              # anteprima
dotnet run --project src/NCMarket.Cli -- prune --days 180
```

Per aprire il CSV con Excel in italiano usare `--sep ";"`; il file è UTF-8 con BOM.

Opzioni comuni: `--planet odin|heimdall` (default `heimdall`), `--db <percorso>` per il
database, `--no-names` per saltare la risoluzione dei nomi di item e skill.

## Deploy su server (Docker + Coolify)

Il repository contiene un `Dockerfile` multi-stage (build con l'SDK .NET 9, runtime su
`mcr.microsoft.com/dotnet/runtime:9.0`) pensato per far girare gli snapshot periodici su un
server, tipicamente tramite [Coolify](https://coolify.io/).

Punti chiave:

- l'immagine imposta `XDG_DATA_HOME=/data`: su Linux .NET risolve `LocalApplicationData`
  con quella variabile, quindi database e cache dei nomi item/skill finiscono in
  `/data/NCMarket` senza bisogno di passare `--db`. **Montare un volume persistente su
  `/data`**, altrimenti i dati si perdono a ogni redeploy;
- comando di default `idle`: il container resta vivo senza fare nulla, in attesa che lo
  scheduler (Scheduled Task di Coolify) esegua i comandi al suo interno. Qualsiasi altro
  argomento viene passato alla CLI, quindi `docker run <immagine> snapshot --planet odin`
  funziona anche in esecuzione one-shot;
- lo script `docker/snapshot-job` è il job da schedulare: esegue `snapshot` e, se
  `NCMARKET_EXPORT=1`, anche l'`export` CSV in `/data/NCMarket/exports`. Esce con codice
  diverso da zero in caso di errore, così lo scheduler può notificare il fallimento.

```bash
# build ed esecuzione one-shot in locale
docker build -t ncmarket .
docker run --rm -v ncmarket-data:/data ncmarket snapshot --planet heimdall
docker run --rm -v ncmarket-data:/data ncmarket snapshots
```

Configurazione su Coolify: risorsa *Application* con build pack `Dockerfile`, nessun FQDN,
health check disabilitato (non è un servizio web), storage persistente montato su `/data`,
variabili `NCMARKET_PLANET` e `NCMARKET_EXPORT`, e uno *Scheduled Task* con comando
`snapshot-job` alla frequenza desiderata (es. `0 */6 * * *`).

Grazie all'archiviazione deduplicata (schema v2) uno snapshot scrive per intero solo le
inserzioni mai viste prima; quelle già note costano ~20 byte l'una. La crescita del
database dipende quindi dal ricambio del mercato, non dalla frequenza degli snapshot.
Per mettere un tetto allo storico si può schedulare anche `prune` (default: conserva
365 giorni), ad esempio una volta a settimana con un secondo *Scheduled Task*.

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
7. **Export CSV flat** ✅ — comando `export`: uno snapshot in CSV con statistiche e
   skill appiattite in colonne, pronto per Excel/analisi.

### Step 2 — Automazione e arricchimento (prossimi sviluppi)
- **Raccolta schedulata** ✅ — immagine Docker e job `snapshot-job` per l'esecuzione
  periodica di `snapshot` su server (vedi *Deploy su server*).
- **Storico deduplicato e retention** ✅ — schema v2 (`listings` + `sightings`): le
  inserzioni ripetute tra snapshot costano ~20 byte invece di una copia completa, con
  migrazione automatica dei database v1; comando `prune` (default: 365 giorni) per
  limitare la crescita del database.
- **Rilevazione vendite**: confronto tra snapshot consecutivi per distinguere item
  venduti da item ritirati (incrocio con le transazioni `BuyProduct` via 9cscan/mimir).
- **Filtri avanzati**: il servizio supporta anche `stat`, `itemIds[]`, `iconIds[]`,
  `isCustom` sulla stessa rotta — esporli nella CLI.

### Step 3 — Valutazioni (obiettivo finale)
- **Motore di pricing**: prezzo equo stimato per item/level/opzioni sulla base della
  serie storica (mediane mobili, percentili per grade/CP).
- **Segnalazione occasioni** ✅ — comando `deals`: confronto tra le offerte correnti
  (mercato live o ultimo snapshot) e le mediane storiche di prezzo e NCG/CP per
  coppia (item, livello), con soglie di sconto e campioni minimi configurabili.
- **Reportistica**: dashboard e grafici andamento prezzi (l'export CSV è già disponibile
  con il comando `export`).

## Requisiti

- .NET SDK 9.0+
- Accesso a internet verso `b.9capi.com` (market service) e `raw.githubusercontent.com`
  (nomi item, opzionale)

## Build

```powershell
dotnet build NC-Market/NCMarket.sln
```
