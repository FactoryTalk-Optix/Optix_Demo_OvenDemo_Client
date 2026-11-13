#region Using directives
using System;
using UAManagedCore;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.UI;
using FTOptix.System;
using OpcUa = UAManagedCore.OpcUa;
#endregion

/// <summary>
/// Oven process simulation. Phases (PreHeat, Bake, Cooldown) are read every tick from the recipe target
/// (Model/RecipeTarget/OvenRecipeTarget), so applying a recipe changes the oven behaviour from the next tick.
/// State is published once per second into the OvenType instance Model/OvenData/&lt;OvenId&gt;
/// (created by EdgeOnbordingLogic), which the MQTT publisher sends to the supervisor.
/// </summary>
public class OvenSimulationLogic : BaseNetLogic
{
    private const string RecipeTargetPath = "Model/RecipeTarget/OvenRecipeTarget";
    private const string OvenIdPath = "Model/HMISettings/DeviceId";
    private const string OvenDataPath = "Model/OvenData";

    private const int TickMs = 250;
    private const int TicksPerSecond = 1000 / TickMs;
    private const double Ambient = 20.0;
    private const double Deadband = 2.0;
    private const double FastCoolPerTick = 2.0;
    private const string AlarmsPath = "Alarms";
    private const string AlarmInputsPath = "NetLogic/alarmSimulationLogic/alarmSimulationArray";
    private const string AlarmStatusPath = "Model/AlarmStatus";
    private const double HeaterKw = 12.0;
    private const double FanKwPerPercent = 0.02;

    /// <summary>True while a cycle is running; recipe application is deferred to the cycle boundary.</summary>
    public static volatile bool CycleRunning;

    private PeriodicTask task;
    private DateTime startedUtc;
    private int phaseIndex;
    private int phaseTicks;
    private int cycleTicks;
    private volatile bool startRequested;
    private volatile bool stopRequested;
    private int tickCounter;
    private bool idle;
    private bool heaterOn;
    private double temperature;
    private uint cycles;
    private double energyKwh;
    private string counterEpoch;
    private uint goodCycles;
    private uint rejectedCycles;
    private double runningSeconds;
    private double idealSeconds;
    private double idealCycleSeconds;
    private bool rejectCycle;
    private DateTime dailyDate;
    private uint dailyCycles;
    private double dailyEnergyKwh;
    private IUAVariable rejectNextCycle;
    private IUAVariable pineapples;
    private IUAVariable uptimeText;
    private IUAVariable activeAlarmCount;
    private IUAVariable hasActiveAlarms;

    public override void Start()
    {
        startedUtc = DateTime.UtcNow;
        dailyDate = startedUtc.Date;
        counterEpoch = Guid.NewGuid().ToString("N");
        cycles = 0;
        goodCycles = 0;
        rejectedCycles = 0;
        runningSeconds = 0;
        idealSeconds = 0;
        energyKwh = 0;
        dailyCycles = 0;
        dailyEnergyKwh = 0;
        EnsureAnalyticsFields(Project.Current.Get("Model/Types/OvenType"));
        var settings = Project.Current.Get("Model/HMISettings");
        rejectNextCycle = EnsureVariable(settings, "RejectNextCycle", OpcUa.DataTypes.Boolean);
        pineapples = EnsureVariable(LogicObject, "PineapplesOnPizza", OpcUa.DataTypes.Boolean);
        uptimeText = EnsureVariable(LogicObject, "UptimeText", OpcUa.DataTypes.String);
        activeAlarmCount = Project.Current.GetVariable(AlarmStatusPath + "/ActiveAlarmCount");
        hasActiveAlarms = Project.Current.GetVariable(AlarmStatusPath + "/HasActiveAlarms");
        if (activeAlarmCount == null || hasActiveAlarms == null)
            Log.Warning("OvenSimulationLogic", $"'{AlarmStatusPath}' not found: run DesignTimeNetLogic1.BuildAlarmStatus.");
        pineapples.Value = false;
        uptimeText.Value = "0.00:00:00";
        pineapples.VariableChange += OnPineapplesChanged;
        SetPineappleAlarm(false);
        temperature = Ambient;
        idle = true;
        startRequested = false;
        stopRequested = false;
        CycleRunning = false;
        task = new PeriodicTask(Tick, TickMs, LogicObject);
        task.Start();
    }

    /// <summary>Requests the start of a cycle; executed by the simulation task on the next tick.</summary>
    [ExportMethod]
    public void StartCycle()
    {
        if (!idle)
        {
            Log.Info("OvenSimulationLogic", "StartCycle ignored: a cycle is already running");
            return;
        }
        startRequested = true;
    }

    /// <summary>Aborts the running cycle; the oven goes back to idle on the next tick.</summary>
    [ExportMethod]
    public void StopCycle()
    {
        if (idle) return;
        stopRequested = true;
    }

    /// <summary>Single dashboard button: starts an idle oven, aborts a running one.</summary>
    [ExportMethod]
    public void ToggleCycle()
    {
        if (idle) StartCycle();
        else StopCycle();
    }

    public override void Stop()
    {
        CycleRunning = false;
        task?.Dispose();
        task = null;
        if (pineapples != null) pineapples.VariableChange -= OnPineapplesChanged;
    }

    private void OnPineapplesChanged(object sender, VariableChangeEventArgs e) => SetPineappleAlarm(e.NewValue.Value is bool on && on);

    // Drives the first alarm input (PineapplesOnPizza) of alarmSimulationLogic, which stays in manual mode.
    private static void SetPineappleAlarm(bool active)
    {
        var array = Project.Current.GetVariable(AlarmInputsPath);
        if (array == null || !(array.Value.Value is bool[] inputs) || inputs.Length == 0) return;
        var copy = (bool[])inputs.Clone();
        copy[0] = active;
        array.Value = new UAValue(copy);
    }

    private void Tick()
    {
        var target = Project.Current.Get<OvenRecipe>(RecipeTargetPath);
        if (target == null) return;

        var phases = new[] { target.PreHeat, target.Bake, target.Cooldown };
        var phase = idle ? null : phases[phaseIndex];

        if (idle)
        {
            temperature = Math.Max(temperature - FastCoolPerTick, Ambient);
            heaterOn = false;
            if (startRequested)
            {
                startRequested = false;
                if (HasRunnableRecipe(phases))
                {
                    idealCycleSeconds = 0;
                    foreach (var recipePhase in phases) idealCycleSeconds += recipePhase.Duration;
                    rejectCycle = rejectNextCycle.Value.Value is bool rejected && rejected;
                    rejectNextCycle.Value = false;
                    idle = false;
                    phaseIndex = 0;
                    phaseTicks = 0;
                    cycleTicks = 0;
                    CycleRunning = true;
                    Log.Info("OvenSimulationLogic", "Cycle started");
                }
                else
                {
                    Log.Warning("OvenSimulationLogic", "StartCycle ignored: the recipe has a phase with zero duration (apply a recipe first)");
                }
            }
        }
        else if (stopRequested)
        {
            stopRequested = false;
            idle = true;
            heaterOn = false;
            CycleRunning = false;
            Log.Info("OvenSimulationLogic", "Cycle aborted");
        }
        else
        {
            RunPhase(phase);
            runningSeconds += 1.0 / TicksPerSecond;
            phaseTicks++;
            cycleTicks++;
            if (phaseTicks >= phase.Duration * TicksPerSecond)
            {
                phaseTicks = 0;
                phaseIndex++;
                if (phaseIndex >= phases.Length)
                {
                    cycles++;
                    ResetDailyCounters();
                    dailyCycles++;
                    if (rejectCycle) rejectedCycles++;
                    else goodCycles++;
                    idealSeconds += idealCycleSeconds;
                    idle = true;
                    heaterOn = false;
                    CycleRunning = false;
                    Log.Info("OvenSimulationLogic", $"Cycle #{cycles} completed");
                }
            }
        }

        tickCounter++;
        if (tickCounter % TicksPerSecond == 0)
        {
            UpdateAlarmStatus();
            Publish(idle ? null : phases[Math.Min(phaseIndex, phases.Length - 1)]);
        }
    }

    private static bool HasRunnableRecipe(OvenPhase[] phases)
    {
        foreach (var p in phases)
        {
            if (p.Duration == 0) return false;
        }
        return true;
    }

    private void RunPhase(OvenPhase phase)
    {
        var setpoint = phase.TemperatureSetpoint;
        if (phase.Heating)
        {
            if (!heaterOn && temperature < setpoint - Deadband) heaterOn = true;
            else if (heaterOn && temperature >= setpoint + Deadband) heaterOn = false;

            if (heaterOn) temperature = Math.Min(temperature + Random.Shared.NextDouble() * 5 + 5, setpoint + Deadband);
            else temperature = Math.Max(temperature - (Random.Shared.NextDouble() * 0.25 + 0.125), Ambient);
        }
        else
        {
            heaterOn = false;
            temperature = Math.Max(temperature - FastCoolPerTick, Math.Max(setpoint, Ambient));
        }
    }

    private void Publish(OvenPhase phase)
    {
        var deviceId = Convert.ToString(Project.Current.GetVariable(OvenIdPath)?.Value.Value);
        if (string.IsNullOrEmpty(deviceId)) return;
        var data = Project.Current.Get(OvenDataPath)?.Get(deviceId);
        if (data == null) return;

        EnsureAnalyticsFields(data);
        ResetDailyCounters();
        var now = DateTime.UtcNow;
        var fans = phase == null ? 0.0 : phase.FanInRamp + phase.FanOutRamp;
        var powerKw = (heaterOn ? HeaterKw : 0.0) + fans * FanKwPerPercent;
        energyKwh += powerKw / 3600.0;
        dailyEnergyKwh += powerKw / 3600.0;

        Set(data, "DeviceId", deviceId);
        Set(data, "ExecutionState", idle ? 0 : phaseIndex + 1);
        Set(data, "Temperature", Math.Round(temperature, 1));
        Set(data, "TemperatureSetpoint", phase == null ? Ambient : phase.TemperatureSetpoint);
        Set(data, "CurrentBatchId", idle ? string.Empty : $"{deviceId}-{cycles + 1:D5}");
        Set(data, "CurrentPhase", idle ? 0 : phaseIndex + 1);
        Set(data, "PhaseElapsedSeconds", idle ? 0.0 : (double)phaseTicks / TicksPerSecond);
        Set(data, "ActiveAlarmCount", CountActiveAlarms());
        OvenCycleTrace.Record(deviceId, !idle, (double)cycleTicks / TicksPerSecond, temperature);
        Set(data, "DoorOpen", false);
        Set(data, "CyclesCompletedToday", dailyCycles);
        Set(data, "PowerDraw", powerKw);
        Set(data, "EnergyConsumedToday", dailyEnergyKwh);
        Set(data, "EnergyConsumedTotal", energyKwh);
        Set(data, "CyclesCompletedTotal", (double)cycles);
        Set(data, "GoodCyclesTotal", (double)goodCycles);
        Set(data, "RejectedCyclesTotal", (double)rejectedCycles);
        Set(data, "RunningSecondsTotal", runningSeconds);
        Set(data, "IdealProductionSecondsTotal", idealSeconds);
        Set(data, "CounterEpoch", counterEpoch);
        var uptime = (int)(now - startedUtc).TotalSeconds;
        Set(data, "UptimeSeconds", uptime);
        uptimeText.Value = TimeSpan.FromSeconds(uptime).ToString(@"d\.hh\:mm\:ss");
        Set(data, "Timestamp", now);
    }

    private void ResetDailyCounters()
    {
        var today = DateTime.UtcNow.Date;
        if (dailyDate == today) return;
        dailyDate = today;
        dailyCycles = 0;
        dailyEnergyKwh = 0;
    }

    private static void EnsureAnalyticsFields(IUANode target)
    {
        if (target == null) return;
        foreach (var name in new[] { "EnergyConsumedTotal", "CyclesCompletedTotal", "GoodCyclesTotal", "RejectedCyclesTotal", "RunningSecondsTotal", "IdealProductionSecondsTotal" })
            EnsureVariable(target, name, OpcUa.DataTypes.Double);
        EnsureVariable(target, "CounterEpoch", OpcUa.DataTypes.String);
    }

    private static IUAVariable EnsureVariable(IUANode parent, string name, NodeId dataType)
    {
        var variable = parent.GetVariable(name);
        if (variable != null) return variable;
        variable = InformationModel.MakeVariable(name, dataType);
        parent.Add(variable);
        return variable;
    }

    private void UpdateAlarmStatus()
    {
        if (activeAlarmCount == null || hasActiveAlarms == null) return;
        var count = CountActiveAlarms();
        if (Convert.ToUInt32(activeAlarmCount.Value.Value) != count) activeAlarmCount.Value = count;
        var any = count > 0;
        if (!(hasActiveAlarms.Value.Value is bool current) || current != any) hasActiveAlarms.Value = any;
    }

    // An off-normal controller is active when its input differs from the normal state.
    private static uint CountActiveAlarms()
    {
        var alarms = Project.Current.Get(AlarmsPath);
        if (alarms == null) return 0;

        uint count = 0;
        foreach (var alarm in alarms.Children)
        {
            var input = alarm.GetVariable("InputValue");
            var normal = alarm.GetVariable("NormalStateValue");
            if (input == null || normal == null) continue;
            if (Convert.ToDouble(input.Value.Value) != Convert.ToDouble(normal.Value.Value)) count++;
        }
        return count;
    }

    private static void Set(IUANode data, string name, object value)
    {
        var variable = data.GetVariable(name);
        if (variable != null) variable.Value = new UAValue(value);
    }
}
