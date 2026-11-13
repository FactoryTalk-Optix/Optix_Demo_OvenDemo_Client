#region Using directives
using System;
using System.Linq;
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
#endregion

public class DesignTimeNetLogic1 : BaseNetLogic
{
    private const string Category = "DesignTimeNetLogic1";
    private const string RowsPath = "UI/UITypes/ListViewRows";

    [ExportMethod]
    public void ApplyAllFixes()
    {
        AddAnalyticsFieldsToOvenType();
        StandardizeLabelFonts();
        FixOnboardingDefaults();
        FixGridHeaders();
        RestoreAlarmRowTemplates();
        PolishDashboard();
        BuildQualityControls();
        BuildAlarmStatus();
        BuildRecipesEmptyHint();
    }

    // Re-runnable: everything needed for a working Dark theme, same palette as the supervisor.
    [ExportMethod]
    public void FixDarkTheme()
    {
        AssignWebSessionType();
        ApplySoftDarkPalette();
        StandardizeLabelFonts();
        FixDarkThemeOverrides();
    }

    // Re-runnable: styles that lacked a Dark value and fixed colors that ignored the theme (Light values stay the same).
    [ExportMethod]
    public void FixDarkThemeOverrides()
    {
        var surface = Project.Current.GetVariable("UI/StyleSheets/Dark/Surface");
        var text = Project.Current.GetVariable("UI/StyleSheets/Dark/Text");
        var changed = 0;

        var background = Project.Current.GetVariable("UI/StyleSheets/Dark/RectangleStyles/Background/FillColor");
        if (background != null && surface != null) { background.SetDynamicLink(surface); changed++; }

        var navItem = Project.Current.GetVariable("UI/StyleSheets/Dark/RectangleStyles/NavBarDefault/FillColor");
        if (navItem != null) { navItem.Value = 0x00FFFFFFu; changed++; }

        var navigation = Project.Current.Get("UI/StyleSheets/Dark/NavigationPanelStyles/Default");
        if (navigation != null && surface != null)
        {
            var fill = navigation.GetVariable("BackgroundColor");
            if (fill == null)
            {
                fill = InformationModel.MakeVariable("BackgroundColor", FTOptix.Core.DataTypes.Color);
                navigation.Add(fill);
            }
            fill.SetDynamicLink(surface);
            changed++;
        }

        if (Project.Current.Get("UI/StyleSheets/Dark/InputBoxStyles/InputBoxStyle1") is InputBoxStyle input && text != null)
        {
            input.TextColorVariable.SetDynamicLink(text);
            changed++;
        }

        changed += FollowThemeInIconConverters();

        // Fixed colors copied from the Light theme: inactive nav icons and the borders of every card and row.
        var links = new System.Collections.Generic.Dictionary<uint, string>
        {
            [0xFF3B3B3B] = "{Session}/StyleSheet/Text",
            [0xFFC7D0D5] = "{Session}/StyleSheet/Line2",
        };
        foreach (var variable in Descendants(Project.Current.Get("UI")).OfType<IUAVariable>().ToList())
        {
            if (variable.DataType != FTOptix.Core.DataTypes.Color) continue;
            if (variable.Children.Any(child => child is DynamicLink || child is IUAObject)) continue;
            if (IsStyleSheetNode(variable)) continue;
            var argb = ArgbOf(variable);
            if (!argb.HasValue || !links.TryGetValue(argb.Value, out var path)) continue;
            AddSourceLink(variable, path);
            changed++;
        }
        Log.Info(Category, $"Dark theme overrides fixed: {changed} nodes changed.");
    }

    // Inactive icon color "dark grey" is unreadable on Dark; converter parameters cannot be linked, so use a mid grey readable on both themes.
    private static int FollowThemeInIconConverters()
    {
        var count = 0;
        foreach (var converter in Descendants(Project.Current.Get("UI")).OfType<ConditionalConverter>().ToList())
        {
            if (IsStyleSheetNode(converter) || converter.Owner is not IUAVariable { BrowseName: "DynamicColor" }) continue;
            var offValue = converter.GetVariable("FalseValue");
            if (offValue == null || ArgbOf(offValue) != 0xFF3B3B3B) continue;
            offValue.Value = 0xFF8693A5u;
            count++;
        }
        return count;
    }

    private static bool IsStyleSheetNode(IUANode node)
    {
        for (var current = node; current != null; current = current.Owner)
            if (current.BrowseName == "StyleSheets" || current.BrowseName == "BaseStyleSheet" || current.BrowseName == "UISessionObject")
                return true;
        return false;
    }

    private static uint? ArgbOf(IUAVariable variable)
    {
        var value = variable.Value.Value;
        if (value is uint argb) return argb;
        if (value is Color color) return color.ARGB;
        return null;
    }

    // Re-runnable: only the native engine had UISessionObject (LightDarkTheme and its transitions), so the theme button did nothing in the WebUI.
    [ExportMethod]
    public void AssignWebSessionType()
    {
        var session = Project.Current.Get("UI/UISessionObject");
        var engine = Project.Current.Get("UI/WebPresentationEngine");
        if (session == null || engine == null)
        {
            Log.Error(Category, "UI/UISessionObject or UI/WebPresentationEngine not found.");
            return;
        }
        var pointer = engine.GetVariable("Session");
        if (pointer == null)
        {
            pointer = InformationModel.MakeVariable<NodePointer>("Session", OpcUa.DataTypes.NodeId);
            ((NodePointer)pointer).Kind = FTOptix.UI.ObjectTypes.UISession;
            engine.Add(pointer);
        }
        pointer.Value = session.NodeId;
        Log.Info(Category, "Web sessions now use UISessionObject.");
    }

    // Re-runnable: rewrites the base colors of the Dark stylesheet (tokens linked to them, e.g. Accent -> Brand400, follow).
    [ExportMethod]
    public void ApplySoftDarkPalette()
    {
        var palette = new System.Collections.Generic.Dictionary<string, uint>
        {
            ["Bg"] = 0xFF2B3646, ["Surface"] = 0xFF354357, ["SurfaceSoft"] = 0xFF425269,
            ["Text"] = 0xFFF1F5F9, ["TextSoft"] = 0xFFC0CDDC,
            ["Line"] = 0xFF5B6E86, ["Line2"] = 0xFF4B5D73,
            ["Brand400"] = 0xFF3F86D6, ["AccentColor"] = 0xFF7FB6F5,
            ["Ok"] = 0xFF3AA76D, ["Warn"] = 0xFFF0C040, ["Danger"] = 0xFFE0605B,
            ["HighColor"] = 0xFF5A2F33, ["HighText"] = 0xFFFFB3AD,
            ["MedColor"] = 0xFF5A4A26, ["MedText"] = 0xFFFFD78A,
            ["InfoColor"] = 0xFF3D4C61, ["InfoText"] = 0xFFC5D0DE,
            ["GoodColor"] = 0xFF25503A, ["GoodText"] = 0xFFA8E6C0,
        };
        var missing = new System.Collections.Generic.List<string>();
        foreach (var token in palette)
        {
            var variable = Project.Current.GetVariable("UI/StyleSheets/Dark/" + token.Key);
            if (variable == null) missing.Add(token.Key);
            else variable.Value = token.Value;
        }
        Log.Info(Category, $"Dark palette applied ({palette.Count - missing.Count} colors){(missing.Count > 0 ? ", missing: " + string.Join(", ", missing) : string.Empty)}.");
    }

    // Re-runnable: keeps OvenType identical to the supervisor's, which receives these fields from the oven (OvenSimulationLogic used to add them only at runtime).
    [ExportMethod]
    public void AddAnalyticsFieldsToOvenType()
    {
        var type = Project.Current.Get("Model/Types/OvenType");
        if (type == null)
        {
            Log.Error(Category, "Model/Types/OvenType not found.");
            return;
        }
        var added = 0;
        foreach (var name in new[] { "EnergyConsumedTotal", "CyclesCompletedTotal", "GoodCyclesTotal", "RejectedCyclesTotal", "RunningSecondsTotal", "IdealProductionSecondsTotal" })
            added += EnsureTypeField(type, name, OpcUa.DataTypes.Double);
        added += EnsureTypeField(type, "CounterEpoch", OpcUa.DataTypes.String);
        Log.Info(Category, $"OvenType analytics fields added: {added}.");
    }

    private static int EnsureTypeField(IUANode type, string name, NodeId dataType)
    {
        if (type.GetVariable(name) != null)
            return 0;
        type.Add(InformationModel.MakeVariable(name, dataType));
        return 1;
    }

    // Re-runnable: same label type scale as the supervisor, explicit in the base stylesheet, Light and Dark (Dark used to inherit a different one).
    [ExportMethod]
    public void StandardizeLabelFonts()
    {
        var reference = Project.Current.Get("UI/StyleSheets/Light/LabelStyles/H1");
        var sizeType = reference?.GetVariable("FontSize")?.DataType;
        var weightType = reference?.GetVariable("FontWeight")?.DataType;
        if (sizeType == null || weightType == null)
        {
            Log.Error(Category, "Light/LabelStyles/H1 must have FontSize and FontWeight to be used as reference.");
            return;
        }
        var scale = new (string Name, float Size, int Weight)[]
        {
            ("Default", 16f, 400), ("H1", 32f, 700), ("H2", 24f, 600), ("H3", 12f, 700), ("H4", 12f, 400), ("H5", 12f, 700),
        };
        var changed = 0;
        foreach (var sheet in new[] { "UI/BaseStyleSheet", "UI/StyleSheets/Light", "UI/StyleSheets/Dark" })
        {
            foreach (var entry in scale)
            {
                var style = Project.Current.Get($"{sheet}/LabelStyles/{entry.Name}");
                if (style == null)
                {
                    Log.Warning(Category, $"{sheet}/LabelStyles/{entry.Name} not found.");
                    continue;
                }
                SetStyleValue(style, "FontSize", sizeType, entry.Size);
                SetStyleValue(style, "FontWeight", weightType, entry.Weight);
                changed++;
            }
        }
        var darkDefault = Project.Current.Get("UI/StyleSheets/Dark/LabelStyles/Default") as LabelStyle;
        var darkText = Project.Current.GetVariable("UI/StyleSheets/Dark/Text");
        if (darkDefault != null && darkText != null)
            darkDefault.TextColorVariable.SetDynamicLink(darkText);
        Log.Info(Category, $"Label font scale applied to {changed} styles.");
    }

    private static void SetStyleValue(IUANode style, string name, NodeId dataType, object value)
    {
        var variable = style.GetVariable(name);
        if (variable == null)
        {
            variable = InformationModel.MakeVariable(name, dataType);
            style.Add(variable);
        }
        variable.Value = new UAValue(value);
    }

    // Re-runnable: shorter batch text, uptime without unit (UptimeText is already formatted d.hh:mm:ss) and one Start/Stop button that follows the oven state.
    [ExportMethod]
    public void PolishDashboard()
    {
        const string row = "UI/UITypes/ListViewRows/OvenDataRow/Cycle/Content/";
        var batchValue = Project.Current.Get<Label>(row + "Cards/Card2/Content/ValueToDispay/CurrentValue");
        if (batchValue != null)
            batchValue.Style = "Default";

        var uptimeUnit = Project.Current.GetVariable(row + "Cards/Card5/ValueAddInfo");
        if (uptimeUnit != null)
            uptimeUnit.Value = "";

        var button = Project.Current.Get(row + "StartCycle");
        var method = Descendants(button).OfType<IUAVariable>().FirstOrDefault(v => v.BrowseName == "Method");
        var text = Project.Current.Get<Label>(row + "StartCycle/Content/ButtonText");
        if (method == null || text == null)
        {
            Log.Warning(Category, "StartCycle button not found.");
            return;
        }
        method.Value = "ToggleCycle";
        BindExpression(text.VisibleVariable, "{0} == 0", "{RowItem}/ExecutionState");
        var stop = text.Owner.Get<Label>("StopText");
        if (stop == null)
        {
            stop = InformationModel.MakeObject<Label>("StopText");
            stop.Text = "Stop";
            stop.Style = "Default";
            text.Owner.Add(stop);
        }
        BindExpression(stop.VisibleVariable, "{0} != 0", "{RowItem}/ExecutionState");
        Log.Info(Category, "Dashboard polished.");
    }

    // Re-runnable: "Reject next cycle" and "Put pineapples on pizza" switches under the Start button of the dashboard row.
    // The second one drives the first input (PineapplesOnPizza) of alarmSimulationLogic through OvenSimulationLogic.
    [ExportMethod]
    public void BuildQualityControls()
    {
        var content = Project.Current.Get("UI/UITypes/ListViewRows/OvenDataRow/Cycle/Content");
        var settings = Project.Current.Get("Model/HMISettings");
        var logic = Project.Current.Get("NetLogic/OvenSimulationLogic");
        if (content == null || settings == null || logic == null)
        {
            Log.Error(Category, "OvenDataRow/Cycle/Content, Model/HMISettings or NetLogic/OvenSimulationLogic not found.");
            return;
        }

        var reject = EnsureVariable(settings, "RejectNextCycle", OpcUa.DataTypes.Boolean);
        var pineapples = EnsureVariable(logic, "PineapplesOnPizza", OpcUa.DataTypes.Boolean);
        EnsureVariable(logic, "UptimeText", OpcUa.DataTypes.String);
        if (content.Get("QualityControls") != null)
            return;

        var row = InformationModel.MakeObject<RowLayout>("QualityControls");
        row.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.HorizontalGap = 8;
        content.Add(row);
        AddSwitch(row, "RejectNextCycle", "Reject next cycle", reject, 0);
        AddSwitch(row, "PineapplesOnPizza", "Put pineapples on pizza", pineapples, 32);
        Log.Info(Category, "Quality controls created.");
    }

    // Re-runnable: Model/AlarmStatus holds the number of active alarms and whether there is at least one (written by OvenSimulationLogic), to bind notifications to.
    [ExportMethod]
    public void BuildAlarmStatus()
    {
        var model = Project.Current.Get("Model");
        if (model == null)
        {
            Log.Error(Category, "Model not found.");
            return;
        }
        var status = model.Get("AlarmStatus");
        if (status == null)
        {
            status = InformationModel.MakeObject<Folder>("AlarmStatus");
            model.Add(status);
        }
        EnsureVariable(status, "ActiveAlarmCount", OpcUa.DataTypes.UInt32);
        EnsureVariable(status, "HasActiveAlarms", OpcUa.DataTypes.Boolean);
        Log.Info(Category, "Model/AlarmStatus ready.");
    }

    private static IUAVariable EnsureVariable(IUANode parent, string name, NodeId dataType)
    {
        var variable = parent.GetVariable(name);
        if (variable != null)
            return variable;
        variable = InformationModel.MakeVariable(name, dataType);
        parent.Add(variable);
        return variable;
    }

    private static void AddSwitch(IUANode row, string name, string text, IUAVariable target, float leftMargin)
    {
        var label = InformationModel.MakeObject<Label>(name + "Label");
        label.Text = text;
        label.Height = 28;
        label.LeftMargin = leftMargin;
        row.Add(label);
        var control = InformationModel.MakeObject<Switch>(name + "Switch");
        control.Width = 52;
        control.Height = 28;
        row.Add(control);
        control.CheckedVariable.SetDynamicLink(target, DynamicLinkMode.ReadWrite);
    }

    // Re-runnable: RecipeListLogic shows this hint and hides the empty detail panel while the oven has no recipe.
    [ExportMethod]
    public void BuildRecipesEmptyHint()
    {
        var view = Project.Current.Get("UI/Screens/Recipes/div/RecipesView");
        if (view == null)
        {
            Log.Error(Category, "UI/Screens/Recipes/div/RecipesView not found.");
            return;
        }
        if (view.Get("EmptyHint") != null)
            return;

        var hint = InformationModel.MakeObject<Label>("EmptyHint");
        hint.Text = "No recipes yet. Recipes are sent by the supervisor once this oven is onboarded.";
        hint.HorizontalAlignment = HorizontalAlignment.Stretch;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.TextHorizontalAlignment = TextHorizontalAlignment.Center;
        hint.Style = "H4";
        hint.Visible = false;
        view.Add(hint);
        Log.Info(Category, "Recipes empty hint created.");
    }

    private static void BindExpression(IUAVariable target, string expression, params string[] paths)
    {
        var evaluator = InformationModel.MakeObject<ExpressionEvaluator>("ExpressionEvaluator1");
        evaluator.Expression = expression;
        for (var i = 0; i < paths.Length; i++)
        {
            var source = InformationModel.MakeVariable("Source" + i, OpcUa.DataTypes.BaseDataType);
            AddSourceLink(source, paths[i]);
            evaluator.Refs.AddReference(FTOptix.CoreBase.ReferenceTypes.HasSource, source);
        }
        target.SetConverter(evaluator);
    }

    // Re-runnable: a fresh oven is "No onboarding" (0), not "Onboarded" (2), and the broker label had a typo.
    [ExportMethod]
    public void FixOnboardingDefaults()
    {
        var status = Project.Current.GetVariable("Model/Onboarding/Result/OnboardingStatus");
        if (status != null)
            status.Value = 0;
        else
            Log.Error(Category, "Model/Onboarding/Result/OnboardingStatus not found.");

        var label = Descendants(Project.Current.Get("UI/Screens/Settings")).OfType<Label>().FirstOrDefault(l => l.BrowseName == "MQTTBrokerAddressText");
        if (label != null)
            label.Text = "MQTT broker address";
        Log.Info(Category, "Onboarding defaults fixed.");
    }

    // Re-runnable: the header labels of Alarms/Logs used the white ChipAccent text on the light GridHeader fill, so they were invisible.
    [ExportMethod]
    public void FixGridHeaders()
    {
        var fixedLabels = 0;
        foreach (var screen in new[] { "Alarms", "Logs" })
        {
            var header = Project.Current.Get("UI/Screens/" + screen + "/Content/Header/Content");
            if (header == null)
            {
                Log.Warning(Category, $"UI/Screens/{screen}/Content/Header/Content not found.");
                continue;
            }
            foreach (var label in header.Children.OfType<Label>())
            {
                SetStyle(label, "H4");
                fixedLabels++;
            }
        }
        Log.Info(Category, $"Grid header labels restyled: {fixedLabels}.");
    }

    // Re-runnable: the Alarms and Logs lists pointed to row templates that no longer exist in the project, so no row could be drawn.
    [ExportMethod]
    public void RestoreAlarmRowTemplates()
    {
        var rows = Project.Current.Get(RowsPath);
        var converter = Project.Current.Get("Converters/AlarmSeverityIconPaths");
        if (rows == null || converter == null)
        {
            Log.Error(Category, $"'{RowsPath}' or 'Converters/AlarmSeverityIconPaths' not found.");
            return;
        }

        var active = EnsureAlarmRow(rows, converter, "ActiveAlarmListRow", OpcUa.ObjectTypes.AlarmConditionType, "{RowItem}/Time");
        var history = EnsureAlarmRow(rows, converter, "AlarmHistoryListRow", OpcUa.VariableTypes.BaseDataVariableType, "{RowItem}/LocalTime");
        PointTemplate("UI/Screens/Alarms/Content/Body/AlarmsList", active);
        PointTemplate("UI/Screens/Logs/Content/Body/RecipesList", history);
        BindHistoryQuery("UI/Screens/Logs/Content/Body/RecipesList/Query");
        Log.Info(Category, "Alarm row templates restored.");
    }

    // The event logger stores one Message column per language (Message_en-US, Message_it-IT, ...): alias the session language as Message.
    private static void BindHistoryQuery(string queryPath)
    {
        var query = Project.Current.GetVariable(queryPath);
        if (query == null)
        {
            Log.Warning(Category, $"{queryPath} not found.");
            return;
        }
        foreach (var child in query.Children.Where(child => child is Converter || child is DynamicLink).ToList())
            child.Delete();
        BindFormattedText(query, "SELECT *, \"Message_{0}\" AS Message FROM AlarmsEventLogger ORDER BY LocalTime DESC", "{Session}/ActualLocaleId");
    }

    private static IUAObjectType EnsureAlarmRow(IUANode rows, IUANode severityConverter, string name, NodeId kind, string timePath)
    {
        if (rows.Get(name) is IUAObjectType existing)
            return existing;

        var type = InformationModel.MakeObjectType(name, FTOptix.UI.ObjectTypes.ListViewRow);
        rows.Add(type);

        // ListViewRow already declares RowItem; only its Kind changes.
        var alias = type.Children.OfType<Alias>().First(child => child.BrowseName == "RowItem");
        alias.Kind = kind;

        var content = InformationModel.MakeObject<RowLayout>("Content");
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.VerticalAlignment = VerticalAlignment.Stretch;
        type.Add(content);

        var iconCell = AddCell(content, "SeverityCell", 64);
        var icon = InformationModel.MakeObject<Image>("SeverityIcon");
        icon.Width = 32;
        icon.Height = 32;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        iconCell.Add(icon);
        var iconConverter = InformationModel.MakeObject<ValueMapConverter>("SeverityIconPaths", severityConverter.NodeId);
        icon.PathVariable.SetConverter(iconConverter);
        AddSourceLink(iconConverter.GetVariable("Source"), "{RowItem}/Severity");

        var time = AddCellLabel(AddCell(content, "TimeCell", 160), "TimeText");
        BindFormattedText(time.TextVariable, "{0:T}", timePath);

        var message = AddCellLabel(AddCell(content, "MessageCell", 900), "MessageText");
        BindFormattedText(message.TextVariable, "{0}", "{RowItem}/Message");

        var underline = InformationModel.MakeObject<Rectangle>("Underline");
        underline.Height = 1;
        underline.HorizontalAlignment = HorizontalAlignment.Stretch;
        underline.VerticalAlignment = VerticalAlignment.Bottom;
        underline.Style = "Underline";
        type.Add(underline);
        return type;
    }

    private static void BindFormattedText(IUAVariable target, string format, string path)
    {
        var formatter = InformationModel.MakeObject<StringFormatter>("StringFormatter1");
        formatter.Format = format;
        var source = InformationModel.MakeVariable("Source0", OpcUa.DataTypes.BaseDataType);
        AddSourceLink(source, path);
        formatter.Refs.AddReference(FTOptix.CoreBase.ReferenceTypes.HasSource, source);
        target.SetConverter(formatter);
    }

    private static Panel AddCell(IUANode row, string name, float width)
    {
        var cell = InformationModel.MakeObject<Panel>(name);
        cell.VerticalAlignment = VerticalAlignment.Stretch;
        cell.Width = width;
        row.Add(cell);
        return cell;
    }

    private static Label AddCellLabel(IUANode cell, string name)
    {
        var label = InformationModel.MakeObject<Label>(name);
        label.HorizontalAlignment = HorizontalAlignment.Stretch;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.LeftMargin = 16;
        label.RightMargin = 16;
        cell.Add(label);
        return label;
    }

    private static void AddSourceLink(IUAVariable target, string path)
    {
        var link = InformationModel.MakeVariable<DynamicLink>("DynamicLink", FTOptix.Core.DataTypes.NodePath);
        link.Value = path;
        target.Add(link);
    }

    private static void PointTemplate(string listPath, IUAObjectType rowType)
    {
        var template = Project.Current.GetVariable(listPath + "/TypeSelectors/TypeSelector1/Template");
        if (template == null)
        {
            Log.Warning(Category, $"{listPath}/TypeSelectors/TypeSelector1/Template not found.");
            return;
        }
        template.Value = rowType.NodeId;
    }

    private static void SetStyle(IUANode node, string style)
    {
        var variable = node.GetVariable("Style");
        if (variable == null)
        {
            variable = InformationModel.MakeVariable("Style", OpcUa.DataTypes.String);
            node.Add(variable);
        }
        variable.Value = style;
    }

    private static System.Collections.Generic.IEnumerable<IUANode> Descendants(IUANode root)
    {
        if (root == null) yield break;
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
