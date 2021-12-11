using Dalamud.Configuration;

namespace SmartCast
{
    internal class Config : IPluginConfiguration
    {
        //public bool EnableCastingPositionLock = false;
        public bool EnableSmartCast = true;
        public bool GroundTargetSmartCastForNonPlayerSpell = true;

        public bool QueueMacroAction = true;
        public bool AutoDismount = true;
        public bool AutoDismountAndCast = true;
        public bool MouseOverFriendly = true;
        public int Version { get; set; }
    }
}