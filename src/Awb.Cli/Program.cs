using System.Globalization;
using System.Text;
using Awb.Core;
using Awb.Core.Rpf;
using Awb.Core.Util;

namespace Awb.Cli;

/// <summary>
/// awbctl — command-line entry for AddonWeapons Builder.
/// <code>
/// awbctl build-templates [data_dir] [out_dir]
/// awbctl scan   &lt;templates_dir&gt; &lt;input_folder&gt;
/// awbctl plan   &lt;templates_dir&gt; &lt;input_folder&gt; [--name N] [--price P] [--shop-id ID]
/// awbctl build  &lt;templates_dir&gt; &lt;input_folder&gt; &lt;out_dir&gt; [options]
/// awbctl verify &lt;archive.rpf&gt;
/// </code>
/// </summary>
internal static class Program
{
    private static readonly string Root = AppContext.BaseDirectory;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var rest = args[1..];
            return args[0] switch
            {
                "build-templates" => BuildTemplates(rest),
                "scan" => Scan(rest),
                "plan" => Plan(rest),
                "build" => Build(rest),
                "verify" => Verify(rest),
                _ => Usage($"unknown command '{args[0]}'"),
            };
        }
        catch (UsageException ex)
        {
            return Usage(ex.Message);
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    private static int Usage(string error)
    {
        Console.Error.WriteLine($"awbctl: error: {error}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            usage: awbctl <command> [...]

              build-templates [data_dir] [out_dir]
                  parse vanilla metas -> template library (default: data/ -> data/templates)
              scan <templates_dir> <input_folder>
                  classify a replace-mod folder, resolve the base weapon (JSON)
              plan <templates_dir> <input_folder> [--name N] [--price P] [--shop-id ID]
                  dry-run of the unique-namespace rename plan
              build <templates_dir> <input_folder> <out_dir>
                  [--name N] [--desc D] [--price P] [--ammo-cost C] [--shop-id ID]
                  [--comp-price STEM=COST ...] [--model-name NAME]
                  [--no-pack] [--merge-pack] [--install-game-dir DIR]
                  full Replace -> Add-On build
              verify <archive.rpf>
                  self-check every resource of a built archive
            """);
    }

    // ------------------------------------------------------------ arg parsing

    private sealed class Args
    {
        public List<string> Positional { get; } = [];
        public Dictionary<string, List<string>> Options { get; } = [];
        public HashSet<string> Flags { get; } = [];

        public string? Opt(string name) => Options.TryGetValue(name, out var v) ? v[^1] : null;
        public List<string> All(string name) => Options.TryGetValue(name, out var v) ? v : [];

        public int Int(string name, int dflt)
        {
            var v = Opt(name);
            if (v is null) return dflt;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                throw new UsageException($"argument {name}: invalid int value: '{v}'");
            return i;
        }

        public int? IntOrNull(string name) => Opt(name) is null ? null : Int(name, 0);
    }

    private static Args Parse(string[] argv, string[] valueOptions, string[] flags)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            var s = argv[i];
            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                string name = s;
                string? value = null;
                int eq = s.IndexOf('=');
                if (eq > 0) { name = s[..eq]; value = s[(eq + 1)..]; }
                if (flags.Contains(name))
                {
                    a.Flags.Add(name);
                    continue;
                }
                if (!valueOptions.Contains(name)) throw new UsageException($"unrecognized argument: {s}");
                if (value is null)
                {
                    if (i + 1 >= argv.Length) throw new UsageException($"argument {name}: expected one argument");
                    value = argv[++i];
                }
                if (!a.Options.TryGetValue(name, out var list)) a.Options[name] = list = [];
                list.Add(value);
            }
            else
            {
                a.Positional.Add(s);
            }
        }
        return a;
    }

    private static void NeedPositional(Args a, int min, int max, string names)
    {
        if (a.Positional.Count < min) throw new UsageException($"the following arguments are required: {names}");
        if (a.Positional.Count > max) throw new UsageException($"unrecognized arguments: {string.Join(' ', a.Positional.Skip(max))}");
    }

    // ------------------------------------------------------------ commands

    private static int BuildTemplates(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 0, 2, "");
        var data = a.Positional.Count > 0 ? a.Positional[0] : Path.Combine(Root, "data");
        var outDir = a.Positional.Count > 1 ? a.Positional[1] : Path.Combine(data, "templates");
        var lib = TemplateLibrary.FromMetas(
            Path.Combine(data, "weapons.meta"), Path.Combine(data, "weaponcomponents.meta"),
            Path.Combine(data, "weaponarchetypes.meta"), Path.Combine(data, "weaponanimations.meta"));
        var idx = lib.Save(outDir);
        Console.WriteLine($"weapons={lib.Weapons.Count} components={lib.Components.Count} " +
                          $"archetypes={lib.Archetypes.Count} -> {outDir} ({idx.Count} templates)");
        return 0;
    }

    private static int Scan(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 2, 2, "templates_dir, input_folder");
        var res = new InputScanner(a.Positional[0]).Scan(a.Positional[1]);
        Console.WriteLine(TextIo.ToJson(res.ToJson()));
        return 0;
    }

    private static int Plan(string[] argv)
    {
        var a = Parse(argv, ["--name", "--price", "--shop-id"], []);
        NeedPositional(a, 2, 2, "templates_dir, input_folder");
        var tdir = a.Positional[0];
        var input = a.Positional[1];
        var sc = new InputScanner(tdir);
        var res = sc.Scan(input);
        if (!res.TemplateFound)
        {
            Console.WriteLine("[!] Base weapon/template not determined — plan not possible.");
            foreach (var w in res.Warnings) Console.WriteLine("    - " + w);
            return 1;
        }
        var tpl = TemplateData.Load(sc.TemplatePath(res.BaseWeapon!));
        var proj = a.Opt("--name") ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(input));
        int price = a.Int("--price", 5000);
        var plan = new Namer(proj, shopIdBase: a.Int("--shop-id", 1000))
            .Plan(res.BaseWeapon!, res.Groups.Select(g => g.Stem), tpl.ComponentOrder);

        Console.WriteLine($"=== BUILD PLAN: {proj} ===");
        Console.WriteLine($"base weapon    : {res.BaseWeapon}  ({res.TemplateSource})");
        Console.WriteLine($"class          : {res.WeaponClass ?? "None"}");
        Console.WriteLine($"WEAPON hash    : {plan.WeaponHash}");
        Console.WriteLine($"SLOT           : {plan.Slot}");
        Console.WriteLine($"unlock         : {plan.Unlock}");
        Console.WriteLine($"device / cs    : {plan.Device} / {plan.Changeset}");
        Console.WriteLine($"shop id / price: {plan.ShopId} / {price}");
        Console.WriteLine($"GXT labels     : {plan.LabelName}, {plan.LabelDesc}, {plan.LabelTt}, {plan.LabelUpper}");
        Console.WriteLine("\nmodels:");
        foreach (var (o, n) in plan.ModelMap) Console.WriteLine($"   {o,-30} -> {n}");
        Console.WriteLine("\ncomponents (from template):");
        foreach (var (o, n) in plan.ComponentMap) Console.WriteLine($"   {o,-34} -> {n}");
        if (res.Warnings.Count > 0)
        {
            Console.WriteLine("\nwarnings:");
            foreach (var w in res.Warnings) Console.WriteLine("   - " + w);
        }
        bool collisions = plan.ModelMap.Values.Distinct().Count() != plan.ModelMap.Count;
        Console.WriteLine($"\nmodel uniqueness check: {(collisions ? "ERROR — collision!" : "OK")}");
        return 0;
    }

    private static Dictionary<string, int> ParseCompPrices(List<string> pairs)
    {
        var result = new Dictionary<string, int>();
        foreach (var p in pairs)
        {
            int eq = p.IndexOf('=');
            if (eq < 0) throw new UsageException($"--comp-price expects stem=cost, got '{p}'");
            var cost = p[(eq + 1)..].Trim();
            if (!int.TryParse(cost, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                throw new UsageException($"--comp-price: invalid cost '{cost}'");
            result[p[..eq].Trim()] = c;
        }
        return result;
    }

    private static int Build(string[] argv)
    {
        var a = Parse(argv,
            ["--name", "--desc", "--price", "--ammo-cost", "--shop-id", "--comp-price", "--model-name", "--install-game-dir"],
            ["--no-pack", "--merge-pack"]);
        NeedPositional(a, 3, 3, "templates_dir, input_folder, out_dir");
        var input = a.Positional[1];
        var opts = new BuildOptions
        {
            TemplatesDir = a.Positional[0],
            InputFolder = input,
            OutDir = a.Positional[2],
            DataDir = Path.Combine(Root, "data"),
            Name = a.Opt("--name") ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(input)),
            Desc = a.Opt("--desc") ?? "An add-on weapon.",
            Price = a.Int("--price", 5000),
            AmmoCost = a.Int("--ammo-cost", 100),
            ShopId = a.IntOrNull("--shop-id"),
            ComponentPrices = ParseCompPrices(a.All("--comp-price")),
            ModelName = a.Opt("--model-name"),
            PackRpf = !a.Flags.Contains("--no-pack"),
            MergePack = a.Flags.Contains("--merge-pack"),
            InstallGameDir = a.Opt("--install-game-dir"),
        };

        BuildResult? result;
        try
        {
            result = Pipeline.BuildAddon(opts, Console.WriteLine);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[!] Build failed: {ex.Message}");
            return 1;
        }

        if (result is null) return 1;
        if (result.Merged)
        {
            var packs = result.Packs.Count > 0 ? result.Packs : [result.Manifest["folder"]!.ToString()];
            Console.WriteLine($"\nMerged into the shared pack(s) {string.Join(", ", packs)} " +
                              $"({result.WeaponsInPack.Count} weapon(s) in '{result.Manifest["folder"]}').");
        }
        else if (result.Prebuilt)
            Console.WriteLine($"\nPrebuilt archive shipped as-is -> {result.Root}");
        else if (!result.Packed)
            Console.WriteLine("\nRemaining: pack the loose *.rpf folders via CodeWalker (see manifest.json).");
        else
            Console.WriteLine("\nReady to install — see manifest.json (install section).");
        return 0;
    }

    private static int Verify(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 1, 1, "archive");
        var problems = RpfTools.VerifyResources(a.Positional[0]);
        if (problems.Count == 0)
        {
            Console.WriteLine("OK: every resource decompresses to exactly its flagged page size.");
            return 0;
        }
        Console.WriteLine($"{problems.Count} problem(s):");
        foreach (var p in problems) Console.WriteLine("  ✗ " + p);
        return 1;
    }
}
