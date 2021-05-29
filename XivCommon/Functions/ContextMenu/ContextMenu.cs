﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud;
using Dalamud.Game;
using Dalamud.Game.Internal.Gui;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivCommon.Functions.ContextMenu.Inventory;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace XivCommon.Functions.ContextMenu {
    /// <summary>
    /// Context menu functions
    /// </summary>
    public class ContextMenu : IDisposable {
        private static class Signatures {
            internal const string ContextMenuOpen = "48 8B C4 57 41 56 41 57 48 81 EC ?? ?? ?? ??";
            internal const string ContextMenuSelected = "48 89 5C 24 ?? 55 57 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 80 B9 ?? ?? ?? ?? ??";
            internal const string AtkValueChangeType = "E8 ?? ?? ?? ?? 45 84 F6 48 8D 4C 24 ??";
            internal const string AtkValueSetString = "E8 ?? ?? ?? ?? 41 03 ED";
            internal const string GetAddonByInternalId = "E8 ?? ?? ?? ?? 8B 6B 20";
        }

        #region Offsets and other constants

        private const int MaxItems = 32;

        /// <summary>
        /// Offset from addon to menu type
        /// </summary>
        private const int ParentAddonIdOffset = 0x1D2;

        /// <summary>
        /// Offset from agent to actions byte array pointer (have to add the actions offset after)
        /// </summary>
        private const int MenuActionsPointerOffset = 0xD18;

        /// <summary>
        /// Offset from [MenuActionsPointer] to actions byte array
        /// </summary>
        private const int MenuActionsOffset = 0x428;

        /// <summary>
        /// Offset from inventory context agent to actions byte array
        /// </summary>
        private const int InventoryMenuActionsOffset = 0x558;

        private const int ActorIdOffset = 0xEF0;
        private const int ContentIdLowerOffset = 0xEE0;
        private const int TextPointerOffset = 0xE08;
        private const int WorldOffset = 0xF00;

        private const int ItemIdOffset = 0x5F8;
        private const int ItemAmountOffset = 0x5FC;
        private const int ItemHqOffset = 0x604;

        private const byte NoopContextId = 0x67;

        #endregion

        /// <summary>
        /// The delegate for context menu events.
        /// </summary>
        public delegate void ContextMenuOpenEventDelegate(ContextMenuOpenArgs args);

        /// <summary>
        /// <para>
        /// The event that is fired when a context menu is being prepared for opening.
        /// </para>
        /// <para>
        /// Requires the <see cref="Hooks.ContextMenu"/> hook to be enabled.
        /// </para>
        /// </summary>
        public event ContextMenuOpenEventDelegate? OpenContextMenu;

        /// <summary>
        /// The delegate for inventory context menu events.
        /// </summary>
        public delegate void InventoryContextMenuOpenEventDelegate(InventoryContextMenuOpenArgs args);

        /// <summary>
        /// <para>
        /// The event that is fired when an inventory context menu is being prepared for opening.
        /// </para>
        /// <para>
        /// Requires the <see cref="Hooks.ContextMenu"/> hook to be enabled.
        /// </para>
        /// </summary>
        public event InventoryContextMenuOpenEventDelegate? OpenInventoryContextMenu;

        /// <summary>
        /// The delegate that is run when a context menu item is selected.
        /// </summary>
        public delegate void ContextMenuItemSelectedDelegate(ContextMenuItemSelectedArgs args);

        /// <summary>
        /// The delegate that is run when an inventory context menu item is selected.
        /// </summary>
        public delegate void InventoryContextMenuItemSelectedDelegate(InventoryContextMenuItemSelectedArgs args);

        private unsafe delegate byte ContextMenuOpenDelegate(IntPtr addon, int menuSize, AtkValue* atkValueArgs);

        private delegate IntPtr GetAddonByInternalIdDelegate(IntPtr raptureAtkUnitManager, short id);

        private readonly GetAddonByInternalIdDelegate _getAddonByInternalId = null!;

        private Hook<ContextMenuOpenDelegate>? ContextMenuOpenHook { get; }

        private delegate byte ContextMenuItemSelectedInternalDelegate(IntPtr addon, int index, byte a3);

        private Hook<ContextMenuItemSelectedInternalDelegate>? ContextMenuItemSelectedHook { get; }

        private unsafe delegate void AtkValueChangeTypeDelegate(AtkValue* thisPtr, ValueType type);

        private readonly AtkValueChangeTypeDelegate _atkValueChangeType = null!;

        private unsafe delegate void AtkValueSetStringDelegate(AtkValue* thisPtr, byte* bytes);

        private readonly AtkValueSetStringDelegate _atkValueSetString = null!;

        private GameFunctions Functions { get; }
        private ClientLanguage Language { get; }
        private GameGui Gui { get; }
        private List<BaseContextMenuItem> Items { get; } = new();
        private int NormalSize { get; set; }

        internal ContextMenu(GameFunctions functions, SigScanner scanner, GameGui gui, ClientLanguage language, Hooks hooks) {
            this.Functions = functions;
            this.Language = language;
            this.Gui = gui;

            if (!hooks.HasFlag(Hooks.ContextMenu)) {
                return;
            }

            if (scanner.TryScanText(Signatures.AtkValueChangeType, out var changeTypePtr, "Context Menu (change type)")) {
                this._atkValueChangeType = Marshal.GetDelegateForFunctionPointer<AtkValueChangeTypeDelegate>(changeTypePtr);
            } else {
                return;
            }

            if (scanner.TryScanText(Signatures.AtkValueSetString, out var setStringPtr, "Context Menu (set string)")) {
                this._atkValueSetString = Marshal.GetDelegateForFunctionPointer<AtkValueSetStringDelegate>(setStringPtr);
            } else {
                return;
            }

            if (scanner.TryScanText(Signatures.GetAddonByInternalId, out var getAddonPtr, "Context Menu (get addon)")) {
                this._getAddonByInternalId = Marshal.GetDelegateForFunctionPointer<GetAddonByInternalIdDelegate>(getAddonPtr);
            } else {
                return;
            }

            if (scanner.TryScanText(Signatures.ContextMenuOpen, out var openPtr, "Context Menu open")) {
                unsafe {
                    this.ContextMenuOpenHook = new Hook<ContextMenuOpenDelegate>(openPtr, new ContextMenuOpenDelegate(this.OpenMenuDetour));
                }

                this.ContextMenuOpenHook.Enable();
            } else {
                return;
            }

            if (scanner.TryScanText(Signatures.ContextMenuSelected, out var selectedPtr, "Context Menu selected")) {
                this.ContextMenuItemSelectedHook = new Hook<ContextMenuItemSelectedInternalDelegate>(selectedPtr, new ContextMenuItemSelectedInternalDelegate(this.ItemSelectedDetour));
                this.ContextMenuItemSelectedHook.Enable();
            }
        }

        /// <inheritdoc />
        public void Dispose() {
            this.ContextMenuOpenHook?.Dispose();
            this.ContextMenuItemSelectedHook?.Dispose();
        }

        private (bool isInventory, IntPtr agent) GetContextMenuAgent() {
            var isInventory = this.Gui.HoveredItem > 0;
            var agentId = isInventory ? 10u : 9u;
            var agent = this.Functions.GetAgentByInternalId(agentId);
            return (isInventory, agent);
        }

        private unsafe string? GetParentAddonName(IntPtr addon) {
            var parentAddonId = Marshal.ReadInt16(addon + ParentAddonIdOffset);
            if (parentAddonId == 0) {
                return null;
            }

            var stage = (AtkStage*) this.Functions.GetAtkStageSingleton();
            var parentAddon = this._getAddonByInternalId((IntPtr) stage->RaptureAtkUnitManager, parentAddonId);
            return Encoding.UTF8.GetString(Util.ReadTerminated(parentAddon + 8));
        }

        private static unsafe (uint actorId, uint contentIdLower, string? text, ushort actorWorld) GetAgentInfo(IntPtr agent) {
            var actorId = *(uint*) (agent + ActorIdOffset);
            var contentIdLower = *(uint*) (agent + ContentIdLowerOffset);
            var textBytes = Util.ReadTerminated(Marshal.ReadIntPtr(agent + TextPointerOffset));
            var text = textBytes.Length == 0 ? null : Encoding.UTF8.GetString(textBytes);
            var actorWorld = *(ushort*) (agent + WorldOffset);
            return (actorId, contentIdLower, text, actorWorld);
        }

        private static unsafe (uint itemId, uint itemAmount, bool itemHq) GetInventoryAgentInfo(IntPtr agent) {
            var itemId = *(uint*) (agent + ItemIdOffset);
            var itemAmount = *(uint*) (agent + ItemAmountOffset);
            var itemHq = (*(byte*) (agent + ItemHqOffset)) == 1;
            return (itemId, itemAmount, itemHq);
        }

        private unsafe byte OpenMenuDetour(IntPtr addon, int menuSize, AtkValue* atkValueArgs) {
            this.Items.Clear();

            var (inventory, agent) = this.GetContextMenuAgent();
            if (agent == IntPtr.Zero) {
                goto Original;
            }

            this.NormalSize = (int) (&atkValueArgs[0])->UInt;

            var hasGameDisabled = menuSize - 7 - this.NormalSize > 0;

            var addonName = this.GetParentAddonName(addon);

            var menuActions = inventory
                ? (byte*) (agent + InventoryMenuActionsOffset)
                : (byte*) (Marshal.ReadIntPtr(agent + MenuActionsPointerOffset) + MenuActionsOffset);

            var nativeItems = new List<NativeContextMenuItem>();
            for (var i = 0; i < this.NormalSize; i++) {
                var atkItem = &atkValueArgs[7 + i];

                var nameBytes = Util.ReadTerminated((IntPtr) atkItem->String);
                var name = Encoding.UTF8.GetString(nameBytes);

                var enabled = true;
                if (hasGameDisabled) {
                    var disabledItem = &atkValueArgs[7 + this.NormalSize + i];
                    enabled = disabledItem->Int == 0;
                }

                var action = *(menuActions + 7 + i);

                nativeItems.Add(new NativeContextMenuItem(action, name, enabled));
            }

            if (inventory) {
                var info = GetInventoryAgentInfo(agent);

                var args = new InventoryContextMenuOpenArgs(
                    addon,
                    agent,
                    addonName,
                    info.itemId,
                    info.itemAmount,
                    info.itemHq
                );
                args.Items.AddRange(nativeItems);

                try {
                    this.OpenInventoryContextMenu?.Invoke(args);
                } catch (Exception ex) {
                    Logger.LogError(ex, "Exception in OpenMenuDetour");
                    goto Original;
                }

                // remove any NormalContextMenuItems that may have been added - these will crash the game
                args.Items.RemoveAll(item => item is NormalContextMenuItem);

                // set the agent of any remaining custom items
                foreach (var item in args.Items) {
                    if (item is InventoryContextMenuItem custom) {
                        custom.Agent = agent;
                    }
                }

                this.Items.AddRange(args.Items);
            } else {
                var info = GetAgentInfo(agent);

                var args = new ContextMenuOpenArgs(
                    addon,
                    agent,
                    addonName,
                    info.actorId,
                    info.contentIdLower,
                    info.text,
                    info.actorWorld
                );
                args.Items.AddRange(nativeItems);

                try {
                    this.OpenContextMenu?.Invoke(args);
                } catch (Exception ex) {
                    Logger.LogError(ex, "Exception in OpenMenuDetour");
                    goto Original;
                }

                // remove any InventoryContextMenuItems that may have been added - these will crash the game
                args.Items.RemoveAll(item => item is InventoryContextMenuItem);

                // set the agent of any remaining custom items
                foreach (var item in args.Items) {
                    if (item is NormalContextMenuItem custom) {
                        custom.Agent = agent;
                    }
                }

                this.Items.AddRange(args.Items);
            }

            if (this.Items.Count > MaxItems) {
                var toRemove = this.Items.Count - MaxItems;
                this.Items.RemoveRange(MaxItems, toRemove);
                Logger.LogWarning($"Context menu item limit ({MaxItems}) exceeded. Removing {toRemove} item(s).");
            }

            var hasCustomDisabled = this.Items.Any(item => !item.Enabled);
            var hasAnyDisabled = hasGameDisabled || hasCustomDisabled;

            for (var i = 0; i < this.Items.Count; i++) {
                var item = this.Items[i];

                if (hasAnyDisabled) {
                    var disabledArg = &atkValueArgs[7 + this.Items.Count + i];
                    this._atkValueChangeType(disabledArg, ValueType.Int);
                    disabledArg->Int = item.Enabled ? 0 : 1;
                }

                // set up the agent to take the appropriate action for this item
                *(menuActions + 7 + i) = item switch {
                    NativeContextMenuItem nativeItem => nativeItem.InternalAction,
                    _ => inventory ? (byte) 0xFF : NoopContextId,
                };

                // set up the menu item
                var newItem = &atkValueArgs[7 + i];
                this._atkValueChangeType(newItem, ValueType.String);

                var name = item switch {
                    NormalContextMenuItem custom => this.Language switch {
                        ClientLanguage.Japanese => custom.NameJapanese,
                        ClientLanguage.English => custom.NameEnglish,
                        ClientLanguage.German => custom.NameGerman,
                        ClientLanguage.French => custom.NameFrench,
                        _ => custom.NameEnglish,
                    },
                    InventoryContextMenuItem custom => this.Language switch {
                        ClientLanguage.Japanese => custom.NameJapanese,
                        ClientLanguage.English => custom.NameEnglish,
                        ClientLanguage.German => custom.NameGerman,
                        ClientLanguage.French => custom.NameFrench,
                        _ => custom.NameEnglish,
                    },
                    NativeContextMenuItem native => native.Name,
                    _ => "Invalid context menu item",
                };
                var nameBytes = Encoding.UTF8.GetBytes(name).Terminate();
                fixed (byte* nameBytesPtr = nameBytes) {
                    this._atkValueSetString(newItem, nameBytesPtr);
                }
            }

            (&atkValueArgs[0])->UInt = (uint) this.Items.Count;

            menuSize = (int) (&atkValueArgs[0])->UInt;
            if (hasAnyDisabled) {
                menuSize *= 2;
            }

            menuSize += 7;

            Original:
            return this.ContextMenuOpenHook!.Original(addon, menuSize, atkValueArgs);
        }

        private byte ItemSelectedDetour(IntPtr addon, int index, byte a3) {
            if (index < 0 || index >= this.Items.Count) {
                goto Original;
            }

            var item = this.Items[index];
            switch (item) {
                // a custom item is being clicked
                case NormalContextMenuItem custom: {
                    var addonName = this.GetParentAddonName(addon);
                    var info = GetAgentInfo(custom.Agent);

                    var args = new ContextMenuItemSelectedArgs(
                        addon,
                        custom.Agent,
                        addonName,
                        info.actorId,
                        info.contentIdLower,
                        info.text,
                        info.actorWorld
                    );

                    try {
                        custom.Action(args);
                    } catch (Exception ex) {
                        Logger.LogError(ex, "Exception in custom context menu item");
                    }

                    break;
                }
                case InventoryContextMenuItem custom: {
                    var addonName = this.GetParentAddonName(addon);
                    var info = GetInventoryAgentInfo(custom.Agent);

                    var args = new InventoryContextMenuItemSelectedArgs(
                        addon,
                        custom.Agent,
                        addonName,
                        info.itemId,
                        info.itemAmount,
                        info.itemHq
                    );

                    try {
                        custom.Action(args);
                    } catch (Exception ex) {
                        Logger.LogError(ex, "Exception in custom context menu item");
                    }

                    break;
                }
            }

            Original:
            return this.ContextMenuItemSelectedHook!.Original(addon, index, a3);
        }
    }
}