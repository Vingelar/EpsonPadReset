using System.Text.Json;
using EpsonPadReset;

namespace EpsonPadReset;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        CliOptions opts;
        try { opts = Cli.Parse(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Cli.PrintHelp();
            return 1;
        }

        if (opts.Help || args.Length == 0)
        {
            Cli.PrintHelp();
            return 0;
        }

        Console.WriteLine("EpsonPadReset — Waste-Ink-Zähler lesen/zurücksetzen");
        Console.WriteLine("Protokoll angelehnt an PadZero (usb_direct) / reinkpy");
        Console.WriteLine("Hinweis: Setzt nur den Software-Zähler, leert keine physischen Kissen.");
        Console.WriteLine();

        try
        {
            var paths = UsbPrint.ListDevicePaths();
            if (paths.Count == 0)
            {
                Console.Error.WriteLine("Kein USB-Drucker gefunden.");
                Console.Error.WriteLine(" * Epson-Treiber installieren (nicht nur generischer IPP)");
                Console.Error.WriteLine(" * USB-Kabel in den USB-Port (nicht LINE/EXT)");
                return 1;
            }

            if (opts.List)
            {
                Console.WriteLine("USB-Drucker-Schnittstellen:");
                for (var i = 0; i < paths.Count; i++)
                    Console.WriteLine($"  [{i}] {paths[i]}");
                return 0;
            }

            if (opts.Device < 0 || opts.Device >= paths.Count)
            {
                Console.Error.WriteLine($"Kein Gerät an Index {opts.Device} (gefunden: {paths.Count}).");
                return 1;
            }

            var modelsPath = Path.Combine(AppContext.BaseDirectory, "models.json");
            if (!File.Exists(modelsPath))
                modelsPath = Path.Combine(Directory.GetCurrentDirectory(), "models.json");
            var db = ModelDb.Load(modelsPath);
            var modelName = opts.Model ?? "ET-2820";
            var model = db.Resolve(modelName);
            var rkey = model.GetReadKey();
            var wkey = model.GetWriteKey();

            Console.WriteLine($"Modell     : {modelName}");
            Console.WriteLine($"Read-Key   : 0x{rkey:X4}");
            Console.WriteLine($"Write-Key  : {wkey}");
            Console.WriteLine($"Gerät [{opts.Device}]: {paths[opts.Device]}");
            Console.WriteLine();

            using var usb = new UsbPrint.Device(paths[opts.Device]);
            using var epson = new EpsonProtocol(usb, rkey, wkey, opts.Verbose);

            Console.WriteLine("Verbindungstest (EEPROM[47] lesen)...");
            epson.Connect();
            var probe = epson.ReadEeprom(47);
            if (probe is null)
            {
                Console.Error.WriteLine("Keine EE:-Antwort nach erfolgreichem Transport.");
                return 2;
            }
            Console.WriteLine($"OK — Transport={epson.TransportName}, EEPROM[47] = {probe}");
            Console.WriteLine();

            if (opts.Info || opts.Reset)
                ShowCounters(epson, model);

            if (opts.Dump || opts.Reset)
            {
                var backupPath = SaveBackup(epson, modelName, opts.Reset ? "pre-reset" : "dump");
                Console.WriteLine($"Backup -> {backupPath}");
            }

            if (opts.Reset)
            {
                var plan = model.GetResetPlan();
                Console.WriteLine();
                Console.WriteLine("--- RESET-PLAN ---");
                var current = new Dictionary<int, int?>();
                foreach (var (addr, value) in plan)
                {
                    var cur = epson.ReadEeprom(addr);
                    current[addr] = cur;
                    var mark = cur == value ? " " : "*";
                    Console.WriteLine($"  {mark} {addr,3} : {Fmt(cur),-4} -> {value}");
                }
                var changes = plan.Count(p => current.GetValueOrDefault(p.Addr) != p.Value);
                Console.WriteLine($"  {changes} von {plan.Count} Adressen würden sich ändern.");

                if (!opts.Yes)
                {
                    Console.WriteLine();
                    Console.WriteLine("Dry-Run. Nichts geschrieben. Mit --yes wirklich zurücksetzen.");
                    return 0;
                }

                Console.WriteLine();
                Console.WriteLine("Schreibe...");
                var ok = true;
                foreach (var (addr, value) in plan)
                {
                    var good = epson.WriteEeprom(addr, value);
                    Console.WriteLine($"  {addr,3} -> {value,-3} {(good ? "OK" : "FAILED")}");
                    ok &= good;
                }

                Console.WriteLine();
                Console.WriteLine(ok ? "Reset abgeschlossen." : "Reset hatte FEHLER — siehe oben.");
                Console.WriteLine();
                Console.WriteLine("--- NACH DEM RESET ---");
                ShowCounters(epson, model);
                Console.WriteLine();
                Console.WriteLine("Drucker aus- und wieder einschalten (Power-Taste).");
                Console.WriteLine("Die physischen Tintenpads sind unverändert voll.");
                return ok ? 0 : 3;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            if (opts.Verbose) Console.Error.WriteLine(ex);
            return 9;
        }
    }

    private static void ShowCounters(EpsonProtocol epson, ModelEntry model)
    {
        Console.WriteLine("--- WASTE COUNTERS ---");
        if (model.Waste is { Count: > 0 })
        {
            foreach (var (name, cfg) in model.Waste)
            {
                if (cfg.Oids is null || cfg.Oids.Length == 0) continue;
                var bytes = cfg.Oids.Select(a => epson.ReadEeprom(a)).ToList();
                if (bytes.Any(b => b is null))
                {
                    Console.WriteLine($"  {name,-22}: Lesefehler");
                    continue;
                }
                var hex = string.Concat(bytes.AsEnumerable().Reverse().Select(b => $"{b:X2}"));
                var raw = Convert.ToInt32(hex, 16);
                if (cfg.Divider is > 0)
                {
                    var pct = raw / cfg.Divider.Value;
                    var flag = pct >= 100 ? " FULL" : pct >= 90 ? " near full" : "";
                    Console.WriteLine($"  {name,-22}: {pct,6:0.00}%{flag}  (raw {raw})");
                }
                else
                {
                    Console.WriteLine($"  {name,-22}: raw {raw}");
                }
            }
        }
        else
        {
            foreach (var (addr, _) in model.GetResetPlan().Take(16))
                Console.WriteLine($"  EEPROM[{addr}] = {Fmt(epson.ReadEeprom(addr))}");
        }
    }

    private static string SaveBackup(EpsonProtocol epson, string model, string tag)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "dumps");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var path = Path.Combine(dir, $"{model}_{stamp}_{tag}.json");
        Console.WriteLine("EEPROM-Backup 0..255 lesen...");
        var map = epson.DumpEeprom(0, 0xFF);
        var doc = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["timestamp"] = stamp,
            ["tag"] = tag,
            ["eeprom"] = map.ToDictionary(kv => kv.Key.ToString(), kv => (object?)kv.Value)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string Fmt(int? v) => v is null ? "?" : v.Value.ToString();
}

internal sealed class CliOptions
{
    public bool Help { get; set; }
    public bool List { get; set; }
    public bool Info { get; set; }
    public bool Dump { get; set; }
    public bool Reset { get; set; }
    public bool Yes { get; set; }
    public bool Verbose { get; set; }
    public int Device { get; set; }
    public string? Model { get; set; }
}

internal static class Cli
{
    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": o.Help = true; break;
                case "-l" or "--list": o.List = true; break;
                case "-i" or "--info": o.Info = true; break;
                case "--dump": o.Dump = true; break;
                case "--reset": o.Reset = true; break;
                case "--yes": o.Yes = true; break;
                case "-v" or "--verbose": o.Verbose = true; break;
                case "-d" or "--device":
                    o.Device = int.Parse(args[++i]); break;
                case "-m" or "--model":
                    o.Model = args[++i]; break;
                default:
                    throw new ArgumentException($"Unbekanntes Argument: {args[i]}");
            }
        }
        if (!o.List && !o.Info && !o.Dump && !o.Reset && !o.Help)
            o.Info = true;
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            EpsonPadReset — Epson Waste-Ink-Zähler (Backup + Reset)

            Voraussetzung: Epson-Treiber, USB-Kabel, Windows.

            Usage:
              EpsonPadReset -l
              EpsonPadReset -i [-m ET-2820] [-d 0]
              EpsonPadReset --dump [-m ET-2820]
              EpsonPadReset --reset            # Dry-Run + Backup-Anzeige
              EpsonPadReset --reset --yes      # Backup, dann wirklich schreiben

            Optionen:
              -l, --list       USB-Drucker auflisten
              -i, --info       Zählerstand anzeigen (Standard)
              --dump           EEPROM 0..255 als JSON speichern
              --reset          Reset-Plan (mit --yes ausführen)
              --yes            Schreiben erlauben
              -d, --device N   Geräteindex (Standard 0)
              -m, --model NAME Modell (Standard ET-2820)
              -v, --verbose    Stacktraces

            Backup liegt unter dumps\ neben der EXE.
            Nach dem Reset: Drucker per Power-Taste neu starten.
            """);
    }
}
