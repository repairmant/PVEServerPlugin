using System;
using System.Linq;
using Sandbox.Game.World;

namespace PVEServerPlugin.Utilities
{
    public static class IdentityUtility
    {

        /// <summary>
        /// Compares display names ignoring case and the platform icon glyphs (Unicode private use area) that
        /// crossplay players have in front of their names.
        /// </summary>
        public static bool NameEquals(string displayName, string name)
        {
            return !string.IsNullOrEmpty(displayName) && !string.IsNullOrEmpty(name) &&
                   string.Equals(StripIcons(displayName), StripIcons(name), StringComparison.OrdinalIgnoreCase);
        }

        private static string StripIcons(string s)
        {
            return new string(s.Where(c => c < '' || c > '').ToArray()).Trim();
        }

        /// <summary>Online player by identity id, steam id or display name. 0 if not found.</summary>
        public static long GetPlayerId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            if (long.TryParse(name, out var id) && MySession.Static.Players.TryGetIdentity(id) != null) return id;

            ulong.TryParse(name, out var steamId);

            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (string.IsNullOrEmpty(player.DisplayName)) continue;

                if (steamId > 0 && player.Id.SteamId == steamId)
                    return player.Identity.IdentityId;

                if (NameEquals(player.DisplayName, name))
                    return player.Identity.IdentityId;
            }
            return 0;
        }

        /// <summary>Like GetPlayerId, but also finds offline (non-NPC) players by display name.</summary>
        public static long GetAnyPlayerId(string name)
        {
            var id = GetPlayerId(name);
            if (id != 0) return id;

            foreach (var identity in MySession.Static.Players.GetAllIdentities())
            {
                if (MySession.Static.Players.IdentityIsNpc(identity.IdentityId)) continue;
                if (NameEquals(identity.DisplayName, name)) return identity.IdentityId;
            }
            return 0;
        }

        public static long GetFactionId(string nameOrTag)
        {
            long id = 0;
            foreach (var (factionId, faction) in MySession.Static.Factions)
            {
                if (faction.IsEveryoneNpc() || (!faction.Name.Equals(nameOrTag,StringComparison.OrdinalIgnoreCase) && !faction.Tag.Equals(nameOrTag,StringComparison.OrdinalIgnoreCase)))continue;

                id = factionId;
                break;

            }

            return id;
        }


        public static string GetIdentityName(long id)
        {
            string name = "Unknown";

            //var factionName = MySession.Static.Factions.getpla

            return name;
        }



    }
}