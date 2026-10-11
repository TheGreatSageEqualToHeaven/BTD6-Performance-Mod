using Btd6Patches;

[assembly: MelonInfo(typeof(PatchesMod), "BTD6 Patches", "0.8.6", "btd-optimize")]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]

namespace Btd6Patches;

public sealed class PatchesMod : MelonMod
{
    private readonly List<IDisposable> _applied = new();
    private PatchSet? _patches;

    public override void OnInitializeMelon()
    {
        try
        {
            var config = PatchConfigStore.Load(Path.Combine(MelonEnvironment.UserDataDirectory, "BTD6-Patches.json"),
                LoggerInstance.Msg);
            _patches = new PatchSet(LoggerInstance);
            _patches.Apply(config, TryApply);
        }
        catch (Exception exception)
        {
            LoggerInstance.Error(string.Format(Errors.PatchSetupFailed, exception));
        }
    }

    public override void OnUpdate() => _patches?.Tick();

    public override void OnDeinitializeMelon()
    {
        if (_patches == null)
        {
            return;
        }

        foreach (var patch in Enumerable.Reverse(_applied))
        {
            try
            {
                patch.Dispose();
            }
            catch (Exception exception)
            {
                LoggerInstance.Error(Errors.PatchDetachFailed + exception.Message);
            }
        }

        _applied.Clear();
        NativeSupport.ClearCaches();
        _patches = null;
    }

    private bool TryApply(string name, Func<IDisposable> apply)
    {
        try
        {
            _applied.Add(apply());
            LoggerInstance.Msg($"{name}: performance patch enabled.");
            return true;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning(string.Format(Errors.PatchSkipped, name, exception.Message));
            return false;
        }
    }
}
