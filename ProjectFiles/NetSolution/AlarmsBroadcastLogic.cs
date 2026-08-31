#region Using directives
using System;
using System.Collections.Generic;
using System.Text.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.Alarm;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
#endregion

public class AlarmsBroadcastLogic : BaseNetLogic
{
    public override void Start()
    {
        // Publish alarms to MQTT broker at regular intervals
        publishTask = new PeriodicTask(PublishAlarmsToMqtt, 1000, LogicObject);
        publishTask.Start();
    }

    public override void Stop()
    {
        // Dispose the periodic task when the logic is stopped
        publishTask?.Dispose();
    }

    private void PublishAlarmsToMqtt()
    {
        try
        {
            // Get the RetainedAlarms object which contains active alarms
            var retainedAlarmsObject = LogicObject.Context.GetNode(FTOptix.Alarm.Objects.RetainedAlarms);
            if (retainedAlarmsObject == null)
            {
                Log.Warning("PublishAlarmsToMqtt", "RetainedAlarms object not found");
                return;
            }

            // Get the LocalizedAlarms container
            var localizedAlarmsObject = retainedAlarmsObject.GetVariable("LocalizedAlarms");
            if (localizedAlarmsObject == null)
            {
                Log.Warning("PublishAlarmsToMqtt", "LocalizedAlarms variable not found");
                return;
            }

            var localizedAlarmsNodeId = (NodeId)localizedAlarmsObject.Value;
            IUANode localizedAlarmsContainer = null;
            if (localizedAlarmsNodeId?.IsEmpty == false)
                localizedAlarmsContainer = LogicObject.Context.GetNode(localizedAlarmsNodeId);

            // Prepare the list of alarm payloads
            var listOfAlarmPayloads = new List<AlarmPayload>();

            if (localizedAlarmsContainer != null)
            {
                // Iterate through all active alarms
                foreach (var alarmNode in localizedAlarmsContainer.Children)
                {
                    try
                    {
                        var alarmPayload = new AlarmPayload
                        {
                            OvenName = CurrentOvenName,
                            OvenId = CurrentOvenId,
                            AlarmName = alarmNode.BrowseName,
                            SourceName = GetVariableValue<string>(alarmNode, "SourceName"),
                            Message = GetLocalizedTextValue(alarmNode, "Message"),
                            Severity = GetVariableValue<ushort>(alarmNode, "Severity"),
                            IsActive = GetVariableValue<bool>(alarmNode, "ActiveState/Id"),
                            IsAcknowledged = GetVariableValue<bool>(alarmNode, "AckedState/Id"),
                            IsConfirmed = GetVariableValue<bool>(alarmNode, "ConfirmedState/Id"),
                            Time = GetVariableValue<DateTime>(alarmNode, "Time"),
                            Timestamp = DateTime.UtcNow
                        };

                        listOfAlarmPayloads.Add(alarmPayload);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("PublishAlarmsToMqtt", $"Error processing alarm {alarmNode.BrowseName}: {ex.Message}");
                    }
                }
            }

            // Publish list of alarms to MQTT
            var alarmsListAsJson = JsonSerializer.Serialize(listOfAlarmPayloads);
            _ = PublishStringToMqtt(alarmsListAsJson, "optix/iot/alarmslist");
        }
        catch (Exception ex)
        {
            Log.Warning("PublishAlarmsToMqtt", $"Cannot publish alarms: {ex.Message}");
        }
    }

    public static bool PublishStringToMqtt(string payload, string topic)
    {
        try
        {
            var mqttClient = Project.Current.Get<FTOptix.MQTTClient.MQTTClient>("MQTT/MQTTClient");
            var mqttConfig = new OptixMqttConfig(topic);
            var result = mqttClient.Publish(mqttConfig.Serialize(), payload);
            Log.Verbose1($"Published alarms to MQTT with result: {result}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("PublishStringToMqtt", $"Cannot publish recipe: {ex.Message}");
            return false;
        }
    }

    private T GetVariableValue<T>(IUANode node, string variablePath)
    {
        try
        {
            var variable = node.GetVariable(variablePath);
            if (variable != null && variable.Value != null)
            {
                return (T)Convert.ChangeType(variable.Value.Value, typeof(T));
            }
        }
        catch
        {
            // Return default value if variable not found or conversion fails
        }
        return default;
    }

    private string GetLocalizedTextValue(IUANode node, string variablePath)
    {
        try
        {
            var variable = node.GetVariable(variablePath);
            if (variable?.Value?.Value is LocalizedText localizedText)
            {
                return localizedText.Text;
            }
            return variable?.Value?.Value?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public class AlarmPayload
    {
        public string OvenName { get; set; }
        public string OvenId { get; set; }
        public string AlarmName { get; set; }
        public string SourceName { get; set; }
        public string Message { get; set; }
        public ushort Severity { get; set; }
        public bool IsActive { get; set; }
        public bool IsAcknowledged { get; set; }
        public bool IsConfirmed { get; set; }
        public DateTime Time { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class OptixMqttConfig
    {
        public string Topic { get; }
        public int QoS { get; }
        public bool Retain { get; }

        public OptixMqttConfig(string topicName, int qos = 2, bool retain = true)
        {
            Topic = topicName;
            QoS = qos;
            Retain = retain;
        }

        public string Serialize()
        {
            return JsonSerializer.Serialize(this);
        }
    }

    private PeriodicTask publishTask;
    private string CurrentOvenId = Project.Current.GetVariable("Model/Oven/OptixOven/OvenId")?.Value?.Value?.ToString() ?? "UnknownOvenId";
    private string CurrentOvenName = Project.Current.GetVariable("Model/Oven/OptixOven/OvenName")?.Value?.Value?.ToString() ?? "UnknownOvenName";
}
