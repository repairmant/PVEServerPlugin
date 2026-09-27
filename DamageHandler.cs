using System;
using NLog;
using PVEServerPlugin.Modules;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace PVEServerPlugin
{
    public static class DamageHandler
    {
        private static bool _init;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public static void Init()
        {
            if (_init)
                return;

            _init = true;

            MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, ProcessDamage);
        }


        private static void ProcessDamage(object target, ref MyDamageInformation info)
        {
            if (ShouldZeroDamage(target, info.AttackerId)) info.Amount = 0;
        }

        /// <summary>
        /// The PVE rule: true when damage from attackerId to target must be nullified.
        /// Shared by the server damage handler and DamageSyncPatch, which stops the raw hit from being
        /// broadcast to clients before the server handler zeroes it.
        /// </summary>
        internal static bool ShouldZeroDamage(object target, long attackerId)
        {
            if (!Config.Instance.EnablePlugin) return false;
            long id;
            switch (target)
            {
                case MySlimBlock block:
                    id = block.CubeGrid.BigOwners.Count > 0 ? block.CubeGrid.BigOwners[0]:0;
                    if (Config.Instance.PvpZones?.Count > 0)
                    {
                        foreach (var zone in Config.Instance.PvpZones)
                        {
                            if (!zone.IsWithinZoneRadius(block.CubeGrid)) continue;
                            return false;
                        }
                    }

                    break;
                case MyCharacter _:
                    return false;
                case MyCubeGrid grid:
                    id =  grid.BigOwners.Count > 0 ? grid.BigOwners[0]:0;
                    if (Config.Instance.PvpZones?.Count > 0)
                    {
                        foreach (var zone in Config.Instance.PvpZones)
                        {
                            if (!zone.IsWithinZoneRadius(grid)) continue;
                            return false;
                        }
                    }

                    break;
                default:
                    id = 0;
                    break;
            }


            if (MyEntities.TryGetEntityById(attackerId, out var attacker, true))
            {

                if (attacker is MyVoxelBase)
                    return false;

                if (attacker is MyCubeBlock block)
                {
                    attackerId = block.CubeGrid.BigOwners.Count > 0 ? block.CubeGrid.BigOwners[0]:0;
                }

                if (attacker is MyHandToolBase handTool)
                {
                    attackerId = handTool.OwnerIdentityId;
                }

                if (attacker is MyAngleGrinder grinder)
                {
                    attackerId = grinder.OwnerIdentityId;
                }

                if (attacker is MyUserControllableGun controllableGun)
                {
                    attackerId = controllableGun.CubeGrid.BigOwners.Count > 0 ? controllableGun.CubeGrid.BigOwners[0]:0;
                }

                if (attacker is MyAutomaticRifleGun gun)
                {
                    attackerId = gun.OwnerIdentityId;
                }

                if (attacker is MyShipToolBase tool)
                {
                    attackerId = tool.CubeGrid.BigOwners.Count > 0 ? tool.CubeGrid.BigOwners[0]:0;
                }

                if (attacker is MyAutomaticRifleGun characterWeapon)
                {
                    attackerId = characterWeapon.OwnerIdentityId;
                }

                if (attacker is MyCubeGrid grid)
                {
                    attackerId = grid.BigOwners.Count > 0 ? grid.BigOwners[0]:0;
                }

                if (attacker is MyLargeTurretBase turret)
                {
                    attackerId = turret.CubeGrid.BigOwners.Count > 0 ? turret.CubeGrid.BigOwners[0]:0;
                }
            }

            if (id == 0 || MySession.Static.Players.IdentityIsNpc(id) || MySession.Static.Players.IdentityIsNpc(attackerId) || id == attackerId) return false;

            if (attackerId == 0 && Config.Instance.EnableNoOwner) return false;


            var attackerSteamId = MySession.Static.Players.TryGetSteamId(attackerId);
            var targetSteamId = MySession.Static.Players.TryGetSteamId(id);

            // attackerId and id are player identities; AreAtWar resolves their factions for faction wars.
            if (ConflictPairModule.AreAtWar(attackerId, id)) return false;

            if (Config.Instance.EnableFactionDamage)
            {
                var fac1 = MySession.Static.Factions.TryGetPlayerFaction(attackerId);
                var fac2 = MySession.Static.Factions.TryGetPlayerFaction(id);

                if (fac2 != null && fac1 != null && fac2 == fac1) return false;
            }

            if (attackerSteamId == targetSteamId) return false;
            return true;
        }

    }
}
