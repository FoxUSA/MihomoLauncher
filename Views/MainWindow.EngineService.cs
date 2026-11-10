using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // Moving an install from before the engine service onto it, by itself — «обновление
    // ставится и далее всё само». First silently, through the old firewall task (it runs our
    // exe as SYSTEM, see MihomoSetup.MigrateToServiceAsSystem); if that is not possible or did
    // not happen, through the usual elevated setup — straight to Windows' own prompt, no
    // question of ours before it.
    public partial class MainWindow
    {
        private bool _serviceMoveRunning;

        // How long the firewall task gets. It can be held back exactly like the engine task was
        // (that is the whole reason for the service), so not forever.
        private const int SilentMoveWaitSeconds = 60;

        private async Task MoveEngineToServiceAsync()
        {
            if (_serviceMoveRunning) return;
            try
            {
                // The service may already be there with the engine still running past it: the
                // firewall sync at startup runs the same old task and does the move on its own,
                // before this check ever gets to (03:27:17, 2026-11-09). Hand the engine over then.
                bool pastService = await Task.Run(() =>
                    MihomoSetup.IsServiceInstalled() && !MihomoSetup.ServiceRunning()
                    && MihomoSetup.CountEngines(_config) > 0).ConfigureAwait(true);
                if (pastService)
                {
                    _serviceMoveRunning = true;
                    await HandEngineToServiceAsync().ConfigureAwait(true);
                    return;
                }

                // Only an install that runs through the old tasks. A fresh one (or one with no
                // engine yet) gets the service from its normal setup anyway.
                bool needed = await Task.Run(() =>
                    !MihomoSetup.IsServiceInstalled()
                    && File.Exists(MihomoSetup.ServiceExePath())
                    && File.Exists((_config?.MihomoExePath ?? "").Trim())
                    && MihomoSetup.IsTaskInstalled()).ConfigureAwait(true);
                if (!needed) return;
                _serviceMoveRunning = true;

                if (await Task.Run(() => MihomoSetup.CanMigrateViaFirewallTask()).ConfigureAwait(true))
                {
                    Log.Info("Перевод движка на службу: через задачу брандмауэра, без запроса прав.");
                    await Task.Run(() => MihomoSetup.RunLegacyFirewallTask()).ConfigureAwait(true);
                    for (int i = 0; i < SilentMoveWaitSeconds; i++)
                    {
                        await Task.Delay(1000).ConfigureAwait(true);
                        if (MihomoSetup.IsServiceInstalled())
                        {
                            Log.Info("Перевод движка на службу: готово.");
                            await HandEngineToServiceAsync().ConfigureAwait(true);
                            return;
                        }
                    }
                    Log.Info("Перевод движка на службу: задача брандмауэра не сделала это за "
                             + SilentMoveWaitSeconds + " с — ставлю службу с запросом прав.");
                }

                // Windows' consent prompt has to land in front of someone. Started with the window
                // hidden (autostart at logon) it would only blink in the taskbar — so wait until
                // the window is opened; the old tasks keep the engine running meanwhile.
                while (_hidden) await Task.Delay(5000).ConfigureAwait(true);
                if (_updating || MihomoSetup.IsServiceInstalled()) return;

                // The setup batch restarts the engine (old task down, service up), so put the
                // live routes back right after, as the watchdog does after a restart.
                string err = "";
                bool ok = await Task.Run(() => MihomoSetup.InstallElevated(_config, out err)).ConfigureAwait(true);
                if (!ok || !MihomoSetup.IsServiceInstalled())
                {
                    Log.Info("Перевод движка на службу не удался" + (err.Length > 0 ? ": " + err : ".")
                             + " Работаю через задачи, как раньше.");
                    return;
                }
                if (await EnsureMihomoRunningAsync(CancellationToken.None).ConfigureAwait(true))
                    ReapplyLiveConfig();
                RefreshEngineState();
            }
            catch (Exception ex) { Log.Error("move-to-service", ex); }
            finally { _serviceMoveRunning = false; }
        }

        // The engine running right now was started by the old task, before the move — left as it
        // is, the whole session ran past the service («плитка подключена, служба остановлена»).
        // Restart it through the service at once: one short engine reload, once ever.
        private async Task HandEngineToServiceAsync()
        {
            if (_updating) return;
            Log.Info("Перевод движка на службу: перезапускаю движок через службу.");
            await Task.Run(() => MihomoSetup.StartEngineAlone(_config)).ConfigureAwait(true);
            if (await EnsureMihomoRunningAsync(CancellationToken.None).ConfigureAwait(true))
                ReapplyLiveConfig();
            Log.Info("Перевод движка на службу: " + MihomoSetup.ServiceState() + ".");
            RefreshEngineState();
        }
    }
}
