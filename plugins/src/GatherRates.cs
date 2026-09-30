using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("GatherRates", "LocalDev", "0.2.0")]
    [Description("Configurable rates for gathering and newly generated loot containers.")]
    public class GatherRates : RustPlugin
    {
        private GatherSettings settings;
        private readonly HashSet<LootContainer> pendingLoot = new HashSet<LootContainer>();

        private class GatherSettings
        {
            [JsonProperty(PropertyName = "石頭與樹木採集倍率")]
            public double DispenserMultiplier = 3.0;

            [JsonProperty(PropertyName = "地上資源拾取倍率")]
            public double CollectibleMultiplier = 3.0;

            [JsonProperty(PropertyName = "新生成寶箱可堆疊物品倍率")]
            public double LootMultiplier = 3.0;
        }

        protected override void LoadDefaultConfig()
        {
            settings = new GatherSettings();
            Config.WriteObject(settings, true);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                settings = Config.ReadObject<GatherSettings>() ?? new GatherSettings();
            }
            catch (Exception error)
            {
                PrintError("Invalid gather configuration: " + error.Message);
                settings = new GatherSettings();
            }

            settings.DispenserMultiplier = ClampRate(settings.DispenserMultiplier);
            settings.CollectibleMultiplier = ClampRate(settings.CollectibleMultiplier);
            settings.LootMultiplier = ClampRate(settings.LootMultiplier);
            Config.WriteObject(settings, true);
        }

        private static double ClampRate(double rate)
        {
            if (double.IsNaN(rate) || double.IsInfinity(rate)) return 3.0;
            return Math.Max(1.0, Math.Min(10.0, rate));
        }

        private static void Multiply(Item item, double rate)
        {
            if (item == null || item.amount <= 0 || rate <= 1.0) return;
            var amount = Math.Round(item.amount * rate, MidpointRounding.AwayFromZero);
            item.amount = (int)Math.Min(int.MaxValue, amount);
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            Multiply(item, settings.DispenserMultiplier);
        }

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            Multiply(item, settings.DispenserMultiplier);
        }

        private void OnCollectiblePickedup(CollectibleEntity collectible, BasePlayer player, Item item)
        {
            Multiply(item, settings.CollectibleMultiplier);
        }

        private void OnLootSpawn(LootContainer container)
        {
            if (container == null || !pendingLoot.Add(container)) return;
            timer.Once(0.1f, () =>
            {
                pendingLoot.Remove(container);
                if (container == null || container.IsDestroyed || container.inventory == null) return;
                foreach (var item in container.inventory.itemList)
                {
                    if (item == null || item.info == null || item.info.stackable <= 1 || item.IsBlueprint()) continue;
                    Multiply(item, settings.LootMultiplier);
                    item.MarkDirty();
                }
            });
        }
    }
}
