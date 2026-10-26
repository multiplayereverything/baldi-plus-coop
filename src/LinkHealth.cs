using System;
using System.Threading;
using UnityEngine;

namespace BaldiTogether
{
    // Link health on top of the transport keepalive (Transport.KeepAliveMs). The socket now stays up while
    // the host's game loop is stalled, so the client measures the host's silence itself: a notice after a few
    // seconds, and only after HostSilentLimit does it treat the host as gone (migration or disconnect).
    // Lab: -bt-lab-stall-host N blocks the host's game loop for N s once a floor is running;
    // -bt-lab-no-keepalive restores the 0.8.x behaviour for the mutation run.
    public sealed partial class TogetherRuntime
    {
        const float HostSilentNotice = 5, HostSilentLimit = 60;
        float lastHostPacketAt, hostQuietMax, labStallAt;
        bool hostSilentTold, linkArgsRead;
        int labStallSeconds, labStalls, linkDrops;

        void NoteLinkEvent(NetworkEvent e)
        {
            if (e.Kind == "disconnected") linkDrops++;
            if (!Hosting && (e.Kind == "packet" || e.Kind == "connected")) lastHostPacketAt = Time.realtimeSinceStartup;
        }

        void UpdateLinkHealth(float now)
        {
            if (!linkArgsRead)
            {
                linkArgsRead = true;
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length && Lab.Enabled; i++)
                {
                    if (args[i] == "-bt-lab-stall-host" && i + 1 < args.Length) int.TryParse(args[i + 1], out labStallSeconds);
                    if (args[i] == "-bt-lab-no-keepalive") Transport.KeepAlive = false;
                }
                labStallSeconds = Mathf.Clamp(labStallSeconds, 0, 120);
            }
            if (labStallSeconds > 0 && labStalls == 0 && Hosting && Running && worldReady && peers.Count > 1)
            {
                if (labStallAt == 0) labStallAt = now + 8;
                else if (now > labStallAt)
                {
                    Log("LAB stalling the host's game loop for " + labStallSeconds + " s");
                    Thread.Sleep(labStallSeconds * 1000);
                    labStalls++;
                    Log("LAB host game loop resumed");
                }
            }
            if (Network == null || Hosting || self < 0 || migrating) { hostSilentTold = false; return; }
            float quiet = now - lastHostPacketAt;
            if (quiet > hostQuietMax) hostQuietMax = quiet;
            if (quiet > HostSilentNotice && !hostSilentTold) { hostSilentTold = true; Tell("The host isn't responding. Waiting for them..."); }
            else if (quiet < 1 && hostSilentTold) { hostSilentTold = false; Tell("The host is back."); }
            if (quiet > HostSilentLimit)
            {
                Log("Host silent for " + quiet.ToString("0") + " s; treating it as gone.");
                if (!Network.Steam && BeginHostMigration()) return;
                Disconnect("The host stopped responding. Return to the menu to rejoin.");
            }
        }

        string LinkDiagnostic()
        {
            return ",\"keepAlive\":" + Transport.KeepAlive.ToString().ToLowerInvariant() + ",\"labStalls\":" + labStalls + ",\"linkDrops\":" + linkDrops
                + ",\"hostQuietMaxMs\":" + (int)(hostQuietMax * 1000) + ",\"hostPacketAgeMs\":" + (Hosting || self < 0 ? -1 : (int)((Time.realtimeSinceStartup - lastHostPacketAt) * 1000)) + ",\"migrationTerm\":" + migrationTerm + ",\"migrating\":" + migrating.ToString().ToLowerInvariant()
                + ",\"queuedBytes\":" + (Network != null ? Network.QueuedBytes : 0);
        }
    }
}
