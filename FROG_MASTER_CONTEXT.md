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
- **"non usato oggi" non significa "obsoleto"**: prima di eliminare codice classificare uso attuale, compatibilità, diagnostica e possibile ruolo futuro;
- se il ruolo futuro di un componente non è ricostruibile con certezza da codice/documentazione/storia Git, non eliminarlo senza chiedere;
- preferire preservare una API dormiente compatibile piuttosto che cancellarla solo perché non ha consumer attuali;
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
- Master corrente al 2026-09-23: `5301209166b685b3f89fc7c5b539fd0bb409c7a1`

Workflow modifiche:

1. leggere `master` reale;
2. verificare dipendenze, ownership e conseguenze;
3. se cambia la logica, spiegare il cambiamento all'utente prima di applicarlo;
4. creare branch dedicata;
5. applicare modifiche minime e coerenti;
6. controllare diff completo;
7. aprire PR;
8. attendere CI verde;
9. merge normale, mai force push;
10. test locale/runtime separato.

Branch sperimentali non mergiate non sono soluzione.

## 3. Obiettivo principale di FROG

FROG deve portare il Main Character il più vicino possibile al fabbisogno richiesto usando ciò che sa realmente esistere, producendo un piano valido e poi accompagnando/verificando l'esecuzione senza confondere:

- stato osservato;
- stato conosciuto;
- stato pianificato;
- decisione da eseguire;
- istruzione fisica corrente;
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

Owner principale: `GlobalTransferPlanner` e suoi componenti.

Input:

- `RequirementSet`;
- snapshot immutabile di inventory/capacity;
- `ResolutionPolicy`;
- priorità canoniche.

Responsabilità:

- calcolare quanto manca;
- scegliere owner/storage;
- scegliere route valide;
- rispettare capacity;
- pianificare switch;
- ottimizzare secondo le priorità canoniche.

Contratto attuale:

- `PlannerPlan.Actions` = simulazione/evidenza fisica interna al planner;
- `PlannerPlan.Decisions` = contratto logico destinato all'Execution;
- il dettaglio fisico può influire dove semanticamente corretto (es. ConsumedStacks), ma non deve trasformarsi automaticamente in comando permanente di Execution;
- `ExecutionOrderCompiler.WithActions` può riordinare evidenza fisica commutabile senza riscrivere `Decisions`.

### 4.3 Execution — "Come eseguo adesso ciò che il planner ha deciso?"

Owner principali:

- `PlanExecutionRuntime`
- `PlanExecutionCoordinator`
- `PlanExecutionSession`
- `PlanExecutionMaterializer`

Execution traduce una `PlannerDecision` logica sullo stato fisico corrente.

Responsabilità:

- prendere la decisione logica corrente;
- materializzarla sugli stack osservati adesso;
- produrre una sola `ExecutionInstruction` autorevole;
- rimaterializzare guida fisica quando split/merge/sort/relocation cambiano il layout ma non la quantità logica;
- per trasferimenti parziali mantenere il baseline logico originario e materializzare soltanto la quantità residua;
- avanzare solo dopo Verification.

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
- verifica su quantità logiche della decisione;
- source-only non completa;
- source decrease e destination increase devono essere coerenti;
- trasferimento parziale coerente resta in attesa e può continuare con instruction residua;
- relocation/split/merge interno non è un MOVE.

Verification non decide il nuovo piano.

### 4.5 Reconciliation — "Realtà e previsione coincidono?"

Owner principale: `PlanExecutionReconciler`.

Responsabilità:

- classificare delta osservati;
- determinare quantità realmente trasferita;
- calcolare residuo;
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

Requisito ancora aperto:
- preservare esplicitamente la cronologia/progress Verified attraverso una nuova sessione di replan, non solo il risultato materiale già presente nell'InventoryIndex.

### 4.7 Presentation

Locator, highlighter, overlay e finestre sono consumatori passivi.

Locator:
- ricevono `ExecutionInstruction.SourceStacks`;
- traducono coordinate fisiche → coordinate visibili;
- non decidono quantità;
- non scelgono stack;
- non ripianificano.

Highlighter/overlay/window:
- mostrano `ExecutionInstruction` e `PlannerDecision`;
- non ricostruiscono il dominio da `PlannerPlan.Actions` + `InventoryIndex`;
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

`InventoryRouteRules` è l'owner condiviso delle regole di accessibilità/route usate da planner ed execution preflight.

## 6. Priorità planner canoniche

Ordine lessicografico esatto:

1. CharacterSwitches
2. RetainerAccesses
3. ConsumedStacks
4. TransferHops
5. SourcePriority
6. Freshness
7. Alphabetical

Mai weighted e non riordinabili a runtime.

Stato attuale:

- `OptimizationSettings` accetta solo l'ordine canonico;
- `QualityFallback` non precede più le sette priorità;
- `TransferHops` è calcolato sulle route logiche, non sui container fisici;
- `SourcePriority` è calcolato sulle source logiche usate;
- `Alphabetical` usa source logiche e non il container;
- `ConsumedStacks` resta volutamente fisico;
- `Freshness` resta basata sull'evidenza fisica iniziale usata dal piano.

PR #25: BUILD/CI VERIFICATA. Runtime planner post-refactor ancora da riconfermare.

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

`PlannerCapacitySnapshot` espone anche capacity per `PlannerLogicalSource` per il preflight Execution, riutilizzando lo stesso modello logico.

## 8. Observation corrente

### CharacterInventory

Owner: `CharacterInventoryObservationObserver`.

Usa `IGameInventory.InventoryChangedRaw` e aggiorna l'intero CharacterInventory tramite `SyncPlayerInventory()`.

Stato storico: RUNTIME VERIFICATO; non modificato dal refactor Execution.

### Retainer

Owner unico: `RetainerInventoryObservationObserver`.

Trigger:

- `ICharacterMonitor.OnActiveRetainerLoaded` → acquisizione iniziale;
- `IGameInventory.InventoryChangedRaw` → split/merge/relocation/cambi successivi.

Snapshot sempre letto RAW tramite `StorageReader.TryReadActiveRetainer`.

Il vecchio bridge `FrogInventoryStartup.OnInventoryChanged → SyncStorageSources` è stato rimosso; `InventoryMonitor` e `InventoryScanner` restano attivi per gli altri consumer CCL.

PR #27: BUILD/CI VERIFICATA. Nuova ownership singola ancora da RUNTIME VERIFICARE.

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

`FreeCompanyObservationProbe` è diagnostica storica ancora AutoActivate. Non eliminarla solo perché diagnostica: prima va chiarito se serve per future verifiche FC; eventualmente renderla opzionale/separata.

## 9. Stato del planner

Componenti validi da conservare:

- `GlobalTransferPlanner`
- `GlobalAllocationPlanner`
- `PlannerState`
- `PlannerActionValidator`
- `PlannerCapacitySnapshot`
- `PlannerPlanEvaluator`
- `GlobalPlannerCoordinator`
- `PlannerDecisionCompiler`

Contratto Planning → Execution implementato:

- le action fisiche del planner restano disponibili per score/audit/simulazione;
- le decisioni logiche aggregano container fisici della stessa route/item/qualità;
- esempio fisico `NQ 5 + NQ 3 + HQ 9 + NQ 2` sulla stessa route diventa decisione logica `NQ 10 + HQ 9`;
- ODR non modifica le decisioni logiche.

PR #23/#24: BUILD/CI VERIFICATA. Comportamento NQ10/split multi-stack ancora da RUNTIME VERIFICARE.

## 10. Resolver e pipeline legacy

Esiste ancora:

`ResolverCoordinator → TransferPlanner → RequirementResolver → TransferPlan`

oltre alla pipeline strategica:

`GlobalPlannerCoordinator → GlobalTransferPlanner → PlannerPlan`.

Non eliminare automaticamente la pipeline legacy.

Audit ancora richiesto:

- capire quali output sono diagnostica/resolution/source policy e quali duplicano realmente planning;
- ricostruire eventuale ruolo futuro da codice/documentazione/storia Git;
- una sola autorità finale deve decidere il piano strategico;
- `InventoryStackResolver` è stato esplicitamente preservato anche se oggi non mostra consumer, perché potrebbe essere scaffolding futuro;
- l'overload compatibile `ExecutionOrderCompiler.OrderStacksForExecution(InventorySource,...)` è stato preservato per lo stesso motivo.

## 11. Execution corrente

Componenti:

- `PlanExecutionRuntime`
- `PlanExecutionCoordinator`
- `PlanExecutionSession`
- `PlanExecutionMaterializer`
- `PlanExecutionVerifier`
- `PlanExecutionReconciler`
- `PlanExecutionPlanGuard`

Stato architetturale:

- Session traccia `PlannerDecision`, non chunk fisici;
- Materializer decide gli stack fisici correnti e produce `ExecutionInstruction`;
- Guard fa preflight della sola decisione corrente, non replay speculativo di tutto il futuro;
- route/accessibilità condivise con planner tramite `InventoryRouteRules`;
- Coordinator mantiene baseline logico e può rimaterializzare residui parziali;
- Runtime orchestra replan senza dipendere dalla UI.

PR #24: BUILD/CI VERIFICATA. Nuovo execution contract non ancora RUNTIME VERIFICATO end-to-end.

## 12. Presentation corrente

### RetainerDisplayLocator / CharacterDisplayLocator / FreeCompanyDisplayLocator

Ora sono presentation-only:

- input: `ExecutionInstruction`;
- output: pagina/slot/quantità visibile;
- non scelgono stack;
- non reinterpretano PlannerAction.

### ExecutionInventoryHighlighter

Consuma `ExecutionInstruction`, non ricostruisce il piano dall'InventoryIndex.

Refresh:

- nuova instruction → nuova chiave fisica;
- ODR change → refresh visuale.

### RetainerListHighlighter

Legge il Retainer target dalla decisione della current instruction.

### ExecutionQuantityOverlay

Rendering passivo delle quantità già decise.

### ExecutionWindow

Mostra:

- `PlannerDecision` logica;
- `ExecutionInstruction` fisica solo per la decisione corrente;
- quantità residua separata dalla quantità pianificata dopo partial move.

Non deve calcolare source/stack.

## 13. ODR e ordine visibile

ODR è execution/presentation ordering, non criterio strategico.

Regole:

- il planner decide WHAT/WHERE;
- Execution decide quali stack correnti materializzano la decisione;
- ODR può ordinare stack fisici equivalenti per guidare l'utente;
- ODR non cambia source logica, quantità o score strategico;
- fallback deterministico se ODR non disponibile.

La traduzione Retainer fisico → pagina/slot visibile tramite `RetainerSortOrder.InventoryCoords` era runtime verificata prima del refactor; va riconfermata insieme al nuovo ExecutionInstruction.

## 14. Lifecycle

Ogni event handler, hook, timer, task e CTS deve avere:

- owner unico;
- subscribe/unsubscribe simmetrico;
- cleanup best-effort;
- nessun fallimento di cleanup deve impedire gli altri.

No force push, no stash/pop cieco, no cambio branch/path/procedura silenzioso.

## 15. Future auto-movement — architettura prevista, NON implementata

L'automazione futura non deve cambiare la separazione dei moduli.

Requisiti previsti:

1. preflight per singola `ExecutionInstruction`;
2. un'azione gameplay alla volta;
3. verification prima dell'azione successiva;
4. timeout/manual-interference classification;
5. AutoRetainer IPC suppression/restore;
6. duplicate/idempotency protection;
7. native UI automation separata dalla logica di planner/execution.

UI command != successo. Il successo resta proprietà di Verification.

Non eliminare componenti apparentemente dormienti se potrebbero essere scaffolding per questa fase senza prima verificarne l'intento.

## 16. Stato runtime realmente verificato

RUNTIME VERIFICATO e non toccato dal refactor, salvo dove indicato:

- FC observation/data-sync storico;
- CharacterInventory RAW observation;
- reale Retainer → CharacterInventory single-stack sul vecchio execution contract;
- FC → CharacterInventory nei casi storici;
- planner manuale non cancellato da `Runtime.Clear`;
- capacity/stack simulation nei casi storici;
- ODR/highlight nelle casistiche storiche precedenti al nuovo ExecutionInstruction.

DA RUNTIME VERIFICARE SUL MASTER CORRENTE:

- contratto `PlannerDecision → ExecutionInstruction`;
- caso NQ10 distribuito su più container;
- split/merge/sort/relocation durante decisione corrente;
- partial move e rimaterializzazione del residuo;
- highlighter + DOVE sincronizzati alla stessa instruction;
- Retainer initial acquisition via `OnActiveRetainerLoaded` + cambi RAW;
- planner scoring post-PR #25;
- CharacterInventory → FC end-to-end;
- rapid FC owner switch completo;
- preservazione esplicita dello storico Verified attraverso replan.

## 17. Audit aperto — ordine corretto

Prima di nuove feature:

1. runtime test del nuovo contratto Planning → Execution;
2. audit della pipeline legacy Resolver/TransferPlanner senza eliminazioni speculative;
3. preservare progress Verified attraverso replan in modo esplicito;
4. verificare lifecycle/necessità futura di `FreeCompanyObservationProbe`;
5. riconfermare CharacterInventory → FC end-to-end;
6. riconfermare rapid FC owner switch;
7. solo dopo riprendere automazione o nuove feature.

## 18. Checklist prima di ogni modifica

Prima di scrivere codice chiedere:

1. Quale modulo possiede questa responsabilità?
2. Sto correggendo una causa o compensando un sintomo?
3. Sto duplicando una decisione già presa altrove?
4. La modifica rende FROG più semplice o più difficile da ragionare?
5. Split/merge/sort/relocation innocui restano innocui?
6. Planner, Execution, Verifier e UI restano separati?
7. Sto per eliminare qualcosa solo perché oggi non è usato?
8. Esiste evidenza che quel codice sia davvero obsoleto e non scaffolding futuro?
9. Posso preservare compatibilità senza compromettere gli invarianti?

Se una risposta non è chiara, non modificare ancora il codice.
