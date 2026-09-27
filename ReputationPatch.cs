using System;
using System.Linq;
using System.Reflection;
using NLog;
using PVEServerPlugin.Modules;
using Torch.Managers.PatchManager;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRage.Network;

namespace PVEServerPlugin
{
    public static class ReputationPatch
    {

        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static void Patch(PatchContext ctx)
        {
            ctx.GetPattern(typeof(MyFactionCollection).GetMethod("FactionStateChangeRequest",
                    BindingFlags.NonPublic | BindingFlags.Static)).Prefixes
                .Add(typeof(ReputationPatch).GetMethod(nameof(ChangeRequest), BindingFlags.NonPublic|BindingFlags.Static));

            ctx.GetPattern(typeof(MyFactionCollection).GetMethod(
                    nameof(MyFactionCollection.DamageFactionPlayerReputation),
                    BindingFlags.Public | BindingFlags.Instance))
                .Prefixes.Add(typeof(ReputationPatch).GetMethod(nameof(DamageFaction), BindingFlags.NonPublic|BindingFlags.Static));
        }

        /// <summary>
        /// Server-side handler for the in-game faction screen. A "Declare War" between player factions becomes a
        /// war challenge; it only goes through once the conflict is Active (accepted by the other side).
        /// </summary>
        private static bool ChangeRequest(MyFactionStateChange action, long fromFactionId, long toFactionId, long playerId)
        {
            if (action != MyFactionStateChange.DeclareWar) return true;
            try
            {
                var factions = MySession.Static.Factions;
                if (factions.IsNpcFaction(fromFactionId) ||
                    factions.IsNpcFaction(toFactionId) ||
                    MySession.Static.Players.IdentityIsNpc(playerId)) return true;

                var context = MyEventContext.Current;
                var senderSteamId = context.IsLocallyInvoked ? 0 : context.Sender.Value;
                var senderIdentity = senderSteamId == 0 ? 0 : MySession.Static.Players.TryGetIdentityId(senderSteamId);

                if (Config.Instance.EnableConflict &&
                    ConflictPairModule.InConflict(fromFactionId, toFactionId, ConflictPairModule.ConflictType.Faction, out _))
                    return true;

                // Server/plugin/mod-originated requests: only allowed for active conflicts (above).
                if (senderIdentity == 0) return false;

                if (!Config.Instance.EnableConflict)
                {
                    ConflictPairModule.Notify(new[] { senderSteamId }, "Wars are disabled on this server.");
                    return false;
                }

                var fromFaction = factions.TryGetFactionById(fromFactionId);
                var authorized = fromFaction != null &&
                                 (fromFaction.IsLeader(senderIdentity) || fromFaction.IsFounder(senderIdentity) ||
                                  MySession.Static.IsUserAdmin(senderSteamId));
                if (!authorized) return false; // the game would reject it as well

                var result = ConflictPairModule.IssueChallenge(fromFactionId, toFactionId,
                    ConflictPairModule.ConflictType.Faction, senderSteamId, out var message);
                if (!string.IsNullOrEmpty(message) && result != ConflictPairModule.ChallengeResult.Created)
                    ConflictPairModule.Notify(new[] { senderSteamId }, message);

                // War accepted by declaring back: let the game apply the declaration.
                if (result == ConflictPairModule.ChallengeResult.BecameActive) return true;

                Core.RequestFactionChange(MyFactionStateChange.AcceptPeace, fromFactionId, toFactionId, playerId);
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "Declare war handling failed; request blocked");
                return false;
            }
        }

        private static bool DamageFaction(MyFactionCollection __instance, long playerIdentityId,
            long attackedIdentityId,
            MyReputationDamageType repDamageType)
        {
            if (MySession.Static.Players.IdentityIsNpc(playerIdentityId) ||
                MySession.Static.Players.IdentityIsNpc(attackedIdentityId)) return true;
            return ConflictPairModule.AreAtWar(playerIdentityId, attackedIdentityId) ||
                   MySession.Static.Factions.GetNpcFactions().Any(x =>
                       x.Members.ContainsKey(playerIdentityId) || x.Members.ContainsKey(attackedIdentityId));
        }

    }
}
