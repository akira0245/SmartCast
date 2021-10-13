using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Plugin;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using SmartCast.DalamuApi;
using Action = Lumina.Excel.GeneratedSheets.Action;

namespace SmartCast
{
	public unsafe class SmartCast : IDalamudPlugin
	{

		internal Config config;
		private UI pluginUI;

		private Dictionary<uint, Action> GroundTargetActions;
		private Dictionary<uint, Action> DismountActions;
		private HashSet<uint> BattleJobs;

		private IntPtr _doActionFunc;
		private IntPtr _doActionLocationFunc;
		private IntPtr _mouseToWorldFunc;
		private IntPtr _actionReadyFunc;
		private IntPtr _canCastFunc;
		private IntPtr _getAdjustedActionIdFunc;

		//private delegate*<long, uint, uint, long, byte, byte, long> GetActionStatus;
		delegate long GetAdjustedActionIdDelegate(IntPtr a1, uint a2);
		private GetAdjustedActionIdDelegate _getAdjustedActionId;

		private delegate ulong CanCastDelegate(long a1, uint a2, uint a3, long a4, byte a5, byte a6);
		private CanCastDelegate _canCast;

		private delegate byte DoActionDelegate(long a1, uint a2, uint a3, long a4, int a5, uint a6, int a7);
		private DoActionDelegate _doAction;
		private Hook<DoActionDelegate> _doActionHook;

		private unsafe delegate byte DoActionLocationDelegate(long a1, uint actionType, uint actionId, long a4/* 0xE0000000 */, Vector3* a5, uint a6/* 0 */);
		private DoActionLocationDelegate _doActionLocation;
		private Hook<DoActionLocationDelegate> _doActionLocationHook;

		private delegate void MouseToWorldDelegate(long a1, uint spellid, uint a3, long result);
		private MouseToWorldDelegate _mouseToWorld;

		private delegate long ActionReadyDelegate(long a1, uint actionType, uint actionId);
		private ActionReadyDelegate _actionReady;

        public delegate byte MacroDetor(long param_1, uint param_2, uint param_3, long param_4);
		private MacroDetor _macroDetor;
        private Vector3 queuedPos;
        private uint queuedTarget;
        
		internal ActionManager* actionManager;

		internal void SavePluginConfig() => DalamudApi.PluginInterface.SavePluginConfig(config);

		public void Dispose()
		{
			pluginUI.Dispose();
			DalamudApi.Framework.Update -= OnFramework;
			DalamudApi.Dispose();
			_doActionLocationHook?.Dispose();
			_doActionHook?.Dispose();
		}

		public SmartCast(DalamudPluginInterface pluginInterface)
		{
			DalamudApi.Initialize(this, pluginInterface);
			GroundTargetActions = DalamudApi.DataManager.GetExcelSheet<Action>().Where(i => i.TargetArea && i.RowId != 7419 && i.RowId != 3573).ToDictionary(i => i.RowId, j => j);
			DismountActions = DalamudApi.DataManager.GetExcelSheet<Action>().Where(i => i.IsPlayerAction && i.RowId > 8 && i.ActionCategory?.Value.RowId is 2 or 3 or 4 or 9 or 15).ToDictionary(i => i.RowId, j => j);
			BattleJobs = DalamudApi.DataManager.GetExcelSheet<ClassJob>().Where(i => i.ClassJobCategory?.Value?.RowId is 30 or 31).Select(i => i.RowId).ToHashSet();

			actionManager = (ActionManager*)DalamudApi.SigScanner.GetStaticAddressFromSig("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B F8 8B CF");
			//ConditionsPtr = (byte*)DalamudApi.SigScanner.GetStaticAddressFromSig("48 8D 0D ?? ?? ?? ?? BA ?? ?? ?? ?? E8 ?? ?? ?? ?? B0 01 48 83 C4 30");
			_canCastFunc = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 83 BC 24 ?? ?? ?? ?? ?? 8B F0");
			_doActionFunc = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? EB 64 B1 01");
			_mouseToWorldFunc = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 41 B6 01 44 38 74 24 ??");
			_doActionLocationFunc = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 3C 01 0F 85 ?? ?? ?? ?? EB 46");
			_actionReadyFunc = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 3C 01 74 45");
			_getAdjustedActionIdFunc = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 8B F8 3B DF ");
			ActionManager.actionCommandRequestTypePtr = DalamudApi.SigScanner.ScanText("02 00 00 00 45 8B C5 89");

			_getAdjustedActionId = Marshal.GetDelegateForFunctionPointer<GetAdjustedActionIdDelegate>(_getAdjustedActionIdFunc);
			_mouseToWorld = Marshal.GetDelegateForFunctionPointer<MouseToWorldDelegate>(_mouseToWorldFunc);
			_doActionLocation = Marshal.GetDelegateForFunctionPointer<DoActionLocationDelegate>(_doActionLocationFunc);
			_actionReady = Marshal.GetDelegateForFunctionPointer<ActionReadyDelegate>(_actionReadyFunc);
			_canCast = Marshal.GetDelegateForFunctionPointer<CanCastDelegate>(_canCastFunc);
			_doAction = Marshal.GetDelegateForFunctionPointer<DoActionDelegate>(_doActionFunc);
			//_doActionLocationHook = new Hook<DoActionLocationDelegate>(_doActionLocationFunc, DetourL);
			//_doActionLocationHook.Enable();
            _macroDetor =
                Marshal.GetDelegateForFunctionPointer<MacroDetor>(
                    DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 33 C0 4C 8B B4 24 ?? ?? ?? ??"));

			_doActionHook = new Hook<DoActionDelegate>(_doActionFunc, Detour);
			_doActionHook.Enable();

			config = (Config)pluginInterface.GetPluginConfig() ?? new Config();
			pluginUI = new UI(this);
			DalamudApi.Framework.Update += OnFramework;
			if (pluginInterface.Reason is not PluginLoadReason.Boot or PluginLoadReason.Update)
			{
				pluginUI.Visible = true;
			}
		}

		private unsafe byte DetourL(long a1, uint actiontype, uint actionid, long targetId, Vector3* a5, uint a6)
		{
			Log.Debug($"_doActionLocation {a1:X}, actiontype: {actiontype}, actionid: {actionid}, a4: {targetId:X}, a5: {*a5}, a6: {a6},");
			var original = _doActionLocationHook.Original.Invoke(a1, actiontype, actionid, targetId, a5, a6);
			Log.Debug($"_doActionLocation original: {original}");

			return original;
		}

		[Command("/smartcast")]
		[HelpMessage("open SmartCast config" +
					 "\n/smartcast <on/off/toggle> → enable/disable ground target action smart cast" +
					 "\n/smartcast nonplayer <on/off/toggle> → enable/disable non-player ground target action smart cast")]
		public void Command1(string command, string args)
		{
			if (string.IsNullOrWhiteSpace(args))
			{
				pluginUI.Visible ^= true;
			}
			else
			{
				if (args.Trim().ToLowerInvariant().StartsWith("nonplayer"))
				{
					toggle("non-player action smart cast", args.Replace("nonplayer", ""),
						ref config.GroundTargetSmartCastForNonPlayerSpell);
				}
				else
				{
					toggle("SmartCast", args, ref config.EnableSmartCast);
				}
			}
		}

		[Command("/queuemacro")]
		[HelpMessage("<on/off> enable/disable queuing macro actions")]
		public void Command2(string command, string args)
		{
			toggle("Macro queue", args, ref config.QueueMacroAction);
		}

		[Command("/smartdismount")]
		[HelpMessage("<on/off> enable/disable auto dismount on action")]
		public void Command3(string command, string args)
		{
			toggle("Auto dismount", args, ref config.AutoDismount);
		}

		void toggle(string displayName, string input, ref bool value)
		{
			var s = input.ToLowerInvariant().Trim();
			_ = s switch
			{
				"on" => value = true,
				"off" => value = false,
				_ => value = !value
			};
			DalamudApi.ChatGui.Print($"[SmartCast] {displayName} {(value ? "enabled." : "disabled.")}");
		}

		private (long a1, uint actionType, uint actionId, long targetId, int a5, uint castFrom, int a7)? waitedAction = null;
		private DateTime? tryTime = null;

		private void OnFramework(Dalamud.Game.Framework framework)
		{

			if (waitedAction == null || tryTime == null || tryTime < DateTime.Now) return;

			(long a1, uint actionType, uint actionId, long targetId, int a5, uint castFrom, int a7) = waitedAction.Value;

			Log.Verbose($"Cast queue: actionType: {actionType}, actionId: {actionId}, targetId: {targetId:X}, castFrom: {castFrom}, a5: {a5}, a7: {a7}");

			var actionReady = _actionReady(a1, actionType, actionId);
			Log.Verbose($"_actionReady: {actionReady}");

			if (actionReady == 0)
			{
				var cancast = _canCast.Invoke(a1, actionType, actionId, targetId, 1, 1);
				Log.Verbose($"cancast: {cancast}");
				if (cancast == 0)
				{
					var doaction = _doAction.Invoke(a1, actionType, actionId, targetId, a5, castFrom, a7);
					Log.Debug($"Cancast passed: DoAction ret: {doaction}, actionType: {actionType}, actionId: {actionId}, targetId: {targetId:X}, castFrom: {castFrom}, a5: {a5}, a7: {a7}");
					if (doaction == 1)
					{
						waitedAction = null;
						tryTime = null;
					}
				}
			}
		}

        void QueueAction(long a1,uint type,uint adjustedId,uint targetId,uint castFrom)
        {
            actionManager->IsQueued = true;
            actionManager->QueuedActionType = 1;
            actionManager->queuedActionId = adjustedId;
            actionManager->queuedActionTargetId = targetId;
            actionManager->QueuedUseType = 0;
            actionManager->QueuedPVPAction = 0;
            if (castFrom == 2)
            {
                queuedTarget = targetId;
				queuedPos = Vector3.Zero;
            }
            else
            {
                MouseToWorld(a1, adjustedId, type, out var mouseOnWorld, out var success, out var worldPos);
                if (success & mouseOnWorld)
                {
                    queuedPos = worldPos;
                    queuedTarget = 0xE000_0000;
                }
            }
			Log.Debug($"QUEUED:{castFrom} {adjustedId} {targetId}");
        }

		private byte Detour(long a1, uint actionType, uint actionId, long targetId, int a5, uint castFrom, int a7)
		{

			var rawAdjustedId = _getAdjustedActionId((IntPtr)a1, actionId);
			var adjustedId = (uint)rawAdjustedId;
			if (config.AutoDismount)
			{
				var mounted = DalamudApi.Condition[ConditionFlag.Mounted];
				if (mounted && (ActionType)actionType == ActionType.Spell)
				{

					if (DismountActions.ContainsKey(adjustedId) && DalamudApi.ClientState.LocalPlayer is not null && BattleJobs.Contains(DalamudApi.ClientState.LocalPlayer.ClassJob.Id))
					{
						if (config.AutoDismountAndCast)
						{
							//PluginLog.Debug($"RawID: {actionId}->{rawAdjustedId}");
							waitedAction = (a1, actionType, adjustedId, targetId, a5, castFrom, a7);
							tryTime = DateTime.Now.AddSeconds(1);
						}
						Log.Debug($"Used actionType: {actionType}, actionId: {actionId}->{adjustedId}, Dismounting!");
						return _doActionHook.Original.Invoke(a1, (uint)ActionType.General, 23, 0xE000_0000, 0, 0, 0);
					}
				}

			}

            if (config.EnableSmartCast)
            {
                if (GroundTargetActions.TryGetValue(actionId, out var action)){
                    if (config.GroundTargetSmartCastForNonPlayerSpell || action.IsPlayerAction)
                    {
                        if (_canCast(a1, actionType, actionId, targetId, 1, 1) != 0UL && _actionReady(a1, actionType, actionId) == 0L)
                        {
                            
                            if (!actionManager->IsQueued)
                            {
                                queuedPos = Vector3.Zero;
                                queuedTarget = 0xE000_0000;
                                QueueAction(a1, actionType, actionId, (uint)targetId, castFrom);
                                return 1;
                            }

                        }
                        else if (_actionReady(a1, actionType, actionId) == 0L)
                        {
                            if (castFrom == 1)
                            {
                                if (queuedPos != Vector3.Zero)
                                {
                                    
                                    {
                                        var pos = queuedPos;
                                        byte b = this._doActionLocation(a1, actionType, actionId, targetId, &pos, 0U);
                                        Log.Debug($"_doActionLocation ret: {b}");
                                        queuedPos = Vector3.Zero;
                                        queuedTarget = 0xE000_0000;
                                        return b;
                                    }
                                }

                                if (queuedTarget != 0xE000_0000)
                                {
									Log.Debug($"exec {queuedTarget}");
                                    var r = _doActionHook.Original(a1, actionType, actionId, targetId, a5, castFrom,
                                        a7);
                                    _macroDetor(a1, actionType, actionId, queuedTarget);
                                    return r;
                                }
                            }

                            if (castFrom != 2)
                            {
                                MouseToWorld(a1, actionId, actionType, out var mouseOnWorld, out var success, out var worldPos);
                                Log.Debug($"targetId: {targetId:X}, castFrom: {castFrom}, mouseOnWorld: {mouseOnWorld}, success: {success}, location: {worldPos}");
                                if (mouseOnWorld && success)
                                {
                                    
                                    {
                                        byte b = this._doActionLocation(a1, actionType, actionId, targetId, &worldPos, 0U);
                                        Log.Debug($"_doActionLocation ret: {b}");
                                        return b;
                                    }
                                }
                            }
                        }
                    }

                }
            }


			//if (config.EnableSmartCast)
			//{
			//	if (GroundTargetActions.TryGetValue(actionId, out var action))
			//	{
			//		if (config.GroundTargetSmartCastForNonPlayerSpell || action.IsPlayerAction)
			//		{
			//			if (!actionManager->IsQueued)
			//			{
			//				actionManager->IsQueued = true;
			//				actionManager->QueuedActionType = 1;
			//				actionManager->queuedActionId = adjustedId;
			//				actionManager->queuedActionTargetId = targetId;
			//				//actionManager->QueuedUseType = 0;
			//				//actionManager->QueuedPVPAction = 0;
			//				return 1;
			//			}
						
			//			if (castFrom != 2U || targetId == 0xE000_0000)
			//			{
			//				if (_actionReady(a1, actionType, actionId) == 0L && _canCast(a1, actionType, actionId, targetId, 1, 1) == 0UL)
			//				{
			//					MouseToWorld(a1, actionId, actionType, out var mouseOnWorld, out var success, out var worldPos);
			//					Log.Debug($"targetId: {targetId:X}, castFrom: {castFrom}, mouseOnWorld: {mouseOnWorld}, success: {success}, location: {worldPos}");
			//					if (mouseOnWorld && success)
			//					{
			//						unsafe
			//						{
			//							byte b = this._doActionLocation(a1, actionType, actionId, targetId, &worldPos, 0U);
			//							Log.Debug($"_doActionLocation ret: {b}");
			//							return b;
			//						}
			//					}
			//				}
			//			}
			//		}
			//	}
			//}

			//if (config.QueueMacroAction && castFrom == 2U)
			//{
			//	castFrom = 0U;
			//}


			{

				var original = this._doActionHook.Original(a1, actionType, actionId, targetId, a5, castFrom, a7);
				Log.Debug($"DoAction ret: {original}({original:X}) original: A1:{a1:X}, actionType: {actionType}, actionId: {actionId}, adjustedId: {rawAdjustedId}, targetId: {targetId:X}, castFrom: {castFrom}, a5: {a5}, a7: {a7}");
				return original;
			}
		}

#if DEBUG
		internal ActionManager capture;
#endif
		private unsafe void MouseToWorld(long a1, uint spellId, uint actionType, out bool mouseOnWorld, out bool success, out Vector3 worldPos)
		{
			var s = stackalloc byte[0x20];
			_mouseToWorld(a1, spellId, actionType, (long)s);
			mouseOnWorld = s[0] == 1;
			success = s[1] == 1;
			worldPos = *(Vector3*)(s + 0x10);
		}

		public string Name => nameof(SmartCast);
	}

	public static class Log
	{
		public static void Debug(object o, [CallerMemberName] string name = null, [CallerLineNumber] int line = 0)
		{
#if DEBUG
			PluginLog.Debug($"[{name} L{line}] {o}");
#endif
		}
		public static void Verbose(object o, [CallerMemberName] string name = null, [CallerLineNumber] int line = 0)
		{
#if DEBUG
			PluginLog.Verbose($"[{name} L{line}] {o}");
#endif
		}
		public static void Info(object o)
		{
			PluginLog.Information(o.ToString());
		}
		public static void Warning(object o)
		{
			PluginLog.Warning(o.ToString());
		}
	}

	[StructLayout(LayoutKind.Explicit)]
	public unsafe struct ActionManager
	{
		//public static IntPtr ActionManager;
		//public static ref bool IsQueued => ref *(bool*)(ActionManager + 0x68);
		//public static ref uint QueuedActionType => ref *(uint*)(ActionManager + 0x6C);
		//public static ref uint QueuedAction => ref *(uint*)(ActionManager + 0x70);
		//public static ref long QueuedTarget => ref *(long*)(ActionManager + 0x78);
		//public static ref uint QueuedUseType => ref *(uint*)(ActionManager + 0x80);
		//public static ref uint QueuedPVPAction => ref *(uint*)(ActionManager + 0x84);


		[FieldOffset(0)] public IntPtr vtbl;
		[FieldOffset(8)] public float aniLock;
		[FieldOffset(0x24)] public uint castingSpellId;
		[FieldOffset(0x28)] public bool casting;
		[FieldOffset(0x2C)] public uint castingSpellId2;
		[FieldOffset(0x30)] public float castTime;
		[FieldOffset(0x34)] public float castTimeFixed;
		[FieldOffset(0x38)] public uint targetId;
		[FieldOffset(0x60)] public float comboRemainTime;
		[FieldOffset(0x64)] public uint lastComboActionId;
		[FieldOffset(0x68)] public bool IsQueued;
		[FieldOffset(0x6C)] public uint QueuedActionType;
		[FieldOffset(0x70)] public uint queuedActionId;
		[FieldOffset(0x78)] public long queuedActionTargetId;
		[FieldOffset(0x80)] public uint QueuedUseType;
		[FieldOffset(0x84)] public uint QueuedPVPAction;
		[FieldOffset(0x110)] public ushort spellUsed;
		[FieldOffset(0x112)] public ushort spellSuccessed;
		[FieldOffset(0x618)] public float gcdPassed;
		[FieldOffset(0x61C)] public float gcd;

		public static IntPtr actionCommandRequestTypePtr = IntPtr.Zero;
		public static byte ActionCommandRequestType
		{
			get => *(byte*)actionCommandRequestTypePtr;
			set
			{
				if (actionCommandRequestTypePtr != IntPtr.Zero)
					SafeMemory.WriteBytes(actionCommandRequestTypePtr, new[] { value });
			}
		}
	}

	enum CastFrom : uint
	{
		UserInput = 0,
		Queue = 1,
		Macro = 2
	}

	public enum ActionType : byte
	{
		None,
		Spell,
		Item,
		KeyItem,
		Ability,
		General,
		Companion,
		CraftAction = 9,
		MainCommand,
		PetAction1,
		Mount = 13,
		ChocoboRaceAbility = 16,
		ChocoboRaceItem
	}

	public enum ActionCategory : byte
	{
		INVALID,
		自动攻击,
		魔法,
		战技,
		能力,
		道具,
		采集能力,
		制作能力,
		任务,
		极限技,
		系统,
		弩炮,
		坐骑,
		特殊技能,
		道具操作,
		奋战技,
	}


}
