using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace BloomPrintAgent
{
    /// A web app (origin) that the user approved to print on this PC.
    public class Pairing
    {
        public string Origin { get; set; }
        public string App { get; set; }
        public string Token { get; set; }
        public string PairedAt { get; set; }
        public string LastUsed { get; set; }
    }

    public class AgentConfig
    {
        public int Port { get; set; }
        public bool RequireApproval { get; set; }
        public bool StartWithWindows { get; set; }
        public bool FirstRun { get; set; }
        public List<Pairing> Pairings { get; set; }

        public AgentConfig()
        {
            Port = 9311;
            RequireApproval = true;
            StartWithWindows = true;
            FirstRun = true;
            Pairings = new List<Pairing>();
        }
    }

    static class Config
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BloomPrintAgent");
        static readonly string FilePath = Path.Combine(Dir, "config.json");
        static readonly object gate = new object();

        public static AgentConfig Current = new AgentConfig();

        public static void Load()
        {
            Directory.CreateDirectory(Dir);
            lock (gate)
            {
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var c = new JavaScriptSerializer().Deserialize<AgentConfig>(File.ReadAllText(FilePath));
                        if (c != null)
                        {
                            if (c.Pairings == null) c.Pairings = new List<Pairing>();
                            if (c.Port <= 0 || c.Port > 65535) c.Port = 9311;
                            Current = c;
                        }
                    }
                }
                catch (Exception e) { Log.Error("Config could not be read, using defaults: " + e.Message); }
            }
        }

        public static void Save()
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(Current));
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);
                }
                catch (Exception e) { Log.Error("Config could not be saved: " + e.Message); }
            }
        }

        public static Pairing FindByToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (gate) return Current.Pairings.FirstOrDefault(p => SafeEquals(p.Token, token));
        }

        public static Pairing AddPairing(string origin, string app)
        {
            var p = new Pairing
            {
                Origin = origin,
                App = string.IsNullOrEmpty(app) ? origin : app,
                Token = NewToken(),
                PairedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                LastUsed = "",
            };
            lock (gate)
            {
                // one token per origin: re-pairing replaces the old one
                Current.Pairings.RemoveAll(x => x.Origin == origin);
                Current.Pairings.Add(p);
            }
            Save();
            return p;
        }

        public static void RemovePairing(string token)
        {
            lock (gate) Current.Pairings.RemoveAll(x => x.Token == token);
            Save();
        }

        public static List<Pairing> PairingsSnapshot()
        {
            lock (gate) return Current.Pairings.ToList();
        }

        static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return "bpa_" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        static bool SafeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
