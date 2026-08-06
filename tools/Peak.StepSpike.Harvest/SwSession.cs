using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.StepSpike.Harvest
{
    /// <summary>
    /// Connect to SolidWorks over COM, open and close documents, and save and
    /// restore the STEP export preferences around everything we touch.
    ///
    /// The preference discipline is copied from
    /// Peak-Release\Export\Converters\StepConverter.cs: these settings are
    /// application-wide, so a spike that changed them and crashed would leave
    /// the user's SolidWorks altered. Every one we touch is restored in a
    /// finally block.
    /// </summary>
    public sealed class SwSession : IDisposable
    {
        private readonly bool _weStartedIt;
        private readonly List<Action> _restores = new List<Action>();

        public ISldWorks App { get; }

        /// <summary>
        /// swStepExportAppearances is 787 in SW2024's swconst and is ABSENT
        /// from SW2022's, which is what we compile against. Using the raw id
        /// keeps one binary working across every installed version; a runtime
        /// round-trip check (see TrySetToggle) reports whether the running
        /// SolidWorks actually honours it rather than assuming.
        /// </summary>
        public const int SwStepExportAppearances = 787;

        public const int SwStepExportAtomicSave = 786;

        private SwSession(ISldWorks app, bool weStartedIt)
        {
            App = app;
            _weStartedIt = weStartedIt;
        }

        public static SwSession Connect(bool visible)
        {
            bool started = false;
            ISldWorks app = null;
            try
            {
                app = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                Log.Info("attached to a running SolidWorks instance");
            }
            catch (COMException)
            {
                var t = Type.GetTypeFromProgID("SldWorks.Application");
                if (t == null)
                    throw new InvalidOperationException(
                        "SldWorks.Application is not registered on this machine");
                app = (ISldWorks)Activator.CreateInstance(t);
                started = true;
                Log.Info("started a new SolidWorks instance");
            }

            app.Visible = visible;
            app.UserControl = false;
            Log.Info($"SolidWorks revision: {app.RevisionNumber()}");
            return new SwSession(app, started);
        }

        // -- documents ----------------------------------------------------
        public IModelDoc2 Open(string path, out string error)
        {
            error = null;
            int type;
            string ext = Path.GetExtension(path).ToUpperInvariant();
            switch (ext)
            {
                case ".SLDPRT": type = (int)swDocumentTypes_e.swDocPART; break;
                case ".SLDASM": type = (int)swDocumentTypes_e.swDocASSEMBLY; break;
                case ".SLDDRW": type = (int)swDocumentTypes_e.swDocDRAWING; break;
                default:
                    error = $"unrecognised extension {ext}";
                    return null;
            }

            int errors = 0, warnings = 0;
            var model = (IModelDoc2)App.OpenDoc6(
                path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "", ref errors, ref warnings);

            if (model == null)
            {
                error = $"OpenDoc6 returned null (errors={errors}, warnings={warnings})";
                return null;
            }
            if (errors != 0 || warnings != 0)
                Log.Info($"  open: errors={errors} warnings={warnings}");
            return model;
        }

        public void Close(IModelDoc2 model)
        {
            if (model == null) return;
            try { App.CloseDoc(model.GetTitle()); }
            catch (Exception ex) { Log.Info($"  close failed: {ex.Message}"); }
        }

        // -- preferences --------------------------------------------------
        /// <summary>Set an integer preference, remembering how to put it back.</summary>
        public void SetInt(swUserPreferenceIntegerValue_e pref, int value)
        {
            int saved = App.GetUserPreferenceIntegerValue((int)pref);
            _restores.Add(() =>
            {
                try { App.SetUserPreferenceIntegerValue((int)pref, saved); }
                catch (Exception ex) { Log.Info($"restore {pref} failed: {ex.Message}"); }
            });
            App.SetUserPreferenceIntegerValue((int)pref, value);
        }

        /// <summary>
        /// Set a toggle by raw id and verify the running SolidWorks honoured
        /// it. Returns false when the preference does not exist in this
        /// version -- which is a finding, not an error.
        /// </summary>
        public bool TrySetToggle(int prefId, bool value, string name)
        {
            bool saved;
            try { saved = App.GetUserPreferenceToggle(prefId); }
            catch (Exception ex)
            {
                Log.Info($"  {name}: unreadable on this version ({ex.Message})");
                return false;
            }

            _restores.Add(() =>
            {
                try { App.SetUserPreferenceToggle(prefId, saved); }
                catch (Exception ex) { Log.Info($"restore {name} failed: {ex.Message}"); }
            });

            App.SetUserPreferenceToggle(prefId, value);
            bool got = App.GetUserPreferenceToggle(prefId);
            if (got != value)
            {
                Log.Info($"  {name}: set to {value} but reads back {got}");
                return false;
            }
            return true;
        }

        public bool TrySetToggle(swUserPreferenceToggle_e pref, bool value)
            => TrySetToggle((int)pref, value, pref.ToString());

        public void Dispose()
        {
            // Restore in reverse order so nested changes unwind correctly.
            for (int i = _restores.Count - 1; i >= 0; i--) _restores[i]();
            _restores.Clear();

            if (_weStartedIt)
            {
                try { App.ExitApp(); Log.Info("closed the SolidWorks instance we started"); }
                catch (Exception ex) { Log.Info($"ExitApp failed: {ex.Message}"); }
            }
        }
    }
}
