#region Using directives
using System;
using System.Linq;
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
using System.Threading;
#endregion

public class OvenScreenLogic : BaseNetLogic
{
    private IUAVariable incomingMessagesVariable;
    private DialogType notificationDialogType;

    public override void Start()
    {
        // Get the NotificationDialog type
        notificationDialogType = Project.Current.Get<DialogType>("UI/Types/DialogBoxes/NotificationDialog");
        if (notificationDialogType == null)
        {
            Log.Warning("IncomingMessagesLogic", "UI/Types/DialogBoxes/NotificationDialog not found");
            return;
        }

        // Subscribe to changes on the IncomingMessages variable
        incomingMessagesVariable = Project.Current.GetVariable("Model/Subscriber/IncomingMessage");
        if (incomingMessagesVariable == null)
        {
            Log.Warning("IncomingMessagesLogic", "Model/Subscriber/IncomingMessage variable not found");
            return;
        }

        incomingMessagesVariable.VariableChange += OnIncomingMessageChanged;

        // Start publisher
        var mqttPublisher = Project.Current.Get<MQTTPublisher>("MQTT/MQTTClient/MQTTPublisher1");
        mqttPublisher.SamplingMode = FTOptix.MQTTClient.SamplingMode.Periodic;
        mqttPublisher.SamplingPeriod = 1000; // Publish every second
        mqttPublisher.Stop();
        Thread.Sleep(100); // Short delay to ensure settings are applied
        mqttPublisher.Start();
    }

    public override void Stop()
    {
        // Unsubscribe from the event
        if (incomingMessagesVariable != null)
        {
            incomingMessagesVariable.VariableChange -= OnIncomingMessageChanged;
        }
    }

    private void OnIncomingMessageChanged(object sender, VariableChangeEventArgs e)
    {
        try
        {
            var messageJson = e.NewValue?.Value?.ToString();
            if (string.IsNullOrEmpty(messageJson))
                return;

            // Parse the incoming message
            var payload = JsonSerializer.Deserialize<SendMessageToOvenLogic.OvenMessagePayload>(messageJson);
            if (payload == null || string.IsNullOrEmpty(payload.OvenId))
            {
                Log.Warning("IncomingMessagesLogic", "Invalid message payload received");
                return;
            }

            var ovenIdVariable = Project.Current.GetVariable("Model/Oven/OptixOven/OvenId");

            if (ovenIdVariable.Value == payload.OvenId)
            {
                // Open the notification dialog and display the message, passing the oven NodeId for reply
                var sourceOvenIdVariable = Project.Current.GetVariable("Model/Subscriber/IncomingMessage/Source");
                sourceOvenIdVariable.Value = payload.Source; // Store source for potential replies
                ShowNotificationDialog($"Received at: {payload.Timestamp} UTC:\n\n{payload.Message}", payload.Source, sourceOvenIdVariable.NodeId);
            }

            Log.Verbose1("IncomingMessagesLogic", $"Message received for non-matching oven: {payload.OvenId}");
            return;
        }
        catch (JsonException ex)
        {
            Log.Warning("IncomingMessagesLogic", $"Failed to parse incoming message: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("IncomingMessagesLogic", $"Error processing incoming message: {ex.Message}");
        }
    }

    private void ShowNotificationDialog(string message, string sender, NodeId targetOvenNodeId)
    {
        try
        {
            // Create alias array to pass the TargetOvenId to the dialog's IncomingTargetOvenId alias
            var aliasArray = new NodeId[] { targetOvenNodeId };

            // Open dialog on Owner (the parent graphical container) with the alias
            var dialogInstance = UICommands.OpenDialog(Owner, notificationDialogType, aliasArray);
            if (dialogInstance == null)
            {
                Log.Warning("IncomingMessagesLogic", "Failed to open NotificationDialog");
                return;
            }

            // Set the title with the sender ID
            var titleVariable = dialogInstance.Get<Label>("NotificationCard1/Title");
            if (titleVariable != null)
            {
                titleVariable.Text = $"Message from: {sender}";
            }
            else
            {
                Log.Warning("IncomingMessagesLogic", "Title variable not found in dialog");
            }

            // Set the message text in the dialog
            var messageTextVariable = dialogInstance.Get<Label>("NotificationCard1/MessageScrollView/Message");
            if (messageTextVariable != null)
            {
                messageTextVariable.Text = message;
            }
            else
            {
                Log.Warning("IncomingMessagesLogic", "Message text variable not found in dialog");
            }
        }
        catch (Exception ex)
        {
            Log.Error("IncomingMessagesLogic", $"Failed to show notification dialog: {ex.Message}");
        }
    }
}
