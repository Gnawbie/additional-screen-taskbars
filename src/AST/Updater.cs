using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace AST
{
    internal sealed class AvailableUpdate
    {
        public Version Version;
        public string DownloadUrl;
        /// <summary>The checksum GitHub lists for the file, in the form "sha256:...".</summary>
        public string Digest;
    }

    /// <summary>
    /// Finds new versions on the project's GitHub releases page, and installs them with the same setup file people
    /// download by hand. This only runs when asked to and never on the bars' thread, so a slow network can't freeze them.
    /// </summary>
    internal static class Updater
    {
        // Not the "latest" address, because that one leaves out pre-releases, which is what every release is so far.
        private const string ReleasesUrl = "https://api.github.com/repos/Gnawbie/additional-screen-taskbars/releases?per_page=20";
        private const string SetupFileName = "ASTSetup.exe";
        private const string DigestPrefix = "sha256:";

        [DataContract]
        private sealed class Release
        {
            [DataMember(Name = "tag_name")] public string Tag;
            [DataMember(Name = "assets")] public List<Asset> Assets;
        }

        [DataContract]
        private sealed class Asset
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "browser_download_url")] public string Url;
            [DataMember(Name = "digest")] public string Digest;
        }

        private static WebClient Client()
        {
            var client = new WebClient();
            // GitHub refuses requests that don't name the program making them.
            client.Headers[HttpRequestHeader.UserAgent] = Installer.AppName + "/" + Installer.Version;
            return client;
        }

        /// <summary>The highest version that has a setup file, whether or not it's newer than this one.</summary>
        public static Task<AvailableUpdate> FindLatestAsync() => Task.Run(() =>
        {
            byte[] json;
            using (var client = Client()) json = client.DownloadData(ReleasesUrl);
            List<Release> releases;
            using (var s = new MemoryStream(json))
                releases = (List<Release>)new DataContractJsonSerializer(typeof(List<Release>)).ReadObject(s);

            AvailableUpdate latest = null;
            foreach (var release in releases)
            {
                var setup = release.Assets?.FirstOrDefault(a => a.Name == SetupFileName);
                if (setup == null || !Version.TryParse((release.Tag ?? "").TrimStart('v'), out var version)) continue;
                if (latest == null || version > latest.Version)
                    latest = new AvailableUpdate { Version = version, DownloadUrl = setup.Url, Digest = setup.Digest };
            }
            return latest;
        });

        public static bool IsNewer(AvailableUpdate update) => update != null && update.Version > new Version(Installer.Version);

        /// <summary>Returns the path of the downloaded setup file, once its checksum has been confirmed.</summary>
        public static Task<string> DownloadAsync(AvailableUpdate update) => Task.Run(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "AST update");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, SetupFileName);
            using (var client = Client()) client.DownloadFile(update.DownloadUrl, path);

            if (update.Digest != null && update.Digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string expected = update.Digest.Substring(DigestPrefix.Length);
                if (!string.Equals(Sha256Of(path), expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    throw new InvalidDataException("The download didn't match its checksum, so it wasn't installed.");
                }
            }
            return path;
        });

        private static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        }

        /// <summary>The setup file closes this copy of AST, replaces it and starts the new one.</summary>
        public static void Install(string setupPath) => Process.Start(setupPath, "--update");
    }
}
