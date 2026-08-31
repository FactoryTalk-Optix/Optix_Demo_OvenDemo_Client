#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

public class RecipesSynchronization : BaseNetLogic
{
    private IUAVariable recipesListVariable;
    private Store store;
    private Table recipesTable;
    private const string TABLE_NAME = "Recipes";
    private const string RECIPE_NAME_COLUMN = "Name";

    public override void Start()
    {
        // Get the database store
        store = Project.Current.Get<Store>("DataStores/EmbeddedDatabase");
        if (store == null)
        {
            Log.Error("RecipesSynchronization", "Database DataStores/EmbeddedDatabase not found");
            return;
        }

        // Get the recipes table
        recipesTable = store.Tables.Get<Table>(TABLE_NAME);
        if (recipesTable == null)
        {
            Log.Error("RecipesSynchronization", $"Table {TABLE_NAME} not found in EmbeddedDatabase");
            return;
        }

        // Get the variable where MQTT client writes the JSON containing recipes from the remote server
        recipesListVariable = Project.Current.GetVariable("Model/Subscriber/RecipesList");
        if (recipesListVariable == null)
        {
            Log.Error("RecipesSynchronization", "Variable Model/Subscriber/RecipesList not found");
            return;
        }

        // Subscribe to variable change event
        recipesListVariable.VariableChange += RecipesListVariable_VariableChange;
    }

    public override void Stop()
    {
        // Mandatory unsubscription to prevent memory leaks
        if (recipesListVariable != null)
        {
            recipesListVariable.VariableChange -= RecipesListVariable_VariableChange;
        }
    }

    private void RecipesListVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            // Get the JSON string from the new value
            string recipesJson = e.NewValue?.Value?.ToString();
            if (string.IsNullOrEmpty(recipesJson))
            {
                Log.Warning("RecipesSynchronization", "Received empty or null recipes JSON");
                return;
            }

            // Deserialize the JSON into a list of ExtendedRecipesPayload
            var recipes = JsonSerializer.Deserialize<List<ExtendedRecipesPayload>>(recipesJson);
            if (recipes == null || recipes.Count == 0)
            {
                Log.Warning("RecipesSynchronization", "No recipes found in JSON payload");
                return;
            }

            Log.Verbose1("RecipesSynchronization", $"Received {recipes.Count} recipes from remote server");

            // Synchronize recipes with database
            SynchronizeRecipesWithDatabase(recipes);
        }
        catch (JsonException jsonEx)
        {
            Log.Error("RecipesSynchronization", $"JSON deserialization error: {jsonEx.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("RecipesSynchronization", $"Error processing recipes: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronizes recipes from the payload with the local database.
    /// Deletes recipes not present in the payload and updates/inserts existing ones.
    /// </summary>
    private void SynchronizeRecipesWithDatabase(List<ExtendedRecipesPayload> recipes)
    {
        // Get existing recipe names from the database
        var existingRecipeNames = GetExistingRecipeNames();
        
        // Get the list of recipe names from the payload
        var payloadRecipeNames = recipes.Select(r => r.RecipeName).ToHashSet();

        // Delete recipes that are not in the payload
        foreach (var existingName in existingRecipeNames)
        {
            if (!payloadRecipeNames.Contains(existingName))
            {
                DeleteRecipeFromDatabase(existingName);
                Log.Info("RecipesSynchronization", $"Deleted recipe not in payload: {existingName}");
            }
        }

        // Update or insert each recipe from the payload
        foreach (var recipe in recipes)
        {
            if (existingRecipeNames.Contains(recipe.RecipeName))
            {
                UpdateRecipeInDatabase(recipe);
                Log.Verbose1("RecipesSynchronization", $"Updated recipe: {recipe.RecipeName}");
            }
            else
            {
                InsertRecipeToDatabase(recipe);
                Log.Verbose1("RecipesSynchronization", $"Inserted new recipe: {recipe.RecipeName}");
            }
        }
    }

    /// <summary>
    /// Retrieves all existing recipe names from the database.
    /// </summary>
    private HashSet<string> GetExistingRecipeNames()
    {
        var recipeNames = new HashSet<string>();

        try
        {
            Object[,] resultSet;
            String[] header;

            // Query to get all recipe names
            store.Query($"SELECT \"{RECIPE_NAME_COLUMN}\" FROM \"{TABLE_NAME}\"", out header, out resultSet);

            if (resultSet != null)
            {
                int rowCount = resultSet.GetLength(0);
                for (int i = 0; i < rowCount; i++)
                {
                    var recipeName = resultSet[i, 0]?.ToString();
                    if (!string.IsNullOrEmpty(recipeName))
                    {
                        recipeNames.Add(recipeName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("RecipesSynchronization", $"Error retrieving existing recipes: {ex.Message}");
        }

        return recipeNames;
    }

    /// <summary>
    /// Deletes a recipe from the database by name.
    /// </summary>
    private void DeleteRecipeFromDatabase(string recipeName)
    {
        try
        {
            Object[,] resultSet;
            String[] header;

            // Escape single quotes in recipe name for SQL
            var escapedName = recipeName.Replace("'", "''");
            store.Query($"DELETE FROM \"{TABLE_NAME}\" WHERE \"{RECIPE_NAME_COLUMN}\" = '{escapedName}'", out header, out resultSet);
        }
        catch (Exception ex)
        {
            Log.Error("RecipesSynchronization", $"Error deleting recipe {recipeName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates an existing recipe in the database.
    /// </summary>
    private void UpdateRecipeInDatabase(ExtendedRecipesPayload recipe)
    {
        try
        {
            // Build the SET clause for the UPDATE query
            var setClauses = new List<string>();

            // Add metadata columns
            setClauses.Add($"\"Version\" = '{EscapeSqlString(recipe.Version)}'");
            setClauses.Add($"\"Approved\" = {(recipe.Approved ? 1 : 0)}");
            setClauses.Add($"\"Used\" = {(recipe.Used ? 1 : 0)}");
            setClauses.Add($"\"Deprecated\" = {(recipe.Deprecated ? 1 : 0)}");

            // Add data columns (with leading slash to match DB column format)
            if (recipe.Data != null)
            {
                foreach (var dataEntry in recipe.Data)
                {
                    // Column names in DB have leading slash: /OvenPhase1/Duration
                    var columnName = dataEntry.Key;
                    var value = ConvertValueForSql(dataEntry.Value);
                    setClauses.Add($"\"{columnName}\" = {value}");
                }
            }

            var setClause = string.Join(", ", setClauses);
            var escapedRecipeName = EscapeSqlString(recipe.RecipeName);

            Object[,] resultSet;
            String[] header;
            store.Query($"UPDATE \"{TABLE_NAME}\" SET {setClause} WHERE \"{RECIPE_NAME_COLUMN}\" = '{escapedRecipeName}'", out header, out resultSet);
        }
        catch (Exception ex)
        {
            Log.Error("RecipesSynchronization", $"Error updating recipe {recipe.RecipeName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Inserts a new recipe into the database using Table.Insert() method.
    /// </summary>
    private void InsertRecipeToDatabase(ExtendedRecipesPayload recipe)
    {
        try
        {
            // Build column names and values lists for Table.Insert()
            var columns = new List<string>();
            var valuesList = new List<object>();

            // Add recipe name
            columns.Add(RECIPE_NAME_COLUMN);
            valuesList.Add(recipe.RecipeName);

            // Add metadata columns
            columns.Add("Version");
            valuesList.Add(recipe.Version ?? string.Empty);

            columns.Add("Approved");
            valuesList.Add(recipe.Approved ? 1 : 0);

            columns.Add("Used");
            valuesList.Add(recipe.Used ? 1 : 0);

            columns.Add("Deprecated");
            valuesList.Add(recipe.Deprecated ? 1 : 0);

            // Add data columns (with leading slash to match DB column format)
            if (recipe.Data != null)
            {
                foreach (var dataEntry in recipe.Data)
                {
                    // Column names in DB have leading slash: /OvenPhase1/Duration
                    var columnName = dataEntry.Key;
                    columns.Add(columnName);
                    valuesList.Add(ConvertJsonElementToValue(dataEntry.Value));
                }
            }

            // Create the values array for Table.Insert (1 row, N columns)
            var values = new object[1, valuesList.Count];
            for (int i = 0; i < valuesList.Count; i++)
            {
                values[0, i] = valuesList[i];
            }

            // Use native Table.Insert() method
            recipesTable.Insert(columns.ToArray(), values);
        }
        catch (Exception ex)
        {
            Log.Error("RecipesSynchronization", $"Error inserting recipe {recipe.RecipeName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts a JsonElement or object to its native value for database insertion.
    /// </summary>
    private object ConvertJsonElementToValue(object value)
    {
        if (value == null)
            return DBNull.Value;

        // Handle JsonElement from System.Text.Json
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Number when jsonElement.TryGetInt32(out int intVal) => intVal,
                JsonValueKind.Number when jsonElement.TryGetInt64(out long longVal) => longVal,
                JsonValueKind.Number when jsonElement.TryGetDouble(out double doubleVal) => doubleVal,
                JsonValueKind.String => jsonElement.GetString() ?? string.Empty,
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                JsonValueKind.Null => DBNull.Value,
                _ => jsonElement.ToString()
            };
        }

        return value;
    }

    /// <summary>
    /// Escapes a string for use in SQL queries.
    /// </summary>
    private string EscapeSqlString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("'", "''");
    }

    /// <summary>
    /// Converts a value from the JSON payload to SQL format.
    /// </summary>
    private string ConvertValueForSql(object value)
    {
        if (value == null)
            return "NULL";

        // Handle JsonElement from System.Text.Json
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Number => jsonElement.GetRawText(),
                JsonValueKind.String => $"'{EscapeSqlString(jsonElement.GetString())}'",
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                JsonValueKind.Null => "NULL",
                _ => $"'{EscapeSqlString(jsonElement.ToString())}'"
            };
        }

        // Handle other types
        return value switch
        {
            string s => $"'{EscapeSqlString(s)}'",
            bool b => b ? "1" : "0",
            int or long or float or double or decimal => value.ToString(),
            _ => $"'{EscapeSqlString(value.ToString())}'"
        };
    }

    /// <summary>
    /// Payload class matching the remote server's RecipePayload structure
    /// </summary>
    public class RecipePayload
    {
        public string RecipeName { get; set; }
        public string Version { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }

    /// <summary>
    /// Extended payload class matching the remote server's ExtendedRecipesPayload structure
    /// Includes metadata fields: Approved, Used, Deprecated
    /// </summary>
    public class ExtendedRecipesPayload : RecipePayload
    {
        public bool Approved { get; set; }
        public bool Used { get; set; }
        public bool Deprecated { get; set; }
    }
}
