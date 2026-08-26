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
  timeout); su Odin le sole Weapon contano ~60.000 inserzioni;
- la rotta accetta anche i filtri `itemIds`, `iconIds` e `isCustom`, che **restringono
  davvero** il risultato, e li lega dal parametro **ripetuto una volta per valore**
  (`itemIds=10181000&itemIds=10182000`). Le altre due forme sbagliano in versi opposti:
  `itemIds=1,2` viene rifiutata con `422`, mentre `itemIds[]=1` riceve `200` ed è
  **ignorata** — cioè restituisce l'intero listino con l'aspetto di una risposta
  filtrata;
- `isCustom=true` **sovrascrive** `itemIds` e `iconIds`: chiesti insieme, gli id spariscono
  e la risposta è l'intero listino dei pezzi da custom craft (`itemIds=10181000` da solo
  restituisce l'item 10181000; con `isCustom=true` restituisce 20160003 e 20160004). Non è
  un capriccio del servizio: un pezzo da custom craft ha un id suo — la gamma `2016…`
  invece della `1018…` di una Transcendent ordinaria — quindi "questo item, ma custom" non
  nomina niente. NC-Market rifiuta la combinazione invece di spedirla; `isCustom=false`
  con gli id si combina regolarmente;
- il filtro `stat`, che la documentazione del servizio elenca accanto ai precedenti,
  **non restringe nulla** su questo deployment: né per nome (`stat=ATK`, `stat=Thorn`)
  né per valore numerico di `StatType`, né sotto `statType` o `stats`, e un valore
  inesistente come `stat=PIPPO` riceve `200` con il listino intero. Per questo NC-Market
  non lo espone: un'opzione che non si applica in silenzio è ciò che la validazione della
  riga di comando esiste per impedire. (Misure del 2026-08-25 su `b.9capi.com`.)

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
├── global.json                 Versione dell'SDK .NET richiesta (9.0)
├── README.md
├── .github/workflows/ci.yml    Build, test e build dell'immagine Docker su push e PR
├── src/
│   ├── NCMarket.Core/          Libreria riusabile
│   │   ├── Planet.cs           Registro pianeti/endpoint (Odin, Heimdall; default Heimdall)
│   │   ├── EquipmentType.cs    Enum equipaggiamenti + parsing
│   │   ├── Models/             DTO della risposta del market service
│   │   ├── MarketClient.cs     Client HTTP con paginazione automatica
│   │   ├── ListingFilter.cs    Filtri che il servizio applica alla query (item, icona, custom)
│   │   ├── IMarketListingSource.cs  Astrazione del listino corrente (la implementa MarketClient)
│   │   ├── ICaptureProgress.cs Avanzamento di una cattura, riportato mentre avviene
│   │   ├── SnapshotService.cs  Orchestrazione di 'snapshot': cattura, salva, finalizza
│   │   ├── DealService.cs      Orchestrazione di 'deals': baseline, listino da confrontare, esito
│   │   ├── DealAlertService.cs Segnalazione delle occasioni nuove, una volta sola ciascuna
│   │   ├── DealMessage.cs      Testo di una segnalazione (quattro righe per inserzione, in MarkdownV2)
│   │   ├── MarkdownV2.cs       Escaping di MarkdownV2, la sintassi che Telegram interpreta
│   │   ├── INotificationChannel.cs  Astrazione del canale di notifica
│   │   ├── TelegramNotifier.cs Invio su Telegram (Bot API) e lettura delle credenziali
│   │   ├── NameProvider.cs     Risoluzione id -> nome per item e skill (cache locale)
│   │   ├── ProductFormat.cs    Formattazione statistiche e skill delle inserzioni
│   │   ├── SnapshotCsvExporter.cs  Export CSV flat di uno snapshot
│   │   ├── DealFinder.cs       Rilevazione occasioni (confronto con le mediane storiche)
│   │   ├── DbLock.cs           Mutua esclusione fra i comandi che scrivono sul database
│   │   └── MarketDb.cs         Storicizzazione su SQLite, rilevazione vendite, query analitiche
│   └── NCMarket.Cli/           Applicazione console
│       ├── CommandLine.cs      Opzioni ammesse per verbo e loro validazione
│       ├── ConsoleReport.cs    Tabelle e messaggi a schermo
│       ├── ConsoleProgress.cs  Avanzamento di una cattura sulla console
│       ├── HelpText.cs         Testo del comando 'help'
│       └── Program.cs          Comandi: fetch, snapshot, snapshots, history, stats, deals,
│                               export, prune, notify-test
└── tests/
    └── NCMarket.Tests/         xUnit: schema e migrazioni, baseline, vendite, prune, deals,
                                notifiche, servizi, CLI
```

Scelte progettuali:

- **.NET 9 / C#**: coerente con l'ecosistema Nine Chronicles (lib9c, Libplanet) e con gli
  SDK già presenti sulla macchina.
- **Core separato dalla CLI**: `NCMarket.Core` contiene anche l'orchestrazione dei due
  comandi che fanno lavoro vero — `SnapshotService` (cattura del listino) e `DealService`
  (ricerca delle occasioni) — e non scrive nulla a schermo: riporta l'avanzamento tramite
  `ICaptureProgress` e restituisce un risultato. Un servizio schedulato o una dashboard li
  guidano come li guida la CLI; in `NCMarket.Cli` restano la lettura delle opzioni e la
  presentazione (`ConsoleReport`).
- **SQLite** (`Microsoft.Data.Sqlite`): zero amministrazione, file unico, adatto a
  storicizzazione e query aggregate. Percorso di default:
  `%LOCALAPPDATA%\NCMarket\ncmarket.db`, personalizzabile con `--db`.
- **Snapshot immutabili, archiviazione deduplicata**: ogni esecuzione di `snapshot`
  registra l'intero listino corrente con timestamp, ma ogni inserzione (`product_id`,
  i cui attributi non cambiano mai: un cambio prezzo crea un nuovo prodotto) è salvata
  una sola volta nella tabella `listings`; l'appartenenza ai singoli snapshot è
  tracciata dalla tabella `sightings` (due interi per riga). Le analisi storiche
  confrontano gli snapshot tra loro come prima.

## Schema del database (v4)

```sql
snapshots(
    id INTEGER PK,            -- progressivo
    planet TEXT,              -- odin | heimdall
    taken_at_utc TEXT,        -- ISO 8601
    item_sub_types TEXT,      -- sottotipi inclusi, es. "6,7,8,9,10"
    product_count INTEGER,    -- inserzioni osservate al momento della cattura
    status TEXT,              -- partial | complete
    max_per_type INTEGER      -- NULL = cattura integrale; il limite --max-per-type altrimenti
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

notified_deals(               -- occasioni già segnalate da 'deals --notify'
    product_id TEXT PK,       -- GUID dell'inserzione, non FK: si notifica anche il
                              -- mercato live, che contiene inserzioni mai storicizzate
    notified_at_utc TEXT      -- ISO 8601
)
```

`product_id` è stabile e immutabile per tutta la vita di un'inserzione (un cambio di
prezzo crea un nuovo prodotto): gli attributi vengono quindi salvati una sola volta e
uno snapshot che riosserva un'inserzione già nota aggiunge solo una riga di
`sightings` (~20 byte) e aggiorna il marcatore *last seen*. È il confronto fra snapshot
consecutivi a rendere osservabili le sparizioni, e quindi le vendite (vedi
[Rilevazione delle vendite](#rilevazione-delle-vendite)).

Uno snapshot nasce `partial` e diventa `complete` solo quando la cattura arriva in
fondo a tutti i tipi richiesti. Se il download di un tipo fallisce, i dati già raccolti
restano consultabili per id, ma `stats`, `deals --from-snapshot` ed `export` continuano
a usare **l'ultimo snapshot completo**, invece di lavorare in silenzio su un listino
monco. Il comando `snapshots` mostra lo stato di ciascuno.

`max_per_type` registra le catture volutamente troncate (`snapshot --max-per-type N`):
non coprono l'intero listino, quindi l'assenza di un'inserzione da uno di questi
snapshot non prova che sia uscita dal mercato. Anche loro sono esclusi dalla rilevazione
delle vendite.

`notified_deals` è ciò che rende una notifica un avviso e non un promemoria: un job
schedulato ritrova la stessa occasione a ogni esecuzione finché qualcuno non la compra,
quindi `deals --notify` invia soltanto le inserzioni mai segnalate prima. La chiave è
`product_id` per lo stesso motivo per cui lo è in `listings`: non cambia finché l'offerta
resta in piedi, e una rimessa in vendita a un prezzo diverso è un prodotto nuovo, cioè
un'offerta nuova, che va segnalata di nuovo. `prune` dimentica una segnalazione solo
quando dimentica anche la sua inserzione: farlo prima significherebbe rimandarla in chat.

I database creati con lo schema v1 (una copia completa di ogni inserzione per
snapshot) vengono **migrati automaticamente** alla prima apertura: viene lasciata una
copia di sicurezza `<db>.v1.bak` accanto al file originale (preceduta da un checkpoint
WAL, così la copia contiene anche le ultime scritture) e il database viene compattato
con `VACUUM`. I database v2 e v3 acquisiscono le colonne `status` e `max_per_type` in
place, senza backup: quelle migrazioni non sono distruttive. Gli snapshot già presenti
valgono come catture integrali, che è ciò che `snapshot` fa quando `--max-per-type` non
viene passato. Le tabelle e gli indici *nuovi* — `notified_deals` è l'ultimo — non hanno
migrazione né numero di versione: sono creati `IF NOT EXISTS` a ogni apertura, quindi un
database esistente li acquisisce da sé. Il database usa il journal WAL, quindi accanto al file
possono comparire i file di servizio `-wal` e `-shm`, più un file `.lock` vuoto usato
per serializzare `snapshot` e `prune`.

## Comandi CLI

```powershell
# Interrogazione live del mercato (senza salvare nulla); la tabella include
# statistiche (base + bonus opzioni) e skill di ogni inserzione
dotnet run --project src/NCMarket.Cli -- fetch --type weapon --order price --limit 20

# Scheda completa per inserzione: tutte le statistiche e il dettaglio delle skill
# (categoria, elemento, probabilità, potenza, cooldown)
dotnet run --project src/NCMarket.Cli -- fetch --type ring --order cp_desc --limit 5 --details

# Solo un item preciso: 10181000 è la Transcendent Sword di elemento Fire. Il filtro
# è applicato dal servizio, quindi la risposta costa una pagina invece dell'intero
# sottotipo; --custom false esclude i pezzi da custom craft
dotnet run --project src/NCMarket.Cli -- fetch --type weapon --item-ids 10181000 --custom false

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
# storici del database. Il confronto avviene tra item comparabili — stesso item,
# stesso livello e stesso numero di opzioni — e per default sulle sole inserzioni
# stimate vendute (vedi "Rilevazione delle vendite"); la metrica primaria è il
# rapporto NCG/CP (un CP alto a basso prezzo è un'occasione), lo sconto sul prezzo
# puro è mostrato come colonna secondaria
dotnet run --project src/NCMarket.Cli -- deals --discount 30

# Confronto con i prezzi richiesti invece che con le vendite stimate: utile finché
# lo storico è corto, ma il riferimento è gonfiato dalle inserzioni mai comprate
dotnet run --project src/NCMarket.Cli -- deals --baseline listed --discount 40

# Euristica di vendita più severa: conta come venduta solo un'inserzione sparita
# entro il +10% sulla mediana del proprio bucket (default: +20%)
dotnet run --project src/NCMarket.Cli -- deals --sale-margin 10

# Occasioni sull'ultimo snapshot (senza download live), con soglie personalizzate.
# Nota: i comparabili sono partizionati anche per numero di opzioni e la popolazione
# di riferimento sono le sole vendite stimate, quindi i bucket sono piccoli; sugli
# item poco scambiati può servire abbassare --min-samples (default 5) o allargare la
# finestra --days per avere abbastanza campioni
dotnet run --project src/NCMarket.Cli -- deals --type ring --from-snapshot --min-samples 3 --days 14

# Solo le rarità indicate (numero 1-8 o nome lib9c: normal, rare, epic, unique,
# legendary, divinity, mythic, transcendent)
dotnet run --project src/NCMarket.Cli -- deals --grade legendary,mythic

# Segnalazione su Telegram delle sole occasioni mai notificate prima: è la forma che
# 'deals' prende su un server, dove nessuno legge l'output. Token e chat si passano
# dall'ambiente, non dalla riga di comando (vedi "Notifiche su Telegram")
dotnet run --project src/NCMarket.Cli -- deals --from-snapshot --discount 30 --notify

# Messaggio di prova, per verificare token e chat senza aspettare la prima occasione
dotnet run --project src/NCMarket.Cli -- notify-test

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

Ogni comando accetta soltanto le proprie opzioni: un'opzione sconosciuta, ripetuta o
priva di valore, un argomento senza `--` o un valore fuori intervallo fanno terminare la
CLI con codice 2 senza eseguire nulla. Un refuso come `deals --dicount 30` è quindi un
errore esplicito, non un filtro che non si applica.

### Rilevazione delle vendite

Un listino non è un mercato: la mediana di tutte le inserzioni osservate include quelle
che **nessuno ha mai comprato**, che restano nel campione fino al `prune` e tengono alto
il riferimento. `deals` per default confronta quindi con le sole inserzioni che risultano
concluse, ricostruite in due passi dal confronto fra snapshot:

1. **sparizione** — un'inserzione ha lasciato il mercato quando uno snapshot successivo
   che *avrebbe potuto vederla* non l'ha vista: stesso pianeta, stesso tipo di
   equipaggiamento, `complete` (non una cattura interrotta) e integrale (senza
   `--max-per-type`). Tutto il resto resta "ancora in vendita": l'assenza da uno snapshot
   parziale o troncato è un artefatto della cattura, non un fatto di mercato;
2. **vendita o ritiro** — fra le sparite, chi chiedeva al più `--sale-margin` percento
   sopra la mediana richiesta del proprio bucket conta come vendita; chi è sparito molto
   sopra il prezzo corrente è quasi sempre un ritiro o una scadenza, e viene scartato.

L'euristica è volutamente asimmetrica — le vendite si concentrano nella parte bassa del
book — quindi un riferimento `sold` sta per costruzione sotto il corrispondente `listed`.
Per questo `deals` dichiara in testa alla tabella su quale popolazione sta confrontando e
come si è divisa (concluse / ancora in vendita / ritiri stimati): la tolleranza si giudica
sui numeri, non si prende per buona. `--baseline listed` torna al comportamento
precedente, `--sale-margin` regola la soglia.

Serve almeno un secondo snapshot completo e integrale perché una sparizione sia
osservabile: su un database appena creato `deals` lo dice invece di restituire una tabella
vuota. Il passo successivo in accuratezza è sostituire l'euristica con le transazioni
`BuyProduct` on-chain (9cscan/mimir), che sono il dato reale.

### Notifiche su Telegram

`deals --notify` manda in chat le occasioni trovate. È la forma che il comando prende su
un server, dove nessuno guarda l'output: un job che gira ogni poche ore non produce una
tabella da leggere, produce un messaggio quando c'è qualcosa da sapere.

**Configurazione** — due variabili d'ambiente, mai opzioni della riga di comando: un token
di bot è una credenziale al portatore, e le opzioni finiscono nella cronologia della shell,
nell'elenco dei processi della macchina e nella definizione dello Scheduled Task.

| Variabile | Come si ottiene |
|---|---|
| `NCMARKET_TELEGRAM_TOKEN` | `@BotFather`, comando `/newbot`: il token è nella risposta |
| `NCMARKET_TELEGRAM_CHAT_ID` | scrivere una volta al bot, poi leggere `https://api.telegram.org/bot<token>/getUpdates` e prendere `message.chat.id` (per gruppi e canali è negativo) |

Se manca una delle due, `--notify` fallisce **prima** della ricerca con codice 2, invece
di scoprirlo dopo i minuti del download. `notify-test` invia un messaggio di prova e serve
a distinguere "non ci sono occasioni" da "le notifiche non arrivano", che altrimenti sono
lo stesso silenzio.

Sul nome: Telegram chiama *webhook* la direzione opposta — un indirizzo che Telegram
contatta per consegnare a un bot i messaggi che gli vengono scritti. Qui non c'è niente da
ricevere: una segnalazione esce, quindi è una `POST` a `sendMessage`, e la macchina che
esegue il job non ha bisogno di indirizzo pubblico, porta in ingresso o certificato.

**Una volta sola per inserzione.** Una ricerca schedulata non è una persona che guarda:
ritrova la stessa occasione a ogni esecuzione finché qualcuno non la compra, e otto
notifiche al giorno per la stessa offerta sono notifiche che non si leggono più. Vengono
quindi segnalate solo le inserzioni mai segnalate prima, tenute in `notified_deals` (vedi
[Schema del database](#schema-del-database-v4)). Anche le occasioni oltre il limite di
`--top` contano come segnalate: il messaggio dichiara quante sono e dove vederle, mentre
lasciarle indietro le farebbe ripresentare a ogni esecuzione senza mai elencarle.

Il messaggio è scritto in **MarkdownV2**, la sintassi che Telegram interpreta: si legge su
un telefono, di corsa, e un prezzo dentro un riquadro monospaziato si distingue dalla frase
che lo circonda in un modo che il testo semplice non permette. Ogni inserzione occupa
quattro righe — cosa è, com'è fatta, quanto costa, perché è a buon mercato — e una riga 🔎
dichiara i filtri quando ce ne sono.

```
🏷️ *NC\-Market* — 2 nuove occasioni su `heimdall`
🔎 Ring · rarità Legendary · storico dal 2026\-08\-14
_Sconto ≥ 25% sulla mediana delle inserzioni concluse per item \+ livello \+ opzioni \(campioni ≥ 5\)_

*1\. Guardian Ring \+7*
Ring · grado 5 · 4 opzioni · CP `12,450`
💰 `142.50 NCG` — sconto `41.2%` su NCG/CP \(`38.0%` sul prezzo\)
📊 `87` CP/NCG vs mediana `148` su `12` inserzioni
```

Il prezzo del markup è l'escaping: il nome di un item è quello che il gioco ha deciso di
chiamarlo, e una parentesi non sfuggita non arriva storta, viene **rifiutata** da Telegram.
Ogni valore passa quindi per `MarkdownV2.Escape` o `MarkdownV2.Code`, nessuna entità
attraversa un a capo — così un messaggio spezzato sui 4096 caratteri resta valido parte per
parte — e se Telegram rifiuta comunque il parsing il testo riparte senza `parse_mode`:
qualche backslash a vista è meglio di un'occasione mai segnalata.

**Se l'invio fallisce** non viene registrato niente e il comando esce con codice diverso da
zero: la stessa occasione viene ritentata alla prossima esecuzione. È il verso giusto in
cui sbagliare — segnare come annunciata un'inserzione il cui messaggio non è partito la
renderebbe invisibile per sempre, mentre il caso opposto costa una notifica doppia. Un
token sbagliato o una chat inesistente non vengono ritentati sul momento (nessun numero di
tentativi li sistema) e l'errore riporta la spiegazione di Telegram **senza** il token, che
altrimenti finirebbe nei log.

Il canale è dietro un'interfaccia (`INotificationChannel`), quindi aggiungerne un secondo —
Discord, un webhook proprio — significa implementarla, senza toccare ciò che decide se e
cosa c'è da dire.

Test:

```bash
dotnet test
```

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
  diverso da zero in caso di errore, così lo scheduler può notificare il fallimento;
- lo script `docker/deals-job` è il secondo job: esegue `deals --notify` e manda in chat
  le occasioni nuove (vedi [Notifiche su Telegram](#notifiche-su-telegram)). Va schedulato
  dopo il primo, perché per default confronta l'ultimo snapshot — quello appena catturato —
  e non riscarica il listino; `NCMARKET_DEALS_ARGS` sostituisce le opzioni di default
  (`--from-snapshot`), ad esempio con `--from-snapshot --discount 30 --grade legendary,mythic`;
- il container gira come utente non privilegiato (`app`, l'utente standard delle immagini
  .NET): `/data` gli appartiene, e un volume Docker vuoto montato lì ne eredita i permessi.
  Un volume che contiene già dati scritti da una versione precedente dell'immagine, quando
  il processo girava come root, va reso accessibile una volta sola:
  `docker run --rm -u 0 --entrypoint chown -v ncmarket-data:/data ncmarket -R app:app /data`.

```bash
# build ed esecuzione one-shot in locale
docker build -t ncmarket .
docker run --rm -v ncmarket-data:/data ncmarket snapshot --planet heimdall
docker run --rm -v ncmarket-data:/data ncmarket snapshots
```

Configurazione su Coolify: risorsa *Application* con build pack `Dockerfile`, nessun FQDN,
health check disabilitato (non è un servizio web), storage persistente montato su `/data`,
variabili `NCMARKET_PLANET` e `NCMARKET_EXPORT`, e uno *Scheduled Task* con comando
`snapshot-job` alla frequenza desiderata (es. `0 */6 * * *`). Per le notifiche si
aggiungono `NCMARKET_TELEGRAM_TOKEN` e `NCMARKET_TELEGRAM_CHAT_ID` (da marcare come
segrete) e un secondo *Scheduled Task* con comando `deals-job`, sfasato di qualche minuto
dal primo (es. `10 */6 * * *`) perché confronta lo snapshot che quello ha appena
catturato. Prima di aspettare la prima occasione conviene verificare il canale con
`docker exec <container> ncmarket notify-test`.

Grazie all'archiviazione deduplicata (schema v2) uno snapshot scrive per intero solo le
inserzioni mai viste prima; quelle già note costano ~20 byte l'una. La crescita del
database dipende quindi dal ricambio del mercato, non dalla frequenza degli snapshot.
Per mettere un tetto allo storico si può schedulare anche `prune` (default: conserva
365 giorni), ad esempio una volta a settimana con un secondo *Scheduled Task*. I due job
non hanno bisogno di essere sfasati a mano: `snapshot` e `prune` prendono un lock su
`<database>.lock`, quindi se si sovrappongono il secondo attende (fino a 30 minuti) invece
di fallire sul `VACUUM`.

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
- **Integrità dei dati raccolti** ✅ — schema v4: gli snapshot interrotti restano marcati
  come parziali e non vengono più scelti come "ultimo snapshot", le catture troncate sono
  riconoscibili; la CLI rifiuta opzioni e argomenti non riconosciuti; suite di test xUnit
  e CI su push e pull request.
- **Rilevazione vendite** ✅ — confronto tra snapshot consecutivi per distinguere item
  venduti da item ritirati; `deals` calcola per default le mediane sulle sole inserzioni
  concluse e dichiara la popolazione usata (vedi
  [Rilevazione delle vendite](#rilevazione-delle-vendite)). Resta da fare l'incrocio con
  le transazioni `BuyProduct` via 9cscan/mimir, che sostituirebbe l'euristica col dato
  reale.
- **Filtri avanzati** ✅ — `fetch --item-ids`, `--icon-ids` e `--custom` passano al
  servizio i filtri che applica sulla stessa rotta, così una domanda su un singolo item
  costa una pagina e non un sottotipo intero. `stat` resta fuori: misurato, non
  restringe nulla (vedi [Fonte dati](#fonte-dati)).

### Step 3 — Valutazioni (obiettivo finale)
- **Motore di pricing**: prezzo equo stimato per item/level/opzioni sulla base della
  serie storica (mediane mobili, percentili per grade/CP).
- **Segnalazione occasioni** ✅ — comando `deals`: confronto tra le offerte correnti
  (mercato live o ultimo snapshot) e le mediane storiche di prezzo e NCG/CP per
  terna (item, livello, numero di opzioni), calcolate sulle inserzioni stimate vendute,
  con soglie di sconto e campioni minimi configurabili. Con `--notify` le occasioni nuove
  arrivano su Telegram invece di restare in una tabella che nessuno guarda, una volta sola
  per inserzione (vedi [Notifiche su Telegram](#notifiche-su-telegram)); il job
  `docker/deals-job` è la forma schedulata dello stesso comando.
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

## Licenza

[MIT](LICENSE).
