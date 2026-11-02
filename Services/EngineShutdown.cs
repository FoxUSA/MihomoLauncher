using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // Bringing the engine down without knocking the machine's network out for several seconds.
    //
    // Plain `schtasks /End` costs the user a visible outage, and for two separate reasons:
    //   * the TUN adapter disappears from under Windows, which then has to notice and rebuild
    //     the route table itself;
    //   * we run fake-ip, so every name resolved in the last minute still sits in the DNS
    //     client cache as a 198.18.x.x address that now routes nowhere.
    // Both need fixing separately — the routes only the engine can put back (step 1), the
    // stale answers only a flush can (step 2). Drop either and half the outage stays.
    //
    // THE RULE THIS MUST NOT BREAK: the engine lives exactly as long as the launcher, and
    // while it lives it resolves the machine's DNS and holds the kill-switch — both of which
    // are enforced by its TUN interface. So TUN may only go down as part of actually killing
    // the engine: if the kill fails, TUN goes straight back up (step 4). An engine that is
    // alive with TUN down is the one outcome worse than the outage we're avoiding.
    //
    // Every step is best-effort: no engine, no API, no rights to flush — worst case we end up
    // exactly where the old kill-it-and-hope code was, never worse. Nothing here needs admin.
    public static class EngineShutdown
    {
        // The engine is local; a PATCH it can answer takes milliseconds. These ceilings only
        // exist so a wedged engine can't hold the launcher's exit open.
        private const int TunPatchTimeoutSeconds = 2;
        // `schtasks /End` returns long before the process is actually gone, and 1.2 s twice was
        // often short enough to call a perfectly normal exit a failure — after which step 4 put
        // TUN back up on an engine that was already dying, and the log cried wolf. The extra
        // second is only ever spent when the engine is genuinely slow to go.
        private const int GoneWaitMs = 2000;

        public static void Stop(AppConfig cfg = null)
        {
            MihomoController ctl = null;
            try
            {
                var c = cfg ?? AppConfigStore.Load();
                if (c != null) ctl = new MihomoController(c);
            }
            catch (Exception ex) { Log.Error("shutdown-config", ex); }

            // 1. Orderly teardown while it can still do it. There's no kill-switch hole here:
            //    the engine is on its way out, and the moment it dies everything goes direct
            //    anyway — this only moves that instant a few hundred ms earlier.
            bool tunOff = ctl != null && SetTun(ctl, false);

            // 2. Kill the fake-ip answers now that real DNS works again.
            FlushDns();

            // 3. Down it goes.
            var conf = cfg ?? SafeConfig();
            End();
            bool gone = WaitGone(conf);
            if (!gone) { End(); gone = WaitGone(conf); }

            // 3b. Still there: the scheduler has lost track of this instance and /End is never
            //     going to reach it. Kill the process itself — leaving it alive is what hands
            //     the NEXT session an engine holding the ports and its own TUN, and two engines
            //     at once break traffic in ways no rule explains.
            if (!gone)
            {
                try
                {
                    var c = conf;
                    // Whoever is listening on the control port right now is the engine we were
                    // steering seconds ago — that PID is the evidence the killer needs when the
                    // image path can't be read (which, for an elevated process, is always).
                    string host; int port;
                    int pid = PortGuard.SplitAddr(c?.MihomoApiAddr ?? "127.0.0.1:9090", out host, out port)
                        ? PortGuard.ListenerPid(port) : 0;
                    if (MihomoSetup.ForceStopEngine(c, pid)) gone = true;
                }
                catch (Exception ex) { Log.Error("shutdown-kill-leftover", ex); }
            }

            // 4. It outlived us — restore what step 1 took away, or the machine is left with
            //    a running engine that neither resolves nor blocks anything.
            if (!gone && tunOff)
            {
                if (SetTun(ctl, true))
                    Log.Info("Движок не остановился — TUN возвращён, DNS и kill-switch на месте.");
                // The usual reason the PATCH above fails is that the engine finally died
                // WHILE we were asking — /End is slower than the 1.2 s we waited for it twice.
                // That's the good outcome, not the bad one: ask once more before crying wolf,
                // or the log fills with a scary line after every single clean exit.
                else if (!WaitGone(conf))
                    Log.Info("Движок не остановился и не принимает команды — TUN мог остаться снятым.");
            }

            FlushDns();
        }

        private static void End()
        {
            try { MihomoSetup.EndTask(); }
            catch (Exception ex) { Log.Error("shutdown-endtask", ex); }
        }

        // PATCH the running engine's config to raise/drop TUN. False if it didn't happen
        // (engine already down, API unreachable, wrong secret).
        private static bool SetTun(MihomoController ctl, bool on)
        {
            try
            {
                // Task.Run + Wait, not GetAwaiter().GetResult(): this runs from App.OnExit,
                // where the dispatcher is already on its way out.
                var t = Task.Run(() => ctl.SetTunAsync(on, TunPatchTimeoutSeconds));
                return t.Wait(TimeSpan.FromSeconds(TunPatchTimeoutSeconds + 1)) && t.Result;
            }
            catch (Exception ex) { Log.Error("shutdown-tun-" + (on ? "on" : "off"), ex); return false; }
        }

        private static AppConfig SafeConfig()
        {
            try { return AppConfigStore.Load(); }
            catch (Exception ex) { Log.Error("shutdown-config-reload", ex); return null; }
        }

        // Did the engine actually die? "Dead" means THE PROCESS IS GONE — nothing else.
        //
        // This used to ask the API and accept two answers as death: no reply, or 401. The 401
        // half was wrong, and wrong in the one case that mattered. An engine answers 401 when it
        // is running on an older config.yaml than ours — which is not a stranger's program, it is
        // OUR engine, left from a session whose exit didn't land or from another copy of the
        // launcher on the same machine. Calling it "gone" skipped step 3b entirely, so the thing
        // we most needed to kill was the one thing guaranteed to survive: still holding its TUN
        // with 198.18.0.1 on it and still sitting on the control port.
        //
        // The next launch then met it, could not steer it, started a second engine beside it, and
        // the route table picked a winner between two identical tunnels. When it picked the
        // orphan, the machine ran on that engine's stale rules — i.e. straight out, real IP, while
        // the launcher showed everything connected. Both "Движок не остановился" lines in the
        // 2026-08-16 log are this, and so is the port war that followed (9090 → 9500 → 9501).
        //
        // Asking the process list has no such blind spot: it answers the same whatever secret the
        // engine holds, whether the scheduler still tracks it, and whether it talks at all.
        private static bool WaitGone(AppConfig cfg)
        {
            var exe = (cfg?.MihomoExePath ?? "").Trim();
            if (exe.Length == 0) return true;   // nothing identifiable to wait for
            try
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(GoneWaitMs);
                while (true)
                {
                    if (MihomoSetup.CountEngines(cfg) == 0) return true;
                    if (DateTime.UtcNow >= deadline) return false;
                    System.Threading.Thread.Sleep(150);
                }
            }
            catch (Exception ex) { Log.Error("shutdown-probe", ex); return false; }
        }

        // Drop the Windows DNS client cache, so the fake-ip answers (198.18.x.x) die with the
        // tunnel instead of lingering until their TTL. Same call `ipconfig /flushdns` makes;
        // it can fail without rights on some machines, which is not worth complaining about.
        public static void FlushDns()
        {
            try { DnsFlushResolverCache(); }
            catch (Exception ex) { Log.Error("shutdown-dns-flush", ex); }
        }

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
        private static extern bool DnsFlushResolverCache();
    }
}
