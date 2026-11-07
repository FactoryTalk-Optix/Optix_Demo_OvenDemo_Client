#region Using directives
using System;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.HMIProject;
using FTOptix.System;
#endregion

/// <summary>
/// Attach as a NetLogic child of the Chat screen. While the screen is open every message is already seen,
/// so Model/Chat/UnreadCount is reset on open and whenever EdgeChatLogic increments it.
/// </summary>
public class ChatLogic : BaseNetLogic
{
    private const string UnreadCountPath = "Model/Chat/UnreadCount";

    private IUAVariable unread;

    public override void Start()
    {
        unread = Project.Current.GetVariable(UnreadCountPath);
        if (unread == null)
        {
            Log.Warning("ChatScreenLogic", $"'{UnreadCountPath}' not found, unread counter not reset.");
            return;
        }

        Reset();
        unread.VariableChange += OnUnreadChanged;
    }

    public override void Stop()
    {
        if (unread != null)
            unread.VariableChange -= OnUnreadChanged;
    }

    private void OnUnreadChanged(object sender, VariableChangeEventArgs e) => Reset();

    private void Reset()
    {
        if (Convert.ToInt32(unread.Value.Value) != 0)
            unread.Value = 0;
    }
}
