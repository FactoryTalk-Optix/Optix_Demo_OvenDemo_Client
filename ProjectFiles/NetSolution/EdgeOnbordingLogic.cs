#region Using directives
using System;
using System.Linq;
using UAManagedCore;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.System;
#endregion

public class EdgeOnbordingLogic : BaseNetLogic
{
    public override void Start()
    {
        EnsureDeviceId();
        // Re-apply on restart in case RuntimeNodeId was already set retentively before this NetLogic started.
        SyncOvenDataWithRuntimeNodeId();
    }

    // On first start (nothing retained yet) DeviceId is empty and the dashboard would have no oven to show
    // until the user applies an OvenId in Settings: generate a default one, same format as the Apply button.
    private static void EnsureDeviceId()
    {
        var settings = Project.Current.Get("Model/HMISettings");
        var deviceId = settings?.GetVariable("DeviceId");
        if (deviceId == null || !string.IsNullOrEmpty((string)deviceId.Value))
            return;

        var shortId = Guid.NewGuid().ToString("N").Substring(0, 5).ToUpperInvariant();
        var ovenId = settings.GetVariable("OvenId");
        if (ovenId != null)
            ovenId.Value = shortId;
        deviceId.Value = "OVEN-" + shortId;
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    // Creates/renames the OvenType instance named after RuntimeNodeId under Model/OvenData.
    // Runs purely off RuntimeNodeId, independently of onboarding/supervisor connection state,
    // so a standalone oven can publish MQTT data as soon as its RuntimeNodeId is set.
    [ExportMethod]
    public void SyncOvenDataWithRuntimeNodeId()
    {
        // Start() and the DeviceId-changed event handler can run concurrently on first start.
        lock (SyncLock)
            SyncOvenData();
    }

    private static readonly object SyncLock = new object();

    private static void SyncOvenData()
    {
        var model = Project.Current.Get("Model");
        if (model == null)
        {
            Log.Error("EdgeOnbordingLogic", "SyncOvenDataWithRuntimeNodeId: Model node not found");
            return;
        }

        var runtimeNodeIdVariable = model.GetVariable("HMISettings/DeviceId");
        if (runtimeNodeIdVariable == null)
        {
            Log.Error("EdgeOnbordingLogic", "SyncOvenDataWithRuntimeNodeId: Model/HMISettings/DeviceId variable not found");
            return;
        }

        var runtimeNodeId = (string)runtimeNodeIdVariable.Value;

        var ovenDataFolder = model.Get("OvenData");
        if (ovenDataFolder == null)
        {
            ovenDataFolder = InformationModel.MakeObject<Folder>("OvenData");
            model.Add(ovenDataFolder);
        }

        // Drop any instance left over from a previous RuntimeNodeId value.
        foreach (var staleInstance in ovenDataFolder.Children.Where(child => child.BrowseName != runtimeNodeId).ToList())
        {
            staleInstance.Delete();
        }

        if (string.IsNullOrEmpty(runtimeNodeId))
        {
            Log.Warning("EdgeOnbordingLogic", "SyncOvenDataWithRuntimeNodeId: Model/HMISettings/DeviceId is empty, no OvenData instance published");
            return;
        }

        var ovenInstance = ovenDataFolder.Get(runtimeNodeId);
        if (ovenInstance == null)
        {
            var ovenType = Project.Current.Get("Model/Types/OvenType");
            if (ovenType == null)
            {
                Log.Error("EdgeOnbordingLogic", "SyncOvenDataWithRuntimeNodeId: type Model/Types/OvenType not found");
                return;
            }
            ovenInstance = InformationModel.MakeObject(runtimeNodeId, ovenType.NodeId);
            ovenDataFolder.Add(ovenInstance);
        }

        // Stamp unconditionally (new or pre-existing/retentive instance): the type's DeviceId->RuntimeNodeId
        // DynamicLink doesn't reliably resolve on runtime-created instances, leaving DeviceId empty on publish.
        // Avoid casting to OvenType: retentive nodes can fail an "as OvenType" check and trigger a
        // duplicate-BrowseName exception on Add(), which aborts Start() and stops MQTT publishing entirely.
        ovenInstance.GetVariable("DeviceId").Value = runtimeNodeId;
    }
}
