extern alias GameCorlib;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Simulation.Bloons;
using Il2CppAssets.Scripts.Simulation.Factory;
using Il2CppAssets.Scripts.Simulation.Objects;
using Il2CppAssets.Scripts.Simulation.Physics;
using Il2CppAssets.Scripts.Simulation.Towers;
using Il2CppAssets.Scripts.Simulation.Towers.Behaviors;
using Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Abilities;
using Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Behaviors;
using Il2CppAssets.Scripts.Simulation.Towers.Projectiles;
using Il2CppAssets.Scripts.Simulation.Towers.Weapons;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.Display;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Constants = Il2CppAssets.Scripts.Constants;

namespace Btd6Patches;

internal sealed class PatchSet
{
    private readonly MelonLogger.Instance _logger;
    private ExperimentalIdleGarbageCollectionPatch? _idleGc;
    private RoundObjectRetirementPatch? _retirement;

    public PatchSet(MelonLogger.Instance logger)
    {
        _logger = logger;
    }

    public void Apply(PatchConfig config, Func<string, Func<IDisposable>, bool> install)
    {
        var guard = new PatchGuard();
        _logger.Msg($"Verified game build profile: {guard.Build}.");
        if (guard.Build == GameBuild.Updated)
        {
            ApplyUpdated(guard, config, new PatchInstaller(install));
        }
        else
        {
            ApplyOriginal(guard, config, new PatchInstaller(install));
        }
    }

    private static void ApplyUpdated(PatchGuard guard, PatchConfig config, PatchInstaller install)
    {
        install.ApplyIf(config.CollisionCandidateTraversal, "Collision Candidate Traversal",
            () => CollisionTraversalPatch.Apply(guard));
        install.ApplyIf(config.WeaponRateCalculationReuse, "Weapon Rate Calculation Reuse",
            () => WeaponRateReusePatch.Apply(guard));
        install.ApplyIf(config.BuffFlagBatchCounting, "Buff Flag Batch Counting",
            () => BuffFlagCountingPatch.Apply(guard));
        install.ApplyIf(config.ModelRotationCalculationReuse, "Model Rotation Calculation Reuse",
            () => ModelRotationReusePatch.Apply(guard));
        install.ApplyIf(config.ModelScaleCalculationFastPath, "Model Scale Calculation Fast Path",
            () => ModelScaleCalculationPatch.Apply(guard));
        install.ApplyIf(config.ModelScaleCoordinateFastPath, "Model Scale Coordinate Fast Path",
            () => ModelScaleCoordinatePatch.Apply(guard));
        install.ApplyIf(config.ProjectileTimerFastPaths, "Projectile Timer Fast Paths",
            () => ProjectileTimersPatch.Apply(guard));
        install.ApplyIf(config.WeaponBehaviorIteratorAccess, "Weapon Behavior Iterator Access",
            () => CurrentIteratorAccessPatches.ApplyWeapon(guard));
        install.ApplyIf(config.RootObjectIteratorAccess, "Root Object Iterator Access",
            () => CurrentIteratorAccessPatches.ApplyRoot(guard));
        install.ApplyIf(config.CollisionCheckerIteratorAccess, "Collision Checker Iterator Access",
            () => CurrentIteratorAccessPatches.ApplyChecker(guard));
        install.ApplyIf(config.CollisionPositionAccessFastPath, "Collision Position Access Fast Path",
            () => CollisionPositionAccessPatch.Apply(guard));
        install.ApplyIf(config.TravelEntityPositionAccess, "Travel Entity Position Access",
            () => CurrentTravelPatches.ApplyPosition(guard, config.TravelPositionPairReuse));
        install.ApplyIf(config.TravelDirectionAccess, "Travel Direction Access",
            () => CurrentTravelPatches.ApplyDirection(guard));
        install.ApplyIf(config.TravelStraightVectorMath, "Travel Straight Vector Math",
            () => CurrentTravelPatches.ApplyMath(guard, config.TravelMovementMathFusion));
        install.ApplyIf(config.ZonedRunItemReuse, "Zoned Run Item Reuse",
            () => ZonedProcessingPatches.ApplyRunReuse(guard));
        install.ApplyIf(config.ZonedListItemAccess, "Zoned List Item Access",
            () => ZonedProcessingPatches.ApplyListAccess(guard));
        install.ApplyIf(config.UnfilteredIteratorAdvance, "Unfiltered Iterator Advance",
            () => IteratorAdvancePatch.Apply(guard));
        install.ApplyIf(config.ModelPresentationIteratorAccess, "Model Presentation Iterator Access",
            () => CurrentIteratorAccessPatches.ApplyModels(guard));
        install.ApplyIf(config.ModelDisplayConversion, "Model Display Conversion",
            () => CurrentModelProjectionPatch.Apply(guard));
        install.ApplyIf(config.ScopedBuffAllocationReduction, "Scoped Buff Allocation Reduction",
            () => ScopedBuffAllocationPatch.Apply(guard));
    }

    private void ApplyOriginal(PatchGuard guard, PatchConfig config, PatchInstaller install)
    {
        EmptyIteratorPatch.Apply(guard, config, install.Apply);
        install.ApplyIf(config.IsEnabled("Patch5", config.StrengthSelectorReuse), "Strength Selector Reuse",
            () => StrengthSelectorReusePatch.Apply(guard));
        install.ApplyIf(config.IsEnabled("Patch6", config.CollisionGridInitializationFastPath),
            "Collision Grid Initialization Fast Path", () => CollisionGridInitializationPatch.Apply(guard));
        LineageIntersectionPatch? lineage = null;
        install.ApplyIf(config.IsEnabled("Patch7", config.LineageIntersectionFastPath),
            "Lineage Intersection Fast Path", () => lineage = LineageIntersectionPatch.Apply(guard));
        install.ApplyIf(config.IsEnabled("Patch8", config.TargetingComparerCache), "Targeting Comparer Cache",
            () => TargetingComparerCachePatch.Apply(guard));
        var otherBuffs = config.IsEnabled("Patch9", config.OtherBuffIteratorPooling);
        var discountBuffs = config.IsEnabled("Patch13", config.DiscountBuffIteratorPooling);
        var buffDispatch = config.IsEnabled("Patch22", config.BuffInterfaceDispatchCache);
        var buffPoolActive = (otherBuffs || discountBuffs || buffDispatch) && install.Apply(
            "Buff Iterator Pooling and Interface Dispatch",
            () => BuffIteratorPoolingPatch.Apply(guard, otherBuffs, discountBuffs, buffDispatch));
        _logger.Msg(
            $"Other Buff Iterator Pooling={buffPoolActive && otherBuffs}; Discount Buff Iterator Pooling={buffPoolActive && discountBuffs}.");
        _logger.Msg($"Buff Interface Dispatch Cache={buffPoolActive && buffDispatch}.");
        install.ApplyIf(config.WarmScopedIteratorPools, "Warm Scoped Iterator Pools",
            () => WarmScopedIteratorPoolsPatch.Apply(buffPoolActive && (otherBuffs || discountBuffs)));
        var idleGcActive = config.ExperimentalIdleGarbageCollection && install.Apply(
            "Idle Garbage Collection (Experimental)",
            () => _idleGc = ExperimentalIdleGarbageCollectionPatch.Apply(config, _logger));
        install.ApplyIf(config.IsEnabled("Patch12", config.AchievementFlagCountingFastPath),
            "Achievement Flag Counting Fast Path", () => AchievementFlagCountingPatch.Apply(guard));
        install.ApplyIf(config.IsEnabled("Patch14", config.DiscountZoneIteratorPooling),
            "Discount Zone Iterator Pooling",
            () => BuffIteratorPoolingPatch.ApplyNestedZones(guard, buffPoolActive && discountBuffs));
        if (config.IsEnabled("Patch16", config.AdaptiveLineageIndex))
        {
            EnableLineageIndex(lineage);
        }

        install.ApplyIf(config.IsEnabled("Patch23", config.ModelGeometryInitializationFastPath),
            "Model Geometry Initialization Fast Path", () => ModelGeometryInitializationPatch.Apply(guard));
        install.ApplyIf(config.IsEnabled("Patch24", config.ProjectileFilterDispatchCache),
            "Projectile Filter Dispatch Cache", () => InterfaceDispatchPatch.ApplyProjectileFilter(guard));
        install.ApplyIf(config.IsEnabled("Patch25", config.SimulationIteratorAccessFastPath),
            "Simulation Iterator Access Fast Path", () => SimulationIteratorAccessPatch.Apply(guard));
        install.ApplyIf(config.SaveAllocationReduction, "Save Allocation Reduction",
            () => SaveAllocationReductionPatch.Apply(guard));
        install.ApplyIf(config.TextPresentationCaching, "Text Presentation Caching",
            () => TextPresentationCachingPatch.Apply(guard));
        install.ApplyIf(config.RoundEndScratchBuffers, "Round-end Scratch Buffers",
            () => RoundEndScratchBuffersPatch.Apply(guard));
        var buffReuseActive = config.BuffRebuildReuse &&
                              install.Apply("Buff Rebuild Reuse", () => BuffRebuildReusePatch.Apply(guard));
        install.ApplyIf(config.BuffBindingSecondChanceCache, "Buff Binding Second Chance Cache",
            () => BuffBindingSecondChancePatch.Apply(buffReuseActive));
        install.ApplyIf(config.CollisionCallbackIteratorAccess, "Collision Callback Iterator Access",
            () => HotLoopIteratorAccessPatches.ApplyCollision(guard));
        install.ApplyIf(config.WeaponBehaviorIteratorAccess, "Weapon Behavior Iterator Access",
            () => HotLoopIteratorAccessPatches.ApplyWeapon(guard));
        install.ApplyIf(config.WeaponAnimationIteratorAccess, "Weapon Animation Iterator Access",
            () => HotLoopIteratorAccessPatches.ApplyWeaponAnimation(guard));
        install.ApplyIf(config.ModelPresentationIteratorAccess, "Model Presentation Iterator Access",
            () => HotLoopIteratorAccessPatches.ApplyModels(guard));
        install.ApplyIf(config.TravelStraightVectorMath, "Travel Straight Vector Math",
            () => TravelStraightVectorMathPatch.Apply(guard));
        install.ApplyIf(config.CollisionGridPositionReuse, "Collision Grid Position Reuse",
            () => CollisionGridPositionPatch.Apply(guard));
        install.ApplyIf(config.BuffMutationLookupFastPath, "Buff Mutation Lookup Fast Path",
            () => BuffMutationLookupPatch.Apply(guard));
        install.ApplyIf(config.RootUnHoldInline, "Root UnHold Inline", () => RootUnHoldInlinePatch.Apply(guard));
        install.ApplyIf(config.CollisionCallbackDispatchCache, "Collision Callback Dispatch Cache",
            () => CollisionCallbackDispatchPatch.Apply(guard));
        install.ApplyIf(config.CollisionCheckerIteratorAccess, "Collision Checker Iterator Access",
            () => HotLoopIteratorAccessPatches.ApplyChecker(guard));
        install.ApplyIf(config.ModelDisplayConversion, "Model Display Conversion",
            () => ModelDisplayConversionPatch.Apply(guard));
        install.ApplyIf(config.TravelEntityPositionAccess, "Travel Entity Position Access",
            () => TravelEntityPositionPatch.Apply(guard));
        install.ApplyIf(config.CollisionCandidateAccessorFastPath, "Collision Candidate Accessor Fast Path",
            () => CandidateDispatchPatches.ApplyCollision(guard));
        install.ApplyIf(config.BuffEnumeratorDispatchCache, "Buff Enumerator Dispatch Cache",
            () => CandidateDispatchPatches.ApplyBuffs(guard));
        install.ApplyIf(config.BuffMutatorBindingReuse, "Buff Mutator Binding Reuse",
            () => BuffMutatorBindingPatch.Apply(guard, buffReuseActive));
        install.ApplyIf(config.InactiveBuffGeometryFastPath, "Inactive Buff Geometry Fast Path",
            () => InactiveBuffGeometryPatch.Apply(guard));
        install.ApplyIf(config.ExperimentalRoundObjectRetirement, "Round Object Retirement (Experimental)",
            () => _retirement = RoundObjectRetirementPatch.Apply(guard, config,
                buffPoolActive && (otherBuffs || discountBuffs), _logger));
        _logger.Msg($"Patch setup complete. Experimental idle GC={idleGcActive}. Automatic GC unchanged.");
    }

    private void EnableLineageIndex(LineageIntersectionPatch? lineage)
    {
        try
        {
            if (lineage == null)
            {
                throw new InvalidOperationException(Errors.IndexedSearchRequiresActiveLineageIntersection);
            }

            lineage.EnableIndexedSearch();
            _logger.Msg("Adaptive Lineage Index: enabled.");
        }
        catch (Exception exception)
        {
            _logger.Warning(Errors.AdaptiveLineageIndexSkipped + exception.Message);
        }
    }

    public void Tick()
    {
        if (_retirement == null && _idleGc == null)
        {
            return;
        }

        try
        {
            _retirement?.Tick();
        }
        catch (Exception exception)
        {
            _retirement?.Dispose();
            _retirement = null;
            _logger.Warning(Errors.RoundObjectRetirementDisabledAfterError + exception.Message);
        }

        try
        {
            _idleGc?.Tick();
        }
        catch (Exception exception)
        {
            _idleGc?.Dispose();
            _idleGc = null;
            _logger.Warning(string.Format(Errors.IdleGarbageCollectionDisabledAfterError, exception.Message));
        }
    }

    private readonly struct PatchInstaller
    {
        private readonly Func<string, Func<IDisposable>, bool> _install;

        public PatchInstaller(Func<string, Func<IDisposable>, bool> install)
        {
            _install = install;
        }

        public bool Apply(string name, Func<IDisposable> create) => _install(name, create);

        public void ApplyIf(bool enabled, string name, Func<IDisposable> create)
        {
            if (enabled)
            {
                _install(name, create);
            }
        }
    }
}

public static class EmptyIteratorPatch
{
    public static void Apply(PatchGuard guard, PatchConfig config, Func<string, Func<IDisposable>, bool> install)
    {
        if (config.IsEnabled("Patch1", config.EmptyIteratorReuse))
        {
            install("Empty Iterator Reuse / Tower Buffs",
                () => NativeEmptySequencePatch.Apply<BuffQuery>(guard, "Patch1",
                    FindDeclaredMethod(typeof(TowerBehavior), "GetBuffs")));
        }

        if (config.IsEnabled("Patch2", config.EmptyIteratorReuse))
        {
            install("Empty Iterator Reuse / Weapon Buffs",
                () => NativeEmptySequencePatch.Apply<BuffQuery>(guard, "Patch2",
                    FindDeclaredMethod(typeof(WeaponBehavior), "GetBuffs")));
        }

        if (config.IsEnabled("Patch3", config.EmptyIteratorReuse))
        {
            install("Empty Iterator Reuse / Ability Buffs",
                () => NativeEmptySequencePatch.Apply<BuffQuery>(guard, "Patch3",
                    FindDeclaredMethod(typeof(AbilityBehavior), "GetBuffs")));
        }

        if (config.IsEnabled("Patch4", config.EmptyIteratorReuse))
        {
            install("Empty Iterator Reuse / Targets",
                () => NativeEmptySequencePatch.Apply<Target>(guard, "Patch4",
                    FindDeclaredMethod(typeof(TargetSupplier), "GetTargets")));
        }
    }

    private static MethodInfo FindDeclaredMethod(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
}

public sealed class RoundEndScratchBuffersPatch : IDisposable
{
    private static readonly List<RoundEndScratchBuffersPatch> _activeHooks = new();
    private readonly NativeHook<RoundEndCallback> _hook;
    private readonly IDisposable _sites;
    private bool _disposed;

    private RoundEndScratchBuffersPatch(nint target, IDisposable sites)
    {
        _sites = sites;
        _hook = new NativeHook<RoundEndCallback>(target, NativeSupport.Export("ScopedRoundEnd"));
        _activeHooks.Add(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _hook.Detach();
        if (_hook.IsHooked)
        {
            throw new InvalidOperationException(Errors.RoundEndScopeStillAttached);
        }

        _sites.Dispose();
        _disposed = true;
        _activeHooks.Remove(this);
    }

    public static IDisposable Apply(PatchGuard guard)
    {
        var method = typeof(Simulation).GetMethod("RoundEnd",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        var target = guard.VerifyRegular("RoundEndScratchBuffers", method);
        var sites = GuardedCallSites.Apply(guard, "Btd6Patches.round-contract.json", () => ConfigureNative(guard));
        var result = new RoundEndScratchBuffersPatch(target, sites);
        try
        {
            result._hook.Attach();
            if (!result._hook.IsHooked)
            {
                throw new InvalidOperationException(Errors.RoundEndScopeHookFailed);
            }

            NativeSupport.GetDelegate<SetOriginalRoundEnd>("SetRoundEndOriginal")(result._hook.TrampolineHandle);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void ConfigureNative(PatchGuard guard)
    {
        nint game = NativeLibrary.Load("GameAssembly.dll");
        var getArrayClass =
            Marshal.GetDelegateForFunctionPointer<GetArrayClass>(
                NativeLibrary.GetExport(game, "il2cpp_array_class_get"));
        var config = new RoundBuffersConfig
        {
            ArrayNew = guard.Address(0x384B10, 1),
            EnumerableToArray = guard.Address(0x16E7C30, 1),
            ListToArray = guard.Address(0x196C930, 1),
            BufferCtor = guard.Address(0x21C7260, 1),
            TowerArray = getArrayClass(Il2CppClassPointerStore.GetNativeClassPointer(typeof(Tower)), 1),
            BehaviorArray = getArrayClass(Il2CppClassPointerStore.GetNativeClassPointer(typeof(RootBehavior)), 1),
            TowerEnumerable =
                Il2CppClassPointerStore.GetNativeClassPointer(typeof(Factory<Tower>.CheckDestroyedEnumerable))
        };
        if (config.TowerArray == 0 || config.BehaviorArray == 0 || config.TowerEnumerable == 0)
        {
            throw new InvalidOperationException(Errors.SnapshotArrayEnumerableTypesUnavailable);
        }

        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureRoundBuffers")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RoundBuffersConfig
    {
        public nint ArrayNew;
        public nint EnumerableToArray;
        public nint ListToArray;
        public nint BufferCtor;
        public nint TowerArray;
        public nint BehaviorArray;
        public nint TowerEnumerable;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref RoundBuffersConfig config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOriginalRoundEnd(nint pointer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RoundEndCallback(nint simulation, int round, int next, nint info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetArrayClass(nint nativeClass, uint rank);
}

public static class SaveAllocationReductionPatch
{
    public static IDisposable Apply(PatchGuard guard) =>
        GuardedCallSites.Apply(guard, "Btd6Patches.save-contract.json", () => ConfigureNative(guard));

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new SaveBuffersConfig
        {
            Rent = guard.Address(0x2A37900, 1),
            Return = guard.Address(0x2A37980, 1)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureSaveBuffers")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SaveBuffersConfig
    {
        public nint Rent;
        public nint Return;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref SaveBuffersConfig config);
}

public sealed class WarmScopedIteratorPoolsPatch : IDisposable
{
    private readonly SetEnabled _setEnabled;

    private WarmScopedIteratorPoolsPatch(SetEnabled setEnabled)
    {
        _setEnabled = setEnabled;
        setEnabled(1);
    }

    public void Dispose() => _setEnabled(0);

    public static IDisposable Apply(bool poolingActive)
    {
        return poolingActive
            ? new WarmScopedIteratorPoolsPatch(NativeSupport.GetDelegate<SetEnabled>("ConfigureWarmScopedPools"))
            : throw new InvalidOperationException(Errors.RequiresActiveGuardedScopedBuffIteratorPooling);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetEnabled(int enabled);
}

public static class ScopedBuffAllocationPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        return GuardedCallSites.Apply(guard, "Btd6Patches.buff-allocation-current-contract.json",
            () => ConfigureNative(guard));
    }

    private static void ConfigureNative(PatchGuard guard)
    {
        var iteratorTypes = typeof(Tower).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        var config = new BuffAllocationConfig
        {
            OtherClass = FindIteratorClass(iteratorTypes, "_AddOtherBuffs_d__183"),
            DiscountClass = FindIteratorClass(iteratorTypes, "_AddDiscountZoneBuffs_d__182"),
            AddRange = guard.Address(0x1bd12a0, 1)
        };
        if (config.OtherClass == 0 || config.DiscountClass == 0)
        {
            throw new InvalidOperationException(Errors.CurrentScopedIteratorClassesUnavailable);
        }

        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureCurrentBuffAllocation")(ref config);
    }

    private static nint FindIteratorClass(Type[] iteratorTypes, string name) =>
        Il2CppClassPointerStore.GetNativeClassPointer(iteratorTypes.Single(t => t.Name == name));

    [StructLayout(LayoutKind.Sequential)]
    private struct BuffAllocationConfig
    {
        public nint OtherClass;
        public nint DiscountClass;
        public nint AddRange;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref BuffAllocationConfig config);
}

public static class BuffIteratorPoolingPatch
{
    public static IDisposable Apply(PatchGuard guard, bool poolOtherBuffs = true, bool poolDiscountBuffs = true,
        bool cacheInterfaceDispatch = false)
    {
        var method = typeof(Tower).GetMethod("UpdateBuffs",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var address = guard.VerifyRegular("Patch9", method);
        var iterator = typeof(Tower).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Single(t => t.Name == "_AddOtherBuffs_d__181");
        var moveNextAddress = guard.VerifyRegular("Patch9.BoxSites", iterator.GetMethod("MoveNext")!);
        NativeSupport.Initialize(guard);
        if (cacheInterfaceDispatch)
        {
            NativeSupport.InitializeDispatch(guard);
        }

        var original = Convert.FromHexString(guard.GetEntry("Patch9").Body);
        var originalMoveNext = Convert.FromHexString(guard.GetEntry("Patch9.BoxSites").Body);
        return NativePatchGroup.Apply(group =>
        {
            var acquire = group.Own(new NativeCallStub(address, NativeSupport.Export("AcquireBuffIterator")));
            var consume = group.Own(new NativeCallStub(address, NativeSupport.Export("ConsumeBuffs")));
            var box = group.Own(new NativeCallStub(moveNextAddress, NativeSupport.Export("BuffLockEnumerator"), true));
            var replacement = NativeSimulationCode.BuffConsumer(original, address, acquire.Address, consume.Address,
                poolOtherBuffs, poolDiscountBuffs);
            if (cacheInterfaceDispatch)
            {
                var resolve = group.Own(new NativeCallStub(address, NativeSupport.Export("ResolveInterfaceFast")));
                replacement = InterfaceDispatchPatch.ContributeBuffSites(replacement, address, resolve.Address);
            }

            if (poolOtherBuffs)
            {
                group.Own(new NativeInlinePatch(moveNextAddress, originalMoveNext,
                    NativeSimulationCode.BuffEnumerator(originalMoveNext, moveNextAddress, box.Address), null));
            }

            group.Own(new NativeInlinePatch(address, original, replacement, null));
        });
    }

    public static IDisposable ApplyNestedZones(PatchGuard guard, bool discountPoolActive)
    {
        if (!discountPoolActive)
        {
            throw new InvalidOperationException(Errors.DiscountConsumerRequired);
        }

        var factory = typeof(Tower).GetMethod("FindDiscountZones",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var iterator = typeof(Tower).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Single(t => t.Name == "_AddDiscountZoneBuffs_d__180");
        var address = guard.VerifyRegular("Patch14.Factory", factory);
        var moveNextAddress = guard.VerifyRegular("Patch14.Consumer",
            iterator.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!);
        NativeSupport.Initialize(guard);
        NativeSupport.InitializeExtended(guard);
        var original = Convert.FromHexString(guard.GetEntry("Patch14.Factory").Body);
        var consumer = Convert.FromHexString(guard.GetEntry("Patch14.Consumer").Body);
        return NativePatchGroup.Apply(group =>
        {
            var acquire = group.Own(new NativeCallStub(address, NativeSupport.Export("AcquireZoneIterator")));
            var scoped =
                group.Own(new NativeCallStub(moveNextAddress, NativeSupport.Export("ScopedFindDiscountZones")));
            var replacementCode = (byte[])original.Clone();
            NativeCallStub.WriteCall(replacementCode, 0x37, address, acquire.Address);
            var replacementMoveNext = (byte[])consumer.Clone();
            NativeCallStub.WriteCall(replacementMoveNext, 0x194, moveNextAddress, scoped.Address);
            group.Own(new NativeInlinePatch(address, original, replacementCode, null));
            group.Own(new NativeInlinePatch(moveNextAddress, consumer, replacementMoveNext, null));
        });
    }
}

public sealed class BuffBindingSecondChancePatch : IDisposable
{
    private readonly SetEnabled _setEnabled;

    private BuffBindingSecondChancePatch(SetEnabled setEnabled)
    {
        _setEnabled = setEnabled;
        setEnabled(1);
    }

    public void Dispose() => _setEnabled(0);

    public static IDisposable Apply(bool buffReuseActive)
    {
        return buffReuseActive
            ? new BuffBindingSecondChancePatch(NativeSupport.GetDelegate<SetEnabled>("ConfigureBindingSecondChance"))
            : throw new InvalidOperationException(Errors.RequiresActiveGuardedBuffRebuildReuse);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetEnabled(int enabled);
}

public static class BuffMutatorBindingPatch
{
    public static IDisposable Apply(PatchGuard guard, bool buffReuseActive)
    {
        if (!buffReuseActive)
        {
            throw new InvalidOperationException(Errors.RequiresGuardedBuffRebuildReuse);
        }

        return GuardedCallSites.Apply(guard, "Btd6Patches.buff-mutator-contract.json", () =>
        {
        });
    }
}

public sealed class BuffRebuildReusePatch : IDisposable
{
    private static readonly List<BuffRebuildReusePatch> _activeHooks = new();
    private readonly NativeHook<PlacementQuery> _hook;
    private readonly IDisposable _sites;
    private bool _disposed;

    private BuffRebuildReusePatch(nint target, IDisposable sites)
    {
        _sites = sites;
        _hook = new NativeHook<PlacementQuery>(target, NativeSupport.Export("ReusedBananaPlacementQuery"));
        _activeHooks.Add(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _hook.Detach();
        if (_hook.IsHooked)
        {
            throw new InvalidOperationException(Errors.BuffReuseHookStillAttached);
        }

        _sites.Dispose();
        _disposed = true;
        _activeHooks.Remove(this);
    }

    public static IDisposable Apply(PatchGuard guard)
    {
        var method = typeof(BananaCentralBuff).GetMethod("TestPlacementTowerBuff",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!;
        var target = guard.VerifyRegular("BuffRebuildReuse", method);
        var sites = GuardedCallSites.Apply(guard, "Btd6Patches.buff-contract.json", () => ConfigureNative(guard));
        var result = new BuffRebuildReusePatch(target, sites);
        try
        {
            result._hook.Attach();
            if (!result._hook.IsHooked)
            {
                throw new InvalidOperationException(Errors.BananaCentralBuffReuseHookFailed);
            }

            NativeSupport.GetDelegate<SetOriginalQuery>("SetBananaQueryOriginal")(result._hook.TrampolineHandle);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new BuffReuseConfig
        {
            BananaClass = Il2CppClassPointerStore.GetNativeClassPointer(typeof(BananaCentralBuff)),
            GetChild = guard.Address(0x175E5F0, 1),
            IsInstance = guard.Address(0x384AB0, 1)
        };
        if (config.BananaClass == 0)
        {
            throw new InvalidOperationException(Errors.BananaCentralNativeTypeUnavailable);
        }

        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureBuffReuse")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BuffReuseConfig
    {
        public nint BananaClass;
        public nint GetChild;
        public nint IsInstance;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref BuffReuseConfig config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOriginalQuery(nint pointer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PlacementQuery(nint output, nint behavior, nint model, nint position, int owner, nint info);
}

public static class BuffMutationLookupPatch
{
    public static IDisposable Apply(PatchGuard guard) => GuardedCallSites.Apply(guard,
        "Btd6Patches.buff-mutation-contract.json", () => ConfigureNative(guard));

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new BuffLookupConfig
        {
            Original = guard.Address(0xDABE30, 1),
            Initialized = guard.Address(0x4CC009A, 1),
            InfoSlot = guard.Address(0x49E5190, 8),
            Hash = guard.Address(0x26F6DE0, 1),
            Equality = guard.Address(0x26EE140, 1)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureBuffMutation")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BuffLookupConfig
    {
        public nint Original;
        public nint Initialized;
        public nint InfoSlot;
        public nint Hash;
        public nint Equality;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref BuffLookupConfig config);
}

public static class InactiveBuffGeometryPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        var contract = NativeContracts.Read<InactiveBuffContract>("Btd6Patches.inactive-buff-contract.json");
        contract.Verify(guard.ReadBytes);
        var original = Convert.FromHexString(contract.Body);
        var address = guard.Address(contract.Start, original.Length);
        NativeSupport.Initialize(guard);
        var config = new BuffGeometryConfig
        {
            Position = guard.Address(0x21FA140, 1),
            Vector2 = guard.Address(0xD71BF0, 1),
            FalseIcon = guard.Address(0x482A30, 1),
            PositionInitialized = guard.Address(0x4CC7CE2, 1),
            VectorInitialized = guard.Address(0x4CBFF03, 1),
            VectorClassSlot = guard.Address(0x4A07398, 8),
            NullReference = guard.Address(0x385970, 1)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureInactiveBuff")(ref config);
        return NativePatchGroup.Apply(group =>
        {
            var stub = group.Own(new NativeCallStub(address, NativeSupport.Export("InactiveBuffCheck"),
                argumentSetup: InactiveBuffCode.ArgumentSetup));
            var replacement = InactiveBuffCode.Create(original.Length, address, stub.Address);
            group.Own(new NativeInlinePatch(address, original, replacement, null));
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BuffGeometryConfig
    {
        public nint Position;
        public nint Vector2;
        public nint FalseIcon;
        public nint PositionInitialized;
        public nint VectorInitialized;
        public nint VectorClassSlot;
        public nint NullReference;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref BuffGeometryConfig config);
}

public static class AchievementFlagCountingPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        var method = typeof(UnityToSimulation).GetMethod("CalculateTotalBuffs",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var address = guard.VerifyRegular("Patch12", method);
        NativeSupport.Initialize(guard);
        NativeSupport.InitializeExtended(guard);
        var original = Convert.FromHexString(guard.GetEntry("Patch12").Body);
        return NativePatchGroup.Apply(group =>
        {
            var stub = group.Own(new NativeCallStub(address, NativeSupport.Export("CountAchievementBuffs")));
            group.Own(new NativeInlinePatch(address, original,
                NativeSimulationCode.AchievementCount(original, address, stub.Address), null));
        });
    }
}

public static class BuffFlagCountingPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        return GuardedNativeBlock.Apply(guard, "Btd6Patches.buff-count-current-contract.json",
            () => ConfigureNative(guard));
    }

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new BuffCountingConfig
        {
            GetEnumerator = guard.Address(0x1bc5a60, 1),
            MoveNext = guard.Address(0x27cc2a0, 1),
            GetInfoSlot = guard.Address(0x4f72670, 8),
            MoveInfoSlot = guard.Address(0x4f8dc38, 8)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureBuffCountBatch")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BuffCountingConfig
    {
        public nint GetEnumerator;
        public nint MoveNext;
        public nint GetInfoSlot;
        public nint MoveInfoSlot;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref BuffCountingConfig config);
}

public static class CollisionCallbackDispatchPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        var contract =
            NativeContracts.Read<NativeWindowContract>("Btd6Patches.collision-callback-dispatch-contract.json");
        if (contract.Start != CollisionCallbackDispatchCode.Start)
        {
            throw new InvalidOperationException(Errors.CallbackDispatchStartChanged);
        }

        var original = Convert.FromHexString(contract.Body);
        var address = guard.Address(contract.Start, original.Length);
        var actual = new byte[original.Length];
        Marshal.Copy(address, actual, 0, actual.Length);
        if (!actual.AsSpan().SequenceEqual(original))
        {
            throw new InvalidOperationException(Errors.CallbackDispatchWindowChanged);
        }

        NativeSupport.InitializeDispatch(guard);
        return NativePatchGroup.Apply(group =>
        {
            var helper = group.Own(new NativeCallStub(address, NativeSupport.Export("CollisionCallbackResolveFast")));
            group.Own(new NativeInlinePatch(address, original,
                CollisionCallbackDispatchCode.Create(original, address, helper.Address), null));
        });
    }
}

public static class CollisionGridInitializationPatch
{
    public static NativeInlinePatch Apply(PatchGuard guard)
    {
        var method = typeof(CollisionGrid<Bloon>.CollisionState).GetMethod("MoveNext",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var entry = guard.GetEntry("Patch6");
        if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters ||
            method.ReturnType != typeof(bool) || method.GetParameters().Length != 0 ||
            method.DeclaringType!.GetGenericTypeDefinition().FullName!.Replace('+', '/') != entry.Type)
        {
            throw new InvalidOperationException(Errors.UnexpectedCollisionIteratorSignature);
        }

        var address = guard.VerifyNativeMember(method, entry.Rva, entry.Body);
        nint constants = Il2CppClassPointerStore.GetNativeClassPointer(typeof(Constants));
        if (constants == 0)
        {
            throw new InvalidOperationException(Errors.ConstantsNativeClassUnavailable);
        }

        IL2CPP.il2cpp_runtime_class_init(constants);
        // Class initialization must resolve this slot to the same Constants type.
        if (constants == 0 || Marshal.ReadIntPtr(guard.Address(0x49CC8C0, 8)) != constants ||
            Marshal.ReadInt32(constants, 0xE4) == 0)
        {
            throw new InvalidOperationException(Errors.ConstantsInitializationNotProven);
        }

        var original = Convert.FromHexString(entry.Body);
        if (!entry.InitializedBranchOffsets.SequenceEqual(new[] { 0x18B, 0x1CB, 0x207, 0x243 }) ||
            !entry.InitializationCompareOffsets.SequenceEqual(new[] { 0x184, 0x1B2, 0x1EE, 0x22A }))
        {
            throw new InvalidOperationException(Errors.UnexpectedCollisionInitializationGates);
        }

        return new NativeInlinePatch(address, original,
            NativeInitializedBranchCode.Create(original, entry.InitializationCompareOffsets,
                entry.InitializedBranchOffsets), null);
    }
}

public static class CollisionGridPositionPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        var contract = NativeContracts.Read<GridPositionContract>("Btd6Patches.grid-position-contract.json");
        contract.Verify(guard.ReadBytes);
        NativeSupport.Initialize(guard);
        var config = new GridPositionConfig
        {
            Getter = guard.Address(0x21FA140, 1),
            Initialized = guard.Address(0x4CC7CE2, 1),
            X = guard.Address(0xD71EB0, 1),
            Y = guard.Address(0x6FD470, 1)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureGridPosition")(ref config);
        return NativePatchGroup.Apply(group =>
        {
            foreach (var block in contract.Blocks)
            {
                var original = Convert.FromHexString(block.Body);
                var address = guard.Address(block.Start, original.Length);
                var stub = group.Own(new NativeCallStub(address, NativeSupport.Export("CollisionGridPositionPair"),
                    argumentSetup: Convert.FromHexString("4889F9")));
                group.Own(
                    new NativeInlinePatch(address, original, GridPositionCode.Create(address, stub.Address), null));
            }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GridPositionConfig
    {
        public nint Getter;
        public nint Initialized;
        public nint X;
        public nint Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref GridPositionConfig config);
}

public static class CandidateDispatchPatches
{
    public static IDisposable ApplyCollision(PatchGuard guard) => Apply(guard, "collision-candidate");
    public static IDisposable ApplyBuffs(PatchGuard guard) => Apply(guard, "buff-enumerator");

    private static IDisposable Apply(PatchGuard guard, string name) =>
        GuardedCallSites.Apply(guard, $"Btd6Patches.{name}-contract.json", () => ConfigureNative(guard));

    private static void ConfigureNative(PatchGuard guard)
    {
        NativeSupport.InitializeDispatch(guard);
        var config = new CandidateDispatchConfig
        {
            PointerInvoker = guard.Address(0x3E50, 1),
            FloatInvoker = guard.Address(0x16930, 1),
            TransformGetter = guard.Address(0x62BD20, 1),
            RadiusGetter = guard.Address(0x21F8170, 1),
            EmptyCollisionsGetter = guard.Address(0x46F7F0, 1),
            BloonClass = Il2CppClassPointerStore.GetNativeClassPointer(typeof(Bloon)),
            CommonInterface = Il2CppClassPointerStore.GetNativeClassPointer(typeof(ICommonBehaviorProxy)),
            CollidableInterface = Il2CppClassPointerStore.GetNativeClassPointer(typeof(ICollidable))
        };
        if (config.BloonClass == 0 || config.CommonInterface == 0 || config.CollidableInterface == 0)
        {
            throw new InvalidOperationException(Errors.CollisionAccessorTypesUnavailable);
        }

        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureCandidateDispatch")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateDispatchConfig
    {
        public nint PointerInvoker;
        public nint FloatInvoker;
        public nint TransformGetter;
        public nint RadiusGetter;
        public nint EmptyCollisionsGetter;
        public nint BloonClass;
        public nint CommonInterface;
        public nint CollidableInterface;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref CandidateDispatchConfig config);
}

public static class CollisionTraversalPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        return GuardedCallSites.Apply(guard, "Btd6Patches.collision-traversal-current-contract.json",
            () => ConfigureNative(guard));
    }

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new CollisionTraversalConfig
        {
            Original = guard.Address(0x24b13b0, 1249),
            Initialized = guard.Address(0x523dea8, 1),
            CellClassSlot = guard.Address(0x4f35f88, 8),
            BloonClass = Il2CppClassPointerStore.GetNativeClassPointer(typeof(Bloon)),
            CollidableInterface = Il2CppClassPointerStore.GetNativeClassPointer(typeof(ICollidable)),
            CommonInterface = Il2CppClassPointerStore.GetNativeClassPointer(typeof(ICommonBehaviorProxy)),
            RadiusGetter = guard.Address(0x24b0150, 9),
            TransformGetter = guard.Address(0x526e50, 8),
            EmptyGroupsGetter = guard.Address(0x4fa090, 3),
            WriteBarrier = guard.Address(0x3f17b0, 5)
        };
        if (config.BloonClass == 0 || config.CollidableInterface == 0 || config.CommonInterface == 0)
        {
            throw new InvalidOperationException(Errors.CurrentCollisionTypesUnavailable);
        }

        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureCollisionTraversal")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CollisionTraversalConfig
    {
        public nint Original;
        public nint Initialized;
        public nint CellClassSlot;
        public nint BloonClass;
        public nint CollidableInterface;
        public nint CommonInterface;
        public nint RadiusGetter;
        public nint TransformGetter;
        public nint EmptyGroupsGetter;
        public nint WriteBarrier;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref CollisionTraversalConfig config);
}

public static class ModelScaleCalculationPatch
{
    public static IDisposable Apply(PatchGuard guard) => GuardedCallSites.Apply(guard,
        "Btd6Patches.model-scale-current-contract.json", () => ConfigureNative(guard));

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new ModelScaleConfig
        {
            Original = guard.Address(0x1026670, 0xDF),
            MetadataInitialized = guard.Address(0x523644C, 1),
            VectorClassSlot = guard.Address(0x4F80010, 8)
        };
        NativeSupport.GetDelegate<ConfigureScale>("ConfigureModelScale")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModelScaleConfig
    {
        public nint Original;
        public nint MetadataInitialized;
        public nint VectorClassSlot;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureScale(ref ModelScaleConfig config);
}

public static class ModelScaleCoordinatePatch
{
    public static IDisposable Apply(PatchGuard guard) =>
        GuardedNativeBlock.Apply(guard, "Btd6Patches.model-scale-coordinate-current-contract.json");
}

public static class ModelDisplayConversionPatch
{
    public static IDisposable Apply(PatchGuard guard) => GuardedCallSites.Apply(guard,
        "Btd6Patches.model-display-contract.json", () => ConfigureNative(guard));

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new DisplayConversionConfig
        {
            Original = guard.Address(0xD71A40, 1),
            BoxedInitialized = guard.Address(0x4CBFF00, 1),
            VectorInitialized = guard.Address(0x4CBFEEC, 1),
            ClassSlot = guard.Address(0x4A07398, 8)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureModelDisplay")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConversionConfig
    {
        public nint Original;
        public nint BoxedInitialized;
        public nint VectorInitialized;
        public nint ClassSlot;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref DisplayConversionConfig config);
}

public static class ModelGeometryInitializationPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        var method = typeof(Scene).GetMethod("PresentModels",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var address = guard.VerifyRegular("Patch23", method);
        guard.VerifyHelpers();
        var resolve = Marshal.GetDelegateForFunctionPointer<ResolveMetadata>(guard.Address(0x385730, 19));
        foreach (var rva in new[] { 0x4A07398L, 0x4A30F90L })
        {
            var slot = guard.Address(rva, 8);
            resolve(slot);
            nint nativeClass = Marshal.ReadIntPtr(slot);
            if (nativeClass == 0)
            {
                throw new InvalidOperationException(Errors.GeometryMetadataUnavailable);
            }

            IL2CPP.il2cpp_runtime_class_init(nativeClass);
            if (Marshal.ReadIntPtr(slot) != nativeClass || Marshal.ReadInt32(nativeClass, 0xE4) == 0)
            {
                throw new InvalidOperationException(Errors.GeometryInitializationNotProven);
            }
        }

        var entry = guard.GetEntry("Patch23");
        var original = Convert.FromHexString(entry.Body);
        var replacementCode = NativeInitializedBranchCode.Create(original, entry.InitializationCompareOffsets,
            entry.InitializedBranchOffsets);
        return new NativeInlinePatch(address, original, replacementCode, null);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResolveMetadata(nint slot);
}

public static class TextPresentationCachingPatch
{
    public static IDisposable Apply(PatchGuard guard) =>
        GuardedCallSites.Apply(guard, "Btd6Patches.text-contract.json", () => ConfigureNative(guard));

    private static void ConfigureNative(PatchGuard guard)
    {
        var comparer = GameCorlib::Il2CppSystem.Collections.Generic.EqualityComparer<string>.Default;
        nint game = NativeLibrary.Load("GameAssembly.dll");
        var getMethods =
            Marshal.GetDelegateForFunctionPointer<GetClassMethods>(NativeLibrary.GetExport(game,
                "il2cpp_class_get_methods"));
        var getMethodName =
            Marshal.GetDelegateForFunctionPointer<GetMethodName>(
                NativeLibrary.GetExport(game, "il2cpp_method_get_name"));
        var getParameterCount =
            Marshal.GetDelegateForFunctionPointer<GetParameterCount>(NativeLibrary.GetExport(game,
                "il2cpp_method_get_param_count"));
        var findClass =
            Marshal.GetDelegateForFunctionPointer<FindClassByName>(NativeLibrary.GetExport(game,
                "il2cpp_class_from_name"));
        VerifyComparer(guard, comparer.Pointer, getMethods, getMethodName, getParameterCount);
        var config = new TextCacheConfig
        {
            DefaultComparer = comparer.Pointer,
            StringClass = findClass(IL2CPP.il2cpp_get_corlib(), "System", "String"),
            Original = guard.Address(0x23CA8F0, 1),
            FindEntry = guard.Address(0x23C01B0, 1),
            StringEquals = guard.Address(0x278B670, 1)
        };
        if (config.StringClass == 0)
        {
            throw new InvalidOperationException(Errors.NativeStringTypeUnavailable);
        }

        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureTextCache")(ref config);
    }

    private static void VerifyComparer(PatchGuard guard, nint comparer, GetClassMethods getMethods,
        GetMethodName getMethodName, GetParameterCount getParameterCount)
    {
        nint iterator = 0;
        nint method;
        var equalsVerified = false;
        var hashVerified = false;
        var observedMethods = new List<string>();
        while ((method = getMethods(Marshal.ReadIntPtr(comparer), ref iterator)) != 0)
        {
            var methodName = Marshal.PtrToStringAnsi(getMethodName(method));
            var parameterCount = getParameterCount(method);
            if ((methodName == "Equals" && parameterCount == 2) || (methodName == "GetHashCode" && parameterCount == 1))
            {
                observedMethods.Add(
                    $"{methodName}/{parameterCount} RVA=0x{Marshal.ReadIntPtr(method) - guard.Address(0, 1):X}");
            }

            switch (methodName, parameterCount)
            {
                case ("Equals", 2):
                    equalsVerified = Marshal.ReadIntPtr(method) == guard.Address(0x26F5B00, 1) ||
                                     Marshal.ReadIntPtr(method) == guard.Address(0x28CE650, 1);
                    break;
                case ("GetHashCode", 1):
                    hashVerified = Marshal.ReadIntPtr(method) == guard.Address(0x1BFADB0, 1);
                    break;
            }
        }

        if (!equalsVerified || !hashVerified)
        {
            throw new InvalidOperationException(Errors.UnverifiedDefaultStringComparer +
                                                string.Join(", ", observedMethods));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextCacheConfig
    {
        public nint DefaultComparer;
        public nint StringClass;
        public nint Original;
        public nint FindEntry;
        public nint StringEquals;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref TextCacheConfig config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetClassMethods(nint nativeClass, ref nint iterator);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetMethodName(nint method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetParameterCount(nint method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint FindClassByName(nint image, [MarshalAs(UnmanagedType.LPStr)] string namespaceName,
        [MarshalAs(UnmanagedType.LPStr)] string className);
}

public static class ModelRotationReusePatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        return GuardedCallSites.Apply(guard, "Btd6Patches.model-rotation-current-contract.json",
            () => ConfigureNative(guard));
    }

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new RotationConfig
        {
            Original = guard.Address(0x1509260, 1150),
            Initialized = guard.Address(0x5239efd, 1),
            SceneClassSlot = guard.Address(0x4fcd200, 8)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureModelRotationReuse")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RotationConfig
    {
        public nint Original;
        public nint Initialized;
        public nint SceneClassSlot;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref RotationConfig config);
}

public static class InterfaceDispatchPatch
{
    public static byte[] ContributeBuffSites(byte[] replacementCode, nint address, nint helper)
    {
        return NativeDispatchCode.InterfaceSites(replacementCode, address, helper,
            new[]
            {
                (0x1F4, 0x239, 0, 0x36F), (0x458, 0x4A9, 0, 0x643), (0x4D5, 0x519, 0, 0x661), (0x7B8, 0x809, 0, 0x988),
                (0x835, 0x879, 0, 0x9A6)
            });
    }

    public static IDisposable ApplyProjectileFilter(PatchGuard guard)
    {
        var method = typeof(Projectile).GetMethod("FilterBloon",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var address = guard.VerifyRegular("Patch24", method);
        NativeSupport.Initialize(guard);
        NativeSupport.InitializeDispatch(guard);
        var original = Convert.FromHexString(guard.GetEntry("Patch24").Body);
        return NativePatchGroup.Apply(group =>
        {
            var stub = group.Own(new NativeCallStub(address, NativeSupport.Export("ResolveInterfaceFast")));
            // ECX holds the index; enter after the original MOVZX ECX,AX.
            var replacementCode =
                NativeDispatchCode.InterfaceSites(original, address, stub.Address, new[] { (0x134, 0x178, 32, 0x1D1) });
            group.Own(new NativeInlinePatch(address, original, replacementCode, null));
        });
    }
}

public static class SimulationIteratorAccessPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        var method = typeof(RootObject).GetMethod("ProcessRootObject",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var address = guard.VerifyRegular("Patch25", method);
        NativeSupport.Initialize(guard);
        NativeSupport.InitializeDispatch(guard);
        var original = Convert.FromHexString(guard.GetEntry("Patch25").Body);
        var replacementCode = (byte[])original.Clone();
        return NativePatchGroup.Apply(group =>
        {
            var stub = group.Own(new NativeCallStub(address, NativeSupport.Export("LockCurrentFast")));
            NativeCallStub.WriteCall(replacementCode, 0x121, address, stub.Address);
            group.Own(new NativeInlinePatch(address, original, replacementCode, null));
        });
    }
}

public static class HotLoopIteratorAccessPatches
{
    public static IDisposable ApplyCollision(PatchGuard guard) => Apply(guard, "collision-loop");
    public static IDisposable ApplyWeapon(PatchGuard guard) => Apply(guard, "weapon-loop");
    public static IDisposable ApplyModels(PatchGuard guard) => Apply(guard, "model-loop");
    public static IDisposable ApplyWeaponAnimation(PatchGuard guard) => Apply(guard, "weapon-animation-loop");
    public static IDisposable ApplyChecker(PatchGuard guard) => Apply(guard, "collision-checker-loop");

    private static IDisposable Apply(PatchGuard guard, string name)
    {
        return GuardedCallSites.Apply(guard, $"Btd6Patches.{name}-contract.json",
            () => NativeSupport.InitializeDispatch(guard));
    }
}

public static class CurrentIteratorAccessPatches
{
    public static IDisposable ApplyWeapon(PatchGuard guard) => Apply(guard, "weapon-loop");
    public static IDisposable ApplyRoot(PatchGuard guard) => Apply(guard, "root-loop");
    public static IDisposable ApplyChecker(PatchGuard guard) => Apply(guard, "checker-loop");
    public static IDisposable ApplyModels(PatchGuard guard) => Apply(guard, "model-loop");

    private static IDisposable Apply(PatchGuard guard, string name) =>
        GuardedCallSites.Apply(guard, $"Btd6Patches.{name}-current-contract.json", () =>
            NativeSupport.GetDelegate<ConfigureCurrent>("ConfigureLockCurrent")(guard.Address(0x27D5FB0, 0x89)));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureCurrent(nint current);
}

public static class CollisionPositionAccessPatch
{
    public static IDisposable Apply(PatchGuard guard) => NativePatchGroup.Apply(group =>
    {
        for (var index = 0; index < 8; index++)
        {
            group.Own(GuardedNativeBlock.Apply(guard,
                $"Btd6Patches.collision-position-{index}-current-contract.json", () =>
                    NativeSupport.GetDelegate<ConfigurePosition>("ConfigureCollisionPosition")(
                        guard.Address(0x24B2270, 0x75), guard.Address(0x523DEAE, 1))));
        }
    });

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigurePosition(nint getter, nint initialized);
}

public static class ZonedProcessingPatches
{
    public static IDisposable ApplyListAccess(PatchGuard guard)
    {
        var contract = NativeContracts.Read<GuardedCallSiteContract>("Btd6Patches.zoned-list-current-contract.json");
        contract.Verify(guard.ReadBytes);
        return NativePatchGroup.Apply(group =>
        {
            foreach (var site in contract.Sites)
            {
                var address = guard.Address(site.Rva, 5);
                var original = guard.Address(site.Callee, 0x46);
                var stub = group.Own(new NativeCallStub(address, original));
                NativeMemory.WriteCode(stub.Address, ZonedInlineCode.ListItem(original));
                var replacement = new byte[5];
                NativeCallStub.WriteCall(replacement, 0, address, stub.Address);
                group.Own(new NativeInlinePatch(address, guard.ReadBytes(site.Rva, 5), replacement, null));
            }
        });
    }

    public static IDisposable ApplyRunReuse(PatchGuard guard)
    {
        var contract = NativeContracts.Read<NativeBlockContract>("Btd6Patches.zoned-run-current-contract.json");
        new GuardedCallSiteContract { Helpers = contract.Helpers }.Verify(guard.ReadBytes);
        var original = Convert.FromHexString(contract.Original);
        if (!guard.ReadBytes(contract.Start, original.Length).AsSpan().SequenceEqual(original))
        {
            throw new InvalidOperationException(Errors.NativeBlockChangedOrHooked);
        }

        NativeSupport.Initialize(guard);
        var getter = guard.Address(0x1B80D50, 0x46);
        NativeSupport.GetDelegate<ConfigureRun>("ConfigureZonedRun")(getter, guard.Address(0x3F26D0, 1));
        return NativePatchGroup.Apply(group =>
        {
            var address = guard.Address(contract.Start, original.Length);
            var stub = group.Own(new NativeCallStub(address, NativeSupport.Export(contract.Export),
                argumentSetup: Convert.FromHexString(contract.ArgumentSetup)));
            var replacement = ZonedInlineCode.RunBlock(address, getter, stub.Address, guard.Address(contract.End, 1));
            group.Own(new NativeInlinePatch(address, original, replacement, null));
        });
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureRun(nint original, nint raiseNull);
}

internal static class ZonedInlineCode
{
    public static byte[] ListItem(nint getter)
    {
        var code = new List<byte>();
        var branches = new List<int>();
        GetterGuard(code, getter);
        Branch(code, branches, "0F85");
        Add(code, "4885C9");
        Branch(code, branches, "0F84");
        Add(code, "3B5118");
        Branch(code, branches, "0F83");
        Add(code, "488B41104885C0");
        Branch(code, branches, "0F84");
        Add(code, "3B5018");
        Branch(code, branches, "0F83");
        // The getter sign-extends the index before reading the reference array.
        Add(code, "4863D2488B44D020");
        Add(code, "C3");
        Resolve(code, branches);
        Jump(code, getter);
        return code.ToArray();
    }

    public static byte[] RunBlock(nint address, nint getter, nint fallback, nint continuation)
    {
        var code = new List<byte>();
        GetterGuard(code, getter);
        Add(code, "0F84");
        code.AddRange(BitConverter.GetBytes(checked((int)(continuation - (address + 29)))));
        Add(code, "E8");
        code.AddRange(BitConverter.GetBytes(checked((int)(fallback - (address + 34)))));
        return code.ToArray();
    }

    private static void GetterGuard(List<byte> code, nint getter)
    {
        Address(code, getter);
        Add(code, "49BA48895C24085748834D3913");
    }

    private static void Branch(List<byte> code, List<int> branches, string opcode)
    {
        Add(code, opcode);
        branches.Add(code.Count);
        code.AddRange(new byte[4]);
    }

    private static void Resolve(List<byte> code, List<int> branches)
    {
        foreach (var offset in branches)
        {
            var displacement = BitConverter.GetBytes(code.Count - offset - 4);
            for (var index = 0; index < displacement.Length; index++)
            {
                code[offset + index] = displacement[index];
            }
        }
    }

    private static void Address(List<byte> code, nint address)
    {
        Add(code, "49BB");
        code.AddRange(BitConverter.GetBytes((long)address));
    }

    private static void Jump(List<byte> code, nint address)
    {
        Address(code, address);
        Add(code, "41FFE3");
    }

    private static void Add(List<byte> code, string bytes) => code.AddRange(Convert.FromHexString(bytes));
}

public static class IteratorAdvancePatch
{
    public static IDisposable Apply(PatchGuard guard) => GuardedCallSites.Apply(guard,
        "Btd6Patches.iterator-advance-current-contract.json", () =>
            NativeSupport.GetDelegate<ConfigureAdvance>("ConfigureIteratorAdvance")(
                guard.Address(0x27D1BC0, 0x158), guard.Address(0x1B66CA0, 8)));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureAdvance(nint original, nint count);
}

public static class CurrentModelProjectionPatch
{
    public static IDisposable Apply(PatchGuard guard) => GuardedCallSites.Apply(guard,
        "Btd6Patches.model-projection-current-contract.json", () =>
            NativeSupport.GetDelegate<ConfigureProjection>("ConfigureCurrentModelProjection")(
                guard.Address(0xF448B0, 0x96), guard.Address(0xF46240, 0x198),
                guard.Address(0x5235C8F, 1), guard.Address(0x5235C7B, 1),
                guard.Address(0x4F80010, 8)));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureProjection(nint original, nint value, nint boxedFlag, nint valueFlag,
        nint classSlot);
}

public static class RootUnHoldInlinePatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        return GuardedCallSites.Apply(guard, "Btd6Patches.root-unhold-contract.json", () =>
        {
        });
    }
}

public static class CurrentTravelPatches
{
    public static IDisposable ApplyPosition(PatchGuard guard, bool combine = false)
    {
        if (combine)
        {
            return GuardedNativeBlock.Apply(guard, "Btd6Patches.travel-position-pair-current-contract.json",
                () => ConfigureAccess(guard));
        }

        return ApplyPositionCalls(guard);
    }

    private static IDisposable ApplyPositionCalls(PatchGuard guard) => NativePatchGroup.Apply(group =>
    {
        for (var index = 0; index < 2; index++)
        {
            group.Own(GuardedNativeBlock.Apply(guard,
                $"Btd6Patches.travel-position-{index}-current-contract.json", () => ConfigureAccess(guard)));
        }
    });

    public static IDisposable ApplyDirection(PatchGuard guard) => GuardedCallSites.Apply(guard,
        "Btd6Patches.travel-direction-current-contract.json", () => ConfigureAccess(guard));

    private static void ConfigureAccess(PatchGuard guard)
    {
        var config = new PositionConfig
        {
            EntityGetter = guard.Address(0xB3E940, 0xB5),
            CommonGetter = guard.Address(0x24B2270, 0x75),
            EntityInitialized = guard.Address(0x5233D18, 1),
            CommonInitialized = guard.Address(0x523DEAE, 1),
            CommonInterfaceSlot = guard.Address(0x4FC9D38, 8)
        };
        NativeSupport.GetDelegate<ConfigurePosition>("ConfigureEntityPosition")(ref config);
        NativeSupport.GetDelegate<ConfigureDirection>("ConfigureTravelDirection")(
            guard.Address(0x664F20, 8), guard.Address(0x3DB0, 10));
    }

    public static IDisposable ApplyMath(PatchGuard guard, bool combine = false) => combine
        ? GuardedNativeBlock.Apply(guard, "Btd6Patches.travel-math-fusion-current-contract.json",
            () => ConfigureMathNative(guard))
        : GuardedCallSites.Apply(guard, "Btd6Patches.travel-math-current-contract.json",
            () => ConfigureMathNative(guard));

    private static void ConfigureMathNative(PatchGuard guard) =>
        NativeSupport.GetDelegate<ConfigureMath>("ConfigureCurrentTravelMath")(
            guard.Address(0x4F80010, 8), guard.Address(0xF44F30, 0x9D),
            guard.Address(0xF44D90, 0xA9), guard.Address(0x5235C99, 1),
            guard.Address(0x5235C96, 1));

    [StructLayout(LayoutKind.Sequential)]
    private struct PositionConfig
    {
        public nint EntityGetter;
        public nint CommonGetter;
        public nint EntityInitialized;
        public nint CommonInitialized;
        public nint CommonInterfaceSlot;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigurePosition(ref PositionConfig config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureDirection(nint getter, nint original);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureMath(nint classSlot, nint multiply, nint add, nint multiplyFlag, nint addFlag);
}

public static class TravelEntityPositionPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        var contract = NativeContracts.Read<EntityPositionContract>("Btd6Patches.entity-position-contract.json");
        contract.Verify(guard.ReadBytes);
        NativeSupport.Initialize(guard);
        var config = new EntityPositionConfig
        {
            Entity = guard.Address(0xA98C30, 1),
            Common = guard.Address(0x21FA140, 1),
            EntityInitialized = guard.Address(0x4CBE6C0, 1),
            CommonInitialized = guard.Address(0x4CC7CE2, 1),
            InterfaceSlot = guard.Address(0x4A548E0, 8)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureEntityPosition")(ref config);
        return NativePatchGroup.Apply(group =>
        {
            var address = guard.Address(contract.Start, 48);
            var stub = group.Own(new NativeCallStub(address, NativeSupport.Export("TravelEntityPositions"),
                argumentSetup: EntityPositionCode.ArgumentSetup));
            group.Own(new NativeInlinePatch(address, Convert.FromHexString(contract.Body),
                EntityPositionCode.Create(address, stub.Address), null));
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EntityPositionConfig
    {
        public nint Entity;
        public nint Common;
        public nint EntityInitialized;
        public nint CommonInitialized;
        public nint InterfaceSlot;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref EntityPositionConfig config);
}

public static class TravelStraightVectorMathPatch
{
    public static IDisposable Apply(PatchGuard guard) => GuardedCallSites.Apply(guard,
        "Btd6Patches.travel-math-contract.json", () => ConfigureNative(guard));

    private static void ConfigureNative(PatchGuard guard)
    {
        guard.VerifyHelpers();
        var resolve = Marshal.GetDelegateForFunctionPointer<ResolveMetadata>(guard.Address(0x385730, 19));
        var slot = guard.Address(0x4A07398, 8);
        resolve(slot);
        nint nativeClass = Marshal.ReadIntPtr(slot);
        if (nativeClass == 0)
        {
            throw new InvalidOperationException(Errors.Vector3MetadataUnavailable);
        }

        IL2CPP.il2cpp_runtime_class_init(nativeClass);
        if (Marshal.ReadIntPtr(slot) != nativeClass || Marshal.ReadInt32(nativeClass, 0xE4) == 0)
        {
            throw new InvalidOperationException(Errors.Vector3InitializationNotProven);
        }

        var config = new VectorMathConfig
        {
            GeometryClass = nativeClass,
            Multiply = guard.Address(0xD72070, 1),
            Add = guard.Address(0xD71ED0, 1)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureTravelMath")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VectorMathConfig
    {
        public nint GeometryClass;
        public nint Multiply;
        public nint Add;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref VectorMathConfig config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResolveMetadata(nint slot);
}

public static class ProjectileTimersPatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        return NativePatchGroup.Apply(group =>
        {
            group.Own(GuardedCallSites.Apply(guard, "Btd6Patches.projectile-age-current-contract.json",
                () => ConfigureNative(guard)));
            group.Own(GuardedNativeBlock.Apply(guard, "Btd6Patches.projectile-history-current-contract.json",
                () => ConfigureNative(guard)));
        });
    }

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new TimerConfig
        {
            OriginalAge = guard.Address(0xc84980, 152),
            AgeInitialized = guard.Address(0x5234724, 1),
            CommonInterface = Il2CppClassPointerStore.GetNativeClassPointer(typeof(ICommonBehaviorProxy)),
            RaiseNull = guard.Address(0x3f26d0, 1)
        };
        if (config.CommonInterface == 0)
        {
            throw new InvalidOperationException(Errors.ProjectileTimerInterfaceUnavailable);
        }

        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureProjectileTimers")(ref config);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimerConfig
    {
        public nint OriginalAge;
        public nint AgeInitialized;
        public nint CommonInterface;
        public nint RaiseNull;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref TimerConfig config);
}

public sealed class LineageIntersectionPatch : IDisposable
{
    private readonly Detour _hook;
    private bool _disposed;
    private SetIndexedSearchEnabled? _setIndexedSearchEnabled;

    private LineageIntersectionPatch(Detour hook)
    {
        _hook = hook;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_setIndexedSearchEnabled != null)
        {
            _setIndexedSearchEnabled(0);
            _setIndexedSearchEnabled = null;
        }

        _hook.Dispose();
        _disposed = true;
    }

    public static LineageIntersectionPatch Apply(PatchGuard guard)
    {
        var method = typeof(Projectile).GetMethod("HaveAlreadyCollidedWithBloonInLineage",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        var address = guard.VerifyRegular("Patch7", method);
        NativeSupport.Initialize(guard);
        var setOriginal = NativeSupport.GetDelegate<SetOriginalLineageScan>("SetLineageOriginal");
        var hook = new Detour(address, NativeSupport.Export("LineageScan"));
        try
        {
            hook.Attach();
            setOriginal(hook.Trampoline);
            return new LineageIntersectionPatch(hook);
        }
        catch
        {
            hook.Dispose();
            throw;
        }
    }

    public void EnableIndexedSearch()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LineageIntersectionPatch));
        }

        _setIndexedSearchEnabled = NativeSupport.GetDelegate<SetIndexedSearchEnabled>("SetIndexedLineage");
        _setIndexedSearchEnabled(1);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOriginalLineageScan(nint original);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetIndexedSearchEnabled(byte enabled);

    private sealed class Detour : IDisposable
    {
        private static readonly List<Detour> _activeHooks = new();
        private readonly NativeHook<LineageScanCallback> _hook;

        public Detour(nint target, nint replacement)
        {
            _hook = new NativeHook<LineageScanCallback>(target, replacement);
            _activeHooks.Add(this);
        }

        public nint Trampoline => _hook.TrampolineHandle;

        public void Dispose()
        {
            _hook.Detach();
            if (_hook.IsHooked)
            {
                throw new InvalidOperationException(Errors.LineageHookRemainsAttached);
            }

            _activeHooks.Remove(this);
        }

        public void Attach()
        {
            _hook.Attach();
            if (!_hook.IsHooked)
            {
                throw new InvalidOperationException(Errors.LineageHookFailed);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte LineageScanCallback(nint projectile, nint bloon, nint info);
    }
}

public static class StrengthSelectorReusePatch
{
    public static NativeInlinePatch Apply(PatchGuard guard)
    {
        var factory = typeof(TargetStrong).GetMethod(nameof(TargetStrong.GetTargets),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var entry = guard.GetEntry("Patch5");
        var method = guard.Verify("Patch5", factory);
        var selector = typeof(TargetSupplier).GetMethod("OrderByStrong",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)!;
        if (selector.ReturnType != typeof(float) || selector.GetParameters().Length != 1 ||
            selector.GetParameters()[0].ParameterType != typeof(Target))
        {
            throw new InvalidOperationException(Errors.UnexpectedStrengthSelectorSignature);
        }

        guard.VerifyNativeMember(selector, entry.SelectorRva, entry.SelectorBody);
        nint selectorMethodInfo = InteropSupport.MethodBaseToIl2CppMethodInfoPointer(selector);
        // The native delegate constructor needs IL2CPP MethodInfo, not a CLR delegate.
        var cachedSelector = new GameCorlib::Il2CppSystem.Func<Target, float>(null, selectorMethodInfo);
        var replacementCode = Convert.FromHexString(entry.Body);
        if (entry.PatchOffset < 0 || entry.PatchLength != 38 ||
            entry.PatchOffset > replacementCode.Length - entry.PatchLength)
        {
            throw new InvalidOperationException(Errors.UnexpectedSelectorAllocationBlock);
        }

        return new NativeInlinePatch(method + entry.PatchOffset,
            replacementCode.AsSpan(entry.PatchOffset, entry.PatchLength).ToArray(),
            NativeSelectorCode.Create(cachedSelector.Pointer, entry.PatchLength), cachedSelector);
    }
}

public static class TargetingComparerCachePatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        var method = typeof(Attack).GetMethod("GetPathSegmentsInRangeFast",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        var address = guard.VerifyRegular("Patch8", method);
        NativeSupport.Initialize(guard);
        var original = Convert.FromHexString(guard.GetEntry("Patch8").Body);
        return NativePatchGroup.Apply(group =>
        {
            var stub = group.Own(new NativeCallStub(address, NativeSupport.Export("CachedComparer")));
            var replacement = NativeSimulationCode.Comparer(original, address, stub.Address);
            group.Own(new NativeInlinePatch(address, original, replacement, null));
        });
    }
}

public static class WeaponRateReusePatch
{
    public static IDisposable Apply(PatchGuard guard)
    {
        return GuardedNativeBlock.Apply(guard, "Btd6Patches.weapon-rate-current-contract.json",
            () => ConfigureNative(guard));
    }

    private static void ConfigureNative(PatchGuard guard)
    {
        var config = new WeaponRateConfig
        {
            Modf = guard.Address(0x48bad0, 1),
            Floor = guard.Address(0x48bc40, 1),
            Ceil = guard.Address(0x48bba0, 1),
            Round = guard.Address(0x48bcd0, 1),
            MetadataInit = guard.Address(0x3f2480, 1),
            ClassInit = guard.Address(0x3f2760, 1),
            RoundFlag = guard.Address(0x52314ff, 1),
            RoundClassSlot = guard.Address(0x4f56b30, 8),
            Multiplier = ReadSingle(guard, 0x3e4bc50),
            Threshold = ReadSingle(guard, 0x3e4c184),
            Half = ReadSingle(guard, 0x3e4bc28),
            One = ReadSingle(guard, 0x3e4bc30),
            PositiveHalf = ReadDouble(guard, 0x3e4bd28),
            NegativeHalf = ReadDouble(guard, 0x3e4bd68)
        };
        NativeSupport.GetDelegate<ConfigureNativeDelegate>("ConfigureWeaponRateReuse")(ref config);
    }

    private static float ReadSingle(PatchGuard guard, long rva) =>
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(guard.Address(rva, 4)));

    private static double ReadDouble(PatchGuard guard, long rva) =>
        BitConverter.Int64BitsToDouble(Marshal.ReadInt64(guard.Address(rva, 8)));

    [StructLayout(LayoutKind.Sequential)]
    private struct WeaponRateConfig
    {
        public nint Modf;
        public nint Floor;
        public nint Ceil;
        public nint Round;
        public nint MetadataInit;
        public nint ClassInit;
        public nint RoundFlag;
        public nint RoundClassSlot;
        public float Multiplier;
        public float Threshold;
        public float Half;
        public float One;
        public double PositiveHalf;
        public double NegativeHalf;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureNativeDelegate(ref WeaponRateConfig config);
}

public sealed class ExperimentalIdleGarbageCollectionPatch : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly MelonLogger.Instance _logger;
    private readonly IdleGcPolicy _policy;
    private bool _disposed;
    private double _nextPollTime;

    private ExperimentalIdleGarbageCollectionPatch(PatchConfig config, MelonLogger.Instance logger)
    {
        _policy = new IdleGcPolicy(config.ExperimentalGcGrowthMiB * 1024L * 1024, config.ExperimentalGcCooldownSeconds,
            config.ExperimentalGcIdleSeconds);
        _logger = logger;
    }

    public void Dispose()
    {
        _disposed = true;
        _clock.Stop();
    }

    public static ExperimentalIdleGarbageCollectionPatch Apply(PatchConfig config, MelonLogger.Instance logger)
    {
        _ = IL2CPP.il2cpp_gc_get_used_size();
        _ = GameCorlib::Il2CppSystem.GC.CollectionCount(0);
        return new ExperimentalIdleGarbageCollectionPatch(config, logger);
    }

    public void Tick()
    {
        var elapsedSeconds = _clock.Elapsed.TotalSeconds;
        if (_disposed || elapsedSeconds < _nextPollTime)
        {
            return;
        }

        _nextPollTime = elapsedSeconds + 0.5;
        var game = InGame.instance;
        var bridge = game == null ? null : InGame.Bridge;
        var idle = game != null && bridge != null && !game.IsCoop;
        if (idle)
        {
            bridge!.GetRoundDisplayInfo(out _, out var roundsActive, out var lost);
            idle = !roundsActive && !lost;
        }

        var gcDisabled = IL2CPP.il2cpp_gc_is_disabled();
        var usedBytes = IL2CPP.il2cpp_gc_get_used_size();
        var collectionCount = GameCorlib::Il2CppSystem.GC.CollectionCount(0);
        if (!_policy.ShouldCollect(elapsedSeconds, usedBytes, bridge?.Pointer ?? (nint)0, idle && !gcDisabled,
                collectionCount))
        {
            return;
        }

        var collectionStartedAt = Stopwatch.GetTimestamp();
        IL2CPP.il2cpp_gc_collect(0);
        var durationMs = (Stopwatch.GetTimestamp() - collectionStartedAt) * 1000.0 / Stopwatch.Frequency;
        var remainingBytes = IL2CPP.il2cpp_gc_get_used_size();
        _policy.Collected(_clock.Elapsed.TotalSeconds, remainingBytes, usedBytes, durationMs,
            GameCorlib::Il2CppSystem.GC.CollectionCount(0));
        _logger.Msg(
            $"Idle Garbage Collection (Experimental): {durationMs:F1} ms; native used {usedBytes / 1048576} -> {remainingBytes / 1048576} MiB; next growth {_policy.EffectiveGrowthBytes / 1048576} MiB, cooldown {_policy.EffectiveCooldownSeconds:F0} s.");
    }
}

public sealed class RoundObjectRetirementPatch : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly MelonLogger.Instance _logger;
    private readonly RetireObjects _retire;
    private readonly StopRetirement _stop;
    private bool _disposed;
    private nint _gameContext;
    private int _lastRound = -1;
    private double _nextPollTime;
    private bool _wasIdle;

    private RoundObjectRetirementPatch(MelonLogger.Instance logger)
    {
        _logger = logger;
        _retire = NativeSupport.GetDelegate<RetireObjects>("RetireRoundObjects");
        _stop = NativeSupport.GetDelegate<StopRetirement>("StopRoundRetirement");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _stop();
        _disposed = true;
        _clock.Stop();
    }

    public static RoundObjectRetirementPatch Apply(PatchGuard guard, PatchConfig settings, bool poolingActive,
        MelonLogger.Instance logger)
    {
        if (!poolingActive)
        {
            throw new InvalidOperationException(Errors.RetirementRequiresBuffPooling);
        }

        if (settings.ExperimentalRetirementPoolCapacity < 32 || settings.ExperimentalRetirementPoolCapacity > 256 ||
            settings.ExperimentalRetirementKeepPerClass < 0 || settings.ExperimentalRetirementKeepPerClass > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), Errors.RetirementSettingsOutsideSupportedBounds);
        }

        NativeSupport.Initialize(guard);
        nint game = NativeLibrary.Load("GameAssembly.dll");
        // The worker requires a pointer-sized thread API.
        foreach (var name in new[] { "il2cpp_thread_attach", "il2cpp_thread_detach" })
        {
            var method = typeof(IL2CPP).GetMethods().Single(m => m.Name == name);
            if (!method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(nint) }) ||
                method.ReturnType != (name.EndsWith("attach") ? typeof(nint) : typeof(void)))
            {
                throw new InvalidOperationException(Errors.UnsupportedRuntimeThreadAPI + name);
            }
        }

        var config = new RetirementConfig
        {
            Domain = IL2CPP.il2cpp_domain_get(),
            Attach = NativeLibrary.GetExport(game, "il2cpp_thread_attach"),
            Detach = NativeLibrary.GetExport(game, "il2cpp_thread_detach")
        };
        var result = new RoundObjectRetirementPatch(logger);
        if (NativeSupport.GetDelegate<StartRetirement>("StartRoundRetirement")(ref config,
                (uint)settings.ExperimentalRetirementPoolCapacity, (uint)settings.ExperimentalRetirementKeepPerClass) ==
            0)
        {
            throw new InvalidOperationException(Errors.RetirementWorkerCouldNotStart);
        }

        logger.Msg(
            $"Round Object Retirement: capacity={settings.ExperimentalRetirementPoolCapacity}, warm reserve={settings.ExperimentalRetirementKeepPerClass}/class; worker releases roots only.");
        return result;
    }

    public void Tick()
    {
        var elapsedSeconds = _clock.Elapsed.TotalSeconds;
        if (_disposed || elapsedSeconds < _nextPollTime)
        {
            return;
        }

        _nextPollTime = elapsedSeconds + 0.25;
        var game = InGame.instance;
        var bridge = game == null ? null : InGame.Bridge;
        nint gameContext = bridge?.Pointer ?? (nint)0;
        var round = -1;
        var idle = game != null && bridge != null && !game.IsCoop;
        var canRetire = idle;
        if (canRetire)
        {
            bridge!.GetRoundDisplayInfo(out _, out var roundsActive, out var lost);
            round = bridge.GetCurrentRound();
            canRetire = !lost;
            idle = canRetire && !roundsActive;
        }

        if (gameContext != _gameContext)
        {
            _gameContext = gameContext;
            _wasIdle = false;
            _lastRound = -1;
        }

        // Autostart can skip idle intervals, so also retire leases when the round changes.
        if (canRetire && ((idle && !_wasIdle) || (_lastRound >= 0 && round != _lastRound)))
        {
            var count = _retire();
            if (count > 0)
            {
                _logger.Msg(
                    $"Round Object Retirement: queued {count} cleared/expired roots at round boundary/idle interval.");
            }
        }

        _wasIdle = idle;
        _lastRound = canRetire ? round : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetirementConfig
    {
        public nint Domain;
        public nint Attach;
        public nint Detach;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte StartRetirement(ref RetirementConfig config, uint capacity, uint keep);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint RetireObjects();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StopRetirement();
}

public sealed class IdleGcPolicy
{
    private readonly double _cooldownSeconds;
    private readonly long _growthBytes;
    private readonly double _idleDelaySeconds;
    private int _backoff = 1;
    private long _baselineBytes;
    private bool _collectedThisIdle;
    private nint _gameContext;
    private double _idleSince = double.NaN;
    private double _lastCollectionTime;
    private int? _observedCollections;

    public IdleGcPolicy(long growthBytes, double cooldownSeconds, double idleSeconds)
    {
        if (growthBytes < 16L * 1024 * 1024 || growthBytes > 4096L * 1024 * 1024 || !double.IsFinite(cooldownSeconds) ||
            cooldownSeconds < 5 || cooldownSeconds > 3600 || !double.IsFinite(idleSeconds) || idleSeconds < 1 ||
            idleSeconds > 30)
        {
            throw new ArgumentOutOfRangeException(null, Errors.ExperimentalGCScheduleOutsideSupportedBounds);
        }

        _growthBytes = growthBytes;
        _cooldownSeconds = cooldownSeconds;
        _idleDelaySeconds = Math.Max(5, idleSeconds);
    }

    public long EffectiveGrowthBytes => Math.Min(_growthBytes * _backoff, 4096L * 1024 * 1024);
    public double EffectiveCooldownSeconds => Math.Min(_cooldownSeconds * _backoff, 3600);

    public bool ShouldCollect(double elapsedSeconds, long usedBytes, nint gameContext, bool idle,
        int? collectionCount = null)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || usedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(null, Errors.GCMeasurementsMustBeFiniteAndNonnegative);
        }

        if (gameContext != _gameContext || gameContext == 0)
        {
            _gameContext = gameContext;
            _baselineBytes = usedBytes;
            _lastCollectionTime = elapsedSeconds;
            _idleSince = double.NaN;
            _collectedThisIdle = false;
            _observedCollections = collectionCount;
            _backoff = 1;
        }

        // Heap growth can hide a collection between polls; check the collection count too.
        if (collectionCount.HasValue && _observedCollections.HasValue && collectionCount != _observedCollections)
        {
            _baselineBytes = usedBytes;
            _lastCollectionTime = elapsedSeconds;
            _idleSince = double.NaN;
            _collectedThisIdle = idle;
        }

        _observedCollections = collectionCount;
        if (usedBytes < _baselineBytes)
        {
            _baselineBytes = usedBytes;
        }

        if (!idle || gameContext == 0)
        {
            _idleSince = double.NaN;
            _collectedThisIdle = false;
            return false;
        }

        if (double.IsNaN(_idleSince))
        {
            _idleSince = elapsedSeconds;
        }

        return !_collectedThisIdle && elapsedSeconds - _idleSince >= _idleDelaySeconds &&
               elapsedSeconds - _lastCollectionTime >= EffectiveCooldownSeconds &&
               usedBytes - _baselineBytes >= EffectiveGrowthBytes;
    }

    public void Collected(double elapsedSeconds, long usedBytes, long usedBeforeCollection = 0, double durationMs = 0,
        int? collectionCount = null)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || usedBytes < 0 || usedBeforeCollection < 0 ||
            !double.IsFinite(durationMs) || durationMs < 0)
        {
            throw new ArgumentOutOfRangeException(null, Errors.GCMeasurementsMustBeFiniteAndNonnegative);
        }

        if (usedBeforeCollection > 0)
        {
            var reclaimed = Math.Max(0, usedBeforeCollection - usedBytes);
            var expensive = durationMs >= 50;
            var ineffective = reclaimed < 16L * 1024 * 1024 || reclaimed / (double)usedBeforeCollection < 0.10;
            _backoff = expensive || ineffective ? Math.Min(_backoff * 2, 8) : Math.Max(_backoff / 2, 1);
        }

        _baselineBytes = usedBytes;
        _lastCollectionTime = elapsedSeconds;
        _collectedThisIdle = true;
        _observedCollections = collectionCount;
    }
}
