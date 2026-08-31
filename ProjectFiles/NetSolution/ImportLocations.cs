#region Using directives
using System;
using System.IO;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.MQTTClient;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.OPCUAServer;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.DataLogger;
using FTOptix.Core;
using System.Linq;
using FTOptix.RecipeX;
using FTOptix.Store;
using FTOptix.Recipe;
using FTOptix.SQLiteStore;
using FTOptix.Alarm;
using FTOptix.WebUI;
using FTOptix.EventLogger;
#endregion

public class ImportLocations : BaseNetLogic
{
    [ExportMethod]
    public void ImportFromCsv()
    {
        var csvPath = new ResourceUri("%PROJECTDIR%/locations.csv").Uri;

        if (!File.Exists(csvPath))
        {
            Log.Error("ImportLocations", $"CSV file not found: {csvPath}");
            return;
        }

        var locationsFolder = Project.Current.Get("Model/Locations");
        if (locationsFolder == null)
        {
            Log.Error("ImportLocations", "Model/Locations folder not found in the project");
            return;
        }

        // Remove existing OfficeLocation children before re-importing
        var existing = locationsFolder.Children.OfType<OfficeLocation>();
        foreach (var item in existing)
            item.Delete();

        var lines = File.ReadAllLines(csvPath);
        // Skip header row (index 0)
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var parts = line.Split(',');
            if (parts.Length < 4)
            {
                Log.Warning("ImportLocations", $"Skipping malformed line {i + 1}: {line}");
                continue;
            }

            var country  = parts[0].Trim();
            var city     = parts[1].Trim();
            var latitude  = double.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var longitude = double.Parse(parts[3].Trim(), System.Globalization.CultureInfo.InvariantCulture);

            var nodeName = $"{city.Replace(" ", "_")}_{country.Replace(" ", "_")}";

            var objectType = Project.Current.Get("Model/Types/OfficeLocation");
            var location = InformationModel.MakeObject(nodeName, objectType.NodeId);

            location.GetVariable("Name").Value = $"{city}, {country}";
            location.GetVariable("Latitude").Value = latitude;
            location.GetVariable("Longitude").Value = longitude;
            locationsFolder.Add(location);
            Log.Info("ImportLocations", $"Added OfficeLocation: {nodeName}");
        }

        Log.Info("ImportLocations", "CSV import complete.");
    }
}
