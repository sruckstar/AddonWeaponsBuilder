using System.Text;
using System.Text.RegularExpressions;
using Awb.Core.Rpf;
using Awb.Core.Util;

namespace Awb.Core;

/// <summary>
/// Installs a finished dlc.rpf into the GTA V game folder, mirroring a manual Add-On
/// install through the mods folder:
/// <code>
/// &lt;game&gt;/OpenIV.asi + dinput8.dll | DSOUND.dll    a mods-folder plugin (installed if none is there)
/// &lt;game&gt;/mods/                                   created if missing
/// &lt;game&gt;/mods/update/update.rpf                     copied from the game on first install
/// &lt;game&gt;/mods/update/update.rpf/common/data/dlclist.xml   gets &lt;Item&gt;dlcpacks:/&lt;DLC&gt;/&lt;/Item&gt;
/// &lt;game&gt;/mods/update/x64/dlcpacks/&lt;DLC&gt;/dlc.rpf    the build is copied here
/// </code>
/// update.rpf may be an unpacked folder (the file is edited on disk) or a real RPF7
/// archive (dlclist.xml is rewritten in place inside it). A fresh copy of the game's
/// update.rpf is still encrypted by the game: it is switched to OPEN on the first edit,
/// with the keys read from the game executable — the same thing OpenIV/CodeWalker do.
/// </summary>
public static partial class GameInstaller
{
    public const string DlclistInner = "common/data/dlclist.xml";

    /// <summary>Plugins that give the game a mods folder — any one of them is enough.</summary>
    public static readonly string[] ModFolderPlugins = ["OpenIV.asi", "DSOUND.dll", "OpenRPF.asi", "RageOpenV.asi"];

    /// <summary>
    /// Proxy DLLs that load *.asi plugins (ScriptHookV / Ultimate ASI Loader names).
    /// dsound.dll is not among them: in a GTA V folder that name is the DSOUND mods loader.
    /// </summary>
    public static readonly string[] AsiLoaders =
        ["dinput8.dll", "xinput1_4.dll", "version.dll", "winmm.dll", "winhttp.dll", "d3d11.dll"];

    /// <summary>
    /// The mods-folder plugin shipped in data/plugins for an edition: OpenIV.asi for Legacy;
    /// for Enhanced the DSOUND.dll mods loader, a self-loading proxy that needs no ASI loader.
    /// </summary>
    public static string BundledPlugin(GameEdition e) => e == GameEdition.Enhanced ? "DSOUND.dll" : "OpenIV.asi";

    /// <summary>The ASI loader shipped in data/plugins for an edition (Alexander Blade's GTA V / GTA V Enhanced loader).</summary>
    public static string BundledAsiLoader(GameEdition e) => e == GameEdition.Enhanced ? "xinput1_4.dll" : "dinput8.dll";

    /// <summary>Plugins that can't serve an edition's mods folder (OpenIV.asi predates Enhanced).</summary>
    private static bool Serves(string plugin, GameEdition e) =>
        !(e == GameEdition.Enhanced && plugin.Equals("OpenIV.asi", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"([ \t]*)</Paths>")] private static partial Regex PathsCloseRe();
    [GeneratedRegex(@"\n([ \t]*)<Item>")] private static partial Regex ItemIndentRe();

    // ================================================================ game preparation

    /// <summary>
    /// The edition to build for when installing into <paramref name="gameDir"/>: the
    /// requested one (with a warning if the folder runs the other), else the one its
    /// executable names. A folder with no executable (a bare mods tree) counts as Legacy.
    /// </summary>
    /// <param name="requested">forced edition, or null to take it from the executable</param>
    public static GameEdition ResolveEdition(string gameDir, GameEdition? requested, Action<string> log)
    {
        if (!Directory.Exists(gameDir))
            throw new DirectoryNotFoundException($"Game folder not found: {gameDir}");

        var detected = GameEditions.Detect(gameDir);
        GameEdition edition;
        if (requested is { } r)
        {
            edition = r;
            if (detected is { } d && d != r)
                log($"    [!] The game folder runs {d.DisplayName()} ({d.ExeName()}), but the pack is " +
                    $"built for {r.DisplayName()} — the game won't load it correctly.");
        }
        else if (detected is { } d)
            edition = d;
        else if (GameEditions.IsAmbiguous(gameDir))
            throw new InvalidOperationException(
                $"The game folder holds both {GameEditions.LegacyExe} and {GameEditions.EnhancedExe} — " +
                "choose the game version (Legacy / Enhanced) explicitly.");
        else
            edition = GameEdition.Legacy;               // not a recognisable install (e.g. a bare mods tree)
        return edition;
    }

    /// <summary>
    /// Make the game able to take add-on packs: a mods-folder plugin (the bundled
    /// OpenIV.asi + ASI loader / DSOUND.dll when the game has none), the mods folder, and
    /// mods/update/update.rpf copied from the game. Idempotent.
    /// </summary>
    /// <param name="pluginsDir">folder with the bundled plugins (data/plugins)</param>
    public static void PrepareGame(string gameDir, GameEdition edition, string pluginsDir, Action<string> log)
    {
        // only touch real game installs: a folder without the executable gets no plugin
        if (GameEditions.Detect(gameDir) is not null || GameEditions.IsAmbiguous(gameDir))
            EnsureModFolderPlugin(gameDir, edition, pluginsDir, log);
        EnsureModsFolder(gameDir, log);
        EnsureUpdateRpf(gameDir, log);
    }

    /// <summary>
    /// Install the bundled mods-folder plugin when the game has none of
    /// <see cref="ModFolderPlugins"/> that works for its edition, and the bundled ASI loader
    /// when the plugin is an .asi and nothing in the folder would load it.
    /// </summary>
    public static void EnsureModFolderPlugin(string gameDir, GameEdition edition, string pluginsDir, Action<string> log)
    {
        var present = ModFolderPlugins.Where(p => File.Exists(Path.Combine(gameDir, p))).ToList();
        var usable = present.Where(p => Serves(p, edition)).ToList();
        if (usable.Count == 0)
        {
            var plugin = BundledPlugin(edition);
            if (present.Count > 0)
                log($"    {string.Join(" / ", present)} can't load the mods folder of {edition.DisplayName()}.");
            CopyBundled(gameDir, pluginsDir, plugin,
                        $"The game has no mods-folder plugin ({string.Join(", ", ModFolderPlugins)})");
            log($"    Installed {plugin} into the game folder — it lets {edition.DisplayName()} load the mods folder.");
            usable.Add(plugin);
        }

        // an .asi does nothing on its own: something has to load it (DSOUND.dll loads itself)
        bool selfLoading = usable.Any(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (selfLoading || AsiLoaders.Any(l => File.Exists(Path.Combine(gameDir, l)))) return;

        var loader = BundledAsiLoader(edition);
        if (!File.Exists(Path.Combine(pluginsDir, loader)))
        {
            log($"    [!] No ASI loader in the game folder — {string.Join(" / ", usable)} will not be loaded " +
                $"and the game will ignore the mods folder. Install ScriptHookV or Ultimate ASI Loader " +
                $"({loader} for {edition.DisplayName()}).");
            return;
        }
        CopyBundled(gameDir, pluginsDir, loader, "No ASI loader in the game folder");
        log($"    Installed the ASI loader {loader} — it loads {string.Join(" / ", usable)} into {edition.DisplayName()}.");
    }

    private static void CopyBundled(string gameDir, string pluginsDir, string name, string why)
    {
        var src = Path.Combine(pluginsDir, name);
        if (!File.Exists(src))
            throw new FileNotFoundException(
                $"{why} and the bundled {name} is missing: {src}. Reinstall AddonWeapons Builder.");
        PathUtil.Copy2(src, Path.Combine(gameDir, name));
    }

    public static void EnsureModsFolder(string gameDir, Action<string> log)
    {
        var mods = Path.Combine(gameDir, "mods");
        if (Directory.Exists(mods)) return;
        Directory.CreateDirectory(mods);
        log($"    Created the mods folder: {mods}");
    }

    /// <summary>Copy the game's update\update.rpf into mods (first install only).</summary>
    public static void EnsureUpdateRpf(string gameDir, Action<string> log)
    {
        var modsUpd = Path.Combine(gameDir, "mods", "update", "update.rpf");
        if (File.Exists(modsUpd) || Directory.Exists(modsUpd)) return;

        var gameUpd = Path.Combine(gameDir, "update", "update.rpf");
        if (!File.Exists(gameUpd))
            throw new FileNotFoundException(
                $"update.rpf not found: neither in mods ({modsUpd}) nor in the game ({gameUpd}).\n" +
                "Check that the selected folder is the GTA V game folder.");

        long size = new FileInfo(gameUpd).Length;
        var root = Path.GetPathRoot(Path.GetFullPath(modsUpd));
        if (!string.IsNullOrEmpty(root))
        {
            try
            {
                long free = new DriveInfo(root).AvailableFreeSpace;
                if (free < size + (256L << 20))
                    throw new IOException(
                        $"Not enough disk space to copy update.rpf into mods: it needs {MergedPack.FmtSize(size)}, " +
                        $"{MergedPack.FmtSize(free)} free on {root}.");
            }
            catch (ArgumentException) { /* not a local drive — just try */ }
        }

        log($"    Copying update\\update.rpf into mods ({MergedPack.FmtSize(size)}) — first install only, this takes a while…");
        Directory.CreateDirectory(Path.GetDirectoryName(modsUpd)!);
        var tmp = modsUpd + ".tmp";
        try
        {
            File.Copy(gameUpd, tmp, overwrite: true);
            File.Move(tmp, modsUpd);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        log($"    update.rpf copied -> {modsUpd}");
    }

    // ================================================================ dlclist.xml

    /// <summary>dlclist.xml text with the pack entry added, or null if already listed.</summary>
    public static string? DlclistWithEntry(string text, string dlcName, Action<string> log)
    {
        var entryPath = $"dlcpacks:/{dlcName}/";
        var entryLine = $"<Item>{entryPath}</Item>";

        if (Regex.IsMatch(text, @"<Item>\s*" + Regex.Escape(entryPath) + @"\s*</Item>"))
        {
            log($"    dlclist.xml: '{entryPath}' already present — skipping.");
            return null;
        }

        var m = PathsCloseRe().Match(text);
        if (!m.Success)
            throw new InvalidDataException(
                "Could not find the <Paths></Paths> section in dlclist.xml — " +
                "the file doesn't look like dlclist.xml.");

        var im = ItemIndentRe().Match(text);
        var indent = im.Success ? im.Groups[1].Value : m.Groups[1].Value + "  ";
        var insertion = $"{indent}{entryLine}\n{m.Groups[1].Value}</Paths>";
        log($"    dlclist.xml: added '{entryLine}'.");
        return text[..m.Index] + insertion + text[(m.Index + m.Length)..];
    }

    private static void AddToDlclistFile(string dlclist, string dlcName, Action<string> log)
    {
        var text = TextIo.ReadText(dlclist);
        var updated = DlclistWithEntry(text, dlcName, log);
        if (updated is not null) TextIo.WriteText(dlclist, updated);
    }

    /// <summary>Run <paramref name="op"/> without keys first; load them from the game only when the archive needs them.</summary>
    private static T WithKeys<T>(string gameDir, Action<string> log, ref GameCrypto? crypto, Func<GameCrypto?, T> op)
    {
        try
        {
            return op(crypto);
        }
        catch (RpfEncryptedException) when (crypto is null)
        {
            crypto = GameCrypto.ForGame(gameDir, log);
            return op(crypto);
        }
    }

    private static void AddToDlclistRpf(string gameDir, string updateRpf, string dlcName, Action<string> log)
    {
        GameCrypto? crypto = null;
        var raw = WithKeys(gameDir, log, ref crypto, c => RpfTools.ReadInnerFile(updateRpf, DlclistInner, c));
        var text = TextIo.DecodeUtf8Sig(raw);
        var updated = DlclistWithEntry(text, dlcName, log);
        if (updated is null) return;

        var info = RpfTools.PatchInnerFile(updateRpf, DlclistInner, Encoding.UTF8.GetBytes(updated), crypto);
        if (info.Opened)
            log("    update.rpf switched from the game's encryption to OPEN (as OpenIV does on the first edit).");
        log($"    dlclist.xml rewritten in update.rpf ({info.Uncompressed} bytes, {info.OnDisk} compressed" +
            (info.Moved ? ", moved to the end of the archive" : "") + ").");
    }

    /// <summary>The game's own dlclist.xml, read out of its encrypted update\update.rpf.</summary>
    private static string GameDlclist(string gameDir, Action<string> log)
    {
        var gameUpd = Path.Combine(gameDir, "update", "update.rpf");
        if (!File.Exists(gameUpd))
            throw new FileNotFoundException($"update.rpf not found in the game: {gameUpd}");
        GameCrypto? crypto = null;
        var raw = WithKeys(gameDir, log, ref crypto, c => RpfTools.ReadInnerFile(gameUpd, DlclistInner, c));
        return TextIo.DecodeUtf8Sig(raw);
    }

    /// <summary>
    /// Add <c>dlcpacks:/&lt;dlcName&gt;/</c> to the game's dlclist.xml (no-op if listed).
    /// Separate from the copy so an unchanged pack can be re-registered cheaply.
    /// </summary>
    /// <exception cref="FileNotFoundException">mods, update.rpf or dlclist.xml is missing</exception>
    public static void RegisterInDlclist(string gameDir, string dlcName, Action<string> log)
    {
        var mods = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(mods))
            throw new FileNotFoundException(
                $"'mods' folder not found in the game folder: {mods}\n" +
                "Add-On installation is only supported via the mods folder.");

        var updateRpf = Path.Combine(mods, "update", "update.rpf");
        if (Directory.Exists(updateRpf))
        {
            var dlclist = Path.Combine(updateRpf, "common", "data", "dlclist.xml");
            if (!File.Exists(dlclist))
            {
                // a loose-file update.rpf (override folder) without its own dlclist.xml:
                // start it from the game's copy
                if (!File.Exists(Path.Combine(gameDir, "update", "update.rpf")))
                    throw new FileNotFoundException(
                        $"dlclist.xml file not found: {dlclist}\n" +
                        "Check that update.rpf was unpacked into mods via OpenIV.");
                var text = GameDlclist(gameDir, log);
                Directory.CreateDirectory(Path.GetDirectoryName(dlclist)!);
                TextIo.WriteText(dlclist, text);
                log($"    dlclist.xml taken from the game's update.rpf -> {dlclist}");
            }
            AddToDlclistFile(dlclist, dlcName, log);
        }
        else if (File.Exists(updateRpf))
        {
            log($"    update.rpf is an archive, editing dlclist.xml inside: {updateRpf}");
            AddToDlclistRpf(gameDir, updateRpf, dlcName, log);
        }
        else
        {
            throw new FileNotFoundException(
                $"update.rpf not found (neither folder nor archive): {updateRpf}\n" +
                "Check the mods/update structure in the game folder.");
        }
    }

    /// <summary>Install a built dlc.rpf into the game; returns the installed path.</summary>
    public static string InstallToGame(string gameDir, string dlcRpf, string dlcName, Action<string> log)
    {
        log($"Installing into game: {gameDir}");
        RegisterInDlclist(gameDir, dlcName, log);
        var destDir = Path.Combine(gameDir, "mods", "update", "x64", "dlcpacks", dlcName);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, "dlc.rpf");
        PathUtil.Copy2(dlcRpf, dest);
        log($"    dlc.rpf copied -> {dest}");
        log("Add-On installed. Launch the game and check the weapon in the shop.");
        return dest;
    }
}
