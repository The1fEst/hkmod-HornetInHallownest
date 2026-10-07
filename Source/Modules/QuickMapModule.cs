extern alias Silksong;
extern alias SilksongPM;
using System;
using System.Collections;
using System.Collections.Generic;
using HornetInHallownest.Core;
using HornetInHallownest.Util;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using SHeroController = Silksong::HeroController;
using SPlayerData = Silksong::PlayerData;
using SInventoryPane = Silksong::InventoryPane;
using SInventoryPaneList = Silksong::InventoryPaneList;
using SInventoryShortcutButtons = Silksong::GlobalEnums.InventoryShortcutButtons;

namespace HornetInHallownest.Modules;

// Hornet's own "Map Control" FSM runs the quick map (slow map walk, animations, double tap for the inventory map), but it
// opens Silksong's map UI through EventRegister events. Gate it on HK's map and route those events to HK's map instead.
public sealed class QuickMapModule : ModuleBase {
    // Silksong EventRegister event -> HK's quick map FSM event
    private static readonly Dictionary<int, string> quickMapEvents = new() {
        { "OPEN QUICK MAP".GetHashCode(), "OPEN QUICK MAP" },
        { "CLOSE QUICK MAP".GetHashCode(), "CLOSE QUICK MAP" },
        { "CLOSE QUICK MAP INSTANT".GetHashCode(), "CLOSE QUICK MAP" }
    };

    private static readonly int openInventoryMap = "OPEN INVENTORY MAP".GetHashCode();

    private const int MapPaneIndex = (int)SInventoryPaneList.PaneTypes.Map;

    private bool leavingHkMap;

    public override string Id => "quick-map";

    public override void Initialize() {
        Detour(typeof(SHeroController), "CanQuickMap", OnCanQuickMap, typeof(bool));
        Detour(typeof(Silksong::EventRegister), "SendEvent", OnSendEvent, typeof(int), typeof(GameObject));
        Detour(typeof(SInventoryPaneList), "SetNextPane", OnSetNextPane, typeof(int), typeof(SInventoryPane));
        Detour(typeof(ListenForPaneLeft), "OnUpdate", OnPaneLeft);
        Detour(typeof(ListenForPaneRight), "OnUpdate", OnPaneRight);
    }

    // Hornet's inventory has no map pane (Silksong's only shows Pharloom and is disabled), so paging onto its slot
    // switches to HK's inventory on the map instead, and paging off HK's map switches back.
    private SInventoryPane OnSetNextPane(Func<SInventoryPaneList, int, SInventoryPane, SInventoryPane> orig,
        SInventoryPaneList self, int direction, SInventoryPane currentPane) {
        if (!HeroSwitch.HornetActive || !PlayerData.instance.hasMap ||
            StepToPane(self, self.GetPaneIndex(currentPane), direction) != MapPaneIndex)
            return orig(self, direction, currentPane);

        StartCoroutine(SwitchToHkInventoryMap(self));
        return currentPane; // the FSM cancels a move onto the same pane
    }

    private void OnPaneLeft(Action<ListenForPaneLeft> orig, ListenForPaneLeft self) {
        if (!TryLeaveHkMap(self.Fsm, -1)) orig(self);
    }

    private void OnPaneRight(Action<ListenForPaneRight> orig, ListenForPaneRight self) {
        if (!TryLeaveHkMap(self.Fsm, 1)) orig(self);
    }

    private bool TryLeaveHkMap(Fsm fsm, int direction) {
        if (!HeroSwitch.HornetActive || leavingHkMap || GameManager.instance.isPaused) return false;
        var inventory = GameManager.instance.inventoryFSM;
        if (!inventory || fsm != inventory.Fsm) return false;
        var actions = InputHandler.Instance.inputActions;
        if (!(direction < 0 ? actions.paneLeft : actions.paneRight).WasPressed) return false;
        var currentPane = inventory.FsmVariables.FindFsmGameObject("Current Pane");
        var mapPane = inventory.FsmVariables.FindFsmGameObject("Map Pane");
        if (currentPane == null || mapPane == null || !currentPane.Value || currentPane.Value != mapPane.Value)
            return false;

        StartCoroutine(SwitchToHornetInventory(inventory, direction));
        return true;
    }

    // InventoryPaneList.GetNextPane's walk from `index`, except that it stops at the map slot.
    private static int StepToPane(SInventoryPaneList list, int index, int direction) {
        var count = list.TotalPaneCount;
        for (var i = 0; i < count; i++) {
            index = (index + direction + count) % count;
            if (index == MapPaneIndex) return index;
            var pane = list.GetPane(index);
            if (pane && list.InvokeMethod<bool>("IsPaneAvailable", pane, (SInventoryPaneList.PaneTypes)index))
                return index;
        }

        return -1;
    }

    private static IEnumerator SwitchToHkInventoryMap(SInventoryPaneList list) {
        var fsm = SilksongPM::PlayMakerFSM.FindFsmOnGameObject(list.gameObject, "Inventory Control");
        if (!fsm) yield break;
        for (var i = 0; i < 120 && fsm.ActiveStateName != "Opened"; i++) yield return null;
        fsm.SendEvent("CLOSE");
        for (var i = 0; i < 300 && fsm.ActiveStateName != "Closed"; i++) yield return null;
        var inventory = GameManager.instance.inventoryFSM;
        if (inventory) inventory.SendEvent("OPEN INVENTORY MAP");
    }

    private IEnumerator SwitchToHornetInventory(PlayMakerFSM hkInventory, int direction) {
        leavingHkMap = true;
        try {
            // The forced close HK uses on scene transitions; a plain CLOSE can leave the map pane drawn.
            hkInventory.SendEvent("INVENTORY CANCEL");
            for (var i = 0; i < 300 && hkInventory.ActiveStateName != "Closed"; i++) yield return null;
            if (hkInventory.ActiveStateName != "Closed") yield break;

            var list = typeof(SInventoryPaneList).GetFieldValue<SInventoryPaneList>("_instance");
            if (!list) yield break;
            var index = StepToPane(list, MapPaneIndex, direction);
            if (index < 0 || index == MapPaneIndex) yield break;

            // Opened like the inventory button: the "Inventory" shortcut opens the pane queued by SetNextOpen.
            SInventoryPaneList.SetNextOpen(list.GetPane(index).name);
            var fsm = SilksongPM::PlayMakerFSM.FindFsmOnGameObject(list.gameObject, "Inventory Control");
            if (!fsm) yield break;
            fsm.FsmVariables.FindFsmEnum("Button Shortcut").Value = SInventoryShortcutButtons.Inventory;
            fsm.SendEvent("BUTTON PRESSED");
        } finally {
            leavingHkMap = false;
        }
    }

    // Silksong requires a Pharloom map (HasAnyMap); mapAllRooms short-circuits that check to HK's hasMap.
    private static bool OnCanQuickMap(Func<SHeroController, bool, bool> orig, SHeroController self, bool onBench) {
        var pd = SPlayerData.instance;
        var mapAllRooms = pd.mapAllRooms;
        pd.mapAllRooms = PlayerData.instance.hasMap;
        try {
            return orig(self, onBench);
        } finally {
            pd.mapAllRooms = mapAllRooms;
        }
    }

    private static void OnSendEvent(Action<int, GameObject> orig, int eventNameHash, GameObject excludeGameObject) {
        if (!HeroSwitch.HornetActive) {
            orig(eventNameHash, excludeGameObject);
            return;
        }

        if (quickMapEvents.TryGetValue(eventNameHash, out var hkEvent)) {
            var quickMap = GameCameras.instance ? GameCameras.instance.hudCamera.transform.Find("Quick Map") : null;
            if (quickMap)
                foreach (var fsm in quickMap.GetComponents<PlayMakerFSM>())
                    fsm.SendEvent(hkEvent);
        }
        else if (eventNameHash == openInventoryMap) {
            var inventory = GameManager.instance.inventoryFSM;
            if (inventory) inventory.SendEvent("OPEN INVENTORY MAP");
        }
        else {
            orig(eventNameHash, excludeGameObject);
        }
    }
}
