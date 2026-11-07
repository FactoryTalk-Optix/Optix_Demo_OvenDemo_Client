#region Using directives
using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.NativeUI;
using FTOptix.Alarm;
using FTOptix.OPCUAServer;
using FTOptix.MQTTClient;
using FTOptix.Retentivity;
using FTOptix.RecipeX;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.Core;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.System;
#endregion

/// <summary>
/// Edge side of the supervisor/oven chat; mirror of the supervisor's <c>ChatLogic</c>.
/// </summary>
/// <remarks>
/// <para>
/// History lives in the <c>ChatMessages</c> table of <c>DataStores/EmbeddedDatabase</c>, created at runtime. The
/// subscription <c>chat/&lt;OvenId&gt;/to-edge</c> is created at runtime too, because the topic depends on
/// <c>Model/HMISettings/DeviceId</c>; messages arrive in <c>Model/Chat/Inbox</c> and replies are published on
/// <c>chat/&lt;OvenId&gt;/to-supervisor</c>.
/// </para>
/// <para>
/// Same delivery rules as the supervisor: queued (<c>Status 0</c>), sent (<c>Status 1</c>), delivered after the
/// supervisor's ack (<c>Status 2</c>); duplicates are discarded by <c>MessageId</c>. The edge only knows its own
/// broker connection, so "sent" does not imply that the supervisor was listening. Every change of the table
/// increments <c>Model/Chat/Revision</c>, which the UI uses to refresh.
/// </para>
/// <para>See docs/OvenDemoNextGen_SupervisorEdgeChat_Design.md (sections 5 and 6).</para>
/// </remarks>
public class EdgeChatLogic : BaseNetLogic
{
    private const string LogNode = "EdgeChatLogic";
    private const string StoreName = "EmbeddedDatabase";
    private const string TableName = "ChatMessages";
    private const string ClientPath = "MQTT/MQTTEdgeDeviceClient";
    private const string SubscriberName = "ChatSubscriber";
    private const string SubscriptionName = "Chat";
    private const string OvenIdPath = "Model/HMISettings/DeviceId";
    private const string InboxPath = "Model/Chat/Inbox";
    private const string RevisionPath = "Model/Chat/Revision";
    private const string UnreadCountPath = "Model/Chat/UnreadCount";
    private const int MaxMessagesPerConversation = 500;
    private const int FlushPeriodMs = 2000;

    /// <summary>Values of the payload <c>Type</c> field.</summary>
    private const string TypeMessage = "msg";
    private const string TypeAck = "ack";

    /// <summary>Values of the <c>Direction</c> column, from the point of view of this node.</summary>
    private const int DirectionReceived = 0;
    private const int DirectionSent = 1;

    /// <summary>Values of the <c>Status</c> column (only meaningful for sent messages).</summary>
    private const int StatusQueued = 0;
    private const int StatusSent = 1;
    private const int StatusDelivered = 2;

    private static readonly object DbLock = new object();
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private long nextId;

    private IUAVariable inbox;
    private IUAVariable ovenId;
    private PeriodicTask flushTask;

    public override void Start()
    {
        try
        {
            EnsureTable();
            inbox = Project.Current.GetVariable(InboxPath);
            ovenId = Project.Current.GetVariable(OvenIdPath);
            if (inbox == null || ovenId == null)
            {
                Log.Error(LogNode, $"'{InboxPath}' or '{OvenIdPath}' not found, chat disabled.");
                return;
            }

            inbox.VariableChange += OnInboxChanged;
            ovenId.VariableChange += OnOvenIdChanged;
            EnsureSubscription();
            flushTask = new PeriodicTask(FlushQueued, FlushPeriodMs, LogicObject);
            flushTask.Start();
            Log.Info(LogNode, $"Chat started, table '{TableName}' ready (next Id {nextId}).");
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"Chat start failed: {ex.Message}");
        }
    }

    public override void Stop()
    {
        if (inbox != null)
            inbox.VariableChange -= OnInboxChanged;
        if (ovenId != null)
            ovenId.VariableChange -= OnOvenIdChanged;
        flushTask?.Dispose();
        flushTask = null;
    }

    /// <summary>
    /// Stores a message for the supervisor and publishes it right away if the MQTT client is connected; otherwise it
    /// stays queued until <see cref="FlushQueued"/> finds the client connected. Called by the Send button.
    /// </summary>
    /// <param name="text">The message text; empty or whitespace-only text is ignored.</param>
    [ExportMethod]
    public void SendMessage(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var deviceId = OwnId();
        if (string.IsNullOrEmpty(deviceId))
        {
            Log.Warning(LogNode, "SendMessage: Model/HMISettings/DeviceId is empty, message not sent.");
            return;
        }

        var messageId = Guid.NewGuid().ToString("D");
        var timestamp = DateTime.UtcNow;
        try
        {
            lock (DbLock)
            {
                InsertRow(deviceId, messageId, DirectionSent, text, timestamp, StatusQueued);
                TrimConversation(deviceId);
                if (PublishMessage(deviceId, messageId, text, timestamp))
                    SetStatus(messageId, deviceId, StatusSent);
            }
            BumpRevision();
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"SendMessage failed: {ex.Message}");
        }
    }

    /// <summary>Resets the unread counter. Meant to be called when the chat screen is shown.</summary>
    [ExportMethod]
    public void MarkAsRead()
    {
        var unread = Project.Current.GetVariable(UnreadCountPath);
        if (unread != null && Convert.ToInt32(unread.Value.Value) != 0)
            unread.Value = 0;
    }

    private void OnOvenIdChanged(object sender, VariableChangeEventArgs e)
    {
        EnsureSubscription();
    }

    /// <summary>
    /// Creates or repairs the <c>Chat</c> subscription (<c>chat/&lt;OvenId&gt;/to-edge</c>, QoS 1, into
    /// <c>Model/Chat/Inbox</c>) on the edge's MQTT client. A new or changed subscription only becomes active after
    /// the client is restarted, so the client is restarted in that case.
    /// </summary>
    private void EnsureSubscription()
    {
        try
        {
            var deviceId = OwnId();
            if (!IsValidDeviceId(deviceId))
            {
                Log.Warning(LogNode, "Model/HMISettings/DeviceId is empty or has characters not allowed in MQTT topics: chat subscription not created.");
                return;
            }

            var client = Project.Current.Get<MQTTClient>(ClientPath);
            var inboxNode = Project.Current.GetVariable(InboxPath);
            if (client == null || inboxNode == null)
            {
                Log.Error(LogNode, $"'{ClientPath}' or '{InboxPath}' not found, chat subscription not created.");
                return;
            }

            var changed = false;
            var subscriber = client.Get(SubscriberName) as MQTTSubscriber;
            if (subscriber == null)
            {
                subscriber = InformationModel.Make<MQTTSubscriber>(SubscriberName);
                client.Add(subscriber);
                changed = true;
            }

            var topic = $"chat/{deviceId}/to-edge";
            var existing = subscriber.Get("Subscriptions")?.Get(SubscriptionName) as SubscriptionPlainText;
            if (existing == null)
            {
                var subscription = InformationModel.Make<SubscriptionPlainText>(SubscriptionName);
                subscriber.Subscriptions.Add(subscription);
                subscription.Topic = topic;
                subscription.QoS = QoSLevel.AtLeastOnce;
                subscription.Variable = inboxNode.NodeId;
                changed = true;
            }
            else
            {
                if (existing.Topic != topic)
                {
                    existing.Topic = topic;
                    changed = true;
                }
                if (existing.QoS != QoSLevel.AtLeastOnce)
                {
                    existing.QoS = QoSLevel.AtLeastOnce;
                    changed = true;
                }
                if (existing.Variable != inboxNode.NodeId)
                {
                    existing.Variable = inboxNode.NodeId;
                    changed = true;
                }
            }

            if (!changed)
                return;

            client.Stop();
            client.Start();
            Log.Info(LogNode, $"Chat subscription '{topic}' ready, MQTT client restarted.");
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"Chat subscription setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles a JSON payload written into <c>Model/Chat/Inbox</c> by the <c>Chat</c> subscription. Payloads that are
    /// not valid JSON, are addressed to another device or carry an invalid Id are logged and discarded.
    /// </summary>
    private void OnInboxChanged(object sender, VariableChangeEventArgs e)
    {
        var json = (string)e.NewValue;
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var payload = JsonSerializer.Deserialize<ChatPayload>(json);
            var deviceId = OwnId();
            if (payload == null || payload.DeviceId != deviceId || !Guid.TryParse(payload.Id, out _))
            {
                Log.Warning(LogNode, $"Discarded chat payload for another device or with an invalid Id: {json}");
                return;
            }

            if (payload.Type == TypeAck)
                HandleAck(deviceId, payload);
            else if (payload.Type == TypeMessage)
                HandleMessage(deviceId, payload);
            else
                Log.Warning(LogNode, $"Discarded chat payload with unknown Type '{payload.Type}'.");
        }
        catch (JsonException ex)
        {
            Log.Warning(LogNode, $"Discarded chat payload that is not valid JSON ({ex.Message}): {json}");
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"Chat reception failed: {ex.Message}");
        }
    }

    /// <summary>Marks a sent message as delivered.</summary>
    private void HandleAck(string deviceId, ChatPayload payload)
    {
        lock (DbLock)
            SetStatus(Guid.Parse(payload.Id).ToString("D"), deviceId, StatusDelivered);
        BumpRevision();
    }

    /// <summary>Stores a received message, acknowledges it and increments the unread counter.</summary>
    private void HandleMessage(string deviceId, ChatPayload payload)
    {
        var messageId = Guid.Parse(payload.Id).ToString("D");
        var isNew = false;
        lock (DbLock)
        {
            if (!RowExists(messageId))
            {
                InsertRow(deviceId, messageId, DirectionReceived, payload.Text ?? string.Empty, ParseTimestampUtc(payload.Timestamp), StatusQueued);
                TrimConversation(deviceId);
                isNew = true;
            }
        }

        // A duplicate (QoS 1 redelivery) only regenerates the ack.
        Publish(deviceId, new ChatPayload { Type = TypeAck, DeviceId = deviceId, Id = messageId });
        if (isNew)
            IncrementUnread();
        BumpRevision();
    }

    /// <summary>Publishes the queued messages once the MQTT client is connected (runs periodically).</summary>
    private void FlushQueued()
    {
        try
        {
            var deviceId = OwnId();
            if (string.IsNullOrEmpty(deviceId) || !IsConnected())
                return;

            var flushed = false;
            lock (DbLock)
            {
                Query($"SELECT MessageId, Text, Timestamp FROM {TableName} WHERE DeviceId = '{EscapeLiteral(deviceId)}' AND Direction = {DirectionSent} AND Status = {StatusQueued} ORDER BY Id", out var rows);
                for (var r = 0; r < rows.GetLength(0); r++)
                {
                    var messageId = Convert.ToString(rows[r, 0]);
                    var timestamp = ParseTimestampUtc(Convert.ToString(rows[r, 2], CultureInfo.InvariantCulture));
                    if (!PublishMessage(deviceId, messageId, Convert.ToString(rows[r, 1]), timestamp))
                        break;

                    SetStatus(messageId, deviceId, StatusSent);
                    flushed = true;
                }
            }

            if (flushed)
                BumpRevision();
        }
        catch (Exception ex)
        {
            Log.Warning(LogNode, $"Flush of queued chat messages failed: {ex.Message}");
        }
    }

    /// <summary>Parses an ISO 8601 timestamp to UTC; falls back to the current time when it is missing or invalid.</summary>
    private static DateTime ParseTimestampUtc(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;
    }

    /// <summary>The DeviceId of this edge: the content of <c>Model/HMISettings/DeviceId</c>.</summary>
    private static string OwnId()
    {
        return Convert.ToString(Project.Current.GetVariable(OvenIdPath)?.Value.Value);
    }

    /// <summary>The DeviceId is part of MQTT topics: only letters, digits, '_' and '-' are accepted.</summary>
    private static bool IsValidDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;

        foreach (var c in deviceId)
        {
            var valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!valid)
                return false;
        }
        return true;
    }

    private static bool IsConnected()
    {
        return Project.Current.Get<MQTTClient>(ClientPath)?.Status == Status.Connected;
    }

    private static bool PublishMessage(string deviceId, string messageId, string text, DateTime timestamp)
    {
        return Publish(deviceId, new ChatPayload
        {
            Type = TypeMessage,
            DeviceId = deviceId,
            Id = messageId,
            Text = text,
            Timestamp = timestamp.ToString("o", CultureInfo.InvariantCulture)
        });
    }

    /// <summary>Publishes a payload to the supervisor (QoS 1, not retained); returns false when the client is not connected.</summary>
    private static bool Publish(string deviceId, ChatPayload payload)
    {
        var client = Project.Current.Get<MQTTClient>(ClientPath);
        if (client == null || client.Status != Status.Connected)
            return false;

        client.Publish(JsonSerializer.Serialize(new PublishConfig($"chat/{deviceId}/to-supervisor")), JsonSerializer.Serialize(payload, JsonOptions));
        return true;
    }

    private static void IncrementUnread()
    {
        var unread = Project.Current.GetVariable(UnreadCountPath);
        if (unread == null)
            return;

        lock (DbLock)
            unread.Value = Convert.ToInt32(unread.Value.Value) + 1;
    }

    /// <summary>Signals to the UI that the history changed (the ListView refreshes on this variable).</summary>
    private static void BumpRevision()
    {
        var revision = Project.Current.GetVariable(RevisionPath);
        if (revision == null)
            return;

        lock (DbLock)
            revision.Value = Convert.ToInt32(revision.Value.Value) + 1;
    }

    #region Store access

    private static Store GetStore()
    {
        var store = Project.Current.Get<Store>($"DataStores/{StoreName}");
        if (store == null)
            throw new InvalidOperationException($"Store 'DataStores/{StoreName}' not found.");
        return store;
    }

    /// <summary>
    /// Creates the table and its columns if missing and reads the last used <c>Id</c>. The table cannot be created at
    /// design time (AddTable is a runtime-only method) and the code assigns the <c>Id</c> itself: this logic is the
    /// only writer of the table.
    /// </summary>
    private void EnsureTable()
    {
        lock (DbLock)
        {
            var store = GetStore();
            if (store.Tables.Get(TableName) == null)
            {
                store.AddTable(TableName);
                Log.Info(LogNode, $"Created table '{TableName}'.");
            }

            var table = store.Tables.Get(TableName);
            EnsureColumn(table, "Id", OpcUa.DataTypes.Int64);
            EnsureColumn(table, "MessageId", OpcUa.DataTypes.String);
            EnsureColumn(table, "DeviceId", OpcUa.DataTypes.String);
            EnsureColumn(table, "Direction", OpcUa.DataTypes.Int32);
            EnsureColumn(table, "Text", OpcUa.DataTypes.String);
            EnsureColumn(table, "Timestamp", OpcUa.DataTypes.DateTime);
            EnsureColumn(table, "Status", OpcUa.DataTypes.Int32);

            Query($"SELECT MAX(Id) FROM {TableName}", out var result);
            var max = result.GetLength(0) > 0 && result[0, 0] != null && !(result[0, 0] is DBNull) ? Convert.ToInt64(result[0, 0]) : 0;
            nextId = max + 1;
        }
    }

    private static void EnsureColumn(Table table, string name, NodeId dataType)
    {
        if (table.Columns.Get(name) != null)
            return;

        table.AddColumn(name, dataType);
        Log.Info(LogNode, $"Added column '{name}' to '{table.BrowseName}'.");
    }

    private void InsertRow(string deviceId, string messageId, int direction, string text, DateTime timestamp, int status)
    {
        var columns = new[] { "Id", "MessageId", "DeviceId", "Direction", "Text", "Timestamp", "Status" };
        var values = new object[1, columns.Length];
        values[0, 0] = nextId++;
        values[0, 1] = messageId;
        values[0, 2] = deviceId;
        values[0, 3] = direction;
        values[0, 4] = text;
        values[0, 5] = timestamp;
        values[0, 6] = status;
        GetStore().Insert(TableName, columns, values);
    }

    /// <summary>Keeps only the newest <see cref="MaxMessagesPerConversation"/> rows of the conversation.</summary>
    private static void TrimConversation(string deviceId)
    {
        var device = EscapeLiteral(deviceId);
        Execute($"DELETE FROM {TableName} WHERE DeviceId = '{device}' AND Id <= (SELECT Id FROM {TableName} WHERE DeviceId = '{device}' ORDER BY Id DESC LIMIT 1 OFFSET {MaxMessagesPerConversation})");
    }

    /// <summary>Sets the status of a sent message.</summary>
    private static void SetStatus(string messageId, string deviceId, int status)
    {
        Execute($"UPDATE {TableName} SET Status = {status} WHERE MessageId = '{EscapeLiteral(messageId)}' AND DeviceId = '{EscapeLiteral(deviceId)}' AND Direction = {DirectionSent}");
    }

    private static bool RowExists(string messageId)
    {
        Query($"SELECT COUNT(*) FROM {TableName} WHERE MessageId = '{EscapeLiteral(messageId)}'", out var result);
        return result.GetLength(0) > 0 && Convert.ToInt64(result[0, 0]) > 0;
    }

    private static void Query(string sql, out object[,] result)
    {
        GetStore().Query(sql, out _, out result);
    }

    private static void Execute(string sql)
    {
        GetStore().Query(sql, out _, out _);
    }

    private static string EscapeLiteral(string value) => (value ?? string.Empty).Replace("'", "''");

    #endregion

    /// <summary>Chat message or acknowledgement; the same JSON travels in both directions. Text and Timestamp are omitted for acks.</summary>
    private sealed class ChatPayload
    {
        public string Type { get; set; }
        public string DeviceId { get; set; }
        public string Id { get; set; }
        public string Text { get; set; }
        public string Timestamp { get; set; }
    }

    /// <summary>JSON config expected as first argument of <c>MQTTClient.Publish</c> (Topic/QoS/Retain).</summary>
    private sealed class PublishConfig
    {
        public string Topic { get; }
        public int QoS { get; } = 1;
        public bool Retain { get; } = false;

        public PublishConfig(string topic)
        {
            Topic = topic;
        }
    }
}
