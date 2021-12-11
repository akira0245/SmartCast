using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Plugin;
using Lumina.Excel.GeneratedSheets;
using SmartCast.DalamuApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
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

        private delegate byte DoActionDelegate(long a1, uint a2, uint a3, long a4, int a5, uint a6, int a7, long a8);
        private DoActionDelegate _doAction;
        private Hook<DoActionDelegate> _doActionHook;

        private unsafe delegate byte DoActionLocationDelegate(long a1, uint actionType, uint actionId, long a4/* 0xE0000000 */, Vector3* a5, uint a6/* 0 */);
        private DoActionLocationDelegate _doActionLocation;
        private Hook<DoActionLocationDelegate> _doActionLocationHook;

        private delegate void MouseToWorldDelegate(long a1, uint spellid, uint a3, long result);
        Hook<MouseToWorldDelegate> _mouseToWorldHook;
        private MouseToWorldDelegate _mouseToWorld;

        private delegate long ActionReadyDelegate(long a1, uint actionType, uint actionId);
        private ActionReadyDelegate _actionReady;

        public delegate byte MacroDetor(long param_1, uint param_2, uint param_3, long param_4, int zerotwo);
        private MacroDetor _macroDetor;
        private Vector3 queuedPos = Vector3.Zero;
        private uint queuedTarget = 0xE000_0000;

        private delegate uint* MouseOverUi(long a1, uint* a2, long a3, int a4);
        private Hook<MouseOverUi> mouseOverUiHook;
        private uint? _mouseOverID;

        private delegate IntPtr PlaceHolder(long param1, string param2, byte param3 = 1, byte param4 = 0);
        private Hook<PlaceHolder> placeHolderHook;
        private PlaceHolder _placeHolderDetour;
        private long _placeHolderA1;

        internal ActionManager* actionManager;
        private readonly CameraManager* cameraManager;
        bool CanCast(uint id, uint type, uint target) => _actionReady((long)actionManager, type, id) == 0 && _canCast.Invoke((long)actionManager, type, id, target, 1, 1) == 0;

        internal void SavePluginConfig() => DalamudApi.PluginInterface.SavePluginConfig(config);

        public void Dispose()
        {
            pluginUI.Dispose();
            DalamudApi.Framework.Update -= OnFramework;
            DalamudApi.Dispose();
            _doActionLocationHook?.Dispose();
            _doActionHook?.Dispose();
            mouseOverUiHook?.Dispose();
            placeHolderHook?.Dispose();
            _mouseToWorldHook?.Dispose();
        }

        public SmartCast(DalamudPluginInterface pluginInterface)
        {
            DalamudApi.Initialize(this, pluginInterface);
            GroundTargetActions = DalamudApi.DataManager.GetExcelSheet<Action>().Where(i => i.TargetArea && i.RowId is not (7419 or 3573 or 24403 or 27819)).ToDictionary(i => i.RowId, j => j);
            DismountActions = DalamudApi.DataManager.GetExcelSheet<Action>().Where(i => i.IsPlayerAction && i.RowId > 8 && i.ActionCategory?.Value?.RowId is 2 or 3 or 4 or 9 or 15).ToDictionary(i => i.RowId, j => j);
            BattleJobs = DalamudApi.DataManager.GetExcelSheet<ClassJob>().Where(i => i.ClassJobCategory?.Value?.RowId is 30 or 31).Select(i => i.RowId).ToHashSet();

            actionManager = (ActionManager*)FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
            _canCastFunc = (IntPtr)FFXIVClientStructs.FFXIV.Client.Game.ActionManager.fpGetActionStatus;
            _doActionFunc = (IntPtr)FFXIVClientStructs.FFXIV.Client.Game.ActionManager.fpUseAction;
            _doActionLocationFunc = (IntPtr)FFXIVClientStructs.FFXIV.Client.Game.ActionManager.fpUseActionLocation;
            _getAdjustedActionIdFunc = (IntPtr)FFXIVClientStructs.FFXIV.Client.Game.ActionManager.fpGetAdjustedActionId;

            cameraManager = (CameraManager*)DalamudApi.SigScanner.GetStaticAddressFromSig("48 8D 35 ?? ?? ?? ?? 48 8B 09");
            _mouseToWorldFunc = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 74 4E");
            _actionReadyFunc = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 3C 01 74 45 FF C7");


            _getAdjustedActionId = Marshal.GetDelegateForFunctionPointer<GetAdjustedActionIdDelegate>(_getAdjustedActionIdFunc);
            _mouseToWorld = Marshal.GetDelegateForFunctionPointer<MouseToWorldDelegate>(_mouseToWorldFunc);
            _doActionLocation = Marshal.GetDelegateForFunctionPointer<DoActionLocationDelegate>(_doActionLocationFunc);
            _actionReady = Marshal.GetDelegateForFunctionPointer<ActionReadyDelegate>(_actionReadyFunc);
            _canCast = Marshal.GetDelegateForFunctionPointer<CanCastDelegate>(_canCastFunc);
            _doAction = Marshal.GetDelegateForFunctionPointer<DoActionDelegate>(_doActionFunc);

            _mouseToWorldHook = new Hook<MouseToWorldDelegate>(_mouseToWorldFunc, mousetoworld);
            _mouseToWorldHook.Enable();

            _macroDetor = Marshal.GetDelegateForFunctionPointer<MacroDetor>(DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 33 C0 EB 93"));

            _doActionHook = new Hook<DoActionDelegate>(_doActionFunc, Detour);
            _doActionHook.Enable();

            mouseOverUiHook = new Hook<MouseOverUi>(DalamudApi.SigScanner.ScanText("40 56 57 41 55 48 83 EC 30 4C 89 64 24 ??"), MouseOverUiDetour);
            mouseOverUiHook.Enable();

            //placeHolderHook = new Hook<PlaceHolder>(DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 5C 24 ?? EB 0C"),
            //    PlaceHolderDetour);
            //placeHolderHook.Enable();

            _placeHolderDetour = Marshal.GetDelegateForFunctionPointer<PlaceHolder>(DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 5C 24 ?? EB 0C"));

            var ptr1 = DalamudApi.SigScanner.GetStaticAddressFromSig("44 0F B6 C0 48 8B 0D ?? ?? ?? ??");
            var ptr2 = Marshal.ReadIntPtr(ptr1) + 0x2B60;
            var ptr3 = Marshal.ReadIntPtr(ptr2) + 0xAA408 + 0x258;
            _placeHolderA1 = Marshal.ReadInt64(ptr3) + 0xAB610;

            config = (Config)pluginInterface.GetPluginConfig() ?? new Config();

            pluginUI = new UI(this);
            DalamudApi.Framework.Update += OnFramework;
            if (pluginInterface.Reason is not PluginLoadReason.Boot or PluginLoadReason.Update)
            {
                pluginUI.Visible = true;
            }
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

        private (long a1, uint actionType, uint actionId, long targetId, int a5, uint castFrom, int a7, long a8)? waitedAction = null;
        private DateTime? tryTime = null;

        private void OnFramework(Dalamud.Game.Framework framework)
        {

            if (waitedAction == null || tryTime == null || tryTime < DateTime.Now) return;

            (long a1, uint actionType, uint actionId, long targetId, int a5, uint castFrom, int a7, long a8) = waitedAction.Value;

            PluginLog.Verbose($"Cast queue: actionType: {actionType}, actionId: {actionId}, targetId: {targetId:X}, castFrom: {castFrom}, a5: {a5}, a7: {a7}");

            var actionReady = _actionReady(a1, actionType, actionId);
            PluginLog.Verbose($"_actionReady: {actionReady}");

            if (actionReady == 0)
            {
                var cancast = _canCast.Invoke(a1, actionType, actionId, targetId, 1, 1);
                PluginLog.Verbose($"cancast: {cancast}");
                if (cancast == 0)
                {
                    var doaction = _doAction.Invoke(a1, actionType, actionId, targetId, a5, castFrom, a7, a8);
                    PluginLog.Debug($"Cancast passed: DoAction ret: {doaction}, actionType: {actionType}, actionId: {actionId}, targetId: {targetId:X}, castFrom: {castFrom}, a5: {a5}, a7: {a7}");
                    if (doaction == 1)
                    {
                        waitedAction = null;
                        tryTime = null;
                    }
                }
            }
        }

        void mousetoworld(long a1, uint spellid, uint a3, long result)
        {
            PluginLog.Debug($"MouseToWorld:{spellid}:{a3}");
            var str = "";
            for (int i = 0; i < 30; i++)
            {
                str += " " + Marshal.ReadByte((IntPtr)result, i);
            }
            PluginLog.Debug($"STACK:{str}");
            _mouseToWorldHook.Original(a1, spellid, a3, result);
        }

        private uint GetObjectId(uint index)
        {
            var ptr = _placeHolderDetour(_placeHolderA1, $"<{index}>");
            if (ptr != IntPtr.Zero || (int)ptr != 0)
            {
                return (uint)Marshal.ReadInt32(ptr + +0x74);
            }

            return 0xE0000000;
        }

        private uint* MouseOverUiDetour(long a1, uint* a2, long a3, int a4)
        {
            //PluginLog.Error($"{a1:X}:{*a2:X}:{a3:X}:{a4}");
            if ((*(byte*)a3 & 0xF) != 0)
            {
                var l3 = *(uint*)(a3 + 8);
                _mouseOverID = l3 switch
                {
                    //0 => DalamudApi.ClientState.LocalPlayer?.ObjectId,
                    < 8 => GetObjectId(l3 + 1),
                    16 => DalamudApi.TargetManager.FocusTarget?.ObjectId,
                    17 => DalamudApi.TargetManager.Target?.TargetObject?.ObjectId,
                    18 => DalamudApi.TargetManager.Target?.ObjectId,
                    _ => null
                };
                if (_mouseOverID != null) PluginLog.Error($"{_mouseOverID:X}");

            }

            var result = mouseOverUiHook.Original(a1, a2, a3, a4);
            return result;
        }

        private IntPtr PlaceHolderDetour(long param1, string param2, byte param3, byte param4)
        {


            var result = placeHolderHook.Original(param1, param2);
            //PluginLog.Error($"{result:X}:{param1:X}:{param2}:{_placeHolderA1:X}");
            return result;
        }


        void QueueAction(long a1, uint type, uint adjustedId, uint targetId, uint castFrom)
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
            PluginLog.Debug($"QUEUED:{castFrom} {adjustedId} {targetId}");
        }

        private byte Detour(long a1, uint actionType, uint actionId, long targetId, int a5, uint castFrom, int a7, long a8)
        {
            PluginLog.Debug($"DoAction original: A1:{a1:X}, actionType: {actionType}, actionId: {actionId}, targetId: {targetId:X}, castFrom: {castFrom}, a5: {a5}, a7: {a7}, a8:{a8}");
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
                            waitedAction = (a1, actionType, adjustedId, targetId, a5, castFrom, a7, (long)a8);
                            tryTime = DateTime.Now.AddSeconds(1);
                        }
                        PluginLog.Debug($"Used actionType: {actionType}, actionId: {actionId}->{adjustedId}, Dismounting!");
                        return _doActionHook.Original.Invoke(a1, (uint)ActionType.General, 23, 0xE000_0000, 0, 0, 0, a8);
                    }
                }

            }

            if (_canCast(a1, actionType, actionId, targetId, 1, 1) != 0UL)	//目前无法使用技能
            {
                if (config.EnableSmartCast && GroundTargetActions.TryGetValue(actionId, out var action) && _actionReady(a1, actionType, actionId) == 0L)
                {
                    if (config.GroundTargetSmartCastForNonPlayerSpell || action.IsPlayerAction)
                    {
                        //地面技能进队列
                        if (!actionManager->IsQueued)
                        {
                            queuedPos = Vector3.Zero;
                            queuedTarget = 0xE000_0000;
                            QueueAction(a1, actionType, actionId, (uint)targetId, castFrom);
                            return 0;
                        }
                    }
                }
                else if (config.QueueMacroAction && castFrom == 2) //ac进队列
                {
                    if (!actionManager->IsQueued)
                    {
                        queuedPos = Vector3.Zero;
                        queuedTarget = 0xE000_0000;
                        QueueAction(a1, actionType, actionId, (uint)targetId, castFrom);
                        return 0;
                    }
                }

            }
            else if (_actionReady(a1, actionType, actionId) == 0L)	//可以使用技能
            {
                if (castFrom == 1)	//队列中技能
                {
                    if (queuedPos != Vector3.Zero)	//已储存了pos
                    {
                        var pos = queuedPos;
                        var b = this._doActionLocation(a1, actionType, actionId, targetId, &pos, 0U);
                        PluginLog.Debug($"_doActionLocation ret: {b}");
                        queuedPos = Vector3.Zero;
                        queuedTarget = 0xE000_0000;
                        return b;

                    }

                    if (queuedTarget != 0xE000_0000)	//已储存了target
                    {
                        PluginLog.Debug($"exec {queuedTarget}");
                        var r = _doActionHook.Original(a1, actionType, actionId, targetId, a5, castFrom,
                            a7, a8);
                        _macroDetor(a1, actionType, actionId, queuedTarget, 0);
                        queuedPos = Vector3.Zero;
                        queuedTarget = 0xE000_0000;
                        return r;
                    }
                }

                if (castFrom != 2 && queuedPos == Vector3.Zero && queuedTarget == 0xE000_0000) //非宏，无储存
                {
                    if (config.EnableSmartCast && GroundTargetActions.TryGetValue(actionId, out var action))
                    {
                        if (config.GroundTargetSmartCastForNonPlayerSpell || action.IsPlayerAction)
                        {
                            MouseToWorld(a1, actionId, actionType, out var mouseOnWorld, out var success,
                                out var worldPos);
                            PluginLog.Debug(
                                $"targetId: {targetId:X}, castFrom: {castFrom}, mouseOnWorld: {mouseOnWorld}, success: {success}, location: {worldPos}");
                            if (mouseOnWorld && success)
                            {

                                {
                                    byte b = this._doActionLocation(a1, actionType, actionId, targetId, &worldPos,
                                        0U);
                                    PluginLog.Debug($"_doActionLocation ret: {b}");
                                    return b;
                                }
                            }
                        }
                    }
                }


            }
            //Smart悬浮施法
            if (config.MouseOverFriendly && DismountActions.TryGetValue(actionId, out var action2) && (action2.CanTargetFriendly || action2.CanTargetParty))
            {

                if (_mouseOverID != null && _mouseOverID != 0)
                {
                    var result = _doActionHook.Original(a1, actionType, actionId, (long)_mouseOverID, a5, castFrom, a7, a8);
                    return result;
                }
                if (DalamudApi.TargetManager.MouseOverTarget != null)
                {
                    var result = _doActionHook.Original(a1, actionType, actionId,
                        DalamudApi.TargetManager.MouseOverTarget.ObjectId, a5, castFrom, a7, a8);
                    return result;
                }

            }

            {
                if (config.MoveToCameraDirection && actionId is
                                                     24401 or 27817 //Hell's Ingress
                                                     or 24402 or 27818  //Hell's Egress
                                                     or 16010 or 17764 //En Avant
                                                     or 94 or 8803 //Elusive Jump
                                                 && CanCast(actionId, 1, 0xE000_0000)
                   )
                {
                    if (DalamudApi.ClientState.LocalPlayer != null)
                    {
                        var localPlayerAddress = DalamudApi.ClientState.LocalPlayer.Address;
                        var rotationOffset = Marshal.OffsetOf<GameObject>("Rotation");

                        var rotationAddress = localPlayerAddress + (int)rotationOffset;
                        ref var rotation = ref *(float*)rotationAddress;
                        rotation = cameraManager->WorldCamera->CurrentHRotation + MathF.PI;
                    }
                }


                var original = this._doActionHook.Original(a1, actionType, actionId, targetId, a5, castFrom, a7, a8);
                PluginLog.Debug($"DoAction ret: original: A1:{a1:X}, actionType: {actionType}, actionId: {actionId}, targetId: {targetId:X}, castFrom: {castFrom}, a5: {a5}, a7: {a7}, a8:{a8}");
                return original;
            }
        }

        private void MouseToWorld(long a1, uint spellId, uint actionType, out bool mouseOnWorld, out bool success, out Vector3 worldPos)
        {

            var s = stackalloc byte[0x20];
            _mouseToWorld(a1, spellId, actionType, (long)s);
            mouseOnWorld = s[0] == 1;
            success = s[1] == 1;
            worldPos = *(Vector3*)(s + 0x10);
            PluginLog.Debug($"{spellId}:{actionType}:{worldPos.X}:{worldPos.Y}:{worldPos.Z}:{mouseOnWorld}:{success}");
        }

        public string Name => nameof(SmartCast);
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

    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct CameraManager
    {
        [FieldOffset(0x0)] public GameCamera* WorldCamera;
        [FieldOffset(0x8)] public GameCamera* IdleCamera;
        [FieldOffset(0x10)] public GameCamera* MenuCamera;
        [FieldOffset(0x18)] public GameCamera* SpectatorCamera;
    }

    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct GameCamera
    {
        [FieldOffset(0x0)] public IntPtr* VTable;
        [FieldOffset(0x90)] public float X;
        [FieldOffset(0x94)] public float Z;
        [FieldOffset(0x98)] public float Y;
        [FieldOffset(0x114)] public float CurrentZoom; // 6
        [FieldOffset(0x118)] public float MinZoom; // 1.5
        [FieldOffset(0x11C)] public float MaxZoom; // 20
        [FieldOffset(0x120)] public float CurrentFoV; // 0.78
        [FieldOffset(0x124)] public float MinFoV; // 0.69
        [FieldOffset(0x128)] public float MaxFoV; // 0.78
        [FieldOffset(0x12C)] public float AddedFoV; // 0
        [FieldOffset(0x130)] public float CurrentHRotation; // -pi -> pi, default is pi
        [FieldOffset(0x134)] public float CurrentVRotation; // -0.349066
        //[FieldOffset(0x138)] public float HRotationDelta;
        [FieldOffset(0x148)] public float MinVRotation; // -1.483530, should be -+pi/2 for straight down/up but camera breaks so use -+1.569
        [FieldOffset(0x14C)] public float MaxVRotation; // 0.785398 (pi/4)
        [FieldOffset(0x160)] public float Tilt;
        [FieldOffset(0x170)] public int Mode; // camera mode??? (0 = 1st person, 1 = 3rd person, 2+ = weird controller mode? cant look up/down)
        //[FieldOffset(0x174)] public int ControlType; // 0 first person, 1 legacy, 2 standard, 3/5/6 ???, 4 ???
        [FieldOffset(0x218)] public float LookAtHeightOffset; // No idea what to call this
        [FieldOffset(0x21C)] public byte ResetLookatHeightOffset; // No idea what to call this
        [FieldOffset(0x2B4)] public float Z2;
    }
}
