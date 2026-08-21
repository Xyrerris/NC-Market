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

Legenda priorità:

- **P0** — bug che corrompono i dati o li nascondono; da fare prima di aggiungere feature.
- **P1** — limiti concettuali del motore di valutazione; è qui che sta il valore.
- **P2** — infrastruttura e debito tecnico; abilitano il lavoro successivo.
- **P3** — quello che il motore, una volta corretto, permette di fare.

---

## Stato del repository

| Voce | Stato al 2026-08-12 | Stato al 2026-08-20 |
|---|---|---|
| Branch di lavoro | `feature/docker-deploy`, **10 commit avanti** su `origin/main` | ✅ nessuno in sospeso: `perf/baseline-streaming` (P2.4 + P2.6) è stato unito a `main` |
| `origin/main` | fermo ai commit iniziali | ✅ allineato: contiene tutto il lavoro fino a P2.6 |
| Build locale | **fallisce**: SDK 8.0.204 contro target `net9.0` | ✅ verde (SDK 9.0.317, versione fissata da `global.json`) |
| Test | nessuno | ✅ 61 test xUnit in `tests/NCMarket.Tests`, tutti verdi |
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

Progetto `tests/NCMarket.Tests` (xUnit), 61 test, nessuna dipendenza di rete:

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

**Non ancora coperti** (vedi *Prossimi passi*): `SnapshotCsvExporter.Write`,
`NameProvider.SplitCsvLine`, `EquipmentTypes.TryParse`, `Grades.TryParse`,
`ProductFormat.*`, migrazione v1 → v2, `MarketClient` (richiede un handler HTTP finto).

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
Discord significa implementarla, senza toccare ciò che decide se e cosa c'è da dire. Il
messaggio è testo semplice senza markup, così non c'è niente da sfuggire e il nome di un
item resta quello che il gioco gli ha dato.

**Il prezzo**: `deals` diventa un comando che scrive, cosa che prima non era. La scrittura
è una `INSERT` per occasione e non prende il lock del database — prenderlo vorrebbe dire
serializzare contro il job di snapshot anche i minuti del download, che avvengono prima —
quindi resta una finestra stretta in cui un `prune` in `VACUUM` può farla fallire dopo che
il messaggio è partito. Costa una notifica doppia, cioè esattamente il modo in cui questo
codice ha già deciso di sbagliare.

**Verificato da**: `DealAlertServiceTests` (5 casi: annuncio unico e silenzio alla seconda
esecuzione, ritentativo dopo un invio fallito, occasioni oltre l'elenco comunque
registrate, ricerca senza risposta, ricerca senza occasioni), `TelegramNotifierTests` (6
casi: destinazione e corpo della richiesta, rifiuto definitivo non ritentato e senza token
nel messaggio, taglio del messaggio sopra i 4096 caratteri e invio di tutte le parti,
lettura delle credenziali), `DealMessageTests` (4 casi: contenuto, ripiego sul prezzo
senza CP confrontabile, occasioni contate e non elencate, filtri dichiarati) e due casi in
`MarketDbTests` per la retention delle segnalazioni e l'idempotenza della registrazione.

**Resta aperto, in prospettiva** (non necessario a chiudere questa voce): non c'è un tetto
al numero di messaggi di una singola esecuzione — l'elenco è limitato da `--top`, ma la
prima esecuzione su uno storico già ricco manda comunque un messaggio lungo, tagliato in
più parti. E la soglia di "interessante" resta quella di `deals` (`--discount`,
`--min-samples`): finché il punto 1 dei prossimi passi non è misurato, è una scelta
ragionata e non una taratura.

---

## Prossimi passi

| # | Intervento | Costo | Perché in questa posizione |
|---|---|---|---|
| 1 | Taratura di `--sale-margin` su dati reali | basso | La soglia di default (20%) è una scelta ragionata, non una misura: con qualche giorno di snapshot si può verificare come si sposta la composizione della popolazione. Si accumula da sé mentre il job di notifica di P3.1 gira |
| 2 | Completare la copertura dei test (P2.1, parte residua) | basso | Export CSV e parsing nomi sono ancora senza asserzioni |
| 3 | Filtri avanzati API (`stat`, `itemIds[]`, `isCustom`) + output `--json` | basso | Già in roadmap; il JSON abilita la dashboard |
| 4 | Mediana + MAD, o vendite on-chain via 9cscan/mimir | medio-alto | I due modi di migliorare ancora la stima, ora che la popolazione è quella giusta |

Con P1.1 chiuso non restano interventi che cambiano la correttezza del motore, con P2.7 il
debito è chiuso e con P3.1 il risultato arriva a destinazione: il punto 1 è una misura da
fare sui dati veri — e adesso i dati si accumulano da soli — gli altri sono estensioni.
