# Oven Demo - Oven Edge (Client)

**FactoryTalk Optix 1.8 | MQTT | RecipeX | SQLite | ECharts | OPC UA | 6 languages | Light/Dark theme**

A simulated pizza oven with its own operator HMI. It runs a realistic bake cycle (PreHeat, Bake, Cooldown), shows temperature,
energy and alarms, keeps a local recipe book and can join a plant-wide supervisor with a single button: it publishes its
telemetry, receives approved recipes, and chats with the head office.

It is the "machine" half of the OvenDemo NextGen pair. The "head office" half is the
**Optix_Demo_OvenDemo_Server** project (see its `OvenDemo_Server_Overview.md`).

An oven works **on its own** as well: the simulation, the recipes, the alarms and the dashboard do not depend on the supervisor.

---

## 1. What the demo wants to show

| Message | How the demo proves it |
|---|---|
| The same Optix technology runs on the machine and in the cloud/office. | Edge HMI and supervisor are two Optix projects talking over open protocols. |
| A machine can be plugged into a fleet by an operator, not by an engineer. | Enter the supervisor address, press **Request onboarding**, wait for the acceptance. |
| The machine stays in charge. | Recipes arrive read-only from the supervisor; the operator on the oven decides when to apply them. |
| A rich, modern HMI without extra tooling. | Responsive dashboard, live cycle chart, light/dark theme, 6 languages. |
| Data model once, reused everywhere. | The same `OvenType` is published over MQTT and offered over OPC UA. |

---

## 2. Demo walkthrough (about 3-5 minutes)

1. **Dashboard** - the oven card shows state, current phase, temperature and setpoint, applied recipe, batch, uptime, cycles
   today and power. A live chart traces the actual temperature of the running cycle against the setpoint.
2. **Recipes** - open the library. Approved recipes can be applied; applying while a cycle is running takes effect at the end
   of the cycle.
3. **Start / Stop** - one button starts an idle oven and stops a running one. Watch the heater cycle on and off around the
   setpoint, then the oven cool down.
4. **Make something go wrong** - flip **Reject next cycle** (the finished batch counts as rejected and lowers the quality
   figure the supervisor computes) or **Put pineapples on pizza** (raises an alarm).
5. **Alarms** - active list and history, with priority filter.
6. **Settings** - the oven has an id like `OVEN-1A2B3`, generated at first start. Set the supervisor address and press
   **Request onboarding**. The status goes Pending -> Onboarded once the supervisor accepts.
7. **Chat** - answer the supervisor's message; delivery status updates on both screens.
8. **Offboarding** - **Force offboarding** resets the onboarding status on the oven side (useful when the oven was removed on the
   supervisor); the oven keeps working stand-alone.

---

## 3. Features

| Page | What it does |
|---|---|
| **Dashboard** | Responsive live view of the oven, KPI values, cycle chart (ECharts), Start / Stop button, quality-control switches. |
| **Alarms** | Active alarm list and alarm history with priority filter (All / High / Medium / Info). |
| **Logs** | Events viewer on the local event logger database. |
| **Recipes** | Recipe book (RecipeX): name, version, status (Draft, Approved, Deprecated), phase values. Read-only for the operator; apply an Approved recipe. |
| **Chat** | Instant messaging with the supervisor, persistent history, delivery status, unread counter. |
| **Settings** | Language, light/dark theme, oven identity, supervisor link (MQTT broker address, OPC UA endpoint, available networks), onboarding status. |

### Functions in detail

- **Process simulation** - three phases per recipe, each with temperature setpoint, duration, fan-in / fan-out ramps and a heating
  flag. The heater switches on and off around the setpoint with a dead band; the oven cools towards ambient when not heating.
  Simulated power is a 12 kW heater plus fans. The cycle ends automatically after the last phase.
- **Recipe-driven behavior** - the phases are read from the recipe target every tick, so applying a recipe changes the oven from
  the next tick on. Recipes are transferred with RecipeX (`TransferFromStoreToTarget`).
- **Production counters** - cycles, good / rejected cycles, energy, running time and ideal production time, with a counter epoch
  so the supervisor can tell a restart from a counter reset. They feed the supervisor's OEE page.
- **Alarms** - four simulated process alarms ("Pineapples on pizza", "Mozzarella being finished", "Pizza is being overcooked",
  "Pizza is ready") with auto-acknowledge. The pineapple alarm is driven by a dashboard switch, the others by the alarm
  simulation logic. The active alarm count is part of the telemetry.
- **Onboarding** - on the first start the oven generates its own id (`OVEN-` + 5 hex digits, kept in retentive storage). The
  onboarding request carries the id and the OPC UA endpoint of the oven; the supervisor answers with a status: none, pending,
  onboarded, declined or timeout.
- **Telemetry** - once onboarded, the oven publishes its `OvenType` snapshot periodically on the MQTT topic named after its id.
- **Recipes from the supervisor** - approved recipes arrive on `recipes/<id>/to-edge`, are validated (only *Approved* or
  *Deprecated* are accepted), stored read-only, acknowledged, and announced in a catalog message whenever the oven reconnects so
  that missing recipes are sent again. The oven reports back which recipe it applied.
- **Chat** - messages to the supervisor are queued while offline and sent on reconnect; delivery is confirmed by the supervisor's
  acknowledgement; duplicates are ignored; history is capped per conversation.
- **Localization and themes** - English, Spanish, Italian, French, German and Chinese; Light and Dark.

---

## 4. How it works

```text
+--------------------------------- OvenDemo Client ---------------------------------+
|                                                                                   |
|  HMI (WebUI http :8080 + Native)   Simulation (250 ms tick)   RecipeX + SQLite     |
|  Dashboard | Alarms | Logs |       PreHeat -> Bake -> Cooldown  recipes, chat,      |
|  Recipes | Chat | Settings         heater, fans, counters       event log          |
|                                                                                   |
|  OvenType (Model/OvenData/<id>) --- MQTT publisher --------------+                |
|  OPC UA server :59101 (publishes Model) -------------------------+---+            |
+-------------------------------------------------------------------|---|-----------+
                                                                    |   |
                                         MQTT broker :8883          |   |  OPC UA
                                       (on the supervisor)          v   v
                                                         OvenDemo Supervisor
```

### MQTT topics used by the oven

| Topic | Direction | Content |
|---|---|---|
| `OnboardingRequest` | oven -> supervisor | `{Sender, Content (OPC UA endpoint), Timestamp}` |
| `OnboardingResult` | supervisor -> oven | Onboarding status (0 none, 1 pending, 2 onboarded, 3 declined, 4 timeout) |
| `<DeviceId>` | oven -> supervisor | Telemetry (Optix Default JSON of `OvenType`) |
| `chat/<DeviceId>/to-edge`, `chat/<DeviceId>/to-supervisor` | both | Chat message / acknowledgement (QoS 1) |
| `recipes/<DeviceId>/to-edge`, `recipes/<DeviceId>/to-supervisor` | both | Recipe delivery, catalog (hello), apply result |

The MQTT client id of the oven is its `DeviceId`.

### Technology

| Area | Technology |
|---|---|
| Platform | FactoryTalk Optix Studio 1.8.0.555, .NET NetLogic (C#) |
| UI | WebUI + Native engines, responsive dashboard, StyleSheets Light/Dark, 6 languages |
| Messaging | `MQTTClient` with publisher (telemetry) and subscribers (onboarding, chat, recipes created at runtime) |
| Recipes | `RecipeX` with `OvenRecipe` target type (PreHeat / Bake / Cooldown) |
| Persistence | `SQLiteStore` (recipes, chat history, event logger), retentive oven id and settings |
| Alarms | `Alarm` module (`OffNormalAlarmController`) and `EventLogger` |
| Charts | Apache ECharts in a `WebBrowser` widget, served by a small loopback listener inside the runtime |
| Interop | `OPCUAServer` publishing the oven model |

### Where to look in the code

| File | Purpose |
|---|---|
| `ProjectFiles/NetSolution/OvenSimulationLogic.cs` | Thermal / cycle simulation, counters, telemetry publication |
| `EdgeOnbordingLogic.cs` | Oven id generation, `OvenData` instance management |
| `EdgeRecipesLogic.cs`, `RecipesLogic.cs`, `RecipeListLogic.cs` | Recipe reception, validation, apply and recipe book |
| `EdgeChatLogic.cs` | Chat history, queue, acknowledgements |
| `alarmSimulationLogic.cs` | Simulated process alarms |
| `EchartOvenDataLogic.cs`, `OvenCycleTrace.cs` | Live temperature chart |
| `DesignTimeNetLogic1.cs` | Design-time builders and fixes, run from Studio (not part of the runtime) |

---

## 5. Run it

**Prerequisites:** FactoryTalk Optix Studio 1.8.0.555 or later.

1. Open `Optix_Demo_OvenDemo_Client.optix` in Studio and start it (Emulator). The WebUI is on `http://<ip>:8080`.
2. Apply an Approved recipe in **Recipes** and press **Start** on the **Dashboard**.
3. To join a fleet: open **Settings**, set the supervisor's IP address, press **Request onboarding** and accept the request on the
   **Optix_Demo_OvenDemo_Server** (**Device registry**).

Running several clients on the same PC requires changing their ports (WebUI 8080, OPC UA 59101); on separate devices no change
is needed. Each oven needs its own id, generated automatically at first start.

---

## 6. Good to know

- Recipes are created and approved on the supervisor. The oven only receives, stores and applies them. A recipe that is not
  Approved cannot be applied.
- The demo link to the supervisor is anonymous and not encrypted (trade-show LAN). Production use needs TLS and authentication.
- Translations cover the main labels in six languages; some texts of this project are still English only, the supervisor project
  is the fully localized one.
