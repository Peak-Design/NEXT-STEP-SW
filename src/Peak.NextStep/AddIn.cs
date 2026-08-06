using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Peak.NextStep
{
    [ComVisible(true)]
    [Guid("6F1B2A74-9C3D-4E15-9A88-2D4C7B0E5F31")]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(ISwAddin))]
    public class AddIn : ISwAddin
    {
        public const string AddInTitle = "NEXT-STEP";

        /// <summary>
        /// Taken from the assembly rather than written twice: the csproj
        /// &lt;Version&gt; is the single source, so a release cannot ship a
        /// binary whose registry entry claims a different version.
        /// </summary>
        public static string AddInVersion =>
            "v" + (typeof(AddIn).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        /// <summary>
        /// Titles this add-in has shipped under. The command tab is looked up
        /// by title, so a rename orphans the old tab -- it stays in the user's
        /// SolidWorks layout forever with a dead button on it. These are swept
        /// on every connect.
        /// </summary>
        private static readonly string[] RetiredTitles = { "Peak NEXT-STEP" };

        public const string AddInDescription =
            "STEP export that preserves the SolidWorks appearance hierarchy, "
            + "including component and assembly level overrides.";

        public static ISldWorks SwApp { get; private set; }

        private int _cookie;
        private ICommandManager _cmdMgr;
        private CommandCallbacks _callbacks;

        /// <summary>CommandGroup UserID -- persisted in the SW registry along with
        /// the user's toolbar customisation, so it must never change.</summary>
        private const int MainCmdGroupId = 71;
        private const int CmdExportUserId = 0;

        // ── Cross-version interop resolver ──────────────────────────────────
        // Compiled against SW2022 interops (v30). When loaded by a newer
        // SolidWorks the CLR cannot find that exact assembly version, so
        // redirect to whichever interops ship with the running installation.
        // This is a fallback only: the Private=True copies beside the DLL are
        // what actually satisfy the bind that happens as AddIn itself loads.
        static AddIn()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var name = new AssemblyName(args.Name);
                if (!name.Name.StartsWith("SolidWorks.Interop.", StringComparison.OrdinalIgnoreCase))
                    return null;
                try
                {
                    string swDir = Path.GetDirectoryName(
                        System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                    string dll = Path.Combine(swDir ?? "", name.Name + ".dll");
                    if (File.Exists(dll)) return Assembly.LoadFrom(dll);
                }
                catch { }
                return null;
            };
        }

        // ── Registration ────────────────────────────────────────────────────

        /// <summary>
        /// The installed SolidWorks version keys under HKLM\SOFTWARE\SolidWorks.
        ///
        /// Two traps, both found the hard way on this machine:
        ///   * the casing is NOT consistent -- 2022/2024/2025 are "SOLIDWORKS
        ///     &lt;year&gt;" but 2026 is "SolidWorks 2026", so a case-sensitive
        ///     StartsWith silently skips it and the add-in never appears;
        ///   * "SOLIDWORKS CAM" also starts with "SOLIDWORKS " and is not a
        ///     SolidWorks version, so a loose filter registers into it.
        /// Requiring a 4-digit year after the prefix fixes both.
        /// </summary>
        private static IEnumerable<string> InstalledVersionKeys(RegistryKey swKey)
        {
            foreach (var name in swKey.GetSubKeyNames())
            {
                if (!name.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase))
                    continue;
                var suffix = name.Substring("SOLIDWORKS ".Length).Trim();
                if (suffix.Length == 4 && suffix.All(char.IsDigit))
                    yield return name;
            }
        }

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            var guid = t.GUID.ToString("B");
            using (var swKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks"))
            {
                if (swKey == null) return;
                foreach (var ver in InstalledVersionKeys(swKey))
                {
                    using (var key = Registry.LocalMachine.CreateSubKey(
                        $@"SOFTWARE\SolidWorks\{ver}\Addins\{guid}"))
                    {
                        key.SetValue(null, 1);
                        key.SetValue("Title", AddInTitle + " " + AddInVersion);
                        key.SetValue("Description", AddInDescription);
                    }
                }
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            var guid = t.GUID.ToString("B");
            using (var swKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks"))
            {
                if (swKey == null) return;
                // Unregister sweeps every key that starts with the prefix, not
                // just the year-shaped ones, so a stray key written by an
                // earlier looser filter (e.g. under "SOLIDWORKS CAM") is
                // cleaned up rather than orphaned.
                foreach (var ver in swKey.GetSubKeyNames()
                             .Where(n => n.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase)))
                    Registry.LocalMachine.DeleteSubKey(
                        $@"SOFTWARE\SolidWorks\{ver}\Addins\{guid}", throwOnMissingSubKey: false);
            }
        }

        // ── ISwAddin ────────────────────────────────────────────────────────
        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                SwApp = (ISldWorks)ThisSW;
                _cookie = Cookie;

                _callbacks = new CommandCallbacks { Owner = this };
                SwApp.SetAddinCallbackInfo2(0, _callbacks, _cookie);
                _cmdMgr = SwApp.GetCommandManager(_cookie);
                BuildCommandUI();

                Log("connected");
                return true;
            }
            catch (Exception ex)
            {
                Log("ConnectToSW failed: " + ex);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            try
            {
                if (_cmdMgr != null)
                {
                    _cmdMgr.RemoveCommandGroup2(MainCmdGroupId, true);
                    Marshal.ReleaseComObject(_cmdMgr);
                    _cmdMgr = null;
                }
            }
            catch (Exception ex) { Log("DisconnectFromSW: " + ex.Message); }

            SwApp = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        private void BuildCommandUI()
        {
            int errors = 0;

            // Only discard the user's saved toolbar layout when our command set
            // has actually changed. Passing ignorePrevious:true unconditionally
            // throws away their customisation on every load.
            object registryIds;
            bool hadPrevious = _cmdMgr.GetGroupDataFromRegistry(MainCmdGroupId, out registryIds);
            var knownIds = new[] { CmdExportUserId };
            bool ignorePrevious = hadPrevious && !SameIds(registryIds as int[], knownIds);

            var group = _cmdMgr.CreateCommandGroup2(
                MainCmdGroupId, AddInTitle, AddInDescription, "", -1, ignorePrevious, ref errors);
            if (group == null) { Log($"CreateCommandGroup2 failed ({errors})"); return; }

            ApplyIcons(group);

            group.AddCommandItem2(
                "Export STEP+", -1,
                "Export STEP preserving the full appearance hierarchy",
                "Export STEP+", 0,
                nameof(CommandCallbacks.ExportStep),
                nameof(CommandCallbacks.EnableExportStep),
                CmdExportUserId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

            group.HasToolbar = true;
            group.HasMenu = true;
            group.Activate();

            // Show for parts and assemblies, in the modelling document types.
            //
            // The tab persists across sessions, so an unconditional
            // AddCommandTabBox()+AddCommands() appends ANOTHER copy of the
            // button on every SolidWorks launch. Remove any existing tab of
            // ours first and rebuild it from scratch -- this is the pattern
            // SolidWorks' own samples use, because there is no reliable way to
            // ask a tab box whether it already holds a given command.
            foreach (var docType in new[] { swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY })
            {
                foreach (var title in new[] { AddInTitle }.Concat(RetiredTitles))
                {
                    var stale = _cmdMgr.GetCommandTab((int)docType, title);
                    if (stale != null) _cmdMgr.RemoveCommandTab(stale);
                }

                var tab = _cmdMgr.AddCommandTab((int)docType, AddInTitle);
                if (tab == null) { Log($"AddCommandTab failed for docType {docType}"); continue; }

                var box = tab.AddCommandTabBox();
                if (box == null) { Log($"AddCommandTabBox failed for docType {docType}"); continue; }

                bool added = box.AddCommands(
                    new[] { group.get_CommandID(CmdExportUserId) },
                    new[] { (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow });
                if (!added) Log($"AddCommands failed for docType {docType}");
            }
        }

        /// <summary>Icon sizes SolidWorks asks for, smallest first.</summary>
        private static readonly int[] IconSizes = { 20, 32, 40, 64, 96, 128 };

        /// <summary>
        /// Point the command group at the PNG icon set shipped beside the DLL.
        ///
        /// SolidWorks takes ABSOLUTE paths and reads the files lazily, so a
        /// missing file produces no error, no icon and no clue why. Every path
        /// is checked here instead, and a missing set is logged and skipped so
        /// the button still appears with SolidWorks' default artwork rather
        /// than the add-in failing to load.
        ///
        /// IconList is the strip of command icons -- one square per command,
        /// side by side -- and MainIconList is the group's own icon. With a
        /// single command both are plain squares.
        /// </summary>
        private void ApplyIcons(ICommandGroup group)
        {
            try
            {
                string dir = Path.Combine(
                    Path.GetDirectoryName(typeof(AddIn).Assembly.Location) ?? ".", "icons");

                var commands = IconSizes.Select(s => Path.Combine(dir, $"NextStep_{s}.png")).ToArray();
                var main = IconSizes.Select(s => Path.Combine(dir, $"NextStepMain_{s}.png")).ToArray();

                var missing = commands.Concat(main).Where(p => !File.Exists(p)).ToList();
                if (missing.Count > 0)
                {
                    Log($"icons not found ({missing.Count} missing, e.g. {missing[0]}); "
                      + "using SolidWorks defaults");
                    return;
                }

                group.IconList = commands;
                group.MainIconList = main;
            }
            catch (Exception ex) { Log("ApplyIcons: " + ex.Message); }
        }

        private static bool SameIds(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ── Logging ─────────────────────────────────────────────────────────
        public static void Log(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(AddIn).Assembly.Location) ?? ".";
                // Fully qualified: SolidWorks.Interop.sldworks also defines
                // an Environment type, so the bare name is ambiguous here.
                File.AppendAllText(Path.Combine(dir, "nextstep-debug.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message
                    + System.Environment.NewLine);
            }
            catch { /* logging must never break the add-in */ }
        }
    }

    /// <summary>
    /// SolidWorks dispatches ribbon callbacks late-bound by method name. AddIn
    /// itself is ClassInterface(None) so only ISwAddin is dispatchable; this
    /// separate AutoDispatch object receives the callbacks.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class CommandCallbacks
    {
        public AddIn Owner { get; set; }

        public void ExportStep() => ExportCommand.Run(AddIn.SwApp);

        /// <summary>1 = enabled, 0 = greyed out.</summary>
        public int EnableExportStep()
        {
            var doc = AddIn.SwApp?.ActiveDoc as IModelDoc2;
            if (doc == null) return 0;
            int t = doc.GetType();
            return (t == (int)swDocumentTypes_e.swDocPART
                 || t == (int)swDocumentTypes_e.swDocASSEMBLY) ? 1 : 0;
        }
    }
}
