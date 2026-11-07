#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.MQTTClient;
using FTOptix.NetLogic;
using FTOptix.RecipeX;
using FTOptix.System;
#endregion

/// <summary>
/// Oven side of the recipe distribution. Receives recipes from the supervisor on <c>recipes/&lt;OvenId&gt;/to-edge</c>,
/// stores them in RecipeX (read-only for the operator), acknowledges them and announces its catalog (hello) whenever it
/// is onboarded and connected so the supervisor can resend what is missing. The supervisor only delivers: which recipe
/// is applied is always decided on the oven (<see cref="ApplyRecipe"/>, <c>TransferFromStoreToTarget</c> into
/// Model/RecipeTarget/OvenRecipeTarget, at the next cycle boundary if a cycle is running) and reported back for information.
/// Replies go to <c>recipes/&lt;OvenId&gt;/to-supervisor</c>.
/// </summary>
public class EdgeRecipesLogic : BaseNetLogic
{
    private const string Cat = "EdgeRecipesLogic";
    private const string SchemaPath = "Recipes/RecipeSchema";
    private const string ClientPath = "MQTT/MQTTEdgeDeviceClient";
    private const string OvenIdPath = "Model/HMISettings/DeviceId";
    private const string OnboardingStatusPath = "Model/Onboarding/Result/OnboardingStatus";
    private const string TargetPath = "Model/RecipeTarget/OvenRecipeTarget";
    private const string OvenDataPath = "Model/OvenData";
    private const string LinkFolderName = "RecipesLink";
    private const string SubscriberName = "RecipesSubscriber";
    private const string SubscriptionName = "Recipes";
    private const int OnboardedValue = 2;
    private const int StatusApproved = 2;
    private const int StatusDeprecated = 3;

    private static readonly string[] PhaseNames = { "PreHeat", "Bake", "Cooldown" };
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    private RecipeSchema schema;
    private IUAVariable inbox;
    private IUAVariable ovenId;
    private PeriodicTask task;
    private bool helloSent;
    private RecipeId pending;
    private IUAVariable lastResult;

    public override void Start()
    {
        schema = Project.Current.Get<RecipeSchema>(SchemaPath);
        ovenId = Project.Current.GetVariable(OvenIdPath);
        if (schema == null || ovenId == null)
        {
            Log.Error(Cat, $"'{SchemaPath}' or '{OvenIdPath}' not found, recipe link disabled.");
            return;
        }

        inbox = EnsureInbox();
        inbox.VariableChange += OnInbox;
        ovenId.VariableChange += OnOvenIdChanged;
        EnsureSubscription();

        task = new PeriodicTask(Background, 2000, LogicObject);
        task.Start();
    }

    public override void Stop()
    {
        task?.Dispose();
        task = null;
        if (inbox != null) inbox.VariableChange -= OnInbox;
        if (ovenId != null) ovenId.VariableChange -= OnOvenIdChanged;
    }

    #region MQTT plumbing

    private IUAVariable EnsureInbox()
    {
        var model = Project.Current.Get("Model");
        var folder = model.Get(LinkFolderName);
        if (folder == null)
        {
            folder = InformationModel.MakeObject<Folder>(LinkFolderName);
            model.Add(folder);
        }
        lastResult = folder.GetVariable("LastResult");
        if (lastResult == null)
        {
            lastResult = InformationModel.MakeVariable("LastResult", OpcUa.DataTypes.String);
            folder.Add(lastResult);
        }
        var variable = folder.GetVariable("Inbox");
        if (variable == null)
        {
            variable = InformationModel.MakeVariable("Inbox", OpcUa.DataTypes.String);
            folder.Add(variable);
        }
        return variable;
    }

    private void OnOvenIdChanged(object sender, VariableChangeEventArgs e) => EnsureSubscription();

    /// <summary>Creates the <c>recipes/&lt;OvenId&gt;/to-edge</c> subscription; a changed subscription needs a client restart.</summary>
    private void EnsureSubscription()
    {
        try
        {
            var id = OwnId();
            var client = Project.Current.Get<MQTTClient>(ClientPath);
            if (client == null || !IsValidId(id)) return;

            var changed = false;
            var subscriber = client.Get(SubscriberName) as MQTTSubscriber;
            if (subscriber == null)
            {
                subscriber = InformationModel.Make<MQTTSubscriber>(SubscriberName);
                client.Add(subscriber);
                changed = true;
            }

            var topic = $"recipes/{id}/to-edge";
            var existing = subscriber.Get("Subscriptions")?.Get(SubscriptionName) as SubscriptionPlainText;
            if (existing == null)
            {
                var s = InformationModel.Make<SubscriptionPlainText>(SubscriptionName);
                subscriber.Subscriptions.Add(s);
                s.Topic = topic;
                s.QoS = QoSLevel.AtLeastOnce;
                s.Variable = inbox.NodeId;
                changed = true;
            }
            else if (existing.Topic != topic || existing.Variable != inbox.NodeId)
            {
                existing.Topic = topic;
                existing.QoS = QoSLevel.AtLeastOnce;
                existing.Variable = inbox.NodeId;
                changed = true;
            }

            if (!changed) return;
            client.Stop();
            client.Start();
            Log.Info(Cat, $"Recipe subscription '{topic}' ready, MQTT client restarted.");
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"Subscription setup failed: {ex.Message}");
        }
    }

    private string OwnId() => Convert.ToString(ovenId?.Value.Value);

    private static bool IsValidId(string id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

    private bool Publish(RecipeMessage message)
    {
        var client = Project.Current.Get<MQTTClient>(ClientPath);
        var id = OwnId();
        if (client == null || client.Status != Status.Connected || !IsValidId(id)) return false;

        message.DeviceId = id;
        var config = JsonSerializer.Serialize(new { Topic = $"recipes/{id}/to-supervisor", QoS = 1, Retain = false });
        client.Publish(config, JsonSerializer.Serialize(message, Json));
        return true;
    }

    #endregion

    #region Periodic work

    private void Background()
    {
        try
        {
            var client = Project.Current.Get<MQTTClient>(ClientPath);
            var connected = client != null && client.Status == Status.Connected;
            var onboarded = Convert.ToInt32(Project.Current.GetVariable(OnboardingStatusPath)?.Value.Value) == OnboardedValue;

            if (connected && onboarded)
            {
                if (!helloSent) helloSent = SendHello();
            }
            else
            {
                helloSent = false;
            }

            if (pending != null && !OvenSimulationLogic.CycleRunning)
            {
                var p = pending;
                pending = null;
                ApplyNow(p);
            }
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"Background task failed: {ex.Message}");
        }
    }

    private bool SendHello()
    {
        var list = new List<RecipeInfo>();
        var all = schema.GetRecipes();
        if (all.ResultCode == GetRecipesResultCode.Success)
        {
            foreach (var r in all.Recipes)
            {
                var id = r.RecipeId;
                var status = Convert.ToInt32(GetMeta(id, "Status") ?? 0);
                list.Add(new RecipeInfo { Name = id.Name, Version = id.Version, Hash = StoredHash(id), Status = StatusName(status) });
            }
        }
        return Publish(new RecipeMessage { Type = "recipe.hello", Recipes = list });
    }

    #endregion

    #region Incoming messages

    private void OnInbox(object sender, VariableChangeEventArgs e)
    {
        var json = e.NewValue.Value as string;
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var message = JsonSerializer.Deserialize<RecipeMessage>(json, Json);
            if (message == null || (message.DeviceId != null && message.DeviceId != OwnId()))
            {
                Log.Warning(Cat, $"Discarded recipe message for another device: {json}");
                return;
            }

            switch (message.Type)
            {
                case "recipe.send": HandleSend(message); break;
                default: Log.Warning(Cat, $"Unknown message type '{message.Type}'"); break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"Message handling failed: {ex.Message}");
        }
    }

    private void HandleSend(RecipeMessage m)
    {
        var id = new RecipeId { Name = m.Name, Version = m.Version };
        var reason = ValidateSend(m);
        if (reason != null) { Ack(m, "Rejected", reason); return; }

        var status = (int)Enum.Parse(typeof(Wf), m.Status);
        if (GetMeta(id, "Status") != null)
        {
            // Recipes are immutable once released: only the status may change.
            if (StoredHash(id) != m.Hash) { Ack(m, "Rejected", "Version already exists with different content"); return; }
            if (!SetMeta(id, "Status", (uint)status)) { Ack(m, "Rejected", "Status not written"); return; }
            Ack(m, "Stored", string.Empty);
            return;
        }

        var created = schema.CreateRecipe(id);
        if (created != CreateRecipeResultCode.Success) { Ack(m, "Rejected", $"Create failed: {created}"); return; }

        foreach (var phase in PhaseNames)
        {
            var p = m.Phases[phase];
            if (!WriteValue(id, phase, "TemperatureSetpoint", p.TemperatureSetpoint)
                || !WriteValue(id, phase, "Duration", p.Duration)
                || !WriteValue(id, phase, "FanInRamp", p.FanInRamp)
                || !WriteValue(id, phase, "FanOutRamp", p.FanOutRamp)
                || !WriteValue(id, phase, "Heating", p.Heating))
            {
                schema.DeleteRecipe(id);
                Ack(m, "Rejected", $"Values of {phase} not written");
                return;
            }
        }

        if (!SetMeta(id, "Status", (uint)status))
        {
            schema.DeleteRecipe(id);
            Ack(m, "Rejected", "Status not written");
            return;
        }
        SetMeta(id, "Used", false);
        SetMeta(id, "Author", m.Author ?? string.Empty);
        SetMeta(id, "Description", m.Description ?? string.Empty);
        Ack(m, "Stored", string.Empty);
        Log.Info(Cat, $"Stored {m.Name} v{m.Version} ({m.Status})");
    }

    private string ValidateSend(RecipeMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Version)) return "Missing name or version";
        if (!Enum.TryParse<Wf>(m.Status, out var status) || (status != Wf.Approved && status != Wf.Deprecated)) return "Only Approved or Deprecated recipes are accepted";
        if (m.Phases == null || PhaseNames.Any(n => !m.Phases.ContainsKey(n))) return "Missing phases";
        foreach (var n in PhaseNames)
        {
            var p = m.Phases[n];
            if (p.TemperatureSetpoint < 0 || p.TemperatureSetpoint > 512) return $"{n}: setpoint out of range";
            if (p.Duration < 1 || p.Duration > 7200) return $"{n}: duration out of range";
            if (p.FanInRamp > 100 || p.FanOutRamp > 100) return $"{n}: fan out of range";
        }
        if (ComputeHash(m.Name, m.Version, m.Phases) != m.Hash) return "Hash mismatch";
        return null;
    }

    /// <summary>Operator action on the oven: applies a stored, Approved recipe (at the cycle boundary if a cycle is running).</summary>
    [ExportMethod]
    public void ApplyRecipe(string name, string version)
    {
        var id = new RecipeId { Name = name, Version = version };
        var status = GetMeta(id, "Status");
        if (status == null) { Reject(id, "Recipe is not stored on the oven"); return; }
        if (Convert.ToInt32(status) != StatusApproved) { Reject(id, "Only an Approved recipe can be applied"); return; }

        if (OvenSimulationLogic.CycleRunning)
        {
            pending = id;
            SetResult($"Pending: {name} v{version} is applied at the cycle boundary");
            Report(id, "Pending", "Cycle running: applied at the cycle boundary");
            return;
        }

        ApplyNow(id);
    }

    private void ApplyNow(RecipeId id)
    {
        var target = Project.Current.Get(TargetPath);
        var result = target == null
            ? TransferFromStoreToTargetResultCode.TargetNotFound
            : schema.TransferFromStoreToTarget(id, target.NodeId, ErrorPolicy.Strict);

        if (result != TransferFromStoreToTargetResultCode.Success)
        {
            Reject(id, $"Apply failed: {result}");
            return;
        }

        SetMeta(id, "Used", true);
        SetAppliedRecipe(id);
        SetResult($"Applied {id.Name} v{id.Version}");
        Report(id, "Applied", string.Empty);
        Log.Info(Cat, $"Applied {id.Name} v{id.Version}");
    }

    private void Reject(RecipeId id, string reason)
    {
        SetResult("ERROR: " + reason);
        Report(id, "Rejected", reason);
        Log.Warning(Cat, reason);
    }

    private void SetResult(string text)
    {
        if (lastResult != null) lastResult.Value = text;
    }

    /// <summary>Tells the supervisor which recipe the oven decided to apply (information only).</summary>
    private void Report(RecipeId id, string result, string reason)
    {
        Publish(new RecipeMessage { Type = "recipe.applyResult", Name = id.Name, Version = id.Version, Result = result, Reason = reason });
    }

    private void SetAppliedRecipe(RecipeId id)
    {
        try
        {
            var data = Project.Current.Get(OvenDataPath)?.Get(OwnId());
            var variable = data?.GetVariable("AppliedRecipe");
            if (variable != null) variable.Value = new UAValue(id);
        }
        catch (Exception ex)
        {
            Log.Warning(Cat, $"AppliedRecipe not written: {ex.Message}");
        }
    }

    private void Ack(RecipeMessage m, string result, string reason)
    {
        Publish(new RecipeMessage { Type = "recipe.ack", MsgId = m.MsgId, Name = m.Name, Version = m.Version, Hash = m.Hash, Result = result, Reason = reason });
        if (result == "Rejected") Log.Warning(Cat, $"Rejected {m.Name} v{m.Version}: {reason}");
    }

        #endregion

    #region RecipeX helpers

    private bool WriteValue(RecipeId id, string phase, string field, object value)
    {
        var code = schema.SetRecipeDataItemValue(id, new[] { phase }, new[] { field }, NoElementAccess(), value);
        if (code == SetRecipeDataItemValueResultCode.Success) return true;
        Log.Warning(Cat, $"{phase}/{field}: {code}");
        return false;
    }

    // The default struct has null arrays, which the store rejects ("Invalid range type"): use empty ones.
    private static ElementAccessStruct NoElementAccess() =>
        new ElementAccessStruct { ArrayIndex = new uint[0], FieldIndexes = new FieldIndexStruct[0] };

    private object ReadValue(RecipeId id, string phase, string field)
    {
        var r = schema.GetRecipeDataItemValue(id, new[] { phase }, new[] { field }, NoElementAccess());
        return r.ResultCode == GetRecipeDataItemValueResultCode.Success ? r.DataItemValue : null;
    }

    private object GetMeta(RecipeId id, string name)
    {
        var r = schema.GetRecipeMetadataValue(id, name);
        return r.ResultCode == GetRecipeMetadataValueResultCode.Success ? r.MetadataValue.Value : null;
    }

    private bool SetMeta(RecipeId id, string name, object value)
    {
        var code = schema.SetRecipeMetadataValue(id, name, value);
        if (code == SetRecipeMetadataValueResultCode.Success) return true;
        Log.Warning(Cat, $"Metadata '{name}' of {id.Name} v{id.Version}: {code}");
        return false;
    }

    private string StoredHash(RecipeId id)
    {
        var phases = new Dictionary<string, PhaseDto>();
        foreach (var n in PhaseNames)
        {
            phases[n] = new PhaseDto
            {
                TemperatureSetpoint = Convert.ToDouble(ReadValue(id, n, "TemperatureSetpoint") ?? 0.0, CultureInfo.InvariantCulture),
                Duration = Convert.ToUInt32(ReadValue(id, n, "Duration") ?? 0u),
                FanInRamp = Convert.ToByte(ReadValue(id, n, "FanInRamp") ?? (byte)0),
                FanOutRamp = Convert.ToByte(ReadValue(id, n, "FanOutRamp") ?? (byte)0),
                Heating = Convert.ToBoolean(ReadValue(id, n, "Heating") ?? false)
            };
        }
        return ComputeHash(id.Name, id.Version, phases);
    }

    /// <summary>Same canonical content hash as the supervisor (first 16 hex chars of SHA-256).</summary>
    private static string ComputeHash(string name, string version, Dictionary<string, PhaseDto> phases)
    {
        var sb = new StringBuilder($"{name}|{version}");
        foreach (var n in PhaseNames)
        {
            var p = phases[n];
            sb.Append('|').Append(p.TemperatureSetpoint.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(',').Append(p.Duration).Append(',').Append(p.FanInRamp).Append(',').Append(p.FanOutRamp).Append(',').Append(p.Heating ? 1 : 0);
        }
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Take(8).Select(b => b.ToString("x2")));
    }

    private static string StatusName(int status) => Enum.IsDefined(typeof(Wf), status) ? ((Wf)status).ToString() : "Draft";

    #endregion

    #region Wire types

    private enum Wf { Draft = 0, InReview = 1, Approved = 2, Deprecated = 3 }

    private sealed class PhaseDto
    {
        public double TemperatureSetpoint { get; set; }
        public uint Duration { get; set; }
        public byte FanInRamp { get; set; }
        public byte FanOutRamp { get; set; }
        public bool Heating { get; set; }
    }

    private sealed class RecipeInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Hash { get; set; }
        public string Status { get; set; }
    }

    private sealed class RecipeMessage
    {
        public string Type { get; set; }
        public string DeviceId { get; set; }
        public string MsgId { get; set; }
        public string CommandId { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Hash { get; set; }
        public string Status { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public Dictionary<string, PhaseDto> Phases { get; set; }
        public string Result { get; set; }
        public string Reason { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<RecipeInfo> Recipes { get; set; }
    }

    #endregion
}
