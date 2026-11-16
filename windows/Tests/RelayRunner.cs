using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Uselim.Tests
{
    // The relay alone on the Mac, for test-relay.sh: the folder, port, limits and test routes from its variables, a host
    // that sits at the desk, the phone's own files from Resources/Web, and zero picture fitting
    public static class RelayRunner
    {
        private sealed class Host : IRelayHost
        {
            private readonly string web;

            public Host()
            {
                web = Environment.GetEnvironmentVariable("USELIM_TEST_WEB")
                      ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Resources", "Web");
            }

            public List<Limit> Limits(out DateTime? at)
            {
                at = null;
                return new List<Limit>();
            }

            public double IdleSeconds(out bool locked)
            {
                locked = false;
                return 0;
            }

            public void OpenLink(string url) => Log.Note("relay test: would open " + url);

            public byte[] WebFile(string name)
            {
                var path = Path.Combine(web, name);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }

            public Fitted FitFile(string path, int longSide) => null;
            public Fitted FitBytes(byte[] data, int longSide) => null;
            public void Saved(string name, string value) { }
        }

        public static int Serve()
        {
            Log.ToConsole = true;
            var settings = RelaySettings.FromTestEnvironment();
            Directory.CreateDirectory(settings.Dir);
            var loop = new Loop("Uselim relay");
            var relay = new Relay(settings, new Host(), loop);
            loop.Post(relay.Start);
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        // one Claude Code event, as Uselim.exe --hook takes it
        public static int Hook()
        {
            string input;
            using (var stdin = Console.OpenStandardInput())
            using (var reader = new StreamReader(stdin)) input = reader.ReadToEnd();
            Waiting.RunHook(input, DateTime.UtcNow);
            return 0;
        }

        public static int Permission()
        {
            var settings = RelaySettings.FromTestEnvironment();
            string input;
            using (var stdin = Console.OpenStandardInput())
            using (var reader = new StreamReader(stdin)) input = reader.ReadToEnd();
            var decision = Uselim.Permission.Run(input, settings.Dir, settings.Port, true);
            if (decision != null) Console.WriteLine(decision);
            return 0;
        }
    }
}
