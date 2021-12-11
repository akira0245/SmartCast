using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Configuration;

namespace SmartCast
{
	class Config : IPluginConfiguration
	{
		//public bool EnableCastingPositionLock = false;
		public bool EnableSmartCast = true;
		public bool GroundTargetSmartCastForNonPlayerSpell = true;

		public bool QueueMacroAction = true;
		public bool AutoDismount = true;
		public bool AutoDismountAndCast = true;
        public bool MouseOverFriendly = false;
        public bool MoveToCameraDirection = true;
        public int Version { get; set; }
	}
}
