using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Kudupst.Tests
{
    /// <summary>
    /// Real Outlook files used as test data. They belong to other open-source projects, so they're downloaded (from a
    /// fixed commit, checked by SHA-256) instead of being copied into this repository.
    /// </summary>
    public static class Samples
    {
        private const string LibPst = "https://raw.githubusercontent.com/rjohnsondev/java-libpst/f158a64acf2a0e46ac3bd699bc7a5a8da6c40d26/src/test/resources/";
        private const string Extractor = "https://raw.githubusercontent.com/epfromer/pst-extractor/7d08ef9bc7112c23275fa948306c76bba6e24fba/src/__tests__/testdata/";
        private const string MsgExtractor = "https://raw.githubusercontent.com/TeamMsgExtractor/msg-extractor/bfa86ed42deed2fe6a4fa2bad52a26c9198d8ae9/example-msg-files/";

        public static string Enron => Get(Extractor + "enron.pst", "6152489de3472be6f1f840935d34b6dbf5f62b025dd015929ea9cf66eb5a5b46");
        public static string MountainMan => Get(Extractor + "mtnman1965%40outlook.com.ost", "b884f6313e3507aaa38c1a70fecf2d9e4689923065a3324b61d4ae12887c862d");
        public static string MountainManTree => Get(Extractor + "mtnman%20tree.txt", "2029d3a8a296db8e8344f0cc3c462f96d595568ea08ee3eadc23b785efaa22d4");
        public static string Rickroll => Get(Extractor + "rickroll.jpg", "acef4761a1252a2a7ed2a8aae4ba19f770c4397cb87ef9bea0e6a5c6dae92ebd");
        public static string ExtractorTest => Get(Extractor + "pstextractortest%40outlook.com.ost", "daacd8d441428634e103b7c1e37fe1e5909256cc917551667049b5e84dfcf5f0");
        public static string Example2013 => Get(LibPst + "example-2013.ost", "b18ff390d0c22273029ee580ab6025976af69a4f84e276d1b1c01a988650853d");
        public static string Passworded => Get(LibPst + "passworded.pst", "e1085e210bc823fcfbdd1220da08b51423f3c39301c563a99dde71895f2af217");
        public static string DistList => Get(LibPst + "dist-list.pst", "c86841da106036b5abe5a2141dc7644cbb2bf8b504873515eb35a2efeb8c28ac");
        public static string MultiTo => Get(MsgExtractor + "multi-to.msg", "4448c8eaba596d5064d68fdc51596dbd29d98a884f30f0c63707cc6b7c09035e");
        public static string UnicodeHeader => Get(MsgExtractor + "unicode-header.msg", "fe13d8dfba467cd1a6dbc538bef43fccc8ac3fcdcba46cfda1fc15b72fe18a7f");
        public static string UnicodeMsg => Get(MsgExtractor + "unicode.msg", "3c67dc70854b994e469fc14496d23bf778e718831806ebafc72436c840439f9c");
        public static string StrangeDate => Get(MsgExtractor + "strangeDate.msg", "9676ca02b32c15bf47bcf4295131d807a2729c2d1cddc53c4d40b57aa6c6d32b");

        private static string Folder
        {
            get
            {
                var folder = Environment.GetEnvironmentVariable("KUDUPST_SAMPLES");
                if (string.IsNullOrEmpty(folder))
                    folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "samples-cache");
                Directory.CreateDirectory(folder);
                return Path.GetFullPath(folder);
            }
        }

        private static string Get(string url, string sha256)
        {
            var name = Uri.UnescapeDataString(url.Substring(url.LastIndexOf('/') + 1));
            var path = Path.Combine(Folder, name);
            if (File.Exists(path) && Hash(File.ReadAllBytes(path)) == sha256) return path;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                var data = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                if (Hash(data) != sha256) throw new Exception($"Downloaded {name}, but it isn't the expected file.");
                File.WriteAllBytes(path, data);
            }
            return path;
        }

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
