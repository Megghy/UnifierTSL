using System.Diagnostics;
using UnifierTSL.FileSystem;

namespace UnifierTSL.Surface.Adapter.Cli.Sessions {
    internal sealed class SurfaceClientProcessLauncher(string pipeName) : IDisposable {
        private readonly Lock stateLock = new();
        private readonly string pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException(GetString("A pipe name must be provided."), nameof(pipeName))
            : pipeName;

        private bool disposed;
        private Process? clientProcess;

        public string PipeName => pipeName;

        public void RestartClient() {
            ThrowIfDisposed();
            StopClient(killIfRunning: true);

            var process = new Process {
                StartInfo = CreateStartInfo(),
                EnableRaisingEvents = true
            };
            process.Start();

            lock (stateLock) {
                clientProcess = process;
            }
        }

        private ProcessStartInfo CreateStartInfo() {
            var clientExePath = Path.Combine("app", $"UnifierTSL.ConsoleClient{FileSystemHelper.GetExecutableExtension()}");
            if (!OperatingSystem.IsLinux()) {
                return new ProcessStartInfo {
                    FileName = clientExePath,
                    Arguments = pipeName,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
            }

            var startInfo = new ProcessStartInfo {
                FileName = "/usr/bin/screen",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-D");
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(clientExePath);
            startInfo.ArgumentList.Add(pipeName);
            return startInfo;
        }

        public void StopClient(bool killIfRunning) {
            Process? process;
            lock (stateLock) {
                process = clientProcess;
                clientProcess = null;
            }

            if (process is null) {
                return;
            }

            try {
                if (killIfRunning && !process.HasExited) {
                    process.Kill();
                }
            }
            catch {
            }
            finally {
                try {
                    process.Dispose();
                }
                catch {
                }
            }
        }

        public void Dispose() {
            lock (stateLock) {
                if (disposed) {
                    return;
                }

                disposed = true;
            }

            StopClient(killIfRunning: true);
        }

        private void ThrowIfDisposed() {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }
}
