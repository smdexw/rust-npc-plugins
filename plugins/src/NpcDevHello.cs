namespace Oxide.Plugins
{
    [Info("NpcDevHello", "LocalDev", "0.1.0")]
    [Description("Checks that the NPC development plugin environment is ready.")]
    public class NpcDevHello : RustPlugin
    {
        private void OnServerInitialized()
        {
            Puts("NPC development environment ready. This sample does not spawn NPCs.");
        }

        [ChatCommand("npcdev")]
        private void NpcDevCommand(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;
            SendReply(player, "NpcDevHello 0.1.0 is loaded.");
        }
    }
}
