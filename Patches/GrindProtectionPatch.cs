using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Torch.Managers.PatchManager;
using VRage.Game.Components;
using VRage.Game.Entity;

namespace PVEServerPlugin.Patches
{
    /// <summary>
    /// Stops mod grinders (e.g. Nanobot Build and Repair) from grinding other players' grids in PVE areas.
    ///
    /// Vanilla ship/hand grinders run the damage system (where the PVE rule zeroes the hit) before calling
    /// MySlimBlock.DecreaseMountLevel, and always pass the grinding player's identity. Mods can only reach
    /// IMySlimBlock.DecreaseMountLevel, which passes identity 0 and skips the damage system entirely, so the PVE rule
    /// never saw them. For those calls the grinder is found from the output inventory and the same PVE rule
    /// (DamageHandler.ShouldZeroDamage) decides whether the grind may happen.
    ///
    /// Build and Repair grinds into a private inventory created with "new MyInventory(...)" that is not attached to
    /// any entity, so its owner block is looked up by scanning mod game-logic components for fields holding that
    /// inventory (cached). If the grinder still can't be identified, it is treated as unowned.
    /// </summary>
    [PatchShim]
    public static class GrindProtectionPatch
    {
        private static readonly Logger Log = LogManager.GetLogger("PVEPlugin");
        private static int _errors;

        // Detached inventory -> entity id of the block whose mod component owns it.
        private static readonly ConditionalWeakTable<MyInventoryBase, StrongBox<long>> DetachedOwners = new ConditionalWeakTable<MyInventoryBase, StrongBox<long>>();
        private static readonly Dictionary<Type, FieldInfo[]> InventoryFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);
        private static DateTime _lastScan = DateTime.MinValue;

        private static readonly FieldInfo CompositeComponents =
            typeof(MyCompositeGameLogicComponent).GetField("m_logicComponents", BindingFlags.Instance | BindingFlags.NonPublic);

        // Once-a-minute summary so blocked grinding is visible in the Torch log.
        private static int _blocked;
        private static string _lastBlocked;
        private static DateTime _lastReport = DateTime.UtcNow;

        public static void Patch(PatchContext ctx)
        {
            var target = typeof(MySlimBlock).GetMethod(nameof(MySlimBlock.DecreaseMountLevel),
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(float), typeof(MyInventoryBase), typeof(bool), typeof(long), typeof(bool) }, null);
            if (target == null)
            {
                Log.Error("GrindProtectionPatch: MySlimBlock.DecreaseMountLevel not found; mod grinder protection NOT active");
                return;
            }

            ctx.GetPattern(target).Prefixes.Add(typeof(GrindProtectionPatch).GetMethod(nameof(DecreaseMountLevelPrefix), BindingFlags.NonPublic | BindingFlags.Static));
            Log.Info("GrindProtectionPatch: PVE rule applied to mod grinders (DecreaseMountLevel without identity)");
        }

        private static bool DecreaseMountLevelPrefix(MySlimBlock __instance, float grinderAmount, MyInventoryBase outputInventory, long identityId)
        {
            // Vanilla grinders pass the player's identity and were already checked by the damage system.
            if (identityId != 0 || grinderAmount <= 0f || !Sync.IsServer) return true;
            try
            {
                var attackerId = ResolveGrinder(outputInventory, out var grinderName);
                if (!DamageHandler.ShouldZeroDamage(__instance, attackerId)) return true;

                _blocked++;
                _lastBlocked = $"{grinderName} -> {__instance.CubeGrid.DisplayName}";
                Report();
                return false;
            }
            catch (Exception e)
            {
                if (_errors++ < 5) Log.Error(e, "GrindProtectionPatch prefix failed (grind left unchanged)");
                return true;
            }
        }

        /// <summary>Entity id (block) or identity id (character) of the grinder; 0 when unknown.</summary>
        private static long ResolveGrinder(MyInventoryBase inventory, out string name)
        {
            name = "unknown grinder";
            if (inventory == null) return 0;

            switch (inventory.Entity as MyEntity)
            {
                case MyCharacter character:
                    name = character.DisplayName;
                    return character.GetPlayerIdentityId();
                case MyCubeBlock block:
                    name = block.DisplayNameText ?? block.BlockDefinition.Id.SubtypeName;
                    return block.EntityId; // ShouldZeroDamage resolves this to the grinder grid's owner
                case null:
                    break;
                default:
                    return 0;
            }

            if (!DetachedOwners.TryGetValue(inventory, out var owner) && DateTime.UtcNow - _lastScan >= RescanInterval)
            {
                _lastScan = DateTime.UtcNow;
                ScanModInventories();
                DetachedOwners.TryGetValue(inventory, out owner);
            }

            if (owner == null) return 0;
            if (MyEntities.TryGetEntityById(owner.Value, out var entity) && entity is MyCubeBlock ownerBlock)
                name = ownerBlock.DisplayNameText ?? ownerBlock.BlockDefinition.Id.SubtypeName;
            return owner.Value;
        }

        /// <summary>Maps detached inventories held by mod game-logic components to their block.</summary>
        private static void ScanModInventories()
        {
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid) || grid.MarkedForClose) continue;
                foreach (var block in grid.GetFatBlocks())
                {
                    var logic = block.GameLogic;
                    if (logic == null || logic is MyNullGameLogicComponent) continue;

                    if (logic is MyCompositeGameLogicComponent && CompositeComponents?.GetValue(logic) is IEnumerable parts)
                    {
                        foreach (var part in parts)
                            if (part is MyGameLogicComponent component) MapComponent(component, block.EntityId);
                    }
                    else
                    {
                        MapComponent(logic, block.EntityId);
                    }
                }
            }
        }

        private static void MapComponent(MyGameLogicComponent component, long blockEntityId)
        {
            foreach (var field in FieldsHoldingInventories(component.GetType()))
            {
                if (!(field.GetValue(component) is MyInventoryBase inventory) || inventory.Entity != null) continue;
                DetachedOwners.Remove(inventory);
                DetachedOwners.Add(inventory, new StrongBox<long>(blockEntityId));
            }
        }

        private static FieldInfo[] FieldsHoldingInventories(Type type)
        {
            if (InventoryFields.TryGetValue(type, out var fields)) return fields;

            var found = new List<FieldInfo>();
            // Game components never keep detached grind inventories; only look at mod script types.
            var assembly = type.Assembly.GetName().Name;
            if (!assembly.StartsWith("Sandbox.") && !assembly.StartsWith("VRage") && !assembly.StartsWith("SpaceEngineers."))
            {
                for (var t = type; t != null && t != typeof(MyGameLogicComponent); t = t.BaseType)
                    foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (typeof(MyInventoryBase).IsAssignableFrom(field.FieldType) || field.FieldType == typeof(VRage.Game.ModAPI.IMyInventory))
                            found.Add(field);
            }

            fields = found.ToArray();
            InventoryFields[type] = fields;
            return fields;
        }

        private static void Report()
        {
            if (DateTime.UtcNow - _lastReport < TimeSpan.FromMinutes(1)) return;
            Log.Info($"GrindProtectionPatch: blocked {_blocked} mod grind steps on protected blocks in the last minute (last: {_lastBlocked})");
            _blocked = 0;
            _lastReport = DateTime.UtcNow;
        }
    }
}
