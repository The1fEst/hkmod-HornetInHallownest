extern alias Silksong;
extern alias SilksongPM;
using System;
using System.Collections;
using System.Collections.Generic;
using HornetInHallownest.Core;
using HornetInHallownest.Util;
using UnityEngine;
using SHeroController = Silksong::HeroController;
using SPlayerData = Silksong::PlayerData;
using SInventoryPane = Silksong::InventoryPane;
using SInventoryPaneList = Silksong::InventoryPaneList;

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

    public override string Id => "quick-map";

    public override void Initialize() {
        Detour(typeof(SHeroController), "CanQuickMap", OnCanQuickMap, typeof(bool));
        Detour(typeof(Silksong::EventRegister), "SendEvent", OnSendEvent, typeof(int), typeof(GameObject));
        Detour(typeof(SInventoryPaneList), "SetNextPane", OnSetNextPane, typeof(int), typeof(SInventoryPane));
    }

    // Hornet's inventory has no map pane (Silksong's only shows Pharloom and is disabled), so paging onto its slot
    // switches to HK's inventory on the map instead.
    private SInventoryPane OnSetNextPane(Func<SInventoryPaneList, int, SInventoryPane, SInventoryPane> orig,
        SInventoryPaneList self, int direction, SInventoryPane currentPane) {
        if (!HeroSwitch.HornetActive || !PlayerData.instance.hasMap || !PassesMapSlot(self, direction, currentPane))
            return orig(self, direction, currentPane);

        StartCoroutine(SwitchToHkInventoryMap(self));
        return currentPane; // the FSM cancels a move onto the same pane
    }

    // InventoryPaneList.GetNextPane's walk: does it reach the map slot before the next available pane?
    private static bool PassesMapSlot(SInventoryPaneList list, int direction, SInventoryPane currentPane) {
        var count = list.TotalPaneCount;
        var index = list.GetPaneIndex(currentPane);
        for (var i = 0; i < count; i++) {
            index = (index + direction + count) % count;
            if (index == (int)SInventoryPaneList.PaneTypes.Map) return true;
            var pane = list.GetPane(index);
            if (pane && list.InvokeMethod<bool>("IsPaneAvailable", pane, (SInventoryPaneList.PaneTypes)index))
                return false;
        }

        return false;
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
