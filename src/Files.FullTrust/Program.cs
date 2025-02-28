using Files.Shared;
using Files.Shared.Extensions;
using Files.FullTrust.MessageHandlers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Files.FullTrust
{
    [SupportedOSPlatform("Windows10.0.10240")]
    internal class Program
    {
        public static ILogger Logger { get; private set; }
        private static readonly LogWriter logWriter = new LogWriter();
        private static readonly JsonElement defaultJson = JsonSerializer.SerializeToElement("{}");

        [STAThread]
        private static async Task Main()
        {
            Logger = new Logger(logWriter);
            await logWriter.InitializeAsync("debug_fulltrust.log");
            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionTrapper;

            if (HandleCommandLineArgs())
            {
                // Handles OpenShellCommandInExplorer
                return;
            }

            try
            {
                // Create message handlers
                messageHandlers = new List<IMessageHandler>
                {
                    new RecycleBinHandler(),
                    new LibrariesHandler(),
                    new NetworkDrivesHandler(),
                    new RecentItemsHandler(),
                };

                // Connect to app service and wait until the connection gets closed
                appServiceExit = new ManualResetEvent(false);
                InitializeAppServiceConnection();

                // Initialize message handlers
                messageHandlers.ForEach(mh => mh.Initialize(connection));

                // Initialize device watcher
                deviceWatcher = new DeviceWatcher(connection);
                deviceWatcher.Start();

                // Wait until the connection gets closed
                appServiceExit.WaitOne();
            }
            finally
            {
                messageHandlers.ForEach(mh => mh.Dispose());
                deviceWatcher?.Dispose();
                connection?.Dispose();
                appServiceExit?.Dispose();
                appServiceExit = null;
            }
        }

        private static void UnhandledExceptionTrapper(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            Logger.UnhandledError(exception, exception.Message);
        }

        private static NamedPipeClientStream connection;
        private static ManualResetEvent appServiceExit;
        private static DeviceWatcher deviceWatcher;
        private static List<IMessageHandler> messageHandlers;

        private static async void InitializeAppServiceConnection()
        {
            connection = new NamedPipeClientStream(".",
                $"LOCAL\\FilesInteropService_ServerPipe",
                PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                await connection.ConnectAsync(cts.Token);
                connection.ReadMode = PipeTransmissionMode.Message;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not initialize pipe!");
            }

            await BeginRead();
            appServiceExit?.Set();
        }

        private static async Task BeginRead()
        {
            try
            {
                using var memoryStream = new MemoryStream();
                var buffer = new byte[connection.InBufferSize];
                while (connection.IsConnected)
                {
                    var readCount = await connection.ReadAsync(buffer);
                    memoryStream.Write(buffer, 0, readCount);
                    if (connection.IsMessageComplete)
                    {
                        var message = Encoding.UTF8.GetString(memoryStream.ToArray()).TrimEnd('\0');
                        OnConnectionRequestReceived(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(message));
                        memoryStream.SetLength(0);
                    }
                }
            }
            catch
            {
            }
        }

        private static async void OnConnectionRequestReceived(Dictionary<string, JsonElement> message)
        {
            // Get a deferral because we use an awaitable API below to respond to the message
            // and we don't want this call to get cancelled while we are waiting.
            if (message == null)
            {
                return;
            }

            if (message.ContainsKey("Arguments"))
            {
                // This replaces launching the fulltrust process with arguments
                // Instead a single instance of the process is running
                // Requests from UWP app are sent via AppService connection
                var arguments = message["Arguments"].GetString();
                Logger.Info($"Argument: {arguments}");

                await SafetyExtensions.IgnoreExceptions(async () =>
                {
                    await Task.Run(() => ParseArgumentsAsync(message, arguments));
                }, Logger);
            }
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static async Task ParseArgumentsAsync(Dictionary<string, JsonElement> message, string arguments)
        {
            switch (arguments)
            {
                case "Terminate":
                    // Exit fulltrust process (UWP is closed or suspended)
                    appServiceExit?.Set();
                    break;

                case "Elevate":
                    // Relaunch fulltrust process as admin
                    if (!IsAdministrator())
                    {
                        try
                        {
                            using (Process elevatedProcess = new Process())
                            {
                                elevatedProcess.StartInfo.Verb = "runas";
                                elevatedProcess.StartInfo.UseShellExecute = true;
                                elevatedProcess.StartInfo.FileName = Environment.ProcessPath;
                                elevatedProcess.StartInfo.Arguments = "elevate";
                                elevatedProcess.Start();
                            }
                            await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", 0 } }, message.Get("RequestID", defaultJson).GetString());
                            appServiceExit?.Set();
                        }
                        catch (Win32Exception)
                        {
                            // If user cancels UAC
                            await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", 1 } }, message.Get("RequestID", defaultJson).GetString());
                        }
                    }
                    else
                    {
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", -1 } }, message.Get("RequestID", defaultJson).GetString());
                    }
                    break;

                default:
                    foreach (var mh in messageHandlers)
                    {
                        await mh.ParseArgumentsAsync(connection, message, arguments);
                    }
                    break;
            }
        }

        private static bool HandleCommandLineArgs()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            var arguments = (string)localSettings.Values["Arguments"];
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                localSettings.Values.Remove("Arguments");

                if (arguments == "TerminateUwp")
                {
                    // Return false and don't exit if PID process is not running
                    // Argument may refer to unrelated session (#9580)
                    return TerminateProcess((int)localSettings.Values["pid"]);
                }
                else if (arguments == "ShellCommand")
                {
                    Win32API.OpenFolderInExistingShellWindow((string)localSettings.Values["ShellCommand"]);

                    return TerminateProcess((int)localSettings.Values["pid"]);
                }
            }

            return false;
        }

        private static bool TerminateProcess(int processId)
        {
            // Kill the process. This is a BRUTAL WAY to kill a process.
            return SafetyExtensions.IgnoreExceptions(() => Process.GetProcessById(processId).Kill());
        }
    }
}
