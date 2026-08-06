using System;
using System.IO;

namespace Peak.StepSpike.Harvest
{
    public static class Log
    {
        private static string _file;

        public static void Init(string path)
        {
            _file = path;
            try { File.WriteAllText(_file, ""); } catch { /* console only */ }
        }

        public static void Info(string message)
        {
            Console.WriteLine(message);
            if (_file == null) return;
            try { File.AppendAllText(_file, DateTime.Now.ToString("HH:mm:ss ") + message + Environment.NewLine); }
            catch { /* console only */ }
        }
    }

    public static class Program
    {
        private const string Usage = @"
Peak.StepSpike.Harvest -- SolidWorks automation harness for the NEXT-STEP-SW
feasibility study.

  baseline   S0: export the full STEP option matrix for every corpus model and
             probe PublishSTEP242File for the MBD licence gate.

Options:
  --corpus <dir>   corpus directory (default ..\..\corpus)
  --out <dir>      output directory  (default ..\..\evidence\S0)
  --visible        show the SolidWorks window (default: hidden)

Example:
  Peak.StepSpike.Harvest.exe baseline --corpus c:\PeakDesign\NEXT-STEP-SW\corpus
";

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                Console.WriteLine(Usage);
                return 1;
            }

            string root = FindRoot();
            string verb = args[0];
            string corpus = Arg(args, "--corpus", Path.Combine(root, "corpus"));
            string outDir = Arg(args, "--out", Path.Combine(root, "evidence", "S0"));
            bool visible = Array.IndexOf(args, "--visible") >= 0;

            Directory.CreateDirectory(outDir);
            Log.Init(Path.Combine(outDir, "harvest.log"));
            Log.Info($"verb={verb} corpus={corpus} out={outDir} visible={visible}");

            try
            {
                switch (verb)
                {
                    case "baseline":
                        return S0Baseline.Run(corpus, outDir, visible);
                    case "probe242":
                        return S0Baseline.Run242Only(corpus, outDir, visible);
                    case "export":
                    {
                        string s6Out = Arg(args, "--out", Path.Combine(root, "evidence", "S6"));
                        Directory.CreateDirectory(s6Out);
                        Log.Init(Path.Combine(s6Out, "export.log"));
                        S6Export.DeInstance = Array.IndexOf(args, "--deinstance") >= 0;
                        S6Export.Materials = Array.IndexOf(args, "--materials") >= 0;
                        return S6Export.Run(corpus, s6Out, visible, Arg(args, "--only", null));
                    }
                    case "harvest":
                    {
                        int maxFaces = int.Parse(Arg(args, "--maxfaces", "400"));
                        string s3Out = Arg(args, "--out", Path.Combine(root, "evidence", "S3"));
                        string single = Arg(args, "--model", null);
                        string reportName = Arg(args, "--report", "s3-harvest.json");
                        Directory.CreateDirectory(s3Out);
                        Log.Init(Path.Combine(s3Out, "harvest.log"));
                        return S3Harvest.Run(corpus, s3Out, visible, maxFaces, single, reportName);
                    }
                    default:
                        Console.WriteLine($"unknown verb '{verb}'");
                        Console.WriteLine(Usage);
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Log.Info("FATAL: " + ex);
                return 3;
            }
        }

        private static string Arg(string[] args, string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : fallback;
        }

        /// <summary>Walk up from the exe to the study root (the folder holding PLAN.md).</summary>
        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PLAN.md")))
                dir = dir.Parent;
            return dir?.FullName ?? Environment.CurrentDirectory;
        }
    }
}
