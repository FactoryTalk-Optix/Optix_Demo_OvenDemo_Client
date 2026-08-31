#region Using directives
using System;
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

public class SendMessageToOvenLogic : BaseNetLogic
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    /// <summary>
    /// Sends a message to a specific oven by publishing it to MQTT.
    /// </summary>
    /// <param name="targetOvenId">The unique identifier of the oven to send the message to.</param>
    /// <param name="message">The message to send.</param>
    [ExportMethod]
    public void SendMessageToOven(string targetOvenId, string message)
    {
        if (string.IsNullOrEmpty(targetOvenId))
        {
            Log.Warning("SendMessageToOven", "Invalid oven NodeId provided");
            return;
        }

        if (string.IsNullOrEmpty(message))
        {
            Log.Warning("SendMessageToOven", "Message cannot be null or empty");
            return;
        }

        try
        {
            // Create the message payload
            var payload = new OvenMessagePayload
            {
                OvenId = targetOvenId,
                Source = Project.Current.GetVariable("Model/Oven/OptixOven/OvenId").Value,
                Timestamp = DateTime.UtcNow,
                Message = message
            };

            // Serialize and publish to MQTT
            var jsonPayload = JsonSerializer.Serialize(payload);
            var result = PublishStringToMqtt(jsonPayload, "optix/iot/ovenmessages");

            if (result)
            {
                Log.Info("SendMessageToOven", $"Message sent to oven '{targetOvenId}': {message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("SendMessageToOven", $"Failed to send message to oven: {ex.Message}");
        }
    }

    /// <summary>
    /// Payload structure for oven messages published to MQTT.
    /// </summary>
    public class OvenMessagePayload
    {
        public string OvenId { get; set; }
        public string Source { get; set; }
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
    }

    public static bool PublishStringToMqtt(string payload, string topic)
    {
        try
        {
            var mqttClient = Project.Current.Get<FTOptix.MQTTClient.MQTTClient>("MQTT/MQTTClient");
            var mqttConfig = new OptixMqttConfig(topic);
            var result = mqttClient.Publish(mqttConfig.Serialize(), payload);
            Log.Verbose1($"Published recipe to MQTT with result: {result}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("PublishStringToMqtt", $"Cannot publish recipe: {ex.Message}");
            return false;
        }
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
}
