using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Awb.Core.Rpf;
using Awb.Core.Util;

namespace Awb.Core;

public sealed class BuildOptions
{
    public required string InputFolder { get; init; }
    public required string OutDir { get; init; }
    public required string TemplatesDir { get; init; }
    public required string DataDir { get; init; }
    public required string Name { get; init; }
    public string Desc { get; init; } = "An add-on weapon.";
    public int Price { get; init; } = 5000;
    public int AmmoCost { get; init; } = 100;
    /// <summary>Null = <see cref="Pipeline.DefaultShopId"/>.</summary>
    public int? ShopId { get; init; }
    /// <summary>Input component asset stem (e.g. 'w_pi_x_mag1') -> shop cost.</summary>
    public Dictionary<string, int>? ComponentPrices { get; init; }
    /// <summary>Rename the weapon's models to this (main model = exactly this). Null = automatic.</summary>
    public string? ModelName { get; init; }
    public bool PackRpf { get; init; } = true;
    /// <summary>Fold the weapon into the shared AddonWeapons pack.</summary>
    public bool MergePack { get; init; }
    /// <summary>GTA V folder to install the finished pack into (mods/…).</summary>
    public string? InstallGameDir { get; init; }
    /// <summary>
    /// Game build to pack for. Null: taken from <see cref="InstallGameDir"/>'s executable,
    /// or Legacy when not installing.
    /// </summary>
    public GameEdition? Edition { get; init; }
    /// <summary>Bundled mods-folder plugins and ASI loaders (OpenIV.asi, DSOUND.dll, dinput8.dll, xinput1_4.dll). Null = DataDir/plugins.</summary>
    public string? PluginsDir { get; init; }
}

/// <summary>
/// Build orchestration shared by the CLI and the GUI. The input folder decides the route:
/// <list type="bullet">
///   <item><b>raw models</b> (.ydr/.ytd only) — Replace → Add-On: base weapon resolved
///   against the template library, whole meta stack generated;</item>
///   <item><b>models + metas</b> — the modder's own metas ship verbatim, only what they
///   left out is generated; models keep their names;</item>
///   <item><b>a finished dlc.rpf</b> — installed as-is, or unpacked into the shared pack.</item>
/// </list>
/// </summary>
public static partial class Pipeline
{
    /// <summary>
    /// Fixed Shop ID for every build: testing showed the game ignores it (several weapons
    /// can share one id and all load), so there's no need to hunt for a free one.
    /// </summary>
    public const int DefaultShopId = 1000;

    [GeneratedRegex("[^a-z0-9_]")] private static partial Regex NonSlugRe();

    /// <summary>The dlcpack folder name the assembler/installer will use for a name.</summary>
    public static string DlcFolderName(string name) => NonSlugRe().Replace(Namer.Slugify(name).ToLowerInvariant(), "");

    /// <summary>Build the template library from vanilla metas if it's missing.</summary>
    public static bool EnsureTemplates(string dataDir, string templatesDir, Action<string> log)
    {
        if (File.Exists(Path.Combine(templatesDir, "_index.json"))) return true;
        string[] need = ["weapons.meta", "weaponcomponents.meta", "weaponarchetypes.meta"];
        var missing = need.Where(f => !File.Exists(Path.Combine(dataDir, f))).ToList();
        if (missing.Count > 0)
        {
            log($"[!] Missing vanilla metas in {dataDir}: {string.Join(", ", missing)}");
            return false;
        }
        log("Building template library from vanilla metas…");
        var anim = Path.Combine(dataDir, "weaponanimations.meta");
        var lib = TemplateLibrary.FromMetas(
            Path.Combine(dataDir, "weapons.meta"), Path.Combine(dataDir, "weaponcomponents.meta"),
            Path.Combine(dataDir, "weaponarchetypes.meta"), File.Exists(anim) ? anim : null);
        lib.Save(templatesDir);
        log($"Done: {lib.Weapons.Count} weapon templates.");
        return true;
    }

    /// <summary>Self-check a freshly packed dlc.rpf; throws if any resource is corrupt.</summary>
    public static void VerifyPack(string dlcRpf, Action<string> log)
    {
        log("Self-checking RPF resources…");
        var problems = RpfTools.VerifyResources(dlcRpf);
        if (problems.Count > 0)
        {
            log($"[!] Check FAILED — {problems.Count} problem resources:");
            foreach (var p in problems) log("    ✗ " + p);
            log("[!] Archive is NOT usable in the game (see above). Build aborted.");
            throw new InvalidOperationException(
                $"dlc.rpf check failed: {problems.Count} resources are corrupt " +
                "(unpacked size doesn't match the flagged size).");
        }
        log("Self-check passed: all resources decompress to exactly their flagged page size — ready to install.");
    }

    /// <summary>The log line naming what a build targets.</summary>
    public static string TargetLine(GameEdition edition) => edition == GameEdition.Enhanced
        ? "Target: GTA V Enhanced (OPEN archive, models in gen9 format)."
        : "Target: GTA V Legacy (OPEN archive).";

    /// <summary>Full build. Returns null when the base weapon can't be determined.</summary>
    public static BuildResult? BuildAddon(BuildOptions o, Action<string> log)
    {
        if (o.InstallGameDir is not null && !o.PackRpf && !o.MergePack)
            throw new ArgumentException("Installing into GTA V requires a packed dlc.rpf — enable RPF packing.");
        var edition = o.InstallGameDir is not null
            ? GameInstaller.ResolveEdition(o.InstallGameDir, o.Edition, log)
            : o.Edition ?? GameEdition.Legacy;
        log(TargetLine(edition));
        if (o.InstallGameDir is not null)
            GameInstaller.PrepareGame(o.InstallGameDir, edition, o.PluginsDir ?? Path.Combine(o.DataDir, "plugins"), log);
        bool packRpf = o.PackRpf || o.MergePack;          // the shared pack is always packed

        // ---- route 3: the folder already holds a finished pack
        var prebuilt = Overrides.FindPrebuiltRpf(o.InputFolder);
        if (prebuilt is not null)
        {
            log($"Input folder holds a prebuilt archive: {Path.GetFileName(prebuilt)} — no weapon models to convert.");
            return BuildPrebuilt(prebuilt, o, edition, log);
        }

        if (!EnsureTemplates(o.DataDir, o.TemplatesDir, log)) return null;
        int shopId = o.ShopId ?? DefaultShopId;

        var sc = new InputScanner(o.TemplatesDir);
        var res = sc.Scan(o.InputFolder);
        log($"Scan: main model = {Py(res.MainModel)}, class = {Py(res.WeaponClass)}");
        if (!res.TemplateFound)
        {
            log("[!] Base weapon not determined — build is not possible.");
            foreach (var w in res.Warnings) log("    - " + w);
            return null;
        }
        log($"Base weapon: {res.BaseWeapon}  ({res.TemplateSource})");
        foreach (var w in res.Warnings) log("    ! " + w);

        // ---- route 2: the folder ships its own metas
        var src = Overrides.Collect(o.InputFolder);
        if (src.Any)
            log($"Found {src.Texts.Count} meta/xml file(s) in the input folder — shipping them as-is " +
                $"instead of generating from templates: {string.Join(", ", src.Names.Values.OrderBy(n => n, StringComparer.Ordinal))}");
        bool keepNames = src.ForbidsRenaming();
        if (keepNames)
        {
            log("    Supplied metas name the models, so the models keep their original names (no _awXXXX renaming).");
            if (!string.IsNullOrEmpty(o.ModelName))
                log("    [!] The chosen model name is ignored — the supplied metas already pin the model names.");
        }

        string? modelName = null;
        if (!string.IsNullOrEmpty(o.ModelName) && !keepNames)
        {
            var clean = Namer.SanitizeModelName(o.ModelName);
            if (clean.Length == 0)
                throw new ArgumentException(
                    $"Model name «{o.ModelName}» has no usable characters — " +
                    "use latin letters, digits and underscores (e.g. w_pi_mygun).");
            log($"Model name: '{res.MainModel}' -> '{clean}' (and its siblings).");
            modelName = clean;
        }

        var tpl = TemplateData.Load(sc.TemplatePath(res.BaseWeapon!));
        var namer = new Namer(o.Name, modelBase: modelName, renameModels: !keepNames);
        var plan = namer.Plan(res.BaseWeapon!,
                              models: res.Groups.Where(g => g.Role != "hi").Select(g => g.Stem),
                              components: tpl.ComponentOrder, shopId: shopId, mainStem: res.MainModel);

        // Generated companions must agree with whatever the supplied metas declare.
        foreach (var (field, value) in Overrides.PlanHints(src.Texts))
            plan.TrySetField(field, value);

        var comps = Overrides.ComponentsFromOverrides(src.Texts);
        if (comps is not null)
            log($"Components taken from the supplied weaponcomponents.meta ({comps.Count}).");

        var mg = new MetaGenerator(tpl, res, plan, o.Price, o.AmmoCost, o.Name, o.Desc,
                                   o.ComponentPrices, comps);
        log($"Components: {(mg.Components.Count > 0 ? string.Join(", ", mg.Components.Select(e => e.Role)) : "—")}");
        var metas = mg.GenerateAll();
        var supplied = src.DataFiles;
        foreach (var (k, v) in supplied) metas[k] = v;       // supplied files win

        if (mg.Components.Count > 0)
        {
            var generated = metas.Keys.Where(k => !supplied.ContainsKey(k)).ToHashSet();
            var problems = MetaGenerator.VerifyComponentMetas(metas, mg.Components, generated);
            if (problems.Count > 0)
            {
                log($"[!] Component check: {problems.Count} problem(s):");
                foreach (var p in problems) log("    ✗ " + p);
            }
            else if (generated.Overlaps(["weapon.meta", "weaponcomponents.meta", "shop_weapon.meta"]))
                log($"Component check: all {mg.Components.Count} component(s) present in the generated metas.");
            else
                log($"Component check: all {mg.Components.Count} component(s) come from your own metas — nothing to check.");
        }
        else
        {
            log("Component check: this build has no weapon components.");
        }

        if (o.MergePack)
        {
            if (src.Configs.Count > 0)
                log("    [!] content.xml / setup2.xml are regenerated for the shared pack (it has one " +
                    "changeset for all weapons) — the supplied ones are not used.");
            return BuildMerged(res, plan, metas, mg, o, edition, log);
        }

        var asm = new DlcAssembler(res, plan, metas, mg.GxtLabels(), o.InputFolder, src);
        if (edition == GameEdition.Enhanced) LogGen9Conversion(o.InputFolder, asm.AssetRenames(), log);
        var result = asm.Build(o.OutDir, packRpf, edition);
        log($"WEAPON hash: {plan.WeaponHash}");
        log($"Assets copied: {result.Assets.Count}");
        if (result.Packed)
        {
            log($"Packed into a single dlc.rpf ({result.Manifest["dlc_rpf_size"]} bytes).");
            VerifyPack(result.DlcRpf!, log);
        }
        else
        {
            log("Assets laid out loose — build with pack_rpf=True for a dlc.rpf.");
        }
        log($"output: {result.Root}");

        if (o.InstallGameDir is not null)
        {
            if (!result.Packed) throw new InvalidOperationException("Installation not possible: dlc.rpf was not packed.");
            GameInstaller.InstallToGame(o.InstallGameDir, result.DlcRpf!, Path.GetFileName(result.Root), log);
            result.InstalledTo = o.InstallGameDir;
        }
        return result;
    }

    /// <summary>Python's str() of an optional value, for log parity.</summary>
    private static string Py(string? s) => s ?? "None";

    /// <summary>Tell the user which of the weapon's models get converted for Enhanced.</summary>
    private static void LogGen9Conversion(string inputFolder, List<(string Src, string Dst)> renames, Action<string> log)
    {
        var legacy = new List<string>();
        foreach (var (src, _) in renames)
        {
            var path = Path.Combine(inputFolder, src);
            if (!File.Exists(path) || !Rpf7.IsResourceExt(Path.GetExtension(path))) continue;
            Span<byte> hdr = stackalloc byte[16];
            using (var fs = File.OpenRead(path))
                if (fs.ReadAtLeast(hdr, 16, throwOnEndOfStream: false) < 16) continue;
            if (ResourceEditions.NeedsConversion(path, hdr, GameEdition.Enhanced)) legacy.Add(Path.GetFileName(src));
        }
        if (legacy.Count > 0)
            log($"Converting {legacy.Count} Legacy model(s) to the Enhanced (gen9) format: {string.Join(", ", legacy)}");
    }

    // ------------------------------------------------------------ merged pack

    private static MergedPackSet OpenPackSet(string outDir, string? installGameDir, Action<string> log)
    {
        var ms = new MergedPackSet(outDir, log);
        if (installGameDir is not null) ms.SyncWithGame(installGameDir);
        return ms;
    }

    /// <summary>Pack every changed dlcpack, self-check it, optionally install the family.</summary>
    private static BuildResult FinishMerged(MergedPackSet ms, string? installGameDir, GameEdition edition,
                                            Action<string> log)
    {
        var built = ms.BuildAll(edition);
        foreach (var b in built)
        {
            log($"Packed '{b.Folder}' into a single dlc.rpf ({b.Size} bytes, {b.Weapons.Count} weapon(s)).");
            VerifyPack(b.DlcRpf, log);
            log($"output: {b.Root}");
        }

        var packs = ms.AllPacks();
        if (packs.Count > 1)
            log($"The set now spans {packs.Count} dlcpacks ({string.Join(", ", packs.Select(p => p.FolderName))}) — " +
                $"each stays under the {MergedPack.FmtSize(ms.Limit)} limit.");

        var primary = built.Count > 0
            ? built[0]
            : new MergedPack.PackBuild(packs[0].Root, Path.Combine(packs[0].Root, "dlc.rpf"),
                                       packs[0].Data.Weapons.Keys.ToList(), packs[0].FolderName, 0);
        var packNames = packs.Select(p => p.FolderName).ToList();
        var result = new BuildResult
        {
            Root = primary.Root, DlcRpf = primary.DlcRpf, Packed = true, Merged = true,
            WeaponsInPack = primary.Weapons, Packs = packNames,
            Manifest = new JsonObject
            {
                ["folder"] = primary.Folder,
                ["weapons"] = new JsonArray(primary.Weapons.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
                ["packs"] = new JsonArray(packNames.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            },
        };

        if (installGameDir is not null)
        {
            var rebuilt = built.Select(b => b.Folder).ToHashSet();
            foreach (var pack in packs)
            {
                var rpf = Path.Combine(pack.Root, "dlc.rpf");
                if (rebuilt.Contains(pack.FolderName))
                    GameInstaller.InstallToGame(installGameDir, rpf, pack.FolderName, log);
                else if (File.Exists(rpf))
                {
                    log($"'{pack.FolderName}' unchanged — leaving the installed copy in place.");
                    GameInstaller.RegisterInDlclist(installGameDir, pack.FolderName, log);
                }
            }
            result.InstalledTo = installGameDir;
        }
        return result;
    }

    private static BuildResult BuildMerged(ScanResult res, NamePlan plan, Dictionary<string, string> metas,
                                           MetaGenerator mg, BuildOptions o, GameEdition edition, Action<string> log)
    {
        var ms = OpenPackSet(o.OutDir, o.InstallGameDir, log);
        var asm = new DlcAssembler(res, plan, metas, mg.GxtLabels(), o.InputFolder);
        var renames = asm.AssetRenames();
        ms.AddWeapon(plan.Suffix, mg.WeaponName, metas, mg.GxtLabels(), o.InputFolder, renames);
        log($"WEAPON hash: {plan.WeaponHash}");
        if (edition == GameEdition.Enhanced) LogGen9Conversion(o.InputFolder, renames, log);
        return FinishMerged(ms, o.InstallGameDir, edition, log);
    }

    // ------------------------------------------------------------ prebuilt dlc.rpf

    /// <summary>
    /// Standalone: the archive is taken as-is (installed, or copied to the output folder).
    /// Merged: it is unpacked and folded into the shared pack.
    /// </summary>
    private static BuildResult BuildPrebuilt(string dlcRpf, BuildOptions o, GameEdition edition, Action<string> log)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(o.InputFolder));
        if (o.MergePack)
        {
            log($"Unpacking '{Path.GetFileName(dlcRpf)}' to fold it into the shared pack…");
            var imported = Overrides.ImportDlcRpf(dlcRpf);
            foreach (var w in imported.Warnings) log("    ! " + w);
            if (imported.Assets.Count == 0 && imported.Metas.Count == 0)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(dlcRpf)} holds neither weapon models nor meta files — nothing to merge.");
            log($"    {imported.Assets.Count} model(s), {imported.Metas.Count} meta file(s), " +
                $"{imported.LabelHashes.Count} text label(s).");

            var ms = OpenPackSet(o.OutDir, o.InstallGameDir, log);
            var packName = string.IsNullOrEmpty(folderName) ? o.Name : folderName;
            var suffix = new Namer(packName).Suffix;
            ms.AddPrebuilt(suffix, packName, imported);
            return FinishMerged(ms, o.InstallGameDir, edition, log);
        }

        var dlcName = DlcFolderName(folderName);
        if (dlcName.Length == 0) dlcName = DlcFolderName(o.Name);

        // the container fits both editions, the models inside may not
        var mismatched = RpfRetarget.Mismatched(dlcRpf, edition);
        var source = dlcRpf;
        string? tmpDir = null;
        if (mismatched.Count > 0)
        {
            if (edition == GameEdition.Legacy)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(dlcRpf)} is built for GTA V Enhanced — its models are in the gen9 format " +
                    $"({string.Join(", ", mismatched.Take(3))}{(mismatched.Count > 3 ? ", …" : "")}), which " +
                    "GTA V Legacy can't load. Use the Legacy version of the mod.");
            log($"The archive holds {mismatched.Count} Legacy model(s) — converting them to the Enhanced (gen9) " +
                $"format; everything else is kept byte for byte.");
            tmpDir = PathUtil.MakeTempDir();
            var converted = Path.Combine(tmpDir, "dlc.rpf");
            RpfRetarget.Convert(dlcRpf, converted, edition);
            VerifyPack(converted, log);
            dlcRpf = converted;
            log($"Installing the converted archive under the dlcpack name '{dlcName}'.");
        }
        else
        {
            log($"Installing the archive as-is under the dlcpack name '{dlcName}'.");
        }

        try
        {
            return ShipPrebuilt(dlcRpf, source, dlcName, o, edition, converted: tmpDir is not null, log);
        }
        finally
        {
            if (tmpDir is not null) PathUtil.TryDeleteDir(tmpDir);
        }
    }

    private static BuildResult ShipPrebuilt(string dlcRpf, string source, string dlcName, BuildOptions o, GameEdition edition,
                                            bool converted, Action<string> log)
    {
        if (o.InstallGameDir is not null)
        {
            var installed = GameInstaller.InstallToGame(o.InstallGameDir, dlcRpf, dlcName, log);
            if (converted) dlcRpf = installed;                  // the temp copy is about to go
            return new BuildResult
            {
                Root = Path.GetDirectoryName(dlcRpf)!, DlcRpf = dlcRpf, Packed = true, Prebuilt = true,
                InstalledTo = o.InstallGameDir, Manifest = new JsonObject { ["folder"] = dlcName },
            };
        }

        var root = Path.Combine(o.OutDir, dlcName);
        Directory.CreateDirectory(root);
        var dest = Path.Combine(root, "dlc.rpf");
        if (!string.Equals(Path.GetFullPath(dlcRpf), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            PathUtil.Copy2(dlcRpf, dest);
        var manifest = new JsonObject
        {
            ["packed"] = true, ["prebuilt"] = true, ["target"] = edition.TargetLabel(),
            ["output"] = $"{dlcName}/dlc.rpf",
            ["source"] = source,
            ["install"] = $"Copy the '{dlcName}' folder (with dlc.rpf inside) to your " +
                          $"dlcpacks path and add 'dlcpacks:/{dlcName}/' to dlclist.xml.",
        };
        TextIo.WriteText(Path.Combine(root, "manifest.json"), TextIo.ToJson(manifest));
        log($"    dlc.rpf copied -> {dest}");
        log($"output: {root}");
        return new BuildResult { Root = root, DlcRpf = dest, Packed = true, Prebuilt = true, Manifest = manifest };
    }
}
