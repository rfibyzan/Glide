using System.Runtime.InteropServices;

namespace Glide.Service;

/// <summary>
/// Minimal SCM host. <c>System.ServiceProcess.ServiceBase</c> is not part of the
/// shared framework and does not survive NativeAOT trimming, so the two calls
/// that matter are imported directly: start dispatcher and report status.
/// </summary>
internal static unsafe class ServiceHost
{
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;

    private static readonly ManualResetEventSlim StopRequested = new(false);
    private static nint _statusHandle;
    private static string _serviceName = "GlideIndex";
    private static Action<CancellationToken> _work = _ => { };

    public static int Run(string serviceName, Action<CancellationToken> work)
    {
        _serviceName = serviceName;
        _work = work;

        fixed (char* name = serviceName)
        {
            var table = stackalloc ServiceTableEntry[2];
            table[0].ServiceName = name;
            table[0].ServiceProc = &ServiceMain;
            table[1].ServiceName = null;
            table[1].ServiceProc = null;

            if (!StartServiceCtrlDispatcher(table))
            {
                int error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine(
                    $"StartServiceCtrlDispatcher failed ({error}). " +
                    "Run this from the service control manager, or use --console.");
                return 1;
            }
        }

        return 0;
    }

    public static void Stop()
    {
        StopRequested.Set();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void ServiceMain(uint argumentCount, char** arguments)
    {
        try
        {
            _statusHandle = RegisterServiceCtrlHandlerEx(_serviceName, &ControlHandler, nint.Zero);

            if (_statusHandle == nint.Zero)
            {
                return;
            }

            ReportStatus(ServiceStartPending, acceptedControls: 0);

            using var source = new CancellationTokenSource();
            StopRequested.Reset();

            var thread = new Thread(() =>
            {
                try
                {
                    _work(source.Token);
                }
                catch (Exception ex)
                {
                    Log.Write($"worker crashed: {ex}");
                    ReportStatus(ServiceStopped, ServiceAcceptStop);
                }
            })
            {
                IsBackground = true,
                Name = "glide-index",
            };

            thread.Start();
            ReportStatus(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown);

            StopRequested.Wait();

            ReportStatus(ServiceStopPending, ServiceAcceptStop);
            source.Cancel();
            thread.Join(TimeSpan.FromSeconds(30));
            ReportStatus(ServiceStopped, ServiceAcceptStop);
        }
        catch (Exception ex)
        {
            // An exception crossing the unmanaged boundary terminates the process
            // without a useful message.
            Log.Write($"service main failed: {ex}");
            ReportStatus(ServiceStopped, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint ControlHandler(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case ServiceControlStop:
            case ServiceControlShutdown:
                ReportStatus(ServiceStopPending, ServiceAcceptStop);
                StopRequested.Set();
                return 0;

            default:
                return 0;
        }
    }

    private static void ReportStatus(uint state, uint acceptedControls)
    {
        if (_statusHandle == nint.Zero)
        {
            return;
        }

        var status = default(ServiceStatus);
        status.ServiceType = ServiceWin32OwnProcess;
        status.CurrentState = state;
        status.ControlsAccepted = acceptedControls;
        status.Win32ExitCode = 0;
        status.CheckPoint = 0;
        status.WaitHint = 10_000;

        SetServiceStatus(_statusHandle, &status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public char* ServiceName;
        public delegate* unmanaged[Stdcall]<uint, char**, void> ServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(ServiceTableEntry* table);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint RegisterServiceCtrlHandlerEx(
        string serviceName,
        delegate* unmanaged[Stdcall]<uint, uint, nint, nint, uint> handler,
        nint context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint handle, ServiceStatus* status);
}
