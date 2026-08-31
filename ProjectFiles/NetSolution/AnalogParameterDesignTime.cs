#region Using directives
using System;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.Alarm;
using FTOptix.WebUI;
using FTOptix.EventLogger;
#endregion

public class AnalogParameterDesignTime : BaseNetLogic
{
    [ExportMethod]
    public void ConfigureAnalogParameter(string widgetPath, string sourceNodePath, string mode = "Current")
    {
        if (string.IsNullOrWhiteSpace(widgetPath))
            throw new ArgumentException("widgetPath cannot be empty", nameof(widgetPath));

        if (string.IsNullOrWhiteSpace(sourceNodePath))
            throw new ArgumentException("sourceNodePath cannot be empty", nameof(sourceNodePath));

        var widget = ResolveNode(widgetPath) as AnalogParameter;
        if (widget == null)
            throw new InvalidOperationException($"Node '{widgetPath}' was not found or is not an AnalogParameter instance.");

        var sourceNode = ResolveNode(sourceNodePath);
        var contextAnalog = ResolveAnalogContext(sourceNode);
        var valueSourcePath = GetValueSourcePath(contextAnalog, NormalizeMode(mode));

        var parameterPointer = widget.GetVariable("ParameterPointer");
        if (parameterPointer == null)
            throw new InvalidOperationException($"Widget '{widget.BrowseName}' does not expose a ParameterPointer variable.");

        parameterPointer.Value = contextAnalog.NodeId;

        var dynamicLink = widget.GetVariable("PointerValue/StringFormatter1/ns=2;Source0/DynamicLink");
        if (dynamicLink == null)
            throw new InvalidOperationException($"Widget '{widget.BrowseName}' does not expose PointerValue/StringFormatter1/ns=2;Source0/DynamicLink.");

        dynamicLink.Value = valueSourcePath;

        Log.Info(nameof(AnalogParameterDesignTime),
            $"Configured '{widget.BrowseName}' with analog source '{GetNodePath(contextAnalog)}' in mode '{NormalizeMode(mode)}'.");
    }

    [ExportMethod]
    public void ConfigureCurrentMinMax(string currentWidgetPath, string minWidgetPath, string maxWidgetPath, string sourceNodePath)
    {
        ConfigureAnalogParameter(currentWidgetPath, sourceNodePath, "Current");
        ConfigureAnalogParameter(minWidgetPath, sourceNodePath, "Min");
        ConfigureAnalogParameter(maxWidgetPath, sourceNodePath, "Max");
    }

    private static IUANode ResolveNode(string nodePath)
    {
        var normalizedPath = NormalizePath(nodePath);
        var node = Project.Current.Get(normalizedPath);

        if (node == null)
            throw new InvalidOperationException($"Node '{nodePath}' was not found in the project.");

        return node;
    }

    private static IUAVariable ResolveAnalogContext(IUANode sourceNode)
    {
        IUANode current = sourceNode;

        while (current != null)
        {
            if (current is IUAVariable variable)
            {
                if (variable.GetVariable("EngineeringUnits") != null)
                    return variable;

                if (variable.BrowseName == "EURange" && variable.Owner is IUAVariable rangeOwnerVariable && rangeOwnerVariable.GetVariable("EngineeringUnits") != null)
                    return rangeOwnerVariable;

                if ((variable.BrowseName == "Low" || variable.BrowseName == "High") && variable.Owner?.BrowseName == "EURange")
                {
                    var ownerVariable = variable.Owner.Owner as IUAVariable;
                    if (ownerVariable?.GetVariable("EngineeringUnits") != null)
                        return ownerVariable;
                }
            }

            current = current.Owner;
        }

        throw new InvalidOperationException($"Node '{GetNodePath(sourceNode)}' does not belong to an analog variable exposing EngineeringUnits.");
    }

    private static string GetValueSourcePath(IUAVariable contextAnalog, string mode)
    {
        return mode switch
        {
            "Current" => "../../../ParameterPointer@Value",
            "Min" => EnsureEuRange(contextAnalog, "Low"),
            "Max" => EnsureEuRange(contextAnalog, "High"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Mode must be Current, Min or Max.")
        };
    }

    private static string EnsureEuRange(IUAVariable contextAnalog, string boundName)
    {
        var euRange = contextAnalog.GetVariable($"EURange/{boundName}");
        if (euRange == null)
            throw new InvalidOperationException($"Analog variable '{GetNodePath(contextAnalog)}' does not expose EURange/{boundName}.");

        return $"../../../ParameterPointer/EURange/{boundName}";
    }

    private static string NormalizeMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "Current";

        return mode.Trim().ToLowerInvariant() switch
        {
            "current" => "Current",
            "min" => "Min",
            "max" => "Max",
            _ => throw new ArgumentException("Mode must be Current, Min or Max.", nameof(mode))
        };
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().TrimStart('/').Replace('\\', '/');
    }

    private static string GetNodePath(IUANode node)
    {
        if (node?.Owner == null)
            return node?.BrowseName ?? string.Empty;

        return $"{GetNodePath(node.Owner)}/{node.BrowseName}";
    }
}
