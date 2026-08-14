# Backlog interventi — NC-Market

Analisi del 2026-08-12 sul branch `feature/docker-deploy` (commit `c23eeba`).
Documento di lavoro: elenca gli interventi individuati, il motivo per cui esistono e
come verificare che siano chiusi. Il piano di prodotto a lungo termine resta nel
[README](README.md#piano-di-sviluppo); qui c'è il dettaglio operativo.

**Aggiornato il 2026-08-13**: chiusi tutti i P0, più P2.1, P2.2, P2.3 e la maggior parte
di P2.6. Restano aperti P1.1, P1.2, P2.4, P2.5.

Legenda priorità:

- **P0** — bug che corrompono i dati o li nascondono; da fare prima di aggiungere feature.
- **P1** — limiti concettuali del motore di valutazione; è qui che sta il valore.
- **P2** — infrastruttura e debito tecnico; abilitano il lavoro successivo.

---

## Stato del repository

| Voce | Stato al 2026-08-12 | Stato al 2026-08-13 |
|---|---|---|
| Branch di lavoro | `feature/docker-deploy`, **10 commit avanti** su `origin/main` | invariato: il merge su `main` resta da fare |
| `origin/main` | fermo ai commit iniziali | invariato |
| Build locale | **fallisce**: SDK 8.0.204 contro target `net9.0` | ✅ verde (SDK 9.0.317, versione fissata da `global.json`) |
| Test | nessuno | ✅ 35 test xUnit in `tests/NCMarket.Tests` |
| CI | nessuna | ✅ `.github/workflows/ci.yml`: build + test + build dell'immagine Docker |
| File spuri tracciati | `p0.txt`, `p1.txt` | ✅ rimossi dal tracciamento, `.gitignore` esteso |

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

## P1 — Il limite concettuale del motore `deals` (aperto)

### P1.1 — I baseline usano prezzi richiesti, non prezzi di vendita

**Dove**: [src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs) (`GetPriceBaselines`)

È il problema più importante dell'intero progetto. Le mediane sono calcolate su tutte le
inserzioni osservate, incluse quelle che **nessuno ha mai comprato**. Un'inserzione
sovrapprezzata resta nel campione fino al `prune` (365 giorni di default) e alza il
riferimento: `deals` finisce per segnalare come occasione ciò che è soltanto meno assurdo
del resto del listino. Sui mercati illiquidi (gradi 7-8, pochi scambi) l'effetto domina il
risultato.

**Intervento** — è la voce *Rilevazione vendite* della roadmap, ed è la feature a maggior
valore rimasto. I dati necessari sono già nel database (`first_seen_snapshot_id`,
`last_seen_snapshot_id`, `sightings`): un'inserzione sparita fra lo snapshot N e l'N+1 è
stata venduta oppure ritirata. Passi:

1. calcolo delle sparizioni per confronto fra snapshot consecutivi dello stesso pianeta e
   sottotipo — **il prerequisito P0.1 è ora chiuso**: basta confrontare i soli snapshot
   `complete`, quindi l'assenza di un'inserzione non può più essere l'artefatto di una
   cattura interrotta;
2. euristica di classificazione: sparita a prezzo ≤ mediana del bucket → probabile vendita;
   sparita molto sopra la mediana → probabile ritiro;
3. calcolo dei baseline sulle sole inserzioni verosimilmente concluse;
4. (opzionale, successivo) incrocio con le transazioni `BuyProduct` on-chain via 9cscan o
   mimir per sostituire l'euristica con il dato reale.

È il salto da "listino" a "mercato": senza questo, `deals` misura le richieste dei
venditori, non i prezzi.

**Fatto quando**: i baseline sono calcolabili sulle sole inserzioni concluse e `deals`
espone su quale popolazione sta confrontando.

### P1.2 — Bucket dei comparabili troppo grossolano

**Dove**: [src/NCMarket.Core/DealFinder.cs](src/NCMarket.Core/DealFinder.cs),
[src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs)

La chiave di confronto è `(item_id, level)`: ignora `option_count`, tipo elementale e
skill, che muovono il prezzo quanto il livello. Un +0 con 4 opzioni e un +0 con 1 opzione
finiscono nello stesso bucket e si "scontano" a vicenda.

**Intervento**: aggiungere `option_count` alla chiave (costo quasi nullo, effetto
immediato). In prospettiva, sostituire il bucketing con un modello di prezzo su
`(item_id, level, option_count, grade)` normalizzato per CP, con statistiche robuste
(mediana + MAD invece della sola mediana) e soglia minima di campioni per bucket.

**Fatto quando**: due inserzioni dello stesso item e livello ma con numero di opzioni
diverso non si confrontano fra loro.

---

## P2 — Infrastruttura e debito tecnico

### P2.1 — Nessun test ✅ FATTO

Progetto `tests/NCMarket.Tests` (xUnit), 35 test, nessuna dipendenza di rete:

- `MarketDbTests` — stato degli snapshot, `GetLatestSnapshotId`, deduplicazione di
  `AddProducts`, mediane e finestra `--days` di `GetPriceBaselines`, `Prune` con e senza
  `--dry-run`, uso effettivo dell'indice via `EXPLAIN QUERY PLAN`;
- `MarketDbMigrationTests` — un database v2 costruito a mano viene migrato a v3
  conservando i dati e classificando correttamente snapshot completi e parziali;
- `DealFinderTests` — soglie, campioni minimi, metrica CP contro metrica prezzo,
  ordinamento;
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

### P2.4 — `Program.cs` monolitico (aperto, ridotto)

Il parsing degli argomenti è uscito da `Program.cs` ed è finito in `CommandLine.cs`.
Resta il resto: orchestrazione (`SnapshotAsync`, `DealsAsync`) e presentazione a schermo
mescolate nello stesso file. Finché la CLI è l'unico consumatore è sostenibile; nel
momento in cui la roadmap introduce dashboard o servizio schedulato, l'orchestrazione va
spostata in `NCMarket.Core` come servizi riusabili. Da fare **prima** del punto
Reportistica della roadmap, non dopo.

### P2.5 — Manca il LICENSE (aperto, richiede una decisione)

Repository pubblico senza file di licenza. La scelta è del proprietario del repository e,
una volta pubblicata, la concessione non è di fatto revocabile per le versioni già
rilasciate: per questo non è stata fatta d'ufficio. Per un progetto personale su GitHub
MIT è la scelta usuale; Apache-2.0 aggiunge una concessione esplicita di brevetto.

### P2.6 — Dettagli minori

- ✅ `FinalizeSnapshot` usa ora i parametri come il resto della classe.
- ✅ `GetSnapshot(id)` interroga direttamente per id invece di caricare tutti gli snapshot
  e filtrare in memoria.
- ✅ `MarketClient` imposta uno `User-Agent` che identifica il progetto e attende 250 ms
  fra una pagina e la successiva.
- ⬜ `GetPriceBaselines` carica ancora tutte le righe in memoria per fare il bucketing in
  C#. Accettabile oggi; da rivedere quando lo storico crescerà (l'alternativa è aggregare
  in SQL). Da affrontare insieme a P1.2, che tocca comunque quella query.

---

## Prossimi passi

| # | Intervento | Costo | Perché in questa posizione |
|---|---|---|---|
| 1 | Merge del branch su `main` | minimo | Allinea il repository e attiva la CI sul ramo principale |
| 2 | Scelta del LICENSE (P2.5) | minimo | Serve una decisione, non del lavoro |
| 3 | **P1.1** (rilevazione vendite) + P1.2 | medio-alto | Rende `deals` effettivamente affidabile; i prerequisiti sono chiusi |
| 4 | Completare la copertura dei test (P2.1, parte residua) | basso | Export CSV e parsing nomi sono ancora senza asserzioni |
| 5 | Notifica occasioni (webhook Telegram/Discord) dal job | basso | È il payoff del deploy su server: non si leggono CSV, si viene avvisati |
| 6 | Filtri avanzati API (`stat`, `itemIds[]`, `isCustom`) + output `--json` | basso | Già in roadmap; il JSON abilita la dashboard |
| 7 | P2.4 (estrarre l'orchestrazione in Core) | medio | Prima di dashboard o servizio schedulato, non dopo |

Il punto 3 è quello che cambia il valore dello strumento.
