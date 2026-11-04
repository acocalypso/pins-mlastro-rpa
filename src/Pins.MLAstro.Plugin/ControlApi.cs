using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.MLAstroRPA;

/// <summary>Loopback-only control service, intended to be called by the PINS API relay.</summary>
public sealed class ControlApi : IDisposable
{
    private readonly MLAstroPlugin plugin;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    public ControlApi(MLAstroPlugin plugin, int port = 1891)
    {
        this.plugin = plugin;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        worker = RunAsync();
    }

    private async Task RunAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(shutdown.Token); }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) when (shutdown.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { break; }
            // The plugin serializes mutations. A stop request must not queue behind a blocked connect.
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            // Browser pages must use the same-origin ninaAPI relay, not target loopback directly.
            if (context.Request.Headers["Origin"] != null) throw new ApiException(403, "Use the PINS API relay.");
            var path = context.Request.Url.AbsolutePath;
            object result;
            if (context.Request.HttpMethod == "GET" && path == "/status") result = plugin.GetStatus();
            else if (context.Request.HttpMethod == "GET" && path == "/usb-devices") result = plugin.GetUsbDevices();
            else if (context.Request.HttpMethod == "GET" && path == "/settings") result = plugin.GetCorrectionSettings();
            else if (context.Request.HttpMethod == "GET" && path == "/device-settings") result = plugin.GetDeviceSettings();
            else if (context.Request.HttpMethod == "POST")
            {
                if (context.Request.ContentLength64 < 0 || context.Request.ContentLength64 > 4096)
                    throw new ApiException(413, "A JSON body of at most 4096 bytes is required.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(path == "/connect" ? 45 : 10));
                var bytes = new byte[(int)context.Request.ContentLength64];
                await context.Request.InputStream.ReadExactlyAsync(bytes, deadline.Token);
                using var body = JsonDocument.Parse(bytes);
                var root = body.RootElement;
                switch (path)
                {
                    case "/jog/start": result = new { leaseId = await plugin.StartJogAsync(root.GetProperty("axis").GetString(), root.GetProperty("direction").GetInt32()) }; break;
                    case "/jog/renew": await plugin.RenewJogAsync(root.GetProperty("leaseId").GetString()); result = plugin.GetStatus(); break;
                    case "/jog/stop": await plugin.StopJogAsync(root.GetProperty("leaseId").GetString()); result = plugin.GetStatus(); break;
                    case "/device-settings": result = await plugin.ConfigureDeviceAsync(new(root.GetProperty("speedLevel").GetInt32(), root.GetProperty("backlashEnabled").GetBoolean(), root.GetProperty("azSteps").GetInt32(), root.GetProperty("altSteps").GetInt32()), deadline.Token); break;
                    case "/connect":
                        var transport = root.GetProperty("transport").GetString();
                        if (transport != "wifi" && transport != "usb") throw new ApiException(400, "Transport must be wifi or usb.");
                        result = new { connected = await plugin.ConnectAsync(transport == "wifi", root.GetProperty("address").GetString(), deadline.Token) };
                        break;
                    case "/disconnect": await plugin.DisconnectAsync(); result = plugin.GetStatus(); break;
                    case "/automation": await plugin.SetAutomationAsync(root.GetProperty("enabled").GetBoolean()); result = plugin.GetStatus(); break;
                    case "/stop": await plugin.StopAsync(); result = plugin.GetStatus(); break;
                    case "/move": await plugin.ManualMoveAsync(root.GetProperty("axis").GetString(), root.GetProperty("direction").GetInt32(), root.GetProperty("arcseconds").GetDouble()); result = plugin.GetStatus(); break;
                    case "/discover": result = await plugin.DiscoverTppaAsync(deadline.Token); break;
                    case "/select-controller": await plugin.SelectForTppaAsync(); result = new { selected = true }; break;
                    case "/settings":
                        await plugin.ConfigureCorrectionAsync(root.GetProperty("factor").GetDouble(), root.GetProperty("maxStepArcMin").GetDouble(), root.GetProperty("settleSeconds").GetDouble(), root.GetProperty("pauseBeforeFirstCorrection").GetBoolean());
                        result = plugin.GetCorrectionSettings(); break;
                    default: throw new ApiException(404, "Unknown operation.");
                }
            }
            else throw new ApiException(404, "Unknown operation.");
            await ReplyAsync(response, 200, result);
        }
        catch (ApiException ex) { await TryReplyAsync(response, ex.Status, ex.Message); }
        catch (InvalidOperationException ex) { await TryReplyAsync(response, 409, ex.Message); }
        catch (ArgumentException ex) { await TryReplyAsync(response, 400, ex.Message); }
        catch (JsonException) { await TryReplyAsync(response, 400, "Invalid JSON body."); }
        catch (System.Collections.Generic.KeyNotFoundException) { await TryReplyAsync(response, 400, "Missing required field."); }
        catch (OperationCanceledException) { await TryReplyAsync(response, 504, "Operation timed out or service stopped."); }
        catch (Exception ex) { NINA.Core.Utility.Logger.Error(ex); await TryReplyAsync(response, 500, "Control operation failed; inspect the PINS log."); }
        finally { response.Close(); }
    }

    private static async Task TryReplyAsync(HttpListenerResponse response, int code, string message)
    {
        try { await ReplyAsync(response, code, new { error = message }); }
        catch (IOException) { }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }
    private static async Task ReplyAsync(HttpListenerResponse response, int code, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        response.StatusCode = code;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
    public void Dispose()
    {
        shutdown.Cancel();
        listener.Close();
        worker.GetAwaiter().GetResult();
        shutdown.Dispose();
    }
    private sealed class ApiException(int status, string message) : Exception(message) { public int Status => status; }
}
