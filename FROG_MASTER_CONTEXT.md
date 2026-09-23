# FROG — Master Context canonico

Ultimo aggiornamento: 2026-09-23.

Questo documento è il contratto architetturale autorevole di FROG. Deve essere letto prima di proporre o applicare modifiche. Non è una cronologia: contiene solo regole, ownership, invarianti, stato verificato e lavoro aperto ancora rilevante.

## 1. Principio guida

FROG deve essere **"una piuma, forte come adamantio"**: leggero, semplice, modulare e resistente.

Regole inderogabili:

- prima la logica, poi il codice;
- prevenire i bug correggendo ownership e contratti, non accumulando eccezioni locali;
- se un sintomo nasce nel layer sbagliato, correggere il confine tra moduli invece di aggiungere euristiche;
- una responsabilità principale per componente;
- non duplicare logica già posseduta da un altro modulo;
- UI, locator e highlighter non possiedono logica di dominio;
- nessun refactor non necessario al problema corrente;
- una modifica che cambia la logica deve essere spiegata all'utente prima di essere applicata;
- se esiste un dubbio non banale sul comportamento, fermarsi e ragionare prima di modificare;
- leggere sempre il codice reale corrente e verificare dipendenze/API prima di cambiare;
- non inventare API, eventi, metodi o semantiche;
- distinguere sempre:
  - DECISO
  - IMPLEMENTATO — DA TESTARE
  - BUILD VERIFICATA
  - RUNTIME VERIFICATO
  - IPOTESI
- `b ok` / `b ook` significa build locale con zero errori;
- una build CI verde non sostituisce un runtime test;
- codice/Git corrente batte la memoria storica.

## 2. Repository e workflow

- Repository: `FabioFrog91/FROG`
- Locale utente: `C:\Dev\FROG`
- Branch normale: `master`
- Solution: `SamplePlugin.slnx`
- Project: `SamplePlugin/SamplePlugin.csproj`
- Namespace Core: `FROG.Core.Inventory`
- Master corrente al 2026-09-23: `523b666d38886ca3e418d22ef7d6a2870691cc85`

Workflow modifiche:

1. leggere `master` reale;
2. creare branch dedicata;
3. applicare modifiche minime e coerenti;
4. controllare diff completo;
5. aprire PR;
6. attendere CI verde;
7. merge normale, mai force push;
8. test locale/runtime separato.

Branch sperimentali non mergiate non sono soluzione. `fix/core-execution-logical-groups` è obsoleta/scartata e non va usata come riferimento.

## 3. Obiettivo principale di FROG

FROG deve portare il Main Character il più vicino possibile al fabbisogno richiesto usando ciò che sa realmente esistere, producendo un piano valido e poi accompagnando/verificando l'esecuzione senza confondere:

- stato osservato;
- stato conosciuto;
- stato pianificato;
- azione eseguita;
- azione verificata.

Il Main Character nasce con il Fetch e resta il Main durante tutta l'esecuzione e durante eventuali replan.

## 4. Pipeline canonica

La pipeline è:

`Input/Teamcraft → RequirementSet → Observation/InventoryIndex → Planning → Execution → Verification → Reconciliation → Replan solo se necessario`

Presentation è laterale e passiva.

### 4.1 Observation — "Che cosa esiste davvero?"

Owner principale: `InventoryIndex` e observer/provider dedicati.

Responsabilità:

- acquisire quantità, HQ/NQ, owner, storage, container, slot;
- distinguere freshness/observation revision da content revision;
- aggiornare Known state da eventi affidabili;
- persistere contenuti quando appropriato.

Non deve:

- scegliere source;
- ottimizzare;
- decidere route;
- creare MOVE;
- decidere cosa evidenziare.

### 4.2 Planning — "Che cosa bisogna fare?"

Owner principale: `GlobalTransferPlanner` e suoi componenti di planner.

Input:

- `RequirementSet`;
- snapshot immutabile di inventory/capacity;
- `ResolutionPolicy`;
- impostazioni di ottimizzazione.

Responsabilità:

- calcolare quanto manca;
- scegliere owner/storage;
- scegliere route valide;
- rispettare capacity;
- pianificare switch;
- ottimizzare secondo le priorità canoniche.

Il planner può usare dettaglio fisico degli stack per scoring/simulazione, ma il dettaglio fisico non deve diventare automaticamente un comando permanente di execution.

### 4.3 Execution — "Come eseguo adesso ciò che il planner ha deciso?"

Execution traduce il piano logico sullo stato fisico corrente.

Responsabilità:

- prendere la prossima decisione logica del piano;
- materializzarla sugli stack correnti osservati;
- produrre una sola istruzione runtime autorevole per UI/highlighter/verifier;
- aggiornare la materializzazione quando cambiano split/merge/sort/relocation senza cambiare il piano logico.

Split, merge, sort e relocation interna che non cambiano la quantità logica non sono motivo di replan.

Execution non deve:

- rifare il Global Planner;
- cambiare scoring/priorità;
- inventare route;
- dipendere dalla UI per avanzare.

### 4.4 Verification — "È successo davvero?"

Owner principale: `PlanExecutionVerifier`.

Regole:

- SWITCH verificato osservando il target character;
- MOVE richiede osservazione nuova di source e destination;
- quantità confermata = `min(source decrease, destination increase)`;
- source-only non completa;
- relocation/split/merge interno non è un MOVE;
- Retainer e CharacterInventory usano quantità logica per evitare falsi trasferimenti da relocation interna.

Verification non decide il nuovo piano.

### 4.5 Reconciliation — "Realtà e previsione coincidono?"

Owner principale: `PlanExecutionReconciler`.

Responsabilità:

- classificare delta osservati;
- determinare quantità realmente trasferita;
- segnalare variance reale.

Non deve:

- fare planning;
- scegliere source future;
- interpretare UI.

### 4.6 Replan — emergenza

Il replan è una misura di emergenza per divergenze reali di quantità/stato, non per layout fisico.

Deve:

- usare RequirementSet originale;
- usare InventoryIndex reale corrente;
- preservare il Main originale;
- considerare ciò che è già realmente arrivato nelle destinazioni;
- non invalidare semanticamente i passaggi già Verified.

La cronologia Verified esplicita attraverso una nuova sessione di replan è ancora un requisito aperto da implementare correttamente.

### 4.7 Presentation

Componenti come locator, highlighter, overlay e finestre devono essere passivi.

Locator:
- traduce coordinate fisiche → coordinate visibili;
- non decide quantità;
- non sceglie stack;
- non ripianifica.

Highlighter/overlay/window:
- mostrano ciò che Execution ha già deciso;
- non ricostruiscono il dominio da `PlannerPlan` + `InventoryIndex`;
- nessuna logica critica deve vivere nel render ImGui.

## 5. Storage e route

Storage supportati:

- `CharacterInventory`
- `Retainer`
- `FreeCompanyChest`

Route valide:

- Retainer → inventario del proprio character;
- CharacterInventory → FC;
- FC → Main CharacterInventory.

Route vietate:

- Retainer → FC diretto;
- Alt CharacterInventory → Main diretto.

FC è hub condiviso. HQ e NQ sono identità logiche separate.

## 6. Priorità planner canoniche

Ordine lessicografico esatto:

1. CharacterSwitches
2. RetainerAccesses
3. ConsumedStacks
4. TransferHops
5. SourcePriority
6. Freshness
7. Alphabetical

Mai weighted.

Stato corrente:
- ordine canonico reso immutabile;
- `QualityFallback` rimosso dallo score perché HqFirst/NqFirst è già deciso a monte nella costruzione dei target qualità;
- `TransferHops` conta route logiche distinte;
- `SourcePriority` è calcolata una volta per source logica;
- `Alphabetical` usa source logiche distinte;
- `ConsumedStacks` e `Freshness` restano intenzionalmente fisici perché misurano consumo reale degli stack ed evidenza osservata.

Stato: BUILD/CI VERIFICATO, runtime planner da riconfermare nelle casistiche complete.

## 7. Capacity

Regole canoniche:

- `Item.StackSize` da Lumina, mai hardcode 999;
- HQ/NQ separati;
- merge compatibili prima degli slot vuoti;
- split entro StackSize;
- un solo slot di riserva per destinazione logica;
- 4 bag normali CharacterInventory = una destinazione logica;
- tutte le pagine FC = una destinazione logica;
- usable empty slots = `max(0, emptySlots - 1)`;
- trasferimento parziale consentito;
- se resta quantità bloccata → `WaitingForCapacity`, non `Complete`;
- capacity simulata dal planner non autorizza da sola un futuro movimento automatico.

## 8. Observation corrente

### CharacterInventory

Owner corretto: `CharacterInventoryObservationObserver`.

Usa `IGameInventory.InventoryChangedRaw` e aggiorna l'intero CharacterInventory tramite `SyncPlayerInventory()`.

### Retainer

Owner desiderato: `RetainerInventoryObservationObserver`.

Usa `IGameInventory.InventoryChangedRaw` e legge RAW Retainer tramite `StorageReader.TryReadActiveRetainer`.

Problema aperto:
- esiste ancora un secondo trigger in `FrogInventoryStartup.OnInventoryChanged` basato su CCL che richiama `SyncStorageSources`;
- due owner dello stesso refresh Retainer violano il principio di ownership unica;
- va ripulito dopo verifica delle dipendenze/lifecycle.

### FreeCompanyChest

Owner: `FreeCompanyInventoryObserver`.

Principi:
- loaded != observed;
- owner/generation-aware;
- pending acquisition da ContainerInfo;
- completamento solo dopo evento post-copy CCL `FreeCompanyPageScanned`;
- observation revision e content revision separate;
- una scan periodica invariata non crea freshness artificiale.

Stato FC observation/data-sync: RUNTIME VERIFICATO.

`FreeCompanyObservationProbe` è diagnostica storica ancora AutoActivate: candidato a rimozione/isolation dopo audit lifecycle.

## 9. Stato del planner

Componenti validi da conservare come base:

- `GlobalTransferPlanner`
- `GlobalAllocationPlanner`
- `PlannerState`
- `PlannerActionValidator`
- `PlannerCapacitySnapshot`
- `PlannerPlanEvaluator`
- `GlobalPlannerCoordinator`

Contratto corrente:

- `PlannerPlan.Actions` = simulazione/evidenza fisica interna del planner;
- `PlannerPlan.Decisions` = contratto logico immutabile Planning → Execution;
- `ExecutionInstruction` = materializzazione runtime della decisione logica sugli stack osservati correnti.

Esempio reale:
- evidenza fisica: NQ 4 + 1 + 3 + 2, HQ 9;
- decisioni logiche: NQ 10, HQ 9;
- split/merge/sort/relocation cambiano solo la `ExecutionInstruction`, non la decisione logica.

Stato: BUILD/CI VERIFICATO; runtime multi-stack ancora da confermare.

## 10. Doppia pipeline resolver/planner

Esiste ancora una pipeline legacy attiva:

`ResolverCoordinator → TransferPlanner → RequirementResolver → TransferPlan`

e una pipeline globale:

`GlobalPlannerCoordinator → GlobalTransferPlanner → PlannerPlan`

La prima è ancora usata per diagnostica/resolution e alimenta anche la `ResolutionPolicy`, ma non deve diventare una seconda autorità di planning.

Audit richiesto:
- separare "resolver/source policy" da "planner";
- eliminare o ridurre `TransferPlanner` se duplica planning;
- `InventoryStackResolver` legacy è stato rimosso perché privo di consumer e legato al vecchio modello fisico;
- una sola autorità finale deve decidere il piano strategico.

## 11. Execution corrente

Componenti:

- `PlanExecutionRuntime`
- `PlanExecutionCoordinator`
- `PlanExecutionSession`
- `PlanExecutionVerifier`
- `PlanExecutionReconciler`
- `PlanExecutionPlanGuard`

Buono da conservare:

- framework-driven, indipendente dalla finestra;
- source+destination verification;
- logical source quantity per Retainer/CharacterInventory;
- source-only non completa;
- capacity blocked non diventa Complete;
- replan usa requirements originali e Main originale.

Stato corrente:
- Session/Coordinator/Verifier/Reconciler lavorano su `PlannerDecision` logiche;
- `PlanExecutionMaterializer` produce una sola `ExecutionInstruction` autorevole dagli stack correnti;
- trasferimenti parziali coerenti avanzano cumulativamente senza replan e rimaterializzano solo il residuo;
- il Guard fa preflight solo della decisione corrente;
- layout change senza delta logico non causa replan;
- Start iniziale e Start post-replan usano lo stesso percorso di preflight.

Stato: BUILD/CI VERIFICATO; runtime multi-stack e progressivo da confermare.

## 12. Presentation corrente

### RetainerDisplayLocator / CharacterDisplayLocator / FreeCompanyDisplayLocator

Stato corrente:
- ricevono `ExecutionInstruction`;
- traducono soltanto stack fisici già materializzati → pagina/slot visibile;
- non leggono `InventoryIndex`;
- non scelgono quantità;
- non ordinano o ripianificano stack.

### ExecutionInventoryHighlighter

Stato corrente:
- consuma `ExecutionInstruction` e locator passivi;
- non legge `InventoryIndex` per ricostruire il dominio;
- ODR può aggiornare solo la traduzione visiva.

### RetainerListHighlighter

Modello corretto:
- legge soltanto il Retainer target dall'ExecutionRuntime e lo mostra.

### ExecutionQuantityOverlay

Modello corretto:
- rendering passivo delle quantità già decise.

### ExecutionWindow

Deve essere solo vista.

Stato corrente:
- mostra `PlannerDecision` logiche;
- solo la decisione corrente riceve una `ExecutionInstruction` fisica;
- distingue quantità pianificata da quantità residua;
- non ricostruisce stack dal piano fisico.

## 13. ODR e ordine visibile

ODR è presentation/execution ordering, non criterio strategico.

Regole:

- il planner decide WHAT/WHERE;
- l'ordine visibile può decidere HOW TO PRESENT/EXECUTE elementi commutabili;
- ODR non deve cambiare source, quantità o score strategico;
- fallback deterministico se ODR non disponibile.

La traduzione Retainer fisico → pagina/slot visibile tramite `RetainerSortOrder.InventoryCoords` è valida e runtime verificata.

## 14. Lifecycle

Ogni event handler, hook, timer, task e CTS deve avere:

- owner unico;
- subscribe/unsubscribe simmetrico;
- cleanup best-effort;
- nessun fallimento di cleanup deve impedire gli altri.

No force push, no stash/pop cieco, no cambio branch/path/procedura silenzioso.

## 15. Stato runtime realmente verificato

RUNTIME VERIFICATO:

- FC observation/data-sync stabile;
- CharacterInventory RAW observation;
- Retainer relocation interna non produce falso verify/replan nel caso testato;
- reale Retainer → CharacterInventory single-stack;
- FC → CharacterInventory nei casi testati;
- planner manuale non viene cancellato da `Runtime.Clear`;
- capacity/stack simulation nei casi già testati;
- highlight ODR/retainer/character/FC nelle casistiche confermate prima dei bug split più recenti.

NON considerare runtime verificato:

- soluzione definitiva split/merge/relocation multi-stack;
- PR #21 come soluzione del problema logico NQ 10;
- preservazione esplicita dello storico Verified attraverso replan;
- CharacterInventory → FC end-to-end recente;
- rapid FC owner switch completo.

## 16. Audit aperto — ordine corretto

Prima di nuove feature:

Completato a livello BUILD/CI:
- contratto Planning → Execution separato tra `Actions`, `Decisions`, `ExecutionInstruction`;
- materializzazione runtime posseduta da Execution;
- locator/highlighter/window resi passivi;
- Session/Guard/Runtime migrati alle decisioni logiche;
- scoring `TransferHops`, `SourcePriority`, `Alphabetical` reso logico;
- `QualityFallback` rimosso dallo score;
- ordine delle sette priorità reso canonico e immutabile.

Restante, in ordine:
1. eliminare la doppia ownership Retainer observation;
2. ridurre/eliminare la pipeline planner legacy duplicata;
3. preservare progress Verified attraverso replan in modo esplicito;
4. rimuovere/isolare diagnostica storica non più necessaria;
5. verificare gli edge case FC ancora aperti (freshness pagina/owner switch rapido);
6. ridurre logica di dominio ancora presente in `MainWindow`;
7. runtime test completo del nuovo execution contract prima di nuove feature/automazione.

## 17. Regola finale per ogni modifica

Prima di scrivere codice chiedere:

1. Quale modulo possiede questa responsabilità?
2. Sto correggendo una causa o compensando un sintomo?
3. Sto duplicando una decisione già presa altrove?
4. La modifica rende FROG più semplice o più difficile da ragionare?
5. Split/merge/sort/relocation innocui restano innocui?
6. Planner, Execution, Verifier e UI restano separati?
7. Posso rimuovere codice invece di aggiungerne?

Se una risposta non è chiara, non modificare ancora il codice.
