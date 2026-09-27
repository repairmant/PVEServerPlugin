using System.Linq;
using PVEServerPlugin.Modules;
using Sandbox.Game.World;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace PVEServerPlugin
{
    [Category("pve")]
    public partial class Commands:CommandModule
    {
        private bool ConflictsEnabled()
        {
            if (Config.Instance.EnableConflict && !Core.NexusDetected) return true;
            Context.Respond("Conflict is not enabled");
            return false;
        }

        private bool InGame(out long identityId)
        {
            identityId = Context.Player?.IdentityId ?? 0;
            if (identityId != 0) return true;
            Context.Respond("This command can only be used ingame");
            return false;
        }

        private string OptionalArg() => Context.Args != null && Context.Args.Count > 0 ? string.Join(" ", Context.Args) : null;

        /// <summary>
        /// Resolves "name or tag" to the caller's side and the target side. Faction tags/names win over player names,
        /// unless the caller doesn't lead a faction and a player with that name exists.
        /// </summary>
        private bool ResolveTarget(string nameOrTag, long identityId, out long mySide, out long otherSide, out ConflictPairModule.ConflictType type)
        {
            mySide = otherSide = 0;
            type = ConflictPairModule.ConflictType.Player;

            var factionId = Utilities.IdentityUtility.GetFactionId(nameOrTag);
            var playerId = Utilities.IdentityUtility.GetAnyPlayerId(nameOrTag);
            var leadFaction = ConflictPairModule.LeadFaction(identityId);

            if (factionId > 0 && (leadFaction != null || playerId == 0))
            {
                if (leadFaction == null)
                {
                    Context.Respond("You need to be the leader or founder of a faction to do this for a faction.");
                    return false;
                }
                mySide = leadFaction.FactionId;
                otherSide = factionId;
                type = ConflictPairModule.ConflictType.Faction;
                return true;
            }

            if (playerId > 0)
            {
                mySide = identityId;
                otherSide = playerId;
                return true;
            }

            Context.Respond($"{nameOrTag} not found");
            return false;
        }

        [Command("challenge player", "Issues a challenge to a specified player.  Use player name.")]
        [Permission(MyPromoteLevel.None)]
        public void PlayerChallenge(string playerName)
        {
            if (!ConflictsEnabled() || !InGame(out var id)) return;

            var subjectId = Utilities.IdentityUtility.GetPlayerId(playerName);
            if (subjectId == 0 || subjectId == id)
            {
                Context.Respond("Challenge failed. Player with name " + playerName + " not found or not online");
                return;
            }

            ConflictPairModule.IssueChallenge(id, subjectId, ConflictPairModule.ConflictType.Player, Context.Player.SteamUserId, out var message);
            Context.Respond(message);
        }

        [Command("challenge faction", "Issues a challenge to a specified faction.  Use faction tag")]
        [Permission(MyPromoteLevel.None)]
        public void FactionChallenge(string factionNameOrTag)
        {
            if (!ConflictsEnabled() || !InGame(out var playerId)) return;

            var playerFaction = ConflictPairModule.LeadFaction(playerId);
            var subjectId = Utilities.IdentityUtility.GetFactionId(factionNameOrTag);
            if (subjectId == 0 || playerFaction == null || subjectId == playerFaction.FactionId)
            {
                Context.Respond("Faction error. This command requires you to be the founder or leader of your faction and a valid faction name or tag to issue challenge");
                return;
            }

            if (MySession.Static.Factions.IsNpcFaction(subjectId))
            {
                Context.Respond("Command cannot be used with NPC faction");
                return;
            }

            ConflictPairModule.IssueChallenge(playerFaction.FactionId, subjectId, ConflictPairModule.ConflictType.Faction, Context.Player.SteamUserId, out var message);
            Context.Respond(message);
        }

        [Command("accept challenge", "Accepts all current challenge or specified player name or faction tag at the end of command")]
        [Permission(MyPromoteLevel.None)]
        public void AcceptChallenge()
        {
            if (!ConflictsEnabled() || !InGame(out var playerId)) return;

            var target = OptionalArg();
            if (target == null)
            {
                var accepted = ConflictPairModule.AcceptAllChallenges(playerId, Context.Player.SteamUserId);
                Context.Respond(accepted == 0 ? "No pending challenge found" : $"Accepted {accepted} challenge(s). You are now at war.");
                return;
            }

            if (!ResolveTarget(target, playerId, out var mySide, out var otherSide, out var type)) return;
            ConflictPairModule.AcceptChallenge(mySide, otherSide, type, Context.Player.SteamUserId, out var message);
            Context.Respond(message);
        }

        [Command("decline challenge", "Declines (or withdraws) a pending challenge with the specified player name or faction tag")]
        [Permission(MyPromoteLevel.None)]
        public void DeclineChallenge(string nameOrTag)
        {
            if (!ConflictsEnabled() || !InGame(out var playerId)) return;
            if (!ResolveTarget(nameOrTag, playerId, out var mySide, out var otherSide, out var type)) return;
            ConflictPairModule.DeclineChallenge(mySide, otherSide, type, out var message);
            Context.Respond(message);
        }

        [Command("accept submission", "Accepts specified or all submissions.  Add player name or faction tag at the end of command to specify")]
        [Permission(MyPromoteLevel.None)]
        public void AcceptSubmission()
        {
            if (!ConflictsEnabled() || !InGame(out var playerId)) return;

            var target = OptionalArg();
            if (target == null)
            {
                var accepted = ConflictPairModule.AcceptAllSubmissions(playerId);
                Context.Respond(accepted == 0 ? "No pending submission found" : $"Accepted {accepted} submission(s).");
                return;
            }

            if (!ResolveTarget(target, playerId, out var mySide, out var otherSide, out var type)) return;
            ConflictPairModule.AcceptSubmission(mySide, otherSide, type, out var message);
            Context.Respond(message);
        }

        [Command("submit faction", "Initiates submission by requesting to submit to all or specified faction tag or player name")]
        [Permission(MyPromoteLevel.None)]
        public void FactionSubmit(string factionNameOrTag)
        {
            if (!ConflictsEnabled() || !InGame(out var playerId)) return;

            var playerFaction = ConflictPairModule.LeadFaction(playerId);
            var subjectId = Utilities.IdentityUtility.GetFactionId(factionNameOrTag);
            if (subjectId == 0 || playerFaction == null)
            {
                Context.Respond("Faction error. This command requires you to be the founder or leader of your faction and a valid faction name or tag");
                return;
            }

            ConflictPairModule.RequestSubmit(playerFaction.FactionId, subjectId, ConflictPairModule.ConflictType.Faction, Context.Player.SteamUserId, out var message);
            Context.Respond(message);
        }

        [Command("submit player","Issues submissions to a specified player")]
        [Permission(MyPromoteLevel.None)]
        public void PlayerSubmit(string playerName)
        {
            if (!ConflictsEnabled() || !InGame(out var id)) return;

            var otherId = Utilities.IdentityUtility.GetAnyPlayerId(playerName);
            if (otherId == 0)
            {
                Context.Respond($"{playerName} not found");
                return;
            }

            ConflictPairModule.RequestSubmit(id, otherId, ConflictPairModule.ConflictType.Player, Context.Player.SteamUserId, out var message);
            Context.Respond(message);
        }

        [Command("conflicts", "Lists your (and your faction's) wars, pending challenges and submissions")]
        [Permission(MyPromoteLevel.None)]
        public void ListConflicts()
        {
            if (!ConflictsEnabled() || !InGame(out var id)) return;
            var lines = ConflictPairModule.DescribeConflicts(id);
            Context.Respond(lines.Count == 0 ? "No wars or pending challenges." : string.Join("\n", lines));
        }

        [Command("admin conflicts", "Lists every war, pending challenge and submission on the server")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminListConflicts()
        {
            if (!ConflictsEnabled()) return;
            var lines = ConflictPairModule.DescribeConflicts(0, all: true);
            Context.Respond(lines.Count == 0 ? "No conflicts." : string.Join("\n", lines));
        }

        [Command("admin clearconflicts", "Removes every conflict and restores peace between the affected factions")]
        [Permission(MyPromoteLevel.Admin)]
        public void AdminClearConflicts()
        {
            if (!ConflictsEnabled()) return;
            Context.Respond($"Removed {ConflictPairModule.ClearAll()} conflict(s).");
        }
    }
}
