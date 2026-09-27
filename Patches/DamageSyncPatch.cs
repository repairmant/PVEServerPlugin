using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace PVEServerPlugin.Patches
{
    /// <summary>
    /// Fixes "blocks lose integrity but never break" in PVE areas.
    ///
    /// The game broadcasts every synced block hit to all clients with the RAW damage (MySlimBlock.SendDamage /
    /// SendDamageBatch) *before* the server runs its damage handlers, so the server-side PVE zeroing never reaches
    /// clients: they apply the full hit to their local copy (the plugin doesn't run there) while the server block
    /// stays intact. Here the PVE rule is applied before the broadcast, so a hit the server would nullify is never
    /// sent or applied anywhere.
    /// </summary>
    [PatchShim]
    public static class DamageSyncPatch
    {
        private static readonly Logger Log = LogManager.GetLogger("PVEPlugin");
        private static int _errors;

        public static void Patch(PatchContext ctx)
        {
            var doDamage = typeof(MySlimBlock).GetMethod(nameof(MySlimBlock.DoDamage),
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(float), typeof(MyStringHash), typeof(bool), typeof(MyHitInfo?), typeof(long), typeof(long), typeof(bool), typeof(MyStringHash?) },
                null);
            var sendBatch = typeof(MySlimBlock).GetMethod(nameof(MySlimBlock.SendDamageBatch),
                BindingFlags.Public | BindingFlags.Static);

            if (doDamage == null || sendBatch == null)
            {
                Log.Error($"DamageSyncPatch: game methods not found (DoDamage={doDamage != null}, SendDamageBatch={sendBatch != null}); client damage sync fix NOT active");
                return;
            }

            ctx.GetPattern(doDamage).Prefixes.Add(typeof(DamageSyncPatch).GetMethod(nameof(DoDamagePrefix), BindingFlags.NonPublic | BindingFlags.Static));
            ctx.GetPattern(sendBatch).Prefixes.Add(typeof(DamageSyncPatch).GetMethod(nameof(SendDamageBatchPrefix), BindingFlags.NonPublic | BindingFlags.Static));
            Log.Info("DamageSyncPatch: PVE rule applied before block damage is broadcast to clients");
        }

        /// <summary>Synced single-block hits (projectiles, mod weapons, thruster flames, scripts).</summary>
        private static void DoDamagePrefix(MySlimBlock __instance, ref float damage, bool sync, long attackerId)
        {
            if (!sync || damage <= 0f || !Sync.IsServer) return;
            try
            {
                // damage <= 0 makes the game return before broadcasting or applying anything.
                if (DamageHandler.ShouldZeroDamage(__instance, attackerId)) damage = 0f;
            }
            catch (Exception e)
            {
                if (_errors++ < 5) Log.Error(e, "DamageSyncPatch.DoDamagePrefix failed (hit left unchanged)");
            }
        }

        /// <summary>Batched hits (collision deformation): drop the blocks the PVE rule protects from the broadcast.</summary>
        private static void SendDamageBatchPrefix(ref Dictionary<MySlimBlock, float> blocks, long attackerId)
        {
            if (blocks == null || blocks.Count == 0 || !Sync.IsServer) return;
            try
            {
                Dictionary<MySlimBlock, float> filtered = null;
                foreach (var pair in blocks)
                {
                    if (!DamageHandler.ShouldZeroDamage(pair.Key, attackerId)) continue;
                    if (filtered == null) filtered = new Dictionary<MySlimBlock, float>(blocks);
                    filtered.Remove(pair.Key);
                }
                // Replace only our copy of the argument; the caller's dictionary is left untouched.
                if (filtered != null) blocks = filtered;
            }
            catch (Exception e)
            {
                if (_errors++ < 5) Log.Error(e, "DamageSyncPatch.SendDamageBatchPrefix failed (batch left unchanged)");
            }
        }
    }
}
