#region Using directives
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.System;
#endregion

/// <summary>
/// Drives the ECharts view in the OvenDataRow: samples the oven state once per second, keeps the actual temperature
/// trace of the running cycle and serves page + JSON snapshot through a loopback HTTP listener that the WebBrowser polls.
/// </summary>
public class EchartOvenDataLogic : BaseNetLogic
{
    private const string LogCategory = "EchartOvenDataLogic";
    private const string RecipeTargetPath = "Model/RecipeTarget/OvenRecipeTarget";
    private const string OvenIdPath = "Model/HMISettings/DeviceId";
    private const string OvenDataPath = "Model/OvenData";
    private const string WebBrowserName = "EchartData";
    private const string RowItemAlias = "RowItem";
    private const int Port = 8765;
    private const int SampleMs = 1000;

    private PeriodicTask task;
    private IUAObject oven;
    private string ovenId;
    private OvenTrace trace;
    private WebBrowser browser;
    private IUAVariable theme;

    public override void Start()
    {
        browser = LogicObject.Owner.Get<WebBrowser>(WebBrowserName);
        if (browser == null)
        {
            Log.Error(LogCategory, $"WebBrowser '{WebBrowserName}' not found under '{LogicObject.Owner.BrowseName}'.");
            return;
        }

        oven = ResolveOven();
        if (oven == null)
        {
            Log.Error(LogCategory, "Unable to resolve the oven data node.");
            return;
        }

        ovenId = oven.BrowseName;
        trace = new OvenTrace();
        if (!EchartServer.Register(ovenId, trace))
        {
            trace = null;
            return;
        }

        theme = FindThemeVariable();
        if (theme != null) theme.VariableChange += OnThemeChanged;
        ApplyUrl();
        Sample();
        task = new PeriodicTask(Sample, SampleMs, LogicObject);
        task.Start();
    }

    public override void Stop()
    {
        if (theme != null) theme.VariableChange -= OnThemeChanged;
        theme = null;
        task?.Dispose();
        task = null;
        if (trace != null) EchartServer.Unregister(ovenId, trace);
        trace = null;
    }

    // The UI session object (owner chain of this row) holds the Light/Dark switch used by the style sheet.
    private IUAVariable FindThemeVariable()
    {
        for (var node = LogicObject.Owner; node != null; node = node.Owner)
            if (node.GetVariable("LightDarkTheme") is IUAVariable variable) return variable;
        return null;
    }

    private void OnThemeChanged(object sender, VariableChangeEventArgs e) => ApplyUrl();

    private void ApplyUrl()
    {
        var dark = theme?.Value.Value is bool value && value;
        browser.URL = $"http://127.0.0.1:{Port}/index.html?oven={Uri.EscapeDataString(ovenId)}&theme={(dark ? "dark" : "light")}";
    }

    private IUAObject ResolveOven()
    {
        if (LogicObject.GetAlias(RowItemAlias) is IUAObject item) return item;

        var deviceId = Convert.ToString(Project.Current.GetVariable(OvenIdPath)?.Value.Value);
        return string.IsNullOrEmpty(deviceId) ? null : Project.Current.Get(OvenDataPath)?.Get(deviceId) as IUAObject;
    }

    private void Sample()
    {
        var target = Project.Current.Get<OvenRecipe>(RecipeTargetPath);
        if (target == null || trace == null) return;

        var recipe = new[] { ("PreHeat", target.PreHeat), ("Bake", target.Bake), ("Cooldown", target.Cooldown) };
        var phases = new List<PhaseInfo>();
        double start = 0;
        foreach (var (name, p) in recipe)
        {
            double duration = Convert.ToDouble(p.Duration);
            phases.Add(new PhaseInfo(name, start, start + duration, Convert.ToDouble(p.TemperatureSetpoint)));
            start += duration;
        }

        var state = (int)ReadDouble("ExecutionState");
        var phase = (int)ReadDouble("CurrentPhase");
        var temperature = ReadDouble("Temperature");
        var running = state == 1 && phase >= 1 && phase <= phases.Count;
        var cycleElapsed = running ? phases[phase - 1].Start + ReadDouble("PhaseElapsedSeconds") : 0.0;

        lock (trace)
        {
            trace.Snapshot = JsonSerializer.SerializeToUtf8Bytes(new
            {
                deviceId = ovenId,
                state = running ? 1 : 0,
                phase = running ? phase : 0,
                temperature,
                setpoint = running ? phases[phase - 1].Setpoint : 0.0,
                cycleElapsed,
                total = start,
                phases = phases.ConvertAll(p => new { name = p.Name, start = p.Start, end = p.End, setpoint = p.Setpoint }),
                actual = OvenCycleTrace.Get(ovenId)
            });
        }
    }

    private double ReadDouble(string variableName)
    {
        var value = oven.GetVariable(variableName)?.Value.Value;
        return value == null ? 0.0 : Convert.ToDouble(value);
    }

    private sealed record PhaseInfo(string Name, double Start, double End, double Setpoint);

    private sealed class OvenTrace
    {
        public byte[] Snapshot = Encoding.UTF8.GetBytes("{}");
    }

    /// <summary>Shared loopback HTTP server: one listener for all rows, started with the first one and stopped with the last.</summary>
    private static class EchartServer
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, OvenTrace> Traces = new();
        private static readonly Dictionary<string, string> Files = new()
        {
            ["/index.html"] = "text/html; charset=utf-8",
            ["/echarts.min.js"] = "application/javascript; charset=utf-8"
        };
        private static HttpListener listener;

        public static bool Register(string id, OvenTrace trace)
        {
            lock (Sync)
            {
                if (listener == null && !StartListener()) return false;
                Traces[id] = trace;
                return true;
            }
        }

        public static void Unregister(string id, OvenTrace trace)
        {
            lock (Sync)
            {
                if (Traces.TryGetValue(id, out var current) && ReferenceEquals(current, trace)) Traces.Remove(id);
                if (Traces.Count > 0 || listener == null) return;
                listener.Close();
                listener = null;
            }
        }

        private static bool StartListener()
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{Port}/");
                l.Start();
                listener = l;
                _ = Task.Run(() => Loop(l));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory, $"Unable to start the chart server on port {Port}: {ex.Message}");
                return false;
            }
        }

        private static async Task Loop(HttpListener l)
        {
            while (l.IsListening)
            {
                HttpListenerContext context;
                try { context = await l.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Handle(context));
            }
        }

        private static void Handle(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath;
                byte[] body = null;
                var contentType = "application/json";

                if (path == "/data")
                {
                    var id = context.Request.QueryString["oven"] ?? string.Empty;
                    OvenTrace trace;
                    lock (Sync) Traces.TryGetValue(id, out trace);
                    if (trace != null) lock (trace) body = trace.Snapshot;
                }
                else if (Files.TryGetValue(path, out var type))
                {
                    var file = ResourceUri.FromProjectRelativePath("echart" + path).Uri;
                    if (File.Exists(file))
                    {
                        body = File.ReadAllBytes(file);
                        contentType = type;
                    }
                }

                if (body == null)
                {
                    context.Response.StatusCode = 404;
                }
                else
                {
                    context.Response.ContentType = contentType;
                    context.Response.Headers["Cache-Control"] = "no-store";
                    context.Response.ContentLength64 = body.Length;
                    context.Response.OutputStream.Write(body, 0, body.Length);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogCategory, $"Request failed: {ex.Message}");
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
