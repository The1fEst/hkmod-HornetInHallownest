extern alias Silksong;
extern alias SilksongPM;
using System;
using System.Collections.Generic;
using HornetInHallownest.Bootstrap;
using HornetInHallownest.Core;
using HornetInHallownest.Util;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HornetInHallownest.Modules;

// General HK -> Hornet PlayMaker broadcast event relay.
public sealed class HeroBroadcastModule : ModuleBase {
    public override string Id => "broadcast";

    // HK slides its HUD out for cutscenes and dialogue by sending these to Hud Canvas' "Slide Out" FSM. Hornet's
    // separate Silksong HUD has its own "Slide Out" FSM taking the same events.
    private static readonly HashSet<string> hudSlideEvents = ["OUT", "OUT INSTANT", "IN"];

    public override void Initialize() {
        Detour(typeof(Fsm), "BroadcastEventToGameObject", OnBroadcast,
            typeof(GameObject), typeof(FsmEvent), typeof(FsmEventData), typeof(bool), typeof(bool));
        Detour(typeof(FSMUtility), "SendEventToGameObject", OnSendEventToGameObject,
            typeof(GameObject), typeof(FsmEvent), typeof(bool));
    }

    private static void OnSendEventToGameObject(Action<GameObject, FsmEvent?, bool> orig, GameObject go, FsmEvent? ev,
        bool isRecursive) {
        orig(go, ev, isRecursive);
        RelayHudSlide(go, ev?.Name);
    }

    private static void RelayHudSlide(GameObject go, string? eventName) {
        if (!HeroSwitch.HornetActive || eventName == null || !hudSlideEvents.Contains(eventName)) return;
        var hk = typeof(GameCameras).GetFieldValue<GameCameras>("_instance");
        if (hk && go == hk.hudCanvas) GameCamerasBootstrap.SendHornetHudSlideEvent(eventName);
    }

    private void OnBroadcast(Action<Fsm, GameObject, FsmEvent?, FsmEventData?, bool, bool> orig, Fsm self,
        GameObject go, FsmEvent? ev, FsmEventData? data, bool sendToChildren, bool excludeSelf) {
        orig(self, go, ev, data, sendToChildren, excludeSelf);
        try {
            var name = ev?.Name;
            RelayHudSlide(go, name);
            if (string.IsNullOrEmpty(name) || !HeroSwitch.HornetActive) return;

            var hero = HornetSpawner.Hornet;
            if (!hero || go != hero.gameObject) return;

            // The roar's real source is HK's "Roar Object" (a boss body), not the event sender.
            if (name == "ROAR ENTER") {
                var source = FsmLookupModule.RoarObject;
                if (!source) source = self.GameObject;
                try {
                    SetRoarFacingTarget(hero.gameObject, source);
                } catch (Exception e) {
                    LogError($"roar facing: {e.Message}");
                }
            }

            foreach (var fsm in hero.gameObject.GetComponents<SilksongPM::PlayMakerFSM>())
                fsm.SendEvent(name);
        } catch (Exception e) {
            LogError($"relay: {e.Message}");
        }
    }

    #region Roar facing

    // Hornet faces a roar by comparing her x to a "Roar Wave Emitter" object's x (CheckTargetDirection). HK bosses never
    // position that object (Silksong bosses do; HK's own facing lives on the Knight-only "Roar Lock" FSM she lacks), and
    // the emitter isn't a real FsmVariable, so it can't be rebound by name. Feed the roaring boss straight into the
    // action's target instead, before ROAR ENTER enters the state that reads it.
    private static void SetRoarFacingTarget(GameObject heroGo, GameObject? boss) {
        if (!boss) return;
        foreach (var fsm in heroGo.GetComponents<SilksongPM::PlayMakerFSM>()) {
            if (fsm.FsmName != "Roar and Wound States") continue;
            var state = fsm.Fsm.GetState("Roar Lock Start");
            if (state == null) return;
            foreach (var action in state.Actions)
                if (action is Silksong::HutongGames.PlayMaker.Actions.CheckTargetDirection { target: not null } ctd)
                    ctd.target.Value = boss;
            return;
        }
    }

    #endregion
}
