using System.Text.Json;
using System.Text.Json.Serialization;

namespace Btd6Patches;

public sealed class PatchConfig
{
    public int ConfigVersion { get; set; } = 3;
    public bool EmptyIteratorReuse { get; set; } = true;
    public bool StrengthSelectorReuse { get; set; } = true;
    public bool CollisionGridInitializationFastPath { get; set; } = true;
    public bool LineageIntersectionFastPath { get; set; } = true;
    public bool TargetingComparerCache { get; set; } = true;
    public bool OtherBuffIteratorPooling { get; set; } = true;
    public bool DiscountBuffIteratorPooling { get; set; } = true;
    public bool AchievementFlagCountingFastPath { get; set; } = true;
    public bool DiscountZoneIteratorPooling { get; set; } = true;
    public bool AdaptiveLineageIndex { get; set; } = true;
    public bool BuffInterfaceDispatchCache { get; set; } = true;
    public bool ModelGeometryInitializationFastPath { get; set; } = true;
    public bool ProjectileFilterDispatchCache { get; set; } = true;
    public bool SimulationIteratorAccessFastPath { get; set; } = true;
    public bool SaveAllocationReduction { get; set; } = true;
    public bool TextPresentationCaching { get; set; } = true;
    public bool RoundEndScratchBuffers { get; set; } = true;
    public bool BuffRebuildReuse { get; set; } = true;
    public bool CollisionCallbackIteratorAccess { get; set; } = true;
    public bool RootObjectIteratorAccess { get; set; } = true;
    public bool WeaponBehaviorIteratorAccess { get; set; } = true;
    public bool ModelPresentationIteratorAccess { get; set; } = true;
    public bool TravelStraightVectorMath { get; set; } = true;
    public bool BuffBindingSecondChanceCache { get; set; } = true;
    public bool CollisionCandidateAccessorFastPath { get; set; } = true;
    public bool BuffEnumeratorDispatchCache { get; set; }
    public bool BuffMutatorBindingReuse { get; set; } = true;
    public bool InactiveBuffGeometryFastPath { get; set; } = true;
    public bool WarmScopedIteratorPools { get; set; } = true;
    public bool ZonedListItemAccess { get; set; } = true;
    public bool ZonedRunItemReuse { get; set; } = true;
    public bool UnfilteredIteratorAdvance { get; set; } = true;
    public bool TravelPositionPairReuse { get; set; } = true;
    public bool TravelMovementMathFusion { get; set; } = true;
    public bool TravelDirectionAccess { get; set; } = true;
    public bool TravelEntityPositionAccess { get; set; } = true;
    public bool WeaponAnimationIteratorAccess { get; set; } = true;
    public bool ModelDisplayConversion { get; set; } = true;
    public bool CollisionPositionAccessFastPath { get; set; } = true;
    public bool CollisionCheckerIteratorAccess { get; set; } = true;
    public bool CollisionCallbackDispatchCache { get; set; } = true;
    public bool RootUnHoldInline { get; set; } = true;
    public bool BuffMutationLookupFastPath { get; set; } = true;
    public bool CollisionGridPositionReuse { get; set; } = true;
    public bool CollisionCandidateTraversal { get; set; } = true;
    public bool WeaponRateCalculationReuse { get; set; } = true;
    public bool BuffFlagBatchCounting { get; set; } = true;
    public bool ModelRotationCalculationReuse { get; set; } = true;
    public bool ModelScaleCalculationFastPath { get; set; } = true;
    public bool ModelScaleCoordinateFastPath { get; set; } = true;
    public bool ProjectileTimerFastPaths { get; set; } = true;
    public bool ScopedBuffAllocationReduction { get; set; } = true;
    public string[] DisabledTargets { get; set; } = Array.Empty<string>();
    public bool ExperimentalIdleGarbageCollection { get; set; }

    [JsonPropertyName("ExperimentalParallelLineageSearch-Unused")]
    public bool ExperimentalParallelLineageSearch { get; set; }

    public int ExperimentalGcGrowthMiB { get; set; } = 64;
    public double ExperimentalGcCooldownSeconds { get; set; } = 60;
    public double ExperimentalGcIdleSeconds { get; set; } = 5;
    public long ExperimentalParallelMinPairs { get; set; } = 1048576;
    public int ExperimentalParallelWaitMs { get; set; } = 1;
    public bool ExperimentalRoundObjectRetirement { get; set; }
    public int ExperimentalRetirementPoolCapacity { get; set; } = 128;
    public int ExperimentalRetirementKeepPerClass { get; set; } = 2;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    internal bool IsEnabled(string target, bool enabled) =>
        enabled && !DisabledTargets.Contains(target, StringComparer.Ordinal);
}

internal static class PatchConfigStore
{
    private static readonly (string Name, string[] Targets)[] _legacyGroups =
    {
        ("EmptyIteratorAllocations", new[] { "Patch1", "Patch2", "Patch3", "Patch4" }),
        ("TargetingAllocations", new[] { "Patch5", "Patch8" }),
        ("BuffPipeline", new[] { "Patch9", "Patch12", "Patch13", "Patch14", "Patch22" }),
        ("CollisionCalculations", new[] { "Patch6", "Patch7", "Patch16", "Patch24" }),
        ("ModelPresentation", new[] { "Patch23" }),
        ("SimulationDispatch", new[] { "Patch25" })
    };

    private static readonly string[] _legacyIds = _legacyGroups.SelectMany(group => group.Targets).ToArray();

    public static PatchConfig Read(string json)
    {
        var config = JsonSerializer.Deserialize<PatchConfig>(json) ?? new PatchConfig();
        using var document = JsonDocument.Parse(json);
        var settings = document.RootElement;
        if (settings.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(Errors.PatchConfigurationMustBeAnObject);
        }

        if (config.DisabledTargets == null)
        {
            throw new JsonException(Errors.DisabledTargetsMustBeAnArray);
        }

        if (!settings.TryGetProperty("ExperimentalParallelLineageSearch-Unused", out _))
        {
            if (settings.TryGetProperty("ExperimentalParallelLineageSearch", out var oldParallel))
            {
                config.ExperimentalParallelLineageSearch = oldParallel.GetBoolean();
            }
            else if (settings.TryGetProperty("ExperimentalPatch11ParallelLineage", out var numericParallel))
            {
                config.ExperimentalParallelLineageSearch = numericParallel.GetBoolean();
            }
        }

        config.AdditionalSettings?.Remove("ExperimentalParallelLineageSearch");
        config.AdditionalSettings?.Remove("ExperimentalPatch11ParallelLineage");
        foreach (var id in _legacyIds)
        {
            config.AdditionalSettings?.Remove(id);
        }

        foreach (var (name, _)in _legacyGroups)
        {
            config.AdditionalSettings?.Remove(name);
        }

        if (settings.TryGetProperty("ConfigVersion", out var version) && version.GetInt32() >= 3)
        {
            return config;
        }

        var hasDisabledTargets = settings.TryGetProperty(nameof(PatchConfig.DisabledTargets), out _);
        if (!hasDisabledTargets)
        {
            config.DisabledTargets = new[]
            {
                "Patch12",
                "Patch14",
                "Patch16",
                "Patch22",
                "Patch23",
                "Patch24",
                "Patch25"
            };
        }

        var groupFlags = _legacyGroups.ToDictionary(group => group.Name, group => ReadLegacyFlag(group.Name, true),
            StringComparer.Ordinal);
        if (!settings.TryGetProperty("ExperimentalIdleGarbageCollection", out _) &&
            settings.TryGetProperty("ExperimentalPatch10IdleGc", out var legacyGc))
        {
            config.ExperimentalIdleGarbageCollection = legacyGc.GetBoolean();
        }

        var usesNumberedSettings = _legacyIds.Any(id => settings.TryGetProperty(id, out _)) ||
                                   (!settings.TryGetProperty("ConfigVersion", out _) &&
                                    !_legacyGroups.Any(group => settings.TryGetProperty(group.Name, out _)));
        var disabledTargets = config.DisabledTargets.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, bool>? numberedFlags = null;
        if (usesNumberedSettings)
        {
            if (!hasDisabledTargets)
            {
                disabledTargets.Clear();
            }

            numberedFlags = _legacyIds.ToDictionary(id => id,
                id => ReadLegacyFlag(id, id == "Patch13" ? ReadLegacyFlag("Patch9", true) : int.Parse(id[5..]) <= 9),
                StringComparer.Ordinal);
        }

        var enabledTargets = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (name, targets)in _legacyGroups)
        {
            var enabled = groupFlags[name];
            if (numberedFlags != null)
            {
                var hasEnabledTargets = targets.Any(id => numberedFlags[id]);
                var hasExplicitGroup = settings.TryGetProperty(name, out _);
                if (!hasExplicitGroup)
                {
                    enabled = hasEnabledTargets;
                }

                if (hasEnabledTargets || hasExplicitGroup)
                {
                    foreach (var id in targets)
                    {
                        if (!numberedFlags[id])
                        {
                            disabledTargets.Add(id);
                        }
                    }
                }
            }

            foreach (var id in targets)
            {
                enabledTargets[id] = enabled && !disabledTargets.Contains(id);
            }
        }

        if (usesNumberedSettings)
        {
            config.DisabledTargets = disabledTargets.OrderBy(id => Array.IndexOf(_legacyIds, id))
                .ThenBy(id => id, StringComparer.Ordinal).ToArray();
        }

        config.EmptyIteratorReuse = enabledTargets["Patch1"] || enabledTargets["Patch2"] || enabledTargets["Patch3"] ||
                                    enabledTargets["Patch4"];
        config.StrengthSelectorReuse = enabledTargets["Patch5"];
        config.CollisionGridInitializationFastPath = enabledTargets["Patch6"];
        config.LineageIntersectionFastPath = enabledTargets["Patch7"];
        config.TargetingComparerCache = enabledTargets["Patch8"];
        config.OtherBuffIteratorPooling = enabledTargets["Patch9"];
        config.DiscountBuffIteratorPooling = enabledTargets["Patch13"];
        config.AchievementFlagCountingFastPath = enabledTargets["Patch12"];
        config.DiscountZoneIteratorPooling = enabledTargets["Patch14"];
        config.AdaptiveLineageIndex = enabledTargets["Patch16"];
        config.BuffInterfaceDispatchCache = enabledTargets["Patch22"];
        config.ModelGeometryInitializationFastPath = enabledTargets["Patch23"];
        config.ProjectileFilterDispatchCache = enabledTargets["Patch24"];
        config.SimulationIteratorAccessFastPath = enabledTargets["Patch25"];
        // The four empty iterator factories still support separate opt-outs.
        config.DisabledTargets = config.DisabledTargets.Where(id =>
            !_legacyIds.Contains(id) ||
            (config.EmptyIteratorReuse && id is "Patch1" or "Patch2" or "Patch3" or "Patch4")).ToArray();
        config.AdditionalSettings?.Remove("ExperimentalPatch10IdleGc");
        config.ConfigVersion = 3;
        return config;

        bool ReadLegacyFlag(string key, bool fallback)
        {
            return settings.TryGetProperty(key, out var value) ? value.GetBoolean() : fallback;
        }
    }

    public static PatchConfig Load(string path, Action<string> report)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        if (!File.Exists(path))
        {
            var defaults = new PatchConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, options));
            return defaults;
        }

        var text = File.ReadAllText(path);
        var config = Read(text);
        using var existing = JsonDocument.Parse(text);
        if (existing.RootElement.TryGetProperty("ConfigVersion", out var version) && version.GetInt32() >= 3 &&
            !existing.RootElement.TryGetProperty("ExperimentalParallelLineageSearch", out _))
        {
            return config;
        }

        var backup = path + ".pre-groups-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak";
        File.Copy(path, backup);
        File.WriteAllText(path, JsonSerializer.Serialize(config, options));
        report("Migrated descriptive patch settings; previous configuration: " + backup);
        return config;
    }
}
