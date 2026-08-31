# Oven Client Frontend Refresh Plan

## Goal
Modernize only the client-side visual experience for the Oven demo, keeping backend behavior and data model intact.

## Current Data Anchors (from project model)
- Oven identity: OvenName, OvenId
- Process status: CurrentPhase, CycleTime, CurrentRecipe
- Thermal state: CurrentTemperature, CurrentStatus.Setpoint, CurrentStatus.Heating
- Actuators: CurrentStatus.InputFan, CurrentStatus.OutputFan
- Consumption: PowerConsumption, GasConsumption

## Suggested rollout
1. Visual language freeze
- Validate color system, typography, card style, and component spacing with PoC pages.
- Freeze a small token set before implementing in Optix styles.

2. Client screen refactor order
- Screen A: Live Dashboard (high value for demos)
- Screen B: Recipe Process Timeline (best for process storytelling)
- Screen C: Stats and Consumption (manager view)

3. Optix implementation mapping
- PoC card => Rectangle/Panel with shared style class
- Metric tiles => Label + Value + optional trend icon
- Timeline => VerticalContainer with repeated phase cards
- Progress bar => Rectangle fill width dynamic link (percentage)
- Thermal gauge (PoC style) => CircularProgressBar or custom SVG panel

4. Interaction standards
- Keep operator actions visible in top toolbar.
- Keep status always visible: phase, recipe, cycle elapsed, heating state.
- Use color semantics consistently: normal, warning, critical.

5. Demo readiness checks
- Large display readability (distance 2-3m)
- Tablet layout readability
- Alarm and warning visibility in <1 second scanning

## First binding scenario (client first startup)
1. Startup gate
- On startup, check retained variable IsBound.
- If false, force open BindingSetup screen and block normal dashboard navigation.

2. Slot selection
- Show only free slots (provided by server sync/model list).
- User selects one slot and confirms binding.

3. Retentive commit
- Persist: IsBound=true, BoundLocationId, BoundSlotId, BoundAtUtc, BoundBy.
- Optionally persist ClientGuid for traceability.

4. Post-bind behavior
- After successful save, redirect to dashboard.
- In normal user role, binding controls are hidden/disabled.

5. Admin override
- Admin settings page exposes Unbind/Rebind actions.
- Any change writes audit log entry (timestamp, user, old/new slot).

6. Failure handling
- If save fails, remain in setup and show clear error message.
- If selected slot becomes occupied before save, reject and refresh free slots list.

## PoC files in this folder
- index.html
- client-dashboard.html
- recipe-process.html
- stats-analytics.html
- binding-setup.html
- styles.css
- app.js

## How to use
Open index.html in browser and review visuals with your team.
Then replicate accepted patterns in Optix UI Types and Screens incrementally.
