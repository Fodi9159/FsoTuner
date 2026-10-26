using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace FsoTuner.Services;

public enum FsoStateKind
{
    Default,
    OnAll,
    OffStrong,
    OffPartial,
    Custom
}

public sealed class FsoSnapshot
{
    public int? FseBehavior { get; init; }
    public int? FseBehaviorMode { get; init; }
    public int? HonorUser { get; init; }
    public int? DxgiHonor { get; init; }
    public int? EfseFlags { get; init; }

    public FsoStateKind Kind { get; init; }
    public string StatusTitle { get; init; } = "";
    public string StatusDetail { get; init; } = "";
    public string RegistryText { get; init; } = "";
}

public static class FsoService
{
    private const string GameConfigStorePath = @"System\GameConfigStore";
    private const string LayersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    private static readonly string[] ValueNames =
    {
        "GameDVR_FSEBehavior",
        "GameDVR_FSEBehaviorMode",
        "GameDVR_HonorUserFSEBehaviorMode",
        "GameDVR_DXGIHonorFSEWindowsCompatible",
        "GameDVR_EFSEFeatureFlags"
    };

    public static FsoSnapshot Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(GameConfigStorePath, false);

        var fse = ReadInt(key, "GameDVR_FSEBehavior");
        var mode = ReadInt(key, "GameDVR_FSEBehaviorMode");
        var honor = ReadInt(key, "GameDVR_HonorUserFSEBehaviorMode");
        var dxgi = ReadInt(key, "GameDVR_DXGIHonorFSEWindowsCompatible");
        var efse = ReadInt(key, "GameDVR_EFSEFeatureFlags");

        var kind = Classify(fse, mode, honor, dxgi, efse);

        return new FsoSnapshot
        {
            FseBehavior = fse,
            FseBehaviorMode = mode,
            HonorUser = honor,
            DxgiHonor = dxgi,
            EfseFlags = efse,
            Kind = kind,
            StatusTitle = kind switch
            {
                FsoStateKind.Default => "FSO allowed · Windows default",
                FsoStateKind.OnAll => "FSO preferred · all games",
                FsoStateKind.OffStrong => "FSO off · strong",
                FsoStateKind.OffPartial => "FSO off · partial",
                _ => "Custom values"
            },
            StatusDetail = kind switch
            {
                FsoStateKind.Default => "Windows default values. FSO is allowed, not forced.",
                FsoStateKind.OnAll => "Explicit FSO-on preference for all fullscreen games; games can still ignore it.",
                FsoStateKind.OffStrong => "FSO disabled; DXGI path forced.",
                FsoStateKind.OffPartial => "FSO off requested; DXGI path not forced.",
                _ => "Values don't match a preset. Review them below."
            },
            RegistryText = FormatRegistry(fse, mode, honor, dxgi, efse)
        };
    }

    public static void WriteDefault()
    {
        using var key = Registry.CurrentUser.CreateSubKey(GameConfigStorePath, true);
        if (key is null)
        {
            throw new InvalidOperationException("Could not open HKCU\\System\\GameConfigStore for writing.");
        }

        foreach (var name in ValueNames)
        {
            key.DeleteValue(name, false);
        }
    }

    public static void WriteOnAll()
    {
        WriteValues(0, 1, 1, 0, 0);
    }

    public static void WriteOff()
    {
        WriteValues(2, 2, 1, 1, 0);
    }

    public static void WriteCustom(int fseBehavior, int fseBehaviorMode, int honorUser, int dxgiHonor, int efseFlags)
    {
        WriteValues(fseBehavior, fseBehaviorMode, honorUser, dxgiHonor, efseFlags);
    }

    private static void WriteValues(int fseBehavior, int fseBehaviorMode, int honorUser, int dxgiHonor, int efseFlags)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GameConfigStorePath, true);
        if (key is null)
        {
            throw new InvalidOperationException("Could not open HKCU\\System\\GameConfigStore for writing.");
        }

        key.SetValue("GameDVR_FSEBehavior", fseBehavior, RegistryValueKind.DWord);
        key.SetValue("GameDVR_FSEBehaviorMode", fseBehaviorMode, RegistryValueKind.DWord);
        key.SetValue("GameDVR_HonorUserFSEBehaviorMode", honorUser, RegistryValueKind.DWord);
        key.SetValue("GameDVR_DXGIHonorFSEWindowsCompatible", dxgiHonor, RegistryValueKind.DWord);
        key.SetValue("GameDVR_EFSEFeatureFlags", efseFlags, RegistryValueKind.DWord);
    }

    public static List<GameOverride> ReadGameOverrides()
    {
        var games = new List<GameOverride>();

        using var key = Registry.CurrentUser.OpenSubKey(LayersPath, false);
        if (key is null)
        {
            return games;
        }

        foreach (var valueName in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(valueName))
            {
                continue;
            }

            if (key.GetValue(valueName) is not string flags)
            {
                continue;
            }

            if (!flags.Contains(FsoFlag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            games.Add(new GameOverride
            {
                Path = valueName,
                DisplayName = GetDisplayName(valueName)
            });
        }

        return games
            .OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(game => game.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void AddGameOverride(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("That executable no longer exists.", path);
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select the game's .exe file.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(LayersPath, true);
        if (key is null)
        {
            throw new InvalidOperationException("Could not open the AppCompatFlags\\Layers key for writing.");
        }

        var tokens = ParseFlags(key.GetValue(path) as string);
        if (!tokens.Contains(FsoFlag, StringComparer.OrdinalIgnoreCase))
        {
            tokens.Add(FsoFlag);
        }

        key.SetValue(path, "~ " + string.Join(' ', tokens), RegistryValueKind.String);
    }

    public static void RemoveGameOverride(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(LayersPath, true);
        if (key is null)
        {
            return;
        }

        if (key.GetValue(path) is not string existing)
        {
            return;
        }

        var tokens = ParseFlags(existing);
        tokens.RemoveAll(token => token.Equals(FsoFlag, StringComparison.OrdinalIgnoreCase));

        if (tokens.Count == 0)
        {
            key.DeleteValue(path, false);
        }
        else
        {
            key.SetValue(path, "~ " + string.Join(' ', tokens), RegistryValueKind.String);
        }
    }

    private static List<string> ParseFlags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token != "~")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetDisplayName(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    return info.FileDescription;
                }

                if (!string.IsNullOrWhiteSpace(info.ProductName))
                {
                    return info.ProductName;
                }
            }
        }
        catch
        {
            // A missing or locked exe still gets a useful fallback name.
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static FsoStateKind Classify(int? fse, int? mode, int? honor, int? dxgi, int? efse)
    {
        var offFse = fse is null or 2;
        var knownEfse = efse is null or 0;

        if (fse == 0 && mode == 1 && honor == 1 && dxgi == 0 && knownEfse)
        {
            return FsoStateKind.OnAll;
        }

        if (mode == 2 && honor == 1 && offFse && knownEfse)
        {
            if (dxgi == 1)
            {
                return FsoStateKind.OffStrong;
            }

            if (dxgi is null or 0)
            {
                return FsoStateKind.OffPartial;
            }
        }

        var defaultish =
            (fse is null or 0) &&
            (mode is null or 0) &&
            (honor is null or 0) &&
            (dxgi is null or 0) &&
            (efse is null or 0);

        return defaultish ? FsoStateKind.Default : FsoStateKind.Custom;
    }

    private static string FormatRegistry(int? fse, int? mode, int? honor, int? dxgi, int? efse)
    {
        static string Value(int? value) => value?.ToString() ?? "(not set)";

        return string.Join(Environment.NewLine, new[]
        {
            $"GameDVR_FSEBehavior                 = {Value(fse)}",
            $"GameDVR_FSEBehaviorMode             = {Value(mode)}",
            $"GameDVR_HonorUserFSEBehaviorMode    = {Value(honor)}",
            $"GameDVR_DXGIHonorFSEWindowsCompatible = {Value(dxgi)}",
            $"GameDVR_EFSEFeatureFlags            = {Value(efse)}"
        });
    }

    private static int? ReadInt(RegistryKey? key, string name)
    {
        if (key?.GetValue(name) is int value)
        {
            return value;
        }

        return null;
    }
}
