using BepInEx;
using BepInEx.Logging;
using DevInterface;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Permissions;
using UnityEngine;

// Allows access to private members
#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace DevToolsUI;

[BepInPlugin("magica.devtoolsuifix", "Dev Tools UI Fix", "0.1.0")]
sealed class Plugin : BaseUnityPlugin
{
    public static new ManualLogSource Logger;
	bool IsInit;

	public static ConditionalWeakTable<Panel, PanelValues> panelCWT = new();

	public void OnEnable()
    {
        Logger = base.Logger;
        On.RainWorld.OnModsInit += OnModsInit;
    }

    private void OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);

        if (IsInit) return;
        IsInit = true;

		try
		{
			On.DevInterface.Panel.ctor += Panel_ctor;
			_ = new Hook(typeof(RectangularDevUINode).GetProperty(nameof(RectangularDevUINode.MouseOver), BindingFlags.Public | BindingFlags.Instance).GetGetMethod(), (Func<RectangularDevUINode, bool> orig, RectangularDevUINode self) =>
			{
				if (self is Panel panel)
				{
					Vector2 absSize = panel.collapsed ? new Vector2(self.size.x, 20f) : self.size + new Vector2(0f, 20f);
					Vector2 absPos = panel.collapsed ? new Vector2(panel.nonCollapsedAbsPos.x, panel.nonCollapsedAbsPos.y + self.size.y) : self.absPos;
					return self.owner != null && (self.owner.mousePos.x > absPos.x && self.owner.mousePos.x < absPos.x + absSize.x && self.owner.mousePos.y > absPos.y) && self.owner.mousePos.y < absPos.y + absSize.y;
				}
				return orig(self);
			});
			IL.DevInterface.Panel.Update += Panel_Update;
		}
		catch (Exception ex)
		{
			UnityEngine.Debug.LogException(ex);
		}
    }

	private void Panel_ctor(On.DevInterface.Panel.orig_ctor orig, Panel self, DevUI owner, string IDstring, DevUINode parentNode, Vector2 pos, Vector2 size, string title)
	{
		orig(self, owner, IDstring, parentNode, pos, size, title);

		PanelValues values = panelCWT.GetOrCreateValue(self);
		if (values != null)
		{
			values.priority = self.Page.subNodes.Count;
		}
	}

	private void Panel_Update(ILContext il)
	{
		try
		{
			ILCursor cursor = new(il);

			cursor.GotoNext(MoveType.After,
				x => x.MatchLdfld<Panel>(nameof(Panel.collapsed)));

			ILLabel next = (ILLabel)cursor.Next.Operand;

			cursor.GotoNext(MoveType.After,
				x => x.MatchBrtrue(out _)
				);

			cursor.Emit(OpCodes.Ldarg_0);
			static bool ShouldUpdateNodes(Panel panel)
			{
				return panel.IsPanelOnTop() && !Input.GetKey(KeyCode.LeftShift);
			}
			cursor.EmitDelegate(ShouldUpdateNodes);
			cursor.Emit(OpCodes.Brfalse, next);

			cursor.GotoNext(MoveType.After, 
				x => x.MatchStfld<Panel>(nameof(Panel.moveOffset)),
				x => x.MatchLdarg(0));

			static void HandleCaseIfHovered(Panel panel)
			{
				panel.fSprites[0].color = Color.black;
				if (panel.IsPanelOnTop() || panel.dragged)
				{
					if (panel.owner != null && panel.MouseOver && Input.GetKey(KeyCode.LeftShift) && !panel.dragged)
					{
						panel.fSprites[0].color = Color.red;
						if (panel.owner.mouseClick)
						{
							panel.dragged = true;
							panel.moveOffset = panel.nonCollapsedAbsPos - panel.owner.mousePos;
						}
					}
					else if (panel.owner != null && panel.dragged)
					{
						panel.fSprites[0].color = Color.blue;
					}

					if (panel.owner != null && panel.MouseOver && Input.GetKey(KeyCode.LeftControl))
					{
						panel.fSprites[0].color = Color.blue;

						if (panel.owner.mouseClick)
							panel.ToggleCollapse();
					}
				}
			}
			cursor.EmitDelegate(HandleCaseIfHovered);
			cursor.Emit(OpCodes.Ldarg_0);
		}
		catch (Exception ex)
		{
			UnityEngine.Debug.LogException(ex);
		}
	}

	public class PanelValues
	{
		public int priority;
	}
}

public static class ExtensionValues
{
	public static bool IsPanelOnTop(this Panel panel)
	{
		if (panel.Page == null && !panel.Page.subNodes.Any(x => x is Panel || x.subNodes.Any(x => x is Panel)))
			return false;

		Panel[] panelNode = panel.Page != null && panel.Page.subNodes.Any(x => x is Panel || x.subNodes.Any(x => x is Panel)) ? panel.Page.subNodes.Where(x => (x is Panel pen && pen.MouseOver) || (x.subNodes.Any(x => x is Panel pan && pan.MouseOver))).SelectMany(x => x is Panel panel2 ? [panel2] : x.subNodes.Where(x => x is Panel).Select(x => x as Panel)).ToArray() : null;

		return (panelNode == null) ||
			(panel.MouseOver && panelNode != null && panelNode.Where(x => x.MouseOver).Count() <= 1) ||
			(panelNode != null && Plugin.panelCWT.TryGetValue(panel, out var values) && panelNode.Where(x => Plugin.panelCWT.TryGetValue(x, out var compare) && values.priority < compare.priority && x.MouseOver).Count() <= 0);
	}
}
