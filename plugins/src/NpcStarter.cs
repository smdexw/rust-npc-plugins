using System.Collections.Generic;
using UnityEngine;
using Rust.Ai.Gen2;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("NpcStarter", "LocalDev", "2.2.1")]
    [Description("A small NPC prototype with admin spawn/remove and nearby player dialogue.")]
    public class NpcStarter : RustPlugin
    {
        private const string Prefab = "assets/prefabs/player/player.prefab";
        private const float TalkRange = 4f;
        private const float RemoveRange = 6f;
        private const int MaxNpcs = 20;
        // Guards only use the two real player gaits (never an in-between speed, which
        // looks like a sped-up walk): walk, or run with the sprint animation.
        private const float FollowTick = 0.25f;
        // BasePlayer hard-codes PositionTickRate = -1 (real players sync another way),
        // so syncPosition never streams movement for these NPCs; send it ourselves.
        private const float PositionSendInterval = 0.1f;
        private const float WalkSpeed = 2.8f;
        private const float RunSpeed = 5.5f;
        private const float OwnerRunningSpeed = 4.5f;
        // Hysteresis so guards do not flicker between gaits or between stop and start.
        private const float RunStartDistance = 6f;
        private const float RunStopDistance = 2.5f;
        private const float StartMoveDistance = 1.5f;
        private const float StopMoveDistance = 0.4f;
        // Guards stay inside their own [min, max] distance band around the owner,
        // spread apart by bearing so they never stand on each other.
        private const float GuardSpacing = 1.8f;
        private const float DefaultMinDistance = 3f;
        private const float DefaultMaxDistance = 6f;
        private const float DistanceStep = 0.5f;
        private const float MinDistanceLimit = 1.5f;
        private const float MaxDistanceLimit = 20f;
        private const float MinDistanceGap = 1f;
        private const int GuardsPerPage = 5;
        // Followers left too far behind (cliffs, buildings, owner respawned elsewhere)
        // are teleported next to the owner instead of giving up.
        private const float DefaultTeleportDistance = 40f;
        private const float TeleportStep = 10f;
        private const float MinTeleportDistance = 20f;
        private const float MaxTeleportDistance = 150f;
        private const float TeleportRetryDelay = 2f;
        private bool autoTeleport = true;
        private float teleportDistance = DefaultTeleportDistance;
        private readonly Dictionary<BaseEntity, float> nextTeleportTry = new Dictionary<BaseEntity, float>();
        // ---- Work: gathering, passive production, smelting, crafting ----
        private const int WorkNone = 0;
        private const int WorkGather = 1;       // gather around an anchor point
        private const int WorkFollowGather = 2; // gather next to the owner while they stand still
        private const int WorkProduce = 3;      // resources appear in the backpack over time
        private static readonly string[] WorkNames = { "無", "指派採集", "跟隨順便採集", "原地產出" };
        private const float GatherRadius = 40f;
        private const float GatherDamage = 25f;
        private const float GatherSwingInterval = 1.2f;
        private const float GatherGiveUpSeconds = 15f;
        private const float ProduceInterval = 30f;
        private const float SmeltInterval = 3f;
        private static readonly string[] CraftItems =
            { "bandage", "syringe.medical", "ammo.pistol", "ammo.rifle", "ammo.shotgun", "arrow.wooden", "gunpowder", "lowgradefuel", "hatchet", "pickaxe" };
        private static readonly string[] CraftNames =
            { "繃帶", "醫療針", "手槍子彈", "步槍子彈", "霰彈", "木箭", "火藥", "低級燃料", "斧頭", "十字鎬" };
        // Gather targets are ResourceEntity nodes (trees/ores) or CollectibleEntity
        // pickups (hemp, mushrooms, resource piles, wild crops).
        private readonly Dictionary<BaseEntity, BaseEntity> gatherTargets = new Dictionary<BaseEntity, BaseEntity>();
        private static readonly string[] GatherCategoryNames = { "樹木", "石頭礦", "金屬礦", "硫磺礦", "大麻纖維", "蘑菇", "地上資源堆", "野生作物" };
        private static readonly string[] GatherCategoryIcons = { "wood", "stones", "metal.ore", "sulfur.ore", "cloth", "mushroom", "stones", "corn" };
        private const int AllGatherCategories = 0xFF;
        private readonly BaseEntity[] gatherQueryBuffer = new BaseEntity[1024];
        private readonly Dictionary<BaseEntity, float> gatherStarted = new Dictionary<BaseEntity, float>();
        private readonly Dictionary<BaseEntity, float> nextSwing = new Dictionary<BaseEntity, float>();
        private readonly Dictionary<BaseEntity, float> unreachableNodes = new Dictionary<BaseEntity, float>();
        private readonly Dictionary<BaseEntity, float> craftFinishAt = new Dictionary<BaseEntity, float>();
        private readonly Dictionary<BaseEntity, float> nextProduce = new Dictionary<BaseEntity, float>();
        private readonly Dictionary<BaseEntity, float> nextSmelt = new Dictionary<BaseEntity, float>();
        private readonly Dictionary<BaseEntity, string> workStatus = new Dictionary<BaseEntity, string>();
        private class LootTask
        {
            public Vector3 Position;
            public float Created;
            public float Started;
            public BaseEntity Container;
        }
        private readonly Dictionary<BaseEntity, LootTask> lootTasks = new Dictionary<BaseEntity, LootTask>();
        private const float LootGiveUpSeconds = 60f;
        private const float DefaultSightRange = 20f;
        private const float SightStep = 5f;
        private const float MinSightRange = 10f;
        private const float MaxSightRange = 100f;
        private readonly Dictionary<BaseEntity, float> followBearings = new Dictionary<BaseEntity, float>();
        // Public so the data file (Newtonsoft JSON) can read and write it.
        public class GuardSettings
        {
            public float MinDistance = DefaultMinDistance;
            public float MaxDistance = DefaultMaxDistance;
            // Aggressive: engage any threat near the owner. Passive: only fire back
            // after the owner or a guard has been hurt.
            public bool Aggressive = true;
            public bool Invulnerable;
            // How far it can spot, shoot and retaliate against enemies.
            public float SightRange = DefaultSightRange;
            // Custom display name; null means the default "護衛 #N".
            public string Name;
            // Work: see WorkNone..WorkProduce. Gather mode works around the anchor.
            public int Work;
            public float AnchorX, AnchorY, AnchorZ;
            public bool Smelt;
            // Bit i set = gather GatherCategoryNames[i].
            public int GatherMask = AllGatherCategories;
            // Walk over and take the items off enemies it kills.
            public bool AutoLoot = true;
            public string CraftItem;
            public int CraftRemaining;
            // Appearance: gender + look index pick the userID the client builds the
            // face/body from; outfit and weapon index into the tables below.
            public bool Female = true;
            public int LookIndex;
            public int Outfit;
            public int Weapon;
        }
        private static readonly string[] OutfitNames = { "無", "防護服", "科學家", "便服", "金屬甲" };
        private static readonly string[][] OutfitItems =
        {
            new string[0],
            new[] { "hazmatsuit" },
            new[] { "hazmatsuit_scientist" },
            new[] { "hoodie", "pants", "shoes.boots" },
            new[] { "metal.facemask", "metal.plate.torso", "roadsign.kilt", "hoodie", "pants", "tactical.gloves", "shoes.boots" }
        };
        private static readonly string[] WeaponNames = { "AK-47", "LR-300", "MP5", "泵動霰彈槍", "栓動步槍" };
        private static readonly string[] WeaponItems = { "rifle.ak", "rifle.lr300", "smg.mp5", "shotgun.pump", "rifle.bolt" };
        private const int LookCount = 1000;
        private readonly Dictionary<BaseEntity, GuardSettings> guardSettings = new Dictionary<BaseEntity, GuardSettings>();
        private readonly Dictionary<BaseEntity, int> npcNumbers = new Dictionary<BaseEntity, int>();
        private int nextNpcNumber = 1;
        private readonly HashSet<BaseEntity> running = new HashSet<BaseEntity>();
        private class OwnerMotion
        {
            public Vector3 LastPosition;
            public Vector3 Heading;
            public float Speed;
        }
        // The server never rotates a real player's transform (only eyes), so the
        // formation is oriented by the owner's actual walking direction instead.
        private readonly Dictionary<BasePlayer, OwnerMotion> ownerMotion = new Dictionary<BasePlayer, OwnerMotion>();
        private readonly HashSet<BaseEntity> walking = new HashSet<BaseEntity>();
        private bool femaleGuards = true;
        private bool stripGuardEquipment = true;
        private readonly List<BaseEntity> npcs = new List<BaseEntity>();
        private readonly Dictionary<BaseEntity, BasePlayer> followers = new Dictionary<BaseEntity, BasePlayer>();
        private readonly Dictionary<BaseEntity, int> formationSlots = new Dictionary<BaseEntity, int>();
        private readonly List<BaseEntity> stopped = new List<BaseEntity>();
        private class GuardState
        {
            public BasePlayer Owner;
            public BaseCombatEntity Target;
            public float AlertUntil;
            public float NextShot;
            public float ReloadUntil;
        }
        private readonly Dictionary<BaseEntity, GuardState> guards = new Dictionary<BaseEntity, GuardState>();
        private readonly Dictionary<BasePlayer, float> reviving = new Dictionary<BasePlayer, float>();
        private const string MenuName = "NpcStarter.GuardMenu";

        // Guards are not saved by the game (enableSaving = false, and Unload kills
        // them), so they are persisted in oxide/data/NpcStarter.json and respawned.
        public class SavedNpc
        {
            public int Number;
            public float X, Y, Z, Yaw;
            // Who it follows (0 = nobody) and whether it follows as an armed guard.
            public ulong OwnerId;
            public bool Guard;
            public GuardSettings Settings = new GuardSettings();
            // Backpack (main inventory) contents.
            public List<SavedItem> Items = new List<SavedItem>();
            // Clothing and belt. Null in saves from before players could equip guards:
            // those guards get their menu outfit instead.
            public List<SavedItem> Wear;
            public List<SavedItem> Belt;
        }
        public class SavedItem
        {
            public string Shortname;
            public int Amount;
            public ulong Skin;
            public int Slot = -1;
            public float Condition = -1f;
            // Issued by the plugin (menu outfit/weapon, gathering tools), not by a player.
            public bool Bound;
            // Weapon attachments and other nested items.
            public List<SavedItem> Contents;
        }
        public class StoredData
        {
            public bool AutoTeleport = true;
            public float TeleportDistance = DefaultTeleportDistance;
            public bool FemaleGuards = true;
            public int NextNumber = 1;
            public List<SavedNpc> Npcs = new List<SavedNpc>();
        }
        private class PendingOwner
        {
            public ulong OwnerId;
            public bool Guard;
        }
        // NPCs waiting for their owner to be online and awake before following again.
        private readonly Dictionary<BaseEntity, PendingOwner> pendingOwners = new Dictionary<BaseEntity, PendingOwner>();

        private void OnServerInitialized()
        {
            Puts("NpcStarter ready. Admin: /npcdemo add; player: /npcdemo talk");
            ReserveGuardIdRange();
            RemoveOrphanNpcs();
            LoadNpcs();
            timer.Every(FollowTick, UpdateFollowers);
            timer.Every(PositionSendInterval, StreamPositions);
            timer.Every(1f, WorkTick);
            timer.Every(60f, FixDuplicateUserIds);
            // Keep open menus up to date (health, status, distance, backpack).
            timer.Every(1f, () =>
            {
                foreach (var player in new List<BasePlayer>(openMenus))
                {
                    if (player == null || !player.IsConnected) { openMenus.Remove(player); continue; }
                    RenderLive(player);
                }
            });
            timer.Once(3f, FixDuplicateUserIds);
        }

        private void OnServerSave() => SaveNpcs();

        private void SaveNpcs()
        {
            RemoveDestroyedReferences();
            var data = new StoredData
            {
                AutoTeleport = autoTeleport,
                TeleportDistance = teleportDistance,
                FemaleGuards = femaleGuards,
                NextNumber = nextNpcNumber
            };
            foreach (var npc in npcs)
            {
                BasePlayer owner;
                PendingOwner pending;
                var saved = new SavedNpc
                {
                    Number = GetNpcNumber(npc),
                    X = npc.transform.position.x,
                    Y = npc.transform.position.y,
                    Z = npc.transform.position.z,
                    Yaw = npc.transform.eulerAngles.y,
                    Settings = GetGuardSettings(npc)
                };
                var inventory = (npc as BasePlayer)?.inventory;
                if (inventory != null)
                {
                    saved.Items = SaveItems(inventory.containerMain);
                    saved.Wear = SaveItems(inventory.containerWear);
                    saved.Belt = SaveItems(inventory.containerBelt);
                }
                if (followers.TryGetValue(npc, out owner) && owner != null)
                {
                    saved.OwnerId = owner.userID.Get();
                    saved.Guard = guards.ContainsKey(npc);
                }
                else if (pendingOwners.TryGetValue(npc, out pending))
                {
                    saved.OwnerId = pending.OwnerId;
                    saved.Guard = pending.Guard;
                }
                data.Npcs.Add(saved);
            }
            Oxide.Core.Interface.Oxide.DataFileSystem.WriteObject(Name, data);
        }

        private void LoadNpcs()
        {
            StoredData data;
            try { data = Oxide.Core.Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name); }
            catch (System.Exception ex) { PrintWarning("Could not read saved NPCs: " + ex.Message); return; }
            if (data?.Npcs == null) return;
            femaleGuards = data.FemaleGuards;
            autoTeleport = data.AutoTeleport;
            teleportDistance = Mathf.Clamp(data.TeleportDistance, MinTeleportDistance, MaxTeleportDistance);
            nextNpcNumber = Mathf.Max(1, data.NextNumber);
            var restored = 0;
            foreach (var saved in data.Npcs)
            {
                if (npcs.Count >= MaxNpcs) break;
                var settings = saved.Settings ?? new GuardSettings();
                var entity = CreateNpc(SnapToGround(new Vector3(saved.X, saved.Y, saved.Z)), Quaternion.Euler(0f, saved.Yaw, 0f), settings, saved.Wear == null);
                if (entity == null) continue;
                npcs.Add(entity);
                guardSettings[entity] = settings;
                npcNumbers[entity] = saved.Number > 0 ? saved.Number : nextNpcNumber++;
                nextNpcNumber = Mathf.Max(nextNpcNumber, npcNumbers[entity] + 1);
                ApplyName(entity);
                var inventory = (entity as BasePlayer)?.inventory;
                if (inventory != null)
                {
                    RestoreItems(inventory.containerMain, saved.Items);
                    RestoreItems(inventory.containerWear, saved.Wear);
                    RestoreItems(inventory.containerBelt, saved.Belt);
                    entity.SendNetworkUpdate();
                }
                if (saved.OwnerId != 0) pendingOwners[entity] = new PendingOwner { OwnerId = saved.OwnerId, Guard = saved.Guard };
                restored++;
            }
            if (restored > 0) Puts($"Restored {restored} saved NPC(s).");
            foreach (var player in BasePlayer.activePlayerList) ResumePendingFollowers(player);
        }

        // Called when an owner is online and awake: saved or paused NPCs rejoin them.
        private void ResumePendingFollowers(BasePlayer player)
        {
            if (player == null || !player.IsConnected || player.IsSleeping() || player.IsDead()) return;
            var ownerId = player.userID.Get();
            foreach (var entry in new List<KeyValuePair<BaseEntity, PendingOwner>>(pendingOwners))
            {
                var npc = entry.Key;
                if (npc == null || npc.IsDestroyed) { pendingOwners.Remove(npc); continue; }
                if (entry.Value.OwnerId != ownerId) continue;
                if (ActivateCompanion(npc, player, entry.Value.Guard)) pendingOwners.Remove(npc);
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            CheckNpcHealth(player);
            BindMenuKeyLater(player, 5f);
        }

        private void OnPlayerInit(BasePlayer player)
        {
            BindMenuKeyLater(player, 5f);
        }

        private void BindMenuKeyLater(BasePlayer player, float delay)
        {
            if (player == null) return;
            timer.Once(delay, () =>
            {
                if (player == null || !player.IsConnected || !player.IsAdmin || player.net?.connection == null) return;
                rust.RunClientCommand(player, "bind f6 npcmenu.open");
                rust.RunClientCommand(player, "writecfg");
                Puts($"Sent F6 menu bind to {player.displayName} ({player.UserIDString}).");
                SendReply(player, "護衛選單已自動綁定到 F6。輸入 /npcmenu 也可以開啟。");
            });
        }

        [ChatCommand("npcdemo")]
        private void NpcDemoCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (args.Length == 0)
            {
                SendReply(player, player.IsAdmin
                    ? "Usage: /npcdemo add | guard | follow | stop | remove | talk | count"
                    : "Stand near an NPC and type /npcdemo talk");
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "guard":
                case "follow":
                    if (!player.IsAdmin) { SendReply(player, "Admin only."); return; }
                    var companions = FindNearby(player, 12f);
                    if (companions.Count == 0) { SendReply(player, "No demo NPC within 12 meters. Use /npcdemo add first."); return; }
                    var guardMode = args[0].ToLowerInvariant() == "guard";
                    var activated = 0;
                    foreach (var companion in companions)
                        if (ActivateCompanion(companion, player, guardMode)) activated++;
                    SendReply(player, guardMode
                        ? $"{activated} NPC(s) are now guarding and following you."
                        : $"{activated} NPC(s) are now following you.");
                    break;

                case "stop":
                    if (!player.IsAdmin) { SendReply(player, "Admin only."); return; }
                    stopped.Clear();
                    foreach (var entry in followers)
                        if (entry.Value == player) stopped.Add(entry.Key);
                    foreach (var npc in stopped)
                    {
                        StopFollowing(npc);
                        FaceDirection(npc, player.transform.position - npc.transform.position);
                        npc.SendNetworkUpdate();
                    }
                    SendReply(player, "Your following NPCs are now waiting.");
                    break;
                case "talk":
                    var nearby = FindNearest(player, TalkRange);
                    SendReply(player, nearby == null
                        ? "No demo NPC within 4 meters."
                        : "NPC: Welcome! This is a first dialogue test. Quests and trading can be added next.");
                    break;

                case "add":
                    if (!player.IsAdmin) { SendReply(player, "Admin only."); return; }
                    RemoveDestroyedReferences();
                    if (npcs.Count >= MaxNpcs) { SendReply(player, "NPC limit reached (20)."); return; }
                    var newSettings = new GuardSettings
                    {
                        Female = femaleGuards,
                        LookIndex = Random.Range(0, LookCount),
                        Outfit = stripGuardEquipment ? 0 : 1
                    };
                    var entity = CreateNpc(GetSpawnPosition(player), player.transform.rotation, newSettings);
                    if (entity == null) { SendReply(player, "NPC failed to spawn. Try level ground."); return; }
                    npcs.Add(entity);
                    guardSettings[entity] = newSettings;
                    npcNumbers[entity] = nextNpcNumber++;
                    ApplyName(entity);
                    FaceDirection(entity, player.transform.position - entity.transform.position, true);
                    entity.SendNetworkUpdate();
                    SendReply(player, "Demo NPC created. Stand nearby and type /npcdemo talk.");
                    break;

                case "remove":
                    if (!player.IsAdmin) { SendReply(player, "Admin only."); return; }
                    var target = FindNearest(player, RemoveRange);
                    if (target == null) { SendReply(player, "No demo NPC within 6 meters."); return; }
                    npcs.Remove(target);
                    StopFollowing(target);
                    ForgetNpc(target);
                    KillNpc(target);
                    SendReply(player, "Demo NPC removed.");
                    break;

                case "count":
                    if (!player.IsAdmin) { SendReply(player, "Admin only."); return; }
                    RemoveDestroyedReferences();
                    SendReply(player, "Active demo NPCs: " + npcs.Count);
                    break;

                default:
                    SendReply(player, "Unknown action. Type /npcdemo for help.");
                    break;
            }
            SaveNpcs();
        }

        [ChatCommand("npcmenu")]
        private void NpcMenuCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!player.IsAdmin)
            {
                SendReply(player, "Admin only.");
                return;
            }
            BindMenuKeyLater(player, 0.1f);
            ShowMenu(player);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            BindMenuKeyLater(player, 0.5f);
            ResumePendingFollowers(player);
        }

        [ConsoleCommand("npcmenu.action")]
        private void NpcMenuAction(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin) return;
            var action = arg.Args == null || arg.Args.Length == 0 ? "close" : arg.Args[0].ToString().ToLowerInvariant();
            if (action == "close")
            {
                CloseMenu(player);
                return;
            }
            if (action == "addguard")
            {
                var before = npcs.Count;
                NpcDemoCommand(player, "npcdemo", new[] { "add" });
                NpcDemoCommand(player, "npcdemo", new[] { "guard" });
                // Show the new recruit.
                if (npcs.Count > before) SelectNpc(player, npcs[npcs.Count - 1]);
            }
            else if (action == "togglegender")
            {
                femaleGuards = !femaleGuards;
                ApplyGenderToNpcs();
                SendReply(player, femaleGuards ? "護衛性別已設定為女性。" : "護衛性別已設定為男性。");
            }
            else if (action == "add" || action == "guard" || action == "follow" || action == "stop" || action == "remove")
                NpcDemoCommand(player, "npcdemo", new[] { action });
            else if (action == "removeall")
                RemoveAllNpcs(player);
            else if (action == "toggleteleport")
                autoTeleport = !autoTeleport;
            else if (action == "teleportup" || action == "teleportdown")
                teleportDistance = Mathf.Clamp(teleportDistance + (action == "teleportup" ? TeleportStep : -TeleportStep),
                    MinTeleportDistance, MaxTeleportDistance);
            SaveNpcs();
            ShowMenu(player);
        }

        [ConsoleCommand("npcmenu.guard")]
        private void NpcMenuGuard(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin || arg.Args == null || arg.Args.Length < 2) return;
            ulong id;
            if (!ulong.TryParse(arg.GetString(0), out id)) return;
            var npc = npcs.Find(n => n != null && !n.IsDestroyed && n.net != null && n.net.ID.Value == id);
            if (npc == null) { ShowMenu(player); return; }
            var settings = GetGuardSettings(npc);
            var step = arg.GetString(2) == "-" ? -DistanceStep : DistanceStep;
            switch (arg.GetString(1).ToLowerInvariant())
            {
                case "min":
                    settings.MinDistance = Mathf.Clamp(settings.MinDistance + step, MinDistanceLimit, MaxDistanceLimit - MinDistanceGap);
                    settings.MaxDistance = Mathf.Max(settings.MaxDistance, settings.MinDistance + MinDistanceGap);
                    break;
                case "max":
                    settings.MaxDistance = Mathf.Clamp(settings.MaxDistance + step, MinDistanceLimit + MinDistanceGap, MaxDistanceLimit);
                    settings.MinDistance = Mathf.Min(settings.MinDistance, settings.MaxDistance - MinDistanceGap);
                    break;
                case "sight":
                    settings.SightRange = Mathf.Clamp(settings.SightRange + (step > 0f ? SightStep : -SightStep), MinSightRange, MaxSightRange);
                    break;
                case "mode":
                    settings.Aggressive = !settings.Aggressive;
                    break;
                case "damage":
                    settings.Invulnerable = !settings.Invulnerable;
                    break;
                case "aggressive":
                    settings.Aggressive = arg.GetString(2) == "1";
                    break;
                case "invulnerable":
                    settings.Invulnerable = arg.GetString(2) == "1";
                    break;
                case "look":
                    SelectNpc(player, npc);
                    GetMenuState(player).Tab = "look";
                    ShowMenu(player);
                    return;
                case "remove":
                    npcs.Remove(npc);
                    StopFollowing(npc);
                    ForgetNpc(npc);
                    KillNpc(npc);
                    break;
            }
            SaveNpcs();
            ShowMenu(player);
        }

        // Sent by the name input field in the guard list: npcmenu.name <id> <text...>
        [ConsoleCommand("npcmenu.name")]
        private void NpcMenuName(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin || arg.Args == null || arg.Args.Length < 1) return;
            ulong id;
            if (!ulong.TryParse(arg.GetString(0), out id)) return;
            var npc = npcs.Find(n => n != null && !n.IsDestroyed && n.net != null && n.net.ID.Value == id);
            if (npc == null) { ShowMenu(player); return; }
            var words = new List<string>();
            for (var i = 1; i < arg.Args.Length; i++) words.Add(arg.GetString(i));
            GetGuardSettings(npc).Name = SanitizeName(string.Join(" ", words));
            ApplyName(npc);
            SaveNpcs();
            ShowMenu(player);
        }

        [ConsoleCommand("npcmenu.look")]
        private void NpcMenuLook(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin || arg.Args == null || arg.Args.Length < 2) return;
            ulong id;
            if (!ulong.TryParse(arg.GetString(0), out id)) return;
            var npc = npcs.Find(n => n != null && !n.IsDestroyed && n.net != null && n.net.ID.Value == id);
            if (npc == null) { ShowMenu(player); return; }
            var settings = GetGuardSettings(npc);
            switch (arg.GetString(1).ToLowerInvariant())
            {
                case "female":
                case "male":
                    var female = arg.GetString(1).ToLowerInvariant() == "female";
                    if (settings.Female == female) break;
                    settings.Female = female;
                    npc = RespawnNpc(npc);
                    break;
                case "prev":
                case "next":
                case "random":
                    var how = arg.GetString(1).ToLowerInvariant();
                    settings.LookIndex = how == "random" ? Random.Range(0, LookCount)
                        : (settings.LookIndex + (how == "next" ? 1 : LookCount - 1)) % LookCount;
                    npc = RespawnNpc(npc);
                    break;
                case "outfit":
                    settings.Outfit = Mathf.Clamp(arg.GetInt(2), 0, OutfitItems.Length - 1);
                    ApplyOutfit(npc as BasePlayer, settings);
                    break;
                case "weapon":
                    settings.Weapon = Mathf.Clamp(arg.GetInt(2), 0, WeaponItems.Length - 1);
                    RemoveIssuedGuns(npc as BasePlayer);
                    if (guards.ContainsKey(npc)) EquipGuard(npc as BasePlayer);
                    break;
                case "back":
                    ShowMenu(player);
                    return;
            }
            SaveNpcs();
            if (npc == null) SendReply(player, "護衛重新生成失敗，請換個位置再試。");
            // Respawning gives the guard a new entity id: keep it selected.
            else SelectNpc(player, npc);
            ShowMenu(player);
        }

        [ConsoleCommand("npcmenu.page")]
        private void NpcMenuPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin) return;
            GetMenuState(player).Page += arg.GetInt(0);
            ShowMenu(player);
        }

        [ConsoleCommand("npcmenu.open")]
        private void NpcMenuOpen(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin) return;
            // F6 toggles: a second press closes the menu.
            if (openMenus.Contains(player)) CloseMenu(player);
            else ShowMenu(player);
        }

        [ConsoleCommand("npcmenu.bind")]
        private void NpcMenuBind(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length != 1) { arg.ReplyWith("npcmenu.bind STEAMID"); return; }
            var player = BasePlayer.FindByID(arg.GetULong(0));
            if (player == null || !player.IsConnected || !player.IsAdmin) { arg.ReplyWith("An online admin is required."); return; }
            BindMenuKeyLater(player, 0.1f);
            arg.ReplyWith("F6 bind queued.");
        }

        // Rust-style palette: dark translucent panels, warm off-white text and the
        // game's muted green / red / blue button colours.
        private const string UiText = "0.87 0.84 0.79 1";
        private const string UiDimText = "0.60 0.58 0.54 1";
        private const string UiHeader = "0.15 0.15 0.14 0.98";
        private const string UiRowA = "0.13 0.13 0.12 0.85";
        private const string UiRowB = "0.17 0.17 0.16 0.85";
        private const string UiGrey = "0.30 0.29 0.27 0.95";
        private const string UiGreen = "0.42 0.53 0.24 1";
        private const string UiRed = "0.65 0.24 0.17 1";
        private const string UiDarkRed = "0.42 0.17 0.13 1";
        private const string UiBlue = "0.23 0.40 0.55 1";
        private const string UiGold = "0.62 0.50 0.20 1";
        private const string BoldFont = "robotocondensed-bold.ttf";
        private const string RegularFont = "robotocondensed-regular.ttf";
        private const string UiPanel = "0.09 0.09 0.085 0.9";
        private const string UiSlot = "0.17 0.17 0.16 0.9";
        private const string UiSelected = "0.34 0.33 0.30 0.95";
        private const string UiInputBack = "0.05 0.05 0.05 0.9";

        // ---- F6 menu ----
        // Layout after Dune: Awakening's character screen, look and colours from Rust:
        // tab bar on top, roster on the left, the centre left open so the selected guard
        // itself can stand there (live preview), tab content on the right and the
        // backpack along the bottom. Changing values are redrawn every second in
        // separate "live" layers, so buttons and the name field are not rebuilt.
        private const string MenuTop = MenuName + ".Top";
        private const string MenuLeft = MenuName + ".Left";
        private const string MenuRight = MenuName + ".Right";
        private const string MenuBottom = MenuName + ".Bottom";
        private const string MenuRightLive = MenuName + ".RightLive";
        private const string MenuBottomLive = MenuName + ".BottomLive";
        private static readonly string[] MenuTabs = { "overview", "combat", "look", "work", "craft", "global" };
        private static readonly string[] MenuTabNames = { "總覽", "戰鬥", "外觀", "工作", "製作", "全體設定" };
        private const int RosterPerPage = 8;
        private const float PreviewMaxDistance = 40f;

        private class MenuState
        {
            public string Tab = "overview";
            public ulong Selected;
            public int Page;
        }
        private readonly Dictionary<ulong, MenuState> menuStates = new Dictionary<ulong, MenuState>();
        private readonly HashSet<BasePlayer> openMenus = new HashSet<BasePlayer>();

        private MenuState GetMenuState(BasePlayer player)
        {
            MenuState state;
            var key = player.userID.Get();
            if (!menuStates.TryGetValue(key, out state)) menuStates[key] = state = new MenuState();
            return state;
        }

        private BaseEntity FindNpcById(ulong id)
        {
            return npcs.Find(n => n != null && !n.IsDestroyed && n.net != null && n.net.ID.Value == id);
        }

        private void SelectNpc(BasePlayer player, BaseEntity npc)
        {
            if (player != null && npc?.net != null) GetMenuState(player).Selected = npc.net.ID.Value;
        }

        private List<BaseEntity> SortedNpcs()
        {
            RemoveDestroyedReferences();
            var list = new List<BaseEntity>(npcs);
            list.Sort((a, b) => GetNpcNumber(a).CompareTo(GetNpcNumber(b)));
            return list;
        }

        private static string RosterRow(int row)
        {
            return MenuName + ".Row" + row;
        }

        private void ShowMenu(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            DestroyMenu(player);
            var state = GetMenuState(player);
            var list = SortedNpcs();
            var npc = FindNpcById(state.Selected);
            if (npc == null && list.Count > 0) npc = list[0];
            state.Selected = npc?.net?.ID.Value ?? 0;
            // Opening the menu never takes control of a guard; only the appearance tab's
            // "preview" button calls one over. Drop a preview of a guard no longer selected.
            BasePlayer previewer;
            if (npc == null || !previews.TryGetValue(npc, out previewer) || previewer != player) EndPreview(player);
            openMenus.Add(player);

            var c = new CuiElementContainer();
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", MenuName);
            AddRegion(c, MenuTop, UiHeader, "0.03 0.905", "0.97 0.955");
            AddRegion(c, MenuLeft, UiPanel, "0.03 0.30", "0.235 0.895");
            AddRegion(c, MenuRight, UiPanel, "0.645 0.30", "0.97 0.895");
            AddRegion(c, MenuBottom, UiPanel, "0.03 0.05", "0.97 0.29");

            for (var i = 0; i < MenuTabs.Length; i++)
            {
                var x = 0.005f + i * 0.105f;
                AddMenuButton(c, MenuTop, MenuTabNames[i], $"npcui.tab {MenuTabs[i]}", $"{x:F3} 0.12", $"{x + 0.1f:F3} 0.88",
                    state.Tab == MenuTabs[i] ? UiBlue : UiGrey, 15);
            }
            AddLabel(c, MenuTop, $"護衛 {npcs.Count} / {MaxNpcs}", 15, TextAnchor.MiddleRight, UiDimText, "0.70 0", "0.935 1", BoldFont);
            AddMenuButton(c, MenuTop, "X", "npcui.close", "0.945 0.12", "0.995 0.88", UiRed, 16);

            BuildRoster(c, state, list, npc);
            if (state.Tab == "global") BuildGlobalTab(c);
            else if (npc == null)
                AddLabel(c, MenuRight, "還沒有護衛。\n按左下角「招募護衛」產生一隻。", 16, TextAnchor.MiddleCenter, UiDimText, "0.05 0.3", "0.95 0.7", RegularFont);
            else
                switch (state.Tab)
                {
                    case "combat": BuildCombatTab(c, npc); break;
                    case "look": BuildLookTab(c, npc); break;
                    case "work": BuildWorkTab(c, npc); break;
                    case "craft": BuildCraftTab(c, npc); break;
                    default: BuildOverviewTab(c, npc); break;
                }
            for (var i = 0; i < 24; i++)
            {
                var slot = SlotRect(i);
                AddPanel(c, MenuBottom, UiSlot, slot[0], slot[1]);
            }
            CuiHelper.AddUi(player, c);
            RenderLive(player);
        }

        private void CloseMenu(BasePlayer player)
        {
            if (player == null) return;
            openMenus.Remove(player);
            EndPreview(player);
            DestroyMenu(player);
        }

        private static void AddRegion(CuiElementContainer c, string name, string color, string min, string max)
        {
            c.Add(new CuiPanel
            {
                Image = { Color = color, Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, MenuName, name);
        }

        // Backpack grid: 12 x 2 slots on the left part of the bottom panel.
        private static string[] SlotRect(int index)
        {
            var column = index % 12;
            var row = index / 12;
            var x = 0.01f + column * 0.056f;
            var top = row == 0 ? 0.83f : 0.43f;
            return new[] { $"{x:F3} {top - 0.38f:F3}", $"{x + 0.052f:F3} {top:F3}" };
        }

        private void BuildRoster(CuiElementContainer c, MenuState state, List<BaseEntity> list, BaseEntity selected)
        {
            AddLabel(c, MenuLeft, "名單", 14, TextAnchor.MiddleLeft, UiDimText, "0.05 0.935", "0.95 0.99", BoldFont);
            var pages = Mathf.Max(1, (list.Count + RosterPerPage - 1) / RosterPerPage);
            state.Page = Mathf.Clamp(state.Page, 0, pages - 1);
            for (var row = 0; row < RosterPerPage; row++)
            {
                var index = state.Page * RosterPerPage + row;
                if (index >= list.Count) break;
                var npc = list[index];
                var top = 0.925f - row * 0.1f;
                var isSelected = npc == selected;
                // Labels are children of the row button so clicks anywhere on it select.
                c.Add(new CuiButton
                {
                    Button = { Color = isSelected ? UiSelected : UiRowA, Command = $"npcui.select {npc.net.ID.Value}", Close = "" },
                    Text = { Text = "" },
                    RectTransform = { AnchorMin = $"0.03 {top - 0.092f:F3}", AnchorMax = $"0.97 {top:F3}" }
                }, MenuLeft, RosterRow(row));
                if (isSelected) AddPanel(c, RosterRow(row), UiGreen, "0 0", "0.02 1");
                AddLabel(c, RosterRow(row), GetDisplayName(npc), 15, TextAnchor.MiddleLeft, UiText, "0.06 0.55", "0.97 0.95", BoldFont);
            }
            AddMenuButton(c, MenuLeft, "＋ 招募護衛", "npcmenu.action addguard", "0.03 0.015", pages > 1 ? "0.60 0.085" : "0.97 0.085", UiGreen, 15);
            if (pages > 1)
            {
                AddMenuButton(c, MenuLeft, "<", "npcmenu.page -1", "0.63 0.015", "0.79 0.085", UiGrey, 16);
                AddMenuButton(c, MenuLeft, ">", "npcmenu.page 1", "0.81 0.015", "0.97 0.085", UiGrey, 16);
            }
        }

        private void AddTabTitle(CuiElementContainer c, string title, string note = null)
        {
            AddLabel(c, MenuRight, title, 18, TextAnchor.MiddleLeft, UiText, "0.04 0.935", "0.50 0.99", BoldFont);
            if (note != null) AddLabel(c, MenuRight, note, 12, TextAnchor.MiddleRight, UiDimText, "0.45 0.935", "0.96 0.99", RegularFont);
        }

        private void AddSectionLabel(CuiElementContainer c, string text, float y0, float y1)
        {
            AddLabel(c, MenuRight, text, 14, TextAnchor.MiddleLeft, UiDimText, $"0.04 {y0:F3}", $"0.96 {y1:F3}", BoldFont);
        }

        private void AddChoice(CuiElementContainer c, string text, string command, float x0, float y0, float x1, float y1, bool selected, int size = 15)
        {
            AddMenuButton(c, MenuRight, text, command, $"{x0:F3} {y0:F3}", $"{x1:F3} {y1:F3}", selected ? UiGreen : UiGrey, size);
        }

        // "label   [-]  value  [+]" on one line of the right panel.
        private void AddPanelStepper(CuiElementContainer c, string label, string minusCommand, string plusCommand, string value, float y0, float y1)
        {
            AddLabel(c, MenuRight, label, 15, TextAnchor.MiddleLeft, UiText, $"0.04 {y0:F3}", $"0.44 {y1:F3}", BoldFont);
            AddMenuButton(c, MenuRight, "-", minusCommand, $"0.45 {y0:F3}", $"0.57 {y1:F3}", UiGrey, 18);
            AddPanel(c, MenuRight, UiInputBack, $"0.58 {y0:F3}", $"0.83 {y1:F3}");
            AddLabel(c, MenuRight, value, 16, TextAnchor.MiddleCenter, UiText, $"0.58 {y0:F3}", $"0.83 {y1:F3}", BoldFont);
            AddMenuButton(c, MenuRight, "+", plusCommand, $"0.84 {y0:F3}", $"0.96 {y1:F3}", UiGrey, 18);
        }

        private string RoleName(BaseEntity npc)
        {
            var settings = GetGuardSettings(npc);
            if (settings.Work != WorkNone) return WorkNames[settings.Work];
            return guards.ContainsKey(npc) ? "護衛" : followers.ContainsKey(npc) ? "隨從" : "待命";
        }

        private void BuildOverviewTab(CuiElementContainer c, BaseEntity npc)
        {
            var settings = GetGuardSettings(npc);
            var id = npc.net.ID.Value;
            AddLabel(c, MenuRight, "名稱（點一下修改，按 Enter 儲存）", 12, TextAnchor.MiddleLeft, UiDimText, "0.04 0.94", "0.96 0.99", RegularFont);
            AddPanel(c, MenuRight, UiInputBack, "0.04 0.87", "0.96 0.935");
            c.Add(new CuiElement
            {
                Parent = MenuRight,
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Text = GetDisplayName(npc),
                        Command = $"npcmenu.name {id}",
                        CharsLimit = MaxNameLength,
                        FontSize = 20,
                        Font = BoldFont,
                        Align = TextAnchor.MiddleLeft,
                        Color = UiText,
                        NeedsKeyboard = true
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.06 0.87", AnchorMax = "0.94 0.935" }
                }
            });
            AddLabel(c, MenuRight,
                $"#{GetNpcNumber(npc)} · {RoleName(npc)} · {WeaponLabel(npc as BasePlayer)} · {(settings.Female ? "女性" : "男性")} 外觀 #{settings.LookIndex + 1}",
                13, TextAnchor.MiddleLeft, UiDimText, "0.04 0.82", "0.96 0.865", RegularFont);
            // Status rows (0.33 - 0.81) are drawn by RenderLive.
            AddMenuButton(c, MenuRight, "開啟背包與裝備", $"npcui.loot {id}", "0.04 0.28", "0.96 0.33", UiBlue, 14);
            AddMenuButton(c, MenuRight, "跟隨我", $"npcui.cmd {id} follow", "0.04 0.165", "0.49 0.265", UiGreen, 16);
            AddMenuButton(c, MenuRight, "原地待命", $"npcui.cmd {id} stay", "0.51 0.165", "0.96 0.265", UiGrey, 16);
            AddMenuButton(c, MenuRight, "叫回身邊", $"npcui.cmd {id} recall", "0.04 0.04", "0.49 0.14", UiBlue, 16);
            AddMenuButton(c, MenuRight, "解散", $"npcmenu.guard {id} remove", "0.51 0.04", "0.96 0.14", UiDarkRed, 16);
        }

        private void BuildCombatTab(CuiElementContainer c, BaseEntity npc)
        {
            var settings = GetGuardSettings(npc);
            var id = npc.net.ID.Value;
            AddTabTitle(c, "戰鬥");
            AddSectionLabel(c, "攻擊模式", 0.86f, 0.91f);
            AddChoice(c, "主動攻擊", $"npcmenu.guard {id} aggressive 1", 0.04f, 0.78f, 0.49f, 0.855f, settings.Aggressive);
            AddChoice(c, "被動反擊", $"npcmenu.guard {id} aggressive 0", 0.51f, 0.78f, 0.96f, 0.855f, !settings.Aggressive);
            AddLabel(c, MenuRight, "主動：看到敵人就開火。被動：你或護衛被攻擊後才反擊。", 12, TextAnchor.MiddleLeft, UiDimText, "0.04 0.735", "0.96 0.775", RegularFont);
            AddSectionLabel(c, "受傷", 0.67f, 0.72f);
            AddChoice(c, "會受傷", $"npcmenu.guard {id} invulnerable 0", 0.04f, 0.59f, 0.49f, 0.665f, !settings.Invulnerable);
            AddChoice(c, "無敵", $"npcmenu.guard {id} invulnerable 1", 0.51f, 0.59f, 0.96f, 0.665f, settings.Invulnerable);
            AddPanelStepper(c, "視距", $"npcmenu.guard {id} sight -", $"npcmenu.guard {id} sight +", $"{settings.SightRange:F0} m", 0.475f, 0.55f);
            AddPanelStepper(c, "最近距離", $"npcmenu.guard {id} min -", $"npcmenu.guard {id} min +", $"{settings.MinDistance:F1} m", 0.375f, 0.45f);
            AddPanelStepper(c, "最遠距離", $"npcmenu.guard {id} max -", $"npcmenu.guard {id} max +", $"{settings.MaxDistance:F1} m", 0.275f, 0.35f);
            AddLabel(c, MenuRight,
                "視距：發現、射擊與反擊敵人的最遠距離。\n跟隨距離：你走遠超過最遠距離它才跟上；太靠近時會退到最近距離（你看著它時不會退開）。",
                12, TextAnchor.UpperLeft, UiDimText, "0.04 0.04", "0.96 0.24", RegularFont);
        }

        private void BuildLookTab(CuiElementContainer c, BaseEntity npc)
        {
            var settings = GetGuardSettings(npc);
            var id = npc.net.ID.Value;
            AddTabTitle(c, "外觀");
            // Optional live preview: only now does the guard walk over to the viewer.
            BasePlayer previewer;
            var previewing = previews.TryGetValue(npc, out previewer) && previewer != null;
            AddMenuButton(c, MenuRight, previewing ? "結束預覽" : "叫到面前預覽", $"npcui.preview {id}", "0.52 0.94", "0.96 0.99",
                previewing ? UiGreen : UiBlue, 13);
            AddSectionLabel(c, "性別", 0.86f, 0.91f);
            AddChoice(c, "女性", $"npcmenu.look {id} female", 0.04f, 0.78f, 0.49f, 0.855f, settings.Female);
            AddChoice(c, "男性", $"npcmenu.look {id} male", 0.51f, 0.78f, 0.96f, 0.855f, !settings.Female);
            AddSectionLabel(c, "臉型／體型", 0.70f, 0.75f);
            AddMenuButton(c, MenuRight, "<", $"npcmenu.look {id} prev", "0.04 0.62", "0.16 0.695", UiGrey, 18);
            AddPanel(c, MenuRight, UiInputBack, "0.18 0.62", "0.58 0.695");
            AddLabel(c, MenuRight, $"外觀 #{settings.LookIndex + 1}", 16, TextAnchor.MiddleCenter, UiText, "0.18 0.62", "0.58 0.695", BoldFont);
            AddMenuButton(c, MenuRight, ">", $"npcmenu.look {id} next", "0.60 0.62", "0.72 0.695", UiGrey, 18);
            AddMenuButton(c, MenuRight, "隨機", $"npcmenu.look {id} random", "0.74 0.62", "0.96 0.695", UiBlue, 15);
            AddSectionLabel(c, "服裝", 0.545f, 0.59f);
            for (var i = 0; i < OutfitNames.Length; i++)
            {
                var x = 0.04f + i * 0.186f;
                AddIconButton(c, MenuRight, OutfitNames[i], OutfitItems[i].Length > 0 ? OutfitItems[i][0] : null, $"npcmenu.look {id} outfit {i}",
                    $"{x:F3} 0.40", $"{x + 0.176f:F3} 0.54", settings.Outfit == i);
            }
            AddSectionLabel(c, "武器", 0.335f, 0.38f);
            for (var i = 0; i < WeaponNames.Length; i++)
            {
                var x = 0.04f + i * 0.186f;
                AddIconButton(c, MenuRight, WeaponNames[i], WeaponItems[i], $"npcmenu.look {id} weapon {i}",
                    $"{x:F3} 0.19", $"{x + 0.176f:F3} 0.33", settings.Weapon == i);
            }
            AddLabel(c, MenuRight, "這裡選的是配給服裝／武器；按 E 打開護衛可以換上你自己的裝備（你給的槍優先使用）。配給槍和子彈可以拿走，拿走後會再發一把；配給服裝拿出來會消失。變更性別或臉型會原地重新生成，名稱、設定、背包與裝備都會保留。",
                11, TextAnchor.UpperLeft, UiDimText, "0.04 0.02", "0.96 0.17", RegularFont);
        }

        private void BuildWorkTab(CuiElementContainer c, BaseEntity npc)
        {
            var settings = GetGuardSettings(npc);
            var id = npc.net.ID.Value;
            AddTabTitle(c, "工作");
            AddSectionLabel(c, "工作模式", 0.87f, 0.92f);
            for (var i = 0; i < WorkNames.Length; i++)
            {
                var x0 = i % 2 == 0 ? 0.04f : 0.51f;
                var top = i < 2 ? 0.865f : 0.785f;
                AddChoice(c, WorkNames[i], $"npcmenu.work {id} mode {i}", x0, top - 0.07f, x0 + 0.45f, top, settings.Work == i);
            }
            AddLabel(c, MenuRight, WorkHints[settings.Work], 12, TextAnchor.MiddleLeft, UiDimText, "0.04 0.655", "0.96 0.705", RegularFont);
            AddSectionLabel(c, "採集哪些資源（綠色＝會採集）", 0.595f, 0.64f);
            AddMenuButton(c, MenuRight, "全選", $"npcmenu.work {id} gatherall", "0.60 0.598", "0.77 0.638", UiGrey, 13);
            AddMenuButton(c, MenuRight, "全不選", $"npcmenu.work {id} gathernone", "0.79 0.598", "0.96 0.638", UiGrey, 13);
            for (var i = 0; i < GatherCategoryNames.Length; i++)
            {
                var x = 0.04f + (i % 4) * 0.235f;
                var top = i < 4 ? 0.585f : 0.42f;
                AddIconButton(c, MenuRight, GatherCategoryNames[i], GatherCategoryIcons[i], $"npcmenu.work {id} gather {i}",
                    $"{x:F3} {top - 0.155f:F3}", $"{x + 0.225f:F3} {top:F3}", (settings.GatherMask & (1 << i)) != 0);
            }
            AddLabel(c, MenuRight, "擊殺後撿戰利品／剝動物", 15, TextAnchor.MiddleLeft, UiText, "0.04 0.17", "0.62 0.235", BoldFont);
            AddChoice(c, settings.AutoLoot ? "開啟" : "關閉", $"npcmenu.work {id} loot", 0.64f, 0.17f, 0.96f, 0.235f, settings.AutoLoot);
            AddLabel(c, MenuRight, "採集、產出與撿到的東西都會放進下方的背包。", 12, TextAnchor.UpperLeft, UiDimText, "0.04 0.03", "0.96 0.13", RegularFont);
        }

        private void BuildCraftTab(CuiElementContainer c, BaseEntity npc)
        {
            var settings = GetGuardSettings(npc);
            var id = npc.net.ID.Value;
            AddTabTitle(c, "製作與熔煉");
            AddLabel(c, MenuRight, "熔煉礦石", 15, TextAnchor.MiddleLeft, UiText, "0.04 0.855", "0.55 0.915", BoldFont);
            AddChoice(c, settings.Smelt ? "開啟" : "關閉", $"npcmenu.work {id} smelt", 0.60f, 0.855f, 0.96f, 0.915f, settings.Smelt);
            AddLabel(c, MenuRight, "用背包的木頭當燃料，把礦石煉成金屬碎片／硫磺／高級金屬。", 12, TextAnchor.MiddleLeft, UiDimText, "0.04 0.80", "0.96 0.85", RegularFont);
            var crafting = settings.CraftRemaining > 0 && !string.IsNullOrEmpty(settings.CraftItem);
            AddLabel(c, MenuRight, crafting ? $"製作中：{CraftNameOf(settings.CraftItem)}（剩 {settings.CraftRemaining} 個）" : "點一下物品加 1 個，用背包材料製作",
                14, TextAnchor.MiddleLeft, crafting ? UiText : UiDimText, "0.04 0.735", "0.68 0.79", BoldFont);
            if (crafting) AddMenuButton(c, MenuRight, "取消製作", $"npcmenu.work {id} cancel", "0.70 0.74", "0.96 0.79", UiRed, 13);
            for (var i = 0; i < CraftItems.Length; i++)
            {
                var x0 = i % 2 == 0 ? 0.04f : 0.51f;
                var top = 0.72f - (i / 2) * 0.137f;
                AddCraftButton(c, MenuRight, CraftItems[i], CraftNames[i], $"npcmenu.work {id} craft {i}",
                    $"{x0:F3} {top - 0.13f:F3}", $"{x0 + 0.45f:F3} {top:F3}", crafting && settings.CraftItem == CraftItems[i]);
            }
        }

        private void BuildGlobalTab(CuiElementContainer c)
        {
            AddTabTitle(c, "全體設定", "套用到所有護衛");
            AddLabel(c, MenuRight, "離你太遠時自動傳送", 15, TextAnchor.MiddleLeft, UiText, "0.04 0.845", "0.58 0.915", BoldFont);
            AddChoice(c, autoTeleport ? "開啟" : "關閉", "npcmenu.action toggleteleport", 0.60f, 0.845f, 0.96f, 0.915f, autoTeleport);
            AddPanelStepper(c, "觸發距離", "npcmenu.action teleportdown", "npcmenu.action teleportup", $"{teleportDistance:F0} m", 0.745f, 0.82f);
            AddLabel(c, MenuRight, "全部護衛的性別", 15, TextAnchor.MiddleLeft, UiText, "0.04 0.625", "0.58 0.695", BoldFont);
            AddMenuButton(c, MenuRight, femaleGuards ? "女性" : "男性", "npcmenu.action togglegender", "0.60 0.625", "0.96 0.695", UiGrey, 15);
            AddSectionLabel(c, "全體指令", 0.53f, 0.58f);
            AddMenuButton(c, MenuRight, "全體跟隨", "npcui.all follow", "0.04 0.44", "0.49 0.52", UiGreen, 16);
            AddMenuButton(c, MenuRight, "全體待命", "npcui.all stay", "0.51 0.44", "0.96 0.52", UiGrey, 16);
            AddMenuButton(c, MenuRight, "全體集合（傳送到身邊）", "npcui.all recall", "0.04 0.34", "0.96 0.42", UiBlue, 16);
            AddMenuButton(c, MenuRight, "清除全部護衛", "npcmenu.action removeall", "0.04 0.04", "0.96 0.12", UiDarkRed, 16);
        }

        private void AddIconButton(CuiElementContainer c, string parent, string text, string itemShortname, string command, string min, string max, bool selected)
        {
            var name = c.Add(new CuiButton
            {
                Button = { Color = selected ? UiGreen : UiGrey, Command = command, Close = "" },
                Text = { Text = "" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
            var definition = itemShortname == null ? null : ItemManager.FindItemDefinition(itemShortname);
            if (definition != null)
                c.Add(new CuiElement
                {
                    Parent = name,
                    Components =
                    {
                        new CuiImageComponent { ItemId = definition.itemid },
                        new CuiRectTransformComponent { AnchorMin = "0.2 0.32", AnchorMax = "0.8 0.95" }
                    }
                });
            AddLabel(c, name, text, 13, definition != null ? TextAnchor.LowerCenter : TextAnchor.MiddleCenter, UiText,
                definition != null ? "0 0.03" : "0 0", definition != null ? "1 0.32" : "1 1", BoldFont);
        }

        // Wide craft button: icon on the left, name and material cost on the right.
        private void AddCraftButton(CuiElementContainer c, string parent, string shortname, string title, string command, string min, string max, bool selected)
        {
            var name = c.Add(new CuiButton
            {
                Button = { Color = selected ? UiGreen : UiGrey, Command = command, Close = "" },
                Text = { Text = "" },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
            var definition = ItemManager.FindItemDefinition(shortname);
            if (definition != null)
                c.Add(new CuiElement
                {
                    Parent = name,
                    Components =
                    {
                        new CuiImageComponent { ItemId = definition.itemid },
                        new CuiRectTransformComponent { AnchorMin = "0.03 0.08", AnchorMax = "0.25 0.92" }
                    }
                });
            AddLabel(c, name, title, 15, TextAnchor.MiddleLeft, UiText, "0.29 0.50", "0.98 0.95", BoldFont);
            var blueprint = definition == null ? null : ItemManager.FindBlueprint(definition);
            var cost = new List<string>();
            if (blueprint != null)
                foreach (var ingredient in blueprint.ingredients)
                    cost.Add($"{MaterialName(ingredient.itemDef)}×{(int)ingredient.amount}");
            AddLabel(c, name, string.Join(" ", cost), 11, TextAnchor.MiddleLeft, UiDimText, "0.29 0.05", "0.98 0.50", RegularFont);
        }

        private string Activity(BaseEntity npc, BasePlayer viewer)
        {
            GuardState guard;
            if (guards.TryGetValue(npc, out guard) && guard.Target != null) return "戰鬥中";
            if (lootTasks.ContainsKey(npc)) return "撿戰利品";
            BasePlayer owner;
            var following = followers.TryGetValue(npc, out owner) && owner != null;
            string status;
            if (following && GetGuardSettings(npc).Work != WorkNone && workStatus.TryGetValue(npc, out status)) return status;
            if (!following) return pendingOwners.ContainsKey(npc) ? "等待主人上線" : "原地待命";
            return owner == viewer ? "跟隨你" : "跟隨 " + owner.displayName;
        }

        private static string HealthColor(float fraction)
        {
            return fraction > 0.5f ? UiGreen : fraction > 0.25f ? UiGold : UiRed;
        }

        // Redraws only what changes: roster status/health, the centre caption, the
        // overview status rows and the backpack contents.
        private void RenderLive(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            for (var row = 0; row < RosterPerPage; row++) CuiHelper.DestroyUi(player, RosterRow(row) + ".Live");
            CuiHelper.DestroyUi(player, MenuRightLive);
            CuiHelper.DestroyUi(player, MenuBottomLive);
            var state = GetMenuState(player);
            var list = SortedNpcs();
            var selected = FindNpcById(state.Selected) as BasePlayer;
            var c = new CuiElementContainer();

            for (var row = 0; row < RosterPerPage; row++)
            {
                var index = state.Page * RosterPerPage + row;
                if (index >= list.Count) break;
                var npc = list[index] as BasePlayer;
                if (npc == null) continue;
                var live = RosterRow(row) + ".Live";
                c.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } }, RosterRow(row), live);
                var distance = Vector3.Distance(npc.transform.position, player.transform.position);
                AddLabel(c, live, $"{Activity(npc, player)} · {distance:F0} m", 12, TextAnchor.MiddleLeft, UiDimText, "0.06 0.24", "0.97 0.55", RegularFont);
                var health = Mathf.Clamp01(npc.health / Mathf.Max(1f, npc.MaxHealth()));
                AddPanel(c, live, "0.04 0.04 0.04 0.9", "0.06 0.08", "0.95 0.18");
                AddPanel(c, live, HealthColor(health), "0.06 0.08", $"{0.06f + 0.89f * health:F3} 0.18");
            }

            if (selected != null)
            {
                var distance = Vector3.Distance(selected.transform.position, player.transform.position);

                if (state.Tab == "overview")
                {
                    var settings = GetGuardSettings(selected);
                    c.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0 0.33", AnchorMax = "1 0.81" } }, MenuRight, MenuRightLive);
                    var rows = new[]
                    {
                        new[] { "血量", $"{selected.health:F0} / {selected.MaxHealth():F0}" },
                        new[] { "狀態", Activity(selected, player) },
                        new[] { "攻擊模式", settings.Aggressive ? "主動攻擊" : "被動反擊" },
                        new[] { "受傷", settings.Invulnerable ? "無敵" : "會受傷" },
                        new[] { "視距", $"{settings.SightRange:F0} m" },
                        new[] { "跟隨距離", $"{settings.MinDistance:F1} – {settings.MaxDistance:F1} m" },
                        new[] { "工作", WorkNames[settings.Work] + (settings.Smelt ? "＋熔煉" : "") + (settings.CraftRemaining > 0 ? "＋製作" : "") },
                        new[] { "離你", $"{distance:F1} m" }
                    };
                    for (var i = 0; i < rows.Length; i++)
                    {
                        var top = 1f - i * 0.125f;
                        AddLabel(c, MenuRightLive, rows[i][0], 15, TextAnchor.MiddleLeft, UiDimText, $"0.04 {top - 0.12f:F3}", $"0.45 {top:F3}", BoldFont);
                        AddLabel(c, MenuRightLive, rows[i][1], 15, TextAnchor.MiddleRight, UiText, $"0.45 {top - 0.12f:F3}", $"0.96 {top:F3}", BoldFont);
                        AddPanel(c, MenuRightLive, "0.25 0.24 0.22 0.6", $"0.04 {top - 0.125f:F3}", $"0.96 {top - 0.12f:F3}");
                    }
                }
            }

            c.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } }, MenuBottom, MenuBottomLive);
            var main = selected?.inventory?.containerMain;
            AddLabel(c, MenuBottomLive, main == null ? "背包" : $"背包 {main.itemList.Count} / {main.capacity}", 14, TextAnchor.MiddleLeft, UiDimText,
                "0.01 0.86", "0.16 0.99", BoldFont);
            AddLabel(c, MenuBottomLive, "裝備（上）／快捷欄（下）", 14, TextAnchor.MiddleLeft, UiDimText, "0.70 0.86", "0.99 0.99", BoldFont);
            AddLabel(c, MenuBottomLive, "看著護衛按 E，或按「開啟背包與裝備」來調整", 11, TextAnchor.MiddleLeft, UiDimText, "0.70 0.01", "0.99 0.15", RegularFont);
            if (main != null)
            {
                foreach (var item in main.itemList)
                {
                    if (item.position < 0 || item.position >= 24) continue;
                    AddItemIcon(c, MenuBottomLive + ".S" + item.position, SlotRect(item.position), item);
                }
                AddLabel(c, MenuBottomLive, BackpackSummary(selected), 12, TextAnchor.MiddleLeft, UiText, "0.16 0.86", "0.68 0.99", RegularFont);
                // Clothing on the top row, belt below, each slot in the game's own order.
                var wear = selected.inventory.containerWear;
                var belt = selected.inventory.containerBelt;
                for (var i = 0; i < wear.capacity; i++)
                {
                    var rect = GearSlotRect(i, wear.capacity, 0.52f);
                    AddPanel(c, MenuBottomLive, UiSlot, rect[0], rect[1]);
                    var item = wear.GetSlot(i);
                    if (item != null) AddItemIcon(c, MenuBottomLive + ".W" + i, rect, item);
                }
                for (var i = 0; i < belt.capacity; i++)
                {
                    var rect = GearSlotRect(i, wear.capacity, 0.19f);
                    AddPanel(c, MenuBottomLive, UiSlot, rect[0], rect[1]);
                    var item = belt.GetSlot(i);
                    if (item != null) AddItemIcon(c, MenuBottomLive + ".B" + i, rect, item);
                }
            }
            CuiHelper.AddUi(player, c);
        }

        // Equipment slots squeezed into the right-hand column of the bottom panel.
        private static string[] GearSlotRect(int index, int columns, float bottom)
        {
            var width = 0.29f / Mathf.Max(1, columns);
            var x = 0.70f + index * width;
            return new[] { $"{x:F3} {bottom:F3}", $"{x + width * 0.92f:F3} {bottom + 0.30f:F3}" };
        }

        private void AddItemIcon(CuiElementContainer c, string name, string[] rect, Item item)
        {
            c.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = rect[0], AnchorMax = rect[1] } }, MenuBottomLive, name);
            c.Add(new CuiElement
            {
                Parent = name,
                Components =
                {
                    new CuiImageComponent { ItemId = item.info.itemid, SkinId = item.skin },
                    new CuiRectTransformComponent { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" }
                }
            });
            if (item.amount > 1)
                AddLabel(c, name, $"x{item.amount}", 12, TextAnchor.LowerRight, UiText, "0 0.02", "0.95 0.4", BoldFont);
            // Issued gear vanishes when taken out, so mark it.
            if (boundItems.Contains(item.uid.Value))
                AddLabel(c, name, "配", 10, TextAnchor.UpperLeft, UiDimText, "0.06 0.55", "0.6 0.98", BoldFont);
        }

        private string BackpackSummary(BasePlayer npc)
        {
            var main = npc?.inventory?.containerMain;
            if (main == null || main.itemList.Count == 0) return "（空的）";
            var totals = new Dictionary<string, int>();
            foreach (var item in main.itemList)
            {
                var key = MaterialName(item.info);
                int count;
                totals.TryGetValue(key, out count);
                totals[key] = count + item.amount;
            }
            var parts = new List<string>();
            foreach (var entry in totals) parts.Add($"{entry.Key} {entry.Value}");
            return string.Join("　", parts);
        }

        [ConsoleCommand("npcui.tab")]
        private void NpcUiTab(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin) return;
            var tab = arg.GetString(0);
            if (System.Array.IndexOf(MenuTabs, tab) >= 0) GetMenuState(player).Tab = tab;
            ShowMenu(player);
        }

        [ConsoleCommand("npcui.select")]
        private void NpcUiSelect(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin) return;
            ulong id;
            if (ulong.TryParse(arg.GetString(0), out id) && FindNpcById(id) != null) GetMenuState(player).Selected = id;
            ShowMenu(player);
        }

        // Toggle the appearance preview: the guard walks up and stands in front of you.
        [ConsoleCommand("npcui.preview")]
        private void NpcUiPreview(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin) return;
            ulong id;
            var npc = ulong.TryParse(arg.GetString(0), out id) ? FindNpcById(id) : null;
            BasePlayer previewer;
            if (npc == null || (previews.TryGetValue(npc, out previewer) && previewer == player)) EndPreview(player);
            else if (Vector3.Distance(npc.transform.position, player.transform.position) > PreviewMaxDistance)
                SendReply(player, $"{GetDisplayName(npc)} 離你太遠（{Vector3.Distance(npc.transform.position, player.transform.position):F0} m），先按「叫回身邊」。");
            else StartPreview(player, npc);
            ShowMenu(player);
        }

        [ConsoleCommand("npcui.close")]
        private void NpcUiClose(ConsoleSystem.Arg arg)
        {
            CloseMenu(arg.Connection?.player as BasePlayer);
        }

        // Bound to Tab next to inventory.toggle: opening the inventory closes the menu.
        [ConsoleCommand("npcmenu.tabclose")]
        private void NpcMenuTabClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player != null && openMenus.Contains(player)) CloseMenu(player);
        }

        // Per-guard orders from the overview tab: follow / stay / recall.
        [ConsoleCommand("npcui.cmd")]
        private void NpcUiCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin) return;
            ulong id;
            var npc = ulong.TryParse(arg.GetString(0), out id) ? FindNpcById(id) : null;
            if (npc != null) OrderNpc(player, npc, arg.GetString(1).ToLowerInvariant());
            SaveNpcs();
            ShowMenu(player);
        }

        // Opens a guard's backpack, clothing and belt from the menu, wherever it is.
        [ConsoleCommand("npcui.loot")]
        private void NpcUiLoot(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null) return;
            ulong id;
            var npc = (ulong.TryParse(arg.GetString(0), out id) ? FindNpcById(id) : null) as BasePlayer;
            if (npc == null || !CanAccessBackpack(player, npc)) return;
            CloseMenu(player);
            OpenBackpack(player, npc);
        }

        // Orders for every guard: follow / stay / recall.
        [ConsoleCommand("npcui.all")]
        private void NpcUiAll(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin) return;
            var order = arg.GetString(0).ToLowerInvariant();
            foreach (var npc in new List<BaseEntity>(npcs))
            {
                BasePlayer owner;
                // "stay" and "recall" only touch guards that follow this player.
                if (order != "follow" && (!followers.TryGetValue(npc, out owner) || owner != player)) continue;
                OrderNpc(player, npc, order);
            }
            SaveNpcs();
            ShowMenu(player);
        }

        private void OrderNpc(BasePlayer player, BaseEntity npc, string order)
        {
            if (npc == null || npc.IsDestroyed) return;
            switch (order)
            {
                case "follow":
                    if (!ActivateCompanion(npc, player, true)) SendReply(player, $"{GetDisplayName(npc)} 無法開始跟隨（附近沒有可走的路）。");
                    break;
                case "stay":
                    pendingOwners.Remove(npc);
                    StopFollowing(npc);
                    break;
                case "recall":
                    BasePlayer owner;
                    if ((!followers.TryGetValue(npc, out owner) || owner != player) && !ActivateCompanion(npc, player, true))
                    {
                        SendReply(player, $"{GetDisplayName(npc)} 無法被叫回。");
                        return;
                    }
                    nextTeleportTry.Remove(npc);
                    var agent = npc.GetComponent<RustNavMeshAgent>();
                    if (agent == null || !TeleportNearOwner(npc, player, agent))
                        SendReply(player, $"{GetDisplayName(npc)} 在你附近找不到可以站的位置。");
                    break;
            }
        }


        [ConsoleCommand("npcmenu.work")]
        private void NpcMenuWork(ConsoleSystem.Arg arg)
        {
            var player = arg.Connection?.player as BasePlayer;
            if (player == null || !player.IsAdmin || arg.Args == null || arg.Args.Length < 2) return;
            ulong id;
            if (!ulong.TryParse(arg.GetString(0), out id)) return;
            var npc = npcs.Find(n => n != null && !n.IsDestroyed && n.net != null && n.net.ID.Value == id);
            if (npc == null) { ShowMenu(player); return; }
            var settings = GetGuardSettings(npc);
            switch (arg.GetString(1).ToLowerInvariant())
            {
                case "mode":
                    settings.Work = Mathf.Clamp(arg.GetInt(2), 0, WorkNames.Length - 1);
                    if (settings.Work == WorkGather)
                    {
                        // Work around where the owner stands when assigning it.
                        settings.AnchorX = player.transform.position.x;
                        settings.AnchorY = player.transform.position.y;
                        settings.AnchorZ = player.transform.position.z;
                    }
                    StopGathering(npc);
                    workStatus.Remove(npc);
                    nextProduce.Remove(npc);
                    break;
                case "smelt":
                    settings.Smelt = !settings.Smelt;
                    if (!settings.Smelt) workStatus.Remove(npc);
                    break;
                case "gather":
                    settings.GatherMask ^= 1 << Mathf.Clamp(arg.GetInt(2), 0, GatherCategoryNames.Length - 1);
                    StopGathering(npc);
                    break;
                case "gatherall":
                    settings.GatherMask = AllGatherCategories;
                    break;
                case "gathernone":
                    settings.GatherMask = 0;
                    StopGathering(npc);
                    break;
                case "loot":
                    settings.AutoLoot = !settings.AutoLoot;
                    if (!settings.AutoLoot) lootTasks.Remove(npc);
                    break;
                case "craft":
                    var index = Mathf.Clamp(arg.GetInt(2), 0, CraftItems.Length - 1);
                    var shortname = CraftItems[index];
                    if (settings.CraftRemaining > 0 && settings.CraftItem != shortname)
                        SendReply(player, $"正在製作{CraftNameOf(settings.CraftItem)}，請先取消目前的製作。");
                    else
                    {
                        settings.CraftItem = shortname;
                        settings.CraftRemaining++;
                    }
                    break;
                case "cancel":
                    CancelCrafting(npc as BasePlayer, settings);
                    workStatus.Remove(npc);
                    break;
                case "open":
                    SelectNpc(player, npc);
                    GetMenuState(player).Tab = "work";
                    break;
                case "back":
                    ShowMenu(player);
                    return;
            }
            SaveNpcs();
            ShowMenu(player);
        }

        private static string CraftNameOf(string shortname)
        {
            var index = System.Array.IndexOf(CraftItems, shortname);
            return index >= 0 ? CraftNames[index] : shortname;
        }

        private static readonly string[] WorkHints =
        {
            "只跟隨和戰鬥，不做其他工作。",
            "在你現在站的位置半徑 40 公尺內，自己去採下方勾選的資源；不跟隨、不傳送。",
            "你停下來時，採集你身邊勾選的資源；你走遠就放下工作跟上。",
            "照常跟隨你，每 30 秒把木頭、石頭、金屬礦、硫磺礦放進背包。"
        };

        private int GetNpcNumber(BaseEntity npc)
        {
            int number;
            return npcNumbers.TryGetValue(npc, out number) ? number : 0;
        }

        private void AddPanel(CuiElementContainer container, string parent, string color, string min, string max)
        {
            container.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        private void AddLabel(CuiElementContainer container, string parent, string text, int size, TextAnchor align, string color, string min, string max, string font)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = size, Align = align, Color = color, Font = font },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        private void AddMenuButton(CuiElementContainer container, string parent, string text, string command, string min, string max, string color, int fontSize = 16)
        {
            container.Add(new CuiButton
            {
                Button = { Color = color, Command = command, Close = "" },
                Text = { Text = text, FontSize = fontSize, Align = TextAnchor.MiddleCenter, Color = UiText, Font = BoldFont },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        private void DestroyMenu(BasePlayer player)
        {
            if (player != null) CuiHelper.DestroyUi(player, MenuName);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            CloseMenu(player);
        }

        // Server console/RCON only, for managing and inspecting this plugin's NPCs.
        [ConsoleCommand("npcdemo.control")]
        private void Control(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) { arg.ReplyWith("Use the chat command."); return; }
            if (arg.Args == null || arg.Args.Length != 2) { arg.ReplyWith("npcdemo.control STEAMID add|guard|follow|stop|count|status"); return; }
            var player = BasePlayer.FindByID(arg.GetULong(0));
            if (player == null || !player.IsConnected || !player.IsAdmin) { arg.ReplyWith("An online admin is required."); return; }
            if (arg.Args[1] == "status")
            {
                var lines = new List<string>();
                foreach (var npc in npcs)
                {
                    if (npc == null || npc.IsDestroyed) continue;
                    var nav = npc.GetComponent<RustNavMeshAgent>();
                    var toPlayer = player.transform.position - npc.transform.position;
                    toPlayer.y = 0f;
                    GuardState guard;
                    if (guards.TryGetValue(npc, out guard))
                        lines.Add($"Guard: owner={guard.Owner.UserIDString}, target={(guard.Target == null ? "none" : guard.Target.ShortPrefabName)}, weapon={(npc as BasePlayer)?.GetActiveItem()?.GetHeldEntity()?.ShortPrefabName}");
                    lines.Add($"NPC {npc.net.ID}: pos={npc.transform.position}, distance={toPlayer.magnitude:F2}, facingAngle={Vector3.Angle(npc.transform.forward, toPlayer):F1}, following={followers.ContainsKey(npc)}, nav={nav != null}, onMesh={nav != null && nav.isOnNavMesh}, path={nav != null && nav.hasPath}");
                }
                arg.ReplyWith(string.Join("\n", lines));
                return;
            }
            NpcDemoCommand(player, "npcdemo", new[] { arg.Args[1].ToString() });
            arg.ReplyWith("Command handled.");
        }

        private void StopFollowing(BaseEntity npc)
        {
            followers.Remove(npc);
            formationSlots.Remove(npc);
            guards.Remove(npc);
            walking.Remove(npc);
            running.Remove(npc);
            if (npc == null || npc.IsDestroyed) return;
            var agent = npc.GetComponent<RustNavMeshAgent>();
            if (agent != null && agent.enabled)
            {
                agent.ResetPath();
                agent.isStopped = true;
            }
        }

        private void RemoveAllNpcs(BasePlayer player)
        {
            if (player == null || !player.IsAdmin) return;
            var removed = 0;
            foreach (var npc in new List<BaseEntity>(npcs))
            {
                if (npc == null || npc.IsDestroyed) continue;
                StopFollowing(npc);
                KillNpc(npc);
                removed++;
            }
            npcs.Clear();
            followers.Clear();
            formationSlots.Clear();
            guards.Clear();
            guardSettings.Clear();
            npcNumbers.Clear();
            pendingOwners.Clear();
            nextNpcNumber = 1;
            SendReply(player, $"已清除 {removed} 隻護衛 NPC。");
        }

        private BaseEntity CreateNpc(Vector3 position, Quaternion rotation, GuardSettings settings, bool outfit = true)
        {
            var entity = GameManager.server.CreateEntity(Prefab, position, rotation);
            if (entity == null) { PrintWarning("NPC prefab could not be created."); return null; }
            entity.enableSaving = false;
            // Marks the entity as moving for network-group updates. It does not
            // stream movement for BasePlayer (see StreamPositions).
            entity.syncPosition = true;
            ApplyLook(entity as BasePlayer, settings);
            entity.Spawn();
            if (entity.IsDestroyed) return null;
            if (outfit) ApplyOutfit(entity as BasePlayer, settings);
            return entity;
        }

        private bool ActivateCompanion(BaseEntity companion, BasePlayer owner, bool guardMode)
        {
            var agent = companion.GetComponent<RustNavMeshAgent>() ?? companion.gameObject.AddComponent<RustNavMeshAgent>();
            agent.enabled = true;
            if (!agent.IsNavMeshBuilt || !agent.Warp(companion.transform.position)) return false;
            agent.updatePosition = true;
            agent.updateRotation = false;
            agent.speed = WalkSpeed;
            // Change gait almost instantly like a player; the default 2 m/s²
            // deceleration kept run speed for ~1.4s under a walk animation.
            agent.acceleration = 20f;
            agent.deceleration.Value = 20f;
            agent.angularSpeed = 180f;
            // No slow creep while approaching the (moving) formation slot.
            agent.autoBraking = false;
            agent.stoppingDistance = 0.5f;
            agent.canSwim = false;
            agent.canOpenDoors = false;
            agent.isStopped = false;
            companion.syncPosition = true;
            followers[companion] = owner;
            pendingOwners.Remove(companion);
            formationSlots[companion] = AllocateFormationSlot(owner, companion);
            if (guardMode)
            {
                DisableNativeCombat(companion as BasePlayer);
                if (!EquipGuard(companion as BasePlayer)) return false;
                guards[companion] = new GuardState { Owner = owner };
            }
            else guards.Remove(companion);
            return true;
        }

        private void ApplyLook(BasePlayer npc, GuardSettings settings)
        {
            if (npc == null) return;
            // Clients derive gender, face and body from userID (PlayerModel.skinType is
            // never networked). Each look index maps to a fixed bot-range ID block of
            // the chosen gender, so the same number gives (nearly) the same face.
            // Clients also key players by userID: an ID shared with any other player
            // entity makes the guard vanish on screen when that one dies, so skip IDs
            // that are already taken.
            var used = UserIdsInUse(npc);
            var start = GuardIdStart + (ulong)Mathf.Clamp(settings.LookIndex, 0, LookCount - 1) * 1000UL;
            for (var id = start; id < start + 1000UL; id++)
            {
                if (used.Contains(id)) continue;
                npc.userID = id;
                npc.UpdateGender();
                if (npc.IsFemale == settings.Female) return;
            }
        }

        private static HashSet<ulong> UserIdsInUse(BaseEntity except)
        {
            var used = new HashSet<ulong>();
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var player = networkable as BasePlayer;
                if (player != null && player != except && !player.IsDestroyed) used.Add(player.userID.Get());
            }
            return used;
        }

        // The game reuses destroyed entity objects, and a new player entity inheriting a
        // guard's ID would make that guard vanish on clients. Swap in a throwaway ID
        // after the kill (killing with userID 0 throws "Cannot get player state
        // without a SteamID" and aborts the caller).
        private ulong nextRetiredUserId = 3000000000UL;

        private void KillNpc(BaseEntity npc)
        {
            if (npc == null || npc.IsDestroyed) return;
            var player = npc as BasePlayer;
            var oldId = player != null ? player.userID.Get() : 0UL;
            foreach (var container in GearContainers(player))
                foreach (var item in container.itemList) { boundItems.Remove(item.uid.Value); issuedGuns.Remove(item.uid.Value); }
            npc.Kill();
            if (player == null) return;
            player.userID = nextRetiredUserId++;
            // Kill() recycled the ID into the game's bot ID pool: take it back out.
            var free = FreeBotIds();
            if (free == null) return;
            var index = free.LastIndexOf(oldId);
            if (index >= 0) free.RemoveAt(index);
        }

        // The game hands out bot userIDs from BasePlayer.freeBotIds (refilled with every
        // unused ID below the highest one on save load, and with the ID of every bot that
        // is destroyed), then from botIdCounter. Guard look IDs live in
        // [GuardIdStart, GuardIdEnd), so keep the game out of that range or its scientists
        // and dwellers end up sharing a guard's ID.
        private const ulong GuardIdStart = 2000000UL;
        private const ulong GuardIdEnd = GuardIdStart + LookCount * 1000UL;
        private static readonly System.Reflection.FieldInfo FreeBotIdsField =
            typeof(BasePlayer).GetField("freeBotIds", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        private static readonly System.Reflection.FieldInfo BotIdCounterField =
            typeof(BasePlayer).GetField("botIdCounter", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        private static List<ulong> FreeBotIds() => FreeBotIdsField?.GetValue(null) as List<ulong>;

        private void ReserveGuardIdRange()
        {
            if (FreeBotIdsField == null || BotIdCounterField == null)
            {
                PrintWarning("BasePlayer bot ID fields not found; guard userIDs may collide with game NPCs.");
                return;
            }
            var free = FreeBotIds();
            if (free != null) free.RemoveAll(id => id >= GuardIdStart && id < GuardIdEnd);
            if ((ulong)BotIdCounterField.GetValue(null) < GuardIdEnd) BotIdCounterField.SetValue(null, GuardIdEnd);
        }

        // Player-model bots that are not in the guard list (left over from an interrupted
        // unload) look like guards but are treated as enemies. Real players have Steam
        // IDs, so their sleepers are never touched.
        private const ulong FirstSteamId = 76561197960265728UL;

        private void RemoveOrphanNpcs()
        {
            var orphans = new List<BasePlayer>();
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var player = networkable as BasePlayer;
                if (player == null || player.IsDestroyed || player.IsConnected || player.ShortPrefabName != "player") continue;
                if (npcs.Contains(player) || player.userID.Get() >= FirstSteamId) continue;
                orphans.Add(player);
            }
            foreach (var orphan in orphans)
            {
                Puts($"Removing orphaned NPC '{orphan.displayName}' ({orphan.userID.Get()}).");
                KillNpc(orphan);
            }
        }

        // Every minute: if some other player entity took a guard's userID, respawn the
        // guard in place with a free one.
        private void FixDuplicateUserIds()
        {
            RemoveOrphanNpcs();
            ReserveGuardIdRange();
            var counts = new Dictionary<ulong, int>();
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var player = networkable as BasePlayer;
                if (player == null || player.IsDestroyed) continue;
                var id = player.userID.Get();
                int count;
                counts.TryGetValue(id, out count);
                counts[id] = count + 1;
            }
            foreach (var npc in new List<BaseEntity>(npcs))
            {
                var player = npc as BasePlayer;
                int count;
                if (player == null || player.IsDestroyed || !counts.TryGetValue(player.userID.Get(), out count) || count < 2) continue;
                Puts($"{GetDisplayName(npc)} shared userID {player.userID.Get()} with another entity; respawning it.");
                // Diagnostics: who else holds this ID?
                foreach (var networkable in BaseNetworkable.serverEntities)
                {
                    var other = networkable as BasePlayer;
                    if (other != null && other != player && !other.IsDestroyed && other.userID.Get() == player.userID.Get())
                        Puts("  other: " + DescribePlayer(other));
                }
                Puts("  guard: " + DescribePlayer(player));
                RespawnNpc(npc);
            }
        }

        private string DescribePlayer(BasePlayer p)
        {
            return $"net={p.net?.ID.Value} prefab={p.ShortPrefabName} type={p.GetType().Name} name='{p.displayName}' " +
                   $"userID={p.userID.Get()} inList={npcs.Contains(p)} connected={p.IsConnected} sleeping={p.IsSleeping()} " +
                   $"dead={p.IsDead()} limitNet={p.limitNetworking} pos={p.transform.position} parent={p.GetParentEntity()?.ShortPrefabName}";
        }

        // RCON / server console: list every non-Steam player entity (guards, orphans, bots).
        [ConsoleCommand("npcstarter.ids")]
        private void NpcStarterIds(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            var lines = new List<string>();
            foreach (var networkable in BaseNetworkable.serverEntities)
            {
                var p = networkable as BasePlayer;
                if (p == null || p.userID.Get() >= FirstSteamId) continue;
                lines.Add(DescribePlayer(p) + $" destroyed={p.IsDestroyed}");
            }
            var free = FreeBotIds();
            var freeInRange = free == null ? -1 : free.FindAll(id => id >= GuardIdStart && id < GuardIdEnd).Count;
            arg.ReplyWith($"botIdCounter={BotIdCounterField?.GetValue(null)} freeBotIds={free?.Count} (in guard range: {freeInRange})\n" +
                          $"{lines.Count} non-Steam player entities:\n" + string.Join("\n", lines));
        }

        // On connect, before the client is sent any snapshots: the server bumps the
        // connection's entity sequence number before serializing, so an entity that
        // throws while saving drops a number and the client disconnects with
        // "Entities Out Of Order". Clean up and respawn any guard that fails a test save.
        private void CheckNpcHealth(BasePlayer player)
        {
            try { FixDuplicateUserIds(); }
            catch (System.Exception ex) { PrintWarning("Duplicate userID check failed: " + ex); }
            var connection = player?.net?.connection;
            var checkedCount = 0;
            var failed = 0;
            foreach (var npc in new List<BaseEntity>(npcs))
            {
                if (npc == null || npc.IsDestroyed) continue;
                checkedCount++;
                var error = TestSerialize(npc, connection);
                if (error == null) continue;
                failed++;
                PrintWarning($"{GetDisplayName(npc)} failed a test save; respawning it. {error}");
                try { RespawnNpc(npc); }
                catch (System.Exception ex) { PrintWarning($"Could not respawn {GetDisplayName(npc)}: {ex}"); }
            }
            Puts($"NPC health check for {player?.displayName}: {checkedCount} guard(s), {failed} respawned.");
        }

        // Serializes the entity and its child entities (held weapon etc.) the way a
        // snapshot would. Returns the failure, or null when everything saved.
        private static string TestSerialize(BaseEntity entity, Network.Connection connection)
        {
            try
            {
                using (var stream = new System.IO.MemoryStream())
                    entity.ToStream(stream, new BaseNetworkable.SaveInfo
                    {
                        forConnection = connection,
                        forDisk = false,
                        cachedTime = BaseNetworkable.ThreadSafeTime.TakeSnapshot()
                    });
            }
            catch (System.Exception ex) { return $"{entity.ShortPrefabName}: {ex}"; }
            foreach (var child in new List<BaseEntity>(entity.children))
            {
                if (child == null || child.IsDestroyed) continue;
                var error = TestSerialize(child, connection);
                if (error != null) return error;
            }
            return null;
        }

        // Gender/face only apply when the client first builds the model (hiding and
        // re-showing the entity did not rebuild it), so swap in a fresh NPC in place
        // that keeps the number, settings, owner and guard role.
        private BaseEntity RespawnNpc(BaseEntity old)
        {
            if (old == null || old.IsDestroyed) return null;
            var settings = GetGuardSettings(old);
            var number = GetNpcNumber(old);
            var index = npcs.IndexOf(old);
            var position = old.transform.position;
            var rotation = old.transform.rotation;
            BasePlayer owner;
            followers.TryGetValue(old, out owner);
            var wasGuard = guards.ContainsKey(old);
            BasePlayer previewer;
            previews.TryGetValue(old, out previewer);
            KeyValuePair<Vector3, float> origin;
            var hasOrigin = previewOrigins.TryGetValue(old, out origin);
            PendingOwner pending;
            pendingOwners.TryGetValue(old, out pending);
            float craftFinish;
            var crafting = craftFinishAt.TryGetValue(old, out craftFinish);
            // Carry the backpack, clothing and belt over to the new body, slot for slot.
            var carried = new List<KeyValuePair<int, Item>>[3];
            var oldContainers = GearContainers(old as BasePlayer);
            for (var i = 0; i < oldContainers.Count; i++)
            {
                carried[i] = new List<KeyValuePair<int, Item>>();
                foreach (var item in new List<Item>(oldContainers[i].itemList))
                {
                    carried[i].Add(new KeyValuePair<int, Item>(item.position, item));
                    item.RemoveFromContainer();
                }
            }
            npcs.Remove(old);
            StopFollowing(old);
            ForgetNpc(old);
            KillNpc(old);
            var entity = CreateNpc(position, rotation, settings, false);
            var newContainers = GearContainers(entity as BasePlayer);
            for (var i = 0; i < oldContainers.Count; i++)
                foreach (var entry in carried[i])
                {
                    var target = i < newContainers.Count ? newContainers[i] : null;
                    if (target != null && (entry.Value.MoveToContainer(target, entry.Key, false) || entry.Value.MoveToContainer(target))) continue;
                    // Issued gear is dropped here and discarded by OnItemDropped.
                    entry.Value.Drop(position + Vector3.up, Vector3.up);
                }
            if (entity == null) return null;
            entity.SendNetworkUpdate();
            if (crafting) craftFinishAt[entity] = craftFinish;
            npcs.Insert(Mathf.Clamp(index, 0, npcs.Count), entity);
            guardSettings[entity] = settings;
            npcNumbers[entity] = number;
            ApplyName(entity);
            if (owner != null && owner.IsConnected) ActivateCompanion(entity, owner, wasGuard);
            else if (pending != null) pendingOwners[entity] = pending;
            if (hasOrigin) previewOrigins[entity] = origin;
            if (previewer != null)
            {
                previews[entity] = previewer;
                FaceDirection(entity, previewer.transform.position - entity.transform.position, true);
            }
            return entity;
        }

        private void ApplyOutfit(BasePlayer npc, GuardSettings settings)
        {
            if (npc == null || npc.inventory?.containerWear == null) return;
            var wear = npc.inventory.containerWear;
            // Swap only the issued outfit; clothing a player put on the guard stays, and
            // issued pieces that clash with it are skipped.
            foreach (var item in new List<Item>(wear.itemList))
                if (boundItems.Contains(item.uid.Value)) DiscardIssued(item);
            foreach (var shortname in OutfitItems[Mathf.Clamp(settings.Outfit, 0, OutfitItems.Length - 1)])
            {
                var item = CreateIssued(shortname);
                if (item != null && !item.MoveToContainer(wear)) DiscardIssued(item);
            }
            npc.SendNetworkUpdate();
        }

        private bool EquipGuard(BasePlayer npc)
        {
            if (npc == null || npc.inventory == null) { PrintWarning("Guard has no NPC inventory."); return false; }
            // Outfit is no longer re-applied here: it would undo the player's equipment.
            if (!EnsureWeaponActive(npc)) { PrintWarning($"Guard weapon not active. Belt capacity={npc.inventory.containerBelt.capacity}, items={npc.inventory.containerBelt.itemList.Count}, active={npc.GetActiveItem()?.info.shortname}"); return false; }
            var weapon = npc.GetActiveItem().GetHeldEntity() as BaseProjectile;
            weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
            weapon.SendNetworkUpdate();
            var shopkeeper = npc as NPCShopKeeper;
            if (shopkeeper != null) shopkeeper.canBeHurt = true;
            npc.SendNetworkUpdate();
            return true;
        }

        private Item FindBeltItem(BasePlayer npc, string shortname)
        {
            if (npc?.inventory?.containerBelt == null) return null;
            foreach (var item in npc.inventory.containerBelt.itemList)
                if (item.info.shortname == shortname) return item;
            return null;
        }

        // The gun a guard fights with: a player's own gun in the belt comes first,
        // then the issued one.
        private Item FindGun(BasePlayer npc)
        {
            if (npc?.inventory?.containerBelt == null) return null;
            Item issued = null;
            foreach (var item in npc.inventory.containerBelt.itemList)
            {
                if (!(item.GetHeldEntity() is BaseProjectile)) continue;
                if (!issuedGuns.Contains(item.uid.Value)) return item;
                if (issued == null) issued = item;
            }
            return issued;
        }

        private string WeaponLabel(BasePlayer npc)
        {
            var gun = FindGun(npc);
            if (gun != null) return gun.info.displayName.english;
            return WeaponNames[Mathf.Clamp(GetGuardSettings(npc).Weapon, 0, WeaponNames.Length - 1)];
        }

        private void RemoveIssuedGuns(BasePlayer npc)
        {
            if (npc?.inventory?.containerBelt == null) return;
            foreach (var item in new List<Item>(npc.inventory.containerBelt.itemList))
                if (issuedGuns.Contains(item.uid.Value)) DiscardIssued(item);
        }

        // Guards hold a gathering tool while working; put the gun back in hand for combat.
        // A guard left without any gun (a player took it) is issued a new one.
        private bool EnsureWeaponActive(BasePlayer npc)
        {
            if (npc?.inventory?.containerBelt == null) return false;
            var gun = FindGun(npc) ?? IssueGun(npc);
            if (gun == null) return false;
            if (npc.GetActiveItem() != gun) npc.UpdateActiveItem(gun.uid);
            return npc.GetActiveItem()?.GetHeldEntity() is BaseProjectile;
        }

        // The menu weapon, loaded, plus a few magazines of real ammo in the backpack.
        // Both are ordinary items players may take; the guard itself fires without
        // using ammo (UpdateGuard refills the magazine).
        private Item IssueGun(BasePlayer npc)
        {
            var gun = ItemManager.CreateByName(WeaponItems[Mathf.Clamp(GetGuardSettings(npc).Weapon, 0, WeaponItems.Length - 1)], 1);
            if (gun == null) return null;
            if (!gun.MoveToContainer(npc.inventory.containerBelt)) { gun.Remove(); return null; }
            issuedGuns.Add(gun.uid.Value);
            var weapon = gun.GetHeldEntity() as BaseProjectile;
            if (weapon == null) return gun;
            weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
            RefillAmmo(npc);
            return gun;
        }

        // Keeps IssuedAmmoMagazines of ammo for the guard's current gun in its backpack,
        // topping it back up after players take some.
        private void RefillAmmo(BasePlayer npc)
        {
            var weapon = FindGun(npc)?.GetHeldEntity() as BaseProjectile;
            var ammoType = weapon?.primaryMagazine?.ammoType;
            if (ammoType == null) return;
            var target = Mathf.Max(1, weapon.primaryMagazine.capacity) * IssuedAmmoMagazines;
            var have = npc.inventory.containerMain.GetAmount(ammoType.itemid, true) + npc.inventory.containerBelt.GetAmount(ammoType.itemid, true);
            if (have >= target) return;
            var ammo = ItemManager.Create(ammoType, target - have);
            if (ammo != null && !ammo.MoveToContainer(npc.inventory.containerMain)) ammo.Remove();
        }

        private BaseMelee EnsureToolActive(BasePlayer npc, string shortname)
        {
            var tool = FindBeltItem(npc, shortname);
            if (tool == null)
            {
                tool = CreateIssued(shortname);
                if (tool == null) return null;
                if (!tool.MoveToContainer(npc.inventory.containerBelt)) { DiscardIssued(tool); return null; }
            }
            if (npc.GetActiveItem() != tool) npc.UpdateActiveItem(tool.uid);
            return tool.GetHeldEntity() as BaseMelee;
        }

        // ---- Guard inventory: issued gear, saving, and players editing equipment ----
        // Item uids of gear the plugin hands out for free (menu outfit, gathering tools).
        // Players may rearrange it on the guard, but it is destroyed the moment it
        // leaves a guard, so it can never be farmed.
        private readonly HashSet<ulong> boundItems = new HashSet<ulong>();
        // Guns the plugin issued. Players may take them (they become ordinary items);
        // while on the guard, the menu may swap them and a player's own gun wins.
        private readonly HashSet<ulong> issuedGuns = new HashSet<ulong>();
        private const int IssuedAmmoMagazines = 3;

        private Item CreateIssued(string shortname)
        {
            var item = ItemManager.CreateByName(shortname, 1);
            if (item != null) boundItems.Add(item.uid.Value);
            return item;
        }

        private void DiscardIssued(Item item)
        {
            boundItems.Remove(item.uid.Value);
            issuedGuns.Remove(item.uid.Value);
            item.RemoveFromContainer();
            item.Remove();
        }

        private bool IsIssued(Item item) => item != null && (boundItems.Contains(item.uid.Value) || issuedGuns.Contains(item.uid.Value));

        // Main, wear and belt, in that order; empty when the entity has no inventory.
        private static List<ItemContainer> GearContainers(BasePlayer npc)
        {
            var inventory = npc?.inventory;
            if (inventory == null || inventory.containerMain == null || inventory.containerWear == null || inventory.containerBelt == null)
                return new List<ItemContainer>();
            return new List<ItemContainer> { inventory.containerMain, inventory.containerWear, inventory.containerBelt };
        }

        // The player whose inventory holds this container, looking through attachment slots.
        private static BasePlayer ContainerPlayer(ItemContainer container)
        {
            while (container != null)
            {
                if (container.playerOwner != null) return container.playerOwner;
                container = container.parent?.parent;
            }
            return null;
        }

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (IsIssued(item)) NextTick(() => CheckTakenOut(item));
            RefreshGuardClothing(container);
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item) => RefreshGuardClothing(container);

        // Clothing is what other players see: push changes made through the loot panel.
        private void RefreshGuardClothing(ItemContainer container)
        {
            var owner = container?.playerOwner;
            if (owner != null && container == owner.inventory?.containerWear && npcs.Contains(owner)) owner.SendNetworkUpdate();
        }

        private void OnItemDropped(Item item, BaseEntity entity)
        {
            if (IsIssued(item)) NextTick(() => CheckTakenOut(item));
        }

        // Issued gear that left a guard: a taken gun becomes the player's to keep,
        // anything else issued is destroyed.
        private void CheckTakenOut(Item item)
        {
            if (item == null || item.removeTime > 0f || !IsIssued(item)) return;
            var holder = ContainerPlayer(item.parent);
            if (holder != null && npcs.Contains(holder)) return;
            if (issuedGuns.Remove(item.uid.Value)) return;
            DiscardIssued(item);
            if (holder != null && holder.IsConnected)
                SendReply(holder, $"「{item.info.displayName.english}」是護衛的配給裝備，拿出來就消失了；請放入你自己的裝備。");
        }

        private List<SavedItem> SaveItems(ItemContainer container)
        {
            var list = new List<SavedItem>();
            if (container == null) return list;
            foreach (var item in container.itemList)
            {
                var saved = new SavedItem
                {
                    Shortname = item.info.shortname,
                    Amount = item.amount,
                    Skin = item.skin,
                    Slot = item.position,
                    Condition = item.hasCondition ? item.condition : -1f,
                    Bound = IsIssued(item)
                };
                if (item.contents != null && item.contents.itemList.Count > 0) saved.Contents = SaveItems(item.contents);
                list.Add(saved);
            }
            return list;
        }

        private void RestoreItems(ItemContainer container, List<SavedItem> items)
        {
            if (container == null || items == null) return;
            foreach (var saved in items)
            {
                var item = ItemManager.CreateByName(saved.Shortname, Mathf.Max(1, saved.Amount), saved.Skin);
                if (item == null) continue;
                if (saved.Condition >= 0f && item.hasCondition) item.condition = saved.Condition;
                if (saved.Contents != null && item.contents != null) RestoreItems(item.contents, saved.Contents);
                if (!(saved.Slot >= 0 && item.MoveToContainer(container, saved.Slot, false)) && !item.MoveToContainer(container))
                {
                    item.Remove();
                    continue;
                }
                // Issued guns are takeable; other issued gear is bound to the guard.
                if (saved.Bound) (item.GetHeldEntity() is BaseProjectile ? issuedGuns : boundItems).Add(item.uid.Value);
            }
        }

        private void ApplyGenderToNpcs()
        {
            foreach (var npc in new List<BaseEntity>(npcs))
            {
                if (npc == null || npc.IsDestroyed) continue;
                var settings = GetGuardSettings(npc);
                if (settings.Female == femaleGuards) continue;
                settings.Female = femaleGuards;
                RespawnNpc(npc);
            }
        }

        private void DisableNativeCombat(BasePlayer npc)
        {
            if (npc == null) return;
            foreach (var component in npc.GetComponents<MonoBehaviour>())
            {
                if (component == null || component == npc) continue;
                var name = component.GetType().Name;
                if (name.Contains("Brain") || name.Contains("AI") || name.Contains("Sense"))
                    component.enabled = false;
            }
            npc.SendNetworkUpdate();
        }

        private bool IsFriendly(BaseEntity entity, BasePlayer owner)
        {
            if (entity == owner || npcs.Contains(entity)) return true;
            var player = entity as BasePlayer;
            return player != null && owner.currentTeam != 0 && player.currentTeam == owner.currentTeam;
        }

        // maxDistance: how far from the centre a threat may be (the guard's sight range
        // plus some slack for a target that is moving away).
        private bool ValidThreat(BaseCombatEntity target, BasePlayer owner, float maxDistance, Vector3 center)
        {
            return target != null && !target.IsDestroyed && !target.IsDead()
                && (target is BasePlayer || target.IsNpc) && !IsFriendly(target, owner)
                && Vector3.Distance(target.transform.position, center) <= maxDistance
                && (!(target is BasePlayer) || !(target as BasePlayer).InSafeZone());
        }

        private float GetThreatRange(BaseEntity guard)
        {
            return GetGuardSettings(guard).SightRange + 10f;
        }

        // Guards protect the area around the owner, except dedicated gatherers working
        // away from them, which defend themselves.
        private Vector3 ThreatCenter(BaseEntity guard, BasePlayer owner)
        {
            return GetGuardSettings(guard).Work == WorkGather ? guard.transform.position : owner.transform.position;
        }

        private BaseCombatEntity FindNearbyThreat(BasePlayer owner, BaseEntity guardEntity)
        {
            if (owner == null || owner.IsDead() || owner.InSafeZone()) return null;
            var guardPlayer = guardEntity as BasePlayer;
            if (guardPlayer == null) return null;
            var sight = GetGuardSettings(guardEntity).SightRange;
            var center = ThreatCenter(guardEntity, owner);
            BaseCombatEntity best = null;
            var bestDistance = sight * sight;
            var candidates = new List<BaseCombatEntity>();
            Vis.Entities(center, sight, candidates);
            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate == guardEntity || !ValidThreat(candidate, owner, sight, center)) continue;
                if (candidate is BasePlayer && (candidate as BasePlayer).IsSleeping()) continue;
                var offset = candidate.transform.position - center;
                // The guard itself must be able to see it (not just the owner).
                if (offset.sqrMagnitude >= bestDistance || !candidate.IsVisible(guardPlayer.eyes.position, sight + 5f)) continue;
                best = candidate;
                bestDistance = offset.sqrMagnitude;
            }
            return best;
        }

        private void AssignFollowBearings(BasePlayer owner)
        {
            // Start from where each guard already is (bearing around the owner) and
            // push neighbours apart until they are GuardSpacing apart on the ring, so
            // guards spread out with the least walking instead of lining up.
            var members = new List<BaseEntity>();
            var angles = new List<float>();
            var radii = new List<float>();
            foreach (var entry in followers)
            {
                if (entry.Value != owner || entry.Key == null || entry.Key.IsDestroyed) continue;
                var offset = entry.Key.transform.position - owner.transform.position;
                offset.y = 0f;
                var angle = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                var settings = GetGuardSettings(entry.Key);
                var index = 0;
                while (index < angles.Count && angles[index] < angle) index++;
                members.Insert(index, entry.Key);
                angles.Insert(index, angle);
                radii.Insert(index, Mathf.Clamp(offset.magnitude, settings.MinDistance, settings.MaxDistance));
            }
            var count = angles.Count;
            if (count > 1)
            {
                for (var iteration = 0; iteration < 10; iteration++)
                    for (var i = 0; i < count; i++)
                    {
                        var j = (i + 1) % count;
                        // Angle that keeps the pair GuardSpacing apart at the nearer radius.
                        var radius = Mathf.Min(radii[i], radii[j]);
                        var minAngle = Mathf.Min(360f / count,
                            2f * Mathf.Asin(Mathf.Min(1f, GuardSpacing * 0.5f / radius)) * Mathf.Rad2Deg);
                        var between = Mathf.Repeat(angles[j] - angles[i], 360f);
                        if (between >= minAngle) continue;
                        var push = (minAngle - between) * 0.5f;
                        angles[i] -= push;
                        angles[j] += push;
                    }
            }
            for (var i = 0; i < count; i++) followBearings[members[i]] = angles[i];
        }

        private Vector3 GetFollowTarget(BasePlayer owner, BaseEntity npc, float distance, bool wasWalking)
        {
            float bearing;
            if (!followBearings.TryGetValue(npc, out bearing))
            {
                var offset = npc.transform.position - owner.transform.position;
                bearing = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            }
            // Inside its band a guard stays put (only shifting sideways to make room);
            // beyond the max it walks back to the middle of the band; closer than the
            // min it steps out to the min.
            var settings = GetGuardSettings(npc);
            var middle = (settings.MinDistance + settings.MaxDistance) * 0.5f;
            var radius = distance > settings.MaxDistance || (wasWalking && distance > middle)
                ? middle
                : Mathf.Clamp(distance, settings.MinDistance, settings.MaxDistance);
            var direction = Quaternion.Euler(0f, bearing, 0f) * Vector3.forward;
            return owner.transform.position + direction * radius;
        }

        private static Vector3 GetAnchor(GuardSettings settings)
        {
            return new Vector3(settings.AnchorX, settings.AnchorY, settings.AnchorZ);
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // Index into GatherCategoryNames, or -1 when it is not something guards gather.
        private static int GatherCategory(BaseEntity entity)
        {
            if (entity is TreeEntity) return 0;
            var name = entity.ShortPrefabName;
            if (entity is OreResourceEntity) return name.Contains("sulfur") ? 3 : name.Contains("metal") ? 2 : 1;
            if (entity is CollectibleEntity)
            {
                if (name.Contains("hemp")) return 4;
                if (name.Contains("mushroom")) return 5;
                if (name.Contains("wood") || name.Contains("stone") || name.Contains("metal") || name.Contains("sulfur")) return 6;
                return 7;
            }
            return -1;
        }

        private static string NodeName(BaseEntity node)
        {
            var category = GatherCategory(node);
            return category >= 0 ? GatherCategoryNames[category] : "資源";
        }

        private void StopGathering(BaseEntity npc)
        {
            gatherTargets.Remove(npc);
            gatherStarted.Remove(npc);
        }

        // Moves to and harvests the nearest enabled resource around center. Returns
        // false when there is nothing to do (nothing in range, or the backpack is full).
        private bool UpdateGathering(BaseEntity npc, RustNavMeshAgent agent, Vector3 center, float radius)
        {
            var player = npc as BasePlayer;
            if (player?.inventory?.containerMain == null) return false;
            var now = Time.realtimeSinceStartup;
            var settings = GetGuardSettings(npc);
            if (settings.GatherMask == 0)
            {
                workStatus[npc] = "沒有勾選要採集的資源";
                StopGathering(npc);
                return false;
            }
            if (player.inventory.containerMain.IsFull())
            {
                workStatus[npc] = "背包已滿，停止採集";
                StopGathering(npc);
                return false;
            }
            BaseEntity node;
            gatherTargets.TryGetValue(npc, out node);
            if (node == null || node.IsDestroyed || FlatDistance(node.transform.position, center) > radius + 5f
                || (settings.GatherMask & (1 << GatherCategory(node))) == 0)
            {
                node = FindGatherNode(npc, center, radius, settings.GatherMask);
                if (node == null)
                {
                    workStatus[npc] = "附近沒有可採集的資源";
                    StopGathering(npc);
                    return false;
                }
                gatherTargets[npc] = node;
                gatherStarted[npc] = now;
            }
            var offset = node.transform.position - npc.transform.position;
            offset.y = 0f;
            // Stand close enough for the tool swing to connect (pickups: right next to it).
            var reach = node is CollectibleEntity ? 1.6f : node is TreeEntity ? 2.4f : 2.2f;
            if (offset.magnitude > reach)
            {
                float started;
                if (gatherStarted.TryGetValue(npc, out started) && now - started > GatherGiveUpSeconds)
                {
                    // Could not reach it (rocks, water, cliffs): skip it for a while.
                    unreachableNodes[node] = now + 120f;
                    StopGathering(npc);
                    return true;
                }
                walking.Add(npc);
                SetGait(npc, agent, offset.magnitude > 15f);
                agent.updateRotation = false;
                agent.isStopped = false;
                agent.SetDestination(node.transform.position);
                var heading = agent.desiredVelocityWS;
                if (heading.sqrMagnitude < 0.01f) heading = offset;
                FaceDirection(npc, heading, false, 90f);
                workStatus[npc] = "前往" + NodeName(node);
                return true;
            }
            walking.Remove(npc);
            SetGait(npc, agent, false);
            agent.isStopped = true;
            FaceDirection(npc, offset, false, 90f);
            float next;
            var collectible = node as CollectibleEntity;
            if (collectible != null)
            {
                // Hemp, mushrooms, piles and crops: pick it up (like pressing E).
                workStatus[npc] = "撿拾：" + NodeName(node);
                if (nextSwing.TryGetValue(npc, out next) && now < next) return true;
                nextSwing[npc] = now + GatherSwingInterval;
                StopGathering(npc);
                collectible.DoPickup(player, false);
                return true;
            }
            var melee = EnsureToolActive(player, node is TreeEntity ? "hatchet" : "pickaxe");
            workStatus[npc] = "採集中：" + NodeName(node);
            if (nextSwing.TryGetValue(npc, out next) && now < next) return true;
            nextSwing[npc] = now + GatherSwingInterval;
            GatherSwing(player, (ResourceEntity)node, melee);
            return true;
        }

        private BaseEntity FindGatherNode(BaseEntity npc, Vector3 center, float radius, int mask)
        {
            var now = Time.realtimeSinceStartup;
            var taken = new HashSet<BaseEntity>();
            foreach (var entry in gatherTargets)
                if (entry.Key != npc && entry.Value != null) taken.Add(entry.Value);
            // Use the server's entity grid rather than a collider overlap: pickups such as
            // hemp were not found through their colliders.
            var count = BaseEntity.Query.Server.GetInSphere(center, radius, gatherQueryBuffer,
                e => e is TreeEntity || e is OreResourceEntity || e is CollectibleEntity);
            BaseEntity best = null;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var candidate = gatherQueryBuffer[i];
                gatherQueryBuffer[i] = null;
                if (candidate == null || candidate.IsDestroyed || taken.Contains(candidate)) continue;
                var resource = candidate as ResourceEntity;
                if (resource != null && (resource.health <= 0f || resource.GetComponent<ResourceDispenser>() == null)) continue;
                var category = GatherCategory(candidate);
                if (category < 0 || (mask & (1 << category)) == 0) continue;
                float until;
                if (unreachableNodes.TryGetValue(candidate, out until) && now < until) continue;
                var distance = FlatDistance(candidate.transform.position, npc.transform.position);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private void GatherSwing(BasePlayer npc, ResourceEntity node, BaseMelee melee)
        {
            var dispenser = node.GetComponent<ResourceDispenser>();
            if (dispenser == null) { StopGathering(npc); return; }
            PlaySwing(npc, melee);
            // The native melee path damages nodes without giving resources to this
            // kind of NPC, so hand out the yield directly and wear the node down.
            dispenser.GiveResources(npc, GatherDamage, GatherDamage / Mathf.Max(1f, node.MaxHealth()), melee);
            node.health -= GatherDamage;
            if (node.health <= 0f)
            {
                StopGathering(npc);
                node.OnDied(new HitInfo(npc, node, Rust.DamageType.Slash, GatherDamage));
            }
            else node.SendNetworkUpdate();
        }

        // A guard killed someone: remember where, so it can collect their items.
        private void OnEntityDeath(BaseCombatEntity victim, HitInfo info)
        {
            var killer = info?.Initiator;
            // Players / human NPCs leave a lootable body; animals leave a corpse to butcher.
            if (victim == null || killer == null || !(victim is BasePlayer || victim.IsNpc) || !npcs.Contains(killer)) return;
            if (!GetGuardSettings(killer).AutoLoot) return;
            var now = Time.realtimeSinceStartup;
            lootTasks[killer] = new LootTask { Position = victim.transform.position, Created = now, Started = now };
        }

        private static List<ItemContainer> LootContainers(BaseEntity entity)
        {
            var result = new List<ItemContainer>();
            var corpse = entity as LootableCorpse;
            if (corpse?.containers != null)
                foreach (var container in corpse.containers)
                    if (container != null) result.Add(container);
            var dropped = entity as DroppedItemContainer;
            if (dropped?.inventory != null) result.Add(dropped.inventory);
            return result;
        }

        // Animal corpses (boar, bear, deer...) are harvested with a tool, not looted.
        private static bool IsAnimalCorpse(BaseEntity entity)
        {
            var corpse = entity as BaseCorpse;
            return corpse != null && !(corpse is LootableCorpse) && corpse.resourceDispenser != null;
        }

        private static bool HasLoot(BaseEntity entity)
        {
            if (IsAnimalCorpse(entity)) return ((BaseCorpse)entity).health > 0f;
            foreach (var container in LootContainers(entity))
                if (container.itemList.Count > 0) return true;
            return false;
        }

        private readonly BaseEntity[] lootQueryBuffer = new BaseEntity[256];

        private BaseEntity FindLootContainer(Vector3 position)
        {
            var count = BaseEntity.Query.Server.GetInSphere(position, 5f, lootQueryBuffer,
                e => e is LootableCorpse || e is DroppedItemContainer || IsAnimalCorpse(e));
            var found = new List<BaseEntity>();
            for (var i = 0; i < count; i++)
            {
                found.Add(lootQueryBuffer[i]);
                lootQueryBuffer[i] = null;
            }
            BaseEntity best = null;
            var bestDistance = float.MaxValue;
            foreach (var entity in found)
            {
                if (entity == null || entity.IsDestroyed || !HasLoot(entity)) continue;
                var distance = Vector3.Distance(entity.transform.position, position);
                if (distance >= bestDistance) continue;
                best = entity;
                bestDistance = distance;
            }
            return best;
        }

        // After combat, walk to the body (or dropped backpack) of its kill and move the
        // items into its own backpack. Returns true while busy with that.
        private bool UpdateLooting(BaseEntity npc, RustNavMeshAgent agent)
        {
            LootTask task;
            if (!lootTasks.TryGetValue(npc, out task)) return false;
            var player = npc as BasePlayer;
            var main = player?.inventory?.containerMain;
            var now = Time.realtimeSinceStartup;
            if (main == null || now - task.Created > LootGiveUpSeconds) { lootTasks.Remove(npc); return false; }
            if (main.IsFull())
            {
                workStatus[npc] = "背包已滿，無法撿取戰利品";
                lootTasks.Remove(npc);
                return false;
            }
            if (task.Container == null || task.Container.IsDestroyed || !HasLoot(task.Container))
            {
                task.Container = FindLootContainer(task.Position);
                // The body can take a moment to appear after the kill.
                if (task.Container == null)
                {
                    if (now - task.Created > 3f) lootTasks.Remove(npc);
                    return false;
                }
                task.Started = now;
            }
            var offset = task.Container.transform.position - npc.transform.position;
            offset.y = 0f;
            if (offset.magnitude > 1.8f)
            {
                if (now - task.Started > GatherGiveUpSeconds) { lootTasks.Remove(npc); return false; }
                walking.Add(npc);
                SetGait(npc, agent, offset.magnitude > 10f);
                agent.updateRotation = false;
                agent.isStopped = false;
                agent.SetDestination(task.Container.transform.position);
                var heading = agent.desiredVelocityWS;
                FaceDirection(npc, heading.sqrMagnitude > 0.01f ? heading : offset, false, 90f);
                workStatus[npc] = "前往撿取戰利品";
                return true;
            }
            walking.Remove(npc);
            SetGait(npc, agent, false);
            agent.isStopped = true;
            FaceDirection(npc, offset, false, 90f);
            if (IsAnimalCorpse(task.Container))
            {
                ButcherSwing(player, (BaseCorpse)task.Container, now);
                if (task.Container == null || task.Container.IsDestroyed || !HasLoot(task.Container))
                {
                    lootTasks.Remove(npc);
                    workStatus[npc] = "剝取完成";
                }
                return true;
            }
            var taken = 0;
            foreach (var container in LootContainers(task.Container))
                foreach (var item in new List<Item>(container.itemList))
                {
                    if (main.IsFull()) break;
                    if (item.MoveToContainer(main)) taken++;
                }
            workStatus[npc] = taken > 0 ? $"撿取了 {taken} 件戰利品" : "戰利品裝不下了";
            lootTasks.Remove(npc);
            return true;
        }

        // Harvest an animal corpse with a hatchet: meat, leather, bones and fat go into
        // the backpack, like a player hitting the corpse.
        private void ButcherSwing(BasePlayer npc, BaseCorpse corpse, float now)
        {
            var melee = EnsureToolActive(npc, "hatchet");
            workStatus[npc] = "剝取動物中";
            float next;
            if (nextSwing.TryGetValue(npc, out next) && now < next) return;
            nextSwing[npc] = now + GatherSwingInterval;
            PlaySwing(npc, melee);
            // The dispenser's own GiveResources skipped meat and fat on corpses, and its
            // DoGather path refuses this kind of NPC, so share out the corpse contents
            // in proportion to the damage and hand over everything left on the last hit.
            Dictionary<ItemDefinition, float> start;
            if (!corpseStartAmounts.TryGetValue(corpse, out start))
            {
                start = new Dictionary<ItemDefinition, float>();
                foreach (var amount in corpse.resourceDispenser.containedItems) start[amount.itemDef] = amount.amount;
                corpseStartAmounts[corpse] = start;
            }
            var fraction = GatherDamage / Mathf.Max(1f, corpse.MaxHealth());
            var finishing = corpse.health - GatherDamage <= 0f;
            foreach (var amount in corpse.resourceDispenser.containedItems)
            {
                if (amount.amount <= 0f) continue;
                float total;
                start.TryGetValue(amount.itemDef, out total);
                var give = finishing ? Mathf.CeilToInt(amount.amount) : Mathf.Max(1, Mathf.RoundToInt(total * fraction));
                give = Mathf.Min(give, Mathf.CeilToInt(amount.amount));
                amount.amount -= give;
                var item = ItemManager.Create(amount.itemDef, give);
                if (item == null) continue;
                // Let rate plugins (e.g. GatherRates) scale it like a normal gather.
                Oxide.Core.Interface.CallHook("OnDispenserGather", corpse.resourceDispenser, npc, item);
                if (!item.MoveToContainer(npc.inventory.containerMain))
                    item.Drop(npc.transform.position + Vector3.up, Vector3.up);
            }
            corpse.health = Mathf.Max(0f, corpse.health - GatherDamage);
            if (corpse.health <= 0f)
            {
                corpseStartAmounts.Remove(corpse);
                corpse.Kill(BaseNetworkable.DestroyMode.Gib);
            }
            else corpse.SendNetworkUpdate();
        }

        private readonly Dictionary<BaseCorpse, Dictionary<ItemDefinition, float>> corpseStartAmounts =
            new Dictionary<BaseCorpse, Dictionary<ItemDefinition, float>>();

        // Swing the held tool so other players see it: the attack signal on the player
        // drives the third-person swing animation; a zero-damage ServerUse adds the
        // swoosh and the impact sparks/sound on whatever is in front.
        private static void PlaySwing(BasePlayer npc, BaseMelee melee)
        {
            npc.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
            if (melee != null && !melee.HasAttackCooldown())
                melee.ServerUse(new HeldEntityServerUseParams(0f, 1f, null, false, false));
        }

        private void HoldAtAnchor(BaseEntity npc, RustNavMeshAgent agent, GuardSettings settings)
        {
            var anchor = GetAnchor(settings);
            var offset = anchor - npc.transform.position;
            offset.y = 0f;
            if (offset.magnitude > 4f)
            {
                walking.Add(npc);
                SetGait(npc, agent, offset.magnitude > 15f);
                agent.isStopped = false;
                agent.SetDestination(anchor);
                FaceDirection(npc, agent.desiredVelocityWS.sqrMagnitude > 0.01f ? agent.desiredVelocityWS : offset, false, 90f);
                return;
            }
            walking.Remove(npc);
            SetGait(npc, agent, false);
            agent.isStopped = true;
        }

        // Once per second: passive production, smelting and crafting from the backpack.
        private void WorkTick()
        {
            var now = Time.realtimeSinceStartup;
            foreach (var entity in npcs)
            {
                var npc = entity as BasePlayer;
                if (npc == null || npc.IsDestroyed || npc.IsDead() || npc.inventory?.containerMain == null) continue;
                var settings = GetGuardSettings(entity);
                var main = npc.inventory.containerMain;
                if (guards.ContainsKey(entity)) RefillAmmo(npc);
                float next;
                if (settings.Work == WorkProduce && (!nextProduce.TryGetValue(entity, out next) || now >= next))
                {
                    nextProduce[entity] = now + ProduceInterval;
                    if (main.IsFull()) workStatus[entity] = "背包已滿，停止產出";
                    else
                    {
                        GiveToBackpack(npc, "wood", 40);
                        GiveToBackpack(npc, "stones", 25);
                        GiveToBackpack(npc, "metal.ore", 10);
                        GiveToBackpack(npc, "sulfur.ore", 6);
                        workStatus[entity] = "產出資源中（每 30 秒）";
                    }
                }
                if (settings.Smelt && (!nextSmelt.TryGetValue(entity, out next) || now >= next))
                {
                    nextSmelt[entity] = now + SmeltInterval;
                    SmeltStep(npc);
                }
                UpdateCrafting(npc, settings, now);
            }
        }

        private static int ItemId(string shortname)
        {
            return ItemManager.FindItemDefinition(shortname)?.itemid ?? 0;
        }

        private void GiveToBackpack(BasePlayer npc, string shortname, int amount)
        {
            var item = ItemManager.CreateByName(shortname, amount);
            if (item == null) return;
            if (!item.MoveToContainer(npc.inventory.containerMain))
                item.Drop(npc.transform.position + Vector3.up, Vector3.up);
        }

        private static readonly string[][] SmeltRecipes =
        {
            new[] { "metal.ore", "metal.fragments", "2" },
            new[] { "sulfur.ore", "sulfur", "2" },
            new[] { "hq.metal.ore", "metal.refined", "1" }
        };

        // Like a small furnace: burns 1 wood per step (making charcoal) while there is ore.
        private void SmeltStep(BasePlayer npc)
        {
            var main = npc.inventory.containerMain;
            var woodId = ItemId("wood");
            var hasOre = false;
            foreach (var recipe in SmeltRecipes)
                if (main.GetAmount(ItemId(recipe[0]), true) > 0) hasOre = true;
            if (!hasOre) { workStatus[npc] = "熔煉：沒有礦石可煉"; return; }
            if (main.GetAmount(woodId, true) <= 0) { workStatus[npc] = "熔煉暫停：缺少木頭"; return; }
            main.Take(null, woodId, 1);
            GiveToBackpack(npc, "charcoal", 1);
            foreach (var recipe in SmeltRecipes)
            {
                var oreId = ItemId(recipe[0]);
                var taken = main.Take(null, oreId, Mathf.Min(int.Parse(recipe[2]), main.GetAmount(oreId, true)));
                if (taken > 0) GiveToBackpack(npc, recipe[1], taken);
            }
            workStatus[npc] = "熔煉中";
        }

        private void UpdateCrafting(BasePlayer npc, GuardSettings settings, float now)
        {
            if (settings.CraftRemaining <= 0 || string.IsNullOrEmpty(settings.CraftItem)) { craftFinishAt.Remove(npc); return; }
            var definition = ItemManager.FindItemDefinition(settings.CraftItem);
            var blueprint = definition == null ? null : ItemManager.FindBlueprint(definition);
            if (blueprint == null) { settings.CraftRemaining = 0; return; }
            var main = npc.inventory.containerMain;
            float finish;
            if (craftFinishAt.TryGetValue(npc, out finish))
            {
                if (now < finish) return;
                craftFinishAt.Remove(npc);
                GiveToBackpack(npc, settings.CraftItem, Mathf.Max(1, blueprint.amountToCreate));
                settings.CraftRemaining--;
                if (settings.CraftRemaining <= 0) settings.CraftItem = null;
                return;
            }
            foreach (var ingredient in blueprint.ingredients)
                if (main.GetAmount(ingredient.itemDef.itemid, true) < (int)ingredient.amount)
                {
                    workStatus[npc] = $"製作暫停：缺少{MaterialName(ingredient.itemDef)}";
                    return;
                }
            foreach (var ingredient in blueprint.ingredients)
                main.Take(null, ingredient.itemDef.itemid, (int)ingredient.amount);
            craftFinishAt[npc] = now + Mathf.Max(1f, blueprint.time);
        }

        private void CancelCrafting(BasePlayer npc, GuardSettings settings)
        {
            // Refund the materials of an item that is half made.
            if (craftFinishAt.Remove(npc) && !string.IsNullOrEmpty(settings.CraftItem))
            {
                var definition = ItemManager.FindItemDefinition(settings.CraftItem);
                var blueprint = definition == null ? null : ItemManager.FindBlueprint(definition);
                if (blueprint != null)
                    foreach (var ingredient in blueprint.ingredients)
                        GiveToBackpack(npc, ingredient.itemDef.shortname, (int)ingredient.amount);
            }
            settings.CraftItem = null;
            settings.CraftRemaining = 0;
        }

        private static readonly Dictionary<string, string> MaterialNames = new Dictionary<string, string>
        {
            { "wood", "木頭" }, { "stones", "石頭" }, { "cloth", "布料" }, { "metal.fragments", "金屬碎片" },
            { "metal.refined", "高級金屬" }, { "charcoal", "木炭" }, { "sulfur", "硫磺" }, { "gunpowder", "火藥" },
            { "lowgradefuel", "低級燃料" }, { "animal.fat", "動物脂肪" }, { "bone.fragments", "骨頭碎片" },
            { "metal.ore", "金屬礦石" }, { "sulfur.ore", "硫磺礦石" }, { "hq.metal.ore", "高級金屬礦石" },
            { "scrap", "廢料" }, { "leather", "皮革" }, { "fat.animal", "動物脂肪" }
        };

        private static string MaterialName(ItemDefinition definition)
        {
            string name;
            return MaterialNames.TryGetValue(definition.shortname, out name) ? name : definition.displayName.english;
        }

        private const float BackpackReach = 5f;
        private const float LookHoldDistance = 8f;

        // True when the player's crosshair is on the NPC (roughly), within range.
        private static bool IsLookingAt(BasePlayer player, BaseEntity npc, float range)
        {
            var toNpc = npc.CenterPoint() - player.eyes.position;
            if (toNpc.magnitude > range) return false;
            return Vector3.Angle(player.eyes.HeadForward(), toNpc) < 12f;
        }

        private bool CanAccessBackpack(BasePlayer player, BaseEntity npc)
        {
            if (player == null || npc == null) return false;
            if (player.IsAdmin) return true;
            BasePlayer owner;
            if (followers.TryGetValue(npc, out owner) && owner == player) return true;
            PendingOwner pending;
            return pendingOwners.TryGetValue(npc, out pending) && pending.OwnerId == player.userID.Get();
        }

        // Walk up to a guard, look at it and press E to open its backpack.
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null || !input.WasJustPressed(BUTTON.USE)) return;
            RaycastHit hit;
            // Reach beyond the default 3 m minimum distance a guard keeps from its owner.
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, BackpackReach, Rust.Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore)) return;
            var npc = hit.GetEntity() as BasePlayer;
            if (npc == null || npc == player || !npcs.Contains(npc) || !CanAccessBackpack(player, npc)) return;
            OpenBackpack(player, npc);
        }

        private void OpenBackpack(BasePlayer player, BasePlayer npc)
        {
            var loot = player.inventory.loot;
            loot.Clear();
            loot.PositionChecks = false;
            loot.entitySource = npc;
            loot.itemSource = null;
            // Same containers and panel as looting a downed player: backpack, clothing, belt.
            loot.AddContainer(npc.inventory.containerMain);
            loot.AddContainer(npc.inventory.containerWear);
            loot.AddContainer(npc.inventory.containerBelt);
            loot.MarkDirty();
            loot.SendImmediate();
            player.ClientRPC(RpcTarget.Player("RPC_OpenLootPanel", player), "player_corpse");
        }

        // Awake players normally cannot be looted; allow it for guard backpacks.
        private object CanLootPlayer(BasePlayer target, BasePlayer looter)
        {
            return target != null && npcs.Contains(target) && CanAccessBackpack(looter, target) ? (object)true : null;
        }

        private bool TeleportNearOwner(BaseEntity npc, BasePlayer owner, RustNavMeshAgent agent)
        {
            float next;
            if (nextTeleportTry.TryGetValue(npc, out next) && Time.realtimeSinceStartup < next) return false;
            nextTeleportTry[npc] = Time.realtimeSinceStartup + TeleportRetryDelay;
            var settings = GetGuardSettings(npc);
            var radius = (settings.MinDistance + settings.MaxDistance) * 0.5f;
            var behind = -GetOwnerMotion(owner).Heading;
            int slot;
            formationSlots.TryGetValue(npc, out slot);
            // Prefer a spot behind the owner (spread by slot so a group does not land on
            // one point), then try other directions around them.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var angle = slot * 35f + attempt * 45f * (attempt % 2 == 0 ? 1f : -1f);
                var spot = SnapToGround(owner.transform.position + Quaternion.Euler(0f, angle, 0f) * behind * radius);
                // Skip other floors, rooftops or cliff bottoms.
                if (Mathf.Abs(spot.y - owner.transform.position.y) > 3f) continue;
                if (!agent.Warp(spot)) continue;
                if (Vector3.Distance(npc.transform.position, owner.transform.position) > radius + 5f) continue;
                agent.ResetPath();
                agent.isStopped = true;
                walking.Remove(npc);
                SetGait(npc, agent, false);
                FaceDirection(npc, owner.transform.position - npc.transform.position, true);
                return true;
            }
            return false;
        }

        private bool IsCrowded(BaseEntity npc)
        {
            foreach (var other in npcs)
                if (other != null && other != npc && !other.IsDestroyed
                    && FlatDistance(other.transform.position, npc.transform.position) < 1f)
                    return true;
            return false;
        }

        private GuardSettings GetGuardSettings(BaseEntity npc)
        {
            GuardSettings settings;
            if (!guardSettings.TryGetValue(npc, out settings))
                guardSettings[npc] = settings = new GuardSettings();
            return settings;
        }

        private const int MaxNameLength = 16;

        private string GetDisplayName(BaseEntity npc)
        {
            var name = GetGuardSettings(npc).Name;
            return string.IsNullOrEmpty(name) ? "護衛 #" + GetNpcNumber(npc) : name;
        }

        // Shown wherever the game prints a player name (kill feed, death messages).
        private void ApplyName(BaseEntity npc)
        {
            var player = npc as BasePlayer;
            if (player == null || player.IsDestroyed) return;
            player.displayName = GetDisplayName(npc);
            player.SendNetworkUpdate();
        }

        private static string SanitizeName(string text)
        {
            if (text == null) return null;
            // Drop characters that would break CUI rich text or console quoting.
            var clean = new System.Text.StringBuilder();
            foreach (var c in text)
                if (c != '<' && c != '>' && c != '"' && c != '\\' && !char.IsControl(c)) clean.Append(c);
            var result = clean.ToString().Trim();
            if (result.Length > MaxNameLength) result = result.Substring(0, MaxNameLength).Trim();
            return result.Length == 0 ? null : result;
        }

        private void ForgetNpc(BaseEntity npc)
        {
            guardSettings.Remove(npc);
            npcNumbers.Remove(npc);
            previews.Remove(npc);
            previewOrigins.Remove(npc);
            pendingOwners.Remove(npc);
            nextTeleportTry.Remove(npc);
            StopGathering(npc);
            nextSwing.Remove(npc);
            craftFinishAt.Remove(npc);
            nextProduce.Remove(npc);
            nextSmelt.Remove(npc);
            workStatus.Remove(npc);
            lootTasks.Remove(npc);
        }

        private OwnerMotion GetOwnerMotion(BasePlayer owner)
        {
            OwnerMotion motion;
            if (ownerMotion.TryGetValue(owner, out motion)) return motion;
            var look = owner.eyes.rotation * Vector3.forward;
            look.y = 0f;
            motion = new OwnerMotion
            {
                LastPosition = owner.transform.position,
                Heading = look.sqrMagnitude > 0.001f ? look.normalized : Vector3.forward
            };
            ownerMotion[owner] = motion;
            return motion;
        }

        private void TrackOwnerMotion(BasePlayer owner)
        {
            var motion = GetOwnerMotion(owner);
            var delta = owner.transform.position - motion.LastPosition;
            delta.y = 0f;
            motion.LastPosition = owner.transform.position;
            var speed = delta.magnitude / FollowTick;
            if (speed > 20f) speed = 0f; // teleport
            motion.Speed = Mathf.Lerp(motion.Speed, speed, 0.5f);
            if (delta.magnitude > 0.1f)
                motion.Heading = Vector3.RotateTowards(motion.Heading, delta.normalized, 60f * Mathf.Deg2Rad, 0f);
        }

        private Vector3 GetSpawnPosition(BasePlayer owner)
        {
            var offsets = new[]
            {
                new Vector3(-3.5f, 0f, -2.0f),
                new Vector3( 3.5f, 0f, -2.0f),
                new Vector3(-4.5f, 0f,  2.0f),
                new Vector3( 4.5f, 0f,  2.0f),
                new Vector3( 0.0f, 0f, -4.5f),
                new Vector3(-6.0f, 0f,  0.0f),
                new Vector3( 6.0f, 0f,  0.0f),
                new Vector3( 0.0f, 0f,  4.5f)
            };
            var slot = npcs.Count % offsets.Length;
            var offset = offsets[slot];
            return SnapToGround(owner.transform.position
                + owner.transform.right * offset.x
                + owner.transform.forward * offset.z);
        }

        private Vector3 SnapToGround(Vector3 position)
        {
            // The owner's height is wrong on slopes; snap to the real ground (terrain,
            // rocks or building floors) so an NPC never ends up inside the ground.
            RaycastHit hit;
            if (Physics.Raycast(position + Vector3.up * 3f, Vector3.down, out hit, 10f,
                Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World | Rust.Layers.Mask.Construction, QueryTriggerInteraction.Ignore))
                position.y = hit.point.y;
            else
                position.y = Mathf.Max(position.y, TerrainMeta.HeightMap.GetHeight(position));
            return position;
        }

        // Live preview: CUI cannot render a 3D model, so the guard being edited stands
        // in front of the viewer (beside the left-docked appearance panel) and holds
        // still until the panel closes.
        private readonly Dictionary<BaseEntity, BasePlayer> previews = new Dictionary<BaseEntity, BasePlayer>();

        private void StartPreview(BasePlayer viewer, BaseEntity npc)
        {
            BasePlayer current;
            if (previews.TryGetValue(npc, out current) && current == viewer) return;
            EndPreview(viewer);
            previews[npc] = viewer;
            // Remember where it was, so it goes back there when the preview ends
            // instead of every previewed guard piling up in front of the viewer.
            previewOrigins[npc] = new KeyValuePair<Vector3, float>(npc.transform.position, npc.transform.eulerAngles.y);
            var forward = viewer.eyes.rotation * Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            // Stand in the open centre of the F6 menu (slightly left of screen centre).
            var spot = SnapToGround(viewer.transform.position + forward * 3.2f - right * 0.3f);
            walking.Remove(npc);
            running.Remove(npc);
            var agent = npc.GetComponent<RustNavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.ResetPath();
                agent.isStopped = true;
                if (!agent.Warp(spot)) npc.transform.position = spot;
            }
            else npc.transform.position = spot;
            FaceDirection(npc, viewer.transform.position - npc.transform.position, true);
        }

        private void EndPreview(BasePlayer viewer)
        {
            foreach (var entry in new List<KeyValuePair<BaseEntity, BasePlayer>>(previews))
            {
                if (entry.Value != viewer) continue;
                previews.Remove(entry.Key);
                ReturnFromPreview(entry.Key);
            }
        }

        private readonly Dictionary<BaseEntity, KeyValuePair<Vector3, float>> previewOrigins =
            new Dictionary<BaseEntity, KeyValuePair<Vector3, float>>();

        // Put a guard back where it stood before it was called over for a preview.
        private void ReturnFromPreview(BaseEntity npc)
        {
            KeyValuePair<Vector3, float> origin;
            if (npc == null || npc.IsDestroyed || !previewOrigins.TryGetValue(npc, out origin)) return;
            previewOrigins.Remove(npc);
            walking.Remove(npc);
            running.Remove(npc);
            var agent = npc.GetComponent<RustNavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                agent.ResetPath();
                if (!agent.Warp(origin.Key)) npc.transform.position = origin.Key;
            }
            else npc.transform.position = origin.Key;
            FaceDirection(npc, Quaternion.Euler(0f, origin.Value, 0f) * Vector3.forward, true);
        }

        private void StreamPositions()
        {
            foreach (var npc in npcs)
            {
                if (npc == null || npc.IsDestroyed || !npc.transform.hasChanged) continue;
                npc.transform.hasChanged = false;
                npc.SendNetworkUpdate_Position();
            }
        }

        private int AllocateFormationSlot(BasePlayer owner, BaseEntity npc)
        {
            var used = new HashSet<int>();
            foreach (var entry in followers)
            {
                if (entry.Value != owner || entry.Key == npc) continue;
                int existing;
                if (formationSlots.TryGetValue(entry.Key, out existing)) used.Add(existing);
            }
            var slot = 0;
            while (used.Contains(slot)) slot++;
            formationSlots[npc] = slot;
            return slot;
        }

        private object OnEntityTakeDamage(BaseCombatEntity victim, HitInfo hit)
        {
            if (victim == null || hit == null) return null;
            var attacker = hit.Initiator as BaseCombatEntity;
            // Guard bullets may only damage the currently selected attacker, never bystanders.
            if (attacker != null && npcs.Contains(attacker))
            {
                GuardState firingGuard;
                if (!guards.TryGetValue(attacker, out firingGuard) || victim != firingGuard.Target
                    || !ValidThreat(victim, firingGuard.Owner, GetThreatRange(attacker), ThreatCenter(attacker, firingGuard.Owner))
                    || firingGuard.Owner.InSafeZone())
                { hit.damageTypes.ScaleAll(0f); return true; }
            }
            GuardSettings victimSettings;
            var invulnerable = npcs.Contains(victim) && guardSettings.TryGetValue(victim, out victimSettings) && victimSettings.Invulnerable;
            if (attacker != null && hit.damageTypes.Total() > 0f)
            {
                // Retaliation (both modes): whoever hurts the owner or a guard becomes
                // the target, even when the hit on an invulnerable guard is cancelled.
                foreach (var entry in guards)
                {
                    var guard = entry.Value;
                    if ((victim == guard.Owner || victim == entry.Key) && guard.Owner.IsConnected
                        && !guard.Owner.IsDead() && !guard.Owner.InSafeZone()
                        && ValidThreat(attacker, guard.Owner, GetThreatRange(entry.Key), ThreatCenter(entry.Key, guard.Owner)))
                    {
                        guard.Target = attacker;
                        guard.AlertUntil = Time.realtimeSinceStartup + 15f;
                    }
                }
            }
            if (invulnerable) { hit.damageTypes.ScaleAll(0f); return true; }
            return null;
        }

        private bool UpdateGuard(BaseEntity entity, BasePlayer owner, RustNavMeshAgent agent)
        {
            GuardState guard;
            if (!guards.TryGetValue(entity, out guard)) return false;
            var npc = entity as BasePlayer;
            if (npc == null) return false;
            // Passive guards never pick targets themselves; OnEntityTakeDamage assigns
            // the attacker once the owner or a guard is hurt.
            if (guard.Target == null && GetGuardSettings(entity).Aggressive)
            {
                guard.Target = FindNearbyThreat(owner, entity);
                if (guard.Target != null) guard.AlertUntil = Time.realtimeSinceStartup + 15f;
            }
            if (guard.Target == null) return false;
            var settings = GetGuardSettings(entity);
            // Do not chase further from the owner than its follow band allows.
            // Gatherers are leashed to their work area instead of the owner.
            var gathering = settings.Work == WorkGather;
            var leashCenter = gathering ? GetAnchor(settings) : owner.transform.position;
            var leash = gathering ? GatherRadius + 20f : Mathf.Max(20f, settings.MaxDistance + 10f);
            if (Time.realtimeSinceStartup > guard.AlertUntil || owner.InSafeZone()
                || !ValidThreat(guard.Target, owner, GetThreatRange(entity), ThreatCenter(entity, owner))
                || Vector3.Distance(entity.transform.position, leashCenter) > leash)
            { guard.Target = null; return false; }
            var target = guard.Target;
            EnsureWeaponActive(npc);
            var weapon = npc.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
            if (weapon == null) { guard.Target = null; return false; }
            var aim = target.CenterPoint() - npc.eyes.position;
            // Do not fire through terrain or structures. Native weapon raycasts resolve body hits.
            var visible = target.IsVisible(npc.eyes.position, settings.SightRange + 5f);
            // Fire within its sight range; otherwise move closer.
            if (!visible || aim.magnitude > settings.SightRange)
            {
                agent.updateRotation = false;
                agent.isStopped = false;
                SetGait(entity, agent, true);
                agent.SetDestination(target.transform.position);
                FaceDirection(entity, agent.desiredVelocityWS);
                return true;
            }
            SetGait(npc, agent, false);
            agent.isStopped = true;
            agent.updateRotation = false;
            FaceDirection(npc, aim);
            var flatAim = new Vector3(aim.x, 0f, aim.z);
            if (Vector3.Angle(npc.transform.forward, flatAim) > 10f) return true;
            var now = Time.realtimeSinceStartup;
            if (now < guard.NextShot) return true;
            if (weapon.primaryMagazine.contents == 0)
            {
                if (guard.ReloadUntil == 0f)
                {
                    guard.ReloadUntil = now + 3f;
                    weapon.SignalBroadcast(BaseEntity.Signal.Reload);
                }
                if (now < guard.ReloadUntil) return true;
                weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
                weapon.SendNetworkUpdate();
                guard.ReloadUntil = 0f;
            }
            // Native NPC gunfire ignores many NPC factions. Use its ammo/effects, then
            // resolve one collision-checked hit ourselves (never apply native damage too).
            if (weapon.HasAttackCooldown()) return true;
            var beforeAmmo = weapon.primaryMagazine.contents;
            weapon.ServerUse(new HeldEntityServerUseParams(0f, 1f,
                Matrix4x4.TRS(npc.eyes.position, Quaternion.LookRotation(aim.normalized), Vector3.one), true, true));
            if (weapon.primaryMagazine.contents < beforeAmmo)
            {
                var hits = new List<RaycastHit>();
                GamePhysics.TraceAll(new Ray(npc.eyes.position, aim.normalized), 0f, hits,
                    aim.magnitude + 1f, 1220225793, QueryTriggerInteraction.Ignore, npc);
                hits.Sort((a, b) => a.distance.CompareTo(b.distance));
                foreach (var collision in hits)
                {
                    var hitEntity = collision.GetEntity();
                    if (hitEntity == npc || hitEntity == weapon) continue;
                    // The first obstruction must be the attacker, not a wall or bystander.
                    if (hitEntity == target)
                    {
                        var hit = new HitInfo(npc, target, Rust.DamageType.Bullet, 20f)
                        {
                            Weapon = weapon, HitPositionWorld = collision.point,
                            PointStart = npc.eyes.position, PointEnd = collision.point,
                            UseProtection = true, DidHit = true
                        };
                        target.Hurt(hit);
                    }
                    break;
                }
            }
            guard.NextShot = now + 0.5f;
            return true;
        }

        private bool RescueOwner(BaseEntity npc, BasePlayer owner, RustNavMeshAgent agent)
        {
            if (owner == null || !owner.IsWounded())
            {
                if (owner != null) reviving.Remove(owner);
                return false;
            }
            if (!reviving.ContainsKey(owner)) reviving[owner] = Time.realtimeSinceStartup;
            var distance = Vector3.Distance(npc.transform.position, owner.transform.position);
            if (distance > 2.5f)
            {
                agent.isStopped = false;
                agent.updateRotation = false;
                SetGait(npc, agent, true);
                agent.SetDestination(owner.transform.position);
                FaceDirection(npc, agent.desiredVelocityWS);
                return true;
            }
            SetGait(npc, agent, false);
            agent.isStopped = true;
            agent.updateRotation = false;
            FaceDirection(npc, owner.transform.position - npc.transform.position);
            if (Time.realtimeSinceStartup - reviving[owner] >= 3f)
            {
                owner.StopWounded(npc as BasePlayer);
                owner.health = Mathf.Max(owner.health, 25f);
                owner.SendNetworkUpdateImmediate();
                reviving.Remove(owner);
                SendReply(owner, "你的護衛已經把你救起來了。");
            }
            return true;
        }

        private void SetGait(BaseEntity npc, RustNavMeshAgent agent, bool run)
        {
            if (run) running.Add(npc);
            else running.Remove(npc);
            agent.speed = run ? RunSpeed : WalkSpeed;
        }

        private void FaceDirection(BaseEntity npc, Vector3 direction, bool immediate = false, float maxDegrees = 45f)
        {
            if (npc == null || npc.IsDestroyed) return;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return;
            var desired = Quaternion.LookRotation(direction.normalized);
            var current = Quaternion.Euler(0f, npc.transform.eulerAngles.y, 0f);
            var rotation = immediate ? desired : Quaternion.RotateTowards(current, desired, maxDegrees);
            npc.transform.rotation = rotation;
            var humanoid = npc as BasePlayer;
            if (humanoid != null)
            {
                // One yaw for root, body/eyes and the replicated view. Do not let the
                // nav agent or shopkeeper greeting independently rotate the upper body.
                humanoid.eyes.rotation = rotation;
                humanoid.viewAngles = rotation.eulerAngles;
                humanoid.modelState.headLook = false;
                humanoid.modelState.lookDir = rotation * Vector3.forward;
                humanoid.modelState.onground = true;
                // Clients pick the run animation from this flag, not from speed.
                humanoid.modelState.sprinting = running.Contains(npc);
                humanoid.modelState.ducked = false;
                humanoid.SendModelState();
            }
        }

        private void UpdateFollowers()
        {
            stopped.Clear();
            var owners = new HashSet<BasePlayer>(followers.Values);
            followBearings.Clear();
            foreach (var owner in owners)
            {
                if (owner == null || owner.IsDestroyed) continue;
                TrackOwnerMotion(owner);
                AssignFollowBearings(owner);
            }
            foreach (var owner in new List<BasePlayer>(ownerMotion.Keys))
                if (!owners.Contains(owner)) ownerMotion.Remove(owner);
            foreach (var entry in followers)
            {
                var npc = entry.Key;
                var player = entry.Value;
                if (npc == null || npc.IsDestroyed || (npc is BaseCombatEntity && (npc as BaseCombatEntity).IsDead()))
                { stopped.Add(npc); continue; }
                if (player == null || !player.IsConnected || player.IsDead() || player.IsSleeping())
                {
                    // Owner left, died or is asleep: wait here and rejoin when they wake.
                    if (player != null)
                        pendingOwners[npc] = new PendingOwner { OwnerId = player.userID.Get(), Guard = guards.ContainsKey(npc) };
                    stopped.Add(npc);
                    continue;
                }
                var agent = npc.GetComponent<RustNavMeshAgent>();
                if (agent == null || !agent.enabled || !agent.isOnNavMesh)
                { stopped.Add(npc); continue; }
                BasePlayer previewer;
                if (previews.TryGetValue(npc, out previewer))
                {
                    // Hold still in front of whoever is editing its appearance.
                    SetGait(npc, agent, false);
                    agent.isStopped = true;
                    if (previewer != null)
                        FaceDirection(npc, previewer.transform.position - npc.transform.position, false, 90f);
                    continue;
                }
                var distance = Vector3.Distance(npc.transform.position, player.transform.position);
                if (autoTeleport)
                {
                    // Not while the owner rides a vehicle: they would pop in behind it
                    // every tick. They catch up once the owner dismounts.
                    if (distance > teleportDistance && !player.isMounted && GetGuardSettings(npc).Work != WorkGather
                        && TeleportNearOwner(npc, player, agent))
                        distance = Vector3.Distance(npc.transform.position, player.transform.position);
                }
                else if (distance > 80f)
                {
                    SendReply(player, "護衛離你太遠，已停下等待。走回去後重新指派它跟隨你。");
                    stopped.Add(npc);
                    continue;
                }
                if (RescueOwner(npc, player, agent))
                {
                    npc.UpdateNetworkGroup();
                    continue;
                }
                if (UpdateGuard(npc, player, agent))
                {
                    npc.UpdateNetworkGroup();
                    continue;
                }
                if (UpdateLooting(npc, agent))
                {
                    npc.UpdateNetworkGroup();
                    continue;
                }
                var work = GetGuardSettings(npc);
                if (work.Work == WorkGather)
                {
                    // Dedicated gatherer: works around its anchor, never follows.
                    if (!UpdateGathering(npc, agent, GetAnchor(work), GatherRadius)) HoldAtAnchor(npc, agent, work);
                    npc.UpdateNetworkGroup();
                    continue;
                }
                if (work.Work == WorkFollowGather && GetOwnerMotion(player).Speed < 0.5f
                    && (gatherTargets.ContainsKey(npc) || distance <= work.MaxDistance + 2f)
                    && UpdateGathering(npc, agent, player.transform.position, Mathf.Max(10f, work.MaxDistance + 4f)))
                {
                    // Owner is standing still: gather beside them until they move off.
                    npc.UpdateNetworkGroup();
                    continue;
                }
                StopGathering(npc);
                if (guards.ContainsKey(npc)) EnsureWeaponActive(npc as BasePlayer);
                if (distance <= Mathf.Max(work.MaxDistance, LookHoldDistance) && IsLookingAt(player, npc, LookHoldDistance))
                {
                    // Owner is looking at it (e.g. walking up to open its backpack):
                    // hold still and face them instead of backing off to the min distance.
                    walking.Remove(npc);
                    SetGait(npc, agent, false);
                    agent.isStopped = true;
                    FaceDirection(npc, player.transform.position - npc.transform.position, false, 90f);
                    npc.UpdateNetworkGroup();
                    continue;
                }
                var motion = GetOwnerMotion(player);
                var ownerMoving = motion.Speed > 0.5f;
                var wasWalking = walking.Contains(npc);
                var target = GetFollowTarget(player, npc, distance, wasWalking);
                var toTarget = target - npc.transform.position;
                toTarget.y = 0f;
                // How far the guard is from its own spot beside the owner.
                var gap = toTarget.magnitude;
                var band = GetGuardSettings(npc);
                var outOfBand = distance > band.MaxDistance || distance < band.MinDistance - 0.3f;
                // Standing on (or right next to) another guard always counts as out of
                // place, otherwise two stacked guards are each pushed apart by less than
                // the start threshold and never move.
                if (!outOfBand && IsCrowded(npc)) outOfBand = true;
                // Stop once at its spot. Leaving the distance band starts it at once;
                // inside the band it only moves to make clear room for another guard.
                if (wasWalking ? gap <= StopMoveDistance : !outOfBand && gap <= StartMoveDistance)
                {
                    walking.Remove(npc);
                    SetGait(npc, agent, false);
                    // Drop the path too, so the agent cannot resume an old route.
                    if (wasWalking) agent.ResetPath();
                    agent.isStopped = true;
                    agent.updateRotation = false;
                    FaceDirection(npc, player.transform.position - npc.transform.position, false, 90f);
                }
                else
                {
                    walking.Add(npc);
                    // Run to catch up (or when the owner runs), then drop back to a walk
                    // once close; never an in-between speed.
                    var run = (motion.Speed > OwnerRunningSpeed && gap > 1f)
                        || gap > (running.Contains(npc) ? RunStopDistance : RunStartDistance);
                    SetGait(npc, agent, run);
                    agent.updateRotation = false;
                    agent.isStopped = false;
                    // While the owner walks, lead the spot a little so the agent does not
                    // "arrive" and stutter; the gap check above decides where to stop.
                    var destination = ownerMoving ? target + motion.Heading * 1.5f : target;
                    if (!agent.SetDestination(destination)) agent.SetDestination(player.transform.position);
                    var heading = agent.desiredVelocityWS;
                    if (heading.sqrMagnitude < 0.01f) heading = destination - npc.transform.position;
                    // Turn to face the way it is going before/while stepping off, like a
                    // player would, instead of sliding sideways while slowly rotating.
                    FaceDirection(npc, heading, !wasWalking, 90f);
                }
                // No full SendNetworkUpdate here: 4 Hz snapshots made movement lurch.
                // Position/rotation stream from StreamPositions, animation via SendModelState.
                npc.UpdateNetworkGroup();
            }
            foreach (var npc in stopped) StopFollowing(npc);
        }

        private BaseEntity FindNearest(BasePlayer player, float range)
        {
            RemoveDestroyedReferences();
            BaseEntity nearest = null;
            var bestDistance = range * range;
            foreach (var npc in npcs)
            {
                var distance = (npc.transform.position - player.transform.position).sqrMagnitude;
                if (distance > bestDistance) continue;
                bestDistance = distance;
                nearest = npc;
            }
            return nearest;
        }

        private List<BaseEntity> FindNearby(BasePlayer player, float range)
        {
            RemoveDestroyedReferences();
            var result = new List<BaseEntity>();
            var maxDistance = range * range;
            foreach (var npc in npcs)
            {
                if (npc == null || npc.IsDestroyed) continue;
                if ((npc.transform.position - player.transform.position).sqrMagnitude <= maxDistance)
                    result.Add(npc);
            }
            return result;
        }

        private void RemoveDestroyedReferences()
        {
            npcs.RemoveAll(npc => npc == null || npc.IsDestroyed);
            foreach (var npc in new List<BaseEntity>(npcNumbers.Keys))
                if (npc == null || npc.IsDestroyed) ForgetNpc(npc);
        }

        private void Unload()
        {
            SaveNpcs();
            followers.Clear();
            formationSlots.Clear();
            guards.Clear();
            reviving.Clear();
            // One failing kill must not leave the rest behind as orphans.
            foreach (var npc in npcs)
            {
                if (npc == null || npc.IsDestroyed) continue;
                try { KillNpc(npc); }
                catch (System.Exception ex) { PrintWarning($"Could not remove NPC on unload: {ex.Message}"); }
            }
            npcs.Clear();
        }
    }
}
