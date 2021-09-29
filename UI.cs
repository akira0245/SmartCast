using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Plugin;
using Dalamud.Utility;
using ImGuiNET;

namespace SmartCast
{
	class UI : IDisposable
	{
		private SmartCast plugin;
		internal bool Visible;
		public UI(SmartCast plugin)
		{
			this.plugin = plugin;
			DalamuApi.DalamudApi.PluginInterface.UiBuilder.Draw += UiBuilder_OnBuildUi;
			DalamuApi.DalamudApi.PluginInterface.UiBuilder.OpenConfigUi += UiBuilder_OnOpenConfigUi;
		}

		private void UiBuilder_OnOpenConfigUi()
		{
			Visible ^= true;
		}

		private void UiBuilder_OnBuildUi()
		{
			if (Visible)
			{
				var cn = (int)DalamuApi.DalamudApi.DataManager.Language == 4;
				ImGui.SetNextWindowSize(new Vector2(480, 320), ImGuiCond.FirstUseEver);
				if (ImGui.Begin("SmartCast Config", ref Visible))
				{

					var configChanged =
						HelpCheckbox(cn ? "使用智能施法" : "Use smart cast", cn ? "当你使用地面目标技能时，将技能施放在当前鼠标位置。" : "When you use ground target actions, cast the action at current mouse cursor position.", ref plugin.config.EnableSmartCast) |
						(plugin.config.EnableSmartCast && HelpCheckbox(cn ? "非玩家技能智能施法" : "Use smart cast for non-player actions", cn ? "为非玩家地面目标技能（如坐骑技能、机甲技能、炮击等）使用智能施法。" : "Use smart cast for non-player ground target actions (such as mount actions, war machina actions, cannonfire, etc.).", ref plugin.config.GroundTargetSmartCastForNonPlayerSpell)) |

						HelpCheckbox(cn ? "按下技能时下坐骑" : "Auto dismount when using action", cn ? "在骑乘状态按下任一战斗技能时自动下坐骑，不使用按下的技能。" : "Dismount when using any battle action.", ref plugin.config.AutoDismount) |
						(plugin.config.AutoDismount && HelpCheckbox(cn ? "下坐骑并使用技能" : "Dismount and do action", cn ? "下坐骑并立即使用按下的技能。" : "Use the action immediately after dismounted.", ref plugin.config.AutoDismountAndCast)) |

						HelpCheckbox(cn ? "宏技能队列" : "Queue macro actions", cn ? "将用户宏中通过“/ac”指令施放的技能插入技能队列中。" : "Insert actions cast by \"/ac\" commands into action queue.", ref plugin.config.QueueMacroAction);

					if (configChanged)
					{
						plugin.SavePluginConfig();
					}
					unsafe
					{
						ImGui.TextUnformatted($"{(long)plugin.actionManager:X}");
						ShowObject(*plugin.actionManager);
					}
				}
				 
				ImGui.End();
			}
		}

		public static void ShowObject(object obj)
		{
			Type type = obj.GetType();
			ImGui.Text(string.Format("Object Dump({0}) for {1}({2})", (object)type.Name, obj, (object)obj.GetHashCode()));
			ImGuiHelpers.ScaledDummy(5f);
			ImGui.TextColored(ImGuiColors.DalamudOrange, "-> Properties:");
			ImGui.Indent();
			foreach (PropertyInfo property in type.GetProperties())
			{
				try
				{
					ImGui.TextColored(ImGuiColors.DalamudOrange,
						string.Format("    {0}: {1:X}", (object)property.Name, property.GetValue(obj)));
				}
				catch (Exception e)
				{
					ImGui.TextColored(ImGuiColors.DalamudOrange,
						string.Format("    {0}: {1}", (object)property.Name, property.GetValue(obj)));
				}
			}
			ImGui.Unindent();
			ImGuiHelpers.ScaledDummy(5f);
			ImGui.TextColored(ImGuiColors.HealerGreen, "-> Fields:");
			ImGui.Indent();
			foreach (FieldInfo field in type.GetFields())
			{
				try
				{
					ImGui.TextColored(ImGuiColors.HealerGreen,
						string.Format("    {0}: {1:X}", (object)field.Name, field.GetValue(obj)));
				}
				catch (Exception e)
				{
					ImGui.TextColored(ImGuiColors.HealerGreen,
						string.Format("    {0}: {1}", (object)field.Name, field.GetValue(obj)));
				}
			}
			ImGui.Unindent();
		}

		private static bool HelpCheckbox(string label, string help, ref bool isChecked)
		{
			var ret = ImGui.Checkbox(label, ref isChecked);

			ImGui.TreePush();
			ImGui.PushTextWrapPos();
			ImGui.TextUnformatted(help);
			ImGui.PopTextWrapPos();
			ImGui.TreePop();
			ImGui.Spacing();

			return ret;
		}

		public void Dispose()
		{
			DalamuApi.DalamudApi.PluginInterface.UiBuilder.Draw -= UiBuilder_OnBuildUi;
			DalamuApi.DalamudApi.PluginInterface.UiBuilder.OpenConfigUi -= UiBuilder_OnOpenConfigUi;
		}
	}
}
