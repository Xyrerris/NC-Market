# Backlog interventi — NC-Market

Analisi del 2026-08-12 sul branch `feature/docker-deploy` (commit `c23eeba`).
Documento di lavoro: elenca gli interventi individuati, il motivo per cui esistono e
come verificare che siano chiusi. Il piano di prodotto a lungo termine resta nel
[README](README.md#piano-di-sviluppo); qui c'è il dettaglio operativo.

**Aggiornato il 2026-08-13**: chiusi tutti i P0, più P2.1, P2.2, P2.3 e la maggior parte
di P2.6.

**Aggiornato il 2026-08-14**: chiuso P1.2 (bucket dei comparabili). Restano aperti P1.1,
P2.4, P2.5 e l'ultimo punto di P2.6.

**Aggiornato il 2026-08-16**: chiuso P1.1 (rilevazione vendite), l'ultimo P1. Restano
aperti P2.4, P2.5 e l'ultimo punto di P2.6: nessuno dei tre tocca la correttezza del
motore di valutazione.

**Aggiornato il 2026-08-16 (2)**: chiuso P2.4 (orchestrazione estratta in `NCMarket.Core`).
Restano aperti P2.5 — che è una decisione, non del lavoro — e l'ultimo punto di P2.6.

**Aggiornato il 2026-08-18**: chiuso l'ultimo punto di P2.6 (le baseline si calcolano a
flusso, un bucket per volta). La misura che il punto aspettava ha però mostrato che
l'indice `ix_listings_last_seen` non viene mai usato dalla finestra `--days`: è la nuova
voce P2.7. Resta aperto P2.5, che è una decisione, non del lavoro.

**Aggiornato il 2026-08-20**: chiuso P2.5 (licenza MIT) e allineato il repository —
P2.4 e P2.6 sono ora su `main`. Resta aperto il solo P2.7, che è parcheggiato in attesa
di un motivo: la finestra `--days` diventa l'uso normale con il job di notifica, ed è
allora che indicizzarla ha senso.

**Aggiornato il 2026-08-21**: chiuso P2.7, l'ultimo punto aperto. Il piano che la voce
conteneva è stato scartato sulla misura — `ANALYZE` non discrimina la finestra, perché
la libreria non ha `SQLITE_ENABLE_STAT4` — e sostituito da un indice di copertura, che
migliora anche il default e toglie alla voce la dipendenza dal job di notifica. Il
backlog non ha più punti aperti.

**Aggiornato il 2026-08-21 (2)**: fatto il punto 1 dei prossimi passi — la notifica delle
occasioni su Telegram — che è la prima voce non di debito del backlog e apre la nuova
sezione P3. Il resto dei prossimi passi è invariato.

**Aggiornato il 2026-08-24**: chiusa la coda di P2.1 — `SnapshotCsvExporter.Write`,
`NameProvider.SplitCsvLine`, `EquipmentTypes.TryParse`, `Grades.TryParse`,
`ProductFormat.*`, la migrazione v1 → v2 e `MarketClient` hanno adesso asserzioni: da 61 a
177 test. Con i due job in esecuzione sul server, la taratura delle soglie sui dati veri
resta il primo dei prossimi passi; gli altri due sono estensioni. Ripulita anche la
lista dei branch: restano `main` e `feature/docker-deploy`.

**Aggiornato il 2026-09-04**: chiusa la nuova sezione P4 — "quanto vale questo pezzo?"
chiesto al bot Telegram — nelle sue cinque voci, che erano le cinque fasi di
`PIANO-VALUTAZIONE.md`: il piano è stato ripiegato qui alla chiusura dell'ultima, come
prevedeva, e il file è sparito perché due copie delle stesse decisioni divergono. Il
progetto ora riceve su Telegram e non solo annuncia. Da 177 a 343 test. I prossimi passi
sono stati riscritti: P4.1 ha chiuso la metà "filtri" del vecchio punto 2, e le tre cose
che la v1 di P4 non fa sono diventate voci a sé.

Legenda priorità:

- **P0** — bug che corrompono i dati o li nascondono; da fare prima di aggiungere feature.
- **P1** — limiti concettuali del motore di valutazione; è qui che sta il valore.
- **P2** — infrastruttura e debito tecnico; abilitano il lavoro successivo.
- **P3** — quello che il motore, una volta corretto, permette di fare.
- **P4** — la stessa domanda posta al contrario: non "cosa conviene comprare" su tutto il
  mercato, ma "quanto vale questo", su un pezzo solo e da chi ce l'ha in mano.

---

## Stato del repository

| Voce | Stato al 2026-08-12 | Stato al 2026-08-20 |
|---|---|---|
| Branch di lavoro | `feature/docker-deploy`, **10 commit avanti** su `origin/main` | ✅ nessuno in sospeso: `perf/baseline-streaming` (P2.4 + P2.6) è stato unito a `main` |
| `origin/main` | fermo ai commit iniziali | ✅ allineato: contiene tutto il lavoro fino a P2.6 |
| Build locale | **fallisce**: SDK 8.0.204 contro target `net9.0` | ✅ verde (SDK 9.0.317, versione fissata da `global.json`) |
| Test | nessuno | ✅ 177 test xUnit in `tests/NCMarket.Tests`, tutti verdi |
| CI | nessuna | ✅ `.github/workflows/ci.yml`: build + test + build dell'immagine Docker |
| File spuri tracciati | `p0.txt`, `p1.txt` | ✅ rimossi dal tracciamento, `.gitignore` esteso |
| Licenza | assente | ✅ MIT ([LICENSE](LICENSE)) |

---

## P0 — Correttezza

### P0.1 — Snapshot parziali silenziosi ✅ FATTO

**Dove**: [src/NCMarket.Cli/Program.cs](src/NCMarket.Cli/Program.cs) (`SnapshotAsync`)

Se il download di uno dei cinque tipi fallisce, l'eccezione risale al `catch` globale.
A quel punto la riga in `snapshots` è già stata creata e i `sightings` dei tipi già
scaricati sono committati, ma `FinalizeSnapshot` non viene mai chiamato: resta uno
snapshot con `product_count = 0` e contenuto incompleto.

Il danno è a valle: `GetLatestSnapshotId` lo restituisce come "ultimo snapshot", quindi
`stats`, `export` e `deals --from-snapshot` lavorano su dati parziali senza alcun avviso.

**Fatto**: schema v3 con colonna `status` su `snapshots` (`partial` | `complete`).
Uno snapshot nasce `partial`; `FinalizeSnapshot` lo promuove a `complete`.
`GetLatestSnapshotId` considera solo i completi. Il comando `snapshots` mostra la colonna
Stato e avvisa quando ne esistono di parziali; uno `snapshot` interrotto stampa un
messaggio esplicito; `export --snapshot <id>` di uno parziale avvisa ma procede (è una
richiesta esplicita). I database v2 acquisiscono la colonna in place: gli snapshot con
`product_count > 0` vengono marcati completi, perché `FinalizeSnapshot` era l'unico
scrittore di quel contatore.

**Verificato da**: `MarketDbTests.A_snapshot_is_partial_until_it_is_finalized`,
`GetLatestSnapshotId_ignores_an_interrupted_snapshot`,
`GetLatestSnapshotId_is_null_when_every_snapshot_is_partial`,
`MarketDbMigrationTests.A_v2_database_gains_the_status_column_on_open`.

### P0.2 — Gli errori 4xx vengono ritentati tre volte ✅ FATTO

**Dove**: [src/NCMarket.Core/MarketClient.cs](src/NCMarket.Core/MarketClient.cs)

`response.EnsureSuccessStatusCode()` lanciava `HttpRequestException`, intercettata dal
`catch (HttpRequestException)` del ciclo di retry e quindi ritentata. Un 404 o un 400
costavano 6 secondi di attesa e producevano il messaggio fuorviante "dopo 3 tentativi".

**Fatto**: `EnsureSuccessStatusCode` sostituito da un controllo esplicito. Solo ciò che
passa `IsTransient` (5xx, 408, 429) rientra nel ciclo; ogni altra risposta non di successo
lancia subito un errore che riporta status code, reason phrase e URL. Il
`catch (HttpRequestException)` resta per i soli errori di trasporto, che sono realmente
transitori.

**Da verificare a mano**: una richiesta che riceve 404 fallisce immediatamente (non
coperto da test: servirebbe un `HttpMessageHandler` finto, vedi *Prossimi passi*).

### P0.3 — Backup di migrazione potenzialmente incompleto ✅ FATTO

**Dove**: [src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs) (`MigrateV1ToV2`)

`File.Copy(DbPath, backupPath)` copia il solo file `.db`. Se il database v1 è in modalità
WAL con un `-wal` non consolidato, il backup perde le ultime scritture — ed è la sola rete
di sicurezza di una migrazione distruttiva (`DROP TABLE products`).

**Fatto**: `PRAGMA wal_checkpoint(TRUNCATE);` immediatamente prima della copia.

### P0.4 — Opzioni sconosciute ignorate in silenzio ✅ FATTO

**Dove**: [src/NCMarket.Cli/CommandLine.cs](src/NCMarket.Cli/CommandLine.cs) (nuovo)

`ParseOptions` raccoglieva qualunque `--chiave` in un dizionario senza validare nulla:
`deals --dicount 30` non produceva errore, gli argomenti senza `--` sparivano in silenzio,
`snapshots --planet odn` restituiva zero righe invece di un errore, `--top -5` passava.

**Fatto**: nuova classe `CommandLine` con l'elenco delle opzioni ammesse per ciascun verbo.
Vengono rifiutati con codice di uscita 2, indicando il token incriminato: verbo sconosciuto,
opzione non ammessa per quel verbo, opzione ripetuta, opzione di valore senza valore,
argomento privo di `--`. I valori numerici sono validati per intervallo
(`--top ≥ 1`, `--discount` 0-100, `--days ≥ 1` per `prune`, ...) e `snapshots --planet`
passa ora da `GetPlanet()` come gli altri comandi, restando opzionale.

**Verificato da**: `CommandLineTests` (12 casi).

### P0.5 — Indice mancante su `last_seen_at_utc` ✅ FATTO

**Dove**: [src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs) (`CreateSchema`)

`last_seen_at_utc` è la colonna di filtro sia di `Prune` sia della finestra `--days` di
`GetPriceBaselines`: entrambe facevano scansione completa di `listings`.

**Fatto**: `ix_listings_last_seen` creato in `CreateSchema`, che gira a ogni apertura —
quindi anche sui database già esistenti, senza migrazione dedicata.

**Verificato da**: `MarketDbTests.Prune_filters_listings_through_the_last_seen_index`
(asserisce su `EXPLAIN QUERY PLAN`) e
`MarketDbMigrationTests.A_v2_database_keeps_its_data_and_gains_the_missing_index`.

**Corretto il 2026-08-18**: metà della motivazione qui sopra era sbagliata. L'indice
serve `Prune`, ed è quello che il test verifica; la finestra `--days` di
`GetPriceBaselines` invece non lo usa e non lo ha mai usato, perché il predicato è scritto
come disgiunzione (`$since IS NULL OR last_seen_at_utc >= $since`) e il planner non può
risolverla con un indice. `EXPLAIN QUERY PLAN` su quella query dice
`SEARCH listings USING INDEX ix_listings_planet_subtype (planet=?)` con qualunque valore
di `$since`. Il seguito è in P2.7.

### P0.6 — Conflitto tra job schedulati ✅ FATTO

**Dove**: [src/NCMarket.Core/DbLock.cs](src/NCMarket.Core/DbLock.cs) (nuovo)

`VACUUM` richiede accesso esclusivo al database. Con la configurazione suggerita nel README
(snapshot ogni 6 ore, prune settimanale) prima o poi i due Scheduled Task si sovrappongono e
il prune fallisce dopo il `busy_timeout` di 5 s.

**Fatto**: lock a livello applicativo invece che di shell, così vale comunque siano lanciati
i comandi (Docker, Scheduled Task, esecuzione manuale). `DbLock` apre in modo esclusivo un
file sentinella `<db>.lock`; `snapshot` e `prune` lo tengono per tutta la durata, attendendo
fino a 30 minuti chi lo detiene e segnalando l'attesa a schermo. Il lock è rilasciato dal
sistema operativo anche se il processo viene ucciso.

### P0.7 — Pulizia del repository ✅ FATTO

`p0.txt` e `p1.txt` rimossi dal tracciamento con `git rm --cached` (restano in locale).
`.gitignore` esteso a `*.db`, `*.db-wal`, `*.db-shm`, `*.db.lock`, `*.bak`, `*.csv`,
`p0.txt`, `p1.txt`, `.vs/`, `.idea/`, `*.user`.

---

## P1 — Il limite concettuale del motore `deals`

### P1.1 — I baseline usano prezzi richiesti, non prezzi di vendita ✅ FATTO

**Dove**: [src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs) (`GetPriceBaselines`),
[src/NCMarket.Cli/Program.cs](src/NCMarket.Cli/Program.cs) (`DealsAsync`)

Era il problema più importante dell'intero progetto. Le mediane erano calcolate su tutte
le inserzioni osservate, incluse quelle che **nessuno ha mai comprato**. Un'inserzione
sovrapprezzata restava nel campione fino al `prune` (365 giorni di default) e alzava il
riferimento: `deals` finiva per segnalare come occasione ciò che era soltanto meno assurdo
del resto del listino. Sui mercati illiquidi (gradi 7-8, pochi scambi) l'effetto dominava
il risultato.

**Fatto**: `GetPriceBaselines` prende ora una `BaselinePopulation` e restituisce un
`BaselineSet` (baseline + `ListingOutcomes`, cioè come si è divisa la popolazione).
Con `Sold` — il default di `deals` — le mediane si calcolano in due passi:

1. **sparizione**: un'inserzione ha lasciato il mercato quando esiste uno snapshot
   successivo che avrebbe potuto vederla e non l'ha vista. "Avrebbe potuto" significa
   stesso pianeta, stesso `item_sub_type`, `complete` (prerequisito P0.1) e non troncato.
   L'ultimo snapshot che soddisfa queste condizioni è la *frontiera di copertura* del
   tipo: `last_seen_snapshot_id < frontiera` è la condizione di sparizione;
2. **vendita o ritiro**: fra le sparite, chi chiedeva al più `--sale-margin` percento
   (default 20) sopra la mediana richiesta del proprio bucket conta come vendita; sopra
   quella soglia è quasi sempre un ritiro o una scadenza, e viene scartata.

I `--max-per-type` hanno reso necessario lo **schema v4**: una cattura troncata è un
campione, non il listino, quindi l'assenza di un'inserzione da uno di questi snapshot non
prova nulla. La colonna `snapshots.max_per_type` (NULL = cattura integrale) la rende
riconoscibile e la esclude dalla frontiera; senza, un solo `snapshot --max-per-type`
avrebbe classificato come vendute tutte le inserzioni che non era arrivato a scaricare.
La migrazione è in place e considera integrali gli snapshot preesistenti, che è ciò che
`snapshot` fa quando l'opzione non viene passata.

`deals` dichiara in testa alla tabella la popolazione di confronto e la sua composizione
(concluse / ancora in vendita / ritiri stimati), così la tolleranza si giudica sui numeri;
`--baseline listed` torna al comportamento precedente e `--sale-margin` senza
`--baseline sold` è un errore, non un'opzione che non si applica. Su un database senza
un secondo snapshot completo, `deals` spiega perché non ci sono vendite invece di
restituire una tabella vuota.

**Onestà dello stimatore**: l'euristica è asimmetrica per costruzione — scarta le sparite
care, non le sparite a poco — quindi una mediana `sold` sta sotto la corrispondente
`listed` anche quando tutte le sparizioni fossero vendite reali. È il motivo per cui la
composizione della popolazione viene stampata invece che nascosta.

**Verificato da**: `MarketDbTests.A_listing_gone_from_a_later_snapshot_is_measured_as_a_sale`,
`A_listing_still_on_sale_is_not_a_sale`,
`The_sale_margin_decides_where_a_disappearance_stops_being_a_sale`,
`An_interrupted_snapshot_is_not_proof_that_a_listing_is_gone`,
`A_snapshot_of_another_type_is_not_proof_that_a_listing_is_gone`,
`A_truncated_snapshot_is_not_proof_that_a_listing_is_gone`,
`A_snapshot_of_another_planet_is_not_proof_that_a_listing_is_gone`,
`MarketDbMigrationTests.A_v2_database_gains_the_capture_limit_column_on_open`.

**Resta aperto, in prospettiva** (non necessario a chiudere questa voce): incrocio con le
transazioni `BuyProduct` on-chain via 9cscan o mimir, che sostituirebbe l'euristica con il
dato reale. Un secondo affinamento a costo minore: usare `seller_agent` per riconoscere il
*re-listing* (sparizione seguita dalla ricomparsa dello stesso venditore nello stesso
bucket) e classificarlo come ritiro anche quando il prezzo era basso.

### P1.2 — Bucket dei comparabili troppo grossolano ✅ FATTO

**Dove**: [src/NCMarket.Core/DealFinder.cs](src/NCMarket.Core/DealFinder.cs),
[src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs)

La chiave di confronto era `(item_id, level)`: ignorava `option_count`, tipo elementale e
skill, che muovono il prezzo quanto il livello. Un +0 con 4 opzioni e un +0 con 1 opzione
finivano nello stesso bucket e si "scontavano" a vicenda.

**Fatto**: la chiave è ora il tipo `BaselineKey(ItemId, Level, OptionCount)`, con il
metodo `BaselineKey.Of(product)` come unico punto in cui la chiave viene derivata da
un'inserzione — così i bucket costruiti da `GetPriceBaselines` e le ricerche fatte da
`DealFinder` non possono divergere. `PriceBaseline` porta la chiave invece di
`ItemId`/`Level` sciolti. Nessuna migrazione: `option_count` era già una colonna di
`listings`, la query si limita a leggerla. Il comando `deals` mostra la colonna `Opz` e
dichiara nell'intestazione che il confronto è per item+livello+opzioni.

Il `grade` non è stato aggiunto alla chiave di proposito: è una proprietà dell'`item_id`,
quindi non partizionerebbe nulla.

**Conseguenza operativa**: i bucket sono più piccoli, quindi con `--min-samples 5` (il
default) sugli item poco scambiati `deals` restituisce meno righe di prima. Sono meno ma
valide: prima il numero era gonfiato da confronti fra pezzi non comparabili. Il README lo
segnala accanto all'esempio del comando.

**Resta aperto, in prospettiva** (non necessario a chiudere questa voce): sostituire il
bucketing con un modello di prezzo normalizzato per CP e statistiche robuste (mediana +
MAD invece della sola mediana). Aveva senso affrontarlo dopo P1.1, perché cambiare
stimatore su una popolazione sbagliata non migliora il risultato; ora che i baseline si
calcolano sulle inserzioni concluse, il prerequisito è soddisfatto.

**Verificato da**: `DealFinderTests.A_listing_is_not_compared_with_a_different_option_count`,
`Each_option_count_is_measured_against_its_own_baseline`,
`MarketDbTests.GetPriceBaselines_keeps_a_separate_bucket_per_option_count`.

---

## P2 — Infrastruttura e debito tecnico

### P2.1 — Nessun test ✅ FATTO

Progetto `tests/NCMarket.Tests` (xUnit), 177 test, nessuna dipendenza di rete:

- `MarketDbTests` — stato degli snapshot, `GetLatestSnapshotId`, deduplicazione di
  `AddProducts`, mediane, partizionamento dei bucket e finestra `--days` di
  `GetPriceBaselines`, rilevazione delle vendite (sparizione, soglia di
  classificazione, e i quattro casi in cui uno snapshot non fa prova: parziale,
  troncato, di un altro tipo, di un altro pianeta), raggruppamento di un bucket sparso
  nel listino, `Prune` con e senza `--dry-run`, uso effettivo dell'indice via
  `EXPLAIN QUERY PLAN`;
- `MarketDbMigrationTests` — un database v2 costruito a mano viene migrato a v4
  conservando i dati, classificando correttamente snapshot completi e parziali e
  acquisendo la colonna `max_per_type`;
- `DealFinderTests` — soglie, campioni minimi, metrica CP contro metrica prezzo,
  comparabilità per numero di opzioni, ordinamento;
- `SnapshotServiceTests` — cattura completa e finalizzazione, cattura interrotta (i tipi
  già scaricati restano, lo snapshot resta parziale), limite per tipo registrato,
  rifiuto di una cattura senza tipi;
- `DealServiceTests` — i quattro esiti possibili di una ricerca, confronto con l'ultimo
  snapshot e con il mercato live, filtro rarità, sorgente di mercato mancante o del
  pianeta sbagliato;
- `DbLockTests` — il secondo detentore attende e poi rinuncia; il rilascio libera il lock;
- `CommandLineTests` — tutti i modi in cui una riga di comando può essere sbagliata.

**Aggiunte il 2026-08-24**, a chiudere la coda che il punto 2 dei prossimi passi teneva
aperta:

- `SnapshotCsvExporterTests` — 7 casi: l'export vuoto è la sola intestazione, la riga per
  intero di un'inserzione, base e bonus di una stat in due colonne con i ripetuti sommati,
  una stat che questa build non conosce che si prende comunque le sue colonne, tanti gruppi
  skill quanti ne serve all'inserzione più ricca con le righe corte riempite di celle
  vuote, la quotatura RFC 4180, e il fatto che a decidere cosa quotare sia il separatore
  scelto;
- `MarketClientTests` — 14 casi: la richiesta (tipo nel percorso, finestra in query,
  `User-Agent` dichiarato e non sovrascritto se il chiamante ne ha già uno), la lettura
  campo per campo di un'inserzione nel formato del servizio, la risposta vuota, il 404 che
  non si ritenta e il 503 che sì, le tre transient di fila che si arrendono conservando la
  causa, la paginazione fino alla prima pagina corta, la de-duplicazione fra pagine e le
  tre pagine sterili che chiudono il giro, `--max-per-type` che taglia, il progresso, e
  l'`HttpClient` prestato che non viene chiuso;
- `MarketDbMigrationTests` — 3 casi in più sulla v1 → v2: la deduplicazione che non perde
  una presenza, il primo e l'ultimo avvistamento ricostruiti dagli id di snapshot, e il
  backup lasciato accanto al file con il database che esce già alla versione corrente;
- `NameProviderTests` — 4 casi in più: un nome quotato che contiene virgole e virgolette,
  le righe che non sono un nome e costano solo se stesse, l'avvio offline senza cache che
  non lascia dietro una cache vuota, e la cache fresca che non viene riscaricata;
- `ProductFormatTests` — 9 casi: nome noto, ripiego sulle serie di grado 7 e 8, id che
  resta numerico quando non c'è niente da inferire e quando non c'è nessun nome caricato,
  fusione di base e bonus per stat, stat a zero comunque stampata, skill per nome e
  probabilità, riga di dettaglio con e senza i segmenti opzionali;
- `EquipmentTypesTests` e `GradesTests` — nomi, alias (`sword`, `divine`), forma numerica
  lib9c, spazi e maiuscole, rifiuto che non lascia dietro un valore, e il controllo che
  `All` copra ogni membro dichiarato nell'enum.

Due dei nuovi guardiani sono stati controllati per mutazione: forzando `seen.Add` a
riuscire sempre in `GetAllProductsAsync` cadono i due test sulla de-duplicazione, e
togliendo una cella al riempimento delle colonne skill cade quello sul padding.

**Il prezzo**: la suite passa da 1 a 10 secondi. Otto se ne vanno nei due test che
attraversano davvero il backoff di `GetProductsPageAsync` (2s, poi 4s). È il costo di
verificare la politica di retry sul codice vero invece che su una copia con i tempi
azzerati, ed è la parte del comportamento che, sbagliata, si nota solo di notte sul
server.

### P2.2 — Nessuna CI e SDK non fissato ✅ FATTO

`global.json` fissa l'SDK a 9.0 (`rollForward: latestMinor`), quindi una macchina con il
solo SDK 8 fallisce subito con un messaggio chiaro invece che con NETSDK1045 sul singolo
progetto. `.github/workflows/ci.yml` esegue su push su `main` e su ogni PR: `restore`,
`build -c Release`, `dotnet test`, e in un job separato il build dell'immagine Docker.
Il workflow prende la versione dell'SDK da `global.json`, così non può divergere.

### P2.3 — Container eseguito come root ✅ FATTO

**Dove**: [Dockerfile](Dockerfile)

Aggiunti `chown -R $APP_UID:$APP_UID /data` e `USER $APP_UID`, sfruttando l'utente non
privilegiato `app` già presente nelle immagini .NET 8+. Il README documenta il caso del
volume preesistente popolato da root.

### P2.4 — `Program.cs` monolitico ✅ FATTO

**Dove**: [src/NCMarket.Core/SnapshotService.cs](src/NCMarket.Core/SnapshotService.cs) (nuovo),
[src/NCMarket.Core/DealService.cs](src/NCMarket.Core/DealService.cs) (nuovo),
[src/NCMarket.Cli/ConsoleReport.cs](src/NCMarket.Cli/ConsoleReport.cs) (nuovo),
[src/NCMarket.Cli/Program.cs](src/NCMarket.Cli/Program.cs)

Il parsing degli argomenti era già uscito da `Program.cs` con `CommandLine.cs`; restavano
orchestrazione e presentazione mescolate nello stesso file di 876 righe. Andava fatto
**prima** del punto Reportistica della roadmap: una dashboard o un job schedulato
avrebbero dovuto riscrivere `SnapshotAsync` e `DealsAsync`, non riusarli.

**Fatto**: l'orchestrazione è in `NCMarket.Core` come due servizi che non scrivono nulla
a schermo.

- `SnapshotService.CaptureAsync` crea lo snapshot, scarica un tipo alla volta, salva ogni
  tipo appena arriva e finalizza solo alla fine — l'ordine che rende recuperabile una
  cattura interrotta (P0.1). Restituisce un `SnapshotReport` (id, composizione, totale).
- `DealService.FindAsync` calcola le baseline, procura il listino da giudicare (mercato
  live o ultimo snapshot), applica il filtro rarità e chiama `DealFinder`. I tre modi in
  cui la domanda non ha risposta — niente storico, nessuna inserzione ancora conclusa,
  nessuno snapshot completo — tornano come `DealStatus`, non come risultato vuoto: sono
  situazioni diverse e nessuna significa "non c'è niente di conveniente".
- L'avanzamento passa da `ICaptureProgress`: la CLI riscrive una riga per tipo, un job
  schedulato scriverebbe su log. `IMarketListingSource` astrae il listino corrente
  (`MarketClient` la implementa), il che rende i due servizi verificabili senza rete.

Il lock resta fuori dai servizi: serializzare i processi è compito di chi lancia i
comandi. In `NCMarket.Cli` ogni comando ora legge le proprie opzioni, chiama il servizio e
passa il risultato a `ConsoleReport`, che raccoglie tutto ciò che va a schermo
(`HelpText` per il manuale, `ConsoleProgress` per l'avanzamento). `Program.cs` passa da
876 a 405 righe e l'output della CLI è invariato.

**In più**: `snapshot --types ,,` creava uno snapshot senza alcun tipo, che veniva
finalizzato come *completo* e diventava l'ultimo snapshot del pianeta, nascondendo il
listino vero a `stats`, `deals` ed `export`. Ora è un errore con codice 2, e il servizio
rifiuta comunque una cattura senza tipi.

**Verificato da**: `SnapshotServiceTests` (4 casi: cattura completa, cattura interrotta,
limite per tipo, nessun tipo) e `DealServiceTests` (9 casi: i quattro `DealStatus`,
percorso snapshot e percorso live, filtro rarità, sorgente mancante, pianeta sbagliato).

### P2.5 — Manca il LICENSE ✅ FATTO

Repository pubblico senza file di licenza. La scelta era del proprietario del repository
e, una volta pubblicata, la concessione non è di fatto revocabile per le versioni già
rilasciate: per questo non è stata fatta d'ufficio.

**Fatto**: [LICENSE](LICENSE) con il testo MIT, la scelta usuale per un progetto personale
su GitHub. Apache-2.0 sarebbe stata l'alternativa, per la concessione esplicita di
brevetto che aggiunge; qui non c'è nulla di brevettabile da concedere.

### P2.6 — Dettagli minori

- ✅ `FinalizeSnapshot` usa ora i parametri come il resto della classe.
- ✅ `GetSnapshot(id)` interroga direttamente per id invece di caricare tutti gli snapshot
  e filtrare in memoria.
- ✅ `MarketClient` imposta uno `User-Agent` che identifica il progetto e attende 250 ms
  fra una pagina e la successiva.
- ✅ `GetPriceBaselines` non tiene più in memoria l'intero storico: le baseline si
  calcolano a flusso, un bucket per volta.

**La misura che il punto aspettava**. Il punto era rimasto aperto in attesa di un numero,
e il numero dice due cose diverse da quelle che ci si aspettava. Su database sintetici
(cattura ogni 6 ore per un anno, cinque tipi, ~22.000 bucket a 2 milioni di inserzioni),
popolazione `listed`, prima dell'intervento:

| Inserzioni | Tempo | Heap vivo al picco | Allocato |
|---|---|---|---|
| 100.000 | 354 ms | 19 MB | 23 MB |
| 500.000 | 1.424 ms | 73 MB | 108 MB |
| 2.000.000 | 4.589 ms | 293 MB | 411 MB |

1. **il tempo non era il problema, e non era ottimizzabile in C#**: leggere le sole righe
   da SQLite, senza costruire niente, costa 4.611 ms su 2 milioni di inserzioni, cioè
   l'intero tempo della chiamata. Il bucketing in C# è gratis in confronto: qualunque
   riscrittura dell'aggregazione avrebbe lasciato il tempo dov'era;
2. **la memoria invece cresceva senza limite**, ed è la parte che il punto denunciava. Il
   picco non è nemmeno il dato utile: `List<T>` raddoppia, quindi nell'istante del
   ridimensionamento convivono il vettore vecchio e quello nuovo, ed è quello a fare i
   293 MB. A storico doppio sarebbero stati il doppio.

**Fatto**: la query è ordinata per chiave del bucket e l'aggregazione consuma il lettore a
gruppi, un bucket alla volta (`ReadBuckets`), riusando un solo buffer per le mediane
(`Summarise`). In memoria sta un bucket — una manciata di pezzi comparabili — invece di un
anno di catture. Il raggruppamento resta a SQLite, che sa versare su disco; la mediana
resta in C#, quindi nessuna funzione finestra e nessuna perdita di leggibilità: era
l'obiezione che aveva bloccato il punto la prima volta.

| Inserzioni | Tempo | Heap vivo al picco | Allocato |
|---|---|---|---|
| 100.000 | 359 ms | 2 MB | 2 MB |
| 500.000 | 1.363 ms | 4 MB | 4 MB |
| 2.000.000 | 6.192 ms | 4 MB | 4 MB |

La memoria è **piatta**: non dipende più dallo storico, solo dal bucket più grande. Il
prezzo è l'ordinamento, e si paga tutto sul caso più grande: fino a 500.000 inserzioni il
tempo è invariato (dentro il rumore), a 2 milioni la popolazione `listed` passa da 4.589 a
6.192 ms (+35%). La popolazione `sold` — quella di default — ci perde molto meno (5.868 →
6.159 ms, +5%) perché in cambio sparisce la seconda aggregazione completa che serviva a
ricalcolare le mediane sul sottoinsieme concluso, e sotto le 500.000 inserzioni migliora
(390 → 304 ms su 100.000).

Un indice dedicato per evitare l'ordinamento è stato provato e **scartato sulla misura**:
`(planet, item_id, level, option_count)` rende la stessa lettura 3,5 volte più lenta
(16.120 ms contro 4.611), perché scandire una tabella nell'ordine di un indice significa
risalire alla riga una alla volta.

**Verificato da**: `MarketDbTests.GetPriceBaselines_gathers_a_bucket_scattered_through_the_listing`,
che alterna di proposito inserzioni di bucket diversi — i test esistenti le inserivano già
raggruppate, quindi passavano anche senza l'ordinamento; questo, tolto l'`ORDER BY`,
fallisce con `Samples = 1` invece di 3. In più, fuori dalla suite: confronto con
un'implementazione di riferimento indipendente scritta in LINQ su 9 combinazioni
(entrambe le popolazioni, margini 0/20/200, filtro tipo, finestre `--days`) a 500.000 e a
2 milioni di inserzioni, tutte identiche; e output della CLI byte per byte identico fra
build vecchia e nuova su 6 combinazioni di opzioni di `deals`.

**Resta aperto, in prospettiva** (non necessario a chiudere questa voce): il passaggio a
mediana + MAD, che era l'altra occasione in cui riprendere questo punto. L'aggregazione a
flusso non lo ostacola — la MAD di un bucket si calcola con lo stesso buffer, in una
seconda passata sui valori già in mano.

### P2.7 — La finestra `--days` non passa dall'indice ✅ FATTO

**Dove**: [src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs)
(`BaselineQuery`, `CreateSchema`)

Trovato misurando P2.6, non cercandolo. Il filtro della finestra storica era scritto
`($since IS NULL OR last_seen_at_utc >= $since)`, un'unica stringa SQL che valeva sia con
la finestra sia senza. Comoda, ma è una disgiunzione: il planner non può risolverla con un
indice, e `EXPLAIN QUERY PLAN` rispondeva
`SEARCH listings USING INDEX ix_listings_planet_subtype (planet=?)` qualunque fosse
`$since`. `ix_listings_last_seen`, creato in P0.5 anche per questa query, non è mai
entrato in gioco.

**Il piano che questa voce conteneva era sbagliato per metà.** Prevedeva due mosse: rendere
il predicato indicizzabile, e dare al planner delle statistiche (`ANALYZE`) perché
scegliesse l'indice finestra per finestra. La seconda non era mai stata provata, e non
funziona: con `ANALYZE` il planner sceglie `ix_listings_last_seen` a sette giorni **e** a
novanta, cioè anche nel caso che questa stessa voce aveva misurato come peggiore. Il
motivo è strutturale: la libreria che il progetto usa davvero (SQLite 3.49.1 via
Microsoft.Data.Sqlite 10.0.10) non è compilata con `SQLITE_ENABLE_STAT4` — `PRAGMA
compile_options` non riporta alcuna voce `STAT` — quindi `ANALYZE` produce la sola
`sqlite_stat1`, che contiene la media di righe per chiave uguale. Dice quanto è selettiva
un'uguaglianza; non dice nulla su quante righe soddisfino un `>=`, che è esattamente la
domanda da cui dipende la scelta.

Scartato sulla misura anche l'indice composito `(planet, last_seen_at_utc)`: senza
statistiche il planner lo usa pure quando non c'è finestra, e il default passa da 3.549 a
**12.940 ms**, 3,6 volte peggio di prima. È lo stesso effetto per cui P2.6 aveva scartato
`(planet, item_id, level, option_count)` — un indice non coprente costringe a risalire
alla riga una alla volta.

**Fatto**: la clausola `WHERE` si costruisce in C# (`BaselineQuery`) con i soli filtri
davvero richiesti, invece dell'idioma `IS NULL OR`; e un indice **di copertura**
`ix_listings_baseline(planet, last_seen_at_utc, item_id, level, option_count, price,
combat_point, item_sub_type, last_seen_snapshot_id)` porta tutte e sole le colonne che la
query seleziona, così SQLite risponde dall'indice e non tocca mai la tabella. È creato in
`CreateSchema`, quindi acquisito da qualunque database all'apertura senza migrazione
dedicata, come in P0.5.

Banco: 2 milioni di inserzioni sintetiche, un anno di storico, 95% sul pianeta di lavoro;
tempi di lettura completa delle righe, la stessa misura di P2.6. La serie è interna a
questo banco e non va confrontata riga per riga con quella di P2.6, che girava su un
database sintetico diverso.

| Caso | Prima | Il piano di questa voce (`ANALYZE`) | Fatto (indice di copertura) |
|---|---|---|---|
| `--days 7` — 36.493 righe | 1.203 ms | 242 ms | **45 ms** |
| `--days 90` — 468.212 righe | 1.633 ms | 3.251 ms | **573 ms** |
| `--days 0`, il default — 1,9 M righe | 3.549 ms | 3.371 ms | **2.629 ms** |

Migliora anche il percorso `--type`, dove la disgiunzione era la stessa e viene tolta
insieme all'altra: 279 → 15 ms su sette giorni, 719 → 693 ms su tutto lo storico.

**Perché non aspetta più il job di notifica**. La voce era parcheggiata perché il piano di
allora aiutava la sola finestra stretta e peggiorava il resto, quindi conveniva aspettare
che la finestra stretta diventasse l'uso normale. Con l'indice di copertura migliorano
tutti e tre i casi, **default compreso**: una riga d'indice porta nove colonne, una riga di
tabella si trascina dietro anche `stats_json` e `skills_json`. Non c'è più niente da
barattare, e l'ipotesi su cui poggiava l'attesa non serve più che sia vera — il che è un
bene, perché era dubbia: `--days` restringe la popolazione delle **baseline**, non le
inserzioni da giudicare, e un job di notifica vuole offerte fresche ma baseline sul
massimo storico possibile.

**Il prezzo**: il database passa da 257 a 383 MB (+49%), e la prima apertura di un
database esistente si ferma il tempo di costruire l'indice — 6 secondi su 2 milioni di
inserzioni.

**Verificato da**: `MarketDbTests.The_baseline_window_is_answered_from_the_covering_index`,
che esegue `EXPLAIN QUERY PLAN` sulla stringa prodotta da `BaselineQuery` — non su una
copia scritta nel test — e pretende `COVERING INDEX`. Il test è stato controllato per
mutazione: aggiungendo `first_seen_snapshot_id` al `SELECT` il piano scende da
`COVERING INDEX` a `INDEX` e il test fallisce, che è il guasto silenzioso contro cui
esiste. Aggiungere invece `id` non lo fa fallire, ed è corretto così: `id` è il rowid,
presente in ogni voce d'indice, quindi la copertura regge davvero.

**Resta aperto, in prospettiva** (non necessario a chiudere questa voce): due misure non
fatte. Se `ix_listings_planet_subtype` sia ancora ripagato, ora che il nuovo indice serve
la stessa query; e quanto costi in scrittura l'indice a nove colonne su uno `snapshot`,
che inserisce qualche migliaio di righe e non due milioni — me lo aspetto trascurabile, ma
è un'aspettativa, non un dato.

---

## P3 — Dal dato all'avviso

### P3.1 — Le occasioni non arrivavano a nessuno ✅ FATTO

**Dove**: [src/NCMarket.Core/DealAlertService.cs](src/NCMarket.Core/DealAlertService.cs) (nuovo),
[src/NCMarket.Core/DealMessage.cs](src/NCMarket.Core/DealMessage.cs) (nuovo),
[src/NCMarket.Core/INotificationChannel.cs](src/NCMarket.Core/INotificationChannel.cs) (nuovo),
[src/NCMarket.Core/TelegramNotifier.cs](src/NCMarket.Core/TelegramNotifier.cs) (nuovo),
[docker/deals-job](docker/deals-job) (nuovo),
[src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs),
[src/NCMarket.Cli/Program.cs](src/NCMarket.Cli/Program.cs)

Il deploy su server raccoglieva dati che nessuno guardava: `deals` sa dire cosa conviene
comprare, ma lo scrive su una console che sul server non esiste. Era il punto 1 dei
prossimi passi, ed era a costo basso perché P2.4 aveva già tirato fuori `DealService`
dalla CLI.

**Fatto**: `deals --notify` manda in chat le occasioni nuove, `docker/deals-job` ne è la
forma schedulata, `notify-test` verifica il canale. Le decisioni che contano sono tre.

**Una volta sola per inserzione.** È il punto su cui la voce sta o cade. Una ricerca
schedulata non è una persona che guarda: rigira ogni poche ore e ritrova la stessa
occasione finché qualcuno non la compra, quindi otto notifiche al giorno per la stessa
offerta sono notifiche che dopo due giorni non si aprono più. La nuova tabella
`notified_deals` tiene le inserzioni già segnalate; la chiave è `product_id` per lo stesso
motivo per cui lo è in `listings` — non cambia finché l'offerta resta in piedi, e una
rimessa in vendita a un prezzo diverso è un prodotto nuovo, cioè un'offerta nuova, che va
segnalata di nuovo. Non è una foreign key verso `listings`: si notifica anche il mercato
live, che contiene inserzioni mai storicizzate. Anche le occasioni oltre `--top` sono
registrate come segnalate, e il messaggio dichiara quante sono: lasciarle indietro le
farebbe ripresentare a ogni esecuzione senza mai elencarle.

**Le credenziali stanno nell'ambiente** (`NCMARKET_TELEGRAM_TOKEN`,
`NCMARKET_TELEGRAM_CHAT_ID`), non fra le opzioni. Un token di bot è una credenziale al
portatore e un'opzione finisce nella cronologia della shell, nell'elenco dei processi e
nella definizione dello Scheduled Task. Per la stessa ragione il token compare nel
percorso dell'URL ma **mai** in un messaggio d'errore, che è la cosa che si incolla in
chat quando qualcosa non va: gli errori nominano `bot<token>/sendMessage`. Se una delle
due variabili manca, `--notify` fallisce con codice 2 **prima** della ricerca, invece di
scoprirlo dopo i minuti del download.

**Prima si manda, poi si registra.** L'ordine inverso sarebbe più comodo e sbaglia dalla
parte peggiore: un'inserzione marcata come annunciata da un invio poi fallito non verrebbe
segnalata mai più, e un silenzio non è osservabile. Così invece un invio fallito non
registra niente, il comando esce diverso da zero — il job risulta fallito sullo scheduler —
e la prossima esecuzione riprova; il rischio speculare è una notifica doppia.

**Sul nome.** Quello che Telegram chiama *webhook* è la direzione opposta: un indirizzo
che Telegram contatta per consegnare a un bot i messaggi che gli scrivono. Qui non c'è
niente da ricevere, la segnalazione esce, quindi è una `POST` a `sendMessage` — ed è anche
il motivo per cui la macchina che esegue il job non ha bisogno di indirizzo pubblico,
porta in ingresso o certificato. Il canale sta dietro `INotificationChannel`: aggiungere
Discord significa implementarla, senza toccare ciò che decide se e cosa c'è da dire.

**Sul formato.** Il messaggio nasce come testo semplice — niente markup, niente da
sfuggire — e passa a MarkdownV2 quando diventa chiaro che l'alert si legge su un telefono,
in mezzo ad altri messaggi: quattro righe per inserzione, il nome in grassetto, ogni cifra
in un riquadro monospaziato, i filtri su una riga a parte. Il prezzo è l'escaping, e non è
cosmetico: il nome di un item è quello che il gioco gli ha dato, e una parentesi non
sfuggita non arriva storta, viene rifiutata. Da qui `MarkdownV2` (`Escape` e `Code`, gli
unici modi in cui `DealMessage` scrive un valore), l'invariante che nessuna entità
attraversi un a capo — è ciò che rende ancora valido il taglio per righe sopra i 4096
caratteri — e il ripiego del canale: a un `400` di parsing il testo riparte senza
`parse_mode`, perché qualche backslash a vista costa meno di una segnalazione persa che
nessuna esecuzione successiva recupera.

**Il prezzo**: `deals` diventa un comando che scrive, cosa che prima non era. La scrittura
è una `INSERT` per occasione e non prende il lock del database — prenderlo vorrebbe dire
serializzare contro il job di snapshot anche i minuti del download, che avvengono prima —
quindi resta una finestra stretta in cui un `prune` in `VACUUM` può farla fallire dopo che
il messaggio è partito. Costa una notifica doppia, cioè esattamente il modo in cui questo
codice ha già deciso di sbagliare.

**Verificato da**: `DealAlertServiceTests` (5 casi: annuncio unico e silenzio alla seconda
esecuzione, ritentativo dopo un invio fallito, occasioni oltre l'elenco comunque
registrate, ricerca senza risposta, ricerca senza occasioni), `TelegramNotifierTests` (9
casi: destinazione e corpo della richiesta, `parse_mode` dichiarato, ripiego senza markup
su un rifiuto di parsing e nessun ripiego su un rifiuto di altro tipo, rifiuto definitivo
non ritentato e senza token nel messaggio, taglio del messaggio sopra i 4096 caratteri e
invio di tutte le parti, lettura delle credenziali), `DealMessageTests` (6 casi: contenuto,
layout per intero, ripiego sul prezzo senza CP confrontabile, occasioni contate e non
elencate, filtri dichiarati, nome item sfuggito), `MarkdownV2Tests` (3 casi: caratteri
speciali, testo che non ne ha, entità code) e due casi in `MarketDbTests` per la retention
delle segnalazioni e l'idempotenza della registrazione.

**Resta aperto, in prospettiva** (non necessario a chiudere questa voce): non c'è un tetto
al numero di messaggi di una singola esecuzione — l'elenco è limitato da `--top`, ma la
prima esecuzione su uno storico già ricco manda comunque un messaggio lungo, tagliato in
più parti. E la soglia di "interessante" resta quella di `deals` (`--discount`,
`--min-samples`): finché il punto 1 dei prossimi passi non è misurato, è una scelta
ragionata e non una taratura.

---

## P4 — Quanto vale questo pezzo?

Fino a P3.1 il progetto parlava su Telegram in una direzione sola: `TelegramNotifier` fa
`POST` su `sendMessage` e non c'era niente che ricevesse. Un chatbot riceve, ed è quello
il salto architetturale di questa sezione — non la stima, che è una query.

La feature è nata come piano a cinque fasi del 2026-08-25 (`PIANO-VALUTAZIONE.md`,
ripiegato qui alla chiusura dell'ultima), eseguite una per volta: i filtri del servizio, il
motore, il parser, il bot, i bottoni. Si scrive al bot il pezzo come lo si legge
sull'oggetto, a righe libere:

```
Transcendent          rarità (nome o 1-8)
Sword Fire            tipo + elemento
+7                    livello, facoltativo, default +0
ATK 1.404.374         opzioni, 1-4
DEF 3.359.312
skill si              facoltativo, default no
CP 151.216.255        facoltativo
```

e la risposta è l'eco di come è stato letto più l'intervallo di prezzo dei comparabili,
con sotto i bottoni che lo aprono.

### Cosa dicono i dati (misure del 2026-08-25 su 40.408 inserzioni heimdall)

Quattro fatti misurati e non dedotti. Ogni decisione di P4.2 e P4.3 discende da uno di
questi, ed è il motivo per cui la misura è venuta prima del codice.

**1. La terna (tipo, grado, elemento) identifica l'item ai gradi 7 e 8.** Verificato su
tutti e cinque i sottotipi: `Transcendent Sword Fire` è `item_id` 10181000 e nient'altro.
Ai gradi ≤6 la stessa terna copre fino a 3 `item_id` (57 terne su 167), quindi lì il
bucket accorpa varianti diverse dello stesso grado e la risposta deve dirlo. È il motivo
per cui l'elemento è obbligatorio: senza, al grado 8 si mescolano cinque item con prezzi
su scale diverse.

**2. `option_count` non è ricostruibile da ciò che l'utente vede.** Su 15.099 weapon,
4.646 (31%) hanno `option_count` maggiore delle opzioni visibili — il caso tipico è
`option_count = 4` con 2 stat aggiuntive e 1 skill (3.402 righe): due tiri sono caduti
sulla stessa stat e il servizio li restituisce fusi in una riga sola. Conseguenza: la
valutazione **non può usare `BaselineKey`**, che su `OptionCount` è costruita.

**3. La chiave proposta partiziona bene.** Bucketizzando su
`(tipo, grado, elemento, livello, insieme delle stat-opzione, skill sì/no)`: 2.167 bucket,
**94,2% delle inserzioni in un bucket con ≥5 comparabili** (91,3% guardando i soli gradi
≥6). Il livello si può non chiedere: il 92% delle inserzioni è `+0`.

**4. Dentro un bucket, il prezzo non segue né il CP né il valore delle opzioni.**
Correlazione di rango nei bucket più popolati di grado ≥6: da `-0,42` a `+0,03`. Il bucket
dell'esempio — Transcendent Sword, ATK+DEF, con skill — ha 7 comparabili tra 11 e 333 NCG,
mediana 41. **Da qui la risposta a intervallo**: un numero solo su questa dispersione
sarebbe una precisione inventata.

### Il vincolo che nessuna fase risolve

Il database aveva **2 snapshot presi a 3 minuti di distanza**. Nessuna sparizione è
osservabile, quindi la popolazione `sold` non esiste e si valuta sui prezzi *richiesti*.
Le cinque voci costruiscono il meccanismo; la qualità della stima la costruisce
`snapshot-job` girando per settimane. Per questo il bot dichiara su cosa sta rispondendo a
**ogni** messaggio, e non una volta nella documentazione.

### P4.1 — I filtri del market service erano citati, non provati ✅ FATTO

**Dove**: [src/NCMarket.Core/MarketClient.cs](src/NCMarket.Core/MarketClient.cs),
[src/NCMarket.Core/ListingFilter.cs](src/NCMarket.Core/ListingFilter.cs),
[src/NCMarket.Core/IMarketListingSource.cs](src/NCMarket.Core/IMarketListingSource.cs),
[src/NCMarket.Cli/CommandLine.cs](src/NCMarket.Cli/CommandLine.cs),
[src/NCMarket.Cli/Program.cs](src/NCMarket.Cli/Program.cs),
[src/NCMarket.Cli/HelpText.cs](src/NCMarket.Cli/HelpText.cs)

Era il punto 2 dei prossimi passi, e viene per primo perché sblocca la metà della risposta
che è disponibile subito. Il bot ha due domande da soddisfare, con disponibilità opposte:
*quanto lo chiedono adesso?* si risponde dal mercato live dal primo giorno, *quanto vale?*
dallo storico dopo settimane di snapshot. Senza filtro per `itemIds[]` la prima costa la
paginazione di un sottotipo intero — ~60.000 inserzioni per le sole Weapon su Odin — cioè
non è una risposta di chat.

Il primo compito era una **verifica**, non un'implementazione: accertare con una chiamata
reale a `b.9capi.com` la forma che il servizio accetta per i parametri di collezione, che
il README citava dalla documentazione e non da una prova. Utile che sia stata fatta per
prima, perché due presupposti su tre erano sbagliati.

| Parametro | Esito |
|---|---|
| `itemIds` | funziona, **ripetuto una volta per valore** |
| `iconIds` | funziona, stessa forma |
| `isCustom` | funziona, ma non insieme agli id |
| `stat` | **inerte**, non esposto |

- la forma `itemIds[]=1` che il README citava riceve `200` ed è **ignorata**: la risposta è
  l'intero listino con l'aspetto di una risposta filtrata. `itemIds=1,2` invece è un `422`.
  Delle due forme sbagliate, una si nota subito e l'altra mai;
- `stat` non restringe nulla in nessuna forma provata — per nome, per valore numerico di
  `StatType`, sotto `statType` o `stats` — e `stat=PIPPO` riceve `200` col listino intero.
  Non viene esposto: un'opzione che non si applica in silenzio è ciò che P0.4 esiste per
  impedire, quindi `fetch --stat` è un errore e non un filtro;
- `isCustom=true` **sovrascrive** `itemIds` e `iconIds`: chiesti insieme, gli id
  spariscono. `itemIds=10181000` da solo dà l'item 10181000; con `isCustom=true` dà
  20160003 e 20160004. La combinazione viene rifiutata da `ListingFilter.Validate` prima di
  raggiungere la rete. `isCustom=false` con gli id si combina regolarmente.

**Fatto**: `fetch --item-ids`, `--icon-ids`, `--custom true|false`; il filtro viaggia su
ogni pagina della paginazione; l'intestazione di `fetch` dichiara il restringimento
applicato, perché una risposta filtrata che si legge come una intera è lo stesso errore in
cui il servizio cade da sé.

**Verificato da**: `MarketClientTests` — forma esatta della query string col parametro
ripetuto, `isCustom=false` che raggiunge l'URL invece di essere assenza di filtro, id e
custom craft insieme rifiutati senza che parta la richiesta, id e `isCustom=false` come
coppia valida, filtro su ogni pagina. `CommandLineTests` — le nuove opzioni di `fetch`,
`--stat` rifiutata per nome. Provato anche end-to-end sul servizio reale.

### P4.2 — Una chiave fatta di ciò che si legge sull'oggetto ✅ FATTO

**Dove**: [src/NCMarket.Core/ElementalType.cs](src/NCMarket.Core/ElementalType.cs) (nuovo),
[src/NCMarket.Core/Valuation.cs](src/NCMarket.Core/Valuation.cs) (nuovo),
[src/NCMarket.Core/ValuationService.cs](src/NCMarket.Core/ValuationService.cs) (nuovo),
[src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs)

È il valore vero della feature e non dipende da Telegram: si sviluppa e si verifica
interamente con `dotnet test`. `ValuationKey` è `BaselineKey` vista da **fuori** dal
servizio, e ogni differenza discende da chi sta chiedendo: niente `ItemId`, perché chi
scrive al bot non lo conosce (fatto 1); niente `OptionCount`, che non è ricostruibile
(fatto 2), ma l'insieme dei *tipi* di stat delle opzioni, che è ciò che si legge
sull'oggetto; il `Grade`, che in `BaselineKey` è deliberatamente assente perché lì è una
proprietà dell'`item_id` e non partizionerebbe nulla, mentre qui l'`item_id` non c'è. I
*valori* delle opzioni non entrano nella chiave: non predicono il prezzo (fatto 4), e
servono a collocare il pezzo dentro l'intervallo.

Il **custom craft** è nella chiave, ed è un risultato di P4.1: un pezzo da custom craft ha
un item id suo (gamma `2016…`/`2046…`) ma condivide sottotipo, grado ed elemento con quelli
ordinari, e nel database sono **55 le terne che contengono entrambe le popolazioni** — 55
bucket che senza quel campo accorperebbero due item diversi.

Con meno di `min-samples` comparabili (default 5, la soglia di `deals`) il servizio allarga
di un passo e **registra quale**:

| # | Bucket | Come si dichiara |
|---|---|---|
| 0 | chiave esatta | — |
| 1 | senza elemento | *stimato su tutti gli elementi* |
| 2 | livelli accorpati | *livelli diversi accorpati* |
| 3 | stesso numero di opzioni invece dello stesso insieme | *opzioni diverse dalle tue* |
| 4 | solo tipo + grado + skill | *stima larga* |

Oltre il passo 4 si risponde "non ho abbastanza dati", che è una risposta: un intervallo
inventato su due campioni è peggio del silenzio, perché sembra uguale a uno buono.
`ValuationResult` non ha un campo "prezzo stimato", e non averlo nel tipo è ciò che
impedisce a un chiamante di inventarselo.

**Fatto**: `ElementalType` col parser costruito invertendo `GameEnums.ElementalTypeName`;
`ValuationKey` con uguaglianza strutturale scritta a mano — il compilatore confronterebbe
l'insieme delle stat per riferimento, e due chiavi fatte delle stesse opzioni diventerebbero
due bucket senza che niente lo mostri; `MarketDb.GetComparables` con l'indice
`ix_listings_valuation`, stretto perché di filtro e non di copertura (`stats_json` va letto
dalla tabella comunque), e con la SQL che porta solo i predicati effettivamente chiesti,
perché `$p IS NULL OR col = $p` non è sargable e la scala ne toglie uno alla volta. Tre
decisioni che il piano lasciava aperte:

- **il passo 2 accorpa i livelli, non li riporta a +0.** Confrontare un +7 con dei +0 non è
  allargare il bucket, è cambiarlo. Ogni passo toglie un predicato e contiene quello prima:
  è ciò che rende la scala una scala e permette al passo, da solo, di dire cosa è stato
  lasciato cadere;
- **il ripiego su `Listed` avviene prima di allargare, non dopo.** L'ordine inverso è la
  lettura naturale — prima il bucket, poi la popolazione — ed è sbagliato oggi e per un
  pezzo: con due snapshot nessuna inserzione risulta conclusa, quindi ogni domanda
  riceverebbe una *stima larga* mentre il bucket esatto sta lì inutilizzato. Prezzi
  richiesti del pezzo giusto dicono più di vendite di un pezzo all'incirca simile;
- **la chiave nel risultato è il pezzo come è stato descritto**, non il bucket misurato:
  una `ValuationKey` non sa dire "qualunque elemento". A dirlo è il passo, e i due insieme
  nominano il bucket esattamente.

Una **misura ha cambiato la query**, ed è lo stesso genere di errore di P2.7. Con la
finestra temporale scritta per esteso, `EXPLAIN QUERY PLAN` risponde `SEARCH listings USING
INDEX ix_listings_baseline (planet=? AND last_seen_at_utc>?)`: `ix_listings_valuation` non
viene mai usato e la valutazione visita una per una tutte le inserzioni recenti del
pianeta, perché né il grado né l'elemento stanno in quell'indice. Il termine viaggia quindi
come `+last_seen_at_utc >= $since` — il più unario lo tiene fuori dalla scelta dell'indice
senza toccarne il valore — così vincono le cinque uguaglianze e la finestra si applica
sulle poche righe rimaste. Nessun risultato sbagliato lo avrebbe segnalato.

**Verificato da**: `ValuationServiceTests` — bucket esatto sufficiente, i quattro passi
presi uno alla volta solo quando servono e sempre riportati con la loro dichiarazione,
esaurimento della scala che risponde `InsufficientData` senza intervallo, `Sold` misurato
quando i campioni bastano e ripiego su `Listed` dichiarato al passo esatto quando non
bastano, percentile con e senza CP, opzioni/skill/custom craft che tengono fuori dal bucket
una pila di inserzioni care, un altro pianeta che non è un passo della scala.
`ValuationKeyTests` — la stat base che non è un'opzione, la stessa stat tirata due volte che
è una sola, uguaglianza e hash strutturali. `MarketDbTests` — comparabili raccolti per
terna, filtro sulle opzioni dal JSON, finestra temporale, e il piano di query che passa da
`ix_listings_valuation` e non da `ix_listings_baseline`. `ElementalsTests` — nomi, valori
lib9c, e i nomi parsati che sono quelli mostrati.

### P4.3 — Dal messaggio libero alla richiesta ✅ FATTO

**Dove**: [src/NCMarket.Core/ValuationRequestParser.cs](src/NCMarket.Core/ValuationRequestParser.cs) (nuovo),
[src/NCMarket.Core/ValuationMessage.cs](src/NCMarket.Core/ValuationMessage.cs) (nuovo)

L'unica parte della valutazione che legge qualcosa scritto da una persona, e sta in `Core`
senza conoscere Telegram: è una funzione da testo a `ValuationQuery`, quindi ogni messaggio
storto che deve sopravvivere è uno unit test e non una sessione di bot.

**Si classificano i token, non le righe.** L'ordine libero era la richiesta; classificare
per token la soddisfa e in più fa funzionare `Sword Fire` sulla stessa riga e tutto il
pezzo su una riga sola, che è come si scrive da un telefono. Quattro regole sono decisioni
e non meccanica: i separatori delle migliaia si ignorano dentro un numero, perché
`1.404.374` è quello che il gioco mostra e nessuno lo riscrive; gli alias delle stat
derivano da `GameEnums.StatTypeName` letto all'indietro, così una stat aggiunta a lib9c
domani è parsabile oggi; un numero nudo è un errore e non un'ipotesi, perché
`Grades.TryParse` accetta `"8"` e un `8` isolato diventerebbe in silenzio *Transcendent*;
l'elemento mancante è un errore e non un ripiego silenzioso, perché quel ripiego esiste ma
è il passo 1 della scala, che si sceglie col bottone di P4.5 e non per distrazione.

**L'eco dell'interpretazione** è di questa voce, non della presentazione. Su testo libero
una lettura sbagliata non produce un errore visibile: produce la valutazione di un altro
pezzo, giusta in tutto tranne che nel pezzo. Una riga costa quasi niente e rende visibile
ogni errore di lettura — lo stesso principio di P0.4, dove `deals --dicount 30` è un errore
e non un filtro che non si applica.

**Fatto**: `ValuationRequestParser.TryParse(messaggio, pianeta, out query, out errore)`,
nella forma che `CommandLine.TryParse` usa già — un messaggio storto è un errore che nomina
il token, non un'eccezione da catturare nel ciclo di polling — e `ValuationMessage.Echo`.
Sei decisioni che il piano lasciava aperte:

- **il pianeta è un parametro, non un token.** Il messaggio descrive un pezzo, non dove
  cercarlo: il pianeta lo sa la chat, e sull'altro ci si sposta col bottone di P4.5 senza
  riscrivere niente;
- **l'eco chiama il tipo `Weapon`, non `Sword`.** Un'eco che restituisce la parola di chi
  scrive non conferma niente, e `Sword` suggerirebbe per giunta che l'item è stato
  identificato — cioè esattamente ciò che una valutazione senza `item_id` non può fare;
- **i numeri escono nel formato del progetto**, `CP 151,216,255`, non in quello del gioco da
  cui sono entrati. Il parser accetta tutte e tre le grafie perché è il gioco a mostrarne
  una; l'eco ne stampa una sola, la stessa di un alert e di un CSV;
- **zero opzioni è un pezzo, non un errore.** Rifiutarlo per intercettare le righe di
  opzione che non sono arrivate rifiuterebbe anche la domanda legittima: a distinguere i due
  casi è l'eco, che scrive *senza opzioni*;
- **`skill` da solo vale sì, e un sì/no isolato si lega in avanti**, così `con skill` — le
  parole con cui l'eco stessa lo dice — si può rimandare indietro come correzione. Un'eco
  che non si può rimandare indietro è una conversazione a senso unico;
- **il valore di una stat è facoltativo.** Serve a essere consumato — è ciò che impedisce a
  `1.404.374` di ricadere nella regola del numero nudo — ma non entra nella chiave e viene
  buttato: `ATK DEF HIT` descrive lo stesso bucket, e rifiutarlo sarebbe chiedere dati che
  non vengono usati.

**Verificato da**: `ValuationRequestParserTests` — messaggio d'esempio letto campo per
campo, ordine delle righe invertito, tutto su una riga, separatori nelle tre forme, valore
dell'opzione consumato e buttato, alias e sinonimi (compreso uno preso da `GameEnums` e mai
scritto nel parser), le grafie del sì/no, custom craft, stessa stat due volte, pezzo senza
opzioni, `Normal` che riempie il campo ancora libero, elemento assente respinto nominando
gli elementi, numero nudo respinto nominando i tre modi in cui poteva essere scritto, campo
risposto due volte respinto invece che sovrascritto, messaggio vuoto che riceve un esempio.
`ValuationMessageTests` — l'eco per intero, i campi mai scritti che ci sono lo stesso, il
tipo che è `Weapon` e non l'alias, il CP che rientra nella grafia del progetto da tutte e
tre le sue.

### P4.4 — Il verbo `bot` ✅ FATTO

**Dove**: [src/NCMarket.Core/TelegramBot.cs](src/NCMarket.Core/TelegramBot.cs) (nuovo),
[src/NCMarket.Core/TelegramUpdateSource.cs](src/NCMarket.Core/TelegramUpdateSource.cs) (nuovo),
[src/NCMarket.Core/TelegramNotifier.cs](src/NCMarket.Core/TelegramNotifier.cs),
[src/NCMarket.Cli/Program.cs](src/NCMarket.Cli/Program.cs),
[Dockerfile](Dockerfile)

È infrastruttura: nessuna decisione sulla stima, tutte le decisioni su cosa succede quando
il processo sta in piedi per giorni e chiunque può scrivergli. **Long polling, non
webhook**, per la stessa ragione per cui una notifica è una `POST`: nessun indirizzo
pubblico, nessuna porta in ingresso, nessun certificato. Un `409` significa due processi
sullo stesso token — tipicamente un redeploy che ha lasciato vivo il vecchio container — e
va riconosciuto e detto, non ritentato finché uno dei due vince a caso.

**L'allowlist è obbligatoria**, e sostituisce la chat di destinazione invece di
affiancarla: `NCMARKET_TELEGRAM_CHAT_ID` non vuol dire niente per un bot che risponde a chi
scrive, quindi `TelegramOptions.ChatId` è diventato facoltativo e `TelegramNotifier` ha
imparato a scrivere a una chat data — una sola implementazione di `sendMessage`, stesso
spezzettamento, stessi tentativi, stesso ripiego senza `parse_mode`. Nell'altro verso è
obbligatoria: senza `NCMARKET_TELEGRAM_ALLOWED_CHATS` il comando esce con codice 2
all'avvio, perché un bot in ascolto risponde a chiunque ne trovi lo username e ogni
messaggio è una query su SQLite. I messaggi dalle altre chat sono ignorati **in silenzio**;
"non sei autorizzato" conferma che il bot esiste e invita a insistere.

**Fatto**: `ncmarket bot`, `TelegramUpdateSource` e `TelegramBot`. Le decisioni che il
piano lasciava aperte:

- **il database si apre per messaggio e si richiude**, invece che in sola lettura. Le due
  strade che il piano indicava non erano equivalenti: read-only non regge, perché l'offset è
  una scrittura ed è proprio lo stato che il bot deve conservare, e `DbLock` è peggio del
  problema che risolve, perché metterebbe le domande in coda dietro a uno snapshot da trenta
  minuti — una risposta che non arriva è indistinguibile da un bot fermo. Aprire e chiudere
  intorno a ogni messaggio costa millisecondi su una cosa che parte da una persona, lascia
  il `VACUUM` libero, e la domanda che capita proprio lì dentro riceve "riprova fra qualche
  secondo" invece di far cadere il processo;
- **l'offset avanza anche sui messaggi a cui non si risponde**: chat fuori allowlist,
  messaggi oltre il limite di frequenza, aggiornamenti senza testo, e perfino quelli la cui
  gestione è finita in eccezione. Un messaggio letto è un messaggio consumato: l'alternativa
  è che uno solo blocchi la coda per sempre;
- **il limite di frequenza spiega il silenzio una volta per finestra**, poi tace. La
  risposta a troppi messaggi non può essere altri messaggi, ma un silenzio senza causa fa
  riscrivere;
- **il testo della risposta è di questa voce, non della successiva.** "Rispondere con l'eco
  e l'intervallo" non si può fare senza dire l'intervallo: `ValuationMessage.Answer` scrive
  minimo, mediana e massimo, i comparabili, la popolazione, la finestra, il passo di
  allargamento quando c'è e il percentile quando il CP è stato dato.

Il modello di deploy è cambiato, ma **non** nel modo che il piano dava per scontato. Il
piano diceva che il bot "diventa il comando di default dell'immagine"; farlo legherebbe i
job al bot, perché un processo lungo che esce su `409` o su un token rifiutato porterebbe
via il container in cui gli Scheduled Task entrano con `docker exec` — un problema di
credenziali Telegram fermerebbe gli snapshot, che con Telegram non c'entrano niente. Il
default resta `idle`, e la stessa immagine fa due ruoli scelti da `NCMARKET_ROLE`, in due
risorse distinte sullo stesso volume: WAL, `busy_timeout` e il lock su `<database>.lock`
valgono fra processi e quindi anche fra container. Ctrl+C e SIGTERM sono lo stesso ordine —
l'uno di una persona, l'altro di Docker — e in entrambi i casi il ciclo esce dopo aver
scritto l'offset dell'ultimo messaggio risposto.

**Verificato da**: `TelegramBotTests` su un `HttpMessageHandler` finto, come già fa
`TelegramNotifierTests` — pezzo risposto con eco e intervallo, chat fuori allowlist ignorata
senza risposta ma con l'offset avanzato, offset scritto e riletto da un secondo processo che
riparte da lì, `409` riportato senza ritentare e senza il token nel messaggio, limite di
frequenza con un solo avviso, dispatch dei comandi compresa la menzione `/valuta@NomeDelBot`
di un gruppo, errore del parser che diventa un messaggio mentre il ciclo prosegue,
aggiornamento senza testo che avanza l'offset in silenzio, errore di rete ritentato invece
che fatale, allowlist assente che non fa partire il bot. `ValuationMessageTests` — il testo
della risposta per intero, il passo di allargamento dichiarato, il percentile assente senza
CP, i dati insufficienti che dicono quanto hanno trovato e non danno alcun prezzo.
`CommandLineTests` — le opzioni di `bot`, e le credenziali che non sono opzioni.

### P4.5 — Flusso guidato e follow-up ✅ FATTO

**Dove**: [src/NCMarket.Core/InlineKeyboard.cs](src/NCMarket.Core/InlineKeyboard.cs) (nuovo),
[src/NCMarket.Core/ValuationCallback.cs](src/NCMarket.Core/ValuationCallback.cs) (nuovo),
[src/NCMarket.Core/TelegramBot.cs](src/NCMarket.Core/TelegramBot.cs),
[src/NCMarket.Core/TelegramUpdateSource.cs](src/NCMarket.Core/TelegramUpdateSource.cs),
[src/NCMarket.Core/TelegramNotifier.cs](src/NCMarket.Core/TelegramNotifier.cs),
[src/NCMarket.Core/ValuationMessage.cs](src/NCMarket.Core/ValuationMessage.cs),
[src/NCMarket.Core/Valuation.cs](src/NCMarket.Core/Valuation.cs),
[src/NCMarket.Core/ValuationService.cs](src/NCMarket.Core/ValuationService.cs)

Il testo libero è il percorso veloce per chi ha preso la mano. `/valuta` senza argomenti
apre quello guidato: rarità, tipo, elemento e skill coi bottoni — quattro campi su sei senza
possibilità di sbagliarli — e restano da scrivere solo le opzioni, o nemmeno quelle. Sotto
ogni risposta stanno i comparabili su cui l'intervallo è costruito (un `11 – 333 NCG` senza
dettaglio è inutilizzabile; col dettaglio si vede subito che i 333 sono un fuori scala e la
mediana no), la stessa stima senza elemento e l'altro pianeta.

**Fatto**: `InlineKeyboard` porta i bottoni e il loro `reply_markup`, `ValuationCallback`
porta cosa dicono, `TelegramUpdateSource` si è iscritto anche ai `callback_query` — un tipo
di aggiornamento lasciato fuori da `allowed_updates` non è un errore, è silenzio — e
`TelegramNotifier` ha imparato `reply_markup` e `answerCallbackQuery`. Cinque decisioni che
il piano lasciava aperte:

- **lo stato per chat sta in memoria, ma i bottoni no.** Il piano diceva "lo stato per chat
  sta in memoria" e vale per la conversazione, che è una cosa che sta succedendo adesso e che
  un riavvio può legittimamente perdere. Non vale per un bottone: un messaggio resta sul
  telefono per settimane, e un bottone che rispondesse "non me lo ricordo più" sarebbe un
  bottone rotto. Quindi ogni bottone di follow-up **porta con sé la domanda intera** —
  pianeta, chiave, CP e gradino di partenza in una cinquantina di caratteri, dentro i 64 byte
  che Telegram concede a `callback_data` — e il bot non conserva niente per rispondergli.
  Restano fuori campioni minimi, finestra e popolazione: sono configurazione del bot in
  esecuzione, non proprietà del pezzo;
- **"senza elemento" è un campo della richiesta, non un secondo metodo.** `ValuationQuery`
  ha ora `StartStep`, e `ValuationService` salta i gradini sotto quello chiesto invece di
  usare una scala diversa: un allargamento scelto e uno subìto danno lo stesso identico
  risultato, e `ValuationResult.Step` resta l'unica cosa che dichiara dov'è stata misurata
  la stima;
- **il flusso guidato non costruisce una `ValuationKey`**: riscrive ciò che è stato premuto
  come il messaggio che una persona avrebbe scritto e lo dà allo stesso parser. C'è una sola
  lettura di un pezzo in questo progetto, e l'eco che torna indietro è la stessa per entrambe
  le strade. Da qui anche il caso che il piano non nominava: se durante il flusso arriva un
  pezzo scritto per intero, vince il pezzo scritto — rispondere "due rarità" a un messaggio
  perfettamente buono si legge come un bot rotto, non come un bot che aspettava;
- **la conversazione scade** dopo mezz'ora. È l'unica cosa nel bot che cambia il significato
  di un messaggio qualunque, e una conversazione dimenticata la settimana scorsa
  impacchetterebbe il messaggio di oggi dentro il pezzo di allora;
- **il testo passa da `MarkdownV2.Escape` frase per frase**, non più in un colpo solo alla
  fine: l'italiano è pieno di punti e parentesi, e un `\` dimenticato non produce un
  messaggio brutto, produce **nessun** messaggio. Le entità restano dentro la riga, che è ciò
  che permette a `TelegramNotifier.Split` di tagliare un elenco lungo fra due righe qualsiasi
  — la stessa invariante di P3.1.

Una pressione viene confermata (`answerCallbackQuery`) **prima** della query sul database e
prima del limite di frequenza: la conferma non è la risposta, è ciò che dice al telefono che
il bottone è stato sentito, e se fallisce viene registrata e lasciata cadere, perché costa
una rotella che gira e non una valutazione.

**Verificato da**: `TelegramBotTests` — il giro completo del flusso guidato (quattro
pressioni e una riga di opzioni, con le conferme), l'ultimo passo chiuso col bottone
"nessuna opzione", i bottoni di una risposta premuti da un **secondo processo** sullo stesso
database, "senza elemento" che dichiara l'allargamento e poi non si ripropone, l'altro
pianeta che offre la strada di ritorno, un bottone di una versione precedente che viene
nominato, una conversazione persa che lo dice, un pezzo scritto durante il flusso che vince,
un comando che chiude il flusso. `ValuationCallbackTests` — andata e ritorno della domanda
coi suoi campi facoltativi, il pezzo più largo che sta nei 64 byte, dodici forme di dati che
nessuno qui ha scritto e che vengono rifiutate, i due vocabolari che non rispondono l'uno per
l'altro. `ValuationMessageTests` — eco e risposta col loro markup per intero, l'elenco dei
comparabili dal più economico, il taglio oltre venti con quanti ne restano, il bucket vuoto,
e l'invariante che nessuna entità attraversa un a capo. `ValuationServiceTests` — la scala
presa da un gradino più in alto.

### Cosa la v1 non fa

**L'affinamento sul valore delle opzioni.** I dati non lo giustificano (fatto 4) e ci sono
due snapshot: prima si accumula storico, poi si rimisura la correlazione, e solo se compare
si aggiunge un modello. Nel frattempo il percentile è informazione onesta a costo zero —
dice dov'è il pezzo nel gruppo, non quanto vale.

**Il riconoscimento dell'item ai gradi bassi.** Sotto il grado 7 la terna copre più varianti
(fatto 1): la risposta lo dichiara e basta. Chiedere il nome dell'item per un pezzo da 1 NCG
non conviene a nessuno.

**La valutazione da `product_id`.** Un `/valuta <product-id>` per un pezzo già a mercato
salterebbe il parser per intero e risponderebbe alla domanda opposta — *conviene comprarlo?*
— a costo quasi nullo ora che P4.1 e P4.2 ci sono. È un'aggiunta, non un prerequisito: sta
qui perché è la prima cosa da fare dopo, non perché manchi qualcosa alla v1.

**Il rischio che resta aperto** è quello che nessuna di queste voci poteva chiudere: lo
storico corto. Finché `snapshot-job` non ha girato per settimane la stima vale quanto un
listino, e l'unica difesa è che il bot dichiari popolazione, campioni e finestra a ogni
risposta — cosa che fa.

---

## Prossimi passi

| # | Intervento | Costo | Perché in questa posizione |
|---|---|---|---|
| 1 | Taratura di `--sale-margin` su dati reali | basso | La soglia di default (20%) è una scelta ragionata, non una misura: con qualche giorno di snapshot si può verificare come si sposta la composizione della popolazione. Si accumula da sé mentre i job di P3.1 e le domande al bot di P4 girano |
| 2 | `/valuta <product-id>` per un pezzo già a mercato | basso | Salta il parser per intero e risponde alla domanda opposta — *conviene comprarlo?* — riusando P4.1 e P4.2 così come sono |
| 3 | Output `--json` dei comandi di lettura | basso | Resto del vecchio punto 2, di cui P4.1 ha chiuso la metà dei filtri; abilita la dashboard |
| 4 | Rimisura della correlazione fra opzioni e prezzo | basso | È la condizione che il fatto 4 di P4 pone all'affinamento della stima: si rifà quando lo storico è lungo, e solo se compare qualcosa si aggiunge un modello |
| 5 | Mediana + MAD, o vendite on-chain via 9cscan/mimir | medio-alto | I due modi di migliorare ancora la stima, ora che la popolazione è quella giusta |

Con P1.1 chiuso non restano interventi che cambiano la correttezza del motore, con P2.7 il
debito è chiuso, con P3.1 il risultato arriva a destinazione e con P4 si può chiedere il
prezzo di un pezzo che si ha in mano. Quello che resta dipende quasi tutto dal tempo: il
punto 1 e il punto 4 sono misure da fare sui dati veri, e i dati si accumulano da soli
perché `snapshot-job` e `deals-job` girano sul server. Gli altri sono estensioni.
