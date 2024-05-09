/* LICENSE
 * Copyright (C) 2022-2024 evict
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

#region > Singleton
namespace Oxide.Plugins
{
    using System;
    using System.IO;
    using Oxide.Core;
    using Oxide.Core.Plugins;
    using Oxide.Core.Configuration;
    using Oxide.Core.Libraries.Covalence;
    using UnityEngine;
    using System.Collections.Generic;
    using System.Linq;
    using Network;


    [Info("Imperium", "chucklenugget/evict", "2.2.8")]
    [Description("Land Claims for Rust")]
    public partial class Imperium : RustPlugin
    {
        //Optional Dependencies
        [PluginReference]
        private Plugin BetterChat, Clans, RaidableBases;
        private List<HookDeferral> HookDeferralRegistry = new List<HookDeferral>();
        public class HookDeferral
        {
            public string hookName;
            public Plugin plugin;

            public HookDeferral(string HookName, Plugin Plugin)
            {
                hookName = HookName;
                plugin = Plugin;
            }
        }

        //Hook Deferrals
        [PluginReference]
        private Plugin NpcSpawn, AirEvent;

        private void InitDeferList()
        {
            //RegisterHookDeferral("OnEntityTakeDamage", NpcSpawn);
            //RegisterHookDeferral("OnEntityTakeDamage", AirEvent);
        }

        private static Imperium Instance;
        private bool Ready;

        public static string dataDirectory = $"file://{Interface.Oxide.DataDirectory}{Path.DirectorySeparatorChar}ImperiumImages{Path.DirectorySeparatorChar}";
        private DynamicConfigFile AreasFile;
        private DynamicConfigFile FactionsFile;
        private DynamicConfigFile PinsFile;
        private DynamicConfigFile WarsFile;
        private GameObject GO;
        private ImperiumOptions Options;
        private Timer UpkeepCollectionTimer;
        private AreaManager Areas;
        private FactionManager Factions;
        private HudManager Hud;
        private PinManager Pins;
        private UserManager Users;
        private WarManager Wars;
        private ZoneManager Zones;
        private RecruitManager Recruits;

        private void Init()
        {
            AreasFile = GetDataFile("areas");
            FactionsFile = GetDataFile("factions");
            PinsFile = GetDataFile("pins");
            WarsFile = GetDataFile("wars");
        }

        private void RegisterHookDeferral(string hook, Plugin plugin)
        {
            if (plugin == null)
                return;
            HookDeferralRegistry.Add(new HookDeferral(hook, plugin));
        }

        private object GetExternalHookResult(string hook, params object[] args)
        {
            if (HookDeferralRegistry.Count == 0)
                return null;
            List<HookDeferral> filtered = HookDeferralRegistry.FindAll(r => r.hookName == hook && r.plugin != null);
            if (filtered.Count == 0)
                return null;
            object result = null;
            foreach (HookDeferral def in filtered)
            {
                if (def.plugin == null)
                    continue;
                result = def.plugin.Call(hook, args);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void Loaded()
        {
            InitLang();
            InitDeferList();
            Permission.RegisterAll(this);
            try
            {
                Options = Config.ReadObject<ImperiumOptions>();
            }
            catch (Exception ex)
            {
                PrintError($"Error while loading configuration: {ex.ToString()}");
            }


            Puts("Area claims are " + (Options.Claims.Enabled ? "enabled" : "disabled"));
            Puts("Taxation is " + (Options.Taxes.Enabled ? "enabled" : "disabled"));
            Puts("Badlands are " + (Options.Badlands.Enabled ? "enabled" : "disabled"));
            Puts("Map pins are " + (Options.Map.PinsEnabled ? "enabled" : "disabled"));
            Puts("War is " + (Options.War.Enabled ? "enabled" : "disabled"));
            Puts("Decay reduction is " + (Options.Decay.Enabled ? "enabled" : "disabled"));
            Puts("Claim upkeep is " + (Options.Upkeep.Enabled ? "enabled" : "disabled"));
            Puts("Zones are " + (Options.Zones.Enabled ? "enabled" : "disabled"));

            if (Options.Upgrading.Enabled)
            {
                PrintWarning("Land upgrading is not available in this Imperium version yet! Disabling it");
                Options.Upgrading.Enabled = false;
            }

            if (Options.Recruiting.Enabled)
            {
                PrintWarning("Recruiting is not available in this Imperium version yet! Disabling it");
                Options.Recruiting.Enabled = false;
            }

            if (BetterChat != null)
            {
                Puts("Using " + BetterChat.Name + " by " + BetterChat.Author);
                Interface.CallHook("API_RegisterThirdPartyTitle", this, new Func<IPlayer, string>(BetterChat_FormattedFactionTag));
            }
            Instance = this;


            //Puts("Recruiting is " + (Options.Recruiting.Enabled ? "enabled" : "disabled"));

            // If the map has already been initialized, we can set up now; otherwise,
            // we need to wait until the savefile has been loaded.
            if (TerrainMeta.Size.x > 0) Setup();
        }

        private void OnServerInitialized(bool initial)
        {
            if (initial)
                Setup();
        }

        private void Setup()
        {
            GO = new GameObject();

            Areas = new AreaManager();
            Factions = new FactionManager();
            Hud = new HudManager();
            Pins = new PinManager();
            Users = new UserManager();
            Wars = new WarManager();
            Zones = new ZoneManager();
            Recruits = new RecruitManager();

            Factions.Init(TryLoad<FactionInfo>(FactionsFile));
            Areas.Init(TryLoad<AreaInfo>(AreasFile));
            Pins.Init(TryLoad<PinInfo>(PinsFile));
            Users.Init();
            Wars.Init(TryLoad<WarInfo>(WarsFile));
            Zones.Init();
            Hud.Init();

            Hud.GenerateMapOverlayImage();

            if (Options.Factions.OverrideInGameTeamSystem)
            {
                RelationshipManager.maxTeamSize = 128;
                RelationshipManager.maxTeamSize_Internal = 128;
            }

            if (Instance.Options.Factions.UseClansPlugin)
            {
                Factions.SyncAllWithClans();
            }

            if (Options.Upkeep.Enabled)
                UpkeepCollectionTimer =
                    timer.Every(Options.Upkeep.CheckIntervalMinutes * 60, Upkeep.CollectForAllFactions);


            PrintToChat($"{Title} v{Version} initialized.");
            Ready = true;
        }

        private void Unload()
        {
            SaveData();
            Hud.Destroy();
            Zones.Destroy();
            Users.Destroy();
            Wars.Destroy();
            Pins.Destroy();
            Areas.Destroy();
            Factions.Destroy();

            if (UpkeepCollectionTimer != null && !UpkeepCollectionTimer.Destroyed)
                UpkeepCollectionTimer.Destroy();

            if (GO != null)
                UnityEngine.Object.Destroy(GO);

            Instance = null;
        }

        private void OnServerSave()
        {
            timer.Once(Core.Random.Range(10, 30), SaveData);
        }

        private void SaveData()
        {
            AreasFile.WriteObject(Areas.Serialize());
            FactionsFile.WriteObject(Factions.Serialize());
            PinsFile.WriteObject(Pins.Serialize());
            WarsFile.WriteObject(Wars.Serialize());
        }

        private DynamicConfigFile GetDataFile(string name)
        {
            return Interface.Oxide.DataFileSystem.GetFile(Name + Path.DirectorySeparatorChar + name);
        }

        private IEnumerable<T> TryLoad<T>(DynamicConfigFile file)
        {
            List<T> items;

            try
            {
                items = file.ReadObject<List<T>>();
            }
            catch (Exception ex)
            {
                PrintWarning($"Error reading data from {file.Filename}: ${ex.ToString()}");
                items = new List<T>();
            }

            return items;
        }

        private void Log(string message, params object[] args)
        {
            LogToFile("log", String.Format(message, args), this, true);
        }

        private bool EnsureUserCanChangeFactionClaims(User user, Faction faction)
        {
            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return false;
            }

            if (faction.MemberCount < Options.Claims.MinFactionMembers)
            {
                user.SendChatMessage(nameof(Messages.FactionTooSmallToOwnLand), Options.Claims.MinFactionMembers);
                return false;
            }

            return true;
        }

        private bool EnsureFactionCanClaimArea(User user, Faction faction, Area area)
        {
            if (area.Type == AreaType.Badlands)
            {
                user.SendChatMessage(nameof(Messages.AreaIsBadlands), area.Id);
                return false;
            }

            if (faction.MemberCount < Instance.Options.Claims.MinFactionMembers)
            {
                user.SendChatMessage(nameof(Messages.FactionTooSmallToOwnLand), Instance.Options.Claims.MinFactionMembers);
                return false;
            }

            Area[] claimedAreas = Areas.GetAllClaimedByFaction(faction);

            if (Instance.Options.Claims.RequireContiguousClaims && !area.IsClaimed && claimedAreas.Length > 0)
            {
                int contiguousClaims = Areas.GetNumberOfContiguousClaimedAreas(area, faction);
                if (contiguousClaims == 0)
                {
                    user.SendChatMessage(nameof(Messages.AreaNotContiguous), area.Id, faction.Id);
                    return false;
                }
            }

            int? maxClaims = Instance.Options.Claims.MaxClaims;
            if (maxClaims != null && claimedAreas.Length >= maxClaims)
            {
                user.SendChatMessage(nameof(Messages.FactionOwnsTooMuchLand), faction.Id, maxClaims);
                return false;
            }

            return true;
        }

        private bool EnsureCupboardCanBeUsedForClaim(User user, BuildingPrivlidge cupboard)
        {
            if (cupboard == null)
            {
                user.SendChatMessage(nameof(Messages.SelectingCupboardFailedInvalidTarget));
                return false;
            }

            if (!cupboard.IsAuthed(user.Player))
            {
                user.SendChatMessage(nameof(Messages.SelectingCupboardFailedNotAuthorized));
                return false;
            }

            return true;
        }

        private bool EnsureLockerCanBeUsedForArmory(User user, Locker locker, Area area)
        {
            if (area == null || area.FactionId != user.Faction.Id)
            {
                user.SendChatMessage(nameof(Messages.AreaNotOwnedByYourFaction));
                return false;
            }
            return true;
        }

        private bool EnsureUserAndFactionCanEngageInDiplomacy(User user, Faction faction)
        {
            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return false;
            }

            if (faction.MemberCount < Options.Claims.MinFactionMembers)
            {
                user.SendChatMessage(nameof(Messages.FactionTooSmallToOwnLand));
                return false;
            }

            if (Areas.GetAllClaimedByFaction(faction).Length == 0)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotOwnLand));
                return false;
            }

            return true;
        }

        private bool EnforceCommandCooldown(User user, string command, int cooldownSeconds)
        {
            int secondsRemaining = user.GetSecondsLeftOnCooldown(command);

            if (secondsRemaining > 0)
            {
                user.SendChatMessage(nameof(Messages.CommandIsOnCooldown), secondsRemaining);
                return false;
            }

            user.SetCooldownExpiration(command, DateTime.UtcNow.AddSeconds(cooldownSeconds));
            return true;
        }

        private bool TryCollectFromStacks(ItemDefinition itemDef, IEnumerable<Item> stacks, int amount)
        {
            if (stacks.Sum(item => item.amount) < amount)
                return false;

            int amountRemaining = amount;
            var dirtyContainers = new HashSet<ItemContainer>();

            foreach (Item stack in stacks)
            {
                var amountToTake = Math.Min(stack.amount, amountRemaining);

                stack.amount -= amountToTake;
                amountRemaining -= amountToTake;

                dirtyContainers.Add(stack.GetRootContainer());

                if (stack.amount == 0)
                    stack.RemoveFromContainer();

                if (amountRemaining == 0)
                    break;
            }

            foreach (ItemContainer container in dirtyContainers)
                container.MarkDirty();

            return true;
        }
    }

}
namespace Oxide.Plugins
{
    using Oxide.Core.Plugins;
    using Oxide.Core.Libraries.Covalence;
    public partial class Imperium
    {
        private string BetterChat_FormattedFactionTag(IPlayer player)
        {
            if (Clans)
                return null;
            Faction faction = Factions.GetByMember(player.Id);
            if (faction == null)
                return string.Empty;
            FactionColorPicker colorPicker = new FactionColorPicker();
            return "[" + colorPicker.GetHexColorForFaction(faction.Id) + "][" + faction.Id + "][/#]";
        }

    }
}

#endregion

#region > Console To Chat

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ConsoleCommand("imperium.panel.close")]
        private void ccmdImperiumPanelClose(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null)
                return;
            User user = player.GetComponent<User>();
            if (user == null)
                return;
            user.Panel.Close();
        }
    }
}

namespace Oxide.Plugins
{
    using UnityEngine;
    public partial class Imperium
    {
        [ConsoleCommand("imperium.panel.opentab")]
        private void ccmdImperiumPanelOpenTab(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null)
                return;
            User user = player.GetComponent<User>();
            if (user == null)
                return;
            user.Panel.OpenTab(arg.Args[0]);
        }
    }
}

namespace Oxide.Plugins
{
    using UnityEngine;
    public partial class Imperium
    {
        [ConsoleCommand("imperium.panel.opencmd")]
        private void ccmdImperiumPanelOpenCmd(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null)
                return;
            User user = player.GetComponent<User>();
            if (user == null)
                return;
            user.Panel.OpenCommand(arg.Args[0]);
        }
    }
}

namespace Oxide.Plugins
{
    using UnityEngine;
    using System;
    using System.Text.RegularExpressions;
    public partial class Imperium
    {
        [ConsoleCommand("imperium.panel.run")]
        private void ccmdImperiumPanelRun(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (player == null)
                return;
            User user = player.GetComponent<User>();
            if (user == null)
                return;
            string chatCommand = user.Panel.GetFullConsoleCommand();
            Regex.Replace(chatCommand, @"[\""]", "\\\"", RegexOptions.None);
            player.SendConsoleCommand("chat.say " + chatCommand);
            if (Convert.ToBoolean(arg.Args[0]))
            {
                user.Panel.Close();
            }
            else
            {
                user.Panel.ClearCurrentCommand();
                user.Panel.Refresh();
            }

        }
    }
}
namespace Oxide.Plugins
{
    using System;
    using UnityEngine;
    public partial class Imperium
    {
        [ConsoleCommand("imperium.panel.setarg")]
        private void ccmdImperiumPanelSetArg(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection.player as BasePlayer;
            if (!player)
                return;
            User user = player.GetComponent<User>();
            if (!user)
                return;
            if (arg.Args.Length < 3)
                return;
            string fullArg = "";
            for (int i = 2; i < arg.Args.Length; i++)
            {
                fullArg = fullArg + arg.Args[i];
                if (i != arg.Args.Length - 1)
                    fullArg = fullArg + " ";
            }
            user.Panel.SetArg(Convert.ToInt32(arg.Args[0]), fullArg, Convert.ToBoolean(arg.Args[1]));
        }
    }
}
#endregion

#region > Chat Commands
#region commons
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ChatCommand("cancel")]
        private void OnCancelCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);

            if (user.CurrentInteraction == null)
            {
                user.SendChatMessage(nameof(Messages.NoInteractionInProgress));
                return;
            }

            user.SendChatMessage(nameof(Messages.InteractionCanceled));
            user.CancelInteraction();
            
        }

    }
}

namespace Oxide.Plugins
{
    using System.Text;

    public partial class Imperium
    {
        [ChatCommand("help")]
        private void OnHelpCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            var sb = new StringBuilder();

            sb.AppendLine($"<size=18>Welcome to {ConVar.Server.hostname}!</size>");
            sb.AppendLine($"Powered by {Name} v{Version} by <color=#ffd479>chucklenugget</color> and <color=#ffd479>evict</color>");
            sb.AppendLine(
                "Do <color=#ffd479>/i</color> to open Imperium UI. You can also do <color=#ffd479>bind i chat.say /i</color> in F1 console to easily toggle Imperium UI");
            sb.AppendLine();

            sb.Append(
                "The following commands are available. To learn more about each command, do <color=#ffd479>/command help</color>. ");
            sb.AppendLine("For example, to learn more about how to claim land, do <color=#ffd479>/claim help</color>.");
            sb.AppendLine();

            sb.AppendLine("<color=#ffd479>/faction</color> Create or join a faction");
            sb.AppendLine("<color=#ffd479>/claim</color> Claim areas of land");

            if (Options.Taxes.Enabled)
                sb.AppendLine("<color=#ffd479>/tax</color> Manage taxation of your land");

            if (Options.Map.PinsEnabled)
                sb.AppendLine("<color=#ffd479>/pin</color> Add pins (points of interest) to the map");

            if (Options.War.Enabled)
                sb.AppendLine("<color=#ffd479>/war</color> See active wars, declare war, or offer peace");

            if (Options.Badlands.Enabled)
            {
                if (user.HasPermission(Permission.AdminBadlands))
                    sb.AppendLine("<color=#ffd479>/badlands</color> Find or change badlands areas");
                else
                    sb.AppendLine("<color=#ffd479>/badlands</color> Find badlands (PVP) areas");
            }

            user.SendChatMessage(sb);
        }
    }
}
#endregion
#region /imperium
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ChatCommand("i")]
        private void OnImperiumCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            user.Panel.Toggle();
        }
    }
}
#endregion
#region /pvp
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ChatCommand("pvp")]
        private void OnPvpCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);

            if (!Options.Pvp.EnablePvpCommand)
            {
                user.SendChatMessage(nameof(Messages.PvpModeDisabled));
                return;
            }

            if (!EnforceCommandCooldown(user, "pvp", Options.Pvp.CommandCooldownSeconds))
                return;

            if (user.IsInPvpMode)
            {
                user.IsInPvpMode = false;
                user.SendChatMessage(nameof(Messages.ExitedPvpMode));
                Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            }
            else
            {
                user.IsInPvpMode = true;
                user.SendChatMessage(nameof(Messages.EnteredPvpMode));
                Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            }

            user.Hud.Refresh();
        }
    }
}
#endregion
#region /badlands
namespace Oxide.Plugins
{
    using System.Linq;

    public partial class Imperium
    {
        [ChatCommand("badlands")]
        private void OnBadlandsCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (!Options.Badlands.Enabled)
            {
                user.SendChatMessage(nameof(Messages.BadlandsDisabled));
                return;
            }

            if (args.Length == 0)
            {
                var areas = Areas.GetAllByType(AreaType.Badlands).Select(a => a.Id);
                user.SendChatMessage(nameof(Messages.BadlandsList), Util.Format(areas), Options.Taxes.BadlandsGatherBonus);
                return;
            }

            if (!user.HasPermission(Permission.AdminBadlands))
            {
                user.SendChatMessage(nameof(Messages.NoPermission));
                return;
            }

            var areaIds = args.Skip(1).Select(arg => Util.NormalizeAreaId(arg)).ToArray();

            switch (args[0].ToLower())
            {
                case "add":
                    if (args.Length < 2)
                        user.SendChatMessage(nameof(Messages.Usage), "/badlands add [XY XY XY...]");
                    else
                        OnAddBadlandsCommand(user, areaIds);
                    break;

                case "remove":
                    if (args.Length < 2)
                        user.SendChatMessage(nameof(Messages.Usage), "/badlands remove [XY XY XY...]");
                    else
                        OnRemoveBadlandsCommand(user, areaIds);
                    break;

                case "set":
                    if (args.Length < 2)
                        user.SendChatMessage(nameof(Messages.Usage), "/badlands set [XY XY XY...]");
                    else
                        OnSetBadlandsCommand(user, areaIds);
                    break;

                case "clear":
                    if (args.Length != 1)
                        user.SendChatMessage(nameof(Messages.Usage), "/badlands clear");
                    else
                        OnSetBadlandsCommand(user, new string[0]);
                    break;

                default:
                    OnBadlandsHelpCommand(user);
                    break;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System.Collections.Generic;
    using System.Linq;

    public partial class Imperium
    {
        private void OnAddBadlandsCommand(User user, string[] args)
        {
            var areas = new List<Area>();

            foreach (string arg in args)
            {
                Area area = Areas.Get(Util.NormalizeAreaId(arg));

                if (area == null)
                {
                    user.SendChatMessage(nameof(Messages.UnknownArea), arg);
                    return;
                }

                if (area.Type != AreaType.Wilderness)
                {
                    user.SendChatMessage(nameof(Messages.AreaNotWilderness), area.Id);
                    return;
                }

                areas.Add(area);
            }

            Areas.AddBadlands(areas);
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            user.SendChatMessage(nameof(Messages.BadlandsSet), Util.Format(Areas.GetAllByType(AreaType.Badlands)));
            Log($"{Util.Format(user)} added {Util.Format(areas)} to badlands");
        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;

    public partial class Imperium
    {
        private User user;

        private void OnBadlandsHelpCommand(User user)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Available commands:");
            sb.AppendLine("  <color=#ffd479>/badlands add XY [XY XY...]</color>: Add area(s) to the badlands");
            sb.AppendLine("  <color=#ffd479>/badlands remove XY [XY XY...]</color>: Remove area(s) from the badlands");
            sb.AppendLine("  <color=#ffd479>/badlands set XY [XY XY...]</color>: Set the badlands to a list of areas");
            sb.AppendLine("  <color=#ffd479>/badlands clear</color>: Remove all areas from the badlands");
            sb.AppendLine("  <color=#ffd479>/badlands help</color>: Prints this message");

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System.Collections.Generic;
    using System.Linq;

    public partial class Imperium
    {
        private void OnRemoveBadlandsCommand(User user, string[] args)
        {
            var areas = new List<Area>();

            foreach (string arg in args)
            {
                Area area = Areas.Get(Util.NormalizeAreaId(arg));

                if (area == null)
                {
                    user.SendChatMessage(nameof(Messages.UnknownArea), arg);
                    return;
                }

                if (area.Type != AreaType.Badlands)
                {
                    user.SendChatMessage(nameof(Messages.AreaNotBadlands), area.Id);
                    return;
                }

                areas.Add(area);
            }

            Areas.Unclaim(areas);
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            user.SendChatMessage(nameof(Messages.BadlandsSet), Util.Format(Areas.GetAllByType(AreaType.Badlands)));
            Log($"{Util.Format(user)} removed {Util.Format(areas)} from badlands");
        }
    }
}

namespace Oxide.Plugins
{
    using System.Collections.Generic;
    using System.Linq;

    public partial class Imperium
    {
        private void OnSetBadlandsCommand(User user, string[] args)
        {
            var areas = new List<Area>();

            foreach (string arg in args)
            {
                Area area = Areas.Get(Util.NormalizeAreaId(arg));

                if (area == null)
                {
                    user.SendChatMessage(nameof(Messages.UnknownArea), arg);
                    return;
                }

                if (area.Type != AreaType.Wilderness)
                {
                    user.SendChatMessage(nameof(Messages.AreaNotWilderness), area.Id);
                    return;
                }

                areas.Add(area);
            }

            Areas.Unclaim(Areas.GetAllByType(AreaType.Badlands));
            Areas.AddBadlands(areas);
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            user.SendChatMessage(nameof(Messages.BadlandsSet), Util.Format(Areas.GetAllByType(AreaType.Badlands)));
            Log($"{Util.Format(user)} set badlands to {Util.Format(areas)}");
        }
    }
}
#endregion
#region /claim
namespace Oxide.Plugins
{
    using System.Linq;

    public partial class Imperium
    {
        [ChatCommand("claim")]
        private void OnClaimCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (!Options.Claims.Enabled)
            {
                user.SendChatMessage(nameof(Messages.AreaClaimsDisabled));
                return;
            }

            if (args.Length == 0)
            {
                OnClaimAddCommand(user);
                return;
            }

            var restArguments = args.Skip(1).ToArray();

            switch (args[0].ToLower())
            {
                case "add":
                    OnClaimAddCommand(user);
                    break;
                case "remove":
                    OnClaimRemoveCommand(user);
                    break;
                case "hq":
                    OnClaimHeadquartersCommand(user);
                    break;
                case "rename":
                    OnClaimRenameCommand(user, restArguments);
                    break;
                case "give":
                    OnClaimGiveCommand(user, restArguments);
                    break;
                case "cost":
                    OnClaimCostCommand(user, restArguments);
                    break;
                case "upkeep":
                    OnClaimUpkeepCommand(user);
                    break;
                case "show":
                    OnClaimShowCommand(user, restArguments);
                    break;
                case "list":
                    OnClaimListCommand(user, restArguments);
                    break;
                case "assign":
                    OnClaimAssignCommand(user, restArguments);
                    break;
                case "delete":
                    OnClaimDeleteCommand(user, restArguments);
                    break;
                case "info":
                case "upgrade":
                default:
                    OnClaimHelpCommand(user);
                    break;
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnClaimAddCommand(User user)
        {
            Faction faction = Factions.GetByMember(user);

            if (!EnsureUserCanChangeFactionClaims(user, faction))
                return;

            user.SendChatMessage(nameof(Messages.SelectClaimCupboardToAdd));
            user.BeginInteraction(new AddingClaimInteraction(faction));
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnClaimAssignCommand(User user, string[] args)
        {
            if (!user.HasPermission(Permission.AdminClaims))
            {
                user.SendChatMessage(nameof(Messages.NoPermission));
                return;
            }

            if (args.Length == 0)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/claim assign FACTION");
                return;
            }

            string factionId = Util.NormalizeFactionId(args[0]);
            Faction faction = Factions.Get(factionId);

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), factionId);
                return;
            }

            user.SendChatMessage(nameof(Messages.SelectClaimCupboardToAssign));
            user.BeginInteraction(new AssigningClaimInteraction(faction));
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnClaimCostCommand(User user, string[] args)
        {
            Faction faction = Factions.GetByMember(user);

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            if (faction.MemberCount < Options.Claims.MinFactionMembers)
            {
                user.SendChatMessage(nameof(Messages.FactionTooSmallToOwnLand), Options.Claims.MinFactionMembers);
                return;
            }

            if (args.Length > 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/claim cost [XY]");
                return;
            }

            Area area;
            if (args.Length == 0)
                area = user.CurrentArea;
            else
                area = Areas.Get(Util.NormalizeAreaId(args[0]));

            if (area == null)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/claim cost [XY]");
                return;
            }

            if (area.Type == AreaType.Badlands)
            {
                user.SendChatMessage(nameof(Messages.AreaIsBadlands), area.Id);
                return;
            }
            else if (area.Type != AreaType.Wilderness)
            {
                user.SendChatMessage(nameof(Messages.CannotClaimAreaAlreadyClaimed), area.Id, area.FactionId);
                return;
            }

            int cost = area.GetClaimCost(faction);
            user.SendChatMessage(nameof(Messages.ClaimCost), area.Id, faction.Id, cost);
        }
    }
}

namespace Oxide.Plugins
{
    using System.Collections.Generic;

    public partial class Imperium
    {
        private void OnClaimDeleteCommand(User user, string[] args)
        {
            if (args.Length == 0)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/claim delete XY [XY XY...]");
                return;
            }

            if (!user.HasPermission(Permission.AdminClaims))
            {
                user.SendChatMessage(nameof(Messages.NoPermission));
                return;
            }

            var areas = new List<Area>();
            foreach (string arg in args)
            {
                Area area = Areas.Get(Util.NormalizeAreaId(arg));

                if (area.Type == AreaType.Badlands)
                {
                    user.SendChatMessage(nameof(Messages.AreaIsBadlands), area.Id);
                    return;
                }

                if (area.Type == AreaType.Wilderness)
                {
                    user.SendChatMessage(nameof(Messages.AreaIsWilderness), area.Id);
                    return;
                }

                areas.Add(area);
            }

            foreach (Area area in areas)
            {
                PrintToChat(Messages.AreaClaimDeletedAnnouncement, area.FactionId, area.Id);
                Log($"{Util.Format(user)} deleted {area.FactionId}'s claim on {area.Id}");
            }

            Areas.Unclaim(areas);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnClaimGiveCommand(User user, string[] args)
        {
            if (args.Length == 0)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/claim give FACTION");
                return;
            }

            Faction sourceFaction = Factions.GetByMember(user);

            if (!EnsureUserCanChangeFactionClaims(user, sourceFaction))
                return;

            string factionId = Util.NormalizeFactionId(args[0]);
            Faction targetFaction = Factions.Get(factionId);

            if (targetFaction == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), factionId);
                return;
            }

            user.SendChatMessage(nameof(Messages.SelectClaimCupboardToTransfer));
            user.BeginInteraction(new TransferringClaimInteraction(sourceFaction, targetFaction));
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnClaimHeadquartersCommand(User user)
        {
            Faction faction = Factions.GetByMember(user);

            if (!EnsureUserCanChangeFactionClaims(user, faction))
                return;

            user.SendChatMessage(nameof(Messages.SelectClaimCupboardForHeadquarters));
            user.BeginInteraction(new SelectingHeadquartersInteraction(faction));
        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;

    public partial class Imperium
    {
        private void OnClaimHelpCommand(User user)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Available commands:");
            sb.AppendLine("  <color=#ffd479>/claim</color>: Add a claim for your faction");
            sb.AppendLine("  <color=#ffd479>/claim hq</color>: Select your faction's headquarters");
            sb.AppendLine("  <color=#ffd479>/claim remove</color>: Remove a claim for your faction (no undo!)");
            sb.AppendLine(
                "  <color=#ffd479>/claim give FACTION</color>: Give a claimed area to another faction (no undo!)");
            sb.AppendLine("  <color=#ffd479>/claim rename XY \"NAME\"</color>: Rename an area claimed by your faction");
            sb.AppendLine("  <color=#ffd479>/claim show XY</color>: Show who owns an area");
            sb.AppendLine("  <color=#ffd479>/claim list FACTION</color>: List all areas claimed for a faction");
            sb.AppendLine("  <color=#ffd479>/claim cost [XY]</color>: Show the cost for your faction to claim an area");

            if (!Options.Upkeep.Enabled)
                sb.AppendLine(
                    "  <color=#ffd479>/claim upkeep</color>: Show information about upkeep costs for your faction");

            sb.AppendLine("  <color=#ffd479>/claim help</color>: Prints this message");

            if (user.HasPermission(Permission.AdminClaims))
            {
                sb.AppendLine("Admin commands:");
                sb.AppendLine(
                    "  <color=#ffd479>/claim assign FACTION</color>: Use the hammer to assign a claim to another faction");
                sb.AppendLine(
                    "  <color=#ffd479>/claim delete XY [XY XY XY...]</color>: Remove the claim on the specified areas (no undo!)");
            }

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Linq;
    using System.Text;

    public partial class Imperium
    {
        private void OnClaimListCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/claim list FACTION");
                return;
            }

            string factionId = Util.NormalizeFactionId(args[0]);
            Faction faction = Factions.Get(factionId);

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), factionId);
                return;
            }

            Area[] areas = Areas.GetAllClaimedByFaction(faction);
            Area headquarters = areas.FirstOrDefault(a => a.Type == AreaType.Headquarters);

            var sb = new StringBuilder();

            if (areas.Length == 0)
            {
                sb.AppendFormat(String.Format("<color=#ffd479>[{0}]</color> has no land holdings.", factionId));
            }
            else
            {
                float percentageOfMap = (areas.Length / (float)Areas.Count) * 100;
                sb.AppendLine(String.Format("<color=#ffd479>[{0}] owns {1} tiles ({2:F2}% of the known world)</color>",
                    faction.Id, areas.Length, percentageOfMap));
                sb.AppendLine(String.Format("Headquarters: {0}", (headquarters == null) ? "Unknown" : headquarters.Id));
                sb.AppendLine(String.Format("Areas claimed: {0}", Util.Format(areas)));
            }

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnClaimRemoveCommand(User user)
        {
            Faction faction = Factions.GetByMember(user);

            if (!EnsureUserCanChangeFactionClaims(user, faction))
                return;

            user.SendChatMessage(nameof(Messages.SelectClaimCupboardToRemove));
            user.BeginInteraction(new RemovingClaimInteraction(faction));
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        private void OnClaimRenameCommand(User user, string[] args)
        {
            Faction faction = Factions.GetByMember(user);

            if (!EnsureUserCanChangeFactionClaims(user, faction))
                return;

            if (args.Length != 2)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/claim rename XY \"NAME\"");
                return;
            }

            var areaId = Util.NormalizeAreaId(args[0]);
            var name = Util.NormalizeAreaName(args[1]);

            if (name == null || name.Length < Options.Claims.MinAreaNameLength ||
                name.Length > Options.Claims.MaxAreaNameLength)
            {
                user.SendChatMessage(nameof(Messages.InvalidAreaName), Options.Claims.MinAreaNameLength,
                    Options.Claims.MaxAreaNameLength);
                return;
            }

            Area area = Areas.Get(areaId);

            if (area == null)
            {
                user.SendChatMessage(nameof(Messages.UnknownArea), areaId);
                return;
            }

            if (area.FactionId != faction.Id)
            {
                user.SendChatMessage(nameof(Messages.AreaNotOwnedByYourFaction), area.Id);
                return;
            }
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            user.SendChatMessage(nameof(Messages.AreaRenamed), area.Id, name);
            Log($"{Util.Format(user)} renamed {area.Id} to {name}");

            area.Name = name;
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnClaimShowCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/claim show XY");
                return;
            }

            Area area = Areas.Get(Util.NormalizeAreaId(args[0]));

            switch (area.Type)
            {
                case AreaType.Badlands:
                    user.SendChatMessage(nameof(Messages.AreaIsBadlands), area.Id);
                    return;
                case AreaType.Claimed:
                    user.SendChatMessage(nameof(Messages.AreaIsClaimed), area.Id, area.FactionId);
                    return;
                case AreaType.Headquarters:
                    user.SendChatMessage(nameof(Messages.AreaIsHeadquarters), area.Id, area.FactionId);
                    return;
                default:
                    user.SendChatMessage(nameof(Messages.AreaIsWilderness), area.Id);
                    return;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        private void OnClaimUpkeepCommand(User user)
        {
            if (!Options.Upkeep.Enabled)
            {
                user.SendChatMessage(nameof(Messages.UpkeepDisabled));
                return;
            }

            Faction faction = Factions.GetByMember(user);

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            if (faction.MemberCount < Options.Claims.MinFactionMembers)
            {
                user.SendChatMessage(nameof(Messages.FactionTooSmallToOwnLand), Options.Claims.MinFactionMembers);
                return;
            }

            Area[] areas = Areas.GetAllClaimedByFaction(faction);

            if (areas.Length == 0)
            {
                user.SendChatMessage(nameof(Messages.NoAreasClaimed));
                return;
            }

            int upkeep = faction.GetUpkeepPerPeriod();
            var nextPaymentHours = (int)faction.NextUpkeepPaymentTime.Subtract(DateTime.UtcNow).TotalHours;

            if (nextPaymentHours > 0)
                user.SendChatMessage(nameof(Messages.UpkeepCost), upkeep, areas.Length, faction.Id, nextPaymentHours);
            else
                user.SendChatMessage(nameof(Messages.UpkeepCostOverdue), upkeep, areas.Length, faction.Id, nextPaymentHours);
        }
    }
}
#endregion
#region /faction
namespace Oxide.Plugins
{
    using System.Linq;

    public partial class Imperium
    {
        [ChatCommand("faction")]
        private void OnFactionCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (args.Length == 0)
            {
                OnFactionShowCommand(user);
                return;
            }

            var restArguments = args.Skip(1).ToArray();

            switch (args[0].ToLower())
            {
                case "create":
                    OnFactionCreateCommand(user, restArguments);
                    break;
                case "join":
                    OnFactionJoinCommand(user, restArguments);
                    break;
                case "leave":
                    OnFactionLeaveCommand(user, restArguments);
                    break;
                case "invite":
                    OnFactionInviteCommand(user, restArguments);
                    break;
                case "kick":
                    OnFactionKickCommand(user, restArguments);
                    break;
                case "promote":
                    OnFactionPromoteCommand(user, restArguments);
                    break;
                case "demote":
                    OnFactionDemoteCommand(user, restArguments);
                    break;
                case "disband":
                    OnFactionDisbandCommand(user, restArguments);
                    break;
                case "badlands":
                    OnFactionBadlandsCommand(user, restArguments);
                    break;
                case "help":
                default:
                    OnFactionHelpCommand(user);
                    break;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        [ChatCommand("f")]
        private void OnFactionChatCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            string message = String.Join(" ", args).Trim();

            if (message.Length == 0)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/f MESSAGE...");
                return;
            }

            Faction faction = Factions.GetByMember(user);

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            faction.SendChatMessage(nameof(Messages.FactionChatMessage), user.UserName, message);
            Puts("[FACTION] {0} - {1}: {2}", faction.Id, user.UserName, message);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnFactionCreateCommand(User user, string[] args)
        {
            if (Instance.Options.Factions.UseClansPlugin)
            {
                user.SendChatMessage(nameof(Messages.CannotManageFactionUseClansInstead));
                return;
            }
            if (!user.HasPermission(Permission.ManageFactions))
            {
                user.SendChatMessage(nameof(Messages.NoPermission));
                return;
            }

            if (user.Faction != null)
            {
                user.SendChatMessage(nameof(Messages.AlreadyMemberOfFaction));
                return;
            }

            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction create NAME");
                return;
            }

            string id = Util.RemoveSpecialCharacters(args[0].Replace(" ", ""));

            if (id.Length < Options.Factions.MinFactionNameLength || id.Length > Options.Factions.MaxFactionNameLength)
            {
                user.SendChatMessage(nameof(Messages.InvalidFactionName), Options.Factions.MinFactionNameLength,
                    Options.Factions.MaxFactionNameLength);
                return;
            }

            if (Factions.Exists(id))
            {
                user.SendChatMessage(nameof(Messages.FactionAlreadyExists), id);
                return;
            }

            PrintToChat(Messages.FactionCreatedAnnouncement, id);
            Log($"{Util.Format(user)} created faction {id}");

            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_victory.prefab");
            Faction faction = Factions.Create(id, user);
            user.SetFaction(faction);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnFactionDemoteCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction demote \"PLAYER\"");
                return;
            }

            Faction faction = Factions.GetByMember(user);

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            User member = Users.Find(args[0]);

            if (member == null)
            {
                user.SendChatMessage(nameof(Messages.InvalidUser), args[0]);
                return;
            }

            if (!faction.HasMember(member))
            {
                user.SendChatMessage(nameof(Messages.UserIsNotMemberOfFaction), member.UserName, faction.Id);
                return;
            }

            if (faction.HasOwner(member))
            {
                user.SendChatMessage(nameof(Messages.CannotPromoteOrDemoteOwnerOfFaction), member.UserName, faction.Id);
                return;
            }

            if (!faction.HasManager(member))
            {
                user.SendChatMessage(nameof(Messages.UserIsNotManagerOfFaction), member.UserName, faction.Id);
                return;
            }

            user.SendChatMessage(nameof(Messages.ManagerRemoved), member.UserName, faction.Id);
            Log($"{Util.Format(user)} demoted {Util.Format(member)} in faction {faction.Id}");
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_failed.prefab");
            faction.Demote(member);
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    public partial class Imperium
    {
        private void OnFactionBadlandsCommand(User user, string[] args)
        {
            if (!Instance.Options.Factions.AllowFactionBadlands)
            {
                user.SendChatMessage(nameof(Messages.NoFactionBadlandsAllowed));
                return;
            }
            Faction faction = user.Faction;
            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction badlands confirm");
                return;
            }
            int elapsedSeconds = Instance.Options.Factions.CommandCooldownSeconds;
            int secondsRemaining = 1;
            if (faction.BadlandsCommandUsedTime != null)
            {
                elapsedSeconds = (int)(DateTime.Now - faction.BadlandsCommandUsedTime).Value.TotalSeconds;
            }

            if (elapsedSeconds < Instance.Options.Factions.CommandCooldownSeconds)
            {
                secondsRemaining = Instance.Options.Factions.CommandCooldownSeconds - elapsedSeconds;
                user.SendChatMessage(nameof(Messages.CommandIsOnCooldown), secondsRemaining);
                return;
            }

            if (faction.IsBadlands)
            {
                user.SendChatMessage(nameof(Messages.FactionIsNotBadlands));
                faction.IsBadlands = false;
                faction.BadlandsCommandUsedTime = DateTime.Now;
                Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_victory.prefab");
            }
            else
            {
                user.SendChatMessage(nameof(Messages.FactionIsBadlands));
                faction.IsBadlands = true;
                faction.BadlandsCommandUsedTime = DateTime.Now;
                Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_accept.prefab");
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnFactionDisbandCommand(User user, string[] args)
        {
            if (args.Length != 1 || args[0].ToLowerInvariant() != "forever")
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction disband forever");
                return;
            }
            if (Clans)
            {
                user.SendChatMessage(nameof(Messages.CannotManageFactionUseClansInstead));
                return;
            }
            Faction faction = user.Faction;

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            PrintToChat(Messages.FactionDisbandedAnnouncement, faction.Id);
            Log($"{Util.Format(user)} disbanded faction {faction.Id}");
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_failed.prefab");
            Factions.Disband(faction);
        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;

    public partial class Imperium
    {
        private void OnFactionHelpCommand(User user)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Available commands:");
            sb.AppendLine("  <color=#ffd479>/faction</color>: Show information about your faction");
            sb.AppendLine(
                "  <color=#ffd479>/f MESSAGE...</color>: Send a message to all online members of your faction");

            if (user.HasPermission(Permission.ManageFactions))
                sb.AppendLine("  <color=#ffd479>/faction create</color>: Create a new faction");

            sb.AppendLine("  <color=#ffd479>/faction join FACTION</color>: Join a faction if you have been invited");
            sb.AppendLine("  <color=#ffd479>/faction leave</color>: Leave your current faction");
            sb.AppendLine(
                "  <color=#ffd479>/faction invite \"PLAYER\"</color>: Invite another player to join your faction");
            sb.AppendLine("  <color=#ffd479>/faction kick \"PLAYER\"</color>: Kick a player out of your faction");
            sb.AppendLine("  <color=#ffd479>/faction promote \"PLAYER\"</color>: Promote a faction member to manager");
            sb.AppendLine("  <color=#ffd479>/faction demote \"PLAYER\"</color>: Remove a faction member as manager");
            sb.AppendLine(
                "  <color=#ffd479>/faction disband forever</color>: Disband your faction immediately (no undo!)");
            sb.AppendLine("  <color=#ffd479>/faction help</color>: Prints this message");

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnFactionInviteCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction invite \"PLAYER\"");
                return;
            }
            if (Clans)
            {
                user.SendChatMessage(nameof(Messages.CannotManageFactionUseClansInstead));
                return;
            }
            Faction faction = Factions.GetByMember(user);

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            User member = Users.Find(args[0]);

            if (member == null)
            {
                user.SendChatMessage(nameof(Messages.InvalidUser), args[0]);
                return;
            }

            if (faction.HasMember(member))
            {
                user.SendChatMessage(nameof(Messages.UserIsAlreadyMemberOfFaction), member.UserName, faction.Id);
                return;
            }

            int? maxMembers = Options.Factions.MaxMembers;
            if (maxMembers != null && faction.MemberCount >= maxMembers)
            {
                user.SendChatMessage(nameof(Messages.FactionHasTooManyMembers), faction.Id, faction.MemberCount);
                return;
            }

            member.SendChatMessage(nameof(Messages.InviteReceived), user.UserName, faction.Id);
            user.SendChatMessage(nameof(Messages.InviteAdded), member.UserName, faction.Id);
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab", member.Player);

            Log($"{Util.Format(user)} invited {Util.Format(member)} to faction {faction.Id}");

            faction.AddInvite(member);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnFactionJoinCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction join FACTION");
                return;
            }
            if (Clans)
            {
                user.SendChatMessage(nameof(Messages.CannotManageFactionUseClansInstead));
                return;
            }
            if (user.Faction != null)
            {
                user.SendChatMessage(nameof(Messages.AlreadyMemberOfFaction));
                return;
            }

            Faction faction = Factions.Get(args[0]);

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[0]);
                return;
            }

            if (!faction.HasInvite(user))
            {
                user.SendChatMessage(nameof(Messages.CannotJoinFactionNotInvited), faction.Id);
                return;
            }

            user.SendChatMessage(nameof(Messages.YouJoinedFaction), faction.Id);
            PrintToChat(Messages.FactionMemberJoinedAnnouncement, user.UserName, faction.Id);
            Log($"{Util.Format(user)} joined faction {faction.Id}");
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            faction.AddMember(user);
            user.SetFaction(faction);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnFactionKickCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction kick \"PLAYER\"");
                return;
            }
            if (Clans)
            {
                user.SendChatMessage(nameof(Messages.CannotManageFactionUseClansInstead));
                return;
            }
            Faction faction = user.Faction;

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            User member = Users.Find(args[0]);

            if (member == null)
            {
                user.SendChatMessage(nameof(Messages.InvalidUser), args[0]);
                return;
            }

            if (!faction.HasMember(member))
            {
                user.SendChatMessage(nameof(Messages.UserIsNotMemberOfFaction), member.UserName, faction.Id);
                return;
            }

            if (faction.HasLeader(member))
            {
                user.SendChatMessage(nameof(Messages.CannotKickLeaderOfFaction), member.UserName, faction.Id);
                return;
            }

            user.SendChatMessage(nameof(Messages.MemberRemoved), member.UserName, faction.Id);
            PrintToChat(Messages.FactionMemberLeftAnnouncement, member.UserName, faction.Id);

            Log($"{Util.Format(user)} kicked {Util.Format(member)} from faction {faction.Id}");
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_failed.prefab");
            faction.RemoveMember(member);
            member.SetFaction(null);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnFactionLeaveCommand(User user, string[] args)
        {
            if (args.Length != 0)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction leave");
                return;
            }
            if (Clans)
            {
                user.SendChatMessage(nameof(Messages.CannotManageFactionUseClansInstead));
                return;
            }
            Faction faction = user.Faction;

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            if (faction.MemberCount == 1)
            {
                PrintToChat(Messages.FactionDisbandedAnnouncement, faction.Id);
                Log($"{Util.Format(user)} disbanded faction {faction.Id} by leaving as its only member");
                Factions.Disband(faction);
                return;
            }

            user.SendChatMessage(nameof(Messages.YouLeftFaction), faction.Id);
            PrintToChat(Messages.FactionMemberLeftAnnouncement, user.UserName, faction.Id);

            Log($"{Util.Format(user)} left faction {faction.Id}");
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_failed.prefab");
            faction.RemoveMember(user);
            user.SetFaction(null);
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnFactionPromoteCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/faction promote \"PLAYER\"");
                return;
            }
            Faction faction = Factions.GetByMember(user);

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            User member = Users.Find(args[0]);

            if (member == null)
            {
                user.SendChatMessage(nameof(Messages.InvalidUser), args[0]);
                return;
            }

            if (!faction.HasMember(member))
            {
                user.SendChatMessage(nameof(Messages.UserIsNotMemberOfFaction), member.UserName, faction.Id);
                return;
            }

            if (faction.HasOwner(member))
            {
                user.SendChatMessage(nameof(Messages.CannotPromoteOrDemoteOwnerOfFaction), member.UserName, faction.Id);
                return;
            }

            if (faction.HasManager(member))
            {
                user.SendChatMessage(nameof(Messages.UserIsAlreadyManagerOfFaction), member.UserName, faction.Id);
                return;
            }

            user.SendChatMessage(nameof(Messages.ManagerAdded), member.UserName, faction.Id);
            Log($"{Util.Format(user)} promoted {Util.Format(member)} in faction {faction.Id}");
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_victory.prefab");
            faction.Promote(member);
        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;

    public partial class Imperium
    {
        private void OnFactionShowCommand(User user)
        {
            Faction faction = user.Faction;

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            var sb = new StringBuilder();

            sb.Append("You are ");
            if (faction.HasOwner(user))
                sb.Append("the owner");
            else if (faction.HasManager(user))
                sb.Append("a manager");
            else
                sb.Append("a member");

            sb.AppendLine($" of <color=#ffd479>[{faction.Id}]</color>.");

            User[] activeMembers = faction.GetAllActiveMembers();

            sb.AppendLine(
                $"<color=#ffd479>{faction.MemberCount}</color> member(s), <color=#ffd479>{activeMembers.Length}</color> online:");
            sb.Append("  ");

            foreach (User member in activeMembers)
                sb.Append($"<color=#ffd479>{member.UserName}</color>, ");

            sb.Remove(sb.Length - 2, 2);
            sb.AppendLine();

            if (faction.InviteIds.Count > 0)
            {
                User[] activeInvitedUsers = faction.GetAllActiveInvitedUsers();

                sb.AppendLine(
                    $"<color=#ffd479>{faction.InviteIds.Count}</color> invited player(s), <color=#ffd479>{activeInvitedUsers.Length}</color> online:");
                sb.Append("  ");

                foreach (User invitedUser in activeInvitedUsers)
                    sb.Append($"<color=#ffd479>{invitedUser.UserName}</color>, ");

                sb.Remove(sb.Length - 2, 2);
                sb.AppendLine();
            }

            user.SendChatMessage(sb);
        }
    }
}
#endregion
#region imperium.images.refresh
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ConsoleCommand("imperium.images.refresh")]
        private void OnRefreshImagesConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;
            arg.ReplyWith("Refreshing images...");
            Hud.RefreshAllImages();
        }
    }
}
#endregion
#region /pin
namespace Oxide.Plugins
{
    using System.Linq;

    public partial class Imperium
    {
        [ChatCommand("pin")]
        private void OnPinCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (!Options.Map.PinsEnabled)
            {
                user.SendChatMessage(nameof(Messages.PinsDisabled));
                return;
            }

            ;

            if (args.Length == 0)
            {
                OnPinHelpCommand(user);
                return;
            }

            var restArguments = args.Skip(1).ToArray();

            switch (args[0].ToLower())
            {
                case "add":
                    OnPinAddCommand(user, restArguments);
                    break;
                case "remove":
                    OnPinRemoveCommand(user, restArguments);
                    break;
                case "list":
                    OnPinListCommand(user, restArguments);
                    break;
                case "delete":
                    OnPinDeleteCommand(user, restArguments);
                    break;
                case "help":
                default:
                    OnPinHelpCommand(user);
                    break;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    public partial class Imperium
    {
        private void OnPinAddCommand(User user, string[] args)
        {
            if (args.Length != 2)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/pin add TYPE \"NAME\"");
                return;
            }

            if (user.Faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            Area area = user.CurrentArea;

            if (area == null)
            {
                user.SendChatMessage(nameof(Messages.YouAreInTheGreatUnknown));
                return;
            }

            if (area.FactionId == null || area.FactionId != user.Faction.Id)
            {
                user.SendChatMessage(nameof(Messages.AreaNotOwnedByYourFaction), area.Id);
                return;
            }

            PinType type;
            if (!Util.TryParseEnum(args[0], out type))
            {
                user.SendChatMessage(nameof(Messages.InvalidPinType), args[0]);
                return;
            }

            string name = Util.NormalizePinName(args[1]);
            if (name == null || name.Length < Options.Map.MinPinNameLength ||
                name.Length > Options.Map.MaxPinNameLength)
            {
                user.SendChatMessage(nameof(Messages.InvalidPinName), Options.Map.MinPinNameLength,
                    Options.Map.MaxPinNameLength);
                return;
            }

            Pin existingPin = Pins.Get(name);
            if (existingPin != null)
            {
                user.SendChatMessage(nameof(Messages.CannotCreatePinAlreadyExists), existingPin.Name, existingPin.AreaId);
                return;
            }

            if (Options.Map.PinCost > 0)
            {
                ItemDefinition scrapDef = ItemManager.FindItemDefinition("scrap");
                List<Item> stacks = user.Player.inventory.FindItemsByItemID(scrapDef.itemid);

                if (!Instance.TryCollectFromStacks(scrapDef, stacks, Options.Map.PinCost))
                {
                    user.SendChatMessage(nameof(Messages.CannotCreatePinCannotAfford), Options.Map.PinCost);
                    return;
                }
            }

            Vector3 position = user.Player.transform.position;

            var pin = new Pin(position, area, user, type, name);
            Pins.Add(pin);

            PrintToChat(Messages.PinAddedAnnouncement, user.Faction.Id, name, type.ToString().ToLower(), area.Id);
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        private void OnPinDeleteCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/pin delete \"NAME\"");
                return;
            }

            if (!user.HasPermission(Permission.AdminPins))
            {
                user.SendChatMessage(nameof(Messages.NoPermission));
                return;
            }

            string name = Util.NormalizePinName(args[0]);
            Pin pin = Pins.Get(name);

            if (pin == null)
            {
                user.SendChatMessage(nameof(Messages.UnknownPin), name);
                return;
            }

            Pins.Remove(pin);
            user.SendChatMessage(nameof(Messages.PinRemoved), pin.Name);
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;

    public partial class Imperium
    {
        private void OnPinHelpCommand(User user)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Available commands:");
            sb.AppendLine("  <color=#ffd479>/pin list [TYPE]</color>: List all pins (or all of a certain type)");
            sb.AppendLine("  <color=#ffd479>/pin add TYPE \"NAME\"</color>: Create a pin at your current location");
            sb.AppendLine("  <color=#ffd479>/pin remove \"NAME\"</color>: Remove a pin you created");
            sb.AppendLine("  <color=#ffd479>/pin help</color>: Prints this message");

            if (user.HasPermission(Permission.AdminPins))
            {
                sb.AppendLine("Admin commands:");
                sb.AppendLine("  <color=#ffd479>/pin delete XY</color>: Delete a pin from an area");
            }

            sb.Append("Available pin types: ");
            foreach (string type in Enum.GetNames(typeof(PinType)).OrderBy(str => str.ToLowerInvariant()))
                sb.AppendFormat("<color=#ffd479>{0}</color>, ", type.ToLowerInvariant());
            sb.Remove(sb.Length - 2, 2);
            sb.AppendLine();

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Linq;
    using System.Text;

    public partial class Imperium
    {
        private void OnPinListCommand(User user, string[] args)
        {
            if (args.Length > 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/pin list [TYPE]");
                return;
            }

            Pin[] pins = Pins.GetAll();

            if (args.Length == 1)
            {
                PinType type;
                if (!Util.TryParseEnum(args[0], out type))
                {
                    user.SendChatMessage(nameof(Messages.InvalidPinType), args[0]);
                    return;
                }

                pins = pins.Where(pin => pin.Type == type).ToArray();
            }

            if (pins.Length == 0)
            {
                user.SendChatMessage("There are no matching pins.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(String.Format("There are <color=#ffd479>{0}</color> matching map pins:", pins.Length));
            foreach (Pin pin in pins.OrderBy(pin => pin.GetDistanceFrom(user.Player)))
            {
                int distance = (int)Math.Floor(pin.GetDistanceFrom(user.Player));
                sb.AppendLine(String.Format(
                    "  <color=#ffd479>{0} ({1}):</color> {2} (<color=#ffd479>{3}m</color> away)", pin.Name,
                    pin.Type.ToString().ToLowerInvariant(), pin.AreaId, distance));
            }

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        private void OnPinRemoveCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/pin remove \"NAME\"");
                return;
            }

            if (user.Faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            string name = Util.NormalizePinName(args[0]);
            Pin pin = Pins.Get(name);

            if (pin == null)
            {
                user.SendChatMessage(nameof(Messages.UnknownPin), name);
                return;
            }

            Area area = Areas.Get(pin.AreaId);
            if (area.FactionId != user.Faction.Id)
            {
                user.SendChatMessage(nameof(Messages.CannotRemovePinAreaNotOwnedByYourFaction), pin.Name, pin.AreaId);
                return;
            }

            Pins.Remove(pin);
            user.SendChatMessage(nameof(Messages.PinRemoved), pin.Name);
        }
    }
}
#endregion
#region /tax
namespace Oxide.Plugins
{
    using System.Linq;

    public partial class Imperium
    {
        [ChatCommand("tax")]
        private void OnTaxCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (!Options.Taxes.Enabled)
            {
                user.SendChatMessage(nameof(Messages.TaxationDisabled));
                return;
            }

            ;

            if (args.Length == 0)
            {
                OnTaxHelpCommand(user);
                return;
            }

            var restArguments = args.Skip(1).ToArray();

            switch (args[0].ToLower())
            {
                case "chest":
                    OnTaxChestCommand(user);
                    break;
                case "rate":
                    OnTaxRateCommand(user, restArguments);
                    break;
                case "help":
                default:
                    OnTaxHelpCommand(user);
                    break;
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnTaxChestCommand(User user)
        {
            Faction faction = Factions.GetByMember(user);

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            user.SendChatMessage(nameof(Messages.SelectTaxChest));
            user.BeginInteraction(new SelectingTaxChestInteraction(faction));
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        private void OnTaxRateCommand(User user, string[] args)
        {
            Faction faction = Factions.GetByMember(user);

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            float taxRate;
            try
            {
                taxRate = Convert.ToInt32(args[0]) / 100f;
            }
            catch
            {
                user.SendChatMessage(nameof(Messages.CannotSetTaxRateInvalidValue), Options.Taxes.MaxTaxRate * 100);
                return;
            }

            if (taxRate < 0 || taxRate > Options.Taxes.MaxTaxRate)
            {
                user.SendChatMessage(nameof(Messages.CannotSetTaxRateInvalidValue), Options.Taxes.MaxTaxRate * 100);
                return;
            }

            user.SendChatMessage(nameof(Messages.SetTaxRateSuccessful), faction.Id, taxRate * 100);
            Log($"{Util.Format(user)} set the tax rate for faction {faction.Id} to {taxRate * 100}%");
            Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
            Factions.SetTaxRate(faction, taxRate);
        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;

    public partial class Imperium
    {
        private void OnTaxHelpCommand(User user)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Available commands:");
            sb.AppendLine("  <color=#ffd479>/tax rate NN</color>: Set the tax rate for your faction");
            sb.AppendLine("  <color=#ffd479>/tax chest</color>: Select a container to use as your faction's tax chest");
            sb.AppendLine("  <color=#ffd479>/tax help</color>: Prints this message");

            user.SendChatMessage(sb);
        }
    }
}
#endregion
#region /recruit
/*
namespace Oxide.Plugins
{
    using System.Linq;

    public partial class Imperium
    {
        [ChatCommand("recruit")]
        void OnRecruitCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (!Options.Recruiting.Enabled)
            {
                user.SendChatMessage(nameof(Messages.RecruitingDisabled);
                return;
            }

            ;

            if (args.Length == 0)
            {
                OnRecruitHereCommand(user);
                return;
            }

            var restArguments = args.Skip(1).ToArray();

            switch (args[0].ToLower())
            {
                case "locker":
                    OnRecruitLockerCommand(user);
                    break;
                case "here":
                    OnRecruitHereCommand(user);
                    break;
                case "help":
                default:
                    OnRecruitHelpCommand(user);
                    break;
            }
        }
    }
}
*/

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnRecruitLockerCommand(User user)
        {
            Faction faction = Factions.GetByMember(user);

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }

            user.SendChatMessage(nameof(Messages.SelectArmoryLocker));
            user.BeginInteraction(new SelectingArmoryLockerInteraction(faction));
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private void OnRecruitHereCommand(User user)
        {
            Faction faction = Factions.GetByMember(user);

            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }
            var npc = (global::HumanNPC)GameManager.server.CreateEntity("assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab", user.transform.position, UnityEngine.Quaternion.identity, false);
            if (npc)
            {
                npc.gameObject.AwakeFromInstantiate();
                npc.Spawn();
                Recruit recruit = npc.gameObject.AddComponent<Recruit>();
                var nav = npc.GetComponent<BaseNavigator>();
                if (nav == null)
                    return;
                nav.DefaultArea = "Walkable";
                npc.NavAgent.areaMask = 1;
                npc.NavAgent.agentTypeID = -1372625422;
                npc.NavAgent.autoTraverseOffMeshLink = true;
                npc.NavAgent.autoRepath = true;
                npc.NavAgent.enabled = true;
                nav.CanUseCustomNav = true;
            }

        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;

    public partial class Imperium
    {
        private void OnRecruitHelpCommand(User user)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Available commands:");
            sb.AppendLine("  <color=#ffd479>/recruit locker NN</color>: Set the armory locker for the current land");
            sb.AppendLine("  <color=#ffd479>/recruit here</color>: Recruit a faction bot for the current land");
            sb.AppendLine("  <color=#ffd479>/recruit help</color>: Prints this message");

            user.SendChatMessage(sb);
        }
    }
}
#endregion
#region /upgrade
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ChatCommand("upgrade")]
        private void OnUpgradeCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;
            if (!Instance.Options.Upgrading.Enabled)
            {
                user.SendChatMessage(nameof(Messages.UpgradingDisabled));
                return;
            }
            if (args.Length == 0)
            {
                OnUpgradeHelpCommand(user);
                return;
            }
            switch (args[0])
            {
                case "land":
                    OnUpgradeLandCommand(user);
                    break;
                case "cost":
                    OnUpgradeCostCommand(user);
                    break;
                default:
                    OnUpgradeHelpCommand(user);
                    break;
            }


        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;
    using UnityEngine;
    public partial class Imperium
    {
        private void OnUpgradeCostCommand(User user)
        {
            Area area = Areas.GetByEntityPosition(user.Player);
            if (user.Faction == null)
            {
                user.SendMessage(Messages.NotMemberOfFaction);
                return;
            }
            if (area == null || area.FactionId != user.Faction.Id)
            {
                user.SendChatMessage(nameof(Messages.AreaNotOwnedByYourFaction));
                return;
            }
            if (area.Level >= Instance.Options.Upgrading.MaxUpgradeLevel)
            {
                user.SendChatMessage(nameof(Messages.AreaIsMaximumLevel));
                return;
            }
            var sb = new StringBuilder();
            int costLevels = Instance.Options.Upgrading.Costs.Count - 1;
            sb.AppendLine("Land upgrade costs for " + area.Name + " is");
            sb.AppendLine(area.UpgradeCost + " scrap");
            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;
    using UnityEngine;
    using System.Collections.Generic;
    public partial class Imperium
    {
        private void OnUpgradeLandCommand(User user)
        {
            Area area = Areas.GetByEntityPosition(user.Player);
            if (user.Faction == null)
            {
                user.SendMessage(Messages.NotMemberOfFaction);
                return;
            }
            if (area == null || area.FactionId != user.Faction.Id)
            {
                user.SendChatMessage(nameof(Messages.AreaNotOwnedByYourFaction));
                return;
            }
            if (area.Level >= Instance.Options.Upgrading.MaxUpgradeLevel)
            {
                user.SendChatMessage(nameof(Messages.AreaIsMaximumLevel));
                return;
            }
            if (!Instance.EnsureUserCanChangeFactionClaims(user, user.Faction))
            {
                user.SendMessage(Messages.UserIsNotManagerOfFaction);
                return;
            }
            var cost = area.UpgradeCost;
            if (cost > 0)
            {
                ItemDefinition scrapDef = ItemManager.FindItemDefinition("scrap");
                List<Item> stacks = user.Player.inventory.FindItemsByItemID(scrapDef.itemid);

                if (!Instance.TryCollectFromStacks(scrapDef, stacks, cost))
                {
                    user.SendChatMessage(nameof(Messages.CannotUpgradeAreaCannotAfford), cost);
                    return;
                }
            }
            area.Level++;
            user.SendChatMessage(nameof(Messages.AreaLevelUpgraded), area.Level);
            Util.RunEffect(user.transform.position, "assets/bundled/prefabs/fx/item_unlock.prefab");
        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;
    public partial class Imperium
    {
        private void OnUpgradeHelpCommand(User user)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Available commands:");
            sb.AppendLine("  <color=#ffd479>/upgrade cost</color>: Show cost to upgrade land");
            sb.AppendLine("  <color=#ffd479>/upgrade land</color>: Upgrade land level");
            sb.AppendLine("  <color=#ffd479>/upgrade</color>: Prints this message");
            user.SendChatMessage(sb);
        }
    }
}
#endregion
#region /hud
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ChatCommand("hud")]
        private void OnHudCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (!EnforceCommandCooldown(user, "hud", Options.Map.CommandCooldownSeconds))
                return;

            user.Hud.Toggle();
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ConsoleCommand("imperium.hud.toggle")]
        private void OnHudToggleConsoleCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection.player as BasePlayer;
            if (player == null) return;

            User user = Users.Get(player);
            if (user == null) return;

            if (!EnforceCommandCooldown(user, "hud", Options.Map.CommandCooldownSeconds))
                return;

            user.Hud.Toggle();
        }
    }
}
#endregion
#region imperium.map.togglelayer
namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        [ConsoleCommand("imperium.map.togglelayer")]
        private void OnMapToggleLayerConsoleCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection.player as BasePlayer;
            if (player == null) return;

            User user = Users.Get(player);
            if (user == null) return;

            if (!user.Map.IsVisible)
                return;

            string str = arg.GetString(0);
            UserMapLayer layer;

            if (String.IsNullOrEmpty(str) || !Util.TryParseEnum(arg.Args[0], out layer))
                return;

            user.Preferences.ToggleMapLayer(layer);
            user.Map.Refresh();
        }
    }
}
#endregion
#region /map
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ChatCommand("map")]
        private void OnMapCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (!user.Map.IsVisible && !EnforceCommandCooldown(user, "map", Options.Map.CommandCooldownSeconds))
                return;

            user.Map.Toggle();
        }
    }
}
#endregion
#region imperium.map.toggle
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        [ConsoleCommand("imperium.map.toggle")]
        private void OnMapToggleConsoleCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection.player as BasePlayer;
            if (player == null) return;

            User user = Users.Get(player);
            if (user == null) return;

            if (!user.Map.IsVisible && !EnforceCommandCooldown(user, "map", Options.Map.CommandCooldownSeconds))
                return;

            user.Map.Toggle();
        }
    }
}
#endregion
#region /war
namespace Oxide.Plugins
{
    using System.Linq;

    public partial class Imperium
    {
        [ChatCommand("war")]
        private void OnWarCommand(BasePlayer player, string command, string[] args)
        {
            User user = Users.Get(player);
            if (user == null) return;

            if (!Options.War.Enabled)
            {
                user.SendChatMessage(nameof(Messages.WarDisabled));
                return;
            }

            if (args.Length == 0)
            {
                OnWarHelpCommand(user);
                return;
            }

            var restArgs = args.Skip(1).ToArray();

            switch (args[0].ToLower())
            {
                case "list":
                    OnWarListCommand(user);
                    break;
                case "status":
                    OnWarStatusCommand(user);
                    break;
                case "declare":
                    OnWarDeclareCommand(user, restArgs);
                    break;
                case "end":
                    OnWarEndCommand(user, restArgs);
                    break;
                case "pending":
                    OnWarPendingCommand(user);
                    break;
                case "approve":
                    OnWarApproveCommand(user, restArgs);
                    break;
                case "deny":
                    OnWarDenyCommand(user, restArgs);
                    break;
                case "admin":
                    OnWarAdminCommand(user, restArgs);
                    break;
                default:
                    OnWarHelpCommand(user);
                    break;
            }
        }
    }
}


namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    public partial class Imperium
    {
        private void OnWarDeclareCommand(User user, string[] args)
        {
            Faction attacker = Factions.GetByMember(user);

            if (!EnsureUserAndFactionCanEngageInDiplomacy(user, attacker))
                return;

            if (args.Length < 2)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/war declare FACTION \"REASON\"");
                return;
            }

            if (Instance.Options.War.NoobFactionProtectionInSeconds > 0)
            {
                int elapsedSeconds = Instance.Options.War.NoobFactionProtectionInSeconds;
                int secondsRemaining = 1;
                elapsedSeconds = (int)(DateTime.Now - attacker.CreationTime).TotalSeconds;

                if (elapsedSeconds < Instance.Options.War.NoobFactionProtectionInSeconds)
                {
                    secondsRemaining = Instance.Options.War.NoobFactionProtectionInSeconds - elapsedSeconds;
                    int minutesRemaining = secondsRemaining / 60;
                    if (secondsRemaining >= 60)
                    {
                        user.SendChatMessage(nameof(Messages.CannotDeclareWarNoobAttacker), minutesRemaining, "minutes");
                        return;

                    }

                    user.SendChatMessage(nameof(Messages.CannotDeclareWarNoobAttacker), secondsRemaining, "seconds");
                    return;
                }
            }

            Faction defender = Factions.Get(Util.NormalizeFactionId(args[0]));

            if (defender == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[0]);
                return;
            }

            if (attacker.Id == defender.Id)
            {
                user.SendChatMessage(nameof(Messages.CannotDeclareWarAgainstYourself));
                return;
            }

            War existingWar = Wars.GetActiveWarBetween(attacker, defender);

            if (existingWar != null)
            {
                user.SendChatMessage(nameof(Messages.CannotDeclareWarAlreadyAtWar), defender.Id);
                return;
            }

            if (Instance.Options.War.NoobFactionProtectionInSeconds > 0)
            {
                int elapsedSeconds = Instance.Options.War.NoobFactionProtectionInSeconds;
                int secondsRemaining = 1;
                elapsedSeconds = (int)(DateTime.Now - defender.CreationTime).TotalSeconds;

                if (elapsedSeconds < Instance.Options.War.NoobFactionProtectionInSeconds)
                {
                    secondsRemaining = Instance.Options.War.NoobFactionProtectionInSeconds - elapsedSeconds;
                    int minutesRemaining = secondsRemaining / 60;
                    if (secondsRemaining >= 60)
                    {
                        user.SendChatMessage(nameof(Messages.CannotDeclareWarDefenderProtected), defender.Id, secondsRemaining, "minutes");
                        return;
                    }
                    user.SendChatMessage(nameof(Messages.CannotDeclareWarDefenderProtected), defender.Id, secondsRemaining, "seconds");
                    return;
                }
            }


            string cassusBelli = args[1].Trim();

            if (cassusBelli.Length < Options.War.MinCassusBelliLength)
            {
                user.SendChatMessage(nameof(Messages.CannotDeclareWarInvalidCassusBelli), defender.Id);
                return;
            }

            if (Instance.Options.War.OnlineDefendersRequired > 0)
            {
                User[] defenders = Instance.Users.GetAll().Where(u => u.Faction.Id == defender.Id).ToArray();
                if (defenders.Length < Instance.Options.War.OnlineDefendersRequired)
                {
                    user.SendChatMessage(nameof(Messages.CannotDeclareWarDefendersNotOnline), Instance.Options.War.OnlineDefendersRequired);
                    return;
                }
            }

            var cost = Instance.Options.War.DeclarationCost;
            if (cost > 0)
            {
                ItemDefinition scrapDef = ItemManager.FindItemDefinition("scrap");
                var stacks = user.Player.inventory.FindItemsByItemID(scrapDef.itemid);

                if (!Instance.TryCollectFromStacks(scrapDef, stacks, cost))
                {
                    user.SendChatMessage(nameof(Messages.CannotDeclareWarCannotAfford), cost);
                    return;
                }
            }
            War war = Wars.DeclareWar(attacker, defender, user, cassusBelli);
            PrintToChat(Messages.WarDeclaredAnnouncement, war.AttackerId, war.DefenderId, war.CassusBelli);
            if (!war.IsActive)
                Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_accept.prefab");
            Log(
                $"{Util.Format(user)} declared war on faction {war.DefenderId} on behalf of {war.AttackerId} for reason: {war.CassusBelli}");


        }
    }
}

namespace Oxide.Plugins
{
    using System.Linq;
    public partial class Imperium
    {
        private void OnWarEndCommand(User user, string[] args)
        {
            Faction faction = Factions.GetByMember(user);

            if (!EnsureUserAndFactionCanEngageInDiplomacy(user, faction))
                return;

            Faction enemy = Factions.Get(Util.NormalizeFactionId(args[0]));

            if (enemy == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[0]);
                return;
            }

            War war = Wars.GetActiveWarBetween(faction, enemy);

            if (war == null)
            {
                user.SendChatMessage(nameof(Messages.NotAtWar), enemy.Id);
                return;
            }

            if (war.IsOfferingPeace(faction))
            {
                user.SendChatMessage(nameof(Messages.CannotOfferPeaceAlreadyOfferedPeace), enemy.Id);
                return;
            }

            war.OfferPeace(faction);

            if (war.IsAttackerOfferingPeace && war.IsDefenderOfferingPeace)
            {
                PrintToChat(Messages.WarEndedTreatyAcceptedAnnouncement, faction.Id, enemy.Id);
                Log($"{Util.Format(user)} accepted the peace offering of {enemy.Id} on behalf of {faction.Id}");
                Wars.EndWar(war, WarEndReason.Treaty);
                Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_victory.prefab");
                OnDiplomacyChanged();
            }
            else
            {
                Util.RunEffect(user.transform.position, "assets/prefabs/missions/effects/mission_failed.prefab");
                user.SendChatMessage(nameof(Messages.PeaceOffered), enemy.Id);
                Log($"{Util.Format(user)} offered peace to faction {enemy.Id} on behalf of {faction.Id}");
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System.Text;

    public partial class Imperium
    {
        private void OnWarHelpCommand(User user)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Available commands:");
            sb.AppendLine("  <color=#ffd479>/war list</color>: Show all active wars");
            sb.AppendLine("  <color=#ffd479>/war status</color>: Show all active wars your faction is involved in");
            sb.AppendLine(
                "  <color=#ffd479>/war declare FACTION \"REASON\"</color>: Declare war against another faction");
            sb.AppendLine(
                "  <color=#ffd479>/war end FACTION</color>: Offer to end a war, or accept an offer made to you");
            if (user.HasPermission("imperium.admin.wars"))
            {
                sb.AppendLine(
                "  <color=#ffd479>/war admin pending</color>: List all wars waiting for admin approval");
                sb.AppendLine(
                "  <color=#ffd479>/war admin approve FACTION_1 FACTION_2</color>: Approve a war between two factions");
                sb.AppendLine(
               "  <color=#ffd479>/war admin deny FACTION_1 FACTION_2</color>: Deny a pending war between two factions");
            }
            sb.AppendLine("  <color=#ffd479>/war help</color>: Show this message");

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Text;

    public partial class Imperium
    {
        private void OnWarListCommand(User user)
        {
            var sb = new StringBuilder();
            War[] wars = Wars.GetAllActiveWars();

            if (wars.Length == 0)
            {
                sb.Append("The island is at peace... for now. No wars have been declared.");
            }
            else
            {
                sb.AppendLine(String.Format("<color=#ffd479>The island is at war! {0} wars have been declared:</color>",
                    wars.Length));
                for (var idx = 0; idx < wars.Length; idx++)
                {
                    War war = wars[idx];
                    sb.AppendFormat("{0}. <color=#ffd479>{1}</color> vs <color=#ffd479>{2}</color>: {2}", (idx + 1),
                        war.AttackerId, war.DefenderId, war.CassusBelli);
                    sb.AppendLine();
                }
            }

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Text;

    public partial class Imperium
    {
        private void OnWarStatusCommand(User user)
        {
            Faction faction = Factions.GetByMember(user);

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            var sb = new StringBuilder();
            War[] wars = Wars.GetAllActiveWarsByFaction(faction);

            if (wars.Length == 0)
            {
                sb.AppendLine("Your faction is not involved in any wars.");
            }
            else
            {
                sb.AppendLine(
                    String.Format("<color=#ffd479>Your faction is involved in {0} wars:</color>", wars.Length));
                for (var idx = 0; idx < wars.Length; idx++)
                {
                    War war = wars[idx];
                    sb.AppendFormat("{0}. <color=#ffd479>{1}</color> vs <color=#ffd479>{2}</color>", (idx + 1),
                        war.AttackerId, war.DefenderId);
                    if (war.IsAttackerOfferingPeace)
                        sb.AppendFormat(": <color=#ffd479>{0}</color> is offering peace!", war.AttackerId);
                    if (war.IsDefenderOfferingPeace)
                        sb.AppendFormat(": <color=#ffd479>{0}</color> is offering peace!", war.DefenderId);
                    sb.AppendLine();
                }
            }

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System.Linq;
    public partial class Imperium
    {
        private void OnWarAdminCommand(User user, string[] args)
        {
            if (!user.HasPermission("imperium.wars.admin"))
            {
                user.SendChatMessage(nameof(Messages.NoPermission));
                return;
            }
            var restArgs = args.Skip(1).ToArray();
            switch (args[0].ToLower())
            {
                case "pending":
                    OnWarAdminPendingCommand(user);
                    break;
                case "approve":
                    OnWarAdminApproveCommand(user, restArgs);
                    break;
                case "deny":
                    OnWarAdminDenyCommand(user, restArgs);
                    break;
                default:
                    OnWarHelpCommand(user);
                    break;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Text;

    public partial class Imperium
    {
        private void OnWarAdminPendingCommand(User user)
        {
            var sb = new StringBuilder();
            War[] wars = Wars.GetAllAdminUnnaprovedWars();

            if (wars.Length == 0)
            {
                sb.AppendLine("There are no wars waiting for an admin decision");
            }
            else
            {
                sb.AppendLine(
                    String.Format("<color=#ffd479>There are {0} pending wars to approve or deny:</color>", wars.Length));
                for (var idx = 0; idx < wars.Length; idx++)
                {
                    War war = wars[idx];
                    sb.AppendFormat("{0}. <color=#ffd479>{1}</color> vs <color=#ffd479>{2}</color>", (idx + 1),
                        war.AttackerId, war.DefenderId);
                    sb.AppendLine();
                }
            }

            user.SendChatMessage(sb);
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Text;
    using System.Linq;

    public partial class Imperium
    {
        private void OnWarAdminApproveCommand(User user, string[] args)
        {
            if (args.Length != 2)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/war admin approve FACTION_1 FACTION_2");
                return;
            }
            War[] wars = Wars.GetAllAdminUnnaprovedWars();
            Faction f1 = Factions.Get(Util.NormalizeFactionId(args[0]));
            Faction f2 = Factions.Get(Util.NormalizeFactionId(args[1]));
            if (wars.Length == 0)
            {
                user.SendChatMessage("There are no wars waiting for an admin decision");
                return;
            }
            if (f1 == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[0]);
                return;
            }
            if (f2 == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[1]);
                return;
            }
            var war = wars.SingleOrDefault(w => w.AttackerId == f1.Id && w.DefenderId == f2.Id ||
            w.AttackerId == f2.Id && w.DefenderId == f1.Id);
            if (war == null)
            {
                user.SendChatMessage(nameof(Messages.NoWarBetweenFactions), f1.Id, f2.Id);
                return;
            }
            Instance.Wars.AdminApproveWar(war);
            PrintToChat(Messages.WarDeclaredAdminApproved, war.AttackerId, war.DefenderId, war.CassusBelli);
            Log(
                $"{Util.Format(user)} approved war between faction {war.DefenderId} and {war.AttackerId} for reason: {war.CassusBelli}");
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Text;
    using System.Linq;

    public partial class Imperium
    {
        private void OnWarAdminDenyCommand(User user, string[] args)
        {
            if (args.Length != 2)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/war admin deny FACTION_1 FACTION_2");
                return;
            }
            War[] wars = Wars.GetAllAdminUnnaprovedWars();
            Faction f1 = Factions.Get(Util.NormalizeFactionId(args[0]));
            Faction f2 = Factions.Get(Util.NormalizeFactionId(args[1]));
            if (wars.Length == 0)
            {
                user.SendChatMessage("There are no wars waiting for an admin decision");
                return;
            }
            if (f1 == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[0]);
                return;
            }
            if (f2 == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[1]);
                return;
            }
            var war = wars.SingleOrDefault(w => w.AttackerId == f1.Id && w.DefenderId == f2.Id ||
            w.AttackerId == f2.Id && w.DefenderId == f1.Id);
            if (war == null)
            {
                user.SendChatMessage(nameof(Messages.NoWarBetweenFactions), f1.Id, f2.Id);
                return;
            }
            Instance.Wars.AdminDenyeWar(war);
            PrintToChat(Messages.WarDeclaredAdminDenied, war.AttackerId, war.DefenderId, war.CassusBelli);
            Log(
                $"{Util.Format(user)} denied war between faction {war.DefenderId} and {war.AttackerId} for reason: {war.CassusBelli}");
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Text;
    using System.Linq;

    public partial class Imperium
    {
        private void OnWarApproveCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/war approve FACTION");
                return;
            }
            Faction faction = Factions.GetByMember(user);
            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }
            War[] wars = Wars.GetAllUnapprovedWarsByFaction(faction);
            Faction f1 = Factions.Get(Util.NormalizeFactionId(args[0]));
            if (wars.Length == 0)
            {
                user.SendChatMessage("There are no pending wars against your faction");
                return;
            }
            if (f1 == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[0]);
                return;
            }
            var war = wars.SingleOrDefault(w => w.AttackerId == f1.Id && w.DefenderId == faction.Id);
            if (war == null)
            {
                user.SendChatMessage(nameof(Messages.NoWarBetweenFactions), f1.Id, faction.Id);
                return;
            }
            Instance.Wars.DefenderApproveWar(war);
            PrintToChat(Messages.WarDeclaredDefenderApproved, war.AttackerId, war.DefenderId, war.CassusBelli);
            Log(
                 $"{Util.Format(user)} approved war between faction {war.DefenderId} and {war.AttackerId} for reason: {war.CassusBelli}");
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Text;
    using System.Linq;

    public partial class Imperium
    {
        private void OnWarDenyCommand(User user, string[] args)
        {
            if (args.Length != 1)
            {
                user.SendChatMessage(nameof(Messages.Usage), "/war deny FACTION");
                return;
            }
            Faction faction = Factions.GetByMember(user);
            if (faction == null || !faction.HasLeader(user))
            {
                user.SendChatMessage(nameof(Messages.NotLeaderOfFaction));
                return;
            }
            War[] wars = Wars.GetAllUnapprovedWarsByFaction(faction);
            Faction f1 = Factions.Get(Util.NormalizeFactionId(args[0]));
            if (wars.Length == 0)
            {
                user.SendChatMessage("There are no pending wars against your faction");
                return;
            }
            if (f1 == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[0]);
                return;
            }
            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.FactionDoesNotExist), args[1]);
                return;
            }
            var war = wars.SingleOrDefault(w => w.AttackerId == f1.Id && w.DefenderId == faction.Id);
            if (war == null)
            {
                user.SendChatMessage(nameof(Messages.NoWarBetweenFactions), f1.Id, faction.Id);
                return;
            }
            Instance.Wars.DefenderDenyWar(war);
            PrintToChat(Messages.WarDeclaredDefenderDenied, war.AttackerId, war.DefenderId, war.CassusBelli);
            Log(
                $"{Util.Format(user)} denied war between faction {war.DefenderId} and {war.AttackerId} for reason: {war.CassusBelli}");
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Text;

    public partial class Imperium
    {
        private void OnWarPendingCommand(User user)
        {
            Faction faction = Factions.GetByMember(user);

            if (faction == null)
            {
                user.SendChatMessage(nameof(Messages.NotMemberOfFaction));
                return;
            }

            var sb = new StringBuilder();
            War[] wars = Wars.GetAllUnapprovedWarsByFaction(faction);

            if (wars.Length == 0)
            {
                sb.AppendLine("Your faction has no unapproved wars.");
            }
            else
            {
                sb.AppendLine(
                    String.Format("<color=#ffd479>Your faction has {0} pending wars:</color>", wars.Length));
                for (var idx = 0; idx < wars.Length; idx++)
                {
                    War war = wars[idx];
                    sb.AppendFormat("{0}. <color=#ffd479>{1}</color> vs <color=#ffd479>{2}</color>", (idx + 1),
                        war.AttackerId, war.DefenderId);
                    if (!war.AdminApproved)
                        sb.AppendFormat(": <color=#ffd479>admin approval pending</color>");
                    if (!war.DefenderApproved)
                        sb.AppendFormat(": defender <color=#ffd479>{0}</color> approval pending", war.DefenderId);
                    sb.AppendLine();
                }
            }

            user.SendChatMessage(sb);
        }
    }
}
#endregion
#endregion

#region > API
namespace Oxide.Plugins
{
    using Oxide.Core.Plugins;

    public partial class Imperium
    {
        [HookMethod(nameof(GetFactionName))]
        public object GetFactionName(BasePlayer player)
        {
            User user = Users.Get(player);

            if (user == null || user.Faction == null)
                return null;
            else
                return user.Faction.Id;
        }
    }
}
#endregion

#region > Events
namespace Oxide.Plugins
{
    using Oxide.Core;

    public partial class Imperium : RustPlugin
    {
        private static class Events
        {
            public static void OnAreaChanged(Area area)
            {
                Interface.CallHook(nameof(OnAreaChanged), area);
            }

            public static void OnUserEnteredArea(User user, Area area)
            {
                Interface.CallHook(nameof(OnUserEnteredArea), user, area);
            }

            public static void OnUserLeftArea(User user, Area area)
            {
                Interface.CallHook(nameof(OnUserLeftArea), user, area);
            }

            public static void OnUserEnteredZone(User user, Zone zone)
            {
                Interface.CallHook(nameof(OnUserEnteredZone), user, zone);
            }

            public static void OnUserLeftZone(User user, Zone zone)
            {
                Interface.CallHook(nameof(OnUserLeftZone), user, zone);
            }

            public static void OnFactionCreated(Faction faction)
            {
                Interface.CallHook(nameof(OnFactionCreated), faction);
            }

            public static void OnFactionDisbanded(Faction faction)
            {
                Interface.CallHook(nameof(OnFactionDisbanded), faction);
            }

            public static void OnFactionTaxesChanged(Faction faction)
            {
                Interface.CallHook(nameof(OnFactionTaxesChanged), faction);
            }

            public static void OnFactionArmoryChanged(Faction faction)
            {
                Interface.CallHook(nameof(OnFactionArmoryChanged), faction);
            }

            public static void OnFactionBadlandsChanged(Faction faction)
            {
                Interface.CallHook(nameof(OnFactionBadlandsChanged), faction);
            }

            public static void OnPlayerJoinedFaction(Faction faction, User user)
            {
                Interface.CallHook(nameof(OnPlayerJoinedFaction), faction, user);
            }

            public static void OnPlayerLeftFaction(Faction faction, User user)
            {
                Interface.CallHook(nameof(OnPlayerLeftFaction), faction, user);
            }

            public static void OnPlayerInvitedToFaction(Faction faction, User user)
            {
                Interface.CallHook(nameof(OnPlayerInvitedToFaction), faction, user);
            }

            public static void OnPlayerUninvitedFromFaction(Faction faction, User user)
            {
                Interface.CallHook(nameof(OnPlayerUninvitedFromFaction), faction, user);
            }

            public static void OnPlayerPromoted(Faction faction, User user)
            {
                Interface.CallHook(nameof(OnPlayerPromoted), faction, user);
            }

            public static void OnPlayerDemoted(Faction faction, User user)
            {
                Interface.CallHook(nameof(OnPlayerDemoted), faction, user);
            }

            public static void OnPinCreated(Pin pin)
            {
                Interface.CallHook(nameof(OnPinCreated), pin);
            }

            public static void OnPinRemoved(Pin pin)
            {
                Interface.CallHook(nameof(OnPinRemoved), pin);
            }
        }
    }
}
#endregion

#region > UMod Hooks
namespace Oxide.Plugins
{
    using Network;
    using Oxide.Core;
    using UnityEngine;
    using Newtonsoft.Json.Linq;
    using Oxide.Core.Libraries.Covalence;
    using System.Collections.Generic;

    public partial class Imperium : RustPlugin
    {
        private void OnUserApprove(Connection connection)
        {
            Users.SetOriginalName(connection.userid.ToString(), connection.username);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            // If the player hasn't fully connected yet, try again in 2 seconds.
            if (player.IsReceivingSnapshot)
            {
                timer.In(2, () => OnPlayerConnected(player));
                return;
            }
            Users.Add(player);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            User user = player.GetComponent<User>();
            if (user != null && !user.UpdatedMarkers)
            {
                Areas.UpdateAreaMarkers();
                user.UpdatedMarkers = true;
            }

        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player != null)
                Users.Remove(player);
        }

        private object OnTeamCreate(BasePlayer player)
        {
            if (Instance.Options.Factions.OverrideInGameTeamSystem)
            {
                User user = Instance.Users.Get(player);
                if (user)
                {
                    user.SendChatMessage("You can't create a team. Say <color=#ffd479>/i</color> to create your faction");
                }
                return true;
            }
            return null;
        }

        private object OnTeamInvite(BasePlayer inviter, BasePlayer target)
        {
            if (Instance.Options.Factions.OverrideInGameTeamSystem)
            {
                return true;
            }
            return null;
        }

        private object OnTeamPromote(RelationshipManager.PlayerTeam team, BasePlayer newLeader)
        {
            if (Instance.Options.Factions.OverrideInGameTeamSystem)
            {
                return true;
            }
            return null;
        }

        private object OnTeamKick(ulong currentTeam, ulong newTeam, BasePlayer player)
        {
            if (Instance.Options.Factions.OverrideInGameTeamSystem)
            {
                return true;
            }
            return null;
        }

        private object OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            if (Instance.Options.Factions.OverrideInGameTeamSystem)
            {
                return true;
            }
            return null;
        }

        private object OnTeamDisband(RelationshipManager.PlayerTeam team)
        {
            if (Instance.Options.Factions.OverrideInGameTeamSystem)
            {
                return true;
            }
            return null;
        }

        private void OnHammerHit(BasePlayer player, HitInfo hit)
        {
            User user = Users.Get(player);
            if (user != null && user.CurrentInteraction != null)
                user.CompleteInteraction(hit);
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hit)
        {
            if (entity == null || hit == null)
                return null;

            object externalResult = Interface.CallHook("CanEntityTakeDamage", new object[] { entity, hit });

            if (externalResult != null)
            {
                if ((bool)externalResult == false)
                    return true;

                return null;
            }
            externalResult = GetExternalHookResult("OnEntityTakeDamage", new object[] { entity, hit });
            if (externalResult != null)
                return externalResult;

            if (hit.damageTypes.Has(Rust.DamageType.Decay))
                return Decay.AlterDecayDamage(entity, hit);

            User attacker = null;
            User defender = entity.GetComponent<User>();

            if (hit.InitiatorPlayer != null)
                attacker = hit.InitiatorPlayer.GetComponent<User>();

            // A player is being injured by something other than a player/NPC.
            if (attacker == null && defender != null)
                return Pvp.HandleIncidentalDamage(defender, hit);

            // One player is damaging another player.
            if (attacker != null && defender != null)
                return Pvp.HandleDamageBetweenPlayers(attacker, defender, hit);

            // A player is damaging a structure.
            if (attacker != null && defender == null)
                return Raiding.HandleDamageAgainstStructure(attacker, entity, hit);

            // A structure is taking damage from something that isn't a player.
            return Raiding.HandleIncidentalDamage(entity, hit);
        }

        private object OnTrapTrigger(BaseTrap trap, GameObject obj)
        {
            var player = obj.GetComponent<BasePlayer>();

            if (trap == null || player == null)
                return null;

            User defender = Users.Get(player);
            return Pvp.HandleTrapTrigger(trap, defender);
        }

        private object CanBeTargeted(BaseCombatEntity target, MonoBehaviour turret)
        {
            if (target == null || turret == null)
                return null;

            // Don't interfere with the helicopter.
            if (turret is HelicopterTurret)
                return null;

            var player = target as BasePlayer;

            if (player == null)
                return null;

            if (Users == null)
            {
                return null;
            }

            var defender = Users.Get(player);
            var entity = turret as BaseCombatEntity;

            if (defender == null || entity == null)
                return null;

            return Pvp.HandleTurretTarget(entity, defender);
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity == null)
                return;
            if (Hud == null)
                return;
            if (Options == null)
                return;
            var plane = entity as CargoPlane;
            if (plane != null)
                Hud.GameEvents.BeginEvent(plane);

            var drop = entity as SupplyDrop;
            if (Options.Zones.Enabled && drop != null)
                Zones.CreateForSupplyDrop(drop);

            var heli = entity as BaseHelicopter;
            if (heli != null)
                Hud.GameEvents.BeginEvent(heli);

            var chinook = entity as CH47Helicopter;
            if (chinook != null)
                Hud.GameEvents.BeginEvent(chinook);

            var crate = entity as HackableLockedCrate;
            if (crate != null)
                Hud.GameEvents.BeginEvent(crate);
            var cargoship = entity as CargoShip;
            if (Options.Zones.Enabled && cargoship != null)
                Zones.CreateForCargoShip(cargoship);
        }

        private object OnEntityReskin(BaseEntity entity, ItemSkinDirectory.Skin skin, BasePlayer player)
        {
            BuildingPrivlidge cupboard = entity as BuildingPrivlidge;
            if(cupboard == null)
                return null;
            Area area = Instance.Areas.GetByClaimCupboard(cupboard);
            if(area == null)
                return null;
            area.IsCupboardChangingSkin = true;
            area.ClaimReskinningPlayer = player;
            area.ReskinnedCupboardLastPosition = cupboard.transform.position;
            return null;
        }

        private object OnEntityReskinned(BaseEntity entity, ItemSkinDirectory.Skin skin, BasePlayer player)
        {
            BuildingPrivlidge cupboard = entity as BuildingPrivlidge;
            if(cupboard == null)
                return null;
            Area area = Instance.Areas.GetByClaimReskinningPlayer(player);
            if(area == null)
                area = Instance.Areas.GetByReskinnedCupboardLastPosition(cupboard.transform.position);
            if(area == null)
                return null;
            if(!area.IsCupboardChangingSkin)
                return null;
            area.IsCupboardChangingSkin = false;
            area.ClaimReskinningPlayer = null;
            area.ClaimCupboard = cupboard;
            return null;
        }

        private void OnEntityKill(BaseNetworkable networkable)
        {
            var entity = networkable as BaseEntity;

            if (!Ready || entity == null)
                return;

            // If a claim TC was destroyed, remove the claim from the area.
            var cupboard = entity as BuildingPrivlidge;
            if (cupboard != null)
            {
                var area = Areas.GetByClaimCupboard(cupboard);
                if (area != null)
                {
                    if(area.IsCupboardChangingSkin)
                    {
                        PrintWarning("Attempt to unclaim area blocked because cupboard was reskinned!");
                        return;
                    }
                    PrintToChat(Messages.AreaClaimLostCupboardDestroyedAnnouncement, area.FactionId, area.Id);
                    Log(
                        $"{area.FactionId} lost their claim on {area.Id} because the tool cupboard was destroyed (hook function)");
                    Areas.Unclaim(area);
                }

                return;
            }

            // If a tax chest was destroyed, remove it from the faction data.
            var container = entity as StorageContainer;
            if (Options.Taxes.Enabled && container != null)
            {
                Faction faction = Factions.GetByTaxChest(container);
                if (faction != null)
                {
                    Log($"{faction.Id}'s tax chest was destroyed (hook function)");
                    faction.TaxChest = null;
                }

                return;
            }

            // If an armory locker was destroyed, remove it from the faction data.
            var locker = entity as Locker;
            if (Options.Recruiting.Enabled && locker != null)
            {
                var area = Areas.GetByArmoryLocker(locker);
                if (area != null)
                {
                    Instance.Puts("locker area is not null");
                    Log($"{area.FactionId}'s armory locker was destroyed at {area.Id} (hook function)");
                    Instance.Areas.RemoveArmory(area);
                }

                return;
            }

            // If a helicopter was destroyed, create an event zone around it.
            var helicopter = entity as BaseHelicopter;
            if (Options.Zones.Enabled && helicopter != null)
                Zones.CreateForDebrisField(helicopter);
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            Taxes.ProcessTaxesIfApplicable(dispenser, entity, item);
            Taxes.AwardBadlandsBonusIfApplicable(dispenser, entity, item);
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            Taxes.ProcessTaxesIfApplicable(dispenser, entity, item);
            Taxes.AwardBadlandsBonusIfApplicable(dispenser, entity, item);
        }

        private object OnEntityDeath(BasePlayer player, HitInfo hit)
        {
            if (player == null)
                return null;

            // When a player dies, remove them from the area and any zones they were in.
            User user = Users.Get(player);
            if (user != null)
            {
                user.CurrentArea = null;
                user.CurrentZones.Clear();
            }

            return null;
        }

        private object OnShopAcceptClick(ShopFront entity, BasePlayer player)
        {
            if (!Instance.Options.War.EnableShopfrontPeace)
                return null;

            var user1 = Instance.Users.Get(entity.vendorPlayer);
            var user2 = Instance.Users.Get(entity.customerPlayer);

            if (user1 == null || user2 == null)
                return null;
            if (user1.Faction == null || user2.Faction == null)
                return null;
            if (!Instance.Wars.AreFactionsAtWar(user1.Faction, user2.Faction))
                return null;
            if (user1.Faction.HasLeader(user1) && user2.Faction.HasLeader(user2))
                return null;
            user1.SendChatMessage("Only owners or managers of both enemy factions can trade right now. Trading will end the war");
            user2.SendChatMessage("Only owners or managers of both enemy factions can trade right now. Trading will end the war");
            return true;
        }

        private object OnShopCompleteTrade(ShopFront entity)
        {
            Instance.Wars.TryShopfrontTreaty(entity.vendorPlayer, entity.customerPlayer);
            return null;
        }

        private void OnUserEnteredArea(User user, Area area)
        {

            Area previousArea = user.CurrentArea;

            user.CurrentArea = area;
            user.Hud.Refresh();

            if (area == null || previousArea == null)
                return;
            if (area.Type == AreaType.Badlands && previousArea.Type != AreaType.Badlands)
            {
                // The player has entered the badlands.
                user.SendChatMessage(nameof(Messages.EnteredBadlands));
            }
            else if (area.Type == AreaType.Wilderness && previousArea.Type != AreaType.Wilderness)
            {
                // The player has entered the wilderness.
                user.SendChatMessage(nameof(Messages.EnteredWilderness));
            }
            else if (area.IsClaimed && !previousArea.IsClaimed)
            {
                // The player has entered a faction's territory.
                user.SendChatMessage(nameof(Messages.EnteredClaimedArea), area.FactionId);
            }
            else if (area.IsClaimed && previousArea.IsClaimed && area.FactionId != previousArea.FactionId)
            {
                // The player has crossed a border between the territory of two factions.
                user.SendChatMessage(nameof(Messages.EnteredClaimedArea), area.FactionId);
            }
        }

        private void OnUserLeftArea(User user, Area area)
        {

        }

        private void OnUserEnteredZone(User user, Zone zone)
        {
            user.CurrentZones.Add(zone);
            user.Hud.Refresh();
        }

        private void OnUserLeftZone(User user, Zone zone)
        {
            user.CurrentZones.Remove(zone);
            user.Hud.Refresh();
        }

        private void OnFactionCreated(Faction faction)
        {
            Hud.RefreshForAllPlayers();
        }

        private void OnFactionDisbanded(Faction faction)
        {
            Area[] areas = Instance.Areas.GetAllClaimedByFaction(faction);

            if (areas.Length > 0)
            {
                foreach (Area area in areas)
                    PrintToChat(Messages.AreaClaimLostFactionDisbandedAnnouncement, area.FactionId, area.Id);

                Areas.Unclaim(areas);
            }

            Wars.EndAllWarsForEliminatedFactions();
            Hud.RefreshForAllPlayers();
        }

        private void OnFactionTaxesChanged(Faction faction)
        {
            Hud.RefreshForAllPlayers();
        }

        private void OnFactionArmoryChanged(Faction faction)
        {

        }

        private void OnAreaChanged(Area area)
        {
            Areas.UpdateAreaMarkers();
            Wars.EndAllWarsForEliminatedFactions();
            Pins.RemoveAllPinsInUnclaimedAreas();
            Hud.GenerateMapOverlayImage();
            Hud.RefreshForAllPlayers();
        }

        private void OnDiplomacyChanged()
        {
            Hud.RefreshForAllPlayers();
        }

        #region CLANS by k1lly0u

        private void OnPluginLoaded(CSharpPlugin plugin)
        {
            if (plugin == Clans)
            {
                if (Instance)
                    Instance.Factions.SyncAllWithClans();
            }
        }

        private void OnClanCreate(string tag)
        {
            if (Instance.Options.Factions.UseClansPlugin)
            {
                Faction faction = Factions.Get(tag);
                JObject clanInfo = Clans.CallHook("GetClan", tag) as JObject;
                if (clanInfo != null)
                {
                    string ownerid = clanInfo.GetValue("owner").Value<string>();
                    User owner = Users.Get(ownerid);
                    faction = Factions.Create(tag, owner);
                    owner.SetFaction(faction);
                }
            }
        }

        private void OnClanDisbanded(string tag, List<string> memberUserIDs)
        {
            if (Clans)
            {
                Factions.Disband(Factions.Get(tag));
            }
        }

        private void OnClanMemberJoined(string userID, string tag)
        {
            if (Clans)
            {
                User user = Users.Get(userID);
                Faction faction = Factions.Get(tag);
                if (faction != null && user != null)
                {
                    faction.AddMember(user);
                    user.SetFaction(faction);
                }
            }
        }

        private void OnClanMemberGone(string userID, string tag)
        {
            if (Instance.Options.Factions.UseClansPlugin)
            {
                User user = Users.Get(userID);
                Faction faction = Factions.Get(tag);
                if (faction != null && user != null)
                {
                    if (faction.HasOwner(user))
                    {
                        JObject jClan = (JObject)Clans.CallHook("GetClan", tag);
                        string clanOwnerId = jClan["owner"].Value<string>();
                        if (clanOwnerId != null)
                        {
                            faction.OwnerId = clanOwnerId;
                        }
                    }
                    faction.RemoveMember(user);
                    user.SetFaction(null);
                }
            }
        }

        #endregion
    }
}
#endregion

#region > Decay
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private static class Decay
        {
            public static object AlterDecayDamage(BaseEntity entity, HitInfo hit)
            {
                if (!Instance.Options.Decay.Enabled)
                    return null;

                if (entity == null || hit == null)
                    return null;


                Area area = GetAreaForDecayCalculation(entity);

                if (area == null)
                {
                    //Instance.PrintWarning($"An entity decayed in an unknown area. This shouldn't happen.");
                    return null;
                }

                float reduction = 0;

                if (area.Type == AreaType.Claimed || area.Type == AreaType.Headquarters)
                    reduction = Instance.Options.Decay.ClaimedLandDecayReduction;

                if (Instance.Options.Upgrading.Enabled && Instance.Options.Upgrading.MaxDecayExtraReduction > 0)
                    reduction += area.GetLevelDecayReduction();

                if (reduction >= 1)
                    return true;

                if (reduction > 0)
                    hit.damageTypes.Scale(Rust.DamageType.Decay, reduction);

                return null;
            }

            private static Area GetAreaForDecayCalculation(BaseEntity entity)
            {
                Area area = null;

                // If the entity is controlled by a claim cupboard, use the area the cupboard controls.
                BuildingPrivlidge cupboard = entity.GetBuildingPrivilege();
                if (cupboard)
                    area = Instance.Areas.GetByClaimCupboard(cupboard);

                // Otherwise, determine the area by its position in the world.
                if (area == null)
                    area = Instance.Areas.GetByEntityPosition(entity);

                return area;
            }
        }
    }
}
#endregion

#region > Messages
namespace Oxide.Plugins
{
    using System.Linq;
    using System.Reflection;
    using System.Collections.Generic;

    public partial class Imperium : RustPlugin
    {
        private static class Messages
        {
            public const string AreaClaimsDisabled = "Area claims are currently disabled.";
            public const string TaxationDisabled = "Taxation is currently disabled.";
            public const string RecruitingDisabled = "Recruiting is currently disabled.";
            public const string BadlandsDisabled = "Badlands are currently disabled.";
            public const string UpkeepDisabled = "Upkeep is currently disabled.";
            public const string WarDisabled = "War is currently disabled.";
            public const string PinsDisabled = "Map pins are currently disabled.";
            public const string PvpModeDisabled = "PVP Mode is currently not available.";
            public const string UpgradingDisabled = "Area upgrading is currently disabled.";

            public const string AreaIsBadlands = "<color=#ffd479>{0}</color> is a part of the badlands.";

            public const string AreaIsClaimed =
                "<color=#ffd479>{0}</color> has been claimed by <color=#ffd479>[{1}]</color>.";

            public const string AreaIsHeadquarters =
                "<color=#ffd479>{0}</color> is the headquarters of <color=#ffd479>[{1}]</color>.";

            public const string AreaIsWilderness = "<color=#ffd479>{0}</color> has not been claimed by a faction.";
            public const string AreaNotBadlands = "<color=#ffd479>{0}</color> is not a part of the badlands.";
            public const string AreaNotOwnedByYourFaction = "<color=#ffd479>{0}</color> is not owned by your faction.";
            public const string AreaNotWilderness = "<color=#ffd479>{0}</color> is not currently wilderness.";

            public const string AreaNotContiguous =
                "<color=#ffd479>{0}</color> is not connected to territory owned by <color=#ffd479>[{1}]</color>.";

            public const string YouAreInTheGreatUnknown = "You are currently in the great unknown!";

            public const string InteractionCanceled = "Command canceled.";
            public const string NoInteractionInProgress = "You aren't currently executing any commands.";
            public const string NoAreasClaimed = "Your faction has not claimed any areas.";

            public const string FactionChatMessage = "<color=#a1ff46>(FACTION)</color> {0}: {1}";

            public const string NotMemberOfFaction = "You are not a member of a faction.";
            public const string AlreadyMemberOfFaction = "You are already a member of a faction.";
            public const string NotLeaderOfFaction = "You must be an owner or a manager of a faction.";
            public const string FactionTooSmallToOwnLand = "To own land, a faction must have least {0} members.";

            public const string FactionOwnsTooMuchLand =
                "<color=#ffd479>[{0}]</color> already owns the maximum number of areas (<color=#ffd479>{1}</color>).";

            public const string FactionHasTooManyMembers =
                "<color=#ffd479>[{0}]</color> already has the maximum number of members (<color=#ffd479>{1}</color>).";
            public const string AreaIsMaximumLevel =
                "This land is already at maximum level";

            public const string FactionIsBadlands = "Your faction territory is now badlands";
            public const string FactionIsNotBadlands = "Your faction territory is no longer badlands";
            public const string NoFactionBadlandsAllowed = "Faction enforced badlands is disabled in this server";
            public const string FactionDoesNotOwnLand = "Your faction must own at least one area.";
            public const string FactionAlreadyExists = "There is already a faction named <color=#ffd479>[{0}]</color>.";
            public const string FactionDoesNotExist = "There is no faction named <color=#ffd479>[{0}]</color>.";
            public const string InvalidUser = "Couldn't find a user whose name matches \"{0}\".";

            public const string InvalidFactionName =
                "Faction names must be between {0} and {1} alphanumeric characters.";

            public const string NotAtWar = "You are not currently at war with <color=#ffd479>[{0}]</color>!";

            public const string Usage = "Usage: <color=#ffd479>{0}</color>";
            public const string CommandIsOnCooldown = "You can't do that again so quickly. Try again in {0} seconds.";
            public const string NoPermission = "You don't have permission to do that.";

            public const string MemberAdded =
                "You have added <color=#ffd479>{0}</color> as a member of <color=#ffd479>[{1}]</color>.";

            public const string MemberRemoved =
                "You have removed <color=#ffd479>{0}</color> as a member of <color=#ffd479>[{1}]</color>.";

            public const string ManagerAdded =
                "You have added <color=#ffd479>{0}</color> as a manager of <color=#ffd479>[{1}]</color>.";

            public const string ManagerRemoved =
                "You have removed <color=#ffd479>{0}</color> as a manager of <color=#ffd479>[{1}]</color>.";

            public const string UserIsAlreadyMemberOfFaction =
                "<color=#ffd479>{0}</color> is already a member of <color=#ffd479>[{1}]</color>.";

            public const string UserIsNotMemberOfFaction =
                "<color=#ffd479>{0}</color> is not a member of <color=#ffd479>[{1}]</color>.";

            public const string UserIsAlreadyManagerOfFaction =
                "<color=#ffd479>{0}</color> is already a manager of <color=#ffd479>[{1}]</color>.";

            public const string UserIsNotManagerOfFaction =
                "<color=#ffd479>{0}</color> is not a manager of <color=#ffd479>[{1}]</color>.";

            public const string CannotPromoteOrDemoteOwnerOfFaction =
                "<color=#ffd479>{0}</color> cannot be promoted or demoted, since they are the owner of <color=#ffd479>[{1}]</color>.";

            public const string CannotKickLeaderOfFaction =
                "<color=#ffd479>{0}</color> cannot be kicked, since they are an owner or manager of <color=#ffd479>[{1}]</color>.";

            public const string InviteAdded =
                "You have invited <color=#ffd479>{0}</color> to join <color=#ffd479>[{1}]</color>.";

            public const string InviteReceived =
                "<color=#ffd479>{0}</color> has invited you to join <color=#ffd479>[{1}]</color>. Say <color=#ffd479>/faction join {1}</color> to accept.";

            public const string CannotJoinFactionNotInvited =
                "You cannot join <color=#ffd479>[{0}]</color>, because you have not been invited.";

            public const string CannotManageFactionUseClansInstead =
                "This server uses the Clans plugin. Manage your faction through the Clans system instead. Say /clanhelp for more info";

            public const string YouJoinedFaction = "You are now a member of <color=#ffd479>[{0}]</color>.";
            public const string YouLeftFaction = "You are no longer a member of <color=#ffd479>[{0}]</color>.";

            public const string SelectingCupboardFailedInvalidTarget = "You must select a tool cupboard.";
            public const string SelectingCupboardFailedNotAuthorized = "You must be authorized on the tool cupboard.";
            public const string SelectingCupboardFailedCantUnclaimHeadquarters = "You must move your headquarters to another land first. Say <color=#ffd479>/claim hq</color> in another land";

            public const string SelectingCupboardFailedNotClaimCupboard =
                "That tool cupboard doesn't represent an area claim made by your faction.";

            public const string CannotClaimAreaAlreadyClaimed =
                "<color=#ffd479>{0}</color> has already been claimed by <color=#ffd479>[{1}]</color>.";

            public const string CannotClaimAreaCannotAfford =
                "Claiming this area costs <color=#ffd479>{0}</color> scrap. Add this amount to your inventory and try again.";

            public const string CannotUpgradeAreaCannotAfford =
                "Upgrading this area costs <color=#ffd479>{0}</color> scrap. Add this amount to your inventory and try again.";

            public const string CannotClaimAreaAlreadyOwned =
                "The area <color=#ffd479>{0}</color> is already owned by your faction, and this cupboard represents the claim.";

            public const string SelectClaimCupboardToAdd =
                "Use the hammer to select a tool cupboard to represent the claim. Say <color=#ffd479>/cancel</color> to cancel.";

            public const string SelectClaimCupboardToRemove =
                "Use the hammer to select the tool cupboard representing the claim you want to remove. Say <color=#ffd479>/cancel</color> to cancel.";

            public const string SelectClaimCupboardForHeadquarters =
                "Use the hammer to select the tool cupboard to represent your faction's headquarters. Say <color=#ffd479>/cancel</color> to cancel.";

            public const string SelectClaimCupboardToAssign =
                "Use the hammer to select a tool cupboard to represent the claim to assign to <color=#ffd479>[{0}]</color>. Say <color=#ffd479>/cancel</color> to cancel.";

            public const string SelectClaimCupboardToTransfer =
                "Use the hammer to select the tool cupboard representing the claim to give to <color=#ffd479>[{0}]</color>. Say <color=#ffd479>/cancel</color> to cancel.";

            public const string ClaimCupboardMoved =
                "You have moved the claim <color=#ffd479>{0}</color> to a new tool cupboard.";

            public const string ClaimCaptured =
                "You have captured <color=#ffd479>{0}</color> from <color=#ffd479>[{1}]</color>!";

            public const string ClaimAdded = "You have claimed <color=#ffd479>{0}</color> for your faction.";
            public const string ClaimRemoved = "You have removed your faction's claim on <color=#ffd479>{0}</color>.";

            public const string ClaimTransferred =
                "You have transferred ownership of <color=#ffd479>{0}</color> to <color=#ffd479>[{1}]</color>.";

            public const string InvalidAreaName =
                "Area names must be between <color=#ffd479>{0}</color> and <color=#ffd479>{1}</color> characters long.";

            public const string UnknownArea = "Unknown area <color=#ffd479>{0}</color>.";
            public const string AreaRenamed = "<color=#ffd479>{0}</color> is now known as <color=#ffd479>{1}</color>.";

            public const string ClaimsList = "<color=#ffd479>[{0}]</color> has claimed: <color=#ffd479>{1}</color>";

            public const string ClaimCost =
                "<color=#ffd479>{0}</color> can be claimed by <color=#ffd479>[{1}]</color> for <color=#ffd479>{2}</color> scrap.";

            public const string UpkeepCost =
                "It will cost <color=#ffd479>{0}</color> scrap per day to maintain the <color=#ffd479>{1}</color> areas claimed by <color=#ffd479>[{2}]</color>. Upkeep is due <color=#ffd479>{3}</color> hours from now.";

            public const string UpkeepCostOverdue =
                "It will cost <color=#ffd479>{0}</color> scrap per day to maintain the <color=#ffd479>{1}</color> areas claimed by <color=#ffd479>[{2}]</color>. Your upkeep is <color=#ffd479>{3}</color> hours overdue! Fill your Headquarters Cupboard with scrap immediately, before your claims begin to fall into ruin.";
            public const string SelectArmoryLocker =
                "Use the hammer to select the locker to set as this land armory. Say <color=#ffd479>/cancel</color> to cancel.";

            public const string SelectTaxChest =
                "Use the hammer to select the container to receive your faction's tribute. Say <color=#ffd479>/cancel</color> to cancel.";

            public const string SelectingTaxChestFailedInvalidTarget = "That can't be used as a tax chest.";

            public const string SelectingTaxChestSucceeded =
                "You have selected a new tax chest that will receive <color=#ffd479>{0}%</color> of the materials harvested within land owned by <color=#ffd479>[{1}]</color>. To change the tax rate, say <color=#ffd479>/tax rate PERCENT</color>.";
            public const string SelectingArmoryLockerFailedInvalidTarget =
                "That can't be used as an armory";

            public const string SelectingArmoryLockerSucceeded =
                "You have selected a new armory locker that will be used to recruit bots in {0}";

            public const string CannotSetTaxRateInvalidValue =
                "You must specify a valid percentage between <color=#ffd479>0-{0}%</color> as a tax rate.";

            public const string SetTaxRateSuccessful =
                "You have set the tax rate on the land holdings of <color=#ffd479>[{0}]</color> to <color=#ffd479>{1}%</color>.";

            public const string BadlandsSet = "Badlands areas are now: <color=#ffd479>{0}</color>";

            public const string BadlandsList =
                "Badlands areas are: <color=#ffd479>{0}</color>. Gather bonus is <color=#ffd479>{1}%</color>.";

            public const string CannotDeclareWarAgainstYourself = "You cannot declare war against yourself!";

            public const string CannotDeclareWarAlreadyAtWar =
                "You area already at war with <color=#ffd479>[{0}]</color>!";

            public const string CannotDeclareWarNoobAttacker =
                "You cannot declare war yet because your faction is not old enough. Try again in <color=#ffd479>[{0}]</color> {1}!";


            public const string CannotDeclareWarDefenderProtected =
                "You cannot declare war against <color=#ffd479>[{0}]</color> because this faction is not old enough. Try again in <color=#ffd479>[{1}]</color> {2}!";

            public const string CannotDeclareWarInvalidCassusBelli =
                "You cannot declare war against <color=#ffd479>[{0}]</color>, because your reason doesn't meet the minimum length.";

            public const string CannotDeclareWarCannotAfford =
                "Declaring war <color=#ffd479>{0}</color> scrap. Add this amount to your inventory and try again.";

            public const string CannotDeclareWarDefendersNotOnline =
                "Declaring war requires at least <color=#ffd479>{0}</color> defending member online. Try again when your enemies are online";
            public const string CannotOfferPeaceAlreadyOfferedPeace =
                "You have already offered peace to <color=#ffd479>[{0}]</color>.";

            public const string PeaceOffered =
                "You have offered peace to <color=#ffd479>[{0}]</color>. They must accept it before the war will end.";

            public const string EnteredBadlands =
                "<color=#ff0000>BORDER:</color> You have entered the badlands! Player violence is allowed here.";

            public const string EnteredWilderness = "<color=#ffd479>BORDER:</color> You have entered the wilderness.";

            public const string EnteredClaimedArea =
                "<color=#ffd479>BORDER:</color> You have entered land claimed by <color=#ffd479>[{0}]</color>.";

            public const string EnteredPvpMode =
                "<color=#ff0000>PVP ENABLED:</color> You are now in PVP mode. You can now hurt, and be hurt by, other players who are also in PVP mode.";

            public const string ExitedPvpMode =
                "<color=#00ff00>PVP DISABLED:</color> You are no longer in PVP mode. You can't be harmed by other players except inside of normal PVP areas.";

            public const string PvpModeOnCooldown = "You must wait at least {0} seconds to exit or re-enter PVP mode.";

            public const string InvalidPinType =
                "Unknown map pin type <color=#ffd479>{0}</color>. Say <color=#ffd479>/pin types</color> to see a list of available types.";

            public const string InvalidPinName =
                "Map pin names must be between <color=#ffd479>{0}</color> and <color=#ffd479>{1}</color> characters long.";

            public const string CannotCreatePinCannotAfford =
                "Creating a new map pin costs <color=#ffd479>{0}</color> scrap. Add this amount to your inventory and try again.";

            public const string CannotCreatePinAlreadyExists =
                "Cannot create a new map pin named <color=#ffd479>{0}</color>, since one already exists with the same name in <color=#ffd479>{1}</color>.";

            public const string UnknownPin = "Unknown map pin <color=#ffd479>{0}</color>.";

            public const string CannotRemovePinAreaNotOwnedByYourFaction =
                "Cannot remove the map pin named <color=#ffd479>{0}</color>, because the area <color=#ffd479>{1} is not owned by your faction.";

            public const string PinRemoved = "Removed map pin <color=#ffd479>{0}</color>.";

            public const string FactionCreatedAnnouncement =
                "<color=#00ff00>FACTION CREATED:</color> A new faction <color=#ffd479>[{0}]</color> has been created!";

            public const string FactionDisbandedAnnouncement =
                "<color=#00ff00>FACTION DISBANDED:</color> <color=#ffd479>[{0}]</color> has been disbanded!";

            public const string FactionMemberJoinedAnnouncement =
                "<color=#00ff00>MEMBER JOINED:</color> <color=#ffd479>{0}</color> has joined <color=#ffd479>[{1}]</color>!";

            public const string FactionMemberLeftAnnouncement =
                "<color=#00ff00>MEMBER LEFT:</color> <color=#ffd479>{0}</color> has left <color=#ffd479>[{1}]</color>!";

            public const string AreaClaimedAnnouncement =
                "<color=#00ff00>AREA CLAIMED:</color> <color=#ffd479>[{0}]</color> claims <color=#ffd479>{1}</color>!";

            public const string AreaClaimedAsHeadquartersAnnouncement =
                "<color=#00ff00>AREA CLAIMED:</color> <color=#ffd479>[{0}]</color> claims <color=#ffd479>{1}</color> as their headquarters!";

            public const string AreaLevelUpgraded =
                "Land upgraded to level {0}";

            public const string AreaCapturedAnnouncement =
                "<color=#ff0000>AREA CAPTURED:</color> <color=#ffd479>[{0}]</color> has captured <color=#ffd479>{1}</color> from <color=#ffd479>[{2}]</color>!";

            public const string AreaClaimRemovedAnnouncement =
                "<color=#ff0000>CLAIM REMOVED:</color> <color=#ffd479>[{0}]</color> has relinquished their claim on <color=#ffd479>{1}</color>!";

            public const string AreaClaimTransferredAnnouncement =
                "<color=#ff0000>CLAIM TRANSFERRED:</color> <color=#ffd479>[{0}]</color> has transferred their claim on <color=#ffd479>{1}</color> to <color=#ffd479>[{2}]</color>!";

            public const string AreaClaimAssignedAnnouncement =
                "<color=#ff0000>AREA CLAIM ASSIGNED:</color> <color=#ffd479>{0}</color> has been assigned to <color=#ffd479>[{1}]</color> by an admin.";

            public const string AreaClaimDeletedAnnouncement =
                "<color=#ff0000>AREA CLAIM REMOVED:</color> <color=#ffd479>[{0}]</color>'s claim on <color=#ffd479>{1}</color> has been removed by an admin.";

            public const string AreaClaimLostCupboardDestroyedAnnouncement =
                "<color=#ff0000>AREA CLAIM LOST:</color> <color=#ffd479>[{0}]</color> has lost its claim on <color=#ffd479>{1}</color>, because the tool cupboard was destroyed!";

            public const string AreaClaimLostArmoryDestroyedAnnouncement =
                "<color=#ff0000>AREA ARMORY LOST:</color> <color=#ffd479>[{0}]</color> has lost its armory on <color=#ffd479>{1}</color>, because the locker was destroyed!";

            public const string AreaClaimLostFactionDisbandedAnnouncement =
                "<color=#ff0000>AREA CLAIM LOST:</color> <color=#ffd479>[{0}]</color> has been disbanded, losing its claim on <color=#ffd479>{1}</color>!";

            public const string AreaClaimLostUpkeepNotPaidAnnouncement =
                "<color=#ff0000>AREA CLAIM LOST:</color>: <color=#ffd479>[{0}]</color> has lost their claim on <color=#ffd479>{1}</color> after it fell into ruin! Tool Cupboard destroyed!";

            public const string HeadquartersChangedAnnouncement =
                "<color=#00ff00>HQ CHANGED:</color> The headquarters of <color=#ffd479>[{0}]</color> is now <color=#ffd479>{1}</color>.";

            public const string NoWarBetweenFactions =
                "There are no war declarations between <color=#ffd479>[{0}]</color> and <color=#ffd479>[{1}]</color> ";

            public const string WarDeclaredAnnouncement =
                "<color=#ff0000>WAR DECLARED:</color> <color=#ffd479>[{0}]</color> has declared war on <color=#ffd479>[{1}]</color>! Their reason: <color=#ffd479>{2}</color>";

            public const string WarDeclaredAdminApproved =
                "<color=#ff0000>WAR APPROVED BY AN ADMIN:</color> An admin approved the war between <color=#ffd479>[{0}]</color> and <color=#ffd479>[{1}]</color>!";

            public const string WarDeclaredAdminDenied =
                "<color=#ff0000>WAR DENIED BY AN ADMIN:</color> An admin denied the war between <color=#ffd479>[{0}]</color> and <color=#ffd479>[{1}]</color>!";

            public const string WarDeclaredDefenderApproved =
                "<color=#ff0000>WAR APPROVED BY DEFENDERS:</color> <color=#ffd479>[{1}]</color> accepted the war declaration from <color=#ffd479>[{0}]</color>";

            public const string WarDeclaredDefenderDenied =
                "<color=#ff0000>WAR DENIED BY DEFENDERS:</color>  <color=#ffd479>[{1}]</color> rejected the war declaration from <color=#ffd479>[{0}]</color>";

            public const string WarEndedTreatyAcceptedAnnouncement =
                "<color=#00ff00>WAR ENDED:</color> The war between <color=#ffd479>[{0}]</color> and <color=#ffd479>[{1}]</color> has ended after both sides have agreed to a treaty.";

            public const string WarEndedFactionEliminatedAnnouncement =
                "<color=#00ff00>WAR ENDED:</color> The war between <color=#ffd479>[{0}]</color> and <color=#ffd479>[{1}]</color> has ended, since <color=#ffd479>[{2}]</color> no longer holds any land.";

            public const string PinAddedAnnouncement =
                "<color=#00ff00>POINT OF INTEREST:</color> <color=#ffd479>[{0}]</color> announces the creation of <color=#ffd479>{1}</color>, a new {2} located in <color=#ffd479>{3}</color>!";


            public static string Get(string key, string userId = null)
            {
                return Instance.lang.GetMessage(key, Instance, userId);
            }

            public static Dictionary<string, string> AsDictionary(BindingFlags bindingAttr = BindingFlags.Public | BindingFlags.DeclaredOnly)
            {
                var dict = typeof(Messages).GetFields().Select(f => new { Key = f.Name, Value = (string)f.GetValue(null) }).ToDictionary
                (
                    item => item.Key,
                    item => item.Value
                );
                return dict;

            }
        }

        private void InitLang()
        {
            Dictionary<string, string> messages = Messages.AsDictionary();
            lang.RegisterMessages(messages, this);

        }
    }
}
#endregion

#region > Pvp
namespace Oxide.Plugins
{
    using System;
    using System.Linq;
    using ProtoBuf;

    public partial class Imperium
    {
        private static class Pvp
        {
            private static string[] BlockedPrefabs = new[]
            {
                "fireball_small",
                "fireball_small_arrow",
                "fireball_small_shotgun",
                "fireexplosion",
                "fireball_small_molotov"
            };

            public static object HandleDamageBetweenPlayers(User attacker, User defender, HitInfo hit)
            {
                if (!Instance.Options.Pvp.RestrictPvp)
                    return null;

                if (attacker == null || defender == null || hit == null)
                    return null;
                

                // Allow players to take the easy way out.
                if (hit.damageTypes.Has(Rust.DamageType.Suicide))
                    return null;

                // If the players are both in factions who are currently at war, they can damage each other anywhere.
                if (attacker.Faction != null && defender.Faction != null &&
                    Instance.Wars.AreFactionsAtWar(attacker.Faction, defender.Faction))
                    return null;

                // If both the attacker and the defender are in PVP mode, or in a PVP area/zone, they can damage one another.
                if (IsUserInDanger(attacker) && IsUserInDanger(defender))
                    return null;

                // Stop the damage.
                return true;
            }

            public static object HandleIncidentalDamage(User defender, HitInfo hit)
            {
                if (defender == null || hit == null)
                    return null;

                if (!Instance.Options.Pvp.RestrictPvp)
                    return null;

                if (hit.Initiator == null)
                    return null;

                // If the damage is coming from something other than a blocked prefab, allow it.
                if (!BlockedPrefabs.Contains(hit.Initiator.ShortPrefabName))
                    return null;

                // If the player is in a PVP area or in PVP mode, allow the damage.
                if (IsUserInDanger(defender))
                    return null;

                return true;
            }

            public static object HandleTrapTrigger(BaseTrap trap, User defender)
            {
                if (!Instance.Options.Pvp.RestrictPvp)
                    return null;

                // A player can always trigger their own traps, to prevent exploiting this mechanic.
                if (defender == null || defender.Player == null || defender.Player.userID == trap.OwnerID)
                    return null;

                Area trapArea = Instance.Areas.GetByEntityPosition(trap);

                // If the defender is in a faction, they can trigger traps placed in areas claimed by factions with which they are at war.
                if (trapArea == null || defender.Faction != null && trapArea.FactionId != null &&
                    Instance.Wars.AreFactionsAtWar(defender.Faction.Id, trapArea.FactionId))
                    return null;

                // If the defender is in a PVP area or zone, the trap can trigger.
                // TODO: Ensure the trap is also in the PVP zone.
                if (IsUserInDanger(defender))
                    return null;

                // Stop the trap from triggering.
                return true;
            }

            public static object HandleTurretTarget(BaseCombatEntity turret, User defender)
            {
                if (!Instance.Options.Pvp.RestrictPvp)
                    return null;

                // A player can be always be targeted by their own turrets, to prevent exploiting this mechanic.
                if (defender.Player.userID == turret.OwnerID)
                    return null;

                Area turretArea = Instance.Areas.GetByEntityPosition(turret);

                if (turretArea == null || defender.CurrentArea == null)
                    return null;

                // If the defender is in a faction, they can be targeted by turrets in areas claimed by factions with which they are at war.
                if (defender.Faction != null && turretArea.FactionId != null &&
                    Instance.Wars.AreFactionsAtWar(defender.Faction.Id, turretArea.FactionId))
                    return null;

                // If the defender is in a PVP area or zone, the turret can trigger.
                // TODO: Ensure the turret is also in the PVP zone.
                if (IsUserInDanger(defender))
                    return null;

                return false;
            }

            public static bool IsUserInDanger(User user)
            {
                return user.IsInPvpMode || IsPvpArea(user.CurrentArea) || user.CurrentZones.Any(IsPvpZone);
            }

            public static bool IsPvpZone(Zone zone)
            {
                switch (zone.Type)
                {
                    case ZoneType.Debris:
                    case ZoneType.SupplyDrop:
                        return Instance.Options.Pvp.AllowedInEventZones;
                    case ZoneType.Monument:
                        return Instance.Options.Pvp.AllowedInMonumentZones;
                    case ZoneType.Raid:
                        return Instance.Options.Pvp.AllowedInRaidZones;
                    default:
                        throw new InvalidOperationException($"Unknown zone type {zone.Type}");
                }
            }

            public static bool IsPvpArea(Area area)
            {
                if (area == null)
                    return Instance.Options.Pvp.AllowedInDeepWater;

                switch (area.Type)
                {
                    case AreaType.Badlands:
                        return Instance.Options.Pvp.AllowedInBadlands;
                    case AreaType.Claimed:
                    case AreaType.Headquarters:
                        {
                            if (Instance.Options.Pvp.AllowedInClaimedLand)
                            {
                                return true;
                            }
                            else if (Instance.Options.Factions.AllowFactionBadlands && Instance.Factions.Get(area.FactionId).IsBadlands)
                            {
                                return true;
                            }
                            else
                            {
                                return false;
                            }
                        }
                    case AreaType.Wilderness:
                        return Instance.Options.Pvp.AllowedInWilderness;
                    default:
                        throw new InvalidOperationException($"Unknown area type {area.Type}");
                }
            }
        }
    }
}
#endregion

#region > Raiding
namespace Oxide.Plugins
{
    using System;
    using System.Linq;

    public partial class Imperium
    {
        private static class Raiding
        {
            // Setting this to true will disable the rules which normally allow owners of land to damage their own structures
            // This means that for the purposes of structural damage, the player will be considered a hostile force, which
            // in turn allows easy testing of raiding rules. Should usually be false unless you're testing something.
            private const bool EnableTestMode = false;

            private enum DamageResult
            {
                Prevent,
                NotProtected,
                Friendly,
                BeingAttacked
            }

            private static string[] BlockedPrefabs = new[]
            {
                "fireball_small",
                "fireball_small_arrow",
                "fireball_small_shotgun",
                "fireexplosion"
            };
            private static string[] ProtectedPrefabs = new[]
            {
                "barricade.concrete",
                "barricade.metal",
                "barricade.sandbags",
                "barricade.stone",
                "barricade.wood",
                "barricade.woodwire",
                "bbq",
                "bed",
                "box.wooden.large",
                "ceilinglight",
                "chair",
                "cupboard",
                "door.double.hinged",
                "door.hinged",
                "dropbox",
                "fireplace",
                "floor.ladder.hatch",
                "floor.grill",
                "fridge",
                "furnace",
                "gates.external",
                "jackolantern",
                "lantern",
                "locker",
                "mailbox",
                "planter.large",
                "planter.small",
                "reactivetarget",
                "refinery",
                "repairbench",
                "researchtable",
                "rug",
                "searchlight",
                "shelves",
                "shutter",
                "sign.hanging",
                "sign.huge.wood",
                "sign.large.wood",
                "sign.medium.wood",
                "sign.pictureframe",
                "sign.pole.banner.large",
                "sign.post",
                "sign.small.wood",
                "stash.small",
                "spikes.floor",
                "spinner.wheel",
                "survivalfishtrap",
                "table",
                "tunalight",
                "vendingmachine",
                "wall.external",
                "wall.frame",
                "wall.window",
                "watchtower.wood",
                "water.barrel",
                "water.catcher",
                "water.purifier",
                "waterbarrel",
                "waterpurifier",
                "window.bars",
                "woodbox",
                "workbench",
                "switch",
                "orswitch",
                "andswitch",
                "xorswitch",
                "timer",
                "splitter",
                "blocker",
                "cabletunnel",
                "doorcontroller",
                "generator",
                "laserdetector",
                "pressurepad",
                "simplelight",
                "solarpanel",
                "electrical.branch",
                "electrical.combiner",
                "electrical.memorycell",
                "smallrechargablebattery",
                "large.rechargable.battery"
            };
            private static string[] RaidTriggeringPrefabs = new[]
            {
                "cupboard",
                "door.double.hinged",
                "door.hinged",
                "floor.ladder.hatch",
                "floor.grill",
                "gates.external",
                "vendingmachine",
                "wall.frame",
                "wall.external",
                "wall.window",
                "window.bars"
            };

            public static object HandleDamageAgainstStructure(User attacker, BaseEntity entity, HitInfo hit)
            {
                if (attacker == null || entity == null || hit == null)
                    return null;

                Area area = Instance.Areas.GetByEntityPosition(entity);

                if (area == null && entity.gameObject.GetComponent<BaseCombatEntity>() == null)
                {
                    Instance.PrintWarning("An entity was damaged in an unknown area. This shouldn't happen.");
                    return null;
                }

                DamageResult result = DetermineDamageResult(attacker, area, entity);
                if (EnableTestMode)
                    Instance.Log("Damage from a player to structure with prefab {0}: {1}", entity.ShortPrefabName,
                        result.ToString());

                if (result == DamageResult.NotProtected)
                {
                    return null;
                }

                if (result == DamageResult.Friendly)
                {
                    if (entity.OwnerID == attacker.Player.userID)
                    {
                        return null;
                    }
                    if (!attacker.Faction.HasLeader(attacker) && (!area.IsWarZone && !area.IsHostile))
                    {
                        if (hit.damageTypes.Has(Rust.DamageType.Explosion) || hit.damageTypes.Has(Rust.DamageType.Heat))
                        {
                            hit.damageTypes.ScaleAll(Instance.Options.Factions.MemberOwnLandExplosiveRaidingDamageScale);
                            return null;
                        }
                        hit.damageTypes.ScaleAll(Instance.Options.Factions.MemberOwnLandEcoRaidingDamageScale);
                        return null;
                    }
                    return null;
                }

                if (result == DamageResult.Prevent)
                    return true;

                float reduction = area.GetDefensiveBonus();

                if (reduction >= 1)
                    return true;

                if (reduction > 0)
                    hit.damageTypes.ScaleAll(reduction);

                if (Instance.Options.Zones.Enabled)
                {
                    BuildingPrivlidge cupboard = entity.GetBuildingPrivilege();

                    if (cupboard != null && IsRaidTriggeringEntity(entity))
                    {
                        float remainingHealth = entity.Health() - hit.damageTypes.Total();
                        if (remainingHealth < 1)
                            Instance.Zones.CreateForRaid(cupboard);
                    }
                }

                return null;
            }

            private static DamageResult DetermineDamageResult(User attacker, Area area, BaseEntity entity)
            {
                // Players can always damage their own entities.
                if (!EnableTestMode && attacker.Player.userID == entity.OwnerID)
                    return DamageResult.Friendly;

                if (area == null || !IsProtectedEntity(entity))
                    return DamageResult.NotProtected;

                if (attacker.Faction != null)
                {
                    // Factions can damage any structure built on their own land.
                    if (!EnableTestMode && area.FactionId != null && attacker.Faction.Id == area.FactionId)
                        return DamageResult.Friendly;

                    // Factions who are at war can damage any structure on enemy land, subject to the defensive bonus.
                    if (area.FactionId != null && Instance.Wars.AreFactionsAtWar(attacker.Faction.Id, area.FactionId))
                        return DamageResult.BeingAttacked;

                    // Factions who are at war can damage any structure built by a member of an enemy faction, subject
                    // to the defensive bonus.
                    BasePlayer owner = BasePlayer.FindByID(entity.OwnerID);
                    if (owner != null)
                    {
                        Faction owningFaction = Instance.Factions.GetByMember(owner.UserIDString);
                        if (owningFaction != null && Instance.Wars.AreFactionsAtWar(attacker.Faction, owningFaction))
                            return DamageResult.BeingAttacked;
                    }
                }

                // If the structure is in a raidable area, it can be damaged subject to the defensive bonus.
                if (IsRaidableArea(area))
                    return DamageResult.BeingAttacked;

                // Prevent the damage.
                return DamageResult.Prevent;
            }

            public static object HandleIncidentalDamage(BaseEntity entity, HitInfo hit)
            {
                if (entity == null || hit == null)
                    return null;
                if (!Instance.Options.Raiding.RestrictRaiding)
                    return null;

                Area area = Instance.Areas.GetByEntityPosition(entity);

                if (area == null)
                {
                    Instance.PrintWarning("An entity was damaged in an unknown area. This shouldn't happen.");
                    return null;
                }

                if (hit.Initiator == null)
                {
                    if (EnableTestMode)
                        Instance.Log("Incidental damage to {0} with no initiator", entity.ShortPrefabName);

                    return null;
                }

                // If the damage is coming from something other than a blocked prefab, allow it.
                if (!BlockedPrefabs.Contains(hit.Initiator.ShortPrefabName))
                {

                    if (EnableTestMode)
                    {
                        Instance.Log("Incidental damage to {0} caused by {1}, allowing since it isn't a blocked prefab",
                            entity.ShortPrefabName, hit.Initiator.ShortPrefabName);
                    }


                    return null;
                }

                // If the player is in a PVP area or in PVP mode, allow the damage.
                if (IsRaidableArea(area))
                {
                    if (EnableTestMode)
                        Instance.Log(
                            "Incidental damage to {0} caused by {1}, allowing since target is in raidable area",
                            entity.ShortPrefabName, hit.Initiator.ShortPrefabName);

                    return null;
                }

                if (EnableTestMode)
                    Instance.Log("Incidental damage to {0} caused by {1}, stopping since it is a blocked prefab",
                        entity.ShortPrefabName, hit.Initiator.ShortPrefabName);

                return true;
            }

            private static bool IsProtectedEntity(BaseEntity entity)
            {
                var buildingBlock = entity as BuildingBlock;

                // All building blocks except for twig are protected.
                if (buildingBlock != null)
                    return buildingBlock.grade != BuildingGrade.Enum.Twigs;

                // Some additional entities (doors, boxes, etc.) are also protected.
                if (ProtectedPrefabs.Any(prefab => entity.ShortPrefabName.Contains(prefab)))
                    return true;

                return false;
            }

            private static bool IsRaidTriggeringEntity(BaseEntity entity)
            {
                var buildingBlock = entity as BuildingBlock;

                // All building blocks except for twig are protected.
                if (buildingBlock != null)
                    return buildingBlock.grade != BuildingGrade.Enum.Twigs;

                // Destriction of some additional entities (doors, etc.) will also trigger raids.
                if (RaidTriggeringPrefabs.Any(prefab => entity.ShortPrefabName.Contains(prefab)))
                    return true;

                return false;
            }

            private static bool IsRaidableArea(Area area)
            {
                if (!Instance.Options.Raiding.RestrictRaiding)
                    return true;

                switch (area.Type)
                {
                    case AreaType.Badlands:
                        return Instance.Options.Raiding.AllowedInBadlands;
                    case AreaType.Claimed:
                    case AreaType.Headquarters:
                        return Instance.Options.Raiding.AllowedInClaimedLand;
                    case AreaType.Wilderness:
                        return Instance.Options.Raiding.AllowedInWilderness;
                    default:
                        throw new InvalidOperationException($"Unknown area type {area.Type}");
                }
            }


        }
    }
}
#endregion

#region > Tax
namespace Oxide.Plugins
{
    using UnityEngine;
    public partial class Imperium
    {
        private static class Taxes
        {
            public static void ProcessTaxesIfApplicable(ResourceDispenser dispenser, BaseEntity entity, Item item)
            {
                if (!Instance.Options.Taxes.Enabled)
                    return;

                var player = entity as BasePlayer;
                if (player == null)
                    return;

                User user = Instance.Users.Get(player);
                if (user == null)
                    return;

                Area area = user.CurrentArea;
                if (area == null || !area.IsClaimed)
                    return;

                Faction faction = Instance.Factions.Get(area.FactionId);
                if (!faction.CanCollectTaxes || faction.TaxChest.inventory.IsFull())
                    return;

                ItemDefinition itemDef = ItemManager.FindItemDefinition(item.info.itemid);
                if (itemDef == null)
                    return;

                float landBonus = Instance.Options.Taxes.ClaimedLandGatherBonus;
                float upgradeBonus = 0f;
                int bonus = (int)(item.amount * landBonus);

                if (Instance.Options.Upgrading.Enabled && Instance.Options.Upgrading.MaxTaxChestBonus > 0)
                {
                    upgradeBonus = area.GetLevelTaxBonus();
                    upgradeBonus = Mathf.Floor(landBonus * 100f) / 100f;
                }

                var tax = (int)(item.amount * faction.TaxRate);

                faction.TaxChest.inventory.AddItem(itemDef, (int)((tax + bonus) * (1 + upgradeBonus)));
                item.amount -= tax;
            }

            public static void AwardBadlandsBonusIfApplicable(ResourceDispenser dispenser, BaseEntity entity, Item item)
            {
                if (!Instance.Options.Badlands.Enabled)
                    return;

                var player = entity as BasePlayer;
                if (player == null) return;

                User user = Instance.Users.Get(player);

                if (user.CurrentArea != null && user.CurrentArea.Type == AreaType.Badlands)
                {
                    var bonus = (int)(item.amount * Instance.Options.Taxes.BadlandsGatherBonus);
                    item.amount += bonus;
                }
            }
        }
    }
}
#endregion

#region > Upkeep
namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using UnityEngine;

    public partial class Imperium
    {
        private static class Upkeep
        {
            public static void CollectForAllFactions()
            {
                foreach (Faction faction in Instance.Factions.GetAll())
                    Collect(faction);
            }

            public static void Collect(Faction faction)
            {
                DateTime now = DateTime.UtcNow;
                Area[] areas = Instance.Areas.GetAllTaxableClaimsByFaction(faction);

                if (areas.Length == 0)
                    return;

                if (now < faction.NextUpkeepPaymentTime)
                {
                    Instance.Log($"[UPKEEP] {faction.Id}: Upkeep not due until {faction.NextUpkeepPaymentTime}");
                    return;
                }

                int amountOwed = faction.GetUpkeepPerPeriod();
                var hoursSincePaid = (int)now.Subtract(faction.NextUpkeepPaymentTime).TotalHours;

                Instance.Log(
                    $"[UPKEEP] {faction.Id}: {hoursSincePaid} hours since upkeep paid, trying to collect {amountOwed} scrap for {areas.Length} area claims");

                var headquarters = areas.Where(a => a.Type == AreaType.Headquarters).FirstOrDefault();
                if (headquarters == null || headquarters.ClaimCupboard == null)
                {
                    Instance.Log($"[UPKEEP] {faction.Id}: Couldn't collect upkeep, faction has no headquarters");
                }
                else
                {
                    ItemDefinition scrapDef = ItemManager.FindItemDefinition("scrap");
                    ItemContainer container = headquarters.ClaimCupboard.inventory;
                    List<Item> stacks = container.FindItemsByItemID(scrapDef.itemid);

                    if (Instance.TryCollectFromStacks(scrapDef, stacks, amountOwed))
                    {
                        faction.NextUpkeepPaymentTime =
                            faction.NextUpkeepPaymentTime.AddHours(Instance.Options.Upkeep.CollectionPeriodHours);

                        faction.IsUpkeepPastDue = false;
                        Instance.Log(
                            $"[UPKEEP] {faction.Id}: {amountOwed} scrap upkeep collected, next payment due {faction.NextUpkeepPaymentTime}");
                        return;
                    }
                }

                faction.IsUpkeepPastDue = true;

                if (hoursSincePaid <= Instance.Options.Upkeep.GracePeriodHours)
                {
                    Instance.Log(
                        $"[UPKEEP] {faction.Id}: Couldn't collect upkeep, but still within {Instance.Options.Upkeep.GracePeriodHours} hour grace period");
                    return;
                }
                Area lostArea = null;
                Area[] NonHQAreas = areas.Where(a => a.Type != AreaType.Headquarters).ToArray();
                if (NonHQAreas.Length > 0)
                {
                    lostArea = NonHQAreas.OrderBy(area => Instance.Areas.GetDepthInsideFriendlyTerritory(area)).First();
                }


                if (lostArea == null)
                    lostArea = headquarters;
                Instance.Log(
                    $"[UPKEEP] {faction.Id}: Upkeep not paid in {hoursSincePaid} hours, seizing claim on {lostArea.Id}");
                Util.PrintToChat(nameof(Messages.AreaClaimLostUpkeepNotPaidAnnouncement), faction.Id, lostArea.Id);
                BuildingPrivlidge cupboard = lostArea.ClaimCupboard;
                Instance.Areas.Unclaim(lostArea);
                if (cupboard)
                    cupboard.Kill(BaseNetworkable.DestroyMode.Gib);
            }
        }
    }
}
#endregion

#region > Area
namespace Oxide.Plugins
{
    using Rust;
    using Rust.UI;
    using UnityEngine;

    public partial class Imperium
    {
        private class Area : MonoBehaviour
        {
            public Vector3 Position { get; private set; }
            public Vector3 Size { get; private set; }

            public string Id { get; private set; }
            public int Row { get; private set; }
            public int Col { get; private set; }

            public AreaType Type { get; set; }
            public string Name { get; set; }
            public string FactionId { get; set; }
            public string ClaimantId { get; set; }
            public BuildingPrivlidge ClaimCupboard { get; set; }
            public BasePlayer ClaimReskinningPlayer { get; set; }
            public Vector3 ReskinnedCupboardLastPosition { get; set; }
            public bool IsCupboardChangingSkin { get; set; }
            public Locker ArmoryLocker { get; set; }
            public int Level { get; set; }
            public MapMarkerGenericRadius mapMarker;
            public VendingMachineMapMarker hqMarker;
            public MapMarkerGenericRadius hqMarkerColor;
            public BaseEntity textMarker;
            public bool IsClaimed
            {
                get { return FactionId != null; }
            }
            public bool IsTaxableClaim
            {
                get { return Type == AreaType.Claimed || Type == AreaType.Headquarters; }
            }
            public bool IsWarZone
            {
                get { return GetActiveWars().Length > 0; }
            }
            public bool IsHostile
            {
                get
                {
                    if (FactionId != null)
                    {
                        if (Instance.Factions.Get(FactionId) != null)
                        {
                            return Instance.Factions.Get(FactionId).IsBadlands;
                        }

                    }
                    return false;
                }
            }
            public int UpgradeCost
            {
                get
                {
                    var costs = Instance.Options.Upgrading.Costs;
                    return costs[Mathf.Clamp(Level, 0, costs.Count - 1)
                    ];
                }
            }
            public void Init(string id, int row, int col, Vector3 position, Vector3 size, AreaInfo info)
            {
                Id = id;
                Row = row;
                Col = col;
                Position = position;
                Size = size;


                if (info != null)
                    TryLoadInfo(info);
                gameObject.layer = (int)Layer.Reserved1;
                gameObject.name = $"imperium_area_{id}";
                transform.position = position;
                transform.rotation = Quaternion.Euler(new Vector3(0, 0, 0));

                var collider = gameObject.AddComponent<BoxCollider>();
                collider.size = Size;
                collider.isTrigger = true;
                collider.enabled = true;


                gameObject.SetActive(true);
                enabled = true;
            }

            private void Awake()
            {
                InvokeRepeating("CheckClaimCupboard", 60f, 60f);
                InvokeRepeating("CheckArmoryLocker", 60f, 60f);
            }

            private void OnDestroy()
            {
                var collider = GetComponent<BoxCollider>();

                if (collider != null)
                    Destroy(collider);

                if (IsInvoking("CheckClaimCupboard"))
                    CancelInvoke("CheckClaimCupboard");
                if (IsInvoking("CheckArmoryLocker"))
                    CancelInvoke("CheckArmoryLocker");
            }

            private void TryLoadInfo(AreaInfo info)
            {
                BuildingPrivlidge cupboard = null;
                Locker locker = null;

                if (info.CupboardId != null)
                {
                    cupboard = BaseNetworkable.serverEntities.Find(new NetworkableId((ulong)info.CupboardId)) as BuildingPrivlidge;
                    if (cupboard == null)
                    {
                        Instance.Log(
                            $"[LOAD] Area {Id}: Cupboard entity {info.CupboardId} not found, treating as unclaimed");
                        return;
                    }
                }

                if (info.FactionId != null)
                {
                    Faction faction = Instance.Factions.Get(info.FactionId);
                    if (faction == null)
                    {
                        Instance.Log(
                            $"[LOAD] Area {Id}: Claimed by unknown faction {info.FactionId}, treating as unclaimed");
                        return;
                    }
                }

                Name = info.Name;
                Type = info.Type;
                FactionId = info.FactionId;
                ClaimantId = info.ClaimantId;
                ClaimCupboard = cupboard;
                Level = info.Level;

                if (info.ArmoryId != null)
                {
                    locker = BaseNetworkable.serverEntities.Find(new NetworkableId((ulong)info.ArmoryId)) as Locker;
                    if (locker == null)
                    {
                        Instance.Log(
                            $"[LOAD] Area {Id}: Locker entity {info.ArmoryId} not found");
                    }
                }

                ArmoryLocker = locker;

                if (FactionId != null)
                    Instance.Log(
                        $"[LOAD] Area {Id}: Claimed by {FactionId}, type = {Type}, cupboard = {Util.Format(ClaimCupboard)}");
            }

            private void CheckClaimCupboard()
            {
                if (ClaimCupboard == null || !ClaimCupboard.IsDestroyed)
                    return;

                Instance.Log(
                    $"{FactionId} lost their claim on {Id} because the tool cupboard was destroyed (periodic check)");
                Util.PrintToChat(nameof(Messages.AreaClaimLostCupboardDestroyedAnnouncement), FactionId, Id);
                Instance.Areas.Unclaim(this);
            }

            private void CheckArmoryLocker()
            {
                if (ArmoryLocker == null || !ArmoryLocker.IsDestroyed)
                    return;

                Instance.Log(
                    $"{FactionId} lost their armory on {Id} because the locker was destroyed (periodic check)");
                Util.PrintToChat(nameof(Messages.AreaClaimLostArmoryDestroyedAnnouncement), FactionId, Id);
                Instance.Areas.RemoveArmory(this);
            }

            private void OnTriggerEnter(Collider collider)
            {
                if (collider.gameObject.layer != (int)Layer.Player_Server)
                    return;

                var user = collider.GetComponentInParent<User>();

                if (user != null)
                {
                    if (user.CurrentArea != this)
                    {
                        Events.OnUserEnteredArea(user, this);
                    }
                }
            }

            private void OnTriggerExit(Collider collider)
            {
                if (collider.gameObject.layer != (int)Layer.Player_Server)
                    return;
                var user = collider.GetComponentInParent<User>();
                if (user != null)
                {
                    Events.OnUserLeftArea(user, this);
                }

            }

            public void UpdateAreaMarker(FactionColorPicker colorPicker)
            {
                bool markerExists = true;
                if (Type != AreaType.Headquarters)
                {
                    if (hqMarker != null)
                    {
                        hqMarker.Kill();
                        hqMarker = null;
                    }

                    if (hqMarkerColor != null)
                    {
                        hqMarkerColor.Kill();
                        hqMarkerColor = null;
                    }

                }
                if (Type == AreaType.Wilderness)
                {
                    if (mapMarker != null)
                        mapMarker.Kill();
                }
                else
                {
                    if (mapMarker == null)
                    {
                        markerExists = false;
                        var markerRadius = ((4000 / Instance.Areas.MapGrid.MapSize) * 0.5f);
                        var marker = GameManager.server.CreateEntity(
                            "assets/prefabs/tools/map/genericradiusmarker.prefab", transform.position)
                            as MapMarkerGenericRadius;
                        marker.radius = markerRadius;
                        marker.enableSaving = false;
                        mapMarker = marker;
                    }

                    if (Type == AreaType.Claimed)
                    {
                        mapMarker.alpha = 0.3f;
                        mapMarker.color1 = Util.ConvertSystemToUnityColor(
                            colorPicker.GetColorForFaction(FactionId));
                        mapMarker.color2 = Color.black;
                        if (!markerExists)
                            mapMarker.Spawn();
                        mapMarker.SendUpdate();
                    }
                    if (Type == AreaType.Headquarters)
                    {
                        mapMarker.alpha = 0.3f;
                        mapMarker.color1 = Util.ConvertSystemToUnityColor(
                            colorPicker.GetColorForFaction(FactionId));
                        mapMarker.color2 = Color.black;
                        if (!markerExists)
                            mapMarker.Spawn();
                        mapMarker.SendUpdate();

                        if (hqMarker == null)
                        {
                            if (ClaimCupboard)
                            {
                                Vector3 tcPosition = ClaimCupboard.transform.position;
                                var marker = GameManager.server.CreateEntity(
                                "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab", tcPosition)
                                as VendingMachineMapMarker;
                                marker.markerShopName = FactionId + " Headquarters";
                                marker.transform.position.Set(marker.transform.position.x, -100f, marker.transform.position.z);
                                hqMarker = marker;
                                hqMarker.enableSaving = false;
                                hqMarker.appType = ProtoBuf.AppMarkerType.Player;
                                hqMarker.Spawn();


                                var markerTop = GameManager.server.CreateEntity(
                            "assets/prefabs/tools/map/genericradiusmarker.prefab")
                            as MapMarkerGenericRadius;
                                markerTop.radius = 0.2f;
                                markerTop.enableSaving = false;
                                markerTop.color1 = Util.ConvertSystemToUnityColor(
                                   colorPicker.GetColorForFaction(FactionId));
                                markerTop.color2 = Color.black;
                                markerTop.alpha = 0.5f;
                                markerTop.SetParent(hqMarker);
                                markerTop.Spawn();
                                hqMarkerColor = markerTop;
                                hqMarker.SendNetworkUpdate();
                                markerTop.SendUpdate();

                            }

                        }
                    }
                    if (Type == AreaType.Badlands)
                    {
                        mapMarker.alpha = 0.2f;
                        mapMarker.color1 = Color.black;
                        mapMarker.color2 = Color.black;
                        if (!markerExists)
                            mapMarker.Spawn();
                        mapMarker.SendUpdate();
                    }

                }
            }

            public float GetDistanceFromEntity(BaseEntity entity)
            {
                return Vector3.Distance(entity.transform.position, transform.position);
            }

            public int GetClaimCost(Faction faction)
            {
                var costs = Instance.Options.Claims.Costs;
                int numberOfAreasOwned = Instance.Areas.GetAllClaimedByFaction(faction).Length;
                int index = Mathf.Clamp(numberOfAreasOwned, 0, costs.Count - 1);
                return costs[index];
            }

            public float GetDefensiveBonus()
            {
                var bonuses = Instance.Options.War.DefensiveBonuses;
                var depth = Instance.Areas.GetDepthInsideFriendlyTerritory(this);
                int index = Mathf.Clamp(depth, 0, bonuses.Count - 1);
                float bonus = bonuses[index];
                if (Instance.Options.Upgrading.Enabled && Instance.Options.Upgrading.MaxRaidDefenseBonus > 0)
                {
                    bonus = Mathf.Clamp(bonus + GetLevelDefensiveBonus(), 0, 1);
                    bonus = Mathf.Floor((bonus * 100) / 100);
                }
                return bonus;
            }

            public float GetTaxRate()
            {
                if (!IsTaxableClaim)
                    return 0;

                Faction faction = Instance.Factions.Get(FactionId);

                if (!faction.CanCollectTaxes)
                    return 0;

                return faction.TaxRate;
            }

            public War[] GetActiveWars()
            {
                if (FactionId == null)
                    return new War[0];

                return Instance.Wars.GetAllActiveWarsByFaction(FactionId);
            }

            private float GetRatio(int level, int maxLevel, float maxBonus)
            {
                return (level / maxLevel) * maxBonus;
            }
            public float GetLevelDefensiveBonus()
            {
                return GetRatio(Level,
                    Instance.Options.Upgrading.MaxUpgradeLevel,
                    Instance.Options.Upgrading.MaxRaidDefenseBonus);

            }
            public float GetLevelDecayReduction()
            {
                return GetRatio(Level,
                    Instance.Options.Upgrading.MaxUpgradeLevel,
                    Instance.Options.Upgrading.MaxDecayExtraReduction);
            }

            public float GetLevelTaxBonus()
            {
                return GetRatio(Level,
                    Instance.Options.Upgrading.MaxUpgradeLevel,
                    Instance.Options.Upgrading.MaxTaxChestBonus);
            }

            public AreaInfo Serialize()
            {
                return new AreaInfo
                {
                    Id = Id,
                    Name = Name,
                    Type = Type,
                    FactionId = FactionId,
                    ClaimantId = ClaimantId,
                    CupboardId = ClaimCupboard?.net?.ID.Value,
                    ArmoryId = ArmoryLocker?.net?.ID.Value,
                    Level = Level
                };
            }
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    public partial class Imperium : RustPlugin
    {
        public class AreaInfo
        {
            [JsonProperty("id")] public string Id;

            [JsonProperty("name")] public string Name;

            [JsonProperty("type"), JsonConverter(typeof(StringEnumConverter))]
            public AreaType Type;

            [JsonProperty("factionId")] public string FactionId;

            [JsonProperty("claimantId")] public string ClaimantId;

            [JsonProperty("cupboardId")] public ulong? CupboardId;

            [JsonProperty("armoryId")] public ulong? ArmoryId;

            [JsonProperty("level")] public int Level;
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using UnityEngine;

    public partial class Imperium
    {
        private class AreaManager
        {
            private Dictionary<string, Area> Areas;
            private Area[,] Layout;

            public MapGrid MapGrid { get; }

            public int Count
            {
                get { return Areas.Count; }
            }

            public AreaManager()
            {
                MapGrid = new MapGrid();
                Areas = new Dictionary<string, Area>();
                Layout = new Area[MapGrid.NumberOfColumns, MapGrid.NumberOfRows];
            }

            public Area Get(string areaId)
            {
                Area area;
                if (Areas.TryGetValue(areaId, out area))
                    return area;
                else
                    return null;
            }

            public Area Get(int row, int col)
            {
                return Layout[row, col];
            }

            public Area[] GetAll()
            {
                return Areas.Values.ToArray();
            }

            public Area[] GetAllByType(AreaType type)
            {
                return Areas.Values.Where(a => a.Type == type).ToArray();
            }

            public Area[] GetAllClaimedByFaction(Faction faction)
            {
                return GetAllClaimedByFaction(faction.Id);
            }

            public Area[] GetAllClaimedByFaction(string factionId)
            {
                return Areas.Values.Where(a => a.FactionId == factionId).ToArray();
            }

            public Area[] GetAllTaxableClaimsByFaction(Faction faction)
            {
                return GetAllTaxableClaimsByFaction(faction.Id);
            }

            public Area[] GetAllTaxableClaimsByFaction(string factionId)
            {
                return Areas.Values.Where(a => a.FactionId == factionId && a.IsTaxableClaim).ToArray();
            }

            public Area GetByClaimCupboard(BuildingPrivlidge cupboard)
            {
                return GetByClaimCupboard(cupboard.net.ID.Value);
            }

            public Area GetByClaimReskinningPlayer(BasePlayer player)
            {
                if(player == null)
                    return null;
                return Areas.Values.FirstOrDefault(a =>
                    a.ClaimCupboard != null && a.ClaimReskinningPlayer == player);
            }

            public Area GetByReskinnedCupboardLastPosition(Vector3 testPosition)
            {
                return Areas.Values.FirstOrDefault(a =>
                    IsApproximatedly(a.ReskinnedCupboardLastPosition, testPosition));
                
            }

            private Vector3 Abs(Vector3 v)
            {
                return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
            }

            private bool IsApproximatedly(Vector3 v1, Vector3 v2)
            {
                Vector3 diff = Abs(v1 - v2);
                return (diff.x < 0.1f) && (diff.y < 0.1f) && (diff.z < 0.1f);
            }

            public Area GetByClaimCupboard(ulong cupboardId)
            {
                return Areas.Values.FirstOrDefault(a =>
                    a.ClaimCupboard != null && a.ClaimCupboard.net.ID.Value == cupboardId);
            }
            public Area GetByArmoryLocker(Locker locker)
            {
                return GetByClaimCupboard(locker.net.ID.Value);
            }

            public Area GetByArmoryLocker(ulong lockerId)
            {
                return Areas.Values.FirstOrDefault(a =>
                    a.ArmoryLocker != null && a.ArmoryLocker.net.ID.Value == lockerId);
            }

            public Area GetByEntityPosition(BaseEntity entity)
            {
                Vector3 position = entity.transform.position;

                int row = Mathf.FloorToInt((MapGrid.MapHeight / 2 - (position.z + (MapGrid.MapOffsetZ / 2))) / MapGrid.CellSize) + Instance.Options.Map.MapGridYOffset;
                int col = Mathf.FloorToInt((MapGrid.MapWidth / 2 + (position.x + (MapGrid.MapOffsetX) / 2)) / MapGrid.CellSize);
                if (Instance.Options.Pvp.AllowedUnderground && position.y < -20f)
                    return null;
                if (row < 0 || col < 0 || row >= MapGrid.NumberOfRows || col >= MapGrid.NumberOfColumns)
                    return null;

                return Layout[row, col];
            }

            public Area GetByWorldPosition(Vector3 position)
            {
                int row = Mathf.FloorToInt((MapGrid.MapHeight / 2 - (position.z + (MapGrid.MapOffsetZ / 2))) / MapGrid.CellSize) + Instance.Options.Map.MapGridYOffset;
                int col = Mathf.FloorToInt((MapGrid.MapWidth / 2 + (position.x + (MapGrid.MapOffsetX) / 2)) / MapGrid.CellSize);
                if (Instance.Options.Pvp.AllowedUnderground && position.y < -20f)
                    return null;
                if (row < 0 || col < 0 || row >= MapGrid.NumberOfRows || col >= MapGrid.NumberOfColumns)
                    return null;

                return Layout[row, col];
            }

            public void Claim(Area area, AreaType type, Faction faction, User claimant, BuildingPrivlidge cupboard)
            {
                area.Type = type;
                area.FactionId = faction.Id;
                area.ClaimantId = claimant.Id;
                area.ClaimCupboard = cupboard;


                Events.OnAreaChanged(area);
            }

            public void SetHeadquarters(Area area, Faction faction)
            {
                // Ensure that no other areas are considered headquarters.
                foreach (Area otherArea in GetAllClaimedByFaction(faction).Where(a => a.Type == AreaType.Headquarters))
                {
                    otherArea.Type = AreaType.Claimed;
                    Events.OnAreaChanged(otherArea);
                }

                area.Type = AreaType.Headquarters;
                Events.OnAreaChanged(area);
            }

            public void Unclaim(IEnumerable<Area> areas)
            {
                Unclaim(areas.ToArray());
            }

            public void Unclaim(params Area[] areas)
            {
                foreach (Area area in areas)
                {
                    area.Type = AreaType.Wilderness;
                    area.FactionId = null;
                    area.ClaimantId = null;
                    area.ClaimCupboard = null;

                    Events.OnAreaChanged(area);
                }
            }

            public void SetArmory(Area area, Locker locker)
            {
                area.ArmoryLocker = locker;

                Events.OnAreaChanged(area);
            }

            public void RemoveArmory(Area area)
            {
                area.ArmoryLocker = null;

                Events.OnAreaChanged(area);
            }

            public void AddBadlands(params Area[] areas)
            {
                foreach (Area area in areas)
                {
                    area.Type = AreaType.Badlands;
                    area.FactionId = null;
                    area.ClaimantId = null;
                    area.ClaimCupboard = null;

                    Events.OnAreaChanged(area);
                }
            }

            public void AddBadlands(IEnumerable<Area> areas)
            {
                AddBadlands(areas.ToArray());
            }

            public int GetNumberOfContiguousClaimedAreas(Area area, Faction owner)
            {
                int count = 0;

                // North
                if (area.Row > 0 && Layout[area.Row - 1, area.Col].FactionId == owner.Id)
                    count++;

                // South
                if (area.Row < MapGrid.NumberOfRows - 1 && Layout[area.Row + 1, area.Col].FactionId == owner.Id)
                    count++;

                // West
                if (area.Col > 0 && Layout[area.Row, area.Col - 1].FactionId == owner.Id)
                    count++;

                // East
                if (area.Col < MapGrid.NumberOfColumns - 1 && Layout[area.Row, area.Col + 1].FactionId == owner.Id)
                    count++;

                return count;
            }

            public int GetDepthInsideFriendlyTerritory(Area area)
            {
                if (!area.IsClaimed)
                    return 0;

                var depth = new int[4];

                // North
                for (var row = area.Row; row >= 0; row--)
                {
                    if (Layout[row, area.Col].FactionId != area.FactionId)
                        break;

                    depth[0]++;
                }

                // South
                for (var row = area.Row; row < MapGrid.NumberOfRows; row++)
                {
                    if (Layout[row, area.Col].FactionId != area.FactionId)
                        break;

                    depth[1]++;
                }

                // West
                for (var col = area.Col; col >= 0; col--)
                {
                    if (Layout[area.Row, col].FactionId != area.FactionId)
                        break;

                    depth[2]++;
                }

                // East
                for (var col = area.Col; col < MapGrid.NumberOfColumns; col++)
                {
                    if (Layout[area.Row, col].FactionId != area.FactionId)
                        break;

                    depth[3]++;
                }
                return depth.Min() - 1;
            }

            public void Init(IEnumerable<AreaInfo> areaInfos)
            {
                Instance.Puts("Creating area objects...");

                Dictionary<string, AreaInfo> lookup;
                if (areaInfos != null)
                    lookup = areaInfos.ToDictionary(a => a.Id);
                else
                    lookup = new Dictionary<string, AreaInfo>();

                for (var row = 0; row < MapGrid.NumberOfRows; row++)
                {
                    for (var col = 0; col < MapGrid.NumberOfColumns; col++)
                    {
                        string areaId = MapGrid.GetAreaId(row, col);
                        Vector3 position = MapGrid.GetPosition(row, col);
                        if (Instance.Options.Pvp.AllowedUnderground)
                        {
                            position.y = position.y + 480f;
                        }
                        Vector3 size = new Vector3(MapGrid.CellSize / 2, 500, MapGrid.CellSize / 2);

                        AreaInfo info = null;
                        lookup.TryGetValue(areaId, out info);

                        var area = new GameObject().AddComponent<Area>();
                        area.Init(areaId, row, col, position, size, info);
                        Areas[areaId] = area;
                        Layout[row, col] = area;
                    }
                }
                Instance.Areas.UpdateAreaMarkers();

                Instance.Puts($"Created {Areas.Values.Count} area objects.");
            }

            public void Destroy()
            {
                DestroyAreaMarkers();
                Area[] areas = UnityEngine.Object.FindObjectsOfType<Area>();

                if (areas != null)
                {
                    Instance.Puts($"Destroying {areas.Length} area objects...");
                    foreach (Area area in areas)
                        UnityEngine.Object.Destroy(area);
                }

                Areas.Clear();
                Array.Clear(Layout, 0, Layout.Length);

                Instance.Puts("Area objects destroyed.");
            }

            public AreaInfo[] Serialize()
            {
                return Areas.Values.Select(area => area.Serialize()).ToArray();
            }

            public void UpdateAreaMarkers()
            {
                FactionColorPicker colorPicker = new FactionColorPicker();

                Area[] AllAreas = GetAll();
                foreach (Area area in AllAreas)
                {
                    area.UpdateAreaMarker(colorPicker);
                }
            }

            public void DestroyAreaMarkers()
            {
                Area[] AllAreas = GetAll();
                foreach (Area area in AllAreas)
                {
                    if (area.mapMarker != null)
                    {
                        area.mapMarker.Kill();
                        area.mapMarker = null;
                    }

                    if (area.hqMarker != null)
                    {
                        area.hqMarker.Kill();
                        area.hqMarker = null;
                    }

                    if (area.hqMarkerColor != null)
                    {
                        area.hqMarkerColor.Kill();
                        area.hqMarkerColor = null;
                    }

                }
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium : RustPlugin
    {
        public enum AreaType
        {
            Wilderness,
            Claimed,
            Headquarters,
            Badlands
        }
    }
}
#endregion

#region > Faction
namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using UnityEngine;

    public partial class Imperium
    {
        private class Faction
        {
            public string Id { get; private set; }
            public string OwnerId { get; set; }
            public HashSet<string> MemberIds { get; }
            public HashSet<string> ManagerIds { get; }
            public HashSet<string> InviteIds { get; }
            public HashSet<string> Aggressors { get; }
            public ulong InGameTeamID { get; set; }

            public float TaxRate { get; set; }
            public StorageContainer TaxChest { get; set; }
            public DateTime NextUpkeepPaymentTime { get; set; }
            public bool IsUpkeepPastDue { get; set; }
            public bool IsBadlands { get; set; }
            public DateTime? BadlandsCommandUsedTime { get; set; }
            public DateTime CreationTime { get; set; }


            public bool CanCollectTaxes
            {
                get { return TaxChest != null; }
            }

            public int MemberCount
            {
                get { return MemberIds.Count; }
            }



            public Faction(string id, User owner)
            {
                Id = id;

                OwnerId = owner.Id;
                MemberIds = new HashSet<string> { owner.Id };
                ManagerIds = new HashSet<string>();
                InviteIds = new HashSet<string>();
                Aggressors = new HashSet<string>();
                TaxChest = null;
                TaxRate = Instance.Options.Taxes.DefaultTaxRate;
                NextUpkeepPaymentTime = DateTime.UtcNow.AddHours(Instance.Options.Upkeep.CollectionPeriodHours);
                IsBadlands = false;
                BadlandsCommandUsedTime = null;
            }

            public Faction(FactionInfo info)
            {
                Id = info.Id;

                OwnerId = info.OwnerId;
                MemberIds = new HashSet<string>(info.MemberIds);
                ManagerIds = new HashSet<string>(info.ManagerIds);
                InviteIds = new HashSet<string>(info.InviteIds);
                Aggressors = new HashSet<string>(info.Aggressors);
                InGameTeamID = info.InGameTeamID;
                if (info.TaxChestId != null)
                {
                    var taxChest = BaseNetworkable.serverEntities.Find(new NetworkableId((ulong)info.TaxChestId)) as StorageContainer;

                    if (taxChest == null || taxChest.IsDestroyed)
                        Instance.Log($"[LOAD] Faction {Id}: Tax chest entity {info.TaxChestId} was not found");
                    else
                        TaxChest = taxChest;
                }
                TaxRate = info.TaxRate;
                NextUpkeepPaymentTime = info.NextUpkeepPaymentTime;
                IsUpkeepPastDue = info.IsUpkeepPastDue;
                IsBadlands = info.IsBadlands;
                BadlandsCommandUsedTime = info.BadlandsCommandUsedTime;
                CreationTime = info.CreationTime;

                Instance.Log($"[LOAD] Faction {Id}: {MemberIds.Count} members, tax chest = {Util.Format(TaxChest)}");
            }

            public bool AddMember(User user)
            {
                if (!MemberIds.Add(user.Id))
                    return false;

                InviteIds.Remove(user.Id);

                Events.OnPlayerJoinedFaction(this, user);
                return true;
            }

            public bool RemoveMember(User user)
            {
                if (!HasMember(user.Id))
                    return false;

                if (HasOwner(user.Id))
                {
                    if (ManagerIds.Count > 0)
                    {
                        OwnerId = ManagerIds.FirstOrDefault();
                        ManagerIds.Remove(OwnerId);
                    }
                    else
                    {
                        OwnerId = MemberIds.FirstOrDefault();
                    }
                }

                MemberIds.Remove(user.Id);
                ManagerIds.Remove(user.Id);

                Events.OnPlayerLeftFaction(this, user);
                return true;
            }

            public bool AddInvite(User user)
            {
                if (!InviteIds.Add(user.Id))
                    return false;

                Events.OnPlayerInvitedToFaction(this, user);
                return true;
            }

            public bool RemoveInvite(User user)
            {
                if (!InviteIds.Remove(user.Id))
                    return false;

                Events.OnPlayerUninvitedFromFaction(this, user);
                return true;
            }

            public bool Promote(User user)
            {
                if (!MemberIds.Contains(user.Id))
                    throw new InvalidOperationException(
                        $"Cannot promote player {user.Id} in faction {Id}, since they are not a member");

                if (!ManagerIds.Add(user.Id))
                    return false;

                Events.OnPlayerPromoted(this, user);
                return true;
            }

            public bool Demote(User user)
            {
                if (!MemberIds.Contains(user.Id))
                    throw new InvalidOperationException(
                        $"Cannot demote player {user.Id} in faction {Id}, since they are not a member");

                if (!ManagerIds.Remove(user.Id))
                    return false;

                Events.OnPlayerDemoted(this, user);
                return true;
            }
            public bool SetBadlands(bool value)
            {
                IsBadlands = value;

                Events.OnFactionBadlandsChanged(this);
                return true;
            }

            public bool HasOwner(User user)
            {
                return HasOwner(user.Id);
            }

            public bool HasOwner(string userId)
            {
                return OwnerId == userId;
            }

            public bool HasLeader(User user)
            {
                return HasLeader(user.Id);
            }

            public bool HasLeader(string userId)
            {
                return HasOwner(userId) || HasManager(userId);
            }

            public bool HasManager(User user)
            {
                return HasManager(user.Id);
            }

            public bool HasManager(string userId)
            {
                return ManagerIds.Contains(userId);
            }

            public bool HasInvite(User user)
            {
                return HasInvite(user.Player.UserIDString);
            }

            public bool HasInvite(string userId)
            {
                return InviteIds.Contains(userId);
            }

            public bool HasMember(User user)
            {
                return HasMember(user.Player.UserIDString);
            }

            public bool HasMember(string userId)
            {
                return MemberIds.Contains(userId);
            }

            public User[] GetAllActiveMembers()
            {
                return MemberIds.Select(id => Instance.Users.Get(id)).Where(user => user != null).ToArray();
            }

            public User[] GetAllActiveInvitedUsers()
            {
                return InviteIds.Select(id => Instance.Users.Get(id)).Where(user => user != null).ToArray();
            }

            public int GetUpkeepPerPeriod()
            {
                var costs = Instance.Options.Upkeep.Costs;

                int totalCost = 0;
                for (var num = 0; num < Instance.Areas.GetAllTaxableClaimsByFaction(this).Length; num++)
                {
                    var index = Mathf.Clamp(num, 0, costs.Count - 1);
                    totalCost += costs[index];
                }

                return totalCost;
            }

            public void SendChatMessage(string message, params object[] args)
            {
                foreach (User user in GetAllActiveMembers())
                    user.SendChatMessage(message, args);
            }

            public void CreateFactionTeam()
            {
                List<User> activeMembers = GetAllActiveMembers().ToList();
                BasePlayer firstMember = activeMembers.FirstOrDefault().Player;
                RelationshipManager.PlayerTeam factionTeam = GetFactionPlayerTeam();
                RelationshipManager.PlayerTeam firstTeam = GetOwnerPlayerTeam();
                //If has any member online
                if (firstMember != null)
                {
                    //faction has no valid team
                    if (firstTeam == null)
                    {
                        firstTeam = RelationshipManager.ServerInstance.CreateTeam();
                        firstTeam.SetTeamLeader(firstMember.userID);
                        firstTeam.AddPlayer(firstMember);
                    }
                    InGameTeamID = firstTeam.teamID;
                    factionTeam = firstTeam;
                }
                //if faction team is still null here, something went very wrong.
                if (factionTeam == null)
                    return;

                //Remove all invalid players from the team
                foreach (ulong teamMember in factionTeam.members)
                {
                    if (!MemberIds.Contains(teamMember.ToString()))
                    {
                        User user = Instance.Users.Get(teamMember.ToString());
                        if (user)
                            user.EnsureIsInFactionTeam();
                    }
                }

                //Add all missing valid players to the team
                foreach (User factionMember in activeMembers)
                {
                    if (!factionTeam.members.Contains(factionMember.Player.userID))
                    {
                        factionMember.EnsureIsInFactionTeam();
                    }
                }
            }

            private RelationshipManager.PlayerTeam GetFactionPlayerTeam()
            {
                return RelationshipManager.ServerInstance.FindTeam(InGameTeamID);
            }

            private RelationshipManager.PlayerTeam GetOwnerPlayerTeam()
            {
                BasePlayer ownerPlayer = Instance.Users.Get(OwnerId).Player;
                if (ownerPlayer == null)
                    return null;
                return RelationshipManager.ServerInstance.FindTeam(ownerPlayer.userID);
            }

            public FactionInfo Serialize()
            {
                return new FactionInfo
                {
                    Id = Id,
                    OwnerId = OwnerId,
                    MemberIds = MemberIds.ToArray(),
                    ManagerIds = ManagerIds.ToArray(),
                    InviteIds = InviteIds.ToArray(),
                    Aggressors = Aggressors.ToArray(),
                    InGameTeamID = InGameTeamID,
                    TaxRate = TaxRate,
                    TaxChestId = TaxChest?.net?.ID.Value,
                    NextUpkeepPaymentTime = NextUpkeepPaymentTime,
                    IsBadlands = IsBadlands,
                    BadlandsCommandUsedTime = BadlandsCommandUsedTime,
                    CreationTime = CreationTime
                };
            }
        }
    }
}

namespace Oxide.Plugins
{
    using UnityEngine;

    public partial class Imperium
    {
        private class FactionEntityMonitor : MonoBehaviour
        {
            private void Awake()
            {
                InvokeRepeating("CheckTaxChests", 60f, 60f);
            }

            private void OnDestroy()
            {
                if (IsInvoking("CheckTaxChests")) CancelInvoke("CheckTaxChests");
            }

            private void EnsureAllTaxChestsStillExist()
            {
                foreach (Faction faction in Instance.Factions.GetAll())
                    EnsureTaxChestExists(faction);
            }

            private void EnsureTaxChestExists(Faction faction)
            {
                if (faction.TaxChest == null || !faction.TaxChest.IsDestroyed)
                    return;

                Instance.Log($"{faction.Id}'s tax chest was destroyed (periodic check)");
                faction.TaxChest = null;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    public partial class Imperium : RustPlugin
    {
        private class FactionInfo
        {
            [JsonProperty("id")] public string Id;

            [JsonProperty("ownerId")] public string OwnerId;

            [JsonProperty("memberIds")] public string[] MemberIds;

            [JsonProperty("managerIds")] public string[] ManagerIds;

            [JsonProperty("inviteIds")] public string[] InviteIds;

            [JsonProperty("aggressors")] public string[] Aggressors;

            [JsonProperty("inGameTeamID")] public ulong InGameTeamID;

            [JsonProperty("taxRate")] public float TaxRate;

            [JsonProperty("taxChestId")] public ulong? TaxChestId;

            [JsonProperty("nextUpkeepPaymentTime")]
            public DateTime NextUpkeepPaymentTime;

            [JsonProperty("isUpkeepPastDue")] public bool IsUpkeepPastDue;

            [JsonProperty("isBadlands")] public bool IsBadlands;

            [JsonProperty("badlandsCommandUsedTime"), JsonConverter(typeof(IsoDateTimeConverter))]
            public DateTime? BadlandsCommandUsedTime;

            [JsonProperty("creationTime"), JsonConverter(typeof(IsoDateTimeConverter))]
            public DateTime CreationTime;
        }


    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using UnityEngine;
    using Newtonsoft.Json.Linq;

    public partial class Imperium
    {
        private class FactionManager
        {
            private Dictionary<string, Faction> Factions = new Dictionary<string, Faction>();
            private FactionEntityMonitor EntityMonitor;

            public FactionManager()
            {
                Factions = new Dictionary<string, Faction>();
                EntityMonitor = Instance.GO.AddComponent<FactionEntityMonitor>();
            }

            public Faction Create(string id, User owner)
            {
                Faction faction;

                if (Factions.TryGetValue(id, out faction))
                    throw new InvalidOperationException(
                        $"Cannot create a new faction named ${id}, since one already exists");

                faction = new Faction(id, owner);
                faction.CreationTime = DateTime.Now;



                Factions.Add(id, faction);

                if (Instance.Options.Factions.OverrideInGameTeamSystem)
                {
                    faction.CreateFactionTeam();
                }

                Events.OnFactionCreated(faction);

                return faction;
            }

            public void Disband(Faction faction)
            {
                foreach (User user in faction.GetAllActiveMembers())
                    user.SetFaction(null);

                Factions.Remove(faction.Id);
                Events.OnFactionDisbanded(faction);
            }

            public Faction[] GetAll()
            {
                return Factions.Values.ToArray();
            }

            public Faction Get(string id)
            {
                Faction faction;
                if (Factions.TryGetValue(id, out faction))
                    return faction;
                else
                    return null;
            }

            public bool Exists(string id)
            {
                return Factions.ContainsKey(id);
            }

            public Faction GetByMember(User user)
            {
                return GetByMember(user.Id);
            }

            public Faction GetByMember(string userId)
            {
                return Factions.Values.Where(f => f.HasMember(userId)).FirstOrDefault();
            }

            public Faction GetByTaxChest(StorageContainer container)
            {
                return GetByTaxChest(container.net.ID.Value);
            }

            public Faction GetByTaxChest(ulong containerId)
            {
                return Factions.Values.SingleOrDefault(f => f.TaxChest != null && f.TaxChest.net.ID.Value == containerId);
            }

            public void SetTaxRate(Faction faction, float taxRate)
            {
                faction.TaxRate = taxRate;
                Events.OnFactionTaxesChanged(faction);
            }

            public void SetTaxChest(Faction faction, StorageContainer taxChest)
            {
                faction.TaxChest = taxChest;
                Events.OnFactionTaxesChanged(faction);
            }

            public void Init(IEnumerable<FactionInfo> factionInfos)
            {
                Instance.Puts($"Creating factions for {factionInfos.Count()} factions...");

                foreach (FactionInfo info in factionInfos)
                {
                    Faction faction = new Faction(info);
                    Factions.Add(faction.Id, faction);
                }

                Instance.Puts("Factions created.");
            }

            public void Destroy()
            {
                UnityEngine.Object.Destroy(EntityMonitor);
                Factions.Clear();
            }

            public FactionInfo[] Serialize()
            {
                return Factions.Values.Select(faction => faction.Serialize()).ToArray();
            }

            internal void SyncAllWithClans()
            {
                if (Instance.Options.Factions.UseClansPlugin)
                {
                    Instance.Puts("Syncing factions with Clans!");
                    //Disband all factions that don't have a matching clan
                    JArray AllClans = (JArray)Instance.Clans.CallHook("GetAllClans");
                    List<string> clanIds = new List<string>();
                    if (AllClans.Count > 0)
                    {
                        for (int i = 0; i < AllClans.Count; i++)
                        {
                            string clanId = AllClans[i].Value<string>();
                            clanIds.Add(clanId);

                        }
                    }
                    List<Faction> allFactions = GetAll().ToList();
                    if (allFactions.Count > 0)
                    {
                        foreach (Faction faction in allFactions)
                        {
                            if (!clanIds.Contains(faction.Id))
                            {
                                Disband(faction);
                            }
                        }
                    }
                    List<User> users = Instance.Users.GetAll().ToList();
                    if (users.Count > 0)
                    {
                        foreach (User user in Instance.Users.GetAll())
                        {
                            user.SyncWithClan();
                        }
                    }
                }
            }
        }
    }
}
#endregion

#region > Pin
namespace Oxide.Plugins
{
    using UnityEngine;

    public partial class Imperium
    {
        private class Pin
        {
            public Vector3 Position { get; }
            public string AreaId { get; }
            public string CreatorId { get; set; }
            public PinType Type { get; set; }
            public string Name { get; set; }

            public Pin(Vector3 position, Area area, User creator, PinType type, string name)
            {
                Position = position;
                AreaId = area.Id;
                CreatorId = creator.Id;
                Type = type;
                Name = name;
            }

            public Pin(PinInfo info)
            {
                Position = info.Position;
                AreaId = info.AreaId;
                CreatorId = info.CreatorId;
                Type = info.Type;
                Name = info.Name;
            }

            public float GetDistanceFrom(BaseEntity entity)
            {
                return GetDistanceFrom(entity.transform.position);
            }

            public float GetDistanceFrom(Vector3 position)
            {
                return Vector3.Distance(position, Position);
            }

            public PinInfo Serialize()
            {
                return new PinInfo
                {
                    Position = Position,
                    AreaId = AreaId,
                    CreatorId = CreatorId,
                    Type = Type,
                    Name = Name
                };
            }
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;
    using UnityEngine;

    public partial class Imperium : RustPlugin
    {
        private class PinInfo
        {
            [JsonProperty("name")] public string Name;

            [JsonProperty("type"), JsonConverter(typeof(StringEnumConverter))]
            public PinType Type;

            [JsonProperty("position"), JsonConverter(typeof(UnityVector3Converter))]
            public Vector3 Position;

            [JsonProperty("areaId")] public string AreaId;

            [JsonProperty("creatorId")] public string CreatorId;
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public partial class Imperium
    {
        private class PinManager
        {
            private Dictionary<string, Pin> Pins;

            public PinManager()
            {
                Pins = new Dictionary<string, Pin>(StringComparer.OrdinalIgnoreCase);
            }

            public Pin Get(string name)
            {
                Pin pin;

                if (!Pins.TryGetValue(name, out pin))
                    return null;

                return pin;
            }

            public Pin[] GetAll()
            {
                return Pins.Values.ToArray();
            }

            public void Add(Pin pin)
            {
                Pins.Add(pin.Name, pin);
                Events.OnPinCreated(pin);
            }

            public void Remove(Pin pin)
            {
                Pins.Remove(pin.Name);
                Events.OnPinRemoved(pin);
            }

            public void RemoveAllPinsInUnclaimedAreas()
            {
                foreach (Pin pin in GetAll())
                {
                    Area area = Instance.Areas.Get(pin.AreaId);
                    if (!area.IsClaimed) Remove(pin);
                }
            }

            public void Init(IEnumerable<PinInfo> pinInfos)
            {
                Instance.Puts($"Creating pins for {pinInfos.Count()} pins...");

                foreach (PinInfo info in pinInfos)
                {
                    var pin = new Pin(info);
                    Pins.Add(pin.Name, pin);
                }

                Instance.Puts("Pins created.");
            }

            public void Destroy()
            {
                Pins.Clear();
            }

            public PinInfo[] Serialize()
            {
                return Pins.Values.Select(pin => pin.Serialize()).ToArray();
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private enum PinType
        {
            Arena,
            Hotel,
            Marina,
            Shop,
            Town
        }
    }
}
#endregion

#region > User
namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using UnityEngine;
    using System.Linq;
    using Newtonsoft.Json.Linq;
    //using ProtoBuf;

    public partial class Imperium
    {
        private class User : MonoBehaviour
        {
            private string OriginalName;
            private Dictionary<string, DateTime> CommandCooldownExpirations;
            public BasePlayer Player { get; private set; }
            public UserMap Map { get; private set; }
            public UserHud Hud { get; private set; }
            public UserPanel Panel { get; set; }
            public UserPreferences Preferences { get; set; }
            public Area CurrentArea { get; set; }
            public HashSet<Zone> CurrentZones { get; private set; }
            public Faction Faction { get; private set; }
            public Interaction CurrentInteraction { get; private set; }
            public DateTime MapCommandCooldownExpiration { get; set; }
            public DateTime PvpCommandCooldownExpiration { get; set; }
            public bool IsInPvpMode { get; set; }

            public bool UpdatedMarkers = false;

            public string Id
            {
                get { return Player.UserIDString; }
            }

            public string UserName
            {
                get { return OriginalName; }
            }

            public string UserNameWithFactionTag
            {
                get { return Player.displayName; }
            }

            public void Init(BasePlayer player)
            {
                Player = player;
                OriginalName = player.displayName;
                CurrentZones = new HashSet<Zone>();
                CommandCooldownExpirations = new Dictionary<string, DateTime>();
                Preferences = UserPreferences.Default;

                Map = new UserMap(this);
                Hud = new UserHud(this);
                Panel = new UserPanel(this);

                InvokeRepeating(nameof(UpdateHud), 5f, 5f);
                InvokeRepeating(nameof(CheckArea), 2f, 2f);
            }

            private void OnDestroy()
            {
                Map.Hide();
                Hud.Hide();
                Panel.Hide();

                if (IsInvoking(nameof(UpdateHud))) CancelInvoke(nameof(UpdateHud));
                if (IsInvoking(nameof(CheckArea))) CancelInvoke(nameof(CheckArea));

                if (Player != null)
                    Player.displayName = OriginalName;
            }

            public void SetFaction(Faction faction)
            {
                if (Faction == faction)
                    return;
                CurrentInteraction = null;
                Faction = faction;

                if (faction == null)
                    Player.displayName = OriginalName;
                else
                    Player.displayName = $"[{faction.Id}] {Player.displayName}";
                if (Instance.Options.Factions.OverrideInGameTeamSystem)
                {
                    CancelInvoke("EnsureIsInFactionTeam");
                    Invoke("EnsureIsInFactionTeam", 3f);
                }

                Player.SendNetworkUpdate();
            }

            public bool HasPermission(string permission)
            {
                return Instance.permission.UserHasPermission(Player.UserIDString, permission);
            }

            public void BeginInteraction(Interaction interaction)
            {
                interaction.User = this;
                CurrentInteraction = interaction;
            }

            public void CompleteInteraction(HitInfo hit)
            {
                if (CurrentInteraction.TryComplete(hit))
                    CurrentInteraction = null;
            }

            public void CancelInteraction()
            {
                MapMarker
                CurrentInteraction = null;
            }

            public void SendChatMessage(string message, params object[] args)
            {
                string format = Instance.lang.GetMessage(message, Instance, Player.UserIDString);
                Instance.SendReply(Player, format, args);
            }

            public void SendChatMessage(StringBuilder sb)
            {
                Instance.SendReply(Player, sb.ToString().TrimEnd());
            }

            public void SendConsoleMessage(string message, params object[] args)
            {
                Player.ConsoleMessage(String.Format(message, args));
            }

            private void UpdateHud()
            {
                Hud.Refresh();
            }

            public int GetSecondsLeftOnCooldown(string command)
            {
                DateTime expiration;

                if (!CommandCooldownExpirations.TryGetValue(command, out expiration))
                    return 0;

                return (int)Math.Max(0, expiration.Subtract(DateTime.UtcNow).TotalSeconds);
            }

            public void SetCooldownExpiration(string command, DateTime time)
            {
                CommandCooldownExpirations[command] = time;
            }

            private void CheckArea()
            {
                Area currentArea = CurrentArea;
                Area correctArea = Instance.Areas.GetByEntityPosition(Player);

                if (currentArea != null && (correctArea == null || currentArea.Id != correctArea.Id))
                {
                    Events.OnUserLeftArea(this, currentArea);
                }
                Events.OnUserEnteredArea(this, correctArea);

            }

            public void EnsureIsInFactionTeam()
            {
                if (Player.currentTeam != 0UL)
                {
                    if (Faction == null || Player.currentTeam != Faction.InGameTeamID)
                    {
                        Player.Team.RemovePlayer(Player.userID);
                        if (Faction == null)
                            return;
                    }
                    if (Player.currentTeam == Faction.InGameTeamID)
                        return;
                }
                if (Player.currentTeam == 0UL && Faction == null)
                    return;
                RelationshipManager.PlayerTeam factionTeam;
                factionTeam = RelationshipManager.ServerInstance.FindTeam(Faction.InGameTeamID);
                if (factionTeam == null)
                {
                    Faction.CreateFactionTeam();
                    return;
                }
                factionTeam.AddPlayer(Player);
            }

            public void SyncWithClan()
            {
                if (!Instance.Options.Factions.UseClansPlugin)
                    return;

                if (Player == null)
                    return;
                string clanId = (string)Instance.Clans.CallHook("GetClanOf", Player);
                //is in correct faction already
                if (Faction?.Id == clanId)
                {
                    return;
                }
                //user is not in a clan
                if (clanId == null)
                {
                    //user is in a faction (Disband if owner, leave if not)
                    if (Faction != null)
                    {
                        if (Faction.HasOwner(this))
                        {
                            Instance.Factions.Disband(this.Faction);
                            SetFaction(null);
                        }
                        else
                        {
                            Faction.RemoveMember(this);
                            SetFaction(null);
                        }

                    }
                    return;
                }
                JObject jClan = (JObject)Instance.Clans.CallHook("GetClan", clanId);
                Faction clanFaction = Instance.Factions.Get(clanId);

                //corresponding clan for faction does not exist yet. If owner, create the correct faction
                if (clanFaction == null)
                {
                    User owner = Instance.Users.Get(jClan.GetValue("owner").Value<string>());
                    //clan owner is online
                    if (owner != null)
                    {
                        clanFaction = Instance.Factions.Create(clanId, owner);
                        if (owner.Faction != null)
                        {
                            Instance.Factions.Disband(owner.Faction);
                        }
                        owner.SetFaction(clanFaction);
                        if (this == owner)
                            return;
                    }
                }
                //if user faction is in a faction and is not in the clanFaction (might be null). Leave the current faction
                if (Faction != null && Faction != clanFaction)
                {

                    if (this.Faction.HasOwner(this))
                    {
                        Instance.Factions.Disband(this.Faction);
                        SetFaction(null);
                    }
                    else
                    {
                        Faction.RemoveMember(this);
                        SetFaction(null);
                    }
                }
                //set own faction if not equal clan faction
                if (Faction != clanFaction)
                    SetFaction(clanFaction);

            }

            private void CheckZones()
            {
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System.Collections.Generic;
    using System.Linq;

    public partial class Imperium
    {
        private class UserManager
        {
            private Dictionary<string, User> Users = new Dictionary<string, User>();
            private Dictionary<string, string> OriginalNames = new Dictionary<string, string>();

            public User[] GetAll()
            {
                return Users.Values.ToArray();
            }

            public User Get(BasePlayer player)
            {
                if (player == null) return null;
                return Get(player.UserIDString);
            }

            public User Get(string userId)
            {
                User user;
                if (Users.TryGetValue(userId, out user))
                    return user;
                else
                    return null;
            }

            public User Find(string searchString)
            {
                User user = Get(searchString);

                if (user != null)
                    return user;

                return Users.Values
                    .Where(u => u.UserName.ToLowerInvariant().Contains(searchString.ToLowerInvariant()))
                    .OrderBy(u =>
                        Util.GetLevenshteinDistance(searchString.ToLowerInvariant(), u.UserName.ToLowerInvariant()))
                    .FirstOrDefault();
            }

            public User Add(BasePlayer player)
            {
                Remove(player);

                string originalName;
                if (OriginalNames.TryGetValue(player.UserIDString, out originalName))
                    player.displayName = originalName;
                else
                    OriginalNames[player.UserIDString] = player.displayName;

                User user = player.gameObject.AddComponent<User>();
                user.Init(player);

                Faction faction = Instance.Factions.GetByMember(user);
                if (faction != null)
                    user.SetFaction(faction);
                else
                    user.SetFaction(null);

                Users[user.Player.UserIDString] = user;

                if (Instance.Options.Factions.UseClansPlugin)
                {
                    user.SyncWithClan();
                }

                return user;
            }

            public bool Remove(BasePlayer player)
            {
                User user = Get(player);
                if (user == null) return false;

                UnityEngine.Object.DestroyImmediate(user);
                Users.Remove(player.UserIDString);

                return true;
            }

            public void SetOriginalName(string userId, string name)
            {
                OriginalNames[userId] = name;
            }

            public void Init()
            {
                var players = BasePlayer.activePlayerList;

                Instance.Puts($"Creating user objects for {players.Count} players...");

                foreach (BasePlayer player in players)
                    Add(player);

                Instance.Puts($"Created {Users.Count} user objects.");
            }

            public void Destroy()
            {
                User[] users = UnityEngine.Object.FindObjectsOfType<User>();

                if (users == null)
                    return;

                Instance.Puts($"Destroying {users.Length} user objects.");

                foreach (var user in users)
                    UnityEngine.Object.DestroyImmediate(user);

                Users.Clear();

                Instance.Puts("User objects destroyed.");
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        private class UserPreferences
        {
            public UserMapLayer VisibleMapLayers { get; private set; }

            public void ShowMapLayer(UserMapLayer layer)
            {
                VisibleMapLayers |= layer;
            }

            public void HideMapLayer(UserMapLayer layer)
            {
                VisibleMapLayers &= ~layer;
            }

            public void ToggleMapLayer(UserMapLayer layer)
            {
                if (IsMapLayerVisible(layer))
                    HideMapLayer(layer);
                else
                    ShowMapLayer(layer);
            }

            public bool IsMapLayerVisible(UserMapLayer layer)
            {
                return (VisibleMapLayers & layer) == layer;
            }

            public static UserPreferences Default
            {
                get
                {
                    return new UserPreferences
                    {
                        VisibleMapLayers = UserMapLayer.Claims | UserMapLayer.Headquarters | UserMapLayer.Monuments |
                                           UserMapLayer.Pins
                    };
                }
            }
        }

        [Flags]
        private enum UserMapLayer
        {
            Claims = 1,
            Headquarters = 2,
            Monuments = 4,
            Pins = 8
        }
    }
}
#endregion

#region > War
namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    public partial class Imperium
    {
        private class War
        {

            public string AttackerId { get; set; }
            public string DefenderId { get; set; }
            public string DeclarerId { get; set; }
            public string CassusBelli { get; set; }

            public DateTime? AttackerPeaceOfferingTime { get; set; }
            public DateTime? DefenderPeaceOfferingTime { get; set; }

            public DateTime StartTime { get; private set; }
            public DateTime? EndTime { get; set; }
            public WarEndReason? EndReason { get; set; }

            public bool AdminApproved { get; set; }

            public bool DefenderApproved { get; set; }

            public bool IsActive
            {
                get { return EndTime == null && AdminApproved && DefenderApproved; }
            }

            public bool IsAttackerOfferingPeace
            {
                get { return AttackerPeaceOfferingTime != null; }
            }

            public bool IsDefenderOfferingPeace
            {
                get { return DefenderPeaceOfferingTime != null; }
            }

            public War(Faction attacker, Faction defender, User declarer, string cassusBelli)
            {
                AttackerId = attacker.Id;
                DefenderId = defender.Id;
                DeclarerId = declarer.Id;
                AdminApproved = !Instance.Options.War.AdminApprovalRequired;
                DefenderApproved = !Instance.Options.War.DefenderApprovalRequired;
                CassusBelli = cassusBelli;
                StartTime = DateTime.UtcNow;
            }

            public War(WarInfo info)
            {
                AttackerId = info.AttackerId;
                DefenderId = info.DefenderId;
                DeclarerId = info.DeclarerId;
                CassusBelli = info.CassusBelli;
                AdminApproved = info.AdminApproved;
                DefenderApproved = info.DefenderApproved;
                AttackerPeaceOfferingTime = info.AttackerPeaceOfferingTime;
                DefenderPeaceOfferingTime = info.DefenderPeaceOfferingTime;
                StartTime = info.StartTime;
                EndTime = info.EndTime;
            }

            public void OfferPeace(Faction faction)
            {
                if (AttackerId == faction.Id)
                    AttackerPeaceOfferingTime = DateTime.UtcNow;
                else if (DefenderId == faction.Id)
                    DefenderPeaceOfferingTime = DateTime.UtcNow;
                else
                    throw new InvalidOperationException(String.Format(
                        "{0} tried to offer peace but the faction wasn't involved in the war!", faction.Id));
            }

            public bool IsOfferingPeace(Faction faction)
            {
                return IsOfferingPeace(faction.Id);
            }

            public bool IsOfferingPeace(string factionId)
            {
                return (factionId == AttackerId && IsAttackerOfferingPeace) ||
                       (factionId == DefenderId && IsDefenderOfferingPeace);
            }

            public WarInfo Serialize()
            {
                return new WarInfo
                {
                    AttackerId = AttackerId,
                    DefenderId = DefenderId,
                    DeclarerId = DeclarerId,
                    CassusBelli = CassusBelli,
                    AdminApproved = AdminApproved,
                    DefenderApproved = DefenderApproved,
                    AttackerPeaceOfferingTime = AttackerPeaceOfferingTime,
                    DefenderPeaceOfferingTime = DefenderPeaceOfferingTime,
                    StartTime = StartTime,
                    EndTime = EndTime,
                    EndReason = EndReason
                };
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium : RustPlugin
    {
        private enum WarEndReason
        {
            Treaty,
            AttackerEliminatedDefender,
            DefenderEliminatedAttacker,
            AdminDenied,
            DefenderDenied
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        private enum WarPhase
        {
            Preparation,
            Combat,
            Raiding
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;
    using Newtonsoft.Json.Linq;

    public partial class Imperium : RustPlugin
    {
        private class WarInfo
        {
            [JsonProperty("attackerId")] public string AttackerId;

            [JsonProperty("defenderId")] public string DefenderId;

            [JsonProperty("declarerId")] public string DeclarerId;

            [JsonProperty("cassusBelli")] public string CassusBelli;

            [JsonProperty("adminApproved")] public bool AdminApproved;

            [JsonProperty("defenderApproved")] public bool DefenderApproved;

            [JsonProperty("attackerPeaceOfferingTime"), JsonConverter(typeof(IsoDateTimeConverter))]
            public DateTime? AttackerPeaceOfferingTime;

            [JsonProperty("defenderPeaceOfferingTime"), JsonConverter(typeof(IsoDateTimeConverter))]
            public DateTime? DefenderPeaceOfferingTime;

            [JsonProperty("startTime"), JsonConverter(typeof(IsoDateTimeConverter))]
            public DateTime StartTime;

            [JsonProperty("endTime"), JsonConverter(typeof(IsoDateTimeConverter))]
            public DateTime? EndTime;

            [JsonProperty("endReason"), JsonConverter(typeof(StringEnumConverter))]
            public WarEndReason? EndReason;
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public partial class Imperium
    {
        private class WarManager
        {
            private List<War> Wars = new List<War>();

            public War[] GetAllActiveWars()
            {
                return Wars.Where(war => war.IsActive).OrderBy(war => war.StartTime).ToArray();
            }

            public War[] GetAllInactiveWars()
            {
                return Wars.Where(war => !war.IsActive).OrderBy(war => war.StartTime).ToArray();
            }

            public War[] GetAllActiveWarsByFaction(Faction faction)
            {
                return GetAllActiveWarsByFaction(faction.Id);
            }

            public War[] GetAllAdminUnnaprovedWars()
            {
                return GetAllInactiveWars().Where(war => !war.AdminApproved && war.EndTime == null)
                    .ToArray();
            }

            public War[] GetAllUnapprovedWarsByFaction(Faction faction)
            {
                return GetAllInactiveWars().Where(war => !war.IsActive && war.EndTime == null)
                    .ToArray();
            }

            public War[] GetAllActiveWarsByFaction(string factionId)
            {
                return GetAllActiveWars().Where(war => war.AttackerId == factionId || war.DefenderId == factionId)
                    .ToArray();
            }

            public War GetActiveWarBetween(Faction firstFaction, Faction secondFaction)
            {
                return GetActiveWarBetween(firstFaction.Id, secondFaction.Id);
            }

            public War GetActiveWarBetween(string firstFactionId, string secondFactionId)
            {
                return GetAllActiveWars().SingleOrDefault(war =>
                    (war.AttackerId == firstFactionId && war.DefenderId == secondFactionId) ||
                    (war.DefenderId == firstFactionId && war.AttackerId == secondFactionId)
                );
            }

            public bool AreFactionsAtWar(Faction firstFaction, Faction secondFaction)
            {
                return AreFactionsAtWar(firstFaction.Id, secondFaction.Id);
            }

            public bool AreFactionsAtWar(string firstFactionId, string secondFactionId)
            {
                return GetActiveWarBetween(firstFactionId, secondFactionId) != null;
            }

            public War DeclareWar(Faction attacker, Faction defender, User user, string cassusBelli)
            {
                var war = new War(attacker, defender, user, cassusBelli);
                Wars.Add(war);
                Instance.OnDiplomacyChanged();
                if (war.IsActive)
                {
                    Util.BroadcastEffect("assets/prefabs/missions/effects/mission_accept.prefab");
                }
                return war;
            }

            public bool TryShopfrontTreaty(BasePlayer player1, BasePlayer player2)
            {
                if (!Instance.Options.War.EnableShopfrontPeace)
                    return false;

                var user1 = Instance.Users.Get(player1);
                var user2 = Instance.Users.Get(player2);

                if (user1 == null || user2 == null)
                    return false;
                if (user1.Faction == null || user2.Faction == null)
                    return false;
                if (!Instance.Wars.AreFactionsAtWar(user1.Faction, user2.Faction))
                    return false;
                if (!user1.Faction.HasLeader(user1) || !user2.Faction.HasLeader(user2))
                    return false;
                EndWar(GetActiveWarBetween(user1.Faction, user2.Faction), WarEndReason.Treaty);
                Util.PrintToChat(nameof(Messages.WarEndedTreatyAcceptedAnnouncement), user1.Faction.Id, user2.Faction.Id);
                Instance.Log($"{Util.Format(user1)} and {Util.Format(user2)} accepted the peace by trading on a shop front");
                return true;
            }

            public void AdminApproveWar(War war)
            {
                war.AdminApproved = true;
                Instance.OnDiplomacyChanged();
                if (war.IsActive)
                {
                    Util.BroadcastEffect("assets/prefabs/missions/effects/mission_accept.prefab");
                }
            }

            public void AdminDenyeWar(War war)
            {
                war.AdminApproved = false;
                EndWar(war, WarEndReason.AdminDenied);
            }

            public void DefenderApproveWar(War war)
            {
                war.DefenderApproved = true;
                Instance.OnDiplomacyChanged();
                if (war.IsActive)
                {
                    Util.BroadcastEffect("assets/prefabs/missions/effects/mission_accept.prefab");
                }
            }

            public void DefenderDenyWar(War war)
            {
                war.DefenderApproved = false;
                EndWar(war, WarEndReason.DefenderDenied);
            }

            public void EndWar(War war, WarEndReason reason)
            {
                war.EndTime = DateTime.UtcNow;
                war.EndReason = reason;
                Instance.OnDiplomacyChanged();
            }

            public void EndAllWarsForEliminatedFactions()
            {
                bool dirty = false;

                foreach (War war in Wars)
                {
                    if (Instance.Areas.GetAllClaimedByFaction(war.AttackerId).Length == 0)
                    {
                        war.EndTime = DateTime.UtcNow;
                        war.EndReason = WarEndReason.DefenderEliminatedAttacker;
                        dirty = true;
                    }

                    if (Instance.Areas.GetAllClaimedByFaction(war.DefenderId).Length == 0)
                    {
                        war.EndTime = DateTime.UtcNow;
                        war.EndReason = WarEndReason.AttackerEliminatedDefender;
                        dirty = true;
                    }
                }

                if (dirty)
                    Instance.OnDiplomacyChanged();
            }

            public void Init(IEnumerable<WarInfo> warInfos)
            {
                Instance.Puts($"Loading {warInfos.Count()} wars...");

                foreach (WarInfo info in warInfos)
                {
                    var war = new War(info);
                    Wars.Add(war);
                    Instance.Log($"[LOAD] War {war.AttackerId} vs {war.DefenderId}, isActive = {war.IsActive}");
                }

                Instance.Puts("Wars loaded.");
            }
            public void Destroy()
            {
                Wars.Clear();
            }

            public WarInfo[] Serialize()
            {
                return Wars.Select(war => war.Serialize()).ToArray();
            }
        }
    }
}
#endregion

#region > Zone
namespace Oxide.Plugins
{
    using Rust;
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    public partial class Imperium
    {
        private class Zone : MonoBehaviour
        {
            private const string SpherePrefab = "assets/prefabs/visualization/sphere.prefab";
            private List<BaseEntity> Spheres = new List<BaseEntity>();

            public ZoneType Type { get; private set; }
            public string Name { get; private set; }
            public MonoBehaviour Owner { get; private set; }
            public DateTime? EndTime { get; set; }

            public void Init(ZoneType type, string name, MonoBehaviour owner, float radius, int darkness,
                DateTime? endTime)
            {
                Type = type;
                Name = name;
                Owner = owner;
                EndTime = endTime;

                Vector3 position = GetGroundPosition(owner.transform.position);

                gameObject.layer = (int)Layer.Reserved1;
                gameObject.name = $"imperium_zone_{name.ToLowerInvariant()}";
                transform.position = position;
                transform.rotation = Quaternion.Euler(new Vector3(0, 0, 0));

                for (var idx = 0; idx < darkness; idx++)
                {
                    var sphere = GameManager.server.CreateEntity(SpherePrefab, position);

                    SphereEntity entity = sphere.GetComponent<SphereEntity>();
                    entity.lerpRadius = radius * 2;
                    entity.currentRadius = radius * 2;
                    entity.lerpSpeed = 0f;

                    sphere.Spawn();
                    Spheres.Add(sphere);
                }

                var collider = gameObject.AddComponent<SphereCollider>();
                collider.radius = radius;
                collider.isTrigger = true;
                collider.enabled = true;

                //Pin this zone to cargo ship so it follows it
                if (type == ZoneType.CargoShip)
                {
                    transform.SetParent(owner.transform, false);
                }

                if (endTime != null)
                    InvokeRepeating("CheckIfShouldDestroy", 10f, 5f);
            }

            private void OnDestroy()
            {
                var collider = GetComponent<SphereCollider>();

                if (collider != null)
                    Destroy(collider);

                foreach (BaseEntity sphere in Spheres)
                    sphere.KillMessage();

                if (IsInvoking("CheckIfShouldDestroy"))
                    CancelInvoke("CheckIfShouldDestroy");
            }

            private void OnTriggerEnter(Collider collider)
            {
                if (collider.gameObject.layer != (int)Layer.Player_Server)
                    return;

                var user = collider.GetComponentInParent<User>();

                if (user != null && !user.CurrentZones.Contains(this))
                    Events.OnUserEnteredZone(user, this);
            }

            private void OnTriggerExit(Collider collider)
            {
                if (collider.gameObject.layer != (int)Layer.Player_Server)
                    return;

                var user = collider.GetComponentInParent<User>();

                if (user != null && user.CurrentZones.Contains(this))
                    Events.OnUserLeftZone(user, this);
            }

            private void CheckIfShouldDestroy()
            {
                if (DateTime.UtcNow >= EndTime)
                    Instance.Zones.Remove(this);
            }

            private Vector3 GetGroundPosition(Vector3 pos)
            {
                return new Vector3(pos.x, TerrainMeta.HeightMap.GetHeight(pos), pos.z);
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using UnityEngine;

    public partial class Imperium
    {
        private class ZoneManager
        {
            private Dictionary<MonoBehaviour, Zone> Zones = new Dictionary<MonoBehaviour, Zone>();

            public void Init()
            {
                if (!Instance.Options.Zones.Enabled || Instance.Options.Zones.MonumentZones == null)
                    return;

                MonumentInfo[] monuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
                foreach (MonumentInfo monument in monuments)
                {
                    float? radius = GetMonumentZoneRadius(monument);
                    if (radius != null)
                    {
                        Vector3 position = monument.transform.position;
                        Vector3 size = monument.Bounds.size;
                        Create(ZoneType.Monument, monument.displayPhrase.english, monument, (float)radius);
                    }
                }
            }

            public Zone GetByOwner(MonoBehaviour owner)
            {
                Zone zone;

                if (Zones.TryGetValue(owner, out zone))
                    return zone;

                return null;
            }

            public void CreateForDebrisField(BaseHelicopter helicopter)
            {
                Vector3 position = helicopter.transform.position;
                float radius = Instance.Options.Zones.EventZoneRadius;
                Create(ZoneType.Debris, "Debris Field", helicopter, radius, GetEventEndTime());
            }

            public void CreateForSupplyDrop(SupplyDrop drop)
            {
                Vector3 position = drop.transform.position;
                float radius = Instance.Options.Zones.EventZoneRadius;
                float lifespan = Instance.Options.Zones.EventZoneLifespanSeconds;
                Create(ZoneType.SupplyDrop, "Supply Drop", drop, radius, GetEventEndTime());
            }

            public void CreateForCargoShip(CargoShip cargoShip)
            {
                Vector3 position = cargoShip.transform.position;
                float radius = Instance.Options.Zones.EventZoneRadius;
                float lifespan = Instance.Options.Zones.EventZoneLifespanSeconds;
                Create(ZoneType.CargoShip, "Cargo Ship", cargoShip, radius, GetEventEndTime());
            }

            public void CreateForRaid(BuildingPrivlidge cupboard)
            {
                // If the building was already being raided, just extend the lifespan of the existing zone.
                Zone existingZone = GetByOwner(cupboard);
                if (existingZone)
                {
                    existingZone.EndTime = GetEventEndTime();
                    Instance.Puts(
                        $"Extending raid zone end time to {existingZone.EndTime} ({existingZone.EndTime.Value.Subtract(DateTime.UtcNow).ToShortString()} from now)");
                    return;
                }

                Vector3 position = cupboard.transform.position;
                float radius = Instance.Options.Zones.EventZoneRadius;

                Create(ZoneType.Raid, "Raid", cupboard, radius, GetEventEndTime());
            }

            public void Remove(Zone zone)
            {
                Instance.Puts($"Destroying zone {zone.name}");

                foreach (User user in Instance.Users.GetAll())
                    user.CurrentZones.Remove(zone);

                Zones.Remove(zone.Owner);

                UnityEngine.Object.Destroy(zone);
            }

            public void Destroy()
            {
                Zone[] zones = UnityEngine.Object.FindObjectsOfType<Zone>();

                if (zones != null)
                {
                    Instance.Puts($"Destroying {zones.Length} zone objects...");
                    foreach (Zone zone in zones)
                        UnityEngine.Object.DestroyImmediate(zone);
                }

                Zones.Clear();

                Instance.Puts("Zone objects destroyed.");
            }

            private void Create(ZoneType type, string name, MonoBehaviour owner, float radius, DateTime? endTime = null)
            {
                var zone = new GameObject().AddComponent<Zone>();
                zone.Init(type, name, owner, radius, Instance.Options.Zones.DomeDarkness, endTime);

                Instance.Puts($"Created zone {zone.Name} at {zone.transform.position} with radius {radius}");

                if (endTime != null)
                    Instance.Puts(
                        $"Zone {zone.Name} will be destroyed at {endTime} ({endTime.Value.Subtract(DateTime.UtcNow).ToShortString()} from now)");

                Zones.Add(owner, zone);
            }

            private float? GetMonumentZoneRadius(MonumentInfo monument)
            {
                if (monument.Type == MonumentType.Cave)
                    return null;

                foreach (var entry in Instance.Options.Zones.MonumentZones)
                {
                    if (monument.name.ToLowerInvariant().Contains(entry.Key))
                        return entry.Value;
                }

                return null;
            }

            private DateTime GetEventEndTime()
            {
                return DateTime.UtcNow.AddSeconds(Instance.Options.Zones.EventZoneLifespanSeconds);
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        public enum ZoneType
        {
            Monument,
            Debris,
            SupplyDrop,
            Raid,
            CargoShip
        }
    }
}
#endregion

#region > Recruit
namespace Oxide.Plugins
{
    using Rust;
    using UnityEngine;
    public partial class Imperium
    {
        public class Recruit : MonoBehaviour
        {
        }
    }
}
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        public class RecruitInfo
        {
            //Existing bots serialized info
        }
    }
}
namespace Oxide.Plugins
{
    public partial class Imperium
    {
        public class RecruitManager
        {
            //Bot management
        }
    }
}
#endregion

#region > Faction Colors
namespace Oxide.Plugins
{
    using System.Collections.Generic;
    using System.Drawing;
    public partial class Imperium
    {
        private class FactionColorPicker
        {
            private static string[] Colors = new[]
            {
                "#00FF00", "#0000FF", "#FF0000", "#01FFFE", "#FFA6FE",
                "#FFDB66", "#006401", "#010067", "#95003A", "#007DB5",
                "#FF00F6", "#FFEEE8", "#774D00", "#90FB92", "#0076FF",
                "#D5FF00", "#FF937E", "#6A826C", "#FF029D", "#FE8900",
                "#7A4782", "#7E2DD2", "#85A900", "#FF0056", "#A42400",
                "#00AE7E", "#683D3B", "#BDC6FF", "#263400", "#BDD393",
                "#00B917", "#9E008E", "#001544", "#C28C9F", "#FF74A3",
                "#01D0FF", "#004754", "#E56FFE", "#788231", "#0E4CA1",
                "#91D0CB", "#BE9970", "#968AE8", "#BB8800", "#43002C",
                "#DEFF74", "#00FFC6", "#FFE502", "#620E00", "#008F9C",
                "#98FF52", "#7544B1", "#B500FF", "#00FF78", "#FF6E41",
                "#005F39", "#6B6882", "#5FAD4E", "#A75740", "#A5FFD2",
                "#FFB167", "#009BFF", "#E85EBE"
            };
            private Dictionary<string, Color> AssignedColors;
            private int NextColor = 0;

            public FactionColorPicker()
            {
                AssignedColors = new Dictionary<string, Color>();
            }

            public Color GetColorForFaction(string factionId)
            {
                Color color;

                if (!AssignedColors.TryGetValue(factionId, out color))
                {
                    color = Color.FromArgb(128, ColorTranslator.FromHtml(Colors[NextColor]));
                    AssignedColors.Add(factionId, color);
                    NextColor = (NextColor + 1) % Colors.Length;
                }

                return color;
            }

            public string GetHexColorForFaction(string factionId)
            {
                string hexcolor;
                Color color;

                if (!AssignedColors.TryGetValue(factionId, out color))
                {
                    color = Color.FromArgb(128, ColorTranslator.FromHtml(Colors[NextColor]));
                    AssignedColors.Add(factionId, color);
                    NextColor = (NextColor + 1) % Colors.Length;
                }
                hexcolor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

                return hexcolor;
            }
        }
    }
}
#endregion

#region > Map
namespace Oxide.Plugins
{
    using System;
    using UnityEngine;

    public partial class Imperium
    {
        public class MapGrid
        {
            public float GRID_CELL_SIZE = 146.3f;

            public float MapSize
            {
                get; set;
            }
            public float MapWidth
            {
                get; set;
            }

            public float MapHeight
            {
                get; set;
            }
            public float MapOffsetX
            {
                get; set;
            }

            public float MapOffsetZ
            {
                get; set;
            }

            public float CellSize
            {
                get { return GRID_CELL_SIZE; }
            }

            public float CellSizeRatio
            {
                get { return (float)MapSize / CellSize; }
            }

            public float CellSizeRatioWidth
            {
                get { return (float)MapWidth / CellSize; }
            }

            public float CellSizeRatioHeight
            {
                get { return (float)MapHeight / CellSize; }
            }

            public int NumberOfCells { get; private set; }
            public int NumberOfColumns { get; private set; }
            public int NumberOfRows { get; private set; }

            private string[] RowIds;
            private string[] ColumnIds;
            private string[,] AreaIds;
            private Vector3[,] Positions;

            public MapGrid()
            {

                MapSize = Mathf.Floor(TerrainMeta.Size.x / CellSize) * CellSize;
                MapWidth = Mathf.Floor(TerrainMeta.Size.x / CellSize) * CellSize;
                MapHeight = Mathf.Floor(TerrainMeta.Size.z / CellSize) * CellSize;


                NumberOfRows = (int)Math.Floor(MapHeight / (float)CellSize);
                NumberOfColumns = (int)Math.Floor(MapWidth / (float)CellSize);

                MapWidth = NumberOfColumns * CellSize;
                MapHeight = NumberOfRows * CellSize;

                MapOffsetX = TerrainMeta.Size.x - (NumberOfColumns * CellSize);
                MapOffsetZ = TerrainMeta.Size.z - (NumberOfRows * CellSize);
                RowIds = new string[NumberOfRows];
                ColumnIds = new string[NumberOfColumns];
                AreaIds = new string[NumberOfColumns, NumberOfRows];
                Positions = new Vector3[NumberOfColumns, NumberOfRows];
                Build();
            }

            public string GetRowId(int row)
            {
                return RowIds[row];
            }

            public string GetColumnId(int col)
            {
                return ColumnIds[col];
            }

            public string GetAreaId(int row, int col)
            {
                return AreaIds[row, col];
            }

            public Vector3 GetPosition(int row, int col)
            {
                return Positions[row, col];
            }

            private void Build()
            {
                string prefix = "";
                char letter = 'A';

                for (int col = 0; col < NumberOfColumns; col++)
                {
                    ColumnIds[col] = prefix + letter;
                    if (letter == 'Z')
                    {
                        prefix = "A";
                        letter = 'A';
                    }
                    else
                    {
                        letter++;
                    }
                }

                for (int row = 0; row < NumberOfRows; row++)
                    RowIds[row] = row.ToString();

                float z = (MapHeight / 2) - CellSize / 2 - (MapOffsetZ / 2) + (CellSize * Instance.Options.Map.MapGridYOffset);
                for (int row = 0; row < NumberOfRows; row++)
                {
                    float x = -(MapWidth / 2) + CellSize / 2 - (MapOffsetX / 2);
                    for (int col = 0; col < NumberOfColumns; col++)
                    {
                        var areaId = ColumnIds[col] + RowIds[row];
                        AreaIds[row, col] = areaId;
                        Positions[row, col] = new Vector3(x, 0, z);
                        x += CellSize;
                    }

                    z -= CellSize;
                }
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System.Collections;
    using System.Drawing;
    using System.Drawing.Drawing2D;

    public partial class Imperium
    {
        private class MapOverlayGenerator : UnityEngine.MonoBehaviour
        {
            public bool IsGenerating { get; private set; }

            public void Generate()
            {
                if (!IsGenerating)
                    StartCoroutine(GenerateOverlayImage());
            }

            private IEnumerator GenerateOverlayImage()
            {
                IsGenerating = true;
                Instance.Puts("Generating new map overlay image...");

                using (var bitmap = new Bitmap(Instance.Options.Map.ImageSize, Instance.Options.Map.ImageSize))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var grid = Instance.Areas.MapGrid;
                    var tileSize = (int)(Instance.Options.Map.ImageSize / grid.CellSizeRatio);

                    var colorPicker = new FactionColorPicker();
                    var textBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));

                    for (int row = 0; row < grid.NumberOfRows; row++)
                    {
                        for (int col = 0; col < grid.NumberOfColumns; col++)
                        {
                            Area area = Instance.Areas.Get(row, col);
                            var x = (col * tileSize);
                            var y = (row * tileSize);
                            var rect = new Rectangle(x, y, tileSize, tileSize);

                            if (area.Type == AreaType.Badlands)
                            {
                                // If the tile is badlands, color it in black.
                                var brush = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(32, 0, 0, 0),
                                    Color.FromArgb(255, 0, 0, 0));
                                graphics.FillRectangle(brush, rect);
                            }
                            else if (area.Type != AreaType.Wilderness)
                            {
                                // If the tile is claimed, fill it with a color indicating the faction.
                                var brush = new SolidBrush(colorPicker.GetColorForFaction(area.FactionId));
                                graphics.FillRectangle(brush, rect);
                            }

                            yield return null;
                        }
                    }

                    var gridLabelFont = new Font("Consolas", 14, FontStyle.Bold);
                    var gridLabelOffset = 5;
                    var gridLinePen = new Pen(Color.FromArgb(192, 0, 0, 0), 2);

                    for (int row = 0; row < grid.NumberOfRows; row++)
                    {
                        if (row > 0)
                            graphics.DrawLine(gridLinePen, 0, (row * tileSize), (grid.NumberOfRows * tileSize),
                                (row * tileSize));
                        graphics.DrawString(grid.GetRowId(row), gridLabelFont, textBrush, gridLabelOffset,
                            (row * tileSize) + gridLabelOffset);
                    }

                    for (int col = 1; col < grid.NumberOfColumns; col++)
                    {
                        graphics.DrawLine(gridLinePen, (col * tileSize), 0, (col * tileSize),
                            (grid.NumberOfColumns * tileSize));
                        graphics.DrawString(grid.GetColumnId(col), gridLabelFont, textBrush,
                            (col * tileSize) + gridLabelOffset, gridLabelOffset);
                    }

                    var converter = new ImageConverter();
                    var imageData = (byte[])converter.ConvertTo(bitmap, typeof(byte[]));

                    Image image = Instance.Hud.RegisterImage(UI.MapOverlayImageUrl, imageData, true);

                    Instance.Puts($"Generated new map overlay image {image.Id}.");
                    Instance.Log($"Created new map overlay image {image.Id}.");

                    IsGenerating = false;
                }
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        public static class MonumentPrefab
        {
            private const string PrefabPrefix = "assets/bundled/prefabs/autospawn/monument/";

            public const string Airfield = PrefabPrefix + "large/airfield_1.prefab";
            public const string BanditTown = PrefabPrefix + "medium/bandit_town.prefab";
            public const string Compound = PrefabPrefix + "medium/compound.prefab";
            public const string Dome = PrefabPrefix + "small/sphere_tank.prefab";
            public const string Harbor1 = PrefabPrefix + "harbor/harbor_1.prefab";
            public const string Harbor2 = PrefabPrefix + "harbor/harbor_2.prefab";
            public const string GasStation = PrefabPrefix + "small/gas_station_1.prefab";
            public const string Junkyard = PrefabPrefix + "large/junkyard_1.prefab";
            public const string LaunchSite = PrefabPrefix + "large/launch_site_1.prefab";
            public const string Lighthouse = PrefabPrefix + "lighthouse/lighthouse.prefab";
            public const string MilitaryTunnel = PrefabPrefix + "large/military_tunnel_1.prefab";
            public const string MiningOutpost = PrefabPrefix + "small/warehouse.prefab";
            public const string QuarryStone = PrefabPrefix + "small/mining_quarry_a.prefab";
            public const string QuarrySulfur = PrefabPrefix + "small/mining_quarry_b.prefab";
            public const string QuaryHqm = PrefabPrefix + "small/mining_quarry_c.prefab";
            public const string PowerPlant = PrefabPrefix + "large/powerplant_1.prefab";
            public const string Trainyard = PrefabPrefix + "large/trainyard_1.prefab";
            public const string SatelliteDish = PrefabPrefix + "small/satellite_dish.prefab";
            public const string SewerBranch = PrefabPrefix + "medium/radtown_small_3.prefab";
            public const string Supermarket = PrefabPrefix + "small/supermarket_1.prefab";
            public const string WaterTreatmentPlant = PrefabPrefix + "large/water_treatment_plant_1.prefab";
            public const string WaterWellA = PrefabPrefix + "tiny/water_well_a.prefab";
            public const string WaterWellB = PrefabPrefix + "tiny/water_well_b.prefab";
            public const string WaterWellC = PrefabPrefix + "tiny/water_well_c.prefab";
            public const string WaterWellD = PrefabPrefix + "tiny/water_well_d.prefab";
            public const string WaterWellE = PrefabPrefix + "tiny/water_well_e.prefab";
        }
    }
}
#endregion

#region > Permissions
namespace Oxide.Plugins
{
    using System.Reflection;

    public partial class Imperium : RustPlugin
    {
        public static class Permission
        {
            public const string AdminFactions = "imperium.factions.admin";
            public const string AdminClaims = "imperium.claims.admin";
            public const string AdminBadlands = "imperium.badlands.admin";
            public const string AdminWars = "imperium.wars.admin";
            public const string AdminPins = "imperium.pins.admin";
            public const string ManageFactions = "imperium.factions";

            public static void RegisterAll(Imperium instance)
            {
                foreach (FieldInfo field in typeof(Permission).GetFields(BindingFlags.Public | BindingFlags.Static))
                    instance.permission.RegisterPermission((string)field.GetRawConstantValue(), instance);
            }
        }
    }
}
#endregion

#region > Utilities
namespace Oxide.Plugins
{
    using System;
    using Newtonsoft.Json;
    using UnityEngine;

    public partial class Imperium : RustPlugin
    {
        private class UnityVector3Converter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var vector = (Vector3)value;
                writer.WriteValue($"{vector.x} {vector.y} {vector.z}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                JsonSerializer serializer)
            {
                string[] tokens = reader.Value.ToString().Trim().Split(' ');
                float x = Convert.ToSingle(tokens[0]);
                float y = Convert.ToSingle(tokens[1]);
                float z = Convert.ToSingle(tokens[2]);
                return new Vector3(x, y, z);
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Vector3);
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections;
    using System.Text;
    using UnityEngine;

    public partial class Imperium
    {
        private static class Util
        {
            private const string NullString = "(null)";

            public static string Format(object obj)
            {
                if (obj == null) return NullString;

                var user = obj as User;
                if (user != null) return Format(user);

                var area = obj as Area;
                if (area != null) return Format(area);

                var entity = obj as BaseEntity;
                if (entity != null) return Format(entity);

                var list = obj as IEnumerable;
                if (list != null) return Format(list);

                return obj.ToString();
            }

            public static string Format(User user)
            {
                if (user == null)
                    return NullString;
                else
                    return $"{user.UserName} ({user.Id})";
            }

            public static string Format(Area area)
            {
                if (area == null)
                    return NullString;
                else if (!String.IsNullOrEmpty(area.Name))
                    return $"{area.Id} ({area.Name})";
                else
                    return area.Id;
            }

            public static string Format(BaseEntity entity)
            {
                if (entity == null)
                    return NullString;
                else if (entity.net == null)
                    return "(missing networkable)";
                else
                    return entity.net.ID.ToString();
            }

            public static string Format(IEnumerable items)
            {
                var sb = new StringBuilder();

                foreach (object item in items)
                    sb.Append($"{Format(item)}, ");

                sb.Remove(sb.Length - 2, 2);

                return sb.ToString();
            }

            public static string NormalizeAreaId(string input)
            {
                return input.ToUpper().Trim();
            }

            public static string NormalizeAreaName(string input)
            {
                return RemoveSpecialCharacters(input.Trim());
            }

            public static string NormalizePinName(string input)
            {
                return RemoveSpecialCharacters(input.Trim());
            }

            public static string NormalizeFactionId(string input)
            {
                string factionId = input.Trim();

                if (factionId.StartsWith("[") && factionId.EndsWith("]"))
                    factionId = factionId.Substring(1, factionId.Length - 2);

                return factionId;
            }

            public static string RemoveSpecialCharacters(string str)
            {
                if (String.IsNullOrEmpty(str))
                    return String.Empty;

                StringBuilder sb = new StringBuilder(str.Length);
                foreach (char c in str)
                {
                    if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                        (c >= 'А' && c <= 'Я') || (c >= 'а' && c <= 'я') || c == ' ' || c == '.' || c == '_')
                        sb.Append(c);
                }

                return sb.ToString();
            }

            public static int GetLevenshteinDistance(string source, string target)
            {
                if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(target))
                    return 0;

                if (source.Length == target.Length)
                    return source.Length;

                if (source.Length == 0)
                    return target.Length;

                if (target.Length == 0)
                    return source.Length;

                var distance = new int[source.Length + 1, target.Length + 1];

                for (int idx = 0; idx <= source.Length; distance[idx, 0] = idx++) ;
                for (int idx = 0; idx <= target.Length; distance[0, idx] = idx++) ;

                for (int i = 1; i <= source.Length; i++)
                {
                    for (int j = 1; j <= target.Length; j++)
                    {
                        int cost = target[j - 1] == source[i - 1] ? 0 : 1;
                        distance[i, j] = Math.Min(
                            Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                            distance[i - 1, j - 1] + cost
                        );
                    }
                }

                return distance[source.Length, target.Length];
            }

            public static bool TryParseEnum<T>(string str, out T value) where T : struct
            {
                if (!typeof(T).IsEnum)
                    throw new ArgumentException("Type parameter must be an enum");

                foreach (var name in Enum.GetNames(typeof(T)))
                {
                    if (String.Equals(name, str, StringComparison.OrdinalIgnoreCase))
                    {
                        value = (T)Enum.Parse(typeof(T), name);
                        return true;
                    }
                }

                value = default(T);
                return false;
            }

            public static void RunEffect(Vector3 position, string prefab, BasePlayer player = null)
            {
                var effect = new Effect();
                effect.Init(Effect.Type.Generic, position, Vector3.zero);
                effect.pooledString = prefab;

                if (player != null)
                {
                    EffectNetwork.Send(effect, player.net.connection);
                }
                else
                {
                    EffectNetwork.Send(effect);
                }
            }

            public static void BroadcastEffect(string prefab)
            {
                Vector3 position;
                BasePlayer player;
                foreach (User user in Instance.Users.GetAll())
                {
                    player = user.Player;
                    position = user.transform.position;
                    if (player)
                    {
                        var effect = new Effect();
                        effect.Init(Effect.Type.Generic, position, Vector3.zero);
                        effect.pooledString = prefab;
                        EffectNetwork.Send(effect, player.net.connection);
                    }
                }
            }

            public static void PrintToChat(string format, params object[] args)
            {
                foreach (User user in Instance.Users.GetAll())
                {
                    if (user.Player)
                    {
                        string message = Instance.lang.GetMessage(format, Instance, user.Player.userID.ToString());
                        user.SendChatMessage(message, args);
                    }
                }

            }

            public static int GetSecondsBetween(DateTime start, DateTime end)
            {
                return (int)(start - end).TotalSeconds;
            }

            public static Color ConvertSystemToUnityColor(System.Drawing.Color color)
            {
                Color result;
                result.r = color.R;
                result.g = color.G;
                result.b = color.B;
                result.a = 255f;
                return result;
            }
        }
    }
}
#endregion

#region > Interactions
namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;

    public partial class Imperium
    {
        private class AddingClaimInteraction : Interaction
        {
            public Faction Faction { get; private set; }

            public AddingClaimInteraction(Faction faction)
            {
                Faction = faction;
            }

            public override bool TryComplete(HitInfo hit)
            {
                var cupboard = hit.HitEntity as BuildingPrivlidge;
                Area area = User.CurrentArea;

                if (area == null)
                    return false;

                if (!Instance.EnsureUserCanChangeFactionClaims(User, Faction))
                    return false;

                if (!Instance.EnsureCupboardCanBeUsedForClaim(User, cupboard))
                    return false;

                if (!Instance.EnsureFactionCanClaimArea(User, Faction, area))
                    return false;

                Area[] claimedAreas = Instance.Areas.GetAllClaimedByFaction(Faction);
                AreaType type = (claimedAreas.Length == 0) ? AreaType.Headquarters : AreaType.Claimed;

                if (area.Type == AreaType.Wilderness)
                {
                    int cost = area.GetClaimCost(Faction);

                    if (cost > 0)
                    {
                        ItemDefinition scrapDef = ItemManager.FindItemDefinition("scrap");
                        List<Item> stacks = User.Player.inventory.FindItemsByItemID(scrapDef.itemid);

                        if (!Instance.TryCollectFromStacks(scrapDef, stacks, cost))
                        {
                            User.SendChatMessage(nameof(Messages.CannotClaimAreaCannotAfford), cost);
                            return false;
                        }
                    }

                    User.SendChatMessage(nameof(Messages.ClaimAdded), area.Id);

                    if (type == AreaType.Headquarters)
                    {
                        Util.PrintToChat(nameof(Messages.AreaClaimedAsHeadquartersAnnouncement), Faction.Id, area.Id);
                        Faction.NextUpkeepPaymentTime =
                            DateTime.UtcNow.AddHours(Instance.Options.Upkeep.CollectionPeriodHours);
                    }
                    else
                    {
                        Util.PrintToChat(nameof(Messages.AreaClaimedAnnouncement), Faction.Id, area.Id);
                    }
                    Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                    Instance.Log($"{Util.Format(User)} claimed {area.Id} on behalf of {Faction.Id}");
                    Instance.Areas.Claim(area, type, Faction, User, cupboard);

                    return true;
                }

                if (area.FactionId == Faction.Id)
                {
                    if (area.ClaimCupboard.net.ID == cupboard.net.ID)
                    {
                        User.SendChatMessage(nameof(Messages.CannotClaimAreaAlreadyOwned), area.Id);
                        return false;
                    }
                    else
                    {
                        // If the same faction claims a new cupboard within the same area, move the claim to the new cupboard.
                        User.SendChatMessage(nameof(Messages.ClaimCupboardMoved), area.Id);
                        Instance.Log(
                            $"{Util.Format(User)} moved {area.FactionId}'s claim on {area.Id} from cupboard {Util.Format(area.ClaimCupboard)} to cupboard {Util.Format(cupboard)}");
                        area.ClaimantId = User.Id;
                        area.ClaimCupboard = cupboard;
                        Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                        return true;
                    }
                }

                if (area.FactionId != Faction.Id)
                {
                    if (area.ClaimCupboard.net.ID != cupboard.net.ID)
                    {
                        // A new faction can't make a claim on a new cabinet within an area that is already claimed by another faction.
                        User.SendChatMessage(nameof(Messages.CannotClaimAreaAlreadyClaimed), area.Id, area.FactionId);
                        return false;
                    }

                    string previousFactionId = area.FactionId;

                    // If a new faction claims the claim cabinet for an area, they take control of that area.
                    User.SendChatMessage(nameof(Messages.ClaimCaptured), area.Id, area.FactionId);
                    Util.PrintToChat(nameof(Messages.AreaCapturedAnnouncement), Faction.Id, area.Id, area.FactionId);
                    Instance.Log(
                        $"{Util.Format(User)} captured the claim on {area.Id} from {area.FactionId} on behalf of {Faction.Id}");

                    Instance.Areas.Claim(area, type, Faction, User, cupboard);
                    Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                    return true;
                }

                Instance.PrintWarning(
                    "Area was in an unknown state during completion of AddingClaimInteraction. This shouldn't happen.");
                return false;
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private class AssigningClaimInteraction : Interaction
        {
            public Faction Faction { get; private set; }

            public AssigningClaimInteraction(Faction faction)
            {
                Faction = faction;
            }

            public override bool TryComplete(HitInfo hit)
            {
                var cupboard = hit.HitEntity as BuildingPrivlidge;

                Area area = User.CurrentArea;

                if (area == null)
                {
                    User.SendChatMessage(nameof(Messages.YouAreInTheGreatUnknown));
                    return false;
                }

                if (area.Type == AreaType.Badlands)
                {
                    User.SendChatMessage(nameof(Messages.AreaIsBadlands), area.Id);
                    return false;
                }

                Area[] ownedAreas = Instance.Areas.GetAllClaimedByFaction(Faction);
                AreaType type = (ownedAreas.Length == 0) ? AreaType.Headquarters : AreaType.Claimed;

                Util.PrintToChat(nameof(Messages.AreaClaimAssignedAnnouncement), Faction.Id, area.Id);
                Instance.Log($"{Util.Format(User)} assigned {area.Id} to {Faction.Id}");

                Instance.Areas.Claim(area, type, Faction, User, cupboard);
                Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                return true;
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private abstract class Interaction
        {
            public User User { get; set; }
            public abstract bool TryComplete(HitInfo hit);
        }
    }
}

namespace Oxide.Plugins
{
    using System.Linq;
    public partial class Imperium
    {
        private class RemovingClaimInteraction : Interaction
        {
            public Faction Faction { get; private set; }

            public RemovingClaimInteraction(Faction faction)
            {
                Faction = faction;
            }

            public override bool TryComplete(HitInfo hit)
            {
                var cupboard = hit.HitEntity as BuildingPrivlidge;
                var fAreas = Instance.Areas.GetAllClaimedByFaction(Faction).ToList();
                if (!Instance.EnsureUserCanChangeFactionClaims(User, Faction) ||
                    !Instance.EnsureCupboardCanBeUsedForClaim(User, cupboard))
                    return false;

                Area area = Instance.Areas.GetByClaimCupboard(cupboard);

                if (area == null)
                {
                    User.SendChatMessage(nameof(Messages.SelectingCupboardFailedNotClaimCupboard));
                    return false;
                }



                if (fAreas.Count > 1 && area.Type == AreaType.Headquarters)
                {
                    User.SendChatMessage(nameof(Messages.SelectingCupboardFailedCantUnclaimHeadquarters));
                    return false;
                }

                Util.PrintToChat(nameof(Messages.AreaClaimRemovedAnnouncement), Faction.Id, area.Id);
                Instance.Log($"{Util.Format(User)} removed {Faction.Id}'s claim on {area.Id}");

                Instance.Areas.Unclaim(area);
                Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                return true;
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private class SelectingHeadquartersInteraction : Interaction
        {
            public Faction Faction { get; private set; }

            public SelectingHeadquartersInteraction(Faction faction)
            {
                Faction = faction;
            }

            public override bool TryComplete(HitInfo hit)
            {
                var cupboard = hit.HitEntity as BuildingPrivlidge;

                if (!Instance.EnsureUserCanChangeFactionClaims(User, Faction) ||
                    !Instance.EnsureCupboardCanBeUsedForClaim(User, cupboard))
                    return false;

                Area area = Instance.Areas.GetByClaimCupboard(cupboard);
                if (area == null)
                {
                    User.SendChatMessage(nameof(Messages.SelectingCupboardFailedNotClaimCupboard));
                    return false;
                }

                Util.PrintToChat(nameof(Messages.HeadquartersChangedAnnouncement), Faction.Id, area.Id);
                Instance.Log($"{Util.Format(User)} set {Faction.Id}'s headquarters to {area.Id}");

                Instance.Areas.SetHeadquarters(area, Faction);
                Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                return true;
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private class SelectingTaxChestInteraction : Interaction
        {
            public Faction Faction { get; private set; }

            public SelectingTaxChestInteraction(Faction faction)
            {
                Faction = faction;
            }

            public override bool TryComplete(HitInfo hit)
            {
                var container = hit.HitEntity as StorageContainer;

                if (container == null)
                {
                    User.SendChatMessage(nameof(Messages.SelectingTaxChestFailedInvalidTarget));
                    return false;
                }

                User.SendChatMessage(nameof(Messages.SelectingTaxChestSucceeded), Faction.TaxRate * 100, Faction.Id);
                Instance.Log($"{Util.Format(User)} set {Faction.Id}'s tax chest to entity {Util.Format(container)}");
                Instance.Factions.SetTaxChest(Faction, container);
                Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                return true;
            }
        }
    }
}

namespace Oxide.Plugins
{
    public partial class Imperium
    {
        private class SelectingArmoryLockerInteraction : Interaction
        {
            public Faction Faction { get; private set; }

            public SelectingArmoryLockerInteraction(Faction faction)
            {
                Faction = faction;
            }

            public override bool TryComplete(HitInfo hit)
            {
                var container = hit.HitEntity as Locker;
                if (container == null)
                {
                    User.SendChatMessage(nameof(Messages.SelectingArmoryLockerFailedInvalidTarget));
                    return false;
                }
                var area = Instance.Areas.GetByEntityPosition(container);
                if (!Instance.EnsureLockerCanBeUsedForArmory(User, container, area))
                    return false;
                User.SendChatMessage(nameof(Messages.SelectingArmoryLockerSucceeded), area.Id);
                Instance.Log($"{Util.Format(User)} set {Faction.Id}'s armory locker to entity {Util.Format(container)} at {area.Id}");
                Instance.Areas.SetArmory(area, container);
                Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                return true;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System.Linq;
    public partial class Imperium
    {
        private class TransferringClaimInteraction : Interaction
        {
            public Faction SourceFaction { get; }
            public Faction TargetFaction { get; }

            public TransferringClaimInteraction(Faction sourceFaction, Faction targetFaction)
            {
                SourceFaction = sourceFaction;
                TargetFaction = targetFaction;
            }

            public override bool TryComplete(HitInfo hit)
            {
                var cupboard = hit.HitEntity as BuildingPrivlidge;
                var fAreas = Instance.Areas.GetAllClaimedByFaction(SourceFaction).ToList();
                if (!Instance.EnsureUserCanChangeFactionClaims(User, SourceFaction) ||
                    !Instance.EnsureCupboardCanBeUsedForClaim(User, cupboard))
                    return false;

                Area area = Instance.Areas.GetByClaimCupboard(cupboard);

                if (area == null)
                {
                    User.SendChatMessage(nameof(Messages.SelectingCupboardFailedNotClaimCupboard));
                    return false;
                }

                if (area.FactionId != SourceFaction.Id)
                {
                    User.SendChatMessage(nameof(Messages.AreaNotOwnedByYourFaction), area.Id);
                    return false;
                }

                if (!Instance.EnsureFactionCanClaimArea(User, TargetFaction, area))
                    return false;



                if (fAreas.Count > 1 && area.Type == AreaType.Headquarters)
                {
                    User.SendChatMessage(nameof(Messages.SelectingCupboardFailedCantUnclaimHeadquarters));
                    return false;
                }

                Area[] claimedAreas = Instance.Areas.GetAllClaimedByFaction(TargetFaction);
                AreaType type = (claimedAreas.Length == 0) ? AreaType.Headquarters : AreaType.Claimed;

                Util.PrintToChat(nameof(Messages.AreaClaimTransferredAnnouncement), SourceFaction.Id, area.Id,
                    TargetFaction.Id);
                Instance.Log(
                    $"{Util.Format(User)} transferred {SourceFaction.Id}'s claim on {area.Id} to {TargetFaction.Id}");
                Util.RunEffect(User.transform.position, "assets/prefabs/missions/effects/mission_objective_complete.prefab");
                Instance.Areas.Claim(area, type, TargetFaction, User, cupboard);

                return true;
            }
        }
    }
}
#endregion

#region > Options
namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class BadlandsOptions
        {
            [JsonProperty("enabled")] public bool Enabled;

            public static BadlandsOptions Default = new BadlandsOptions
            {
                Enabled = true
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;
    using System.Collections.Generic;

    public partial class Imperium : RustPlugin
    {
        private class ClaimOptions
        {
            [JsonProperty("enabled")] public bool Enabled;

            [JsonProperty("costs")] public List<int> Costs = new List<int>();

            [JsonProperty("maxClaims")] public int? MaxClaims;

            [JsonProperty("minAreaNameLength")] public int MinAreaNameLength;

            [JsonProperty("maxAreaNameLength")] public int MaxAreaNameLength;

            [JsonProperty("minFactionMembers")] public int MinFactionMembers;

            [JsonProperty("requireContiguousClaims")]
            public bool RequireContiguousClaims;

            public static ClaimOptions Default = new ClaimOptions
            {
                Enabled = true,
                Costs = new List<int> { 50, 100, 200, 300, 400, 500 },
                MaxClaims = null,
                MinAreaNameLength = 3,
                MaxAreaNameLength = 20,
                MinFactionMembers = 1,
                RequireContiguousClaims = true
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class DecayOptions
        {
            [JsonProperty("enabled")] public bool Enabled;

            [JsonProperty("claimedLandDecayReduction")]
            public float ClaimedLandDecayReduction;

            public static DecayOptions Default = new DecayOptions
            {
                Enabled = false,
                ClaimedLandDecayReduction = 0.5f
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class FactionOptions
        {
            [JsonProperty("minFactionNameLength")] public int MinFactionNameLength = 1;

            [JsonProperty("maxFactionNameLength")] public int MaxFactionNameLength = 8;

            [JsonProperty("maxMembers")] public int? MaxMembers;

            [JsonProperty("allowFactionBadlands")] public bool AllowFactionBadlands = false;

            [JsonProperty("factionBadlandsCommandCooldownSeconds")] public int CommandCooldownSeconds = 300;

            [JsonProperty("overrideInGameTeamSystem")] public bool OverrideInGameTeamSystem = false;

            [JsonProperty("memberOwnLandEcoRaidingDamageScale")] public float MemberOwnLandEcoRaidingDamageScale = 1f;

            [JsonProperty("memberOwnLandExplosiveRaidingDamageScale")] public float MemberOwnLandExplosiveRaidingDamageScale = 1f;

            [JsonProperty("useClansPlugin")] private bool _UseClansPlugin = false;

            [JsonIgnore]
            public bool UseClansPlugin { get { return (_UseClansPlugin && Instance.Clans != null); } }

            public static FactionOptions Default = new FactionOptions
            {
                MinFactionNameLength = 1,
                MaxFactionNameLength = 8,
                MaxMembers = null,
                AllowFactionBadlands = false,
                CommandCooldownSeconds = 600,
                OverrideInGameTeamSystem = true,
                MemberOwnLandEcoRaidingDamageScale = 1f,
                MemberOwnLandExplosiveRaidingDamageScale = 1f,
                _UseClansPlugin = false
            };


        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;
    using System.Collections.Generic;
    public partial class Imperium : RustPlugin
    {
        private class UpgradingOptions
        {
            [JsonProperty("enabled [THIS IS NOT AVAILABLE YET]")] public bool Enabled = false;
            [JsonProperty("maxUpgradeLevel")] public int MaxUpgradeLevel = 10;
            [JsonProperty("maxProduceBonus")] public float MaxProduceBonus = 0.5f;
            [JsonProperty("maxTaxChestBonus")] public float MaxTaxChestBonus = 1f;
            [JsonProperty("maxRaidDefenseBonus")] public float MaxRaidDefenseBonus = 0.2f;
            [JsonProperty("maxDecayExtraReduction")] public float MaxDecayExtraReduction = 1f;
            [JsonProperty("maxRecruitBotBuffs")] public float MaxRecruitBotsBuffs = 0.2f;
            [JsonProperty("costs")] public List<int> Costs = new List<int>();

            public static UpgradingOptions Default = new UpgradingOptions
            {
                Enabled = false,
                MaxUpgradeLevel = 10,
                MaxProduceBonus = 0.5f,
                MaxTaxChestBonus = 1f,
                MaxRaidDefenseBonus = 0.2f,
                MaxDecayExtraReduction = 1f,
                MaxRecruitBotsBuffs = 0.2f,
                Costs = new List<int> { 0, 100, 200, 300, 400, 500 }
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class MapOptions
        {

            [JsonProperty("mapGridYOffset")] public int MapGridYOffset;

            [JsonProperty("pinsEnabled")] public bool PinsEnabled;

            [JsonProperty("minPinNameLength")] public int MinPinNameLength;

            [JsonProperty("maxPinNameLength")] public int MaxPinNameLength;

            [JsonProperty("pinCost")] public int PinCost;

            [JsonProperty("commandCooldownSeconds")]
            public int CommandCooldownSeconds;

            [JsonProperty("imageUrl")] public string ImageUrl;

            [JsonProperty("imageSize")] public int ImageSize;

            [JsonProperty("serverLogoUrl")] public string ServerLogoUrl;

            public static MapOptions Default = new MapOptions
            {
                MapGridYOffset = 0,
                PinsEnabled = true,
                MinPinNameLength = 2,
                MaxPinNameLength = 20,
                PinCost = 100,
                CommandCooldownSeconds = 10,
                ImageUrl = "",
                ImageSize = 1440,
                ServerLogoUrl = ""
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class ImperiumOptions
        {
            [JsonProperty("badlands")] public BadlandsOptions Badlands = new BadlandsOptions();

            [JsonProperty("claims")] public ClaimOptions Claims = new ClaimOptions();

            [JsonProperty("decay")] public DecayOptions Decay = new DecayOptions();

            [JsonProperty("factions")] public FactionOptions Factions = new FactionOptions();

            [JsonProperty("hud")] public HudOptions Hud = new HudOptions();

            [JsonProperty("map")] public MapOptions Map = new MapOptions();

            [JsonProperty("pvp")] public PvpOptions Pvp = new PvpOptions();

            [JsonProperty("raiding")] public RaidingOptions Raiding = new RaidingOptions();

            [JsonProperty("taxes")] public TaxOptions Taxes = new TaxOptions();

            [JsonProperty("upkeep")] public UpkeepOptions Upkeep = new UpkeepOptions();

            [JsonProperty("war")] public WarOptions War = new WarOptions();

            [JsonProperty("zones")] public ZoneOptions Zones = new ZoneOptions();

            [JsonProperty("recruiting")] public RecruitingOptions Recruiting = new RecruitingOptions();

            [JsonProperty("upgrading")] public UpgradingOptions Upgrading = new UpgradingOptions();

            public static ImperiumOptions Default = new ImperiumOptions
            {
                Badlands = BadlandsOptions.Default,
                Claims = ClaimOptions.Default,
                Decay = DecayOptions.Default,
                Factions = FactionOptions.Default,
                Hud = HudOptions.Default,
                Map = MapOptions.Default,
                Pvp = PvpOptions.Default,
                Raiding = RaidingOptions.Default,
                Taxes = TaxOptions.Default,
                Upkeep = UpkeepOptions.Default,
                War = WarOptions.Default,
                Zones = ZoneOptions.Default,
                Recruiting = RecruitingOptions.Default,
                Upgrading = UpgradingOptions.Default
            };
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Loading default configuration.");
            Config.WriteObject(ImperiumOptions.Default, true);
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class PvpOptions
        {
            [JsonProperty("restrictPvp")] public bool RestrictPvp;

            [JsonProperty("allowedInBadlands")] public bool AllowedInBadlands;

            [JsonProperty("allowedInClaimedLand")] public bool AllowedInClaimedLand;

            [JsonProperty("allowedInWilderness")] public bool AllowedInWilderness;

            [JsonProperty("allowedInEventZones")] public bool AllowedInEventZones;

            [JsonProperty("allowedInMonumentZones")]
            public bool AllowedInMonumentZones;

            [JsonProperty("allowedInRaidZones")] public bool AllowedInRaidZones;

            [JsonProperty("allowedInDeepWater")] public bool AllowedInDeepWater;

            [JsonProperty("allowedUnderground")] public bool AllowedUnderground;

            [JsonProperty("enablePvpCommand")] public bool EnablePvpCommand;


            [JsonProperty("commandCooldownSeconds")]
            public int CommandCooldownSeconds;

            public static PvpOptions Default = new PvpOptions
            {
                RestrictPvp = false,
                AllowedInBadlands = true,
                AllowedInClaimedLand = true,
                AllowedInEventZones = true,
                AllowedInMonumentZones = true,
                AllowedInRaidZones = true,
                AllowedInWilderness = true,
                AllowedInDeepWater = true,
                AllowedUnderground = true,
                EnablePvpCommand = false,
                CommandCooldownSeconds = 60
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class RaidingOptions
        {
            [JsonProperty("restrictRaiding")] public bool RestrictRaiding;

            [JsonProperty("allowedInBadlands")] public bool AllowedInBadlands;

            [JsonProperty("allowedInClaimedLand")] public bool AllowedInClaimedLand;

            [JsonProperty("allowedInWilderness")] public bool AllowedInWilderness;

            public static RaidingOptions Default = new RaidingOptions
            {
                RestrictRaiding = false,
                AllowedInBadlands = true,
                AllowedInClaimedLand = true,
                AllowedInWilderness = true
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class TaxOptions
        {
            [JsonProperty("enabled")] public bool Enabled;

            [JsonProperty("defaultTaxRate")] public float DefaultTaxRate;

            [JsonProperty("maxTaxRate")] public float MaxTaxRate;

            [JsonProperty("claimedLandGatherBonus")]
            public float ClaimedLandGatherBonus;

            [JsonProperty("badlandsGatherBonus")] public float BadlandsGatherBonus;

            public static TaxOptions Default = new TaxOptions
            {
                Enabled = true,
                DefaultTaxRate = 0.1f,
                MaxTaxRate = 0.2f,
                ClaimedLandGatherBonus = 0.1f,
                BadlandsGatherBonus = 0.1f
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class RecruitingOptions
        {
            [JsonProperty("enabled [THIS IS NOT AVAILABLE YET]")] public bool Enabled;

            public static RecruitingOptions Default = new RecruitingOptions
            {
                Enabled = false
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;

    public partial class Imperium : RustPlugin
    {
        private class HudOptions
        {
            [JsonProperty("showEventsHud")] public bool ShowEventsHUD;
            [JsonProperty("leftPanelXOffset")] public float LeftPanelXOffset = 0f;
            [JsonProperty("leftPanelYOffset")] public float LeftPanelYOffset = 0f;
            [JsonProperty("rightPanelXOffset")] public float RightPanelXOffset = 0f;
            [JsonProperty("rightPanelYOffset")] public float RightPanelYOffset = 0f;

            public static HudOptions Default = new HudOptions
            {
                ShowEventsHUD = true,
                LeftPanelXOffset = 0f,
                LeftPanelYOffset = 0f,
                RightPanelXOffset = 0f,
                RightPanelYOffset = 0f
            };
        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;
    using System.Collections.Generic;

    public partial class Imperium : RustPlugin
    {
        private class UpkeepOptions
        {
            [JsonProperty("enabled")] public bool Enabled;

            [JsonProperty("costs")] public List<int> Costs = new List<int>();

            [JsonProperty("checkIntervalMinutes")] public int CheckIntervalMinutes;

            [JsonProperty("collectionPeriodHours")]
            public int CollectionPeriodHours;

            [JsonProperty("gracePeriodHours")] public int GracePeriodHours;

            public static UpkeepOptions Default = new UpkeepOptions
            {
                Enabled = false,
                Costs = new List<int> { 10, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 },
                CheckIntervalMinutes = 15,
                CollectionPeriodHours = 24,
                GracePeriodHours = 12
            };

        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;
    using System.Collections.Generic;

    public partial class Imperium : RustPlugin
    {
        private class WarOptions
        {
            [JsonProperty("enabled")] public bool Enabled;

            [JsonProperty("noobFactionProtectionInSeconds")] public int NoobFactionProtectionInSeconds;

            [JsonProperty("declarationCost")] public int DeclarationCost;

            [JsonProperty("onlineDefendersRequired")] public int OnlineDefendersRequired;

            [JsonProperty("adminApprovalRequired")] public bool AdminApprovalRequired;

            [JsonProperty("defenderApprovalRequired")] public bool DefenderApprovalRequired;

            [JsonProperty("enableShopfrontPeace")] public bool EnableShopfrontPeace;

            [JsonProperty("priorAggressionRequired")] public bool PriorAggressionRequired;

            [JsonProperty("spamPreventionSeconds")] public int SpamPreventionSeconds;

            [JsonProperty("minCassusBelliLength")] public int MinCassusBelliLength;

            [JsonProperty("defensiveBonuses")] public List<float> DefensiveBonuses = new List<float>();

            public static WarOptions Default = new WarOptions
            {
                Enabled = true,
                NoobFactionProtectionInSeconds = 0,
                DeclarationCost = 0,
                OnlineDefendersRequired = 0,
                AdminApprovalRequired = false,
                DefenderApprovalRequired = false,
                PriorAggressionRequired = false,
                EnableShopfrontPeace = true,
                SpamPreventionSeconds = 0,
                MinCassusBelliLength = 50,
                DefensiveBonuses = new List<float> { 0, 0.5f, 1f }
            };


        }
    }
}

namespace Oxide.Plugins
{
    using Newtonsoft.Json;
    using System.Collections.Generic;

    public partial class Imperium : RustPlugin
    {
        private class ZoneOptions
        {
            [JsonProperty("enabled")] public bool Enabled;

            [JsonProperty("domeDarkness")] public int DomeDarkness;

            [JsonProperty("eventZoneRadius")] public float EventZoneRadius;

            [JsonProperty("eventZoneLifespanSeconds")]
            public float EventZoneLifespanSeconds;

            [JsonProperty("monumentZones")]
            public Dictionary<string, float> MonumentZones = new Dictionary<string, float>();

            public static ZoneOptions Default = new ZoneOptions
            {
                Enabled = true,
                DomeDarkness = 3,
                EventZoneRadius = 150f,
                EventZoneLifespanSeconds = 600f,
                MonumentZones = new Dictionary<string, float>
                {
                    {"airfield", 200},
                    {"sphere_tank", 120},
                    {"junkyard", 150},
                    {"launch_site", 300},
                    {"military_tunnel", 150},
                    {"powerplant", 175},
                    {"satellite_dish", 130},
                    {"trainyard", 180},
                    {"water_treatment_plant", 180 },
                    {"oilrig_1",200},
                    {"oilrig_2",200},
                    {"military_base",150},
                    {"research_base",150}
                }
            };
        }
    }
}
#endregion

#region > Game Event Watcher
namespace Oxide.Plugins
{
    using System.Collections.Generic;
    using UnityEngine;

    public partial class Imperium
    {
        private class GameEventWatcher : MonoBehaviour
        {
            private const float CheckIntervalSeconds = 5f;
            private HashSet<CargoPlane> CargoPlanes = new HashSet<CargoPlane>();
            private HashSet<BaseHelicopter> PatrolHelicopters = new HashSet<BaseHelicopter>();
            private HashSet<CH47Helicopter> ChinookHelicopters = new HashSet<CH47Helicopter>();
            private HashSet<HackableLockedCrate> LockedCrates = new HashSet<HackableLockedCrate>();
            private HashSet<CargoShip> CargoShips = new HashSet<CargoShip>();

            public bool IsCargoPlaneActive
            {
                get { return CargoPlanes.Count > 0; }
            }

            public bool IsHelicopterActive
            {
                get { return PatrolHelicopters.Count > 0; }
            }

            public bool IsChinookOrLockedCrateActive
            {
                get { return ChinookHelicopters.Count > 0 || LockedCrates.Count > 0; }
            }

            public bool IsCargoShipActive
            {
                get { return CargoShips.Count > 0; }
            }

            private void Awake()
            {
                foreach (CargoPlane plane in FindObjectsOfType<CargoPlane>())
                    BeginEvent(plane);

                foreach (BaseHelicopter heli in FindObjectsOfType<BaseHelicopter>())
                    BeginEvent(heli);

                foreach (CH47Helicopter chinook in FindObjectsOfType<CH47Helicopter>())
                    BeginEvent(chinook);

                foreach (HackableLockedCrate crate in FindObjectsOfType<HackableLockedCrate>())
                    BeginEvent(crate);

                foreach (CargoShip ship in FindObjectsOfType<CargoShip>())
                    BeginEvent(ship);

                InvokeRepeating("CheckEvents", CheckIntervalSeconds, CheckIntervalSeconds);
            }

            private void OnDestroy()
            {
                CancelInvoke();
            }

            public void BeginEvent(CargoPlane plane)
            {
                Instance.Puts($"Beginning cargoplane event, plane at @ {plane.transform.position}");
                CargoPlanes.Add(plane);
            }

            public void BeginEvent(BaseHelicopter heli)
            {
                Instance.Puts($"Beginning patrol helicopter event, heli at @ {heli.transform.position}");
                PatrolHelicopters.Add(heli);
            }

            public void BeginEvent(CH47Helicopter chinook)
            {
                Instance.Puts($"Beginning chinook event, heli at @ {chinook.transform.position}");
                ChinookHelicopters.Add(chinook);
            }

            public void BeginEvent(HackableLockedCrate crate)
            {
                Instance.Puts($"Beginning locked crate event, crate at @ {crate.transform.position}");
                LockedCrates.Add(crate);
            }

            public void BeginEvent(CargoShip ship)
            {
                Instance.Puts($"Beginning cargo ship event, ship at @ {ship.transform.position}");
                CargoShips.Add(ship);
            }

            private void CheckEvents()
            {
                var endedEvents = CargoPlanes.RemoveWhere(IsEntityGone)
                                  + PatrolHelicopters.RemoveWhere(IsEntityGone)
                                  + ChinookHelicopters.RemoveWhere(IsEntityGone)
                                  + LockedCrates.RemoveWhere(IsEntityGone)
                                  + CargoShips.RemoveWhere(IsEntityGone);

                if (endedEvents > 0)
                    Instance.Hud.RefreshForAllPlayers();
            }

            private bool IsEntityGone(BaseEntity entity)
            {
                return !entity.IsValid() || !entity.gameObject.activeInHierarchy;
            }
        }
    }
}
#endregion

#region > Hud
namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Oxide.Game.Rust.Cui;

    public partial class Imperium
    {
        private class HudManager
        {
            private Dictionary<string, Image> Images;
            private bool UpdatePending;

            public GameEventWatcher GameEvents { get; private set; }

            private ImageDownloader ImageDownloader;
            private MapOverlayGenerator MapOverlayGenerator;

            public HudManager()
            {
                Images = new Dictionary<string, Image>();
                GameEvents = Instance.GO.AddComponent<GameEventWatcher>();
                ImageDownloader = Instance.GO.AddComponent<ImageDownloader>();
                MapOverlayGenerator = Instance.GO.AddComponent<MapOverlayGenerator>();
            }

            public void RefreshForAllPlayers()
            {
                if (UpdatePending)
                    return;

                Instance.NextTick(() =>
                {
                    foreach (User user in Instance.Users.GetAll())
                    {
                        user.Map.Refresh();
                        user.Hud.Refresh();
                    }

                    UpdatePending = false;
                });

                UpdatePending = true;
            }

            public Image RegisterImage(string url, byte[] imageData = null, bool overwrite = false)
            {
                Image image;

                if (Images.TryGetValue(url, out image) && !overwrite)
                    return image;
                else
                    image = new Image(url);

                Images[url] = image;

                if (imageData != null)
                    image.Save(imageData);
                else
                    ImageDownloader.Download(image);

                return image;
            }

            public void RefreshAllImages()
            {
                foreach (Image image in Images.Values.Where(image => !image.IsGenerated))
                {
                    image.Delete();
                    ImageDownloader.Download(image);
                }
            }

            public CuiRawImageComponent CreateImageComponent(string imageUrl)
            {
                Image image;

                if (String.IsNullOrEmpty(imageUrl))
                {
                    Instance.PrintError(
                        $"CuiRawImageComponent requested for an image with a null URL. Did you forget to set MapImageUrl in the configuration?");
                    return null;
                }

                if (!Images.TryGetValue(imageUrl, out image))
                {
                    Instance.PrintError(
                        $"CuiRawImageComponent requested for image with an unregistered URL {imageUrl}. This shouldn't happen.");
                    return null;
                }

                if (image.Id != null)
                    return new CuiRawImageComponent { Png = image.Id, Sprite = UI.TransparentTexture };
                else
                    return new CuiRawImageComponent { Url = image.Url, Sprite = UI.TransparentTexture };
            }

            public void GenerateMapOverlayImage()
            {
                MapOverlayGenerator.Generate();
            }

            public void Init()
            {
                UserPanel.InitializeUserPanelCommandDefs();
                RegisterImage(dataDirectory + "map-image.png");
                RegisterImage(dataDirectory + "server-logo.png");
                RegisterDefaultImages(typeof(UI.HudIcon));
                RegisterDefaultImages(typeof(UI.MapIcon));
            }

            public void Destroy()
            {
                UnityEngine.Object.DestroyImmediate(ImageDownloader);
                UnityEngine.Object.DestroyImmediate(MapOverlayGenerator);
                UnityEngine.Object.DestroyImmediate(GameEvents);

                foreach (Image image in Images.Values)
                    image.Delete();

                Images.Clear();
            }

            private void RegisterDefaultImages(Type type)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    RegisterImage((string)field.GetValue(null));
                }

            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;

    public partial class Imperium
    {
        private class Image
        {
            public string Url { get; private set; }
            public string Id { get; private set; }

            public bool IsDownloaded
            {
                get { return Id != null; }
            }

            public bool IsGenerated
            {
                get { return Url != null && !Url.StartsWith("http", StringComparison.Ordinal); }
            }

            public Image(string url, string id = null)
            {
                Url = url;
                Id = id;
            }

            public string Save(byte[] data)
            {
                if (IsDownloaded) Delete();
                Id = FileStorage.server.Store(data, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID, 0)
                    .ToString();
                return Id;
            }

            public void Delete()
            {
                if (!IsDownloaded) return;
                FileStorage.server.Remove(Convert.ToUInt32(Id), FileStorage.Type.png,
                    CommunityEntity.ServerInstance.net.ID);
                Id = null;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using UnityEngine;

    public partial class Imperium
    {
        private class ImageDownloader : MonoBehaviour
        {
            private Queue<Image> PendingImages = new Queue<Image>();

            public bool IsDownloading { get; private set; }

            public void Download(Image image)
            {
                PendingImages.Enqueue(image);
                if (!IsDownloading) DownloadNext();
            }

            private void DownloadNext()
            {
                if (PendingImages.Count == 0)
                {
                    IsDownloading = false;
                    return;
                }

                Image image = PendingImages.Dequeue();
                StartCoroutine(DownloadImage(image));

                IsDownloading = true;
            }

            private IEnumerator DownloadImage(Image image)
            {
                var www = new WWW(image.Url);
                yield return www;

                if (!String.IsNullOrEmpty(www.error))
                {
                    Instance.Puts($"Error while downloading image {image.Url}: {www.error}");
                }
                else if (www.bytes == null || www.bytes.Length == 0)
                {
                    Instance.Puts($"Error while downloading image {image.Url}: No data received");
                }
                else
                {
                    byte[] data = ImageConversion.EncodeToPNG(www.texture);
                    image.Save(data);
                    DestroyImmediate(www.texture);
                    Instance.Puts($"Stored {image.Url} as id {image.Id}");
                    DownloadNext();
                }
            }
        }
    }
}

namespace Oxide.Plugins
{
    using System;
    using System.Collections.Generic;

    public partial class Imperium
    {
        private class ImperiumMapMarker
        {
            public string IconUrl;
            public string Label;
            public float X;
            public float Z;

            public static ImperiumMapMarker ForUser(User user)
            {
                return new ImperiumMapMarker
                {
                    IconUrl = UI.MapIcon.Player,
                    X = TranslatePositionX(user.Player.transform.position.x),
                    Z = TranslatePositionZ(user.Player.transform.position.z)
                };
            }

            public static ImperiumMapMarker ForHeadquarters(Area area, Faction faction)
            {
                return new ImperiumMapMarker
                {
                    IconUrl = UI.MapIcon.Headquarters,
                    Label = Util.RemoveSpecialCharacters(faction.Id),
                    X = TranslatePositionX(area.ClaimCupboard.transform.position.x),
                    Z = TranslatePositionZ(area.ClaimCupboard.transform.position.z)
                };
            }

            public static ImperiumMapMarker ForMonument(MonumentInfo monument)
            {
                string iconUrl = GetIconForMonument(monument);
                return new ImperiumMapMarker
                {
                    IconUrl = iconUrl,
                    Label = (iconUrl == UI.MapIcon.Unknown) ? monument.displayPhrase.english : null,
                    X = TranslatePositionX(monument.transform.position.x),
                    Z = TranslatePositionZ(monument.transform.position.z)
                };
            }

            public static ImperiumMapMarker ForPin(Pin pin)
            {
                string iconUrl = GetIconForPin(pin);
                return new ImperiumMapMarker
                {
                    IconUrl = iconUrl,
                    Label = pin.Name,
                    X = TranslatePositionX(pin.Position.x),
                    Z = TranslatePositionZ(pin.Position.z)
                };
            }

            private static float TranslatePositionX(float pos)
            {
                var mapHeight = TerrainMeta.Size.x;
                return (pos + mapHeight / 2f) / mapHeight;
            }

            private static float TranslatePositionZ(float pos)
            {
                var mapWidth = TerrainMeta.Size.z;
                return (pos + mapWidth / 2f) / mapWidth;
            }

            private static string GetIconForMonument(MonumentInfo monument)
            {
                if (monument.Type == MonumentType.Cave) return UI.MapIcon.Cave;
                if (monument.name.Contains("airfield")) return UI.MapIcon.Airfield;
                if (monument.name.Contains("bandit_town")) return UI.MapIcon.BanditTown;
                if (monument.name.Contains("compound")) return UI.MapIcon.Compound;
                if (monument.name.Contains("sphere_tank")) return UI.MapIcon.Dome;
                if (monument.name.Contains("harbor")) return UI.MapIcon.Harbor;
                if (monument.name.Contains("gas_station")) return UI.MapIcon.GasStation;
                if (monument.name.Contains("junkyard")) return UI.MapIcon.Junkyard;
                if (monument.name.Contains("launch_site")) return UI.MapIcon.LaunchSite;
                if (monument.name.Contains("lighthouse")) return UI.MapIcon.Lighthouse;
                if (monument.name.Contains("military_tunnel")) return UI.MapIcon.MilitaryTunnel;
                if (monument.name.Contains("warehouse")) return UI.MapIcon.MiningOutpost;
                if (monument.name.Contains("powerplant")) return UI.MapIcon.PowerPlant;
                if (monument.name.Contains("quarry")) return UI.MapIcon.Quarry;
                if (monument.name.Contains("satellite_dish")) return UI.MapIcon.SatelliteDish;
                if (monument.name.Contains("radtown_small_3")) return UI.MapIcon.SewerBranch;
                if (monument.name.Contains("power_sub")) return UI.MapIcon.Substation;
                if (monument.name.Contains("supermarket")) return UI.MapIcon.Supermarket;
                if (monument.name.Contains("trainyard")) return UI.MapIcon.Trainyard;
                if (monument.name.Contains("water_treatment_plant")) return UI.MapIcon.WaterTreatmentPlant;
                return UI.MapIcon.Unknown;
            }
        }

        private static string GetIconForPin(Pin pin)
        {
            switch (pin.Type)
            {
                case PinType.Arena:
                    return UI.MapIcon.Arena;
                case PinType.Hotel:
                    return UI.MapIcon.Hotel;
                case PinType.Marina:
                    return UI.MapIcon.Marina;
                case PinType.Shop:
                    return UI.MapIcon.Shop;
                case PinType.Town:
                    return UI.MapIcon.Town;
                default:
                    return UI.MapIcon.Unknown;
            }
        }
    }
}

namespace Oxide.Plugins
{
    using Oxide.Game.Rust.Cui;
    using System;
    using System.Linq;
    using UnityEngine;
    using System.Globalization;
    public partial class Imperium
    {

        public static class UI
        {

            //public const string ImageBaseUrl = "";
            public const string MapOverlayImageUrl = "imperium://map-overlay.png";
            public const string TransparentTexture = "assets/content/textures/generic/fulltransparent.tga";

            public static class Element
            {
                public static string Hud = "Hud";
                public static string HudPanelLeft = "Imperium.HudPanel.Top";
                public static string HudPanelRight = "Imperium.HudPanel.Middle";
                public static string HudPanelWarning = "Imperium.HudPanel.Warning";
                public static string HudPanelText = "Imperium.HudPanel.Text";
                public static string HudPanelIcon = "Imperium.HudPanel.Icon";
                public static string Overlay = "Overlay";
                public static string MapDialog = "Imperium.MapDialog";
                public static string MapHeader = "Imperium.MapDialog.Header";
                public static string MapHeaderTitle = "Imperium.MapDialog.Header.Title";
                public static string MapHeaderCloseButton = "Imperium.MapDialog.Header.CloseButton";
                public static string MapContainer = "Imperium.MapDialog.MapContainer";
                public static string MapTerrainImage = "Imperium.MapDialog.MapTerrainImage";
                public static string MapLayers = "Imperium.MapDialog.MapLayers";
                public static string MapClaimsImage = "Imperium.MapDialog.MapLayers.ClaimsImage";
                public static string MapMarkerIcon = "Imperium.MapDialog.MapLayers.MarkerIcon";
                public static string MapMarkerLabel = "Imperium.MapDialog.MapLayers.MarkerLabel";
                public static string MapSidebar = "Imperium.MapDialog.Sidebar";
                public static string MapButton = "Imperium.MapDialog.Sidebar.Button";
                public static string MapServerLogoImage = "Imperium.MapDialog.Sidebar.ServerLogo";

                public static string PanelWindow = "Imperium.Panel.Window";
                public static string PanelDialog = "Imperium.Panel.Dialog";
                public static string PanelHeader = "Imperium.Panel.Header";
                public static string PanelHeaderTitle = "Imperium.Panel.Header.Title";
                public static string PanelSidebar = "Imperium.Panel.Sidebar";
                public static string PanelTab = "Imperium.Panel.Sidebar.Tab";
                public static string PanelLabel = "Imperium.Panel.Dialog.Label";
                public static string PanelCommandButton = "Imperium.Panel.Dialog.CommandButton";
                public static string PanelTextInput = "Imperium.Panel.Dialog.TextInput";
                public static string PanelConfirmButton = "Imperium.Panel.Dialog.ConfirmButton";
            }

            public static class HudIcon
            {
                public static string Badlands = dataDirectory + "icons/hud/badlands.png";
                public static string CargoPlaneIndicatorOn = dataDirectory + "icons/hud/cargoplane-on.png";
                public static string CargoPlaneIndicatorOff = dataDirectory + "icons/hud/cargoplane-off.png";
                public static string CargoShipIndicatorOn = dataDirectory + "icons/hud/cargo-ship-on.png";
                public static string CargoShipIndicatorOff = dataDirectory + "icons/hud/cargo-ship-off.png";
                public static string ChinookIndicatorOn = dataDirectory + "icons/hud/chinook-on.png";
                public static string ChinookIndicatorOff = dataDirectory + "icons/hud/chinook-off.png";
                public static string Claimed = dataDirectory + "icons/hud/claimed.png";
                public static string Clock = dataDirectory + "icons/hud/clock.png";
                public static string Debris = dataDirectory + "icons/hud/debris.png";
                public static string Defense = dataDirectory + "icons/hud/defense.png";
                public static string Harvest = dataDirectory + "icons/hud/harvest.png";
                public static string Headquarters = dataDirectory + "icons/hud/headquarters.png";
                public static string HelicopterIndicatorOn = dataDirectory + "icons/hud/helicopter-on.png";
                public static string HelicopterIndicatorOff = dataDirectory + "icons/hud/helicopter-off.png";
                public static string Monument = dataDirectory + "icons/hud/monument.png";
                public static string Players = dataDirectory + "icons/hud/players.png";
                public static string PvpMode = dataDirectory + "icons/hud/pvp.png";
                public static string Raid = dataDirectory + "icons/hud/raid.png";
                public static string Ruins = dataDirectory + "icons/hud/ruins.png";
                public static string Sleepers = dataDirectory + "icons/hud/sleepers.png";
                public static string SupplyDrop = dataDirectory + "icons/hud/supplydrop.png";
                public static string Taxes = dataDirectory + "icons/hud/taxes.png";
                public static string Warning = dataDirectory + "icons/hud/warning.png";
                public static string WarZone = dataDirectory + "icons/hud/warzone.png";
                public static string Wilderness = dataDirectory + "icons/hud/wilderness.png";
            }

            public static class MapIcon
            {
                public static string Airfield = dataDirectory + "icons/map/airfield.png";
                public static string Arena = dataDirectory + "icons/map/arena.png";
                public static string BanditTown = dataDirectory + "icons/map/bandit-town.png";
                public static string Cave = dataDirectory + "icons/map/cave.png";
                public static string Compound = dataDirectory + "icons/map/compound.png";
                public static string Dome = dataDirectory + "icons/map/dome.png";
                public static string GasStation = dataDirectory + "icons/map/gas-station.png";
                public static string Harbor = dataDirectory + "icons/map/harbor.png";
                public static string Headquarters = dataDirectory + "icons/map/headquarters.png";
                public static string Hotel = dataDirectory + "icons/map/hotel.png";
                public static string Junkyard = dataDirectory + "icons/map/junkyard.png";
                public static string LaunchSite = dataDirectory + "icons/map/launch-site.png";
                public static string Lighthouse = dataDirectory + "icons/map/lighthouse.png";
                public static string Marina = dataDirectory + "icons/map/marina.png";
                public static string MilitaryTunnel = dataDirectory + "icons/map/military-tunnel.png";
                public static string MiningOutpost = dataDirectory + "icons/map/mining-outpost.png";
                public static string Player = dataDirectory + "icons/map/player.png";
                public static string PowerPlant = dataDirectory + "icons/map/power-plant.png";
                public static string Quarry = dataDirectory + "icons/map/quarry.png";
                public static string SatelliteDish = dataDirectory + "icons/map/satellite-dish.png";
                public static string SewerBranch = dataDirectory + "icons/map/sewer-branch.png";
                public static string Shop = dataDirectory + "icons/map/shop.png";
                public static string Substation = dataDirectory + "icons/map/substation.png";
                public static string Supermarket = dataDirectory + "icons/map/supermarket.png";
                public static string Town = dataDirectory + "icons/map/town.png";
                public static string Trainyard = dataDirectory + "icons/map/trainyard.png";
                public static string Unknown = dataDirectory + "icons/map/unknown.png";
                public static string WaterTreatmentPlant = dataDirectory + "icons/map/water-treatment-plant.png";
            }

            public static class Colors
            {
                public static string Primary = "#CC412B";
                public static string Secondary = "#222222";
                public static string Highlight = "#2D2D2D";
                public static string Disabled = "#AAAAAA";
                public static string Success = "#708C41";
                public static string Info = "#206A9E";
            }


            public static CuiElementContainer Container(string panel, string color, UI4 dimensions, bool blur = true, string parent = "Overlay")
            {
                CuiElementContainer container = new CuiElementContainer()
            {
                {
                    new CuiPanel
                    {
                        Image = { Color = color, Material = blur ? "assets/content/ui/uibackgroundblur-ingamemenu.mat" : string.Empty },
                        RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                        CursorEnabled = true
                    },
                    new CuiElement().Parent = parent,
                    panel
                }
            };
                return container;
            }

            public static CuiElementContainer Popup(string panel, string text, int size, UI4 dimensions, TextAnchor align = TextAnchor.MiddleCenter, string parent = "Overlay")
            {
                CuiElementContainer container = UI.Container(panel, "0 0 0 0", dimensions);

                UI.Label(container, panel, text, size, UI4.Full, align);

                return container;
            }

            public static void Panel(CuiElementContainer container, string panel, string color, UI4 dimensions)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = color },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                    FadeOut = 0.25f
                },
                panel);
            }

            public static void Label(CuiElementContainer container, string panel, string text, int size, UI4 dimensions, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text = { FontSize = size, Align = align, Text = text },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                    FadeOut = 0.25f
                },
                panel);
            }

            public static void Label(CuiElementContainer container, string panel, string color, string text, int size, UI4 dimensions, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text = { FontSize = size, Align = align, Text = text, Color = color },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                    FadeOut = 0.25f
                },
                panel);
            }

            public static void Button(CuiElementContainer container, string panel, string color, string text, int size, UI4 dimensions, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = command, FadeIn = 0f },
                    RectTransform = { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() },
                    Text = { Text = text, FontSize = size, Align = align },
                    FadeOut = 0.25f
                },
                panel);
            }

            public static void Input(CuiElementContainer container, string panel, string color, string text, int size, string command, UI4 dimensions, TextAnchor anchor = TextAnchor.MiddleLeft)
            {
                UI.Panel(container, panel, color, dimensions);
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                {
                    new CuiInputFieldComponent
                    {
                        Align = anchor,
                        CharsLimit = 300,
                        Command = command + text,
                        FontSize = size,
                        IsPassword = false,
                        Text = text,
                        NeedsKeyboard = true
                    },
                    new CuiRectTransformComponent {AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                }
                });
            }

            public static void Image(CuiElementContainer container, string panel, string png, UI4 dimensions)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                {
                    new CuiRawImageComponent {Png = png },
                    new CuiRectTransformComponent { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                }
                });
            }

            public static void Image(CuiElementContainer container, string panel, int itemId, ulong skinId, UI4 dimensions)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                {
                    new CuiImageComponent { ItemId = itemId, SkinId = skinId },
                    new CuiRectTransformComponent { AnchorMin = dimensions.GetMin(), AnchorMax = dimensions.GetMax() }
                }
                });
            }

            public static void Toggle(CuiElementContainer container, string panel, string boxColor, int fontSize, UI4 dimensions, string command, bool isOn)
            {
                UI.Panel(container, panel, boxColor, dimensions);

                if (isOn)
                    UI.Label(container, panel, "✔", fontSize, dimensions);

                UI.Button(container, panel, "0 0 0 0", string.Empty, 0, dimensions, command);
            }

            public static string Color(string hexColor, float alpha)
            {
                if (hexColor.StartsWith("#"))
                    hexColor = hexColor.TrimStart('#');

                int red = int.Parse(hexColor.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int green = int.Parse(hexColor.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int blue = int.Parse(hexColor.Substring(4, 2), NumberStyles.AllowHexSpecifier);

                return $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {alpha}";
            }

        }

        public class UI4
        {
            public float xMin, yMin, xMax, yMax;

            public UI4(float xMin, float yMin, float xMax, float yMax)
            {
                this.xMin = xMin;
                this.yMin = yMin;
                this.xMax = xMax;
                this.yMax = yMax;
            }

            public string GetMin() => $"{xMin} {1 - yMax}";

            public string GetMax() => $"{xMax} {1 - yMin}";

            private static UI4 _full;

            public static UI4 Full
            {
                get
                {
                    if (_full == null)
                        _full = new UI4(0, 0, 1, 1);
                    return _full;
                }
            }
        }
    }

}

namespace Oxide.Plugins
{
    using Oxide.Game.Rust.Cui;
    using System;
    using System.Linq;
    using UnityEngine;

    public partial class Imperium
    {
        private class UserHud
        {
            private const float IconSize = 0.0775f;

            private static class PanelColor
            {
                //public const string BackgroundNormal = "1 0.95 0.875 0.075";
                public const string BackgroundNormal = "0 0 0 0.5";
                public const string BackgroundDanger = "0.77 0.25 0.17 0.75";
                public const string BackgroundSafe = "0.44 0.54 0.26 1";
                public const string TextNormal = "0.85 0.85 0.85 1";
                public const string TextDanger = "0.85 0.65 0.65 1";
                public const string TextSafe = "1 1 1 1";
            }

            public User User { get; }
            public bool IsDisabled { get; set; }

            public UserHud(User user)
            {
                User = user;
            }

            public void Show()
            {
                CuiHelper.AddUi(User.Player, Build());
            }

            public void Hide()
            {

                CuiHelper.DestroyUi(User.Player, UI.Element.HudPanelLeft);
                CuiHelper.DestroyUi(User.Player, UI.Element.HudPanelRight);
                CuiHelper.DestroyUi(User.Player, UI.Element.HudPanelWarning);
            }

            public void Toggle()
            {
                if (IsDisabled)
                {
                    IsDisabled = false;
                    Show();
                }
                else
                {
                    IsDisabled = true;
                    Hide();
                }
            }

            public void Refresh()
            {
                if (IsDisabled)
                    return;

                Hide();
                Show();
            }

            private CuiElementContainer Build()
            {

                var container = new CuiElementContainer();

                Area area = User.CurrentArea;
                float xOff = Instance.Options.Hud.LeftPanelXOffset;
                float yOff = Instance.Options.Hud.LeftPanelXOffset;
                UI4 ui4 = new UI4(0.006f + xOff, 0.011f + yOff, 0.241f + xOff, 0.044f + yOff);
                container.Add(new CuiPanel
                {
                    Image = { Color = GetLeftPanelBackgroundColor() },
                    RectTransform = { AnchorMin = ui4.GetMin(), AnchorMax = ui4.GetMax() }
                }, UI.Element.Hud, UI.Element.HudPanelLeft);

                xOff = Instance.Options.Hud.RightPanelXOffset;
                yOff = Instance.Options.Hud.RightPanelYOffset;
                ui4 = new UI4(0.759f + xOff, 0.011f + yOff, 0.994f + xOff, 0.044f + yOff);

                if (Instance.Options.Hud.ShowEventsHUD)
                {
                    container.Add(new CuiPanel
                    {
                        Image = { Color = PanelColor.BackgroundNormal },
                        RectTransform = { AnchorMin = ui4.GetMin(), AnchorMax = ui4.GetMax() }
                    }, UI.Element.Hud, UI.Element.HudPanelRight);
                }
                AddWidget(container, UI.Element.HudPanelLeft, GetLocationIcon(), GetLeftPanelTextColor(),
                    GetLocationDescription());

                if (area != null)
                {
                    if (area.Type == AreaType.Badlands)
                    {
                        string harvestBonus = String.Format("+{0}%", Instance.Options.Taxes.BadlandsGatherBonus * 100);
                        AddWidget(container, UI.Element.HudPanelLeft, UI.HudIcon.Harvest, GetLeftPanelTextColor(),
                            harvestBonus, 0.77f);
                    }
                    else if (area.IsWarZone)
                    {
                        string defensiveBonus = String.Format("+{0}%", area.GetDefensiveBonus() * 100);
                        AddWidget(container, UI.Element.HudPanelLeft, UI.HudIcon.Defense, GetLeftPanelTextColor(),
                            defensiveBonus, 0.77f);
                    }
                    else if (area.IsTaxableClaim)
                    {
                        string taxRate = String.Format("{0}%", area.GetTaxRate() * 100);
                        AddWidget(container, UI.Element.HudPanelLeft, UI.HudIcon.Taxes, GetLeftPanelTextColor(),
                            taxRate, 0.78f);
                    }
                }

                if (Instance.Options.Hud.ShowEventsHUD)
                {
                    string planeIcon = Instance.Hud.GameEvents.IsCargoPlaneActive
                    ? UI.HudIcon.CargoPlaneIndicatorOn
                    : UI.HudIcon.CargoPlaneIndicatorOff;
                    AddWidget(container, UI.Element.HudPanelRight, planeIcon);

                    string shipIcon = Instance.Hud.GameEvents.IsCargoShipActive
                        ? UI.HudIcon.CargoShipIndicatorOn
                        : UI.HudIcon.CargoShipIndicatorOff;
                    AddWidget(container, UI.Element.HudPanelRight, shipIcon, 0.1f);

                    string heliIcon = Instance.Hud.GameEvents.IsHelicopterActive
                        ? UI.HudIcon.HelicopterIndicatorOn
                        : UI.HudIcon.HelicopterIndicatorOff;
                    AddWidget(container, UI.Element.HudPanelRight, heliIcon, 0.2f);

                    string chinookIcon = Instance.Hud.GameEvents.IsChinookOrLockedCrateActive
                        ? UI.HudIcon.ChinookIndicatorOn
                        : UI.HudIcon.ChinookIndicatorOff;
                    AddWidget(container, UI.Element.HudPanelRight, chinookIcon, 0.3f);

                    string activePlayers = BasePlayer.activePlayerList.Count.ToString();
                    AddWidget(container, UI.Element.HudPanelRight, UI.HudIcon.Players, PanelColor.TextNormal, activePlayers,
                        0.43f);

                    string sleepingPlayers = BasePlayer.sleepingPlayerList.Count.ToString();
                    AddWidget(container, UI.Element.HudPanelRight, UI.HudIcon.Sleepers, PanelColor.TextNormal,
                        sleepingPlayers, 0.58f);

                    string currentTime = TOD_Sky.Instance.Cycle.DateTime.ToString("HH:mm");
                    AddWidget(container, UI.Element.HudPanelRight, UI.HudIcon.Clock, PanelColor.TextNormal, currentTime,
                        0.75f);
                }


                bool claimUpkeepPastDue = Instance.Options.Upkeep.Enabled && User.Faction != null &&
                                          User.Faction.IsUpkeepPastDue && Instance.Areas.GetAllClaimedByFaction(User.Faction.Id).Length > 0;
                if (User.IsInPvpMode || claimUpkeepPastDue)
                {
                    if (Instance.Options.Hud.ShowEventsHUD)
                    {
                        ui4.yMin += 0.045f;
                        ui4.yMax += 0.045f;
                    }
                    CuiPanel panel = new CuiPanel
                    {
                        Image = { Color = PanelColor.BackgroundDanger },
                        RectTransform = { AnchorMin = ui4.GetMin(), AnchorMax = ui4.GetMax() }
                    };

                    container.Add(panel, UI.Element.Hud, UI.Element.HudPanelWarning);

                    if (claimUpkeepPastDue)
                        AddWidget(container, UI.Element.HudPanelWarning, UI.HudIcon.Ruins, PanelColor.TextDanger,
                            "Claim upkeep past due! (" + User.Faction.GetUpkeepPerPeriod() + " scrap)");
                    else
                        AddWidget(container, UI.Element.HudPanelWarning, UI.HudIcon.PvpMode, PanelColor.TextDanger,
                            "PVP mode enabled");
                }

                return container;
            }

            private string GetRightPanelBackgroundColor()
            {
                if (User.IsInPvpMode)
                    return PanelColor.BackgroundDanger;
                else
                    return PanelColor.BackgroundNormal;
            }

            private string GetRightPanelTextColor()
            {
                if (User.IsInPvpMode)
                    return PanelColor.TextDanger;
                else
                    return PanelColor.TextNormal;
            }

            private string GetLocationIcon()
            {
                Zone zone = User.CurrentZones.FirstOrDefault();

                if (zone != null)
                {
                    switch (zone.Type)
                    {
                        case ZoneType.SupplyDrop:
                            return UI.HudIcon.SupplyDrop;
                        case ZoneType.Debris:
                            return UI.HudIcon.Debris;
                        case ZoneType.Monument:
                            return UI.HudIcon.Monument;
                        case ZoneType.Raid:
                            return UI.HudIcon.Raid;
                    }
                }

                Area area = User.CurrentArea;

                if (area == null)
                {
                    if (Instance.Options.Pvp.AllowedInDeepWater)
                        return UI.HudIcon.Monument;
                    else
                        return UI.HudIcon.Wilderness;
                }

                if (area.IsWarZone)
                    return UI.HudIcon.WarZone;

                switch (area.Type)
                {
                    case AreaType.Badlands:
                        return UI.HudIcon.Badlands;
                    case AreaType.Claimed:
                        return UI.HudIcon.Claimed;
                    case AreaType.Headquarters:
                        return UI.HudIcon.Headquarters;
                    default:
                        return UI.HudIcon.Wilderness;
                }
            }

            private string GetLocationDescription()
            {
                Area area = User.CurrentArea;
                Zone zone = User.CurrentZones.FirstOrDefault();

                if (zone != null)
                {
                    if (area == null)
                        return zone.Name;
                    else
                        return $"{area.Id}: {zone.Name}";
                }

                if (area == null)
                    return "The Great Unknown";

                switch (area.Type)
                {
                    case AreaType.Badlands:
                        return $"{area.Id}: Badlands";
                    case AreaType.Claimed:
                        if (!String.IsNullOrEmpty(area.Name))
                            return $"{area.Id}: {area.Name} ({area.FactionId})";
                        else
                            return $"{area.Id}: {area.FactionId} Territory";
                    case AreaType.Headquarters:
                        if (!String.IsNullOrEmpty(area.Name))
                            return $"{area.Id}: {area.Name} ({area.FactionId} HQ)";
                        else
                            return $"{area.Id}: {area.FactionId} Headquarters";
                    default:
                        return $"{area.Id}: Wilderness";
                }
            }

            private string GetLeftPanelBackgroundColor()
            {
                if(User == null)
                {
                    Instance.PrintWarning($"An UserHud is trying to update but has no User associated with it. This shouldn't happen");
                    return PanelColor.BackgroundNormal;
                }
                if (User.CurrentZones.Count > 0)
                    return PanelColor.BackgroundDanger;

                Area area = User.CurrentArea;

                if (area == null)
                {
                    if (Instance.Options.Pvp.AllowedInDeepWater)
                        return PanelColor.BackgroundDanger;
                    else
                        return PanelColor.BackgroundNormal;
                }

                if (area.IsWarZone || area.IsHostile)
                    return PanelColor.BackgroundDanger;

                switch (area.Type)
                {
                    case AreaType.Badlands:
                        return Instance.Options.Pvp.AllowedInBadlands
                            ? PanelColor.BackgroundDanger
                            : PanelColor.BackgroundNormal;
                    case AreaType.Claimed:
                    case AreaType.Headquarters:
                        return Instance.Options.Pvp.AllowedInClaimedLand
                            ? PanelColor.BackgroundDanger
                            : area.FactionId == User.Faction.Id
                            ? PanelColor.BackgroundSafe
                            : PanelColor.BackgroundNormal;
                    default:
                        return Instance.Options.Pvp.AllowedInWilderness
                            ? PanelColor.BackgroundDanger
                            : PanelColor.BackgroundNormal;
                }
            }

            private string GetLeftPanelTextColor()
            {
                if (User == null)
                {
                    Instance.PrintWarning($"An UserHud is trying to update but has no User associated with it. This shouldn't happen");
                    return PanelColor.TextNormal;
                }

                if (User.CurrentZones.Count > 0)
                    return PanelColor.TextDanger;

                Area area = User.CurrentArea;

                if (area == null)
                {
                    if (Instance.Options.Pvp.AllowedInDeepWater)
                        return PanelColor.TextDanger;
                    else
                        return PanelColor.TextNormal;
                }

                if (area.IsWarZone || area.Type == AreaType.Badlands)
                    return PanelColor.TextDanger;
                switch (area.Type)
                {
                    case AreaType.Badlands:
                        return Instance.Options.Pvp.AllowedInBadlands
                            ? PanelColor.TextDanger
                            : PanelColor.TextNormal;
                    case AreaType.Claimed:
                    case AreaType.Headquarters:
                        return Instance.Options.Pvp.AllowedInClaimedLand
                            ? PanelColor.TextDanger
                            : area.FactionId == User.Faction.Id
                            ? PanelColor.TextSafe
                            : PanelColor.TextNormal;
                    default:
                        return Instance.Options.Pvp.AllowedInWilderness
                            ? PanelColor.TextDanger
                            : PanelColor.TextNormal;
                }
            }

            private void AddWidget(CuiElementContainer container, string parent, string iconName, string textColor, string text,
                float left = 0f)
            {
                var guid = Guid.NewGuid().ToString();

                container.Add(new CuiElement
                {
                    Name = UI.Element.HudPanelIcon + guid,
                    Parent = parent,
                    Components =
                    {
                        Instance.Hud.CreateImageComponent(iconName),
                        new CuiRectTransformComponent
                        {
                            AnchorMin = $"{left} {IconSize}",
                            AnchorMax = $"{left + IconSize} {1 - IconSize}",
                            OffsetMin = "6 0",
                            OffsetMax = "6 0"
                        }
                    }
                });

                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = text,
                        Color = textColor,
                        FontSize = 13,
                        Align = TextAnchor.MiddleLeft,
                    },
                    RectTransform =
                    {
                        AnchorMin = $"{left + IconSize} 0",
                        AnchorMax = "1 1",
                        OffsetMin = "11 0",
                        OffsetMax = "11 0"
                    }
                }, parent, UI.Element.HudPanelText + guid);
            }

            private void AddWidget(CuiElementContainer container, string parent, string iconName, float left = 0f)
            {
                var guid = Guid.NewGuid().ToString();

                container.Add(new CuiElement
                {
                    Name = UI.Element.HudPanelIcon + guid,
                    Parent = parent,
                    Components =
                    {
                        Instance.Hud.CreateImageComponent(iconName),
                        new CuiRectTransformComponent
                        {
                            AnchorMin = $"{left} {IconSize}",
                            AnchorMax = $"{left + IconSize} {1 - IconSize}",
                            OffsetMin = "6 0",
                            OffsetMax = "6 0"
                        }
                    }
                });
            }
        }
    }
}

namespace Oxide.Plugins
{
    using Oxide.Game.Rust.Cui;
    using System.Collections.Generic;
    using System;
    using System.Linq;
    using UnityEngine;

    public partial class Imperium
    {
        private class UserPanel
        {
            public static List<UIChatCommandDef> UiCommands = new List<UIChatCommandDef>();
            public static void InitializeUserPanelCommandDefs()
            {
                UiCommands =
                new List<UIChatCommandDef>()
                {
                    //faction create
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "CREATE",
                        shortDescription = "Create a new faction with the given name (Max 8 name length)",
                        command = "faction create",
                        auth = UIChatCommandDef.FactionAuth.NotFactionMember,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Faction Name",
                                description = "The name of your new faction",
                                isSubstring = false,
                            }
                        }
                    },
                    //faction join
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "JOIN",
                        shortDescription = "Accept a faction invite, as long as you have been invited",
                        command = "faction join",
                        auth = UIChatCommandDef.FactionAuth.NotFactionMember,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Faction Name",
                                description = "The name of the faction you want to join",
                                isSubstring = false,
                            }
                        }
                    },
                    //faction show
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "INFO",
                        shortDescription = "Show information about your faction in the chat",
                        command = "faction",
                        auth = UIChatCommandDef.FactionAuth.Member,
                        authExclusive = false
                    },

                    //faction invite
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "INVITE MEMBER",
                        shortDescription = "Invite a player to your faction. \nThe player must then accept with FACTION JOIN",
                        command = "faction invite",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Player name",
                                description = "The player to invite to your faction",
                                isSubstring = true,
                            }
                        }
                    },
                    //faction promote
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "PROMOTE MEMBER",
                        shortDescription = "Promote a member to manager role",
                        command = "faction promote",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Player name",
                                description = "The player to promote to manager role",
                                isSubstring = true,
                            }
                        }
                    },
                    //faction demote
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "DEMOTE MEMBER",
                        shortDescription = "Demote a manager to member role",
                        command = "faction demote",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Player name",
                                description = "The player to demote to member role",
                                isSubstring = true,
                            }
                        }
                    },
                    //faction kick
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "KICK MEMBER",
                        shortDescription = "Kick a player from your faction",
                        command = "faction kick",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Player name",
                                description = "The player to kick from your faction",
                                isSubstring = true,
                            }
                        }
                    },

                    //faction badlands confirm
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "TOGGLE BADLANDS",
                        shortDescription = "Allows/Deny PVP in all of your faction's lands.",
                        command = "faction badlands confirm",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true
                    },
                    //faction leave
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "LEAVE",
                        shortDescription = "Leave your current faction",
                        command = "faction leave",
                        auth = UIChatCommandDef.FactionAuth.Member,
                        authExclusive = false
                    },

                    //faction disband forever
                    new UIChatCommandDef()
                    {
                        category = "faction",
                        displayName = "DISBAND",
                        shortDescription = "Disband your entire faction. \nThis action cannot be undone",
                        command = "faction disband forever",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true
                    },

                    //CLAIM
                    //claim add
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "CLAIM LAND",
                        shortDescription = "Starts claim interaction. \nHit a tool cupboard with a hammer to complete the interaction",
                        command = "claim add",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        closesUI = true
                    },
                    //claim remove
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "UNCLAIM LAND",
                        shortDescription = "Starts unclaim interaction. \nHit a tool cupboard with a hammer to complete the interaction",
                        command = "claim remove",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        closesUI = true
                    },
                    //claim hq
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "SET HEADQUARTERS",
                        shortDescription = "Starts set headquarters interaction. \nHit a tool cupboard with a hammer to complete the interaction",
                        command = "claim hq",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        closesUI = true
                    },
                    
                    //claim give
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "GIVE LAND",
                        shortDescription = "Gives the selected land to a target faction. \nHit a tool cupboard with a hammer to complete the interaction",
                        command = "claim give",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Faction Name",
                                description = "The faction to receive the land",
                                isSubstring = false,
                            }
                        },
                        closesUI = true
                    },
                    //claim rename
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "RENAME LAND",
                        shortDescription = "Rename the target land with the specified name",
                        command = "claim rename",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Land coordinates [XY]",
                                description = "The land to rename (Example usage: C6)",
                                isSubstring = false,
                            },
                            new UIChatCommandArg()
                            {
                                label = "Name",
                                description = "The new name for the land",
                                isSubstring = true,
                            },
                        }
                    },
                    //claim cost
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "CHECK CLAIM COST",
                        shortDescription = "Shows the scrap claim cost for a given land in the chat",
                        command = "claim cost",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Land coordinates [XY]",
                                description = "The land to check the claim cost (Example usage: C6)",
                                isSubstring = false,
                            }
                        }
                    },
                    //claim list
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "CLAIM LIST",
                        shortDescription = "Shows a list of areas claimed by a faction",
                        command = "claim list",
                        auth = UIChatCommandDef.FactionAuth.Member,
                        authExclusive = false,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Faction Name",
                                description = "Target faction to check for claimed areas",
                                isSubstring = false,
                            }
                        }
                    },
                    //claim upkeep
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "CHECK LAND UPKEEP",
                        shortDescription = "Show current land upkeep status",
                        command = "claim upkeep",
                        auth = UIChatCommandDef.FactionAuth.Member,
                        authExclusive = false
                    },

                    //claim assign
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "CLAIM ASSIGN (ADMIN)",
                        shortDescription = "Assigns a land to a target faction. \nHit a tool cupboard with a hammer to complete the interaction",
                        command = "claim assign",
                        auth = UIChatCommandDef.FactionAuth.ServerAdmin,
                        authExclusive = true,
                        closesUI = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Faction Name",
                                description = "Target faction to assign the land to",
                                isSubstring = false,
                            }
                        }
                    },

                    //claim delete
                    new UIChatCommandDef()
                    {
                        category = "claim",
                        displayName = "CLAIM DELETE (ADMIN)",
                        shortDescription = "Deletes an existing land claim",
                        command = "claim delete",
                        auth = UIChatCommandDef.FactionAuth.ServerAdmin,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Land coordinates [XY]",
                                description = "The land to remove the current claim. (Example usage: C6)",
                                isSubstring = false,
                            }
                        }
                    },



                    //TAX
                    //tax chest
                    new UIChatCommandDef()
                    {
                        category = "tax",
                        displayName = "SELECT TAX CHEST",
                        shortDescription = "Select a tax chest for your faction. \nHit a chest with a hammer to complete the interaction",
                        command = "tax chest",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        closesUI = true
                    },

                    //tax rate XX
                    new UIChatCommandDef()
                    {
                        category = "tax",
                        displayName = "SET TAX PERCENTAGE",
                        shortDescription = "Select your faction's tax percentage. \nThis percentage will be taken from any resources gathered \nand automatically appear in your faction's Tax Chest",
                        command = "tax rate",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Tax Percentage [XX]",
                                description = "The farming percentage to charge in your land (Example usage: 10)",
                                isSubstring = false,
                            }
                        }
                    },

                    //WAR
                    //war status
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "WAR STATUS",
                        shortDescription = "Shows in the chat all wars your faction is involved",
                        command = "war status",
                        auth = UIChatCommandDef.FactionAuth.Member,
                        authExclusive = false
                    },
                    //war declare
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "DECLARE WAR",
                        shortDescription = "Declare war against an enemy faction \nwith a given reason.\nCost to declare war is " +
                            Instance.Options.War.DeclarationCost + " scrap",
                        command = "war declare",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Target Faction",
                                description = "The enemy faction's name to declare war against",
                                isSubstring = false,
                            },
                            new UIChatCommandArg()
                            {
                                label = "Reason",
                                description = "The reason behind the war declaration",
                                isSubstring = true,
                            }
                        }
                    },

                    //war end
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "END WAR",
                        shortDescription = "Ask for peace or accept a peace offer from an enemy faction.\nFaction leaders can also end war by trading in a shopfront",
                        command = "war end",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Enemy Faction",
                                description = "The enemy faction's name to end war",
                                isSubstring = false,
                            }
                        }
                    },

                    //war pending
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "LIST PENDING WAR REQUESTS",
                        shortDescription = "Check for pending war requests against your faction that can be approved or denied",
                        command = "war pending",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true
                    },

                    //war approve
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "APPROVE PENDING WAR REQUEST",
                        shortDescription = "Approve a war request from an enemy faction. \nThis will officialy start war between your factions",
                        command = "war approve",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Enemy Faction",
                                description = "The enemy faction's name to approve a pending war request",
                                isSubstring = false,
                            }
                        }
                    },

                    //war deny
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "DENY PENDING WAR REQUEST",
                        shortDescription = "Deny pending war request against your faction. \nThis will cancel the war request",
                        command = "war deny",
                        auth = UIChatCommandDef.FactionAuth.Leader,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Enemy Faction",
                                description = "The enemy faction's name to deny a pending war request",
                                isSubstring = false,
                            }
                        }
                    },

                    //war admin pending
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "LIST PENDING WAR REQUESTS (ADMIN)",
                        shortDescription = "List all pending war requests waiting for admin approval",
                        command = "war admin pending",
                        auth = UIChatCommandDef.FactionAuth.ServerAdmin,
                        authExclusive = true
                    },

                    //war approve
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "APPROVE PENDING WAR REQUEST (ADMIN)",
                        shortDescription = "Approve a war request waiting for admin approval. \nThis will officialy start the war between the two factions",
                        command = "war admin approve",
                        auth = UIChatCommandDef.FactionAuth.ServerAdmin,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Faction 1",
                                description = "The first faction involved in the war declaration",
                                isSubstring = false,
                            },
                            new UIChatCommandArg()
                            {
                                label = "Faction 2",
                                description = "The second faction involved in the war declaration",
                                isSubstring = false,
                            }
                        }
                    },

                    //war deny
                    new UIChatCommandDef()
                    {
                        category = "war",
                        displayName = "DENY PENDING WAR REQUEST (ADMIN)",
                        shortDescription = "Deny a war request waiting for admin approval. \nThis will cancel the war request from the attackers",
                        command = "war admin deny",
                        auth = UIChatCommandDef.FactionAuth.ServerAdmin,
                        authExclusive = true,
                        args =
                        {
                            new UIChatCommandArg()
                            {
                                label = "Faction 1",
                                description = "The first faction involved in the war declaration",
                                isSubstring = false,
                            },
                            new UIChatCommandArg()
                            {
                                label = "Faction 2",
                                description = "The second faction involved in the war declaration",
                                isSubstring = false,
                            }
                        }
                    }
                };

            }

            public User User { get; }
            public bool IsDisabled = true;
            public string currentCategory = "";
            public UIChatCommandDef selectedCommand;
            public string currentCommand = "";
            public Dictionary<int, string> indexedArgs = new Dictionary<int, string>();
            public int currentRequiredArgs = 0;

            public const float SPACING = 0.015f;


            public class UIChatCommandDef
            {
                public string displayName;
                public string shortDescription;
                public string category;
                public string command;
                public string uid;
                public List<UIChatCommandArg> args = new List<UIChatCommandArg>();
                public FactionAuth auth = FactionAuth.NotFactionMember;
                public bool authExclusive = true;
                public bool closesUI = false;

                public UIChatCommandDef()
                {
                    uid = Guid.NewGuid().ToString();
                }


                public enum FactionAuth
                {
                    NotFactionMember,
                    Member,
                    Manager,
                    Leader,
                    ServerAdmin
                }
            }

            public class UIChatCommandArg
            {
                public string label;
                public string description;
                public bool isSubstring = false;
            }

            public UserPanel(User user)
            {
                User = user;
            }

            public void ClearCurrentCommand()
            {
                selectedCommand = null;
                currentCommand = "";
                indexedArgs.Clear();
            }

            public void OpenTab(string category)
            {
                ClearCurrentCommand();
                currentCategory = category;
                Refresh();
            }

            public void OpenCommand(string uid)
            {
                ClearCurrentCommand();
                currentCategory = null;
                selectedCommand = UiCommands.Find(c => c.uid == uid);
                Refresh();
            }

            private List<CuiElementContainer> Build()
            {
                List<CuiElementContainer> result = new List<CuiElementContainer>();

                CuiElementContainer container = UI.Container(UI.Element.PanelWindow,
                    UI.Color(UI.Colors.Secondary, 0.8f),
                    new UI4(0.62f, 0.05f, 1f, 0.85f), true);

                CuiElementContainer header = CreatePanelHeader(container);
                CuiElementContainer sidebar = CreatePanelSidebar(container);
                CuiElementContainer dialog = UI.Container(UI.Element.PanelDialog,
                    UI.Color(UI.Colors.Highlight, 0f),
                    new UI4(0.05f, 0.15f, 0.75f, 0.95f),
                    false,
                    UI.Element.PanelWindow);
                if (selectedCommand != null)
                {
                    CreateSelectedCommandDialog(dialog);
                }
                else if (currentCategory != null && currentCategory != "")
                {
                    CreateSelectedCategoryButtons(dialog);
                }
                else
                {
                    CreateHomeDialog(dialog);
                }


                result = new List<CuiElementContainer>() { container, header, sidebar, dialog };

                return result;
            }

            private CuiElementContainer CreatePanelHeader(CuiElementContainer container)
            {
                CuiElementContainer header = UI.Container(UI.Element.PanelHeader,
                    UI.Color(UI.Colors.Primary, 1f),
                    new UI4(0f, 0f, 1f, 0.1f),
                    false,
                    UI.Element.PanelWindow
                );
                UI.Label(header, UI.Element.PanelHeader,
                    "IMPERIUM",
                    36,
                    UI4.Full
                );
                UI.Button(header, UI.Element.PanelHeader,
                    UI.Color(UI.Colors.Secondary, 1f),
                    "X",
                    12,
                    new UI4(0.93f, 0.3f, 0.97f, 0.7f),
                    "imperium.panel.close"
                );

                return header;
            }

            private CuiElementContainer CreatePanelSidebar(CuiElementContainer container)
            {
                List<string> categories = new List<string>() { "faction", "claim", "tax", "war" };
                CuiElementContainer sidebar = UI.Container(UI.Element.PanelSidebar,
                    UI.Color(UI.Colors.Primary, 1f),
                    new UI4(0.8f, 0.1f, 1f, 1f),
                    false,
                    UI.Element.PanelWindow
                );
                float sy = SPACING;
                for (int i = 0; i < categories.Count; i++)
                {
                    UI.Button(sidebar, UI.Element.PanelSidebar,
                        UI.Color(UI.Colors.Primary, 1f),
                        categories[i].ToUpper(),
                        20,
                        new UI4(0f, sy, 1f, sy + 0.1f),
                        "imperium.panel.opentab " + categories[i].ToString()
                    );
                    sy += 0.1f + SPACING;
                }
                return sidebar;
            }

            private void CreateSelectedCommandDialog(CuiElementContainer container)
            {
                if (selectedCommand == null)
                    return;
                float sy = SPACING;

                UI.Label(container, UI.Element.PanelDialog,
                    selectedCommand.displayName, 26,
                    new UI4(0f, sy, 1f, sy + 0.1f),
                    TextAnchor.MiddleLeft);
                sy += SPACING + 0.1f;

                UI.Label(container, UI.Element.PanelDialog,
                    selectedCommand.shortDescription, 12,
                    new UI4(0f, sy, 1f, sy + 0.1f),
                    TextAnchor.UpperLeft);
                sy += SPACING + 0.1f;

                if (selectedCommand.args.Count > 0)
                {
                    for (int i = 0; i < selectedCommand.args.Count; i++)
                    {
                        UIChatCommandArg arg = selectedCommand.args[i];
                        UI.Label(container, UI.Element.PanelDialog,
                            arg.label, 18,
                            new UI4(0f, sy, 1f, sy + 0.05f),
                            TextAnchor.MiddleLeft);
                        sy += SPACING + 0.05f;

                        UI.Label(container, UI.Element.PanelDialog,
                            arg.description, 12,
                            new UI4(0f, sy, 1f, sy + 0.05f),
                            TextAnchor.MiddleLeft);
                        sy += SPACING + 0.05f;

                        UI.Input(container, UI.Element.PanelDialog, UI.Color(UI.Colors.Info, 0.75f),
                            "", 16, "imperium.panel.setarg " + i + " " + arg.isSubstring.ToString().ToLower(),
                            new UI4(0f, sy, 1f, sy + 0.05f)
                            );
                        sy += SPACING + 0.05f;
                    }
                }

                UI.Button(container, UI.Element.PanelDialog,
                    UI.Color(UI.Colors.Success, 1f),
                    "CONFIRM",
                    16,
                    new UI4(0.7f, 0.9f, 1f, 1f),
                    "imperium.panel.run " + selectedCommand.closesUI.ToString().ToLower(),
                    TextAnchor.MiddleCenter
                    );
            }

            private void CreateSelectedCategoryButtons(CuiElementContainer container)
            {
                if (currentCategory == null || currentCategory == "")
                    return;
                float sy = SPACING;

                string title = currentCategory.ToUpper();

                if (currentCategory == "faction" && User.Faction != null)
                {
                    title = title + " [" + User.Faction.Id.ToUpper() + "]";
                }

                UI.Label(container, UI.Element.PanelDialog,
                    title, 26,
                    new UI4(0f, sy, 1f, 0.1f),
                    TextAnchor.MiddleLeft);
                sy += SPACING + 0.1f;

                List<UIChatCommandDef> categoryCmds = UiCommands.FindAll(c => c.category == currentCategory);

                if (categoryCmds.Count > 0)
                {
                    int buttonsAdded = 0;
                    for (int i = 0; i < categoryCmds.Count; i++)
                    {
                        UIChatCommandDef cmd = categoryCmds[i];
                        bool skip = false;
                        if (cmd.authExclusive)
                        {
                            if (cmd.auth == UIChatCommandDef.FactionAuth.NotFactionMember && User.Faction != null)
                                skip = true;
                            if (cmd.auth == UIChatCommandDef.FactionAuth.Leader && User.Faction == null)
                                skip = true;
                            if (cmd.auth == UIChatCommandDef.FactionAuth.Leader && User.Faction != null && !User.Faction.HasLeader(User))
                                skip = true;
                            if (cmd.auth == UIChatCommandDef.FactionAuth.ServerAdmin && (User.Player.Connection.authLevel == 0))
                                skip = true;
                        }
                        else
                        {
                            if (cmd.auth == UIChatCommandDef.FactionAuth.Leader && User.Faction != null && !User.Faction.HasLeader(User))
                                skip = true;
                            if (cmd.auth > UIChatCommandDef.FactionAuth.NotFactionMember && User.Faction == null)
                                skip = true;
                            if (cmd.auth == UIChatCommandDef.FactionAuth.ServerAdmin && (User.Player.Connection.authLevel == 0))
                                skip = true;
                        }
                        string color = UI.Color(UI.Colors.Info, 1f);
                        if (cmd.auth == UIChatCommandDef.FactionAuth.ServerAdmin)
                            color = UI.Color(UI.Colors.Primary, 1f);
                        if (!skip)
                        {
                            UI.Button(container, UI.Element.PanelDialog,
                                color,
                                cmd.displayName,
                                14,
                                new UI4(0f, sy, 1f, sy + 0.05f),
                                "imperium.panel.opencmd " + cmd.uid,
                                TextAnchor.MiddleCenter);
                            sy += SPACING + 0.05f;
                            buttonsAdded++;
                        }

                    }
                    if (buttonsAdded == 0)
                    {
                        UI.Label(container, UI.Element.PanelDialog,
                            UI.Color(UI.Colors.Disabled, 1f),
                            "No options available yet.\n\nTry creating or joining a faction first", 18,
                            new UI4(0f, sy, 1f, sy + 0.5f),
                            TextAnchor.MiddleCenter);
                        sy += SPACING + 0.1f;
                    }
                }
            }

            private void CreateHomeDialog(CuiElementContainer container)
            {
                float sy = SPACING;
                string description = "At its heart, Imperium adds the idea of territory to Rust. \n\nThe game is divided into a grid of tiles matching those displayed on the in-game map. \n\nPlayers can create factions, and these factions can claim these tiles of land and levy taxes on resources harvested therein. \n\nFactions can declare war on one another and battle for control of the territory.";
                string credits = "Original creator: ChuckleNugget\nImperium 2.0 developer: evict";
                UI.Label(container, UI.Element.PanelDialog,
                    UI.Color(UI.Colors.Disabled, 1f),
                    "IMPERIUM", 26,
                    new UI4(0f, sy, 1f, 0.1f),
                    TextAnchor.MiddleCenter);
                sy += SPACING + 0.12f;

                UI.Label(container, UI.Element.PanelDialog,
                    UI.Color(UI.Colors.Disabled, 1f),
                    description, 18,
                    new UI4(0f, sy, 1f, 0.9f),
                    TextAnchor.UpperCenter);

                UI.Label(container, UI.Element.PanelDialog,
                    UI.Color(UI.Colors.Success, 1f),
                    credits, 12,
                    new UI4(0f, 0.90f, 1f, 1f),
                    TextAnchor.LowerLeft);
            }

            public void Close()
            {
                ClearCurrentCommand();
                Hide();
            }

            public void SetCommand(string command)
            {
                ClearCurrentCommand();
                currentCommand = command;
            }

            public string GetFullConsoleCommand()
            {
                string s = "";
                s = s + "\"/";
                s = s + selectedCommand.command;
                if (indexedArgs.Count > 0)
                {
                    for (int i = 0; i < indexedArgs.Count; i++)
                    {
                        s = s + " ";
                        s = s + indexedArgs[i];
                    }
                }
                s = s + "\"";
                return s;
            }
            public void SetArg(int index, string arg, bool isSubstring = false)
            {
                if (!indexedArgs.ContainsKey(index))
                {
                    indexedArgs.Add(index, arg);
                }
                else
                {
                    indexedArgs[index] = arg;
                }
                if (isSubstring)
                {
                    indexedArgs[index] = "\\\"" + indexedArgs[index] + "\\\"";
                }
            }

            public void RemoveArg(int index)
            {
                if (indexedArgs.ContainsKey(index))
                {
                    indexedArgs.Remove(index);
                }
            }

            public bool HasArg(int index)
            {
                return indexedArgs.ContainsKey(index);
            }

            public void Show()
            {
                List<CuiElementContainer> containers = Build();
                foreach (CuiElementContainer container in containers)
                {
                    CuiHelper.AddUi(User.Player, container);
                }
                IsDisabled = false;
            }

            public void Hide()
            {
                CuiHelper.DestroyUi(User.Player, UI.Element.PanelWindow);
                CuiHelper.DestroyUi(User.Player, UI.Element.PanelHeader);
                CuiHelper.DestroyUi(User.Player, UI.Element.PanelSidebar);
                CuiHelper.DestroyUi(User.Player, UI.Element.PanelDialog);
                IsDisabled = true;

            }

            public void Toggle()
            {
                if (IsDisabled)
                {
                    IsDisabled = false;
                    Show();
                }
                else
                {
                    IsDisabled = true;
                    Hide();
                }
            }

            public void Refresh()
            {
                if (IsDisabled)
                    return;

                Hide();
                Show();
            }
        }
    }
}
#endregion

#region > UI Console Commands

#endregion

#region > User Map
namespace Oxide.Plugins
{
    using System;
    using Oxide.Game.Rust.Cui;
    using UnityEngine;
    using System.Linq;

    public partial class Imperium
    {
        private class UserMap
        {
            public User User { get; }
            public bool IsVisible { get; private set; }

            public UserMap(User user)
            {
                User = user;
            }

            public void Show()
            {
                CuiHelper.AddUi(User.Player, BuildDialog());
                CuiHelper.AddUi(User.Player, BuildSidebar());
                CuiHelper.AddUi(User.Player, BuildMapLayers());
                IsVisible = true;
            }

            public void Hide()
            {
                CuiHelper.DestroyUi(User.Player, UI.Element.MapDialog);
                IsVisible = false;
            }

            public void Toggle()
            {
                if (IsVisible)
                    Hide();
                else
                    Show();
            }

            public void Refresh()
            {
                if (IsVisible)
                {
                    CuiHelper.DestroyUi(User.Player, UI.Element.MapSidebar);
                    CuiHelper.DestroyUi(User.Player, UI.Element.MapLayers);
                    CuiHelper.AddUi(User.Player, BuildSidebar());
                    CuiHelper.AddUi(User.Player, BuildMapLayers());
                }
            }

            // --- Dialog ---

            private CuiElementContainer BuildDialog()
            {
                var container = new CuiElementContainer();

                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.75" },
                    RectTransform = { AnchorMin = "0.164 0.014", AnchorMax = "0.836 0.986" },
                    CursorEnabled = true
                }, UI.Element.Overlay, UI.Element.MapDialog);

                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0.012 0.014", AnchorMax = "0.774 0.951" }
                }, UI.Element.MapDialog, UI.Element.MapContainer);

                AddDialogHeader(container);
                AddMapTerrainImage(container);

                return container;
            }

            private void AddDialogHeader(CuiElementContainer container)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 1" },
                    RectTransform = { AnchorMin = "0 0.966", AnchorMax = "0.999 0.999" }
                }, UI.Element.MapDialog, UI.Element.MapHeader);

                container.Add(new CuiLabel
                {
                    Text = { Text = ConVar.Server.hostname, FontSize = 13, Align = TextAnchor.MiddleLeft, FadeIn = 0 },
                    RectTransform = { AnchorMin = "0.012 0.025", AnchorMax = "0.099 0.917" }
                }, UI.Element.MapHeader, UI.Element.MapHeaderTitle);

                container.Add(new CuiButton
                {
                    Text = { Text = "X", FontSize = 13, Align = TextAnchor.MiddleCenter },
                    Button = { Color = "0 0 0 0", Command = "imperium.map.toggle", FadeIn = 0 },
                    RectTransform = { AnchorMin = "0.972 0.083", AnchorMax = "0.995 0.917" }
                }, UI.Element.MapHeader, UI.Element.MapHeaderCloseButton);
            }

            private void AddMapTerrainImage(CuiElementContainer container)
            {
                CuiRawImageComponent image = Instance.Hud.CreateImageComponent(dataDirectory + "map-image.png");

                // If the image hasn't been loaded, just display a black box so we don't cause an RPC AddUI crash.
                if (image == null)
                    image = new CuiRawImageComponent { Color = "0 0 0 1" };

                container.Add(new CuiElement
                {
                    Name = UI.Element.MapTerrainImage,
                    Parent = UI.Element.MapContainer,
                    Components =
                    {
                        image,
                        new CuiRectTransformComponent {AnchorMin = "0 0", AnchorMax = "1 1"}
                    }
                });
            }

            // --- Sidebar ---

            private CuiElementContainer BuildSidebar()
            {
                var container = new CuiElementContainer();
                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0.786 0.014", AnchorMax = "0.988 0.951" }
                }, UI.Element.MapDialog, UI.Element.MapSidebar);

                AddLayerToggleButtons(container);
                AddServerLogo(container);

                return container;
            }

            private void AddLayerToggleButtons(CuiElementContainer container)
            {
                container.Add(new CuiButton
                {
                    Text = { Text = "Land Claims", FontSize = 14, Align = TextAnchor.MiddleCenter },
                    Button =
                    {
                        Color = GetButtonColor(UserMapLayer.Claims), Command = "imperium.map.togglelayer claims",
                        FadeIn = 0
                    },
                    RectTransform = { AnchorMin = "0 0.924", AnchorMax = "1 1" }
                }, UI.Element.MapSidebar, UI.Element.MapButton + Guid.NewGuid().ToString());

                container.Add(new CuiButton
                {
                    Text = { Text = "Faction Headquarters", FontSize = 14, Align = TextAnchor.MiddleCenter },
                    Button =
                    {
                        Color = GetButtonColor(UserMapLayer.Headquarters),
                        Command = "imperium.map.togglelayer headquarters", FadeIn = 0
                    },
                    RectTransform = { AnchorMin = "0 0.832", AnchorMax = "1 0.909" }
                }, UI.Element.MapSidebar, UI.Element.MapButton + Guid.NewGuid().ToString());

                container.Add(new CuiButton
                {
                    Text = { Text = "Monuments", FontSize = 14, Align = TextAnchor.MiddleCenter },
                    Button =
                    {
                        Color = GetButtonColor(UserMapLayer.Monuments),
                        Command = "imperium.map.togglelayer monuments", FadeIn = 0
                    },
                    RectTransform = { AnchorMin = "0 0.741", AnchorMax = "1 0.817" }
                }, UI.Element.MapSidebar, UI.Element.MapButton + Guid.NewGuid().ToString());

                container.Add(new CuiButton
                {
                    Text = { Text = "Pins", FontSize = 14, Align = TextAnchor.MiddleCenter },
                    Button =
                    {
                        Color = GetButtonColor(UserMapLayer.Pins), Command = "imperium.map.togglelayer pins",
                        FadeIn = 0
                    },
                    RectTransform = { AnchorMin = "0 0.649", AnchorMax = "1 0.726" }
                }, UI.Element.MapSidebar, UI.Element.MapButton + Guid.NewGuid().ToString());
            }

            private void AddServerLogo(CuiElementContainer container)
            {
                CuiRawImageComponent image = Instance.Hud.CreateImageComponent(dataDirectory + "server-logo.png");

                // If the image hasn't been loaded, just display a black box so we don't cause an RPC AddUI crash.
                if (image == null)
                    image = new CuiRawImageComponent { Color = "0 0 0 1" };

                container.Add(new CuiElement
                {
                    Name = UI.Element.MapServerLogoImage,
                    Parent = UI.Element.MapSidebar,
                    Components =
                    {
                        image,
                        new CuiRectTransformComponent {AnchorMin = "0 0", AnchorMax = "1 0.133"}
                    }
                });
            }

            // --- Map Layers ---

            private CuiElementContainer BuildMapLayers()
            {
                var container = new CuiElementContainer();

                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, UI.Element.MapContainer, UI.Element.MapLayers);

                if (User.Preferences.IsMapLayerVisible(UserMapLayer.Claims))
                    AddClaimsLayer(container);

                if (User.Preferences.IsMapLayerVisible(UserMapLayer.Monuments))
                    AddMonumentsLayer(container);

                if (User.Preferences.IsMapLayerVisible(UserMapLayer.Headquarters))
                    AddHeadquartersLayer(container);

                if (User.Preferences.IsMapLayerVisible(UserMapLayer.Pins))
                    AddPinsLayer(container);

                AddMarker(container, ImperiumMapMarker.ForUser(User));

                return container;
            }

            private void AddClaimsLayer(CuiElementContainer container)
            {
                CuiRawImageComponent image = Instance.Hud.CreateImageComponent(UI.MapOverlayImageUrl);

                // If the claims overlay hasn't been generated yet, just display a black box so we don't cause an RPC AddUI crash.
                if (image == null)
                    image = new CuiRawImageComponent { Color = "0 0 0 1" };

                container.Add(new CuiElement
                {
                    Name = UI.Element.MapClaimsImage,
                    Parent = UI.Element.MapLayers,
                    Components =
                    {
                        image,
                        new CuiRectTransformComponent {AnchorMin = "0 0", AnchorMax = "1 1"}
                    }
                });
            }

            private void AddMonumentsLayer(CuiElementContainer container)
            {
                var monuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
                foreach (MonumentInfo monument in monuments.Where(ShowMonumentOnMap))
                    AddMarker(container, ImperiumMapMarker.ForMonument(monument));
            }

            private void AddHeadquartersLayer(CuiElementContainer container)
            {
                foreach (Area area in Instance.Areas.GetAllByType(AreaType.Headquarters))
                {
                    var faction = Instance.Factions.Get(area.FactionId);
                    AddMarker(container, ImperiumMapMarker.ForHeadquarters(area, faction));
                }
            }

            private void AddPinsLayer(CuiElementContainer container)
            {
                foreach (Pin pin in Instance.Pins.GetAll())
                    AddMarker(container, ImperiumMapMarker.ForPin(pin));
            }

            private void AddMarker(CuiElementContainer container, ImperiumMapMarker marker, float iconSize = 0.01f)
            {
                container.Add(new CuiElement
                {
                    Name = UI.Element.MapMarkerIcon + Guid.NewGuid().ToString(),
                    Parent = UI.Element.MapLayers,
                    Components =
                    {
                        Instance.Hud.CreateImageComponent(marker.IconUrl),
                        new CuiRectTransformComponent
                        {
                            AnchorMin = $"{marker.X - iconSize} {marker.Z - iconSize}",
                            AnchorMax = $"{marker.X + iconSize} {marker.Z + iconSize}"
                        }
                    }
                });

                if (!String.IsNullOrEmpty(marker.Label))
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = marker.Label, FontSize = 8, Align = TextAnchor.MiddleCenter, FadeIn = 0 },
                        RectTransform =
                        {
                            AnchorMin = $"{marker.X - 0.1} {marker.Z - iconSize - 0.0175}",
                            AnchorMax = $"{marker.X + 0.1} {marker.Z - iconSize}"
                        }
                    }, UI.Element.MapLayers, UI.Element.MapMarkerLabel + Guid.NewGuid().ToString());
                }
            }

            private bool ShowMonumentOnMap(MonumentInfo monument)
            {
                return monument.Type != MonumentType.Cave
                       && !monument.name.Contains("power_sub")
                       && !monument.name.Contains("water_well")
                       && !monument.name.Contains("swamp")
                       && !monument.name.Contains("ice_lake");
            }

            private string GetButtonColor(UserMapLayer layer)
            {
                if (User.Preferences.IsMapLayerVisible(layer))
                    return "0 0 0 1";
                else
                    return "0.33 0.33 0.33 1";
            }
        }
    }
}
#endregion

#region > DEPRECATED
namespace Oxide.Plugins
{
    using System.Collections.Generic;

    public partial class Imperium
    {
        private class LruCache<K, V>
        {
            private Dictionary<K, LinkedListNode<LruCacheItem>> Nodes;
            private LinkedList<LruCacheItem> RecencyList;

            public int Capacity { get; private set; }

            public LruCache(int capacity)
            {
                Capacity = capacity;
                Nodes = new Dictionary<K, LinkedListNode<LruCacheItem>>();
                RecencyList = new LinkedList<LruCacheItem>();
            }

            public bool TryGetValue(K key, out V value)
            {
                LinkedListNode<LruCacheItem> node;

                if (!Nodes.TryGetValue(key, out node))
                {
                    value = default(V);
                    return false;
                }

                LruCacheItem item = node.Value;
                RecencyList.Remove(node);
                RecencyList.AddLast(node);

                value = item.Value;
                return true;
            }

            public void Set(K key, V value)
            {
                LinkedListNode<LruCacheItem> node;

                if (Nodes.TryGetValue(key, out node))
                {
                    RecencyList.Remove(node);
                    node.Value.Value = value;
                }
                else
                {
                    if (Nodes.Count >= Capacity)
                        Evict();

                    var item = new LruCacheItem(key, value);
                    node = new LinkedListNode<LruCacheItem>(item);
                }

                RecencyList.AddLast(node);
                Nodes[key] = node;
            }

            public bool Remove(K key)
            {
                LinkedListNode<LruCacheItem> node;

                if (!Nodes.TryGetValue(key, out node))
                    return false;

                Nodes.Remove(key);
                RecencyList.Remove(node);

                return true;
            }

            public void Clear()
            {
                Nodes.Clear();
                RecencyList.Clear();
            }

            private void Evict()
            {
                LruCacheItem item = RecencyList.First.Value;
                RecencyList.RemoveFirst();
                Nodes.Remove(item.Key);
            }

            private class LruCacheItem
            {
                public K Key;
                public V Value;

                public LruCacheItem(K key, V value)
                {
                    Key = key;
                    Value = value;
                }
            }
        }
    }
}
#endregion