using BepInEx;
using BepInEx.Logging;
using DevInterface;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using RWCustom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Permissions;
using UnityEngine;

// Allows access to private members
#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace DevToolsUI;

[BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
sealed class Plugin : BaseUnityPlugin
{
	public const string PLUGIN_GUID = "magica.devtoolsuifix";
	public const string PLUGIN_NAME = "Dev Tools UI Fix"; 
	public const string PLUGIN_VERSION = "0.1.0"; 
	public static new ManualLogSource Logger;
	bool IsInit;

	public static ConditionalWeakTable<Panel, PanelValues> panelCWT = new();
	internal static Dictionary<Type, List<PlacedObjectRepresentation>> placedObjs = [];
	private static int priority;
	internal static List<Panel> panelNodes = [];
	internal static Dictionary<RoomSettings.RoomEffect.Type, Type> roomEffects = [];

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
			On.DevInterface.Handle.Update += Handle_Update;
			On.DevInterface.SwitchPageButton.Clicked += SwitchPageButton_Clicked;
			On.DevInterface.ObjectsPage.RemoveObject += ObjectsPage_RemoveObject;
			IL.DevInterface.ObjectsPage.CreateObjRep += ObjectsPage_CreateObjRep;
			On.DevInterface.Button.Update += Button_Update;
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

	private void ObjectsPage_RemoveObject(On.DevInterface.ObjectsPage.orig_RemoveObject orig, ObjectsPage self, PlacedObjectRepresentation objRep)
	{
		if (placedObjs.ContainsKey(objRep.GetType()) && placedObjs[objRep.GetType()].Contains(objRep))
		{
			placedObjs[objRep.GetType()].Remove(objRep);
			if (placedObjs[objRep.GetType()].Count == 0)
			{
				placedObjs.Remove(objRep.GetType());
			}
		}

		if (objRep.subNodes.Any(x => x is Panel pan && panelNodes.Contains(x)))
			objRep.subNodes.Where(x => x is Panel).ToList().ForEach(x => panelNodes.Remove((Panel)x));

		// Removes any lingering sprites and such from placed objects
		objRep.fSprites.ForEach(x => x?.RemoveFromContainer());
		objRep.subNodes.ForEach(x => x.fSprites.ForEach(x => x?.RemoveFromContainer()));
		objRep.subNodes.ForEach(x => x.subNodes.ForEach(x => x.fSprites.ForEach(x => x?.RemoveFromContainer())));

		List<FieldInfo> fields = [.. objRep.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)];
		fields.AddRange(objRep.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance));
		foreach (var field in fields)
		{
			if (field.FieldType.BaseType != null)
			{
				if (objRep.owner != null && objRep.owner.room != null)
				{
					// TODO: Fix cosmetic sprites as well
					if (field.FieldType.GetInterfaces().Contains(typeof(IDrawable)))
					{
						UnityEngine.Debug.Log("Attempting to remove spriteleaser...");
						RoomCamera.SpriteLeaser currentLeaser = objRep.owner.room.game.cameras[0].spriteLeasers.Where(x => x.drawableObject == field.GetValue(objRep)).FirstOrDefault();
						currentLeaser?.CleanSpritesAndRemove();
					}
				}
			}
		}
		orig(self, objRep);
	}

	private void Handle_Update(On.DevInterface.Handle.orig_Update orig, Handle self)
	{
		orig(self);

		PlacedObjectRepresentation parent = self.parentNode is PlacedObjectRepresentation p ? p : self is PlacedObjectRepresentation a ? a : null;
		if (!self.MouseOver && !self.dragged && parent != null && placedObjs.ContainsKey(parent.GetType()) && placedObjs[parent.GetType()].Contains(parent))
		{
			float hue = Mathf.Lerp(0f, 1f, (float)(placedObjs[parent.GetType()].IndexOf(parent) * 1f) / (float)placedObjs[parent.GetType()].Count);
			self.SetColor(Custom.HSL2RGB(hue, 1f, 0.8f));
		}
	}

	private void SwitchPageButton_Clicked(On.DevInterface.SwitchPageButton.orig_Clicked orig, SwitchPageButton self)
	{
		placedObjs.Clear();
		panelNodes.Clear();
		priority = 0;
		orig(self);
	}

	private void ObjectsPage_CreateObjRep(ILContext il)
	{
		try
		{
			ILCursor cursor = new(il);

			cursor.GotoNext(MoveType.After,
				x => x.MatchStfld<PlacedObject>(nameof(PlacedObject.pos)));

			cursor.Emit(OpCodes.Ldarg_0);
			cursor.Emit(OpCodes.Ldarg_2);
			static void ReplaceObjPos(ObjectsPage self, PlacedObject pObj)
			{
				if (pObj.pos.x < 0f)
				{
					pObj.pos.x = 10f;
				}
				if (pObj.pos.y < 0f)
				{
					pObj.pos.y = 10f;
				}
			}
			cursor.EmitDelegate(ReplaceObjPos);
		}
		catch (Exception ex)
		{
			UnityEngine.Debug.LogException(ex);
		}
	}

	private void Button_Update(On.DevInterface.Button.orig_Update orig, Button self)
	{
		if (self is SwitchPageButton && self.parentNode != null && self.parentNode.subNodes.Any(x => (x is Panel panel && panel.MouseOver) || x.subNodes.Any(x => x is Panel panel && panel.MouseOver) || (x is Handle handle && handle.MouseOver) || x.subNodes.Any(x => x is Handle handle && handle.MouseOver)))
			return;

		orig(self);
	}

	private void Panel_ctor(On.DevInterface.Panel.orig_ctor orig, Panel self, DevUI owner, string IDstring, DevUINode parentNode, Vector2 pos, Vector2 size, string title)
	{
		orig(self, owner, IDstring, parentNode, pos, size, title);

		PanelValues values = panelCWT.GetOrCreateValue(self);
		if (values != null)
		{
			priority++;
			values.priority = priority;
			panelNodes.Add(self);
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
				return (panel.IsPanelOnTop() || panel.subNodes.Any(x => (x is Panel pan && pan.IsPanelOnTop()) || (x is Handle handle && handle.MouseOver) )) && !Input.GetKey(KeyCode.LeftShift);
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
					if (panel.MouseOver && panel.owner.mouseClick && panelCWT.TryGetValue(panel, out var value))
					{
						panel.fSprites.ForEach(x => x.MoveToFront());
						panel.fLabels.ForEach(x => x.MoveToFront());
						panel.subNodes.ForEach(x => x.fSprites.ForEach((x) => x.MoveToFront()));
						panel.subNodes.ForEach(x => x.fLabels.ForEach((x) => x.MoveToFront()));
						panel.subNodes.ForEach(x => x.subNodes.ForEach(x => x.fSprites.ForEach((x) => x.MoveToFront())));
						panel.subNodes.ForEach(x => x.subNodes.ForEach(x => x.fLabels.ForEach((x) => x.MoveToFront())));
						priority++;
						value.priority = priority;
					}

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
				if (panel.MoreThanOneOfSameTypeExists() && panel.parentNode is PlacedObjectRepresentation rep && placedObjs.ContainsKey(rep.GetType()) && placedObjs[rep.GetType()].Contains(rep) && !panel.dragged)
				{
					float hue = Mathf.Lerp(0f, 1f, (float)(placedObjs[rep.GetType()].IndexOf(rep) * 1f) / (float)placedObjs[rep.GetType()].Count);
					panel.fSprites[0].color = Custom.HSL2RGB(hue, 1f, 0.2f);
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

		if (panel.Page.subNodes.Any(x => (x is Handle handle && handle.MouseOver) || x.subNodes.Any(x => x is Handle handle && handle.MouseOver)))
			return false;

		return (Plugin.panelNodes == null) ||
			(panel.MouseOver && Plugin.panelNodes != null && Plugin.panelNodes.Where(x => x != null && x.MouseOver).Count() <= 1) ||
			(Plugin.panelNodes != null && Plugin.panelCWT.TryGetValue(panel, out var values) && Plugin.panelNodes.Where(x => x != null && Plugin.panelCWT.TryGetValue(x, out var compare) && values.priority < compare.priority && x.MouseOver).Count() <= 0);
	}

	public static bool MoreThanOneOfSameTypeExists(this Panel panel)
	{

		if (panel.parentNode != null && panel.parentNode is PlacedObjectRepresentation rep && Plugin.placedObjs.ContainsKey(rep.GetType()) && Plugin.placedObjs[rep.GetType()].Contains(rep))
		{
			return true;
		}
		else if (panel.Page != null && panel.parentNode != null && panel.parentNode is PlacedObjectRepresentation rep2 && panel.Page.subNodes.Where(x => x is PlacedObjectRepresentation && x.GetType() == panel.parentNode.GetType()).Count() > 1)
		{
			if (!Plugin.placedObjs.ContainsKey(rep2.GetType()))
			{
				Plugin.placedObjs.Add(rep2.GetType(), []);
			}
			Plugin.placedObjs[rep2.GetType()].Add(rep2);
		}

		return false;
	}

	public static bool IsPanelOffscreen(this Panel panel)
	{
		return panel.absPos.x < 0f || panel.absPos.y < 0f || panel.absPos.x > Custom.rainWorld.options.ScreenSize.x || panel.absPos.x > Custom.rainWorld.options.ScreenSize.y;
	}
}
