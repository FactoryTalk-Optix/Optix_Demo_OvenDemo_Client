#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.CoreBase;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.Retentivity;
using FTOptix.Alarm;
using FTOptix.Core;
using FTOptix.OPCUAClient;
using FTOptix.System;
#endregion

public class RecipeListLogic : BaseNetLogic
{
    public override void Start()
    {
        RefreshRecipeGroups();
    }

    public override void Stop()
    {
    }

    [ExportMethod]
    public void RefreshRecipeGroups()
    {
        var groupList = LogicObject.Owner.Get(GroupListPath);
        if (groupList == null)
        {
            Log.Warning(LogCategory, $"'{GroupListPath}' was not found under '{LogicObject.Owner.BrowseName}'.");
            return;
        }

        var recipeSchema = Project.Current.Get<RecipeSchema>(RecipeSchemaPath);
        if (recipeSchema == null)
        {
            Log.Warning(LogCategory, $"Recipe schema '{RecipeSchemaPath}' was not found.");
            return;
        }

        var store = Project.Current.Get<Store>(StorePath);
        if (store == null)
        {
            Log.Warning(LogCategory, $"Recipe store '{StorePath}' was not found.");
            return;
        }

        var recipeGroupType = Project.Current.Get(RecipeGroupTypePath);
        if (recipeGroupType == null)
        {
            Log.Warning(LogCategory, $"Recipe group type '{RecipeGroupTypePath}' was not found.");
            return;
        }

        try
        {
            var schemaName = recipeSchema.BrowseName;
            var metadataTable = QuoteIdentifier($"RecipeMetadata_{schemaName}");
            var schemaLiteral = QuoteLiteral(schemaName);
            var groupsQuery =
                $"SELECT RecipesList.Name, COUNT(*) " +
                $"FROM Recipes AS RecipesList " +
                $"JOIN {metadataTable} AS MetadataList ON RecipesList.Id = MetadataList.RecipeId " +
                $"WHERE RecipesList.RecipeSchemaName = '{schemaLiteral}' " +
                "GROUP BY RecipesList.Name ORDER BY RecipesList.Name";

            object[,] rows;
            try
            {
                store.Query(groupsQuery, out _, out rows);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Recipe grouping SQL query failed: {ex.Message}", ex);
            }

            var groups = new List<RecipeGroup>();
            if (rows != null)
            {
                for (var row = 0; row < rows.GetLength(0); row++)
                {
                    var name = Convert.ToString(rows[row, 0], CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    groups.Add(new RecipeGroup(name, Convert.ToInt32(rows[row, 1], CultureInfo.InvariantCulture)));
                }
            }

            var generatedNodes = new List<IUANode>();
            try
            {
                for (var index = 0; index < groups.Count; index++)
                {
                    var group = groups[index];
                    var groupIndex = index.ToString("D4", CultureInfo.InvariantCulture);

                    var recipeGroup = InformationModel.MakeObject(
                        $"{GeneratedGroupPrefix}{groupIndex}",
                        recipeGroupType.NodeId);
                    generatedNodes.Add(recipeGroup);
                    SetRecipeGroupVariable(recipeGroup, "RecipeName", group.Name);
                    SetRecipeGroupVariable(recipeGroup, "Count", Math.Max(group.VersionCount, 1));
                }
            }
            catch
            {
                foreach (var node in generatedNodes)
                    node.Delete();

                throw;
            }

            RemoveGeneratedNodes(groupList);
            foreach (var node in generatedNodes)
                groupList.Add(node);
            UpdateEmptyState(groups.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"Unable to refresh recipe groups: {ex.Message}");
        }
    }

    // EmptyHint is created by DesignTimeNetLogic1.BuildRecipesEmptyHint.
    private void UpdateEmptyState(int recipeCount)
    {
        var view = LogicObject.Owner.Owner;
        if (view?.Get("SelectedRecipeView") is Item detail)
            detail.Visible = recipeCount > 0;
        if (view?.Get("EmptyHint") is Item hint)
            hint.Visible = recipeCount == 0;
    }

    private static void SetRecipeGroupVariable(IUANode recipeGroup, string variableName, object value)
    {
        var variable = recipeGroup.GetVariable(variableName);
        if (variable == null)
            throw new InvalidOperationException(
                $"Recipe group type '{recipeGroup.BrowseName}' has no '{variableName}' variable.");

        variable.Value = new UAValue(value);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string QuoteLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static void RemoveGeneratedNodes(IUANode groupList)
    {
        var generatedNodes = groupList.Children
            .Where(node =>
                node.BrowseName.StartsWith(GeneratedGroupPrefix, StringComparison.Ordinal) ||
                node.BrowseName.StartsWith(GeneratedHeaderPrefix, StringComparison.Ordinal) ||
                node.BrowseName.StartsWith(GeneratedListPrefix, StringComparison.Ordinal))
            .ToList();

        foreach (var node in generatedNodes)
            node.Delete();
    }

    private sealed class RecipeGroup
    {
        public RecipeGroup(string name, int versionCount)
        {
            Name = name;
            VersionCount = versionCount;
        }

        public string Name { get; }
        public int VersionCount { get; }
    }

    private const string LogCategory = "RecipeListLogic";
    private const string GroupListPath = "ScrollViewObject/GroupList";
    private const string RecipeSchemaPath = "Recipes/RecipeSchema";
    private const string StorePath = "DataStores/EmbeddedDatabase";
    private const string RecipeGroupTypePath = "UI/UITypes/RecipeGroup";
    private const string GeneratedGroupPrefix = "RecipeGroup_";
    private const string GeneratedHeaderPrefix = "RecipeGroupHeader_";
    private const string GeneratedListPrefix = "RecipeGroupList_";
}
