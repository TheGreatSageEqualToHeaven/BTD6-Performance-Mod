extern alias GameCorlib;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;
using static Btd6Patches.NativeMemory;

namespace Btd6Patches;

public sealed class PatchGuard
{
    private readonly NativeManifest _manifest;
    private readonly ProcessModule _module;

    public PatchGuard()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(Errors.UnsupportedPlatform);
        }

        _module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m =>
            string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
        var filename = _module.FileName ?? throw new InvalidOperationException(Errors.GameAssemblyPathUnavailable);
        using var file = File.OpenRead(filename);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(file));
        var selected = NativeContracts.ForBuild(hash);
        _manifest = selected.Manifest;
        Build = selected.Build;
    }

    public GameBuild Build { get; }

    public NativeMethodContract GetEntry(string name) => _manifest.Methods.Single(e => e.Name == name);

    public nint Address(long rva, int length)
    {
        if (length <= 0 || rva < 0 || rva > _module.ModuleMemorySize - length)
        {
            throw new InvalidOperationException(Errors.AddressOutsideInspectedGameModule);
        }

        return _module.BaseAddress + checked((int)rva);
    }

    public byte[] ReadBytes(long rva, int length) => Read(Address(rva, length), length);

    public nint Verify(string name, MethodInfo method)
    {
        var entry = GetEntry(name);
        return PatchSignature.Matches(method, entry.Type, entry.Method, entry.ElementType)
            ? VerifyNativeMember(method, entry.Rva, entry.Body)
            : throw new InvalidOperationException(Errors.UnexpectedNativeMethodSignature);
    }

    public nint VerifyRegular(string name, MethodInfo method)
    {
        var entry = GetEntry(name);
        if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters ||
            method.DeclaringType?.FullName?.Replace('+', '/') != entry.Type || method.Name != entry.Method ||
            TypeName(method.ReturnType) != entry.ReturnType || !method.GetParameters()
                .Select(p => TypeName(p.ParameterType)).SequenceEqual(entry.ParameterTypes))
        {
            throw new InvalidOperationException(Errors.UnexpectedReplacementMethodSignature);
        }

        return VerifyNativeMember(method, entry.Rva, entry.Body);
    }

    private static string TypeName(Type type)
    {
        return type.IsGenericType
            ? type.GetGenericTypeDefinition().FullName + "<" +
              string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">"
            : type.FullName!;
    }

    public void VerifyHelpers()
    {
        foreach (var entry in _manifest.Helpers)
        {
            var expected = Convert.FromHexString(entry.Body);
            var actual = ReadBytes(entry.Rva, expected.Length);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(string.Format(Errors.HelperChangedOrHooked, entry.Name));
            }
        }
    }

    public unsafe nint VerifyNativeMember(MethodBase method, long expectedRva, string expectedBody)
    {
        var info = InteropSupport.MethodBaseToIl2CppMethodInfoPointer(method);
        if (info == IntPtr.Zero)
        {
            throw new InvalidOperationException(Errors.NativeMethodInfoUnavailable);
        }

        nint pointer = UnityVersionHandler.Wrap((Il2CppMethodInfo*)info).MethodPointer;
        var rva = pointer - _module.BaseAddress.ToInt64();
        var expected = Convert.FromHexString(expectedBody);
        if (expected.Length == 0 || rva != expectedRva || rva < 0 || rva > _module.ModuleMemorySize - expected.Length)
        {
            throw new InvalidOperationException(Errors.NativeEntryPointDiffersFromInspectedMethod);
        }

        var actual = Read(pointer, expected.Length);
        return actual.AsSpan().SequenceEqual(expected)
            ? pointer
            : throw new InvalidOperationException(Errors.NativeMethodBodyChangedOrAlreadyHooked);
    }
}

public static class GuardedCallSites
{
    public static IDisposable Apply(PatchGuard guard, string resourceName, Action? configure = null)
    {
        var contract = NativeContracts.Read<GuardedCallSiteContract>(resourceName);
        contract.Verify(guard.ReadBytes);
        NativeSupport.Initialize(guard);
        configure?.Invoke();
        return NativePatchGroup.Apply(group =>
        {
            foreach (var site in contract.Sites)
            {
                var address = guard.Address(site.Rva, 5);
                var original = guard.ReadBytes(site.Rva, 5);
                var replacement = (byte[])original.Clone();
                if (site.Replacement.Length != 0)
                {
                    replacement = Convert.FromHexString(site.Replacement);
                    if (replacement.Length != 5)
                    {
                        throw new InvalidOperationException(Errors.InvalidCallSiteReplacementLength);
                    }
                }
                else
                {
                    var stub = group.Own(new NativeCallStub(address, NativeSupport.Export(site.Export),
                        argumentSetup: Convert.FromHexString(site.ArgumentSetup)));
                    if (site.TailJump)
                    {
                        NativeCallStub.WriteJump(replacement, 0, address, stub.Address);
                    }
                    else
                    {
                        NativeCallStub.WriteCall(replacement, 0, address, stub.Address);
                    }
                }

                group.Own(new NativeInlinePatch(address, original, replacement, null));
            }
        });
    }
}

public static class GuardedNativeBlock
{
    public static IDisposable Apply(PatchGuard guard, string resource, Action? configure = null)
    {
        var contract = NativeContracts.Read<NativeBlockContract>(resource);
        var dependencies = new GuardedCallSiteContract
        {
            Helpers = contract.Helpers
        };
        dependencies.Verify(guard.ReadBytes);
        var original = Convert.FromHexString(contract.Original);
        if (original.Length < 10 || original.Length != contract.End - contract.Start)
        {
            throw new InvalidOperationException(Errors.NativeBlockBoundaryMismatch);
        }

        var address = guard.Address(contract.Start, original.Length);
        var actual = guard.ReadBytes(contract.Start, original.Length);
        if (!actual.AsSpan().SequenceEqual(original))
        {
            throw new InvalidOperationException(Errors.NativeBlockChangedOrHooked);
        }

        NativeSupport.Initialize(guard);
        configure?.Invoke();
        return NativePatchGroup.Apply(group =>
        {
            var stub = group.Own(new NativeCallStub(address, NativeSupport.Export(contract.Export),
                argumentSetup: Convert.FromHexString(contract.ArgumentSetup)));
            var after = Convert.FromHexString(contract.ReturnSetup);
            if (after.Length + 10 > original.Length)
            {
                throw new InvalidOperationException(Errors.NativeReturnSetupExceedsBlock);
            }

            var replacement = NativeCode.CallAndContinue(original.Length, address, stub.Address, after,
                guard.Address(contract.Continuation == 0 ? contract.End : contract.Continuation, 1));
            group.Own(new NativeInlinePatch(address, original, replacement, null));
        });
    }
}

public sealed class NativeInlinePatch : IDisposable
{
    private static readonly List<NativeInlinePatch> _live = new();
    private readonly nint _address;
    private readonly byte[] _original;
    private readonly byte[] _replacement;
    private readonly Il2CppObjectBase? _root;
    private bool _disposed;

    public NativeInlinePatch(nint address, byte[] original, byte[] replacement, Il2CppObjectBase? root)
    {
        if (address == 0 || original.Length == 0 || original.Length != replacement.Length)
        {
            throw new ArgumentException(Errors.InvalidNativePatchRange);
        }

        _address = address;
        _original = (byte[])original.Clone();
        _replacement = (byte[])replacement.Clone();
        _root = root;
        if (!Read().AsSpan().SequenceEqual(original))
        {
            throw new InvalidOperationException(Errors.CallSiteChangedBeforePatchApplication);
        }

        // Keep the delegate rooted if writing or restoration fails.
        _live.Add(this);
        try
        {
            Write(replacement);
        }
        catch
        {
            Write(original);
            _live.Remove(this);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!Read().AsSpan().SequenceEqual(_replacement))
        {
            throw new InvalidOperationException(
                Errors.CallSiteOwnedByAnotherPatch);
        }

        Write(_original);
        _disposed = true;
        _live.Remove(this);
        GC.KeepAlive(_root);
    }

    private byte[] Read() => NativeMemory.Read(_address, _original.Length);
    private void Write(byte[] bytes) => WriteCode(_address, bytes);
}

public sealed class NativePatchGroup : IDisposable
{
    private static readonly List<NativePatchGroup> _live = new();
    private readonly List<IDisposable> _owned = new();

    public NativePatchGroup()
    {
        _live.Add(this);
    }

    public void Dispose()
    {
        // Restore callers before freeing stubs. Failed restores must keep their code alive.
        while (_owned.Count > 0)
        {
            _owned[^1].Dispose();
            _owned.RemoveAt(_owned.Count - 1);
        }

        _live.Remove(this);
    }

    public T Own<T>(T value)
        where T : IDisposable
    {
        _owned.Add(value);
        return value;
    }

    public static NativePatchGroup Apply(Action<NativePatchGroup> configure)
    {
        var group = new NativePatchGroup();
        try
        {
            configure(group);
            return group;
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }
}

public sealed class NativeEmptySequencePatch : IDisposable
{
    private static readonly List<NativeEmptySequencePatch> _live = new();
    private readonly NativeHook<GetSequenceNative> _hook;
    private readonly Il2CppObjectBase _sequenceRoot;
    private bool _disposed;
    private nint _executable;

    private NativeEmptySequencePatch(nint target, Il2CppObjectBase sequence)
    {
        _sequenceRoot = sequence;
        _executable = AllocateCode(NativeReturnCode.Create(sequence.Pointer));
        _hook = new NativeHook<GetSequenceNative>(target, _executable);
        // A failed attach can leave a live detour; retain its code.
        _live.Add(this);
        _hook.Attach();
        if (!_hook.IsHooked)
        {
            throw new InvalidOperationException(Errors.NativeHookDidNotAttach);
        }
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
            throw new InvalidOperationException(Errors.DetourRemainsActiveKeepingItsCodeAlive);
        }

        if (!VirtualFree(_executable, 0, 0x8000))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _executable = 0;
        _disposed = true;
        _live.Remove(this);
        GC.KeepAlive(_sequenceRoot);
    }

    public static NativeEmptySequencePatch Apply<T>(PatchGuard guard, string name, MethodInfo method)
        where T : Il2CppObjectBase
    {
        var target = guard.Verify(name, method);
        // Check the replacement type too; the manifest only describes the original method.
        return method.ReturnType.GetGenericArguments()[0] != typeof(T)
            ? throw new InvalidOperationException(Errors.ReplacementElementTypeMismatch)
            : new NativeEmptySequencePatch(target, EmptySequence<T>.Get());
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetSequenceNative(nint instance, nint methodInfo);

    private static class EmptySequence<T>
        where T : Il2CppObjectBase
    {
        private static GameCorlib::Il2CppSystem.Collections.Generic.IEnumerable<T>? _empty;

        public static Il2CppObjectBase Get()
        {
            if (_empty != null)
            {
                return _empty;
            }

            var array = GameCorlib::Il2CppSystem.Array.Empty<T>();
            var candidate = array.Cast<GameCorlib::Il2CppSystem.Collections.Generic.IEnumerable<T>>();
            var first = candidate.GetEnumerator();
            var second = candidate.GetEnumerator();
            // Reuse requires a stateless shared enumerator.
            if (array.Length != 0 || first.Pointer != second.Pointer ||
                first.Cast<GameCorlib::Il2CppSystem.Collections.IEnumerator>().MoveNext() ||
                second.Cast<GameCorlib::Il2CppSystem.Collections.IEnumerator>().MoveNext())
            {
                throw new InvalidOperationException(
                    string.Format(Errors.SharedEmptyEnumeratorUnavailable, typeof(T).Name));
            }

            _empty = candidate;
            return _empty;
        }
    }
}

public sealed class NativeCallStub : IDisposable
{
    private static readonly List<NativeCallStub> _live = new();

    public NativeCallStub(nint near, nint destination, bool passIterator = false, byte[]? argumentSetup = null)
    {
        if (passIterator && argumentSetup != null)
        {
            throw new ArgumentException(Errors.ConflictingNativeArgumentSetup);
        }

        // CALL shifts the iterator at caller RSP+A0 to stub RSP+A8.
        var setup = passIterator ? Convert.FromHexString("4C8B8424A8000000") : argumentSetup ?? Array.Empty<byte>();
        var code = new byte[setup.Length + 12];
        setup.CopyTo(code, 0);
        code[setup.Length] = 0x48;
        code[setup.Length + 1] = 0xB8;
        BitConverter.GetBytes(destination).CopyTo(code, setup.Length + 2);
        code[^2] = 0xFF;
        code[^1] = 0xE0;
        var basis = near & ~0xFFFFL;
        for (long delta = 0; delta < 0x70000000 && Address == 0; delta += 0x10000)
        {
            foreach (var sign in new[]
                     {
                         1L,
                         -1L
                     }
                    )
            {
                var hint = basis + sign * delta;
                if (hint <= 0)
                {
                    continue;
                }

                Address = VirtualAlloc((nint)hint, 0x1000, 0x3000, 0x04);
                if (Address != 0)
                {
                    break;
                }
            }
        }

        if (Address == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _live.Add(this);
        Marshal.Copy(code, 0, Address, code.Length);
        if (!VirtualProtect(Address, 0x1000, 0x20, out _) ||
            !FlushInstructionCache(GetCurrentProcess(), Address, (nuint)code.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public nint Address { get; private set; }

    public void Dispose()
    {
        if (Address == 0)
        {
            return;
        }

        if (!VirtualFree(Address, 0, 0x8000))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Address = 0;
        _live.Remove(this);
    }

    public static void WriteCall(byte[] body, int offset, nint method, nint stub) =>
        WriteBranch(body, offset, method, stub, 0xE8, Errors.ExpectedNativeCALL);

    public static void WriteJump(byte[] body, int offset, nint method, nint stub) =>
        WriteBranch(body, offset, method, stub, 0xE9, Errors.ExpectedNativeTailJMP);

    private static void WriteBranch(byte[] body, int offset, nint method, nint stub, byte opcode, string error)
    {
        if (offset < 0 || offset > body.Length - 5 || body[offset] != opcode)
        {
            throw new InvalidOperationException(error);
        }

        var displacement = checked((int)(stub - (long)method - offset - 5));
        BitConverter.GetBytes(displacement).CopyTo(body, offset + 1);
    }
}

internal static class NativeMemory
{
    internal static byte[] Read(nint address, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(address, bytes, 0, length);
        return bytes;
    }

    internal static void WriteCode(nint address, byte[] bytes)
    {
        if (!VirtualProtect(address, (nuint)bytes.Length, 0x40, out var previous))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);
            if (!FlushInstructionCache(GetCurrentProcess(), address, (nuint)bytes.Length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (!VirtualProtect(address, (nuint)bytes.Length, previous, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    internal static nint AllocateCode(byte[] code)
    {
        var address = VirtualAlloc(0, (nuint)code.Length, 0x3000, 0x04);
        if (address == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            Marshal.Copy(code, 0, address, code.Length);
            if (!VirtualProtect(address, (nuint)code.Length, 0x20, out _) ||
                !FlushInstructionCache(GetCurrentProcess(), address, (nuint)code.Length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return address;
        }
        catch
        {
            VirtualFree(address, 0, 0x8000);
            throw;
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    internal static extern nint VirtualAlloc(nint address, nuint size, uint allocation, uint protection);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool VirtualFree(nint address, nuint size, uint type);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport("kernel32")]
    internal static extern nint GetCurrentProcess();
}

public static class NativeSupport
{
    private static nint _library;
    private static bool _extendedInitialized;
    private static bool _dispatchInitialized;
    public static nint Export(string name) => NativeLibrary.GetExport(_library, name);

    public static T GetDelegate<T>(string name)
        where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Export(name));

    public static void Initialize(PatchGuard guard)
    {
        if (_library != 0)
        {
            return;
        }

        guard.VerifyHelpers();
        VerifyHandleAbi();
        var loaded = LoadLibrary();
        nint game = NativeLibrary.Load("GameAssembly.dll");
        var config = new Config
        {
            ObjectNew = RuntimeExport(game, "object_new"),
            HandleNew = RuntimeExport(game, "gchandle_new"),
            WeakNew = RuntimeExport(game, "gchandle_new_weakref"),
            HandleTarget = RuntimeExport(game, "gchandle_get_target"),
            HandleFree = RuntimeExport(game, "gchandle_free"),
            ClassSize = RuntimeExport(game, "class_instance_size"),
            Barrier = RuntimeExport(game, "gc_wbarrier_set_field")
        };
        // Only the original build has verified RVAs for these helpers.
        if (guard.Build == GameBuild.Original)
        {
            config.ComparerClass =
                Il2CppClassPointerStore.GetNativeClassPointer(
                    typeof(GameCorlib::Il2CppSystem.Func<PathSegmentInRange, PathSegmentInRange, int>));
            config.ComparerCtor = guard.Address(0x1D71910, 1);
            config.ListEnumerator = guard.Address(0x1964EF0, 1);
            config.LockEnumeratorCtor = guard.Address(0x1DB76F0, 1);
            config.BuffAddRange = guard.Address(0x19CCD70, 1);
            config.LockEnumeratorOriginal = guard.Address(0x1B4C830, 1);
            if (config.ComparerClass == 0)
            {
                throw new InvalidOperationException(Errors.NativePathComparerTypeUnavailable);
            }
        }

        Marshal.GetDelegateForFunctionPointer<Configure>(NativeLibrary.GetExport(loaded, "Configure"))(ref config);
        _library = loaded; // Detours may still reference this library.
    }

    private static nint LoadLibrary()
    {
        using var resource = typeof(NativeSupport).Assembly.GetManifestResourceStream("Btd6Patches.native.dll") ??
                             throw new InvalidOperationException(Errors.NativeHelperResourceMissing);
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var binary = bytes.ToArray();
        var digest = SHA256.HashData(binary);
        var hash = Convert.ToHexString(digest);
        var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "BTD6-Patches", "native");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"BTD6-Patches.Native-{hash}.dll");
        if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(digest))
        {
            File.WriteAllBytes(path, binary);
        }

        return NativeLibrary.Load(path);
    }

    public static void ClearCaches()
    {
        if (_library != 0)
        {
            GetDelegate<Clear>("ClearCaches")();
        }
    }

    public static void InitializeDispatch(PatchGuard guard)
    {
        if (_dispatchInitialized)
        {
            return;
        }

        var config = new DispatchConfig
        {
            Resolve = guard.Address(0x38B160, 1),
            Current = guard.Address(0x24F0B00, 1)
        };
        GetDelegate<ConfigureDispatch>("ConfigureDispatch")(ref config);
        _dispatchInitialized = true;
    }

    public static void InitializeExtended(PatchGuard guard)
    {
        if (_extendedInitialized)
        {
            return;
        }

        var config = new ExtendedConfig
        {
            FindZones = guard.Address(0xB1CE00, 1),
            BuffGetEnumerator = guard.Address(0x19B6730, 1),
            BuffMoveNext = guard.Address(0x24CA2C0, 1),
            BuffGetInfoSlot = guard.Address(0x4A69EC8, 8),
            BuffMoveInfoSlot = guard.Address(0x49D0FC0, 8)
        };
        GetDelegate<ConfigureExtended>("ConfigureExtended")(ref config);
        _extendedInitialized = true;
    }

    private static void VerifyHandleAbi()
    {
        // GC handle widths can differ across Unity versions.
        VerifyHandleMethod("il2cpp_gchandle_new", typeof(nint), typeof(nint), typeof(bool));
        VerifyHandleMethod("il2cpp_gchandle_new_weakref", typeof(nint), typeof(nint), typeof(bool));
        VerifyHandleMethod("il2cpp_gchandle_get_target", typeof(nint), typeof(nint));
        VerifyHandleMethod("il2cpp_gchandle_free", typeof(void), typeof(nint));
    }

    private static nint RuntimeExport(nint game, string name) => NativeLibrary.GetExport(game, "il2cpp_" + name);

    private static void VerifyHandleMethod(string name, Type result, params Type[] parameters)
    {
        var method = typeof(IL2CPP).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == name);
        if (method.ReturnType != result ||
            !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters))
        {
            throw new InvalidOperationException(Errors.UnsupportedNativeGCHandleABI + name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Config
    {
        public nint ObjectNew, HandleNew, WeakNew, HandleTarget, HandleFree, ClassSize, Barrier;

        public nint ComparerClass,
            ComparerCtor,
            ListEnumerator,
            LockEnumeratorCtor,
            BuffAddRange,
            LockEnumeratorOriginal;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Configure(ref Config config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Clear();

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatchConfig
    {
        public nint Resolve, Current;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureDispatch(ref DispatchConfig config);

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedConfig
    {
        public nint FindZones, BuffGetEnumerator, BuffMoveNext, BuffGetInfoSlot, BuffMoveInfoSlot;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigureExtended(ref ExtendedConfig config);
}

public static class InactiveBuffCode
{
    // RCX=tower (R14), RDX=support (RDI), R8=original class owner (R13).
    public static byte[] ArgumentSetup => Convert.FromHexString("4C89F14889FA4D89E8");

    public static byte[] Create(int length, nint address, nint stub)
    {
        return length != 0x52
            ? throw new InvalidOperationException(Errors.UnexpectedInactiveIconBlockLength)
            : NativeCode.CallAndContinue(length, address, stub);
    }
}

public static class GridPositionCode
{
    public static byte[] Create(nint address, nint helper) => NativeCode.CallAndContinue(0x43, address, helper,
        Convert.FromHexString("660F6EF048C1E820660F6EC031D2"));
}

public static class EntityPositionCode
{
    // RCX=live behavior in RBX; RDX=original caller's scratch at RSP+20.
    // A leaf stub's stack is eight bytes below the caller after its CALL.
    public static byte[] ArgumentSetup => Convert.FromHexString("4889D9488D542428");

    public static byte[] Create(nint address, nint stub)
    {
        // Preserve the first position result in RDI.
        return NativeCode.CallAndContinue(48, address, stub, Convert.FromHexString("488B7C2420"));
    }
}

public static class NativeSimulationCode
{
    public static byte[] Comparer(byte[] original, nint address, nint helper)
    {
        var body = (byte[])original.Clone();
        foreach (var offset in new[]
                 {
                     0x77F,
                     0x7E9,
                     0x805,
                     0x821
                 }
                )
        {
            RequireCall(body, offset);
            body.AsSpan(offset, 5).Fill(0x90);
        }

        if (body.Length < 0x83E)
        {
            throw new ArgumentException(Errors.TruncatedComparerBody);
        }

        // RSI receives a delegate bound to the original Attack and method.
        var block = Convert.FromHexString("4C89C24889F9E8000000004889C6909090");
        NativeCallStub.WriteCall(block, 6, address + 0x82D, helper);
        block.CopyTo(body, 0x82D);
        return body;
    }

    public static byte[] BuffConsumer(byte[] original, nint address, nint acquire, nint consume, bool other = true,
        bool discount = true)
    {
        var body = (byte[])original.Clone();
        if (discount)
        {
            NativeCallStub.WriteCall(body, 0x6FE, address, acquire);
            NativeCallStub.WriteCall(body, 0x739, address, consume);
        }

        if (!other)
        {
            return body;
        }

        NativeCallStub.WriteCall(body, 0xFD9, address, acquire);
        NativeCallStub.WriteCall(body, 0x100C, address, consume);
        return body;
    }

    public static byte[] AchievementCount(byte[] original, nint address, nint helper)
    {
        if (original.Length < 0x21B)
        {
            throw new ArgumentException(Errors.TruncatedAchievementCounter);
        }

        var body = (byte[])original.Clone();
        // RDX holds the BuffQuery list; EAX returns the count for unchecked 32-bit addition.
        var block = Convert.FromHexString("4889D131D2E80000000001C789BC2400010000E900000000");
        NativeCallStub.WriteCall(block, 5, address + 0x187, helper);
        BitConverter.GetBytes(checked(0x140 - (0x187 + block.Length))).CopyTo(block, 20);
        body.AsSpan(0x187, 0x21B - 0x187).Fill(0x90);
        block.CopyTo(body, 0x187);
        return body;
    }

    public static byte[] BuffEnumerator(byte[] original, nint address, nint helper)
    {
        var body = (byte[])original.Clone();
        foreach (var offset in new[]
                 {
                     0x136,
                     0x3D7,
                     0x605
                 }
                )
        {
            NativeCallStub.WriteCall(body, offset, address, helper);
        }

        return body;
    }

    private static void RequireCall(byte[] body, int offset)
    {
        if (offset < 0 || offset > body.Length - 5 || body[offset] != 0xE8)
        {
            throw new ArgumentException(Errors.ExpectedAllocationCALL);
        }
    }
}

public static class CollisionCallbackDispatchCode
{
    public const long Start = 0xB982D4;
    public const int Length = 0x79;

    public static byte[] Create(byte[] original, nint address, nint helper)
    {
        if (original.Length != Length || !original.AsSpan(0, 3).SequenceEqual(Convert.FromHexString("4C8B10")))
        {
            throw new ArgumentException(Errors.UnexpectedCollisionCallbackReceiverWindow);
        }

        // RSI holds the callback formerly in RAX; RDI is the projectile, R14 the bloon.
        return NativeDispatchCode.InterfaceSites(original, address, helper, new[] { (0, 0x44, 23, 0x58) }, 0x16);
    }
}

public static class NativeDispatchCode
{
    // RDI holds the object; R9 holds the interface type.
    public static byte[] InterfaceSites(byte[] original, nint address, nint helper,
        (int Start, int Resume, int Slot, int Direct)[] sites, byte receiver = 0x17)
    {
        var body = (byte[])original.Clone();
        var previous = -1;
        foreach (var site in sites)
        {
            int call = site.Resume - 8, fallback = call - (site.Slot == 0 ? 9 : 12);
            if (site.Start < previous || site.Resume > body.Length || fallback - site.Start < 47 || site.Direct < 0 ||
                site.Direct > body.Length - 3 || (site.Slot != 0 && site.Slot != 23 && site.Slot != 32) ||
                !(body.AsSpan(site.Start, 3).SequenceEqual(new byte[] { 0x4C, 0x8B, 0x17 }) ||
                  body.AsSpan(site.Start, 3).SequenceEqual(new byte[] { 0x4C, 0x8B, 0x10 })))
            {
                throw new ArgumentException(Errors.UnexpectedInterfaceDispatchSite);
            }

            // Try the first interface inline. Tables with >=16 entries use the cached
            // fallback through the original CALL site to preserve exception unwinding.
            var block = Convert.FromHexString(
                "4C8B17410FB7922E0100004D8B82B000000031C939D17300488D04094D390CC00F8400000000FFC183FA107300EBE5");
            if (receiver != 0x17 && receiver != 0x16)
            {
                throw new ArgumentException(Errors.UnsupportedReceiverRegister);
            }

            block[2] = receiver;
            block[23] = checked((byte)(fallback - site.Start - 24));
            block[44] = checked((byte)(fallback - site.Start - 45));
            BitConverter.GetBytes(site.Direct - site.Start - 38).CopyTo(block, 34);
            body.AsSpan(site.Start, fallback - site.Start).Fill(0x90);
            block.CopyTo(body, site.Start);
            previous = site.Resume;
            NativeCallStub.WriteCall(body, call, address, helper);
        }

        return body;
    }
}

public static class NativeReturnCode
{
    // Windows x64 returns pointers in RAX.
    public static byte[] Create(nint value)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var code = new byte[11];
        code[0] = 0x48;
        code[1] = 0xB8;
        BinaryPrimitives.WriteInt64LittleEndian(code.AsSpan(2, 8), value);
        code[10] = 0xC3;
        return code;
    }
}

public static class NativeSelectorCode
{
    // The caller expects its delegate in RBX.
    public static byte[] Create(nint selector, int length)
    {
        if (selector == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selector));
        }

        if (length < 10)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var code = NativeCode.Nops(length);
        code[0] = 0x48;
        code[1] = 0xBB;
        BinaryPrimitives.WriteInt64LittleEndian(code.AsSpan(2, 8), selector);
        return code;
    }
}

public static class NativeInitializedBranchCode
{
    // After class initialization, replace its comparison and short JNZ without moving code.
    public static byte[] Create(byte[] original, int[] compares, int[] branches)
    {
        if (compares.Length == 0 || compares.Length != branches.Length)
        {
            throw new ArgumentException(Errors.InvalidInitializationGateCount);
        }

        var result = (byte[])original.Clone();
        var previousEnd = -1;
        for (var i = 0; i < compares.Length; i++)
        {
            int compare = compares[i], branch = branches[i];
            if (compare < previousEnd || compare < 0 || compare > original.Length - 7 || branch < compare + 7 ||
                branch > original.Length - 2 ||
                !(original.AsSpan(compare, 7).SequenceEqual(new byte[] { 0x83, 0xB9, 0xE4, 0, 0, 0, 0 }) ||
                  original.AsSpan(compare, 7).SequenceEqual(new byte[] { 0x83, 0xB8, 0xE4, 0, 0, 0, 0 })) ||
                original[branch] != 0x75 || original[branch + 1] >= 0x80 ||
                branch + 2 + original[branch + 1] >= original.Length)
            {
                throw new ArgumentException(Errors.UnexpectedOrOverlappingInitializationGate);
            }

            new byte[]
            {
                0x0F,
                0x1F,
                0x80,
                0,
                0,
                0,
                0
            }.CopyTo(result, compare);
            result[branch] = 0xEB;
            previousEnd = branch + 2;
        }

        return result;
    }
}

internal static class NativeCode
{
    internal static byte[] Nops(int length)
    {
        var code = new byte[length];
        code.AsSpan().Fill(0x90);
        return code;
    }

    internal static byte[] CallAndContinue(int length, nint address, nint helper, byte[]? returnSetup = null,
        nint continuation = 0)
    {
        returnSetup ??= Array.Empty<byte>();
        var code = Nops(length);
        var jump = 5 + returnSetup.Length;
        code[0] = 0xE8;
        returnSetup.CopyTo(code, 5);
        code[jump] = 0xE9;
        NativeCallStub.WriteCall(code, 0, address, helper);
        NativeCallStub.WriteJump(code, jump, address, continuation == 0 ? address + length : continuation);
        return code;
    }
}
