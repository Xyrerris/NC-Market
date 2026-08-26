# Piano — Valutazione di un prodotto dal bot Telegram

Piano di lavoro del 2026-08-25 su `main` (commit `20284d8`). Descrive la feature
"quanto vale questo pezzo?" chiesta al bot Telegram, divisa in cinque fasi eseguibili
una alla volta. Il formato segue [BACKLOG.md](BACKLOG.md): per ogni fase *dove* si
interviene, *cosa* si decide e *cosa* la verifica.

Quando le fasi saranno chiuse, il loro contenuto va ripiegato in `BACKLOG.md` come
sezione P4, com'è successo a P3.1.

---

## Il punto di partenza

Il progetto oggi parla su Telegram in una sola direzione: `TelegramNotifier` fa `POST`
su `sendMessage` e non c'è niente che riceva. Un chatbot riceve, e questo è il salto
architetturale della feature — non la stima, che è una query.

L'input concordato è un messaggio a righe libere:

```
Transcendent          rarità (nome o 1-8)
Sword Fire            tipo + elemento
+7                    livello, facoltativo, default +0
ATK 1.404.374         opzioni, 1-4
DEF 3.359.312
HIT 5.734.266
Skill si              facoltativo, default no
CP 151.216.255        facoltativo
```

Ordine delle righe libero, elemento obbligatorio, livello e CP facoltativi.

### Cosa dicono i dati (misure del 2026-08-25 su 40.408 inserzioni heimdall)

Quattro fatti che il piano dà per acquisiti, perché sono stati misurati e non dedotti.

**1. La terna (tipo, grado, elemento) identifica l'item ai gradi 7 e 8.** Verificato su
tutti e cinque i sottotipi: `Transcendent Sword Fire` è `item_id` 10181000 e nient'altro.
Ai gradi ≤6 la stessa terna copre fino a 3 `item_id` (57 terne su 167), quindi lì il
bucket accorpa varianti diverse dello stesso grado e la risposta deve dirlo. È il motivo
per cui l'elemento è obbligatorio: senza, al grado 8 si mescolano cinque item con prezzi
su scale diverse.

**2. `option_count` non è ricostruibile da ciò che l'utente vede.** Su 15.099 weapon,
4.646 (31%) hanno `option_count` maggiore delle opzioni visibili — il caso tipico è
`option_count = 4` con 2 stat aggiuntive e 1 skill (3.402 righe): due tiri sono caduti
sulla stessa stat e il servizio li restituisce fusi in una riga sola. **Conseguenza:
la valutazione non può usare `BaselineKey`**, che su `OptionCount` è costruita. Serve una
chiave propria, fatta di ciò che si legge sull'oggetto.

**3. La chiave proposta partiziona bene.** Bucketizzando su
`(tipo, grado, elemento, livello, insieme delle stat-opzione, skill sì/no)`: 2.167 bucket,
**94,2% delle inserzioni in un bucket con ≥5 comparabili** (91,3% guardando i soli gradi
≥6). Il livello si può non chiedere: il 92% delle inserzioni è `+0`.

**4. Dentro un bucket, il prezzo non segue né il CP né il valore delle opzioni.**
Correlazione di rango nei bucket più popolati di grado ≥6: da `-0,42` a `+0,03`. Il
bucket dell'esempio — Transcendent Sword, ATK+DEF, con skill — ha 7 comparabili tra 11 e
333 NCG, mediana 41. **Da qui la risposta a intervallo**: un numero singolo su questa
dispersione sarebbe una precisione inventata.

### Il vincolo che nessuna fase risolve

Il database ha **2 snapshot presi a 3 minuti di distanza**. Nessuna sparizione è
osservabile, quindi la popolazione `sold` non esiste e oggi si valuterebbe sui prezzi
*richiesti*. Le cinque fasi costruiscono il meccanismo; la qualità della stima la
costruisce `snapshot-job` girando per settimane. Il bot deve dichiarare su cosa sta
rispondendo a ogni messaggio, non una volta nella documentazione.

---

## F1 — Filtri avanzati sul market service ✅ FATTO

**Dove**: [src/NCMarket.Core/MarketClient.cs](src/NCMarket.Core/MarketClient.cs),
[src/NCMarket.Core/IMarketListingSource.cs](src/NCMarket.Core/IMarketListingSource.cs),
[src/NCMarket.Cli/CommandLine.cs](src/NCMarket.Cli/CommandLine.cs),
[src/NCMarket.Cli/Program.cs](src/NCMarket.Cli/Program.cs),
[src/NCMarket.Cli/HelpText.cs](src/NCMarket.Cli/HelpText.cs), README

È il punto 2 dei *Prossimi passi* del backlog, e viene per primo perché sblocca la metà
della risposta che è disponibile subito. Il bot ha due domande da soddisfare, con
disponibilità opposte:

| Domanda | Fonte | Disponibile |
|---|---|---|
| *Quanto lo chiedono adesso?* | mercato live | dal primo giorno |
| *Quanto vale?* | storico, mediane sulle vendite stimate | dopo settimane di snapshot |

Senza filtro per `itemIds[]` la prima domanda costa la paginazione di un sottotipo
intero — ~60.000 inserzioni per le sole Weapon su Odin — cioè non è una risposta di chat.

**Primo compito, ed è una verifica, non un'implementazione**: accertare la forma esatta
che il servizio accetta per i parametri di collezione (`itemIds[]`, `stat`, `isCustom`)
con una chiamata reale a `b.9capi.com`. Il README li cita dalla documentazione del
servizio, non da una prova.

**Esito della verifica** (2026-08-25, `b.9capi.com`, sottotipo weapon). Utile che sia
stata fatta per prima: due dei tre presupposti erano sbagliati.

| Parametro | Esito |
|---|---|
| `itemIds` | funziona, **ripetuto una volta per valore** |
| `iconIds` | funziona, stessa forma |
| `isCustom` | funziona, ma non insieme agli id (sotto) |
| `stat` | **inerte**, non esposto |

- la forma `itemIds[]=1` che il README citava riceve `200` ed è **ignorata**: la risposta
  è l'intero listino con l'aspetto di una risposta filtrata. `itemIds=1,2` invece è un
  `422`. Delle due forme sbagliate, una si nota subito e l'altra mai;
- `stat` non restringe nulla in nessuna forma provata — per nome, per valore numerico di
  `StatType`, sotto `statType` o `stats` — e `stat=PIPPO` riceve `200` col listino
  intero. Non viene esposto: un'opzione che non si applica in silenzio è ciò che P0.4
  esiste per impedire, quindi `fetch --stat` è un errore, non un filtro;
- `isCustom=true` **sovrascrive** `itemIds` e `iconIds`: chiesti insieme, gli id
  spariscono. `itemIds=10181000` da solo dà l'item 10181000; con `isCustom=true` dà
  20160003 e 20160004. La combinazione viene rifiutata da `ListingFilter.Validate` prima
  di raggiungere la rete. `isCustom=false` con gli id si combina regolarmente.

**Fatto**: `fetch --item-ids`, `--icon-ids`, `--custom true|false`; il filtro viaggia su
ogni pagina della paginazione; l'intestazione di `fetch` dichiara il restringimento
applicato, perché una risposta filtrata che si legge come una intera è lo stesso errore
in cui il servizio cade da sé.

**Verificato da**: `MarketClientTests` — forma esatta della query string col parametro
ripetuto, `isCustom=false` che raggiunge l'URL invece di essere assenza di filtro, id e
custom craft insieme rifiutati senza che parta la richiesta, id e `isCustom=false` come
coppia valida, filtro su ogni pagina della paginazione. `CommandLineTests` — le nuove
opzioni accettate da `fetch`, `--stat` rifiutata per nome. Provato anche end-to-end sul
servizio reale: risposta filtrata, e codice 2 sia per un id non numerico sia per la
combinazione impossibile.

---

## F2 — `ValuationKey` e `ValuationService`

**Dove**: `src/NCMarket.Core/ElementalType.cs` (nuovo),
`src/NCMarket.Core/Valuation.cs` (nuovo),
`src/NCMarket.Core/ValuationService.cs` (nuovo),
[src/NCMarket.Core/MarketDb.cs](src/NCMarket.Core/MarketDb.cs)

È il valore vero della feature e non dipende da Telegram: si sviluppa e si verifica
interamente con `dotnet test`.

### `ElementalType`

Enum e parser sul modello di [Grade.cs](src/NCMarket.Core/Grade.cs) e
[EquipmentType.cs](src/NCMarket.Core/EquipmentType.cs), che sono già la forma che questo
progetto dà a "valore di lib9c più `TryParse`":

```
Normal = 0, Fire = 1, Water = 2, Land = 3, Wind = 4
```

`Elementals.TryParse` accetta il nome o il numero. La resa a schermo resta
`GameEnums.ElementalTypeName`, che è già l'unica sorgente dei nomi: il parser aggiunge il
verso opposto, non un secondo elenco.

### `ValuationKey`

```
ValuationKey(EquipmentType Type, int Grade, ElementalType Element,
             int Level, ImmutableSortedSet<int> OptionStats, bool HasSkill)
```

Le differenze da `BaselineKey`, e il perché di ciascuna:

- **niente `ItemId`**: chi scrive al bot non lo conosce. Ai gradi 7-8 la terna
  (tipo, grado, elemento) lo determina, sotto lo restringe a poche varianti;
- **niente `OptionCount`**: non è ricostruibile (fatto 2). Al suo posto l'insieme dei
  *tipi* di stat delle opzioni, che è esattamente ciò che si legge sull'oggetto;
- **`Grade` c'è**, mentre in `BaselineKey` è deliberatamente assente perché lì è una
  proprietà dell'`item_id` e non partizionerebbe nulla. Qui l'`item_id` non c'è, quindi
  il grado torna a essere informazione.

I *valori* delle opzioni non entrano nella chiave. Non predicono il prezzo (fatto 4):
servono a collocare il pezzo dentro l'intervallo, e la risposta lo dice come
collocazione, non come stima.

**Il custom craft va aggiunto alla chiave**, ed è un risultato di F1: un pezzo da custom
craft ha un item id suo (gamma `2016…`/`2046…`), ma condivide sottotipo, grado ed
elemento con quelli ordinari. Nel database sono **55 le terne (tipo, grado, elemento) che
contengono entrambe le popolazioni**, cioè 55 bucket che senza questo campo
accorperebbero due item diversi. Il custom craft si ferma al grado 6 — ai gradi 7 e 8 non
ce n'è — quindi il caso principale non è toccato, ma i gradi bassi sì. Va chiesto
nell'input, come campo facoltativo con default "no": chi ha fatto custom craft lo sa.

### `MarketDb.GetComparables`

Filtri SQL: `planet`, `item_sub_type`, `grade`, `elemental_type`, `level`, più la
finestra `last_seen_at_utc`. Le stat delle opzioni e la skill si filtrano in memoria
deserializzando `stats_json` e `skills_json` sulle sole righe sopravvissute — il bucket è
dell'ordine delle decine, e nessun indice può entrare in un JSON.

Due vincoli ereditati da P2.7, che non vanno riscoperti:

- **la SQL porta solo i filtri effettivamente chiesti**. `$p IS NULL OR col = $p` non è
  sargable e costa l'indice: la query si costruisce come fa `BaselineQuery`, aggiungendo
  i predicati che ci sono. Serve perché la scala di allargamento (sotto) toglie predicati
  un passo alla volta;
- **indice nuovo** `ix_listings_valuation(planet, item_sub_type, grade, elemental_type,
  level)`. Non può coprire la query — `stats_json` va letto dalla tabella comunque —
  quindi è un indice di filtro, stretto: cinque colonne piccole, non il raddoppio che è
  costato `ix_listings_baseline`. Creato `IF NOT EXISTS` come gli altri, nessuna
  migrazione.

La popolazione riusa quella che c'è: `BaselinePopulation`, `ListingOutcomes` e la
classifica vendita/ritiro di `GetPriceBaselines`. La frontiera di copertura
(`GetCoverageFrontier`) va resa condivisibile tra i due percorsi, perché due copie della
stessa euristica divergono. Preferenza a `Sold`, ripiego dichiarato su `Listed` quando i
campioni conclusi non bastano.

### La scala di allargamento

Con meno di `min-samples` comparabili (default 5, la soglia di `deals`), il servizio
allarga di un passo e **registra quale**:

| # | Bucket | Come si dichiara |
|---|---|---|
| 0 | chiave esatta | — |
| 1 | senza elemento | *stimato su tutti gli elementi* |
| 2 | livello riportato a +0 | *livelli diversi accorpati* |
| 3 | stesso numero di opzioni invece dello stesso insieme | *opzioni diverse dalle tue* |
| 4 | solo tipo + grado + skill | *stima larga* |

Oltre il passo 4 si risponde "non ho abbastanza dati", che è una risposta. Un intervallo
inventato su due campioni è peggio del silenzio, perché sembra uguale a uno buono.

### `ValuationResult`

Porta: chiave usata, passo di allargamento raggiunto, numero di comparabili, popolazione
e `ListingOutcomes`, i cinque numeri della distribuzione (min, p25, mediana, p75, max),
la finestra temporale, e — se il CP è stato fornito — il percentile del pezzo tra i
comparabili. Nessun campo "prezzo stimato": non esiste un numero solo, e non averlo nel
tipo impedisce a un chiamante di inventarselo.

**Fatto quando**: dato un `ValuationQuery`, il servizio restituisce l'intervallo e il
passo di allargamento, oppure dichiara che i dati non bastano.

**Verificato da**: `ValuationServiceTests` — bucket esatto sufficiente, ogni passo della
scala preso solo quando serve e sempre riportato, esaurimento della scala, ripiego da
`Sold` a `Listed` dichiarato, percentile con e senza CP, insieme di stat che distingue
due bucket, skill che distingue due bucket. `MarketDbTests` — comparabili raccolti per
terna, filtro sulle opzioni dal JSON, uso del nuovo indice nel piano di query (come già
fa il test su `ix_listings_baseline`), finestra temporale.

---

## F3 — Parser del messaggio

**Dove**: `src/NCMarket.Core/ValuationRequestParser.cs` (nuovo),
`src/NCMarket.Core/ValuationMessage.cs` (nuovo)

Sta in `Core` e non conosce Telegram: è una funzione da testo a `ValuationQuery`, quindi
si verifica per casi storti senza far girare un bot.

**Si classificano i token, non le righe.** L'ordine libero era la richiesta; classificare
per token la soddisfa e in più fa funzionare `Sword Fire` sulla stessa riga e
`Transcendent Sword Fire +7` su una riga sola. Un alias di stat consuma il numero che
segue, `CP` idem, `skill` consuma il sì/no che segue, `+N` sta da solo.

Regole che vale la pena scrivere:

- **i separatori delle migliaia si ignorano dentro un numero**: `1.404.374`, `1,404,374`
  e `1404374` sono lo stesso valore, perché è quello che il gioco mostra e nessuno lo
  riscrive a mano;
- **gli alias delle stat derivano da `GameEnums.StatTypeName`**, che resta la sorgente
  unica: il parser ne costruisce la mappa inversa e vi aggiunge i sinonimi
  (`ATTACK`/`ATTACCO` per `ATK`, `SPEED` per `SPD`, `CRIT` per `CRI`). Aggiungere una
  stat a lib9c non lascia il parser indietro;
- **un numero nudo è un errore**, non un'ipotesi. `Grades.TryParse` accetta `"8"`, quindi
  un `8` isolato diventerebbe in silenzio *Transcendent*: si risponde nominando il token
  e chiedendo cosa fosse;
- **elemento mancante è un errore**, non un ripiego silenzioso su "tutti gli elementi".
  Il ripiego esiste, ma è il passo 1 della scala e si sceglie con un bottone (F5), non
  per distrazione.

**L'eco dell'interpretazione** è parte di questa fase, non della presentazione:

```
Ho letto: Transcendent Sword · Fire · +7 · opzioni ATK, DEF, HIT · con skill · CP 151.216.255
```

È la difesa che vale di più contro un parser su testo libero: costa una riga e rende
visibile ogni errore di lettura, invece di lasciarlo diventare una stima sbagliata che
sembra giusta. È lo stesso principio di P0.4 — `deals --dicount 30` è un errore, non un
filtro che non si applica.

Gli errori nominano il token e dicono cosa manca. "Non ho capito" non è un messaggio
d'errore, è una scrollata di spalle.

**Fatto quando**: il parser legge il messaggio d'esempio in qualunque ordine di righe e
produce l'eco.

**Verificato da**: `ValuationRequestParserTests` — ordine invertito, tutto su una riga,
separatori nelle tre forme, alias e sinonimi, `si`/`sì`/`yes`/`no`, skill assente = no,
livello assente = +0, elemento assente respinto, numero nudo respinto, stat sconosciuta
respinta nominandola, più di quattro opzioni respinte, testo dell'eco per intero.

---

## F4 — Il verbo `bot`

**Dove**: `src/NCMarket.Core/TelegramBot.cs` (nuovo),
`src/NCMarket.Core/TelegramUpdateSource.cs` (nuovo),
[src/NCMarket.Core/TelegramNotifier.cs](src/NCMarket.Core/TelegramNotifier.cs),
[src/NCMarket.Cli/CommandLine.cs](src/NCMarket.Cli/CommandLine.cs),
[src/NCMarket.Cli/Program.cs](src/NCMarket.Cli/Program.cs),
[Dockerfile](Dockerfile), README

È infrastruttura: nessuna decisione sulla stima, tutte le decisioni su cosa succede
quando il processo sta in piedi per giorni e chiunque può scrivergli.

**Long polling, non webhook.** `getUpdates` con `timeout` lungo, offset persistito. La
scelta è la stessa già documentata per le notifiche e per la stessa ragione: nessun
indirizzo pubblico, nessuna porta in ingresso, nessun certificato. Con un webhook il
container avrebbe bisogno di tutti e tre.

**Un solo poller per token.** Due processi sullo stesso token si prendono un `409
Conflict` da Telegram a vicenda. Un redeploy che lascia vivo il vecchio container è
esattamente questo caso: il `409` va riconosciuto e detto ("un'altra istanza sta già
leggendo"), non ritentato in silenzio finché uno dei due vince.

**L'offset va persistito**, altrimenti un riavvio rilegge o perde messaggi. Sta nel
database, accanto al resto dello stato.

**Allowlist obbligatoria.** `NCMARKET_TELEGRAM_CHAT_ID` è la chat *a cui* si notifica; un
bot in ascolto risponde a chiunque ne trovi lo username, e ogni messaggio è una query su
SQLite. Serve una variabile separata — `NCMARKET_TELEGRAM_ALLOWED_CHATS`, elenco di id —
e il silenzio per tutto il resto: rispondere "non sei autorizzato" a uno sconosciuto
conferma che il bot esiste e invita a insistere. Più un limite di messaggi per chat al
minuto. Se la variabile manca, `bot` fallisce all'avvio con codice 2, come già fa
`--notify` senza credenziali: un bot aperto a Internet per distrazione è il modo peggiore
di scoprire questa riga.

**Il database si apre in sola lettura.** Il bot tiene una connessione aperta per giorni,
mentre `snapshot` e `prune` scrivono. In WAL un lettore non blocca uno scrittore, ma il
`VACUUM` di `prune` sì: il bot va aperto read-only e deve mollare la connessione tra un
messaggio e l'altro, oppure rispettare `DbLock`. Altrimenti il `prune` settimanale
fallisce, e lo fa in un momento che non ha niente a che vedere con la causa.

**Il modello di deploy cambia.** Oggi il container è `idle` più Scheduled Task; un bot è
un processo lungo, quindi diventa il comando di default dell'immagine. Gli Scheduled Task
di snapshot e deals restano dove sono.

**Fatto quando**: il bot risponde con l'eco e l'intervallo a un messaggio da una chat
autorizzata, ignora le altre, e sopravvive a un riavvio senza rileggere la coda.

**Verificato da**: `TelegramBotTests` su un `HttpMessageHandler` finto, come già fa
`TelegramNotifierTests` — chat fuori allowlist ignorata senza risposta, offset avanzato e
riletto dopo un riavvio simulato, `409` riportato e non ritentato, limite di frequenza,
dispatch dei comandi, errore del parser che diventa un messaggio e non un'eccezione che
ferma il polling.

---

## F5 — Flusso guidato e follow-up

**Dove**: `src/NCMarket.Core/TelegramBot.cs`, `src/NCMarket.Core/ValuationMessage.cs`

Il testo libero è il percorso veloce per chi ha preso la mano. `/valuta` senza argomenti
apre quello guidato: **inline keyboard** per i campi enumerabili — 8 rarità, 5 tipi, 5
elementi, skill sì/no — che sono quattro campi su sei senza possibilità di sbagliare.
Restano da digitare solo le opzioni e, volendo, il CP.

I bottoni sotto la risposta valgono quanto la risposta:

- **Vedi i comparabili** — elenca le inserzioni su cui l'intervallo è costruito. Un
  `11 – 333 NCG` senza dettaglio è inutilizzabile; col dettaglio si vede subito che i 333
  sono un fuori scala e la mediana no;
- **Senza elemento** / **Su Odin** — riesegue con un passo di allargamento o sull'altro
  pianeta, invece di far riscrivere il messaggio da capo.

Lo stato per chat sta in memoria: un riavvio perde le conversazioni a metà, ed è
accettabile — costa un `/valuta` ripetuto. Va scritto, non lasciato scoprire.

Forma della risposta:

```
🏷️ Transcendent Sword · Fire · +0 · ATK/DEF · con skill

💰 11 – 333 NCG   mediana 41 NCG
📊 7 comparabili · prezzi richiesti · heimdall · snapshot del 24/08
📈 Le tue opzioni stanno nel 60° percentile del gruppo
⚠️  Poche vendite osservate: è quanto si chiede, non quanto si paga
```

Vale MarkdownV2 con le regole già in casa: ogni valore passa da `MarkdownV2.Escape` o
`MarkdownV2.Code`, nessuna entità attraversa un a capo, e un `400` di parsing fa
ripartire il testo senza `parse_mode`.

**Fatto quando**: `/valuta` porta a una stima senza che l'utente scriva altro che i
numeri delle opzioni.

**Verificato da**: casi in `TelegramBotTests` sul giro dei callback e in
`ValuationMessageTests` sul layout per intero, sul ripiego senza CP, sulla dichiarazione
del passo di allargamento e sull'escaping del nome dell'item.

---

## Cosa la v1 non fa

**L'affinamento sul valore delle opzioni.** I dati non lo giustificano (fatto 4) e ci
sono due snapshot: prima si accumula storico, poi si rimisura la correlazione, e solo se
compare si aggiunge un modello. Nel frattempo il percentile è informazione onesta a costo
zero — dice dov'è il pezzo nel gruppo, non quanto vale.

**Il riconoscimento dell'item ai gradi bassi.** Sotto il grado 7 la terna copre più
varianti: la risposta lo dichiara e basta. Chiedere il nome dell'item per un pezzo da
1 NCG non conviene a nessuno.

**La valutazione da `product_id`.** Un `/valuta <product-id>` per un pezzo già a mercato
salterebbe il parser per intero e risponderebbe alla domanda opposta — *conviene
comprarlo?* — a costo quasi nullo una volta che F1 e F2 ci sono. È un'aggiunta, non un
prerequisito: sta qui perché è la prima cosa da fare dopo, non perché manchi qualcosa
alla v1.

## Rischi

| Rischio | Dove morde | Cosa lo contiene |
|---|---|---|
| ~~I filtri `itemIds[]`/`stat` non sono utilizzabili~~ | ~~F1~~ | Chiuso: `itemIds` funziona ed è ciò che serve alla risposta live; `stat` non funziona e non viene esposto |
| Lo storico resta troppo corto | La stima vale quanto un listino | Il bot dichiara popolazione, campioni e finestra a ogni risposta |
| Il bot tiene il database aperto | `prune` fallisce sul `VACUUM` | Connessione read-only e rilascio tra i messaggi (F4) |
| Il testo libero resta fragile | Stime sbagliate che sembrano giuste | Eco dell'interpretazione (F3) e flusso guidato (F5) |
