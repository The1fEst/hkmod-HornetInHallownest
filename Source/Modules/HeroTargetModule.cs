using System;
using HornetInHallownest.Core;
using HornetInHallownest.Util;
using HutongGames.PlayMaker;

namespace HornetInHallownest.Modules;

// Redirect HK's "where/who is the hero" to the active hero.
//   - PlayMaker global "Hero" var (most enemy/cutscene/UI FSMs)
//   - LineOfSightDetector + the GetHero action
//   - SoulOrb (a plain MonoBehaviour reading HeroController.instance directly) + soul->silk conversion
//   - CameraTarget + CameraController (follow state: falling, look up/down, facing, dash)
public sealed class HeroTargetModule : ModuleBase {
    public override string Id => "hero-target";

    public override void Initialize() {
        // Enemy targeting: LoS reads HeroController.instance (positional), so place the inert Knight at Hornet's spot for
        // the call, restore after. GetHero caches instance into a per-FSM local var, so rewrite its result.
        Detour(typeof(LineOfSightDetector), "Update", OnLosUpdate);
        Detour(typeof(GetHero), "OnEnter", OnGetHero);

        // SoulOrb (pooled) homes to HeroController.instance: Start caches it once at pool warmup (before Hornet exists),
        // OnEnable fires per fling, so retarget on both. AddMPCharge converts the soul grant to silk.
        Detour(typeof(SoulOrb), "Start", OnSoulOrbActivate);
        Detour(typeof(SoulOrb), "OnEnable", OnSoulOrbActivate);
        Detour(typeof(HeroController), "AddMPCharge", OnAddMpCharge, typeof(int));

        // The camera follows Hornet's transform (RetargetCamera) but reads the hero's state off HeroController.instance,
        // the inert Knight whose cState froze on deactivation: a frozen `falling` glues the camera to her Y (fallStick),
        // look up/down never triggers, the look-ahead keeps his facing. Mirror her state onto him for the call.
        Detour(typeof(CameraTarget), "Update", (Action<Action<CameraTarget>, CameraTarget>)OnCameraUpdate);
        Detour(typeof(CameraController), "LateUpdate",
            (Action<Action<CameraController>, CameraController>)OnCameraUpdate);
    }

    protected override void OnDeinitialize() {
        // Leave HK coherent: point the global back at the Knight.
        if (heroVar != null && HeroController.UnsafeInstance != null)
            heroVar.Value = HeroController.UnsafeInstance.gameObject;
        heroVar = null;
    }

    #region Global "Hero" variable

    private static FsmGameObject? heroVar;

    // Point HK's PlayMaker global "Hero" at the active hero. HK re-binds it to its Knight on scene entry.
    public static void SyncGlobal() {
        if (heroVar == null) {
            var globals = PlayMakerGlobals.Instance;
            if (!globals) return;
            heroVar = globals.Variables.FindFsmGameObject("Hero");
            if (heroVar == null) {
                return;
            }
        }

        var target = HeroSwitch.ActiveHeroGameObject;
        if (target && heroVar.Value != target) heroVar.Value = target;

        // A hero-GO change leaves HK's cached local "Hero" vars pointing at the old GO; re-sync them.
        if (target && target != lastHeroSwept) {
            SyncLocalHeroVars(target);
            lastHeroSwept = target;
        }
    }

    private static UnityEngine.GameObject? lastHeroSwept;

    // Variables that get set to the hero game object.
    private static readonly string[] heroVarNames = ["Hero", "Hero Obj", "Hero Object"];

    private static void SyncLocalHeroVars(UnityEngine.GameObject hero) {
        foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(UnityEngine.FindObjectsSortMode.None)) {
            var vars = fsm.FsmVariables;
            if (vars == null) continue;
            foreach (var name in heroVarNames) {
                var v = vars.FindFsmGameObject(name);
                if (v != null && v.Value != hero) v.Value = hero;
            }
        }
    }

    #endregion

    #region Enemy targeting

    private void OnGetHero(Action<GetHero> orig, GetHero self) {
        orig(self); // sets storeResult to HeroController.instance (the Knight)
        if (self.storeResult != null && HeroSwitch.ActiveHeroGameObject is { } hero)
            self.storeResult.Value = hero;
    }

    private void OnLosUpdate(Action<LineOfSightDetector> orig, LineOfSightDetector self) {
        var knight = HeroController.UnsafeInstance;
        var hornet = HornetSpawner.Hornet;
        if (!HeroSwitch.HornetActive || !knight || !hornet) {
            orig(self);
            return;
        }

        var kt = knight.transform;
        var saved = kt.position;
        kt.position = hornet.transform.position;
        try {
            orig(self);
        } finally {
            kt.position = saved;
        }
    }

    #endregion

    #region Camera

    // The Knight's position matters too: CameraController's look offset is relative to hero_ctrl.transform.
    private void OnCameraUpdate<T>(Action<T> orig, T self) {
        var knight = HeroController.UnsafeInstance;
        var hornet = HornetSpawner.Hornet;
        if (!HeroSwitch.HornetActive || !knight || knight.enabled || !hornet) {
            orig(self);
            return;
        }

        var kt = knight.transform;
        var ks = knight.cState;
        var hs = hornet.cState;
        var savedPosition = kt.position;
        var savedVelocity = knight.current_velocity;
        var (falling, lookingUp, lookingDown, facingRight, dashing) =
            (ks.falling, ks.lookingUp, ks.lookingDown, ks.facingRight, ks.dashing);

        kt.position = hornet.transform.position;
        knight.current_velocity = hornet.current_velocity;
        // Silksong sets `falling` at zero vertical velocity too (hanging on a ledge); HK only while moving down, and its
        // fall catch would yank the camera down to her.
        (ks.falling, ks.lookingUp, ks.lookingDown, ks.facingRight, ks.dashing) =
            (hs.falling && hornet.current_velocity.y < 0f, hs.lookingUp, hs.lookingDown, hs.facingRight, hs.dashing);
        try {
            orig(self);
        } finally {
            kt.position = savedPosition;
            knight.current_velocity = savedVelocity;
            (ks.falling, ks.lookingUp, ks.lookingDown, ks.facingRight, ks.dashing) =
                (falling, lookingUp, lookingDown, facingRight, dashing);
        }
    }

    #endregion

    #region Soul orbs -> silk

    private void OnSoulOrbActivate(Action<SoulOrb> orig, SoulOrb self) {
        orig(self);
        if (HeroSwitch.HornetActive && HornetSpawner.Hornet is { } hero)
            self.SetFieldValue("target", hero.transform);
    }

    private void OnAddMpCharge(Action<HeroController, int> orig, HeroController self, int amount) {
        if (HeroSwitch.HornetActive && HornetSpawner.Hornet is { } hero) {
            hero.AddSilk(1, true); // 1 silk per orb; skip the Knight's soul add
            return;
        }

        orig(self, amount);
    }

    #endregion
}
