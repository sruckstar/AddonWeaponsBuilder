using System.Text.Json.Serialization;
using Awb.Core.Util;

namespace Awb.Core;

/// <summary>How an installed weapon reached the game.</summary>
public enum ModKind
{
    /// <summary>Its own dlcpack (mods/update/x64/dlcpacks/&lt;name&gt;).</summary>
    Pack,
    /// <summary>A weapon inside the shared AddonWeapons[N] pack.</summary>
    Merged,
}

/// <summary>One weapon (or standalone pack) this builder installed into a game.</summary>
/// <param name="Id">stable key for <see cref="ModChange"/>: <c>pack:&lt;folder&gt;</c> or <c>merged:&lt;suffix&gt;</c></param>
/// <param name="Pack">the dlcpack folder that holds it</param>
public sealed record InstalledMod(string Id, string Name, ModKind Kind, string Pack, bool Enabled);

/// <summary>What to do with one installed weapon: switch it on/off, or remove it (wins over the switch).</summary>
public sealed record ModChange(string Id, bool Enable, bool Remove = false);

/// <summary>A standalone pack the builder installed, as kept in the game's registry file.</summary>
public sealed class RegisteredPack
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("edition")] public string Edition { get; set; } = "";
    [JsonPropertyName("installed")] public DateTime Installed { get; set; }
}

public sealed class ModRegistry
{
    [JsonPropertyName("packs")] public Dictionary<string, RegisteredPack> Packs { get; set; } = [];
}

/// <summary>
/// The weapons this builder installed into a game, and switching them on/off or removing
/// them. Two kinds, handled differently:
/// <list type="bullet">
///   <item><b>standalone dlcpacks</b> — recorded in <c>mods/AddonWeaponsBuilder.json</c>;
///   switched off by taking the pack out of dlclist.xml (and moving it to dlcpacks_disabled),
///   removed by deleting it;</item>
///   <item><b>weapons of the shared AddonWeapons pack</b> — the staged pack's manifest is the
///   record; a switched-off weapon stays in the pack but content.xml doesn't list its metas;
///   a removed one is dropped with its models. Either way the pack is rebuilt and reinstalled.</item>
/// </list>
/// </summary>
public static class InstalledMods
{
    public const string RegistryFile = "AddonWeaponsBuilder.json";

    public static string RegistryPath(string gameDir) => Path.Combine(gameDir, "mods", RegistryFile);

    private static ModRegistry LoadRegistry(string gameDir)
    {
        var path = RegistryPath(gameDir);
        if (!File.Exists(path)) return new ModRegistry();
        try
        {
            return TextIo.FromJson<ModRegistry>(File.ReadAllText(path)) ?? new ModRegistry();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return new ModRegistry();
        }
    }

    private static void SaveRegistry(string gameDir, ModRegistry reg) => TextIo.WriteJson(RegistryPath(gameDir), reg);

    /// <summary>Record a standalone pack that was just installed (replacing an earlier record of it).</summary>
    public static void RegisterPack(string gameDir, string dlcName, string displayName, GameEdition edition)
    {
        if (MergedPack.FolderIndex(dlcName) is not null) return;       // the shared pack keeps its own record
        var reg = LoadRegistry(gameDir);
        reg.Packs[dlcName] = new RegisteredPack
        {
            Name = displayName, Edition = edition == GameEdition.Enhanced ? "enhanced" : "legacy",
            Installed = DateTime.UtcNow,
        };
        SaveRegistry(gameDir, reg);
    }

    private static bool PackOn(string gameDir, string dlcName) =>
        File.Exists(Path.Combine(GameInstaller.PackDir(gameDir, dlcName), "dlc.rpf"));

    private static bool PackOff(string gameDir, string dlcName) =>
        File.Exists(Path.Combine(GameInstaller.DisabledPackDir(gameDir, dlcName), "dlc.rpf"));

    /// <summary>
    /// Everything the builder installed into <paramref name="gameDir"/>: its standalone packs
    /// still on disk (on or off) and the weapons of each shared pack staged in
    /// <paramref name="stagingDir"/> that is installed in this game. Reads only.
    /// </summary>
    public static List<InstalledMod> List(string gameDir, string stagingDir)
    {
        var list = new List<InstalledMod>();
        if (!Directory.Exists(Path.Combine(gameDir, "mods"))) return list;

        foreach (var (folder, p) in LoadRegistry(gameDir).Packs)
        {
            bool on = PackOn(gameDir, folder);
            if (!on && !PackOff(gameDir, folder)) continue;              // deleted by hand
            list.Add(new InstalledMod($"pack:{folder}", p.Name.Length > 0 ? p.Name : folder, ModKind.Pack, folder, on));
        }

        if (Directory.Exists(stagingDir))
        {
            foreach (var pack in new MergedPackSet(stagingDir).AllPacks())
            {
                if (!PackOn(gameDir, pack.FolderName)) continue;         // staged for another game folder
                foreach (var (suffix, w) in pack.Data.Weapons)
                    list.Add(new InstalledMod($"merged:{suffix}", w.Name.Length > 0 ? w.Name : suffix, ModKind.Merged,
                                              pack.FolderName, !w.Disabled));
            }
        }
        return list.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Apply switches and removals. Standalone packs are handled on disk right away; the
    /// shared packs that changed are rebuilt, self-checked and reinstalled once at the end
    /// (one that lost its last weapon is removed from the game).
    /// </summary>
    public static void Apply(string gameDir, string stagingDir, GameEdition edition,
                             IReadOnlyCollection<ModChange> changes, Action<string> log)
    {
        if (changes.Count == 0) return;
        var reg = LoadRegistry(gameDir);
        bool regChanged = false;

        var merged = new List<ModChange>();
        foreach (var c in changes)
        {
            if (c.Id.StartsWith("merged:", StringComparison.Ordinal))
            {
                merged.Add(c);
                continue;
            }
            if (!c.Id.StartsWith("pack:", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown installed weapon id: {c.Id}");
            var folder = c.Id["pack:".Length..];
            if (c.Remove)
            {
                GameInstaller.UninstallPack(gameDir, folder, log);
                regChanged |= reg.Packs.Remove(folder);
            }
            else if (c.Enable)
                GameInstaller.EnablePack(gameDir, folder, log);
            else
                GameInstaller.DisablePack(gameDir, folder, log);
        }
        if (regChanged) SaveRegistry(gameDir, reg);

        if (merged.Count > 0) ApplyMerged(gameDir, stagingDir, edition, merged, log);
    }

    private static void ApplyMerged(string gameDir, string stagingDir, GameEdition edition,
                                    List<ModChange> changes, Action<string> log)
    {
        var ms = new MergedPackSet(stagingDir, log);
        ms.SyncWithGame(gameDir);
        var emptied = new HashSet<MergedPack>();
        foreach (var c in changes)
        {
            var suffix = c.Id["merged:".Length..];
            var pack = ms.OwnerOf(suffix);
            if (pack is null)
            {
                log($"    [!] Weapon '{suffix}' is no longer in any AddonWeapons pack — skipped.");
                continue;
            }
            if (c.Remove)
            {
                pack.RemoveWeapon(suffix);
                if (pack.IsEmpty) emptied.Add(pack);
            }
            else
                pack.SetEnabled(suffix, c.Enable);
        }

        // a pack left with no weapons at all goes from the game and from staging
        foreach (var pack in emptied)
        {
            GameInstaller.UninstallPack(gameDir, pack.FolderName, log);
            PathUtil.TryDeleteDir(pack.Root);
        }

        foreach (var b in ms.BuildAll(edition))
        {
            log($"Packed '{b.Folder}' into a single dlc.rpf ({b.Size} bytes, {b.Weapons.Count} weapon(s)).");
            Pipeline.VerifyPack(b.DlcRpf, log);
            GameInstaller.InstallToGame(gameDir, b.DlcRpf, b.Folder, log);
        }
    }
}
