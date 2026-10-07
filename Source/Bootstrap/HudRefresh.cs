extern alias Silksong;
extern alias SilksongPM;
using System;
using System.Collections.Generic;
using HornetInHallownest.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HornetInHallownest.Bootstrap;

// Refreshes for Silksong HUD elements that don't auto-update when their backing PlayerData field changes mid-play.
internal static class HudRefresh {
    // "MAX HP UP" can only ever add a mask.
    internal static void RefreshMaxHealthHud() {
        SendToHealthDisplays("MAX HP UP");
    }

    // "HUD APPEAR RESET" is a global transition (any state -> Init) so every mask re-reads count and fill; it can also
    // remove masks when maxHealth dropped (which "MAX HP UP" can't). Needed because the DDOL rig otherwise shows the
    // previous life's mask count on a reused rig (new save / menu re-entry).
    internal static void ResetHealthHud() {
        SendToHealthDisplays("HUD APPEAR RESET");
    }

    private static void SendToHealthDisplays(string evt) {
        // Re-enable the self-disabled (EnableFsmSelf(false)) FSMs first, so the event reaches masks parked in Idle/Inactive.
        foreach (var f in Object.FindObjectsByType<SilksongPM::PlayMakerFSM>(FindObjectsSortMode.None))
            if (string.Equals(f.FsmName, "health_display", StringComparison.OrdinalIgnoreCase))
                try {
                    Behaviour b = f;
                    if (!b.enabled) b.enabled = true;
                    f.SendEvent(evt);
                } catch (Exception e) {
                    Log.Error($"[HudRefresh] {f.FsmName}: {e.Message}");
                }
    }

    // DrawSpool() resizes the spool bar from PlayerData.CurrentSilkMaxBasic; nothing else re-runs it after silkMax
    // changes mid-play (it's only called once at HUD bring-up).
    // Skipped while Hornet's HUD is hidden: chunks spawned into it never Awake, so SilkChunk.Add throws on them and leaves
    // them blank in the spool. SetHornetHudVisible redraws it when her HUD comes back.
    internal static void RefreshMaxSilkHud() {
        var spool = Silksong::SilkSpool.Instance;
        if (spool && spool.isActiveAndEnabled) spool.DrawSpool();
    }

    // ObjectPool.RecycleAll pulls the spool's chunks back into the pool. Silksong destroys the HUD along with it on quit,
    // but this rig survives, so the spool would keep them listed and the pool would hand them out again as new chunks.
    internal static void ForgetSilkChunks() {
        var spool = Silksong::SilkSpool.Instance;
        if (!spool) return;
        spool.GetFieldValue<List<Silksong::SilkChunk>>("silkChunks")?.Clear();
        spool.SetFieldValue("mossChunks", null);
        spool.SetFieldValue("partsChunk", null);
        spool.SetFieldValue("wasUsingChunk", null);
    }
}
