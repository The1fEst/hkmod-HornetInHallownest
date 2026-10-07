extern alias Silksong;
using System;
using HornetInHallownest.Core;
using HutongGames.PlayMaker.Actions;
using SActorStates = Silksong::GlobalEnums.ActorStates;

namespace HornetInHallownest.Modules;

// HK's quick map is the Knight's own "Map Control" FSM, left running while he's inert (KnightPresence). It gates on his
// HeroController.CanQuickMap and freezes him via SendMessage; both have to act on Hornet instead.
public sealed class QuickMapModule : ModuleBase {
    internal const string MapControlFsm = "Map Control";

    public override string Id => "quick-map";

    public override void Initialize() {
        Detour(typeof(HeroController), "CanQuickMap", OnCanQuickMap);
        Detour(typeof(SendMessage), "DoSendMessage", OnSendMessage);
    }

    // HK's conditions on Hornet's state; Silksong's own CanQuickMap requires a Pharloom map.
    private static bool OnCanQuickMap(Func<HeroController, bool> orig, HeroController self) {
        var hornet = HornetSpawner.Hornet;
        if (!HeroSwitch.HornetActive || !hornet || self.enabled) return orig(self);
        return !GameManager.instance.isPaused && !hornet.controlReqlinquished &&
               hornet.hero_state != SActorStates.no_input && hornet.cState is {
                   onConveyor: false, dashing: false, backDashing: false, attacking: false, recoiling: false,
                   transitioning: false, hazardDeath: false, hazardRespawning: false, recoilFrozen: false,
                   onGround: true
               } && hornet.CanInput();
    }

    private static void OnSendMessage(Action<SendMessage> orig, SendMessage self) {
        orig(self);
        if (!HeroSwitch.HornetActive || self.Fsm.Name != MapControlFsm) return;
        if (HornetSpawner.Hornet is not { } hornet) return;
        switch (self.functionCall.FunctionName) {
            case "RelinquishControl":
                hornet.RelinquishControl();
                break;
            case "StopAnimationControl":
                hornet.StopAnimationControl();
                break;
            case "RegainControl":
                hornet.RegainControl();
                break;
            case "StartAnimationControl":
                hornet.StartAnimationControl();
                break;
        }
    }
}
