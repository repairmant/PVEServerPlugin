using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NLog;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.API.Managers;
using Torch.Mod;
using Torch.Mod.Messages;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace PVEServerPlugin.Modules
{
    /// <summary>
    /// Tracks challenges/wars between players or factions.
    ///
    /// Patch notes by repairmant87 (vs upstream 1.5.105):
    ///  * pairs are matched by the actual (unordered) pair of ids + type instead of by id sum
    ///  * a pending challenge can be found/accepted (upstream InConflict() only ever returned Active pairs)
    ///  * damage/reputation/landing-gear checks resolve player -> faction before looking up faction wars
    ///  * one pair per side-pair (no duplicates); declaring back on a pending challenger accepts it
    ///  * every state change is saved; dead factions/identities are pruned on load
    ///  * all operations return a message for the caller and notify both sides (chat + TorchMod notification)
    /// </summary>
    public class ConflictPairModule
    {
        private static readonly Logger Log = LogManager.GetLogger("PVEPlugin");
        private static readonly object SyncRoot = new object();

        // Kept as a HashSet for source compatibility; always mutate under SyncRoot and call Changed().
        public static HashSet<ConflictPairData> ConflictPairs = new HashSet<ConflictPairData>();

        // Lock-free snapshot of Active pairs, read from the damage handler.
        private static ConflictPairData[] _activeSnapshot = new ConflictPairData[0];

        public class ConflictPairData
        {
            [JsonProperty(Order = 1)]
            public string ChallengerName { get; set; }
            [JsonProperty(Order = 2)]
            public long ChallengerId { get; set; }
            [JsonProperty(Order = 3)]
            public long SubjectName { get; set; }
            [JsonProperty(Order = 4)]
            public long SubjectId { get; set; }
            [JsonProperty(Order = 5)]
            public ulong ChangeRequestId { get; set; }
            [JsonProperty(Order = 6)]
            public ConflictType CurrentConflictType { get; set; }
            [JsonProperty(Order = 7)]
            public ConflictState CurrentConflictState { get; set; }
            /// <summary>Side (player or faction id) that created the current pending state (challenge or submission).</summary>
            [JsonProperty(Order = 8)]
            public long PendingFromId { get; set; }

            public bool Matches(long a, long b, ConflictType type)
            {
                return CurrentConflictType == type &&
                       ((ChallengerId == a && SubjectId == b) || (ChallengerId == b && SubjectId == a));
            }

            public bool Involves(long id) => ChallengerId == id || SubjectId == id;

            public long Other(long id) => ChallengerId == id ? SubjectId : ChallengerId;
        }

        public enum ConflictState
        {
            PendingSubmission,
            PendingConflict,
            Active
        }

        public enum ConflictType
        {
            Player,
            Faction
        }

        public enum ChallengeResult
        {
            Invalid,
            Created,
            AlreadyPending,
            BecameActive,
            AlreadyActive,
            SubmissionPending
        }

        #region Persistence

        public static void LoadConflictData(string path)
        {
            lock (SyncRoot)
            {
                var loaded = new List<ConflictPairData>();
                try
                {
                    var data = File.Exists(path) ? File.ReadAllText(path) : "";
                    if (!string.IsNullOrWhiteSpace(data))
                        loaded = JsonConvert.DeserializeObject<List<ConflictPairData>>(data) ?? new List<ConflictPairData>();
                }
                catch (Exception e)
                {
                    Log.Error(e, $"Failed to read conflict data at {path}; starting with no conflicts (file left untouched)");
                    ConflictPairs = new HashSet<ConflictPairData>();
                    Changed(save: false);
                    return;
                }

                var cleaned = new List<ConflictPairData>();
                var dropped = 0;
                foreach (var pair in loaded)
                {
                    if (pair == null || pair.ChallengerId == 0 || pair.SubjectId == 0 || pair.ChallengerId == pair.SubjectId ||
                        !SideExists(pair.ChallengerId, pair.CurrentConflictType) || !SideExists(pair.SubjectId, pair.CurrentConflictType))
                    {
                        dropped++;
                        continue;
                    }

                    if (pair.PendingFromId == 0 && pair.CurrentConflictState == ConflictState.PendingConflict)
                        pair.PendingFromId = pair.ChallengerId;

                    var existing = cleaned.FirstOrDefault(x => x.Matches(pair.ChallengerId, pair.SubjectId, pair.CurrentConflictType));
                    if (existing == null)
                    {
                        cleaned.Add(pair);
                        continue;
                    }

                    dropped++;
                    if (Rank(pair.CurrentConflictState) > Rank(existing.CurrentConflictState))
                    {
                        cleaned.Remove(existing);
                        cleaned.Add(pair);
                    }
                }

                ConflictPairs = new HashSet<ConflictPairData>(cleaned);
                if (dropped > 0)
                {
                    try
                    {
                        File.Copy(path, path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, "Could not back up conflict data before cleanup");
                    }
                    Log.Warn($"Conflict data cleanup: removed {dropped} duplicate/stale entries, {cleaned.Count} kept");
                    Changed(save: true);
                }
                else
                {
                    Changed(save: false);
                }

                Log.Info($"Loaded {cleaned.Count} conflicts ({cleaned.Count(x => x.CurrentConflictState == ConflictState.Active)} active)");
            }
        }

        private static int Rank(ConflictState state)
        {
            switch (state)
            {
                case ConflictState.Active: return 3;
                case ConflictState.PendingSubmission: return 2;
                default: return 1;
            }
        }

        private static bool SideExists(long id, ConflictType type)
        {
            return type == ConflictType.Faction
                ? MySession.Static.Factions.TryGetFactionById(id) != null
                : MySession.Static.Players.TryGetIdentity(id) != null;
        }

        /// <summary>Call under SyncRoot after any mutation.</summary>
        private static void Changed(bool save = true)
        {
            _activeSnapshot = ConflictPairs.Where(x => x.CurrentConflictState == ConflictState.Active).ToArray();
            if (save) SaveConflictData();
        }

        private static void SaveConflictData()
        {
            var path = Core.Instance?.ConflictDataPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(ConflictPairs, Formatting.Indented));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to save conflict data");
            }
        }

        /// <summary>Drops every conflict that involves a faction that no longer exists.</summary>
        public static void OnFactionRemoved(long factionId)
        {
            lock (SyncRoot)
            {
                var removed = ConflictPairs.RemoveWhere(x => x.CurrentConflictType == ConflictType.Faction && x.Involves(factionId));
                if (removed == 0) return;
                Log.Info($"Faction {factionId} removed; dropped {removed} conflicts");
                Changed();
            }
        }

        #endregion

        #region Queries

        private static ConflictPairData Find(long a, long b, ConflictType type)
        {
            return ConflictPairs.FirstOrDefault(x => x.Matches(a, b, type));
        }

        /// <summary>True only for an Active conflict between the two sides. foundPairModule is set whenever a pair exists.</summary>
        public static bool InConflict(long challengerId, long subjectId, ConflictType type, out ConflictPairData foundPairModule)
        {
            foundPairModule = null;
            if (!Config.Instance.EnableConflict) return false;
            if (Core.NexusDetected && type == ConflictType.Faction)
                return MySession.Static.Factions.AreFactionsEnemies(challengerId, subjectId);

            lock (SyncRoot)
            {
                foundPairModule = Find(challengerId, subjectId, type);
            }
            return foundPairModule?.CurrentConflictState == ConflictState.Active;
        }

        /// <summary>Faction/faction lookup (kept for compatibility with upstream call sites that pass faction ids).</summary>
        public static bool InConflict(long challengerId, long subjectId, out ConflictPairData foundPairModule)
        {
            return InConflict(challengerId, subjectId, ConflictType.Faction, out foundPairModule);
        }

        /// <summary>
        /// Are two player identities at war, either personally or through their factions?
        /// Hot path (damage handler): lock-free.
        /// </summary>
        public static bool AreAtWar(long identityA, long identityB)
        {
            if (!Config.Instance.EnableConflict || identityA == 0 || identityB == 0 || identityA == identityB) return false;

            var factions = MySession.Static.Factions;
            var facA = factions.GetPlayerFaction(identityA);
            var facB = factions.GetPlayerFaction(identityB);

            if (Core.NexusDetected)
            {
                if (facA != null && facB != null) return facA != facB && factions.AreFactionsEnemies(facA.FactionId, facB.FactionId);
                if (facB != null) return factions.IsFactionWithPlayerEnemy(identityA, facB.FactionId);
                if (facA != null) return factions.IsFactionWithPlayerEnemy(identityB, facA.FactionId);
                return false;
            }

            var active = _activeSnapshot;
            if (active.Length == 0) return false;

            foreach (var pair in active)
            {
                if (pair.Matches(identityA, identityB, ConflictType.Player)) return true;
                if (facA != null && facB != null && facA != facB &&
                    pair.Matches(facA.FactionId, facB.FactionId, ConflictType.Faction)) return true;
            }
            return false;
        }

        public static List<string> DescribeConflicts(long identityId, bool all = false)
        {
            var lines = new List<string>();
            var faction = MySession.Static.Factions.GetPlayerFaction(identityId);
            lock (SyncRoot)
            {
                foreach (var pair in ConflictPairs)
                {
                    if (!all)
                    {
                        var mine = pair.CurrentConflictType == ConflictType.Player
                            ? pair.Involves(identityId)
                            : faction != null && pair.Involves(faction.FactionId);
                        if (!mine) continue;
                    }

                    var a = SideName(pair.ChallengerId, pair.CurrentConflictType);
                    var b = SideName(pair.SubjectId, pair.CurrentConflictType);
                    switch (pair.CurrentConflictState)
                    {
                        case ConflictState.Active:
                            lines.Add($"WAR: {a} vs {b}");
                            break;
                        case ConflictState.PendingConflict:
                            lines.Add($"Pending challenge: {SideName(pair.PendingFromId == 0 ? pair.ChallengerId : pair.PendingFromId, pair.CurrentConflictType)} -> {SideName(pair.Other(pair.PendingFromId == 0 ? pair.ChallengerId : pair.PendingFromId), pair.CurrentConflictType)}");
                            break;
                        case ConflictState.PendingSubmission:
                            lines.Add($"Pending submission (ceasefire): {SideName(pair.PendingFromId, pair.CurrentConflictType)} offered to {SideName(pair.Other(pair.PendingFromId), pair.CurrentConflictType)}");
                            break;
                    }
                }
            }
            return lines;
        }

        #endregion

        #region Challenges

        /// <summary>
        /// challenger challenges subject. If subject already has a pending challenge against challenger,
        /// this counts as accepting it and the war starts.
        /// </summary>
        public static ChallengeResult IssueChallenge(long challengerId, long subjectId, ConflictType type, ulong requesterSteamId, out string message)
        {
            message = null;
            if (challengerId == 0 || subjectId == 0 || challengerId == subjectId || !SideExists(challengerId, type) || !SideExists(subjectId, type))
            {
                message = "Invalid challenge target.";
                return ChallengeResult.Invalid;
            }

            var challengerName = SideName(challengerId, type);
            var subjectName = SideName(subjectId, type);

            var becameActive = false;
            lock (SyncRoot)
            {
                var pair = Find(challengerId, subjectId, type);
                if (pair == null)
                {
                    ConflictPairs.Add(new ConflictPairData
                    {
                        ChallengerId = challengerId,
                        ChallengerName = challengerName,
                        SubjectId = subjectId,
                        CurrentConflictType = type,
                        CurrentConflictState = ConflictState.PendingConflict,
                        ChangeRequestId = requesterSteamId,
                        PendingFromId = challengerId
                    });
                    Changed();
                }
                else
                {
                    switch (pair.CurrentConflictState)
                    {
                        case ConflictState.Active:
                            message = $"You are already at war with {subjectName}.";
                            return ChallengeResult.AlreadyActive;
                        case ConflictState.PendingSubmission:
                            message = $"A submission between you and {subjectName} is pending. Resolve it first (!pve accept submission).";
                            return ChallengeResult.SubmissionPending;
                        case ConflictState.PendingConflict when (pair.PendingFromId == 0 ? pair.ChallengerId : pair.PendingFromId) == challengerId:
                            message = $"Your challenge to {subjectName} is still waiting for them to accept.";
                            return ChallengeResult.AlreadyPending;
                        default:
                            // The other side challenged us first: declaring back == accepting.
                            pair.CurrentConflictState = ConflictState.Active;
                            pair.ChangeRequestId = requesterSteamId;
                            pair.PendingFromId = 0;
                            Changed();
                            becameActive = true;
                            break;
                    }
                }
            }

            if (becameActive)
            {
                OnWarStarted(challengerId, subjectId, type);
                message = $"War with {subjectName} has started.";
                return ChallengeResult.BecameActive;
            }

            var acceptHint = type == ConflictType.Faction
                ? $"Your faction leader can accept with !pve accept challenge {challengerName}, or by declaring war on {challengerName}. Decline with !pve decline challenge {challengerName}."
                : $"Accept with !pve accept challenge {challengerName} or decline with !pve decline challenge {challengerName}.";
            Notify(SideMembers(subjectId, type), $"{challengerName} has challenged you to war. {acceptHint}");
            Notify(SideMembers(challengerId, type), $"War challenge sent to {subjectName}. The war starts when they accept.");
            Log.Info($"Challenge: {challengerName} ({challengerId}) -> {subjectName} ({subjectId}) [{type}]");
            message = $"War challenge sent to {subjectName}.";
            return ChallengeResult.Created;
        }

        /// <summary>acceptor accepts a pending challenge issued by challenger.</summary>
        public static bool AcceptChallenge(long acceptorId, long challengerId, ConflictType type, ulong requesterSteamId, out string message)
        {
            lock (SyncRoot)
            {
                var pair = Find(acceptorId, challengerId, type);
                var from = pair == null ? 0 : (pair.PendingFromId == 0 ? pair.ChallengerId : pair.PendingFromId);
                if (pair == null || pair.CurrentConflictState != ConflictState.PendingConflict)
                {
                    message = pair?.CurrentConflictState == ConflictState.Active
                        ? $"You are already at war with {SideName(challengerId, type)}."
                        : $"No pending challenge from {SideName(challengerId, type)}.";
                    return false;
                }
                if (from != challengerId)
                {
                    message = $"That challenge was issued by you; {SideName(challengerId, type)} has to accept it.";
                    return false;
                }

                pair.CurrentConflictState = ConflictState.Active;
                pair.ChangeRequestId = requesterSteamId;
                pair.PendingFromId = 0;
                Changed();
            }

            OnWarStarted(challengerId, acceptorId, type);
            message = $"War with {SideName(challengerId, type)} has started.";
            return true;
        }

        /// <summary>Accepts every pending challenge addressed to this player (and to their faction if they lead it).</summary>
        public static int AcceptAllChallenges(long identityId, ulong requesterSteamId)
        {
            var targets = new List<(long challenger, long acceptor, ConflictType type)>();
            var leadFaction = LeadFaction(identityId);
            lock (SyncRoot)
            {
                foreach (var pair in ConflictPairs.Where(x => x.CurrentConflictState == ConflictState.PendingConflict))
                {
                    var from = pair.PendingFromId == 0 ? pair.ChallengerId : pair.PendingFromId;
                    var to = pair.Other(from);
                    if ((pair.CurrentConflictType == ConflictType.Player && to == identityId) ||
                        (pair.CurrentConflictType == ConflictType.Faction && leadFaction != null && to == leadFaction.FactionId))
                        targets.Add((from, to, pair.CurrentConflictType));
                }
            }

            var count = 0;
            foreach (var (challenger, acceptor, type) in targets)
                if (AcceptChallenge(acceptor, challenger, type, requesterSteamId, out _)) count++;
            return count;
        }

        /// <summary>Removes a pending challenge between the two sides, whichever side issued it.</summary>
        public static bool DeclineChallenge(long sideId, long otherId, ConflictType type, out string message)
        {
            lock (SyncRoot)
            {
                var pair = Find(sideId, otherId, type);
                if (pair == null || pair.CurrentConflictState != ConflictState.PendingConflict)
                {
                    message = $"No pending challenge between you and {SideName(otherId, type)}.";
                    return false;
                }
                ConflictPairs.Remove(pair);
                Changed();
            }

            Notify(SideMembers(otherId, type), $"The war challenge between {SideName(sideId, type)} and {SideName(otherId, type)} was withdrawn/declined.");
            message = $"Challenge with {SideName(otherId, type)} removed.";
            return true;
        }

        #endregion

        #region Submissions

        /// <summary>submitter offers to end an active war with other (ceasefire until the other side accepts).</summary>
        public static bool RequestSubmit(long submitterId, long otherId, ConflictType type, ulong requesterSteamId, out string message)
        {
            lock (SyncRoot)
            {
                var pair = Find(submitterId, otherId, type);
                if (pair == null || pair.CurrentConflictState != ConflictState.Active)
                {
                    message = pair?.CurrentConflictState == ConflictState.PendingSubmission
                        ? "A submission is already pending for this war."
                        : $"You are not at war with {SideName(otherId, type)}.";
                    return false;
                }

                pair.CurrentConflictState = ConflictState.PendingSubmission;
                pair.PendingFromId = submitterId;
                pair.ChangeRequestId = requesterSteamId;
                Changed();
            }

            var submitterName = SideName(submitterId, type);
            Notify(SideLeaders(otherId, type), $"{submitterName} has offered to submit (ceasefire until resolved). Accept with !pve accept submission {submitterName}.");
            message = $"Submission sent to {SideName(otherId, type)}. Damage between you is blocked while it is pending.";
            return true;
        }

        /// <summary>acceptor accepts the submission offered by submitter; the conflict ends.</summary>
        public static bool AcceptSubmission(long acceptorId, long submitterId, ConflictType type, out string message)
        {
            lock (SyncRoot)
            {
                var pair = Find(acceptorId, submitterId, type);
                if (pair == null || pair.CurrentConflictState != ConflictState.PendingSubmission || pair.PendingFromId != submitterId)
                {
                    message = $"No pending submission from {SideName(submitterId, type)}.";
                    return false;
                }
                ConflictPairs.Remove(pair);
                Changed();
            }

            OnWarEnded(acceptorId, submitterId, type);
            message = $"Submission from {SideName(submitterId, type)} accepted. The war is over.";
            return true;
        }

        public static int AcceptAllSubmissions(long identityId)
        {
            var targets = new List<(long acceptor, long submitter, ConflictType type)>();
            var leadFaction = LeadFaction(identityId);
            lock (SyncRoot)
            {
                foreach (var pair in ConflictPairs.Where(x => x.CurrentConflictState == ConflictState.PendingSubmission))
                {
                    var to = pair.Other(pair.PendingFromId);
                    if ((pair.CurrentConflictType == ConflictType.Player && to == identityId) ||
                        (pair.CurrentConflictType == ConflictType.Faction && leadFaction != null && to == leadFaction.FactionId))
                        targets.Add((to, pair.PendingFromId, pair.CurrentConflictType));
                }
            }

            var count = 0;
            foreach (var (acceptor, submitter, type) in targets)
                if (AcceptSubmission(acceptor, submitter, type, out _)) count++;
            return count;
        }

        /// <summary>Admin: removes every conflict and restores peace between the affected factions.</summary>
        public static int ClearAll()
        {
            List<ConflictPairData> removed;
            lock (SyncRoot)
            {
                removed = ConflictPairs.ToList();
                ConflictPairs.Clear();
                Changed();
            }
            foreach (var pair in removed.Where(x => x.CurrentConflictState != ConflictState.PendingConflict))
                OnWarEnded(pair.ChallengerId, pair.SubjectId, pair.CurrentConflictType);
            return removed.Count;
        }

        #endregion

        #region War start/end side effects

        private static void OnWarStarted(long a, long b, ConflictType type)
        {
            var text = $"WAR: {SideName(a, type)} and {SideName(b, type)} are now at war. Grids and players of both sides can damage each other.";
            Notify(SideMembers(a, type).Concat(SideMembers(b, type)), text);
            Log.Info($"War started: {SideName(a, type)} ({a}) vs {SideName(b, type)} ({b}) [{type}]");
            // Mirror the war in the game's faction relation (turrets, HUD). Passes our patch because the pair is Active now.
            if (type == ConflictType.Faction) Core.RequestFactionChange(MyFactionStateChange.DeclareWar, a, b, 0);
            if (Config.Instance.EnableConflict) Core.Instance?.RecheckReputations();
        }

        private static void OnWarEnded(long a, long b, ConflictType type)
        {
            Notify(SideMembers(a, type).Concat(SideMembers(b, type)), $"PEACE: the war between {SideName(a, type)} and {SideName(b, type)} is over.");
            Log.Info($"War ended: {SideName(a, type)} ({a}) vs {SideName(b, type)} ({b}) [{type}]");
            if (type == ConflictType.Faction) Core.RequestFactionChange(MyFactionStateChange.AcceptPeace, a, b, 0);
            if (Config.Instance.EnableConflict) Core.Instance?.RecheckReputations();
        }

        #endregion

        #region Helpers

        /// <summary>The faction this identity leads (leader or founder), or null.</summary>
        public static MyFaction LeadFaction(long identityId)
        {
            var faction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (faction == null) return null;
            return faction.IsLeader(identityId) || faction.IsFounder(identityId) ? faction : null;
        }

        public static string SideName(long id, ConflictType type)
        {
            if (type == ConflictType.Faction)
                return MySession.Static.Factions.TryGetFactionById(id)?.Tag ?? $"faction {id}";
            return MySession.Static.Players.TryGetIdentity(id)?.DisplayName ?? $"player {id}";
        }

        private static IEnumerable<ulong> SideMembers(long id, ConflictType type)
        {
            if (type == ConflictType.Player)
                return new[] { MySession.Static.Players.TryGetSteamId(id) };
            var faction = MySession.Static.Factions.TryGetFactionById(id);
            if (faction == null) return Enumerable.Empty<ulong>();
            return faction.Members.Keys.Select(m => MySession.Static.Players.TryGetSteamId(m)).ToList();
        }

        private static IEnumerable<ulong> SideLeaders(long id, ConflictType type)
        {
            if (type == ConflictType.Player)
                return new[] { MySession.Static.Players.TryGetSteamId(id) };
            var faction = MySession.Static.Factions.TryGetFactionById(id);
            if (faction == null) return Enumerable.Empty<ulong>();
            return faction.Members.Where(m => m.Value.IsLeader || m.Value.IsFounder)
                .Select(m => MySession.Static.Players.TryGetSteamId(m.Key)).ToList();
        }

        /// <summary>Sends to online players only: chat message (always visible) + HUD notification (needs TorchMod on the client).</summary>
        public static void Notify(IEnumerable<ulong> steamIds, string message)
        {
            IChatManagerServer chat = null;
            try
            {
                chat = Core.Instance?.Torch?.CurrentSession?.Managers?.GetManager<IChatManagerServer>();
            }
            catch
            {
                // ignored: fall back to notifications only
            }

            foreach (var steamId in steamIds.Where(x => x != 0).Distinct())
            {
                if (!MySession.Static.Players.TryGetPlayerBySteamId(steamId, out _)) continue;
                try
                {
                    chat?.SendMessageAsOther("PVE", message, Color.Orange, steamId);
                    ModCommunication.SendMessageTo(new NotificationMessage(message, 10000, MyFontEnum.White), steamId);
                }
                catch (Exception e)
                {
                    Log.Warn(e, $"Failed to notify {steamId}");
                }
            }
        }

        #endregion
    }
}
