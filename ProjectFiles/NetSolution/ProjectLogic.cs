#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.CoreBase;
using FTOptix.NativeUI;
using FTOptix.Retentivity;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.MQTTClient;
using FTOptix.EventLogger;
using FTOptix.DataLogger;
using FTOptix.OPCUAServer;
using FTOptix.Alarm;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.System;
using System.Collections.Generic;
#endregion

public class ProjectLogic : BaseNetLogic
{
    public override void Start()
    {
        GetNetworkInterfaces();
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    private const string DeviceIdPrefix = "OVEN-";

    // Call from a single "Execute method" event action: bind targetVariable with a Dynamic
    // Link to the retentive device-id variable (append "@NodeId" to the link to pass the
    // pointer, not the value). Does nothing if the variable already holds an id, so it is
    // safe to call again after a restart without overwriting the stored value.
    [ExportMethod]
    public void EnsureDeviceIdToVariable(NodeId targetVariable)
    {
        var variable = InformationModel.GetVariable(targetVariable);
        if (variable == null)
        {
            Log.Error("ProjectLogic", "EnsureDeviceIdToVariable: targetVariable not found");
            return;
        }

        if (!string.IsNullOrEmpty((string)variable.Value))
            return;

        var shortId = Guid.NewGuid().ToString("N").Substring(0, 5).ToUpperInvariant();
        variable.Value = DeviceIdPrefix + shortId;
    }
    public static List<string> GetNetworkInterfaces()
    {
        var interfaces = new List<string>();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var info = $"{ua.Address}";
                    Log.Info(info);
                    interfaces.Add(info);
                }
            }
        }
        return interfaces;
    }
}
