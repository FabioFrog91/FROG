# FROG — Master Context canonico

Ultimo aggiornamento del contesto: 2026-09-22.

Questo documento è il punto di ripartenza autorevole per una nuova chat, un'altra AI o una nuova sessione di sviluppo. Deve essere letto integralmente prima di proporre o applicare modifiche.

## 1. Regola di continuità del prompt

Questo file deve essere mantenuto sincronizzato con il progetto.

- Una decisione esplicitamente confermata dall'utente entra subito come `DECISO`.
- Una modifica entra come `IMPLEMENTATO — DA TESTARE` quando è nel codice ma non ha ancora ricevuto una build/runtime confirmation.
- `b ok` o `b ook` significa build locale completata con successo e zero errori. Solo allora la modifica può diventare `BUILD VERIFICATA`.
- Un test runtime confermato dall'utente, per esempio “funziona” o “perfetto”, promuove la parte interessata a `RUNTIME VERIFICATO`.
- Ipotesi, ricerche e proposte non devono essere presentate come implementate: restano `DA DECIDERE`, `DA IMPLEMENTARE` o `DA TESTARE`.
- Conservare nella conversazione gli esiti intermedi. Aggiornare e pubblicare questo documento soltanto quando l'utente lo richiede esplicitamente, consolidando allora tutte le decisioni, implementazioni e conferme accumulate.
- Rimuovere o correggere le istruzioni diventate obsolete: non accumulare appendici contraddittorie.
- Non inserire credenziali, sessioni autenticate, dati privati dei personaggi o output sensibili.

Gerarchia in caso di conflitto:

1. richiesta più recente dell'utente;
2. questo handoff aggiornato;
3. codice locale effettivo e relativo Git history;
4. repository remoto;
5. API/documentazione ufficiale verificata;
6. inferenze, che devono essere dichiarate come tali.

## 2. Modalità di collaborazione

L'utente non è un programmatore professionista. Parlare principalmente in italiano, in modo semplice ma tecnicamente corretto.

Regole inderogabili:

- principio guida FROG: **"una piuma, forte come adamantio"** — leggero, semplice, modulare e resistente; prevenire i bug correggendo la logica e l'ownership, non accumulando eccezioni o compensazioni locali;
- prima di aggiungere un fix, verificare se il sintomo nasce da una responsabilità collocata nel layer sbagliato; se sì, correggere l'ownership invece di aggiungere euristiche;
- presentation/UI/locator/highlighter non devono reinterpretare o ripianificare il dominio: devono consumare decisioni già risolte dal Core;
- il replan è una misura di emergenza per divergenze reali di quantità/stato, non per semplici split, merge, sort o relocation fisiche interne che lasciano invariata la quantità logica;
- un replan deve partire dallo stato reale corrente e dal fabbisogno residuo implicito nelle requirement originali, senza annullare semanticamente i MOVE già verificati;
- capire architettura, dipendenze, ownership dello stato e conseguenze prima di modificare;
- leggere sempre i file reali e cercare implementazioni già esistenti;
- non ricostruire codice dalla memoria e non inventare API;
- niente logica duplicata se un componente esistente può essere esteso coerentemente;
- una responsabilità coerente per `.cs`; evitare sia monoliti sia micro-file artificiali;
- niente refactor, astrazioni o dipendenze non correlate al problema corrente;
- prima correttezza, poi performance, poi parallelismo;
- non spostare logica importante nel rendering ImGui;
- il runtime deve continuare a funzionare con le finestre chiuse;
- verificare prima e dopo ogni modifica: stato Git, diff, build disponibile, effetti indiretti e regressioni;
- se un dubbio non banale cambia il comportamento, fermarsi e discuterlo prima;
- distinguere sempre `VERIFICATO`, `IMPLEMENTATO — DA TESTARE`, `IPOTESI` e `FUTURO`;
- non dichiarare mai che il codice compila senza un vero `b ok` dell'utente o una build realmente eseguita nello stesso ambiente;
- quando si consegnano comandi PowerShell per il PC dell'utente, iniziare sempre con `cd C:\Dev\FROG`;
- non usare `git pull` alla cieca e non usare `-p:Platform=x64` con la solution `.slnx`.
- il workflow abituale di pubblicazione usa il connettore GitHub autenticato di ChatGPT sulla branch `debug/retainer-row-inspector`; le credenziali Git del terminale sono separate da quelle del connettore;
- un errore di `git push` eseguito nella shell dimostra soltanto che quella shell non dispone delle credenziali necessarie: non significa che il connettore GitHub sia scollegato;
- prima di dichiarare indisponibile GitHub o cambiare metodo di consegna, verificare il connettore con una lettura innocua del repository o della branch;
- non sostituire autonomamente il workflow con patch, copie manuali o altri trasferimenti: spiegare il problema e ottenere prima l'approvazione dell'utente;
- non chiedere né acquisire password, token o sessioni GitHub dell'utente; usare esclusivamente l'autenticazione già gestita dal connettore;
- pubblicare senza force-push, verificare sul remoto i file aggiornati e poi fornire `git fetch origin` seguito da `git restore --source origin/debug/retainer-row-inspector -- <file>` solo per i file interessati.

Prima di una modifica importante:

1. preservare un checkpoint recuperabile;
2. controllare working tree e submodule;
3. eseguire la baseline disponibile;
4. identificare componenti e test già esistenti da riusare;
5. applicare modifiche minime e isolate;
6. rivedere il diff completo;
7. annotare lo stato corretto nella conversazione e aggiornare questo documento solo su richiesta esplicita dell'utente.

## 3. Repository e ambiente

- Repository: `FabioFrog91/FROG`
- Locale utente: `C:\Dev\FROG`
- Branch di lavoro corrente: `debug/retainer-row-inspector`
- Solution: `SamplePlugin.slnx`
- Project: `SamplePlugin/SamplePlugin.csproj`
- Dalamud SDK: `15.0.0`
- SDK locale utente noto: `.NET 10.0.401`, Debug
- Namespace Core: `FROG.Core.Inventory`

Build locale autorevole:

```powershell
cd C:\Dev\FROG
dotnet build SamplePlugin.slnx -c Debug
```

Il CI GitHub storico non inizializza correttamente tutti i submodule e non è una prova autorevole finché quel workflow non viene corretto.

Stato Git osservato in questo workspace al 2026-09-22:

- ultimo checkpoint locale di codice con build e runtime verificati: `0b070a0` — `Aggregate capacity advice across blocked stacks`;
- equivalente più recente pubblicato dal connettore GitHub prima di questo aggiornamento del prompt: `b3fe6d8`;
- checkpoint immediatamente precedenti del flusso capacità: `40c4746` — `Resume execution after capacity becomes available` e `f6e492c` — `Explain capacity-blocked execution plans`;
- l'intero flusso stack/capacità fino a `0b070a0` ha ricevuto `b ok` e le conferme runtime descritte nella sezione 12;
- il riferimento locale `origin/debug/retainer-row-inspector` può restare indietro dopo una pubblicazione tramite connettore finché non viene eseguito `git fetch origin`; non dedurre da questo che il remoto non sia aggiornato;
- backup tag pertinenti: `backup/pre-stack-capacity-20260921`, `backup/pre-execution-capacity-message-20260921`, `backup/pre-capacity-waiting-flow-20260921` e `backup/pre-capacity-advice-aggregation-20260921`;
- submodule `CriticalCommonLib` pinned a `34d364ea938e585b4f7eeab5b36e4261fdd817d0`;
- nel submodule sono presenti due modifiche locali intenzionali di unsubscribe in `InventoryMonitor.cs` e `InventoryScanner.cs`;
- le stesse modifiche sono conservate in `patches/critical-common-lib-dispose-events.patch`;
- non sovrascrivere o perdere queste modifiche.

Questo workspace non dispone di `dotnet`, `csc` o `msbuild`. Il checkpoint locale `0b070a0`, pubblicato sul remoto come `b3fe6d8`, ha ricevuto un vero `b ok` nell'ambiente Windows dell'utente e successive conferme runtime.

## 4. Architettura fondamentale

Pipeline:

`Teamcraft/input → RequirementSet → InventoryIndex → resolver/policy → GlobalTransferPlanner → PlannerPlan → execution → verification → reconciliation/replan`

Stati concettuali da non confondere:

- Observed
- Known
- Planned
- Executed
- Verified
- Reconciled

Il Core FROG deve restare proprio, modulare e indipendente. `CriticalCommonLib` può fornire adapter/provider, ma non deve possedere le regole di dominio di FROG. FCCH e InventoryTools sono riferimenti tecnici, non nuove dipendenze né codice da copiare wholesale.

Convenzioni:

- nomi brevi, chiari e coerenti con i componenti esistenti;
- i contratti provider seguono il suffisso `API`, non introdurre casualmente interfacce `I...`;
- non creare nuovi `Manager`, `Coordinator` o state machine generici se la responsabilità appartiene a una classe esistente;
- record/enumerazioni strettamente collegati possono restare nello stesso file della loro responsabilità.

## 5. Modello inventory e routing

Storage supportati:

- `CharacterInventory`
- `Retainer`
- `FreeCompanyChest`

Il Main Character è il personaggio da cui nasce il Fetch e rimane il Main durante tutti i replan, anche quando il client è loggato su un alt.

Route fisicamente valide:

- Retainer → inventario del proprio character;
- Character Inventory → FC;
- FC → Main Inventory.

Route vietate:

- Retainer → FC diretto;
- Alt Inventory → Main diretto.

La FC è un hub condiviso e usa `ParentCharacterId = 0`. Gli item già nel Main soddisfano immediatamente il fabbisogno e non devono essere mossi. Missing è best-effort: materiale disponibile parzialmente viene usato e il risultato è `CompletedWithMissing`, non un errore.

Identità e contenuti restano separati:

- `CharacterCatalog` persiste le identità in `character-catalog.json`;
- `InventoryIndex` persiste i contenuti in `inventory-index.json`.

HQ e NQ sono dimensioni separate. FROG conserva `RawItemId`, `BaseItemId` e `IsHq`; non aggiungere manualmente offset come `1.000.000`.

## 6. Global Planner

Il planner decide il piano migliore secondo priorità lessicografiche configurabili:

1. CharacterSwitches
2. RetainerAccesses
3. ConsumedStacks
4. TransferHops
5. SourcePriority
6. Freshness
7. Alphabetical

Non hardcodare priorità alternative nella ricerca. `PlannerState` è simulazione e ogni azione deve passare da `PlannerActionValidator`.

Principio dell'ordine visibile:

- optimization decide `WHAT/WHERE`;
- ODR decide execution `ORDER` dopo la ricerca;
- non aggiungere la posizione visibile allo scoring;
- non cambiare source o quantità per ottenere un ordine visivamente migliore.

`ExecutionOrderCompiler` è ora collegato e usa uno snapshot ODR acquisito prima del task del planner. Non legge il dictionary non thread-safe di `OdrScanner` dal background task. Ordina soltanto MOVE commutabili e mantiene switch e dipendenze di bridge. Il fallback resta fisico e deterministico.

Ordine operativo confermato:

- pagina visibile crescente;
- slot visibile crescente;
- poi BaseItemId e qualità come tie-breaker;
- lo stesso ordinamento viene usato per gli stack consumati dentro una MOVE;
- FC source segue pagina 1 → pagina 5 e slot crescente.

Stato: `RUNTIME VERIFICATO` dall'utente con “funziona”.

## 7. Free Company Chest

Principio invariabile: `container loaded != container observed`.

Comportamento corrente:

- rileva la pagina FC attiva tramite UI;
- legge soltanto la pagina osservata;
- mentre la chest è aperta effettua polling controllato;
- una pagina modificata deve produrre osservazioni/fingerprint stabili prima del `ReplaceSource`;
- apertura/chiusura resetta i candidate snapshot;
- un fingerprint uguale al Known evita replace inutili;
- questo evita il transient zero-wipe durante lo scorrimento rapido delle tab e dopo il cambio personaggio.

Depositi FC:

- la destination è page-agnostic tramite `InventorySource.AnyFreeCompanyPageContainer`;
- il planner non deve vincolare il deposito a pagina 1 o a un'altra pagina specifica;
- il gioco/utente può usare una pagina diversa o effettuare un merge compatibile;
- il verifier osserva la quantità logica complessiva nella FC;
- il replan deve preservare il Main originale e riconoscere la FC prima come destination e poi come source del ritorno al Main.

Highlight FC:

- `FreeCompanyDisplayLocator` risolve pagina e slot sorgente;
- `ExecutionInventoryHighlighter` evidenzia tab e slot della FC;
- usa scritture ATK locali e ripristina lo stato originale;
- nessuna azione o scrittura server.

Stato: deposito page-agnostic, recupero dopo switch e highlight FC sono stati testati runtime; l'utente ha confermato “funziona”.

## 8. Retainer e posizione visibile

Storage interno retainer: 7 container × 25 slot. UI: 5 pagine × 35 slot.

La traduzione usa `RetainerSortOrder.InventoryCoords`:

- trovare l'indice della coppia fisica `(slotIndex, containerIndex)`;
- `visiblePage = index / 35 + 1`;
- `visibleSlot = index % 35 + 1`.

Planner e verifier continuano a usare coordinate fisiche. Execution/display usano ODR. Se ODR non è disponibile non inventare coordinate: usare il fallback fisico deterministico.

Questa traduzione e l'ordine visibile sono `RUNTIME VERIFICATI`.

## 9. Execution, verification e reconciliation

Componenti esistenti da rispettare:

- `PlanExecutionRuntime`
- `PlanExecutionCoordinator`
- `PlanExecutionSession`
- `PlanExecutionVerifier`
- `PlanExecutionReconciler`
- `PlanExecutionPlanGuard`

Status esistenti:

- Idle
- Pending
- Executed
- WaitingForObservation
- Verified
- ReplanRequired
- WaitingForCapacity
- Complete

La pipeline è framework-driven; `ExecutionWindow` è una vista passiva. Chiudere la finestra non arresta l'esecuzione, mentre Stop sì.

Regole:

- SWITCH è verificato osservando il `ContentId` target;
- MOVE richiede osservazioni nuove sia della source sia della destination;
- quantità confermata = `min(source decrease, destination increase)`;
- una diminuzione solo source non può avanzare o completare l'azione;
- quantità diversa da quella pianificata produce variance e replan residuo;
- non sottrarre manualmente dalle requirements;
- il replan usa sempre RequirementSet originale contro InventoryIndex reale;
- il Main originale deve essere preservato;
- un piano con quantità disponibili ma bloccate dalla capacità non deve risultare `Complete`: deve entrare in `WaitingForCapacity`;
- `WaitingForCapacity` è una pausa interamente client-side: non muove oggetti e non invia azioni al server;
- Execution deve indicare il numero minimo di slot aggiuntivi da liberare, considerando merge, stack massimo, separazione HQ/NQ e slot di riserva, e mostrare il primo movimento che verrà sbloccato;
- soltanto durante `WaitingForCapacity`, il runtime ricontrolla la capacità mirata ogni 250 ms e avvia automaticamente il replan quando tutti i blocchi di capacità noti entrano nello snapshot reale osservato;
- `COPIA DEBUG` nella finestra Execution deve includere stato, Missing, CapacityBlocked, UnavailableMissing, CapacityAdvice, blocchi di capacità e step;
- `PlanExecutionPlanGuard` rigioca solo le azioni residue usando `PlannerActionValidator`;
- il guard opera ai checkpoint stabili: start/restart e dopo un'azione verificata, non durante un trasferimento in corso;
- non reintrodurre `InventoryIndex.Revision` per invalidare globalmente il piano.

Il controllo dei delta appaiati è implementato nel commit `55cc0a6`. Evita falsi avanzamenti, ma una scomparsa source-only può ancora restare in attesa senza una classificazione definitiva: vedere lavoro aperto.

## 10. Highlight e overlay

Funzioni runtime verificate:

- riga retainer tramite NodeId 14;
- tab e slot retainer;
- retry quando il grid arriva in ritardo;
- tab e slot dell'inventario alt verso FC;
- tab e slot FC source;
- quantity badge ImGui `-N` per ogni stack coinvolto;
- restore esatto dei valori ATK originali sulla stessa addon instance.

Sicurezza:

- nessun raw pointer conservato tra frame;
- i binding conservano addon name, node id, address dell'istanza e stato visuale originale;
- scritture colore solo se necessarie;
- nessun click, callback gameplay, inventory move o packet send;
- rainbow mode è stato rifiutato e non va implementato.

Non ripetere nei log o nel prompt dati privati catturati durante i test dei retainer.

## 11. Lifecycle, task e Dispose

Commit corrente `1799b0a`:

- propaga `CancellationToken` nel Global Planner;
- cancella in sicurezza task planner e login sync;
- usa generation guard contro completion obsolete;
- rende `Plugin.Dispose()` best-effort e resistente alle eccezioni dei singoli cleanup;
- include la patch CCL per due unsubscribe mancanti.

Stato: `BUILD VERIFICATA` tramite `b ok` dell'utente il 2026-09-21. La verifica runtime specifica dei percorsi di cancellazione/cleanup resta distinta dalla build.

Ogni nuovo event handler, hook, timer, task o `CancellationTokenSource` deve avere ownership chiara e cleanup simmetrico. `Dispose()` non deve lasciare che il fallimento di un cleanup impedisca quelli successivi.

## 12. Stack, merge e capacità

Stato: `BUILD E RUNTIME VERIFICATI` fino al checkpoint locale `0b070a0`, pubblicato sul remoto come `b3fe6d8`.

Verifiche runtime confermate dall'utente:

- merge semplice NQ verso uno stack compatibile già presente nella destinazione;
- overflow/split: uno stack a 980 più 50 produce 999 più 31 senza overstack;
- HQ e NQ restano separati e non vengono mergiati insieme;
- con un solo slot vuoto fisico residuo, lo slot viene protetto come riserva e non viene generata una MOVE che lo consumi;
- due blocchi distinti dello stesso item, uno HQ e uno NQ, richiedono correttamente due slot: dopo averne liberato uno Execution resta in attesa, dopo il secondo esegue il replan automatico;
- capacità parziale: con 1.200 unità disponibili e un solo slot utilizzabile viene pianificata una MOVE da 999 e vengono dichiarate 201 unità bloccate; liberato un altro slot, il replan riparte e pianifica il residuo;
- messaggio `WaitingForCapacity`, consiglio aggregato sugli slot, indicazione del movimento successivo e copia debug della finestra Execution.

Verifiche runtime ancora mirate:

- capacità/merge FC multipagina attraverso tutte le pagine della destinazione logica;
- caso con zero slot fisici vuoti, se si vuole una prova separata dal caso già verificato con zero slot utilizzabili dopo la riserva;
- scenari estesi con più destinazioni logiche o route bridge più lunghe.

Decisioni confermate:

- il massimo stack è per-item e deve provenire da `Lumina.Excel.Sheets.Item.StackSize`; non hardcodare `999`;
- CCL espone già la stessa semantica tramite `InventoryItem.RemainingStack` e `FullStack`;
- HQ e NQ non possono essere mergiati insieme;
- riempire prima tutti gli stack parziali compatibili;
- dividere l'eccedenza in nuovi stack senza mai superare `Item.StackSize`;
- i merge non consumano slot vuoti;
- conservare un solo slot di riserva per destinazione logica, non per pagina;
- le quattro bag normali del character sono una destinazione logica;
- tutte le pagine FC sono una destinazione logica;
- capacità utilizzabile degli slot vuoti = `max(0, slotVuoti - 1)`;
- se non entra tutto, trasferire quanto può entrare e poi mettere in pausa mantenendo attivo il refresh; non richiedere spazio per l'intero fabbisogno prima di iniziare.
- se `Item.StackSize` non è disponibile o non è valido, fallire in modo sicuro e non assumere `999`;
- la simulazione deve aggiornare uno stato virtuale dopo ogni allocazione, così azioni successive non possono riusare la stessa capacità;
- per le destinazioni page-agnostic, in particolare la FC, cercare prima tutti i merge compatibili e poi gli slot vuoti disponibili nell'intera destinazione logica;
- prima di ogni futuro movimento automatico, ricalcolare la capacità sullo snapshot osservato più recente: il piano non autorizza da solo l'esecuzione.

Problemi reali trovati nel codice:

- `PlannerCapacitySnapshot` viene costruito dall'intero `InventoryIndex.Items` prima del filtro degli item richiesti;
- conserva soltanto slot totali/occupati, merge space per item rilevante e stack massimi, evitando di portare gli item estranei nella ricerca;
- gli slot logici standard sono 140 per character, 175 per retainer e 250 per FC; cristalli e container estranei non contano nell'occupazione normale;
- `GlobalPlannerCoordinator` acquisisce `Item.StackSize` da Lumina prima del task background e fallisce in modo sicuro se manca;
- `PlannerState.Move()` riempie gli stack parziali compatibili, divide il residuo in stack entro il massimo e aggiorna una capacità virtuale immutabile dopo ogni MOVE;
- `PlannerActionValidator` e `PlanExecutionPlanGuard` riusano la stessa capacità; il guard ricostruisce l'occupazione dallo stato reale più recente;
- se entra solo una parte, il compilatore riduce il MOVE alla quantità realmente accettabile e il resto rimane Missing;
- `PlannerPlan` conserva `CapacityBlocked`, i `PlannerCapacityBlock` ordinati e i consigli `PlannerCapacityAdvice` aggregati;
- i consigli vengono aggregati per destinazione logica, item, qualità e stack massimo: blocchi compatibili dello stesso item/qualità possono condividere capacità, mentre HQ e NQ richiedono stack distinti;
- `PlannerCapacitySnapshot.TryReserveDestination` simula in sequenza tutti i blocchi noti sullo snapshot corrente e autorizza il replan soltanto quando entrano tutti;
- Execution usa `WaitingForCapacity`, non `Complete`, conserva la sessione attiva e ricontrolla soltanto la capacità interessata ogni 250 ms finché non può eseguire il replan;
- il primo movimento bloccato viene mostrato come prossimo step utile;
- scoring, selezione source, route e ordinamento ODR non sono stati modificati.

Non implementare una formula locale duplicata. Stack simulation, validator e plan guard devono condividere la stessa responsabilità/calcolo.

Limite intenzionale ancora aperto: permessi FC e disponibilità runtime delle singole tab non fanno ancora parte della capacità del planner. Il futuro preflight automatico dovrà verificare la destinazione realmente accessibile e fallire in modo sicuro; la capacità pianificata non autorizza da sola un movimento.

## 13. Interferenze manuali e discard

Stato: `DIREZIONE DECISA — NON IMPLEMENTATO`.

La base esistente osserva source e destination e non accetta una sparizione source-only come trasferimento. Manca però un timeout/settle finale che distingua:

- trasferimento valido;
- trasferimento parziale;
- rimozione esterna non spiegata;
- discard confermato.

Comportamento deciso:

- in manuale, dopo refresh stabile, non segnare l'azione come eseguita; fare replan sullo stato reale e aumentare Missing se non esistono fonti alternative;
- in una futura modalità automatica, qualunque delta incompatibile deve mettere in pausa prima di inviare un'altra azione;
- non attribuire con certezza l'azione all'utente se potrebbe provenire da un altro plugin;
- se la causa non è dimostrata, mostrare “oggetto rimosso fuori dal trasferimento pianificato”, non “discard dell'utente”.

API ufficiale studiata:

- `IChatGui.LogMessage` espone `ILogMessage`;
- sono disponibili `LogMessageId`, `GameData`, parametri interi/stringa, source e target;
- l'oggetto è valido solo durante il callback: copiare subito i dati necessari;
- usare ID e parametri strutturati, non il testo localizzato della chat;
- `FormatLogMessageForDebugging()` non va usato nella logica normale;
- la UI `SelectYesno` è soltanto un segnale preventivo, non una prova univoca di discard.

Prima dell'implementazione serve catturare runtime il vero `LogMessageId` e la disposizione parametri del discard nella versione di gioco corrente.

## 14. Automatic movements e interoperabilità plugin

Stato: `ARCHITETTURA DECISA — NON IMPLEMENTATO`.

Attualmente FROG non muove oggetti e non invia azioni al server. Highlight e overlay sono client-side presentation.

Quando si progetterà l'automazione:

- iniziare dal flusso UI nativo, non da signature headless;
- inviare una sola azione alla volta;
- attendere conferma inventory source+destination prima della successiva;
- conservare `PlanExecutionCoordinator`, `PlanExecutionVerifier` e `PlanExecutionReconciler` come autorità della conferma; gli altri plugin sono riferimenti, non sostituti di questa pipeline;
- non considerare riuscito un movimento soltanto perché il comando è stato inviato, la UI si è chiusa o non è comparso un errore;
- prima del dispatch eseguire un preflight sullo stato corrente: character, retainer/storage attivo, UI pronta, source slot/item/qualità/quantità, destination capacity, osservazioni non stale e nessuna operazione inventory nativa pendente;
- avere ownership/issued marker esplicito per distinguere l'azione FROG da interferenze;
- associare a ogni dispatch un identificatore/fingerprint idempotente e non reinviare un'azione dall'esito incerto; dopo un timeout effettuare prima un refresh completo;
- introdurre timeout e risultati distinti per rifiuto, nessuna modifica, trasferimento parziale, delta incompatibile e modifica source-only;
- interrompere pulitamente su input manuale, lag, disconnect, chiusura UI, cambio character/storage inatteso o delta incompatibile;
- usare l'IPC pubblico di AutoRetainer per la soppressione: catturare lo stato precedente, modificare solo ciò che serve e ripristinare esattamente quello stato in completamento, annullamento, eccezione e `Dispose()`;
- modellare il coordinamento plugin come una lease con cleanup garantito, non come task di cleanup sparsi nella coda;
- per Artisan, FCCH o altri plugin senza un contratto IPC pubblico verificato, non modificare configurazioni o memoria interna: rilevare l'attività incompatibile e fallire/andare in pausa in modo sicuro;
- il controllo delle native `InventoryManager.PendingOperations`, come idea osservata in FCCH, è un gate pre-dispatch aggiuntivo e non sostituisce la conferma dei delta;
- i messaggi strutturati di rifiuto possono anticipare la pausa, ma non costituiscono da soli prova di riuscita o fallimento definitivo;
- eventuali backend headless/signature restano futuri, opzionali e separati dal primo executor UI-based;
- non promettere assenza di rischio ToS e non progettare evasione della detection.

Riferimenti studiati e decisioni derivate:

- FCCH: riempimento stack parziali, split sugli slot vuoti, stato virtuale della capacità, gate sulle operazioni native e rilevamento rifiuti;
- Artisan restock: sequenza UI/task e sospensione di AutoRetainer/YesAlready, da rendere più robusta con cleanup centralizzato;
- AutoRetainer: un movimento per iterazione, capacità reale, snapshot prima del comando e timeout, migliorati in FROG dalla verifica bilaterale esistente;
- FROG non deve copiare il fallback stack `999`, la conferma source-only o l'avanzamento basato sul solo dispatch/evento generico.

## 15. Prossimo lavoro corretto

Stack/capacità, attesa per spazio e replan automatico hanno superato build e test runtime mirati. Prossimi checkpoint:

1. testare nel gioco merge e capacità della FC come unica destinazione logica multipagina, compresi stack compatibili presenti in pagine diverse;
2. facoltativamente isolare il caso con zero slot fisici vuoti e provare scenari con più destinazioni logiche o route bridge più lunghe;
3. continuare a verificare che esecuzione manuale, consiglio capacità e replan preservino il Main originale e il RequirementSet originale;
4. controllare diagnostica allocazioni/memoria per confermare che snapshot compatto e polling limitato a `WaitingForCapacity` restino leggeri;
5. correggere eventuali problemi senza ampliare il refactor;
6. aggiornare questo file soltanto quando l'utente lo richiede esplicitamente, promuovendo a `RUNTIME VERIFICATO` solo i casi realmente provati.

Il riconoscimento discard deve restare un passaggio separato perché richiede una cattura runtime reale.

Dopo stack/capacità e relativa verifica, l'ordine approvato per preparare l'automazione è:

1. preflight condiviso immediatamente prima del movimento;
2. timeout e classificazione degli esiti senza duplicare il coordinator esistente;
3. lease IPC di AutoRetainer con ripristino garantito;
4. issued marker/fingerprint e protezione contro retry duplicati;
5. primo executor UI-based strettamente seriale;
6. backend headless soltanto come valutazione futura separata.
