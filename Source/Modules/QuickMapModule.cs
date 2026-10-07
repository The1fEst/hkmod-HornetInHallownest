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

    // Set from the first pane move of a switch between the two inventories until the target one has opened. Pane moves in
    // between are swallowed: a second switch started mid-way leaves both inventories open.
    private bool switchingInventories;

    public override string Id => "quick-map";

    public override void Initialize() {
        Detour(typeof(SHeroController), "CanQuickMap", OnCanQuickMap, typeof(bool));
        Detour(typeof(Silksong::EventRegister), "SendEvent", OnSendEvent, typeof(int), typeof(GameObject));
        Detour(typeof(SInventoryPane), "get_IsAvailable", OnPaneIsAvailable);
        Detour(typeof(SInventoryPaneList), "SetNextPane", OnSetNextPane, typeof(int), typeof(SInventoryPane));
        Detour(typeof(SInventoryPaneList), "SetCurrentPane", OnSetCurrentPane, typeof(int), typeof(SInventoryPane));
        Detour(typeof(ListenForPaneLeft), "OnUpdate", OnPaneLeft);
        Detour(typeof(ListenForPaneRight), "OnUpdate", OnPaneRight);
    }

    // Silksong's map pane only shows Pharloom and stays disabled. It's listed (pane icon) while HK's map is owned, but
    // moving onto it switches to HK's inventory on the map instead, and paging off HK's map switches back.
    private static bool OnPaneIsAvailable(Func<SInventoryPane, bool> orig, SInventoryPane self) {
        if (HeroSwitch.HornetActive && self.TryGetComponent<Silksong::InventoryMapManager>(out _))
            return PlayerData.instance.hasMap;
        return orig(self);
    }

    private SInventoryPane OnSetNextPane(Func<SInventoryPaneList, int, SInventoryPane, SInventoryPane> orig,
        SInventoryPaneList self, int direction, SInventoryPane currentPane) {
        if (!HeroSwitch.HornetActive) return orig(self, direction, currentPane);
        if (switchingInventories) return currentPane; // the FSM cancels a move onto the same pane
        if (!PlayerData.instance.hasMap || StepToPane(self, self.GetPaneIndex(currentPane), direction) != MapPaneIndex)
            return orig(self, direction, currentPane);

        StartCoroutine(SwitchToHkInventoryMap(self));
        return currentPane;
    }

    // Direct moves, e.g. selecting the map's pane icon. Without a current pane the inventory is opening, never onto the map.
    private SInventoryPane OnSetCurrentPane(Func<SInventoryPaneList, int, SInventoryPane?, SInventoryPane> orig,
        SInventoryPaneList self, int index, SInventoryPane? currentPane) {
        if (!HeroSwitch.HornetActive || !currentPane) return orig(self, index, currentPane);
        if (switchingInventories) return currentPane;
        if (index != MapPaneIndex) return orig(self, index, currentPane);

        StartCoroutine(SwitchToHkInventoryMap(self));
        return currentPane;
    }

    private void OnPaneLeft(Action<ListenForPaneLeft> orig, ListenForPaneLeft self) {
        if (!TryLeaveHkMap(self.Fsm, -1)) orig(self);
    }

    private void OnPaneRight(Action<ListenForPaneRight> orig, ListenForPaneRight self) {
        if (!TryLeaveHkMap(self.Fsm, 1)) orig(self);
    }

    // True when the press is consumed: it leaves HK's map for Hornet's inventory, or a switch is already under way.
    private bool TryLeaveHkMap(Fsm fsm, int direction) {
        if (!HeroSwitch.HornetActive || GameManager.instance.isPaused) return false;
        var inventory = GameManager.instance.inventoryFSM;
        if (!inventory || fsm != inventory.Fsm) return false;
        var actions = InputHandler.Instance.inputActions;
        if (!(direction < 0 ? actions.paneLeft : actions.paneRight).WasPressed) return false;
        if (switchingInventories) return true;
        var currentPane = inventory.FsmVariables.FindFsmGameObject("Current Pane");
        var mapPane = inventory.FsmVariables.FindFsmGameObject("Map Pane");
        if (inventory.ActiveStateName != "Opened" || currentPane == null || mapPane == null || !currentPane.Value ||
            currentPane.Value != mapPane.Value)
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

    private IEnumerator SwitchToHkInventoryMap(SInventoryPaneList list) {
        switchingInventories = true;
        try {
            var fsm = SilksongPM::PlayMakerFSM.FindFsmOnGameObject(list.gameObject, "Inventory Control");
            if (!fsm) yield break;
            yield return WaitForState(() => fsm.ActiveStateName, "Opened");
            fsm.SendEvent("CLOSE");
            yield return WaitForState(() => fsm.ActiveStateName, "Closed");
            var inventory = GameManager.instance.inventoryFSM;
            if (!inventory) yield break;
            inventory.SendEvent("OPEN INVENTORY MAP");
            yield return WaitForState(() => inventory.ActiveStateName, "Opened");
        } finally {
            switchingInventories = false;
        }
    }

    private IEnumerator SwitchToHornetInventory(PlayMakerFSM hkInventory, int direction) {
        switchingInventories = true;
        try {
            // On the map HK's inventory holds "Do Not Close": CLOSE is refused, and the forced INVENTORY CANCEL stalls on
            // the same check before it returns control to the hero.
            hkInventory.FsmVariables.FindFsmBool("Do Not Close").Value = false;
            hkInventory.SendEvent("CLOSE");
            yield return WaitForState(() => hkInventory.ActiveStateName, "Closed");
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
            yield return WaitForState(() => fsm.ActiveStateName, "Opened");
        } finally {
            switchingInventories = false;
        }
    }

    // In real time: the inventories pause the game, and their close animations last a fixed time, not a frame count.
    private static IEnumerator WaitForState(Func<string> activeState, string state, float timeoutSeconds = 3f) {
        var deadline = Time.unscaledTime + timeoutSeconds;
        while (activeState() != state && Time.unscaledTime < deadline) yield return null;
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
