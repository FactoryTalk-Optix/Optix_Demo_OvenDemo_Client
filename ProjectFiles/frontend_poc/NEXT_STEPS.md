# Oven Demo Client — Prossimi step

Stato: PoC HTML completato e schermata di binding + dashboard trasferite in Optix.

---

## Sprint 1 · Data quality  _(priorità: ALTA — impatto visivo immediato)_

- [ ] **Limitare i decimali** su tutti i valori numerici (max 1-2 cifre decimali)
  - Temperatura: `388.4 °F` ✓
  - Gas: `882.9 ft³/h` (non `882.866667`) ✓&nbsp;— applicato nel PoC, riprodurre in Optix
- [ ] **Unità coerenti** — scegliere un sistema e applicarlo ovunque
  - Temperatura: solo `°F` o solo `°C`
  - Gas: `ft³/h` oppure `Nm³/h` (non entrambe)
  - Fan: `%` con spazio prima del simbolo
- [ ] **Formato date** uniforme: `Apr 9, 2026, 1:03:08 PM` (Intl.DateTimeFormat in WebBrowser, Optix `DateTimeOffsetToString` in HMI)
- [ ] **Valori null / no-data** → mostrare `—` (trattino em), non `0` o stringa vuota

---

## Sprint 2 · Layout e grid  _(priorità: MEDIA)_

- [ ] **Grid 4+8**: colonna forno a 4 colonne (più compatta), dati a 8 colonne
- [ ] **Altezza uniforme** delle card dati (minima 340 px o via CSS `align-items: stretch`)
- [ ] **Fascia consumi full-width** oppure 2 col 50/50 come nel PoC
- [ ] **Badge row**: normalizzare larghezza e baseline
  - Heating / Idle sempre con colore semantico (ok/warn)
  - "Demo" come badge neutro fisso
- [ ] **Margine testata**: aggiungere `padding-top` al primo elemento sotto l'header Optix

---

## Sprint 3 · Design tokens in Optix  _(priorità: MEDIA)_

- [ ] Creare `StyleSheet` globale con le variabili brand `#235d9f`
  - Sfumatura primaria: `Brand700 = #235d9f`
  - Testo principale: `TextPrimary = #16304d`
  - Testo secondario: `TextSoft = #5f7690`
  - Background card: `SurfaceWhite = #ffffff`
  - Background pagina: `PageBG = #f3f7fc`
  - Linea separatore: `DividerLine = #d8e4f0`
- [ ] Applicare variabili ai componenti esistenti invece di colori fissi

---

## Sprint 4 · Componentizzazione  _(priorità: MEDIA)_

- [ ] **`StatusPill`** — tipo riusabile con proprietà `Status (enum: ok / warn / danger)` e `Label (string)`
  - Varianti: verde = Running/Heating, arancio = Idle/Alarm, rosso = Error
- [ ] **`MetricCard`** — tipo con `Label`, `Value`, `Unit` (template singolo valore grande + etichetta)
- [ ] **`DataRow`** — tipo con `Label` a sinistra, `Value` a destra (per le card processo/config)
- [ ] **`SectionCard`** — wrapper con padding uniforme e ombra coerente
- [ ] **`PrimaryButton`** — colore brand, border-radius 12, hover con `Brand800`

---

## Sprint 5 · Binding flow completo in Optix  _(priorità: ALTA per uso reale)_

- [ ] **Variabili retentive** da creare nel model:
  - `IsBound` (bool, retentive)
  - `BoundLocationId` (string, retentive)
  - `BoundAtUtc` (DateTime, retentive)
  - `BoundBy` (string, retentive)
- [ ] **Startup gate**: NetLogic `RuntimeInit`
  ```csharp
  if (IsBound.Value == false)
      Session.CurrentUser = "Anonymous";
      // navigate to BindingSetup screen
  else
      // navigate to ClientDashboard screen
  ```
- [ ] **BindingSetup screen in Optix**:
  - ComboBox legato alla lista location dal server model
  - Button "Confirm" → scrive le variabili retentive + naviga a Dashboard
  - Disabilitare ComboBox dopo il binding (controllo `!IsBound`)
- [ ] **Lock utente standard**: nascondere pulsanti di modifica tramite `Visibility` legata a `CurrentUser.Roles`
- [ ] **Admin page** con Unbind/Rebind:
  - Accessibile solo con ruolo `Admin`
  - Al click Unbind → `IsBound = false`, naviga a BindingSetup
  - Aggiungere entry audit nel log (NetLogic + Logger)

---

## Sprint 6 · Stati operativi  _(priorità: MEDIA — necessario per demo robusta)_

- [ ] **Loading state**: spinner / shimmer mentre i tag non hanno ancora valori
- [ ] **No data / disconnected**: icona offline, messaggio `Server not reachable`
- [ ] **Alarm state**: cambio colore header in `danger`, badge `ALARM` rosso lampeggiante
- [ ] **Idle state**: LED verde → grigio, badge "Idle" arancio
- [ ] **Binding required**: bloccare la UI e mostrare overlay se `IsBound = false` a runtime

---

## QA Checklist pre-demo  _(spuntare prima di ogni demo live)_

### Scaling
- [ ] Layout corretto a **100%**
- [ ] Layout corretto a **125%** (impostazione Windows comune)
- [ ] Layout corretto a **150%**

### Tema
- [ ] Light mode (sistema Windows: tema chiaro)
- [ ] Dark mode (sistema Windows: tema scuro)
- [ ] Toggle manuale light → dark funziona e persiste al reload
- [ ] Toggle manuale dark → light funziona e persiste al reload

### Dati
- [ ] Tutti i numeri hanno max 1-2 decimali
- [ ] Unità mostrate uniformemente
- [ ] Date leggibili nel formato locale scelto
- [ ] Nessun valore mostra `null`, `undefined`, `NaN` o la stringa vuota

### Binding
- [ ] Prima apertura senza `IsBound`: schermata binding appare
- [ ] Dopo Confirm: Dashboard appare e binding è memorizzato
- [ ] Reload pagina: dashboard appare direttamente (binding non perso)
- [ ] Utente standard non può aprire pagina Unbind
- [ ] Admin può eseguire Unbind

### Edge cases
- [ ] Server offline → messaggio appropriato, nessun crash
- [ ] Valore temperatura `null` → mostra `—`
- [ ] Valore fan `0` → mostra `0 %` (non `—`)
- [ ] Nome macchina molto lungo (>30 char) → non rompe il layout

---

## Note architetturali

| Area | Decisione consigliata |
|---|---|
| Unità temperatura | `°F` (già in Optix) — uniformare anche le etichette |
| Unità gas | `ft³/h` (già in Optix) — formattare a max 1 decimale |
| Formato data | `MMM d, yyyy, h:mm:ss tt` (Optix `DateTimeOffsetToString`) |
| Retentive storage | Tag nel model con `Retentive = true` — no file separato |
| Routing screens | `Session.CurrentPage` da NetLogic `RuntimeInit` |
| Admin detection | `Session.CurrentUser.BelongsToGroup("Administrator")` |
