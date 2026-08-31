#region Using directives
using System;
using System.Globalization;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.CoreBase;
using FTOptix.OPCUAServer;
using FTOptix.MQTTClient;
using FTOptix.Retentivity;
using FTOptix.DataLogger;
using FTOptix.Core;
using FTOptix.RecipeX;
using FTOptix.Store;
using FTOptix.Recipe;
using FTOptix.SQLiteStore;
using FTOptix.Alarm;
using FTOptix.WebUI;
using FTOptix.EventLogger;
#endregion

public class ApplyRecipeLogic : BaseNetLogic
{
    private const string TableName = "Recipes";
    private const string NameColumn = "Name";
    private const string VersionColumn = "Version";
    private DialogType genericNotificationDialogType;

    public override void Start()
    {
        genericNotificationDialogType = Project.Current.Get<DialogType>("UI/Types/DialogBoxes/RecipeApplicationNotificationDialog");
        if (genericNotificationDialogType == null)
        {
            Log.Warning("ApplyRecipeLogic", "UI/Types/DialogBoxes/RecipeApplicationNotificationDialog not found");
        }
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]
    public void ApplyRecipeFromDatabase(string recipeName, string recipeVersion, NodeId callerScreenNodeId)
    {
        var callerScreen = InformationModel.Get(callerScreenNodeId);
        if (callerScreen == null)
        {
            Log.Error("ApplyRecipeLogic", $"Caller screen not found for NodeId: {callerScreenNodeId}");
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(recipeName) || string.IsNullOrWhiteSpace(recipeVersion))
            {
                Log.Error("ApplyRecipeLogic", "Recipe name and version are required");
                ShowResultDialog(false, callerScreen);
                return;
            }

            var optixOven = Project.Current.Get<OptixOven>("Model/Oven/OptixOven");
            if (optixOven == null)
            {
                Log.Error("ApplyRecipeLogic", "Model/Oven/OptixOven not found");
                ShowResultDialog(false, callerScreen);
                return;
            }

            var store = Project.Current.Get<Store>("DataStores/EmbeddedDatabase");
            if (store == null)
            {
                Log.Error("ApplyRecipeLogic", "Database DataStores/EmbeddedDatabase not found");
                ShowResultDialog(false, callerScreen);
                return;
            }

            var escapedName = EscapeSqlString(recipeName);
            var escapedVersion = EscapeSqlString(recipeVersion);
            var query =
                $"SELECT * FROM \"{TableName}\" WHERE \"{NameColumn}\" = '{escapedName}' AND \"{VersionColumn}\" = '{escapedVersion}' LIMIT 1";

            store.Query(query, out var header, out var resultSet);

            if (resultSet == null || resultSet.GetLength(0) == 0)
            {
                Log.Warning("ApplyRecipeLogic", $"Recipe not found: {recipeName} v{recipeVersion}");
                ShowResultDialog(false, callerScreen);
                return;
            }

            var rowIndex = 0;
            ApplyRecipeToOven(header, resultSet, rowIndex, optixOven);

            Project.Current.GetVariable("Model/Oven/OptixOven/CurrentRecipe").Value = $"{recipeName} v{recipeVersion}";

            Log.Info("ApplyRecipeLogic", $"Applied recipe {recipeName} v{recipeVersion} to Model/Oven/OptixOven");
            ShowResultDialog(true, callerScreen);
        }
        catch (Exception ex)
        {
            Log.Error("ApplyRecipeLogic", $"Error applying recipe from database: {ex.Message}");
            ShowResultDialog(false, callerScreen);
        }
    }

    private void ShowResultDialog(bool success, IUANode callerScreen)
    {
        try
        {
            if (genericNotificationDialogType == null)
            {
                Log.Error("ApplyRecipeLogic", "GenericNotificationDialog type not available, cannot show result dialog");
                return;
            }

            var dialogInstance = UICommands.OpenDialog(callerScreen, genericNotificationDialogType, Array.Empty<NodeId>());
            if (dialogInstance == null)
            {
                Log.Warning("ApplyRecipeLogic", "Failed to open GenericNotificationDialog");
                return;
            }

            var successVariable = dialogInstance.GetVariable("Success");
            if (successVariable != null)
            {
                successVariable.Value = success ? 1 : 0;
            }
            else
            {
                Log.Warning("ApplyRecipeLogic", "Success variable not found in GenericNotificationDialog");
            }
        }
        catch (Exception ex)
        {
            Log.Error("ApplyRecipeLogic", $"Failed to show result dialog: {ex.Message}");
        }
    }

    private void ApplyRecipeToOven(string[] header, object[,] resultSet, int rowIndex, OptixOven oven)
    {
        if (header == null)
            return;

        for (var col = 0; col < header.Length; col++)
        {
            var columnName = header[col];
            var value = resultSet[rowIndex, col];

            if (string.IsNullOrEmpty(columnName) || value == null || value == DBNull.Value)
                continue;

            switch (columnName)
            {
                case "OvenPhase1/Duration":
                    oven.OvenPhase1.Duration = ToInt(value);
                    break;
                case "OvenPhase1/Setpoint":
                    oven.OvenPhase1.Setpoint = ToInt(value);
                    break;
                case "OvenPhase1/InputFan":
                    oven.OvenPhase1.InputFan = ToInt(value);
                    break;
                case "OvenPhase1/OutputFan":
                    oven.OvenPhase1.OutputFan = ToInt(value);
                    break;
                case "OvenPhase1/Heating":
                    oven.OvenPhase1.Heating = ToBool(value);
                    break;
                case "OvenPhase2/Duration":
                    oven.OvenPhase2.Duration = ToInt(value);
                    break;
                case "OvenPhase2/Setpoint":
                    oven.OvenPhase2.Setpoint = ToInt(value);
                    break;
                case "OvenPhase2/InputFan":
                    oven.OvenPhase2.InputFan = ToInt(value);
                    break;
                case "OvenPhase2/OutputFan":
                    oven.OvenPhase2.OutputFan = ToInt(value);
                    break;
                case "OvenPhase2/Heating":
                    oven.OvenPhase2.Heating = ToBool(value);
                    break;
                case "OvenPhase3/Duration":
                    oven.OvenPhase3.Duration = ToInt(value);
                    break;
                case "OvenPhase3/Setpoint":
                    oven.OvenPhase3.Setpoint = ToInt(value);
                    break;
                case "OvenPhase3/InputFan":
                    oven.OvenPhase3.InputFan = ToInt(value);
                    break;
                case "OvenPhase3/OutputFan":
                    oven.OvenPhase3.OutputFan = ToInt(value);
                    break;
                case "OvenPhase3/Heating":
                    oven.OvenPhase3.Heating = ToBool(value);
                    break;
                default:
                    break;
            }
        }
    }

    private string EscapeSqlString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("'", "''");
    }

    private int ToInt(object value)
    {
        if (value is int intValue)
            return intValue;

        if (value is long longValue)
            return Convert.ToInt32(longValue);

        if (value is double doubleValue)
            return Convert.ToInt32(doubleValue);

        if (value is float floatValue)
            return Convert.ToInt32(floatValue);

        if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return 0;
    }

    private bool ToBool(object value)
    {
        if (value is bool boolValue)
            return boolValue;

        if (value is int intValue)
            return intValue != 0;

        if (value is long longValue)
            return longValue != 0;

        var asString = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (bool.TryParse(asString, out var parsedBool))
            return parsedBool;

        if (int.TryParse(asString, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedInt))
            return parsedInt != 0;

        return false;
    }
}
