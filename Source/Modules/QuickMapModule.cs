extern alias Silksong;
using System;
using System.Collections.Generic;
using HornetInHallownest.Core;
using UnityEngine;
using SHeroController = Silksong::HeroController;
using SPlayerData = Silksong::PlayerData;

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
