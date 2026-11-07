#region Using directives
using FTOptix.NetLogic;
using System;
using UAManagedCore;
using FTOptix.EventLogger;
using FTOptix.RecipeX;
using FTOptix.Retentivity;
using FTOptix.System;
#endregion

public class alarmSimulationLogic : BaseNetLogic
{
    public override void Start()
    {
        alarmSimulationTask = new PeriodicTask(AlarmSimulation, 5000, LogicObject);
        alarmSimulationTask.Start();
    }

    public override void Stop()
    {
        alarmSimulationTask.Dispose();
    }

    private void AlarmSimulation()
    {
        if (enabled)
        {
            var alarms = simulatedAlarms;
            Random rnd = new Random();
            for (int i = 0; i < alarms.Length; i++)
            {
                if (rnd.NextDouble() < 0.1)
                {
                    alarms[i] = !alarms[i];
                }
            }
            simulatedAlarms = alarms;
        }
    }

    /// <summary>Sets one of the simulated alarm inputs (0-based index). The random simulation should be disabled (enableSimulation = false).</summary>
    [ExportMethod]
    public void SetAlarm(int index, bool active)
    {
        var alarms = simulatedAlarms;
        if (index < 0 || index >= alarms.Length)
        {
            Log.Warning("alarmSimulationLogic", $"SetAlarm: index {index} out of range (0-{alarms.Length - 1})");
            return;
        }
        alarms[index] = active;
        simulatedAlarms = alarms;
    }

    [ExportMethod]
    public void ToggleAlarm(int index)
    {
        var alarms = simulatedAlarms;
        if (index < 0 || index >= alarms.Length)
        {
            Log.Warning("alarmSimulationLogic", $"ToggleAlarm: index {index} out of range (0-{alarms.Length - 1})");
            return;
        }
        alarms[index] = !alarms[index];
        simulatedAlarms = alarms;
    }

    [ExportMethod]
    public void ClearAlarms()
    {
        simulatedAlarms = new bool[simulatedAlarms.Length];
    }

    PeriodicTask alarmSimulationTask;
    private bool[] simulatedAlarms
    {
        get => LogicObject.GetVariable("alarmSimulationArray").Value;
        set => LogicObject.GetVariable("alarmSimulationArray").Value = value;
    }
    private bool enabled => LogicObject.GetVariable("enableSimulation").Value;
}
