using System.Text;
using System.Text.RegularExpressions;
using Awb.Core.Rpf;
using Awb.Core.Util;

namespace Awb.Core;

/// <summary>
/// Installs a finished dlc.rpf into the GTA V game folder, mirroring a manual Add-On
/// install through the OpenIV mods folder:
/// <code>
/// &lt;game&gt;/mods/                                   must exist
/// &lt;game&gt;/mods/update/update.rpf/common/data/dlclist.xml   gets &lt;Item&gt;dlcpacks:/&lt;DLC&gt;/&lt;/Item&gt;
/// &lt;game&gt;/mods/update/x64/dlcpacks/&lt;DLC&gt;/dlc.rpf    the build is copied here
/// </code>
/// update.rpf may be an unpacked folder (the file is edited on disk) or a real RPF7
/// archive (dlclist.xml is rewritten in place inside it).
/// </summary>
public static partial class GameInstaller
{
    public const string DlclistInner = "common/data/dlclist.xml";

    [GeneratedRegex(@"([ \t]*)</Paths>")] private static partial Regex PathsCloseRe();
    [GeneratedRegex(@"\n([ \t]*)<Item>")] private static partial Regex ItemIndentRe();

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

    private static void AddToDlclistRpf(string updateRpf, string dlcName, Action<string> log)
    {
        var raw = RpfTools.ReadInnerFile(updateRpf, DlclistInner);
        var text = TextIo.DecodeUtf8Sig(raw);
        var updated = DlclistWithEntry(text, dlcName, log);
        if (updated is not null)
        {
            var info = RpfTools.PatchInnerFile(updateRpf, DlclistInner, Encoding.UTF8.GetBytes(updated));
            log($"    dlclist.xml rewritten in update.rpf ({info.Uncompressed} bytes, {info.OnDisk} compressed).");
        }
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
                "Add-On installation is only supported via the OpenIV mods folder.");

        var updateRpf = Path.Combine(mods, "update", "update.rpf");
        if (Directory.Exists(updateRpf))
        {
            var dlclist = Path.Combine(updateRpf, "common", "data", "dlclist.xml");
            if (!File.Exists(dlclist))
                throw new FileNotFoundException(
                    $"dlclist.xml file not found: {dlclist}\n" +
                    "Check that update.rpf was unpacked into mods via OpenIV.");
            AddToDlclistFile(dlclist, dlcName, log);
        }
        else if (File.Exists(updateRpf))
        {
            log($"    update.rpf is an archive, editing dlclist.xml inside: {updateRpf}");
            AddToDlclistRpf(updateRpf, dlcName, log);
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
