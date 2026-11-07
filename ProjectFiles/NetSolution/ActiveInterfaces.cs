#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.NativeUI;
using FTOptix.Alarm;
using FTOptix.RecipeX;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTClient;
using FTOptix.Retentivity;
using FTOptix.DataLogger;
using FTOptix.Core;
using FTOptix.System;
#endregion

public class ActiveInterfaces : BaseNetLogic
{
    public override void Start()
    {
        var interfaces = ProjectLogic.GetNetworkInterfaces();
        Log.Info($"{string.Join("; ", interfaces)}");
        if (Owner is not Label label)
            return;
        label.Text = $"{string.Join("; ", interfaces)}";
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }
}
