using System;
using System.Collections.Generic;
using System.Linq;

namespace AST
{
    internal sealed class SpeedTest
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
    }

    /// <summary>The sites offered for the network icon's Speed test item. The first is used until another is chosen.</summary>
    internal static class SpeedTests
    {
        public static readonly IReadOnlyList<SpeedTest> All = new[]
        {
            new SpeedTest { Id = "ookla", Name = "Speedtest by Ookla", Url = "https://www.speedtest.net/" },
            new SpeedTest { Id = "fast", Name = "Fast.com by Netflix", Url = "https://fast.com/" },
            new SpeedTest { Id = "cloudflare", Name = "Cloudflare Speed Test", Url = "https://speed.cloudflare.com/" },
            new SpeedTest { Id = "mlab", Name = "M-Lab Speed Test", Url = "https://speed.measurementlab.net/" },
        };

        public static SpeedTest Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }
}
