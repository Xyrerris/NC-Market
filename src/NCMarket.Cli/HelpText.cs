namespace NCMarket.Cli;

/// <summary>
/// The manual the CLI prints for <c>help</c>. It is the only documentation most users of
/// a command-line tool read, so it lives on its own instead of pushing the commands apart
/// in <c>Program</c>; the option lists it describes are the ones enforced by
/// <see cref="CommandLine"/>, which is where a new option has to be declared too.
/// </summary>
internal static class HelpText
{
    public static void Print() => Console.WriteLine("""
        NC-Market — prezzi degli equipaggiamenti dal mercato di Nine Chronicles

        Uso: ncmarket <comando> [opzioni]

        Comandi:
          fetch      Interroga il mercato live (non salva nulla)
                       --type weapon|armor|belt|necklace|ring   (obbligatorio; 'sword' = weapon)
                       --order <ordine>      default: unit_price
                                             (price, price_desc, cp, cp_desc, grade, grade_desc,
                                              level, level_desc, unit_price, unit_price_desc,
                                              opt_count, opt_count_desc, crystal, crystal_desc,
                                              crystal_per_price, crystal_per_price_desc)
                       --limit <n>           default: 20
                       --offset <n>          default: 0
                       --details             scheda completa per inserzione: statistiche
                                             (ATK, HP, DEF, ...) base e bonus, skill con
                                             probabilità/potenza, cristalli, venditore

          snapshot   Scarica e storicizza il listino nel database SQLite
                       --types w,a,...       default: tutti e cinque i tipi
                       --max-per-type <n>    limite prodotti per tipo (default: tutti)

          snapshots  Elenca gli snapshot salvati, con lo stato di ciascuno: gli snapshot
                     'PARZIALE' sono catture interrotte a metà e non vengono usati come
                     ultimo snapshot da stats, deals ed export
                       --planet <pianeta>    filtro opzionale (default: tutti i pianeti)

          history    Storico prezzi di un item attraverso gli snapshot
                       --item <itemId>       (obbligatorio, es. 10152001)

          stats      Statistiche per item sull'ultimo snapshot
                       --type <tipo>         filtro opzionale
                       --top <n>             default: 30

          deals      Occasioni: inserzioni correnti sotto la mediana storica del
                     database (metrica primaria: NCG per punto CP, per item+livello+opzioni)
                       --type <tipo>         filtro opzionale (default: tutti i tipi)
                       --grade <g[,g...]>    filtro rarità: 1-8 o normal, rare, epic,
                                             unique, legendary, divinity, mythic,
                                             transcendent (default: tutte)
                       --discount <pct>      sconto minimo percentuale (0-100), default: 25
                       --min-samples <n>     inserzioni storiche minime per confronto, default: 5
                       --days <n>            finestra storica in giorni (default: tutto lo storico)
                       --baseline sold|listed
                                             popolazione su cui si calcolano le mediane
                                             storiche. 'sold' (default) usa le sole
                                             inserzioni sparite da uno snapshot completo
                                             successivo a un prezzo compatibile con una
                                             vendita; 'listed' usa tutte le inserzioni
                                             osservate, cioè i prezzi richiesti
                       --sale-margin <pct>   tolleranza dell'euristica di vendita: una
                                             inserzione sparita conta come venduta se non
                                             chiedeva più di questa percentuale sopra la
                                             mediana del proprio bucket; sopra è
                                             considerata un ritiro (default: 20, solo con
                                             --baseline sold)
                       --from-snapshot       confronta l'ultimo snapshot invece del mercato live
                       --max-per-type <n>    limite prodotti per tipo (solo live)
                       --top <n>             default: 30 (vale anche per le occasioni
                                             elencate nella notifica)
                       --notify              invia su Telegram le occasioni mai segnalate
                                             prima. Ogni inserzione si notifica una volta
                                             sola: un job schedulato non ripete la stessa
                                             offerta a ogni esecuzione

          notify-test
                     Invia un messaggio di prova sul canale di notifica: verifica token e
                     chat senza aspettare la prima occasione

          export     Esporta uno snapshot in CSV flat: una riga per inserzione,
                     statistiche in colonne <stat>_base/<stat>_bonus e skill in
                     colonne skill1_*/skill2_*
                       --snapshot <id>       default: ultimo snapshot del pianeta
                       --type <tipo>         filtro opzionale
                       --out <file>          default: ncmarket-<pianeta>-s<id>[-tipo].csv
                       --sep ,|;|tab         separatore CSV (default: ','; per Excel
                                             in italiano usare ';')

          prune      Retention: elimina le inserzioni non più viste da N giorni
                     (con i relativi avvistamenti e gli snapshot rimasti vuoti),
                     poi compatta il database con VACUUM
                       --days <n>            giorni di storico da conservare, default: 365
                       --dry-run             mostra cosa verrebbe rimosso senza modificare nulla

        Opzioni comuni (accettate dai comandi a cui si applicano):
          --planet odin|heimdall   default: heimdall (non si applica a prune)
          --db <percorso>          database SQLite, per i comandi che lo usano
                                   (default: %LOCALAPPDATA%\NCMarket\ncmarket.db)
          --no-names               non risolvere i nomi di item e skill

        Notifiche (deals --notify, notify-test) — configurate da variabili d'ambiente e
        non da opzioni, perché un token di bot è una credenziale e le opzioni finiscono
        nella cronologia della shell e nell'elenco dei processi:
          NCMARKET_TELEGRAM_TOKEN     token del bot, da @BotFather
          NCMARKET_TELEGRAM_CHAT_ID   chat di destinazione (negativo per gruppi e canali)

        Ogni comando accetta soltanto le proprie opzioni: un'opzione sconosciuta, ripetuta
        o priva di valore fa terminare la CLI con codice 2 senza eseguire nulla.

        snapshot e prune si serializzano fra loro tramite un lock su <database>.lock: se
        due job schedulati si sovrappongono, il secondo attende invece di fallire.

        Esempi:
          ncmarket fetch --type weapon --order price --limit 10
          ncmarket fetch --type ring --order cp_desc --limit 5 --details
          ncmarket snapshot --planet odin
          ncmarket history --item 10152001
          ncmarket stats --type ring --top 20
          ncmarket deals --discount 30
          ncmarket deals --grade legendary,mythic
          ncmarket deals --baseline listed --discount 40
          ncmarket deals --sale-margin 10 --min-samples 3
          ncmarket deals --type ring --from-snapshot --min-samples 3
          ncmarket notify-test
          ncmarket deals --from-snapshot --discount 30 --notify
          ncmarket export --type weapon --sep ;
          ncmarket export --snapshot 2 --out listino.csv
          ncmarket prune --dry-run
          ncmarket prune --days 180
        """);
}
