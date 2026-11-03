using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // api.byfox.dev — the one source of geo and rule templates (since 2026-11-03; before that
    // byfox.dev/data/… and raw.githubusercontent.com, the latter often blocked from RU).
    //
    // The API is append-only: paths, file names, fields and formats never change or go away,
    // new things appear beside them. Freshness comes from the small root manifest.json
    // (rule.date, geo.*.date), checked at most once a day; every downloadable file carries a
    // sha256 in its section's manifest, checked before anything is replaced.
    public static class ByfoxApi
    {
        public const string Base = "https://api.byfox.dev/";
        public const string RuleBase = Base + "rule/";
        public const string GeoBase = Base + "geo/";

        public const string RootManifestUrl = Base + "manifest.json";
        public const string RuleManifestUrl = RuleBase + "manifest.json";
        public const string TemplatesUrl = RuleBase + "templates.json";
        public const string GeoDateUrl = GeoBase + "geolite2_data.txt";
        public const string GeoCityUrl = GeoBase + "GeoLite2-City.mmdb";
        public const string GeoAsnUrl = GeoBase + "GeoLite2-ASN.mmdb";

        // The API counts its consumers by this.
        public static string UserAgent => "MihomoLauncher/" + VersionInfo.AppVersion;

        public static bool IsOurs(string url)
        {
            try { return new Uri(url).Host.EndsWith("byfox.dev", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            h.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
            return h;
        }

        public static async Task<byte[]> GetBytesAsync(string url, int timeoutSeconds, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using (var resp = await Http.GetAsync(url, cts.Token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        public static async Task<string> GetStringAsync(string url, int timeoutSeconds, CancellationToken ct)
            => Encoding.UTF8.GetString(await GetBytesAsync(url, timeoutSeconds, ct).ConfigureAwait(false));

        // ---- JSON: objects → Dictionary<string,object>, arrays → List<object>, numbers → double ----
        // A small reader of our own: these files are free-form maps (domain → ids, path → hash),
        // which DataContractJsonSerializer can't take, and System.Web.Extensions breaks the WPF
        // markup compiler (it then wants System.Web).

        public static object ParseJson(string json)
        {
            var r = new JsonReader(json ?? "");
            var v = r.Value();
            r.Ws();
            if (!r.End) throw new FormatException("JSON: лишние символы на позиции " + r.Pos);
            return v;
        }

        private sealed class JsonReader
        {
            private readonly string _s;
            private int _i;
            public JsonReader(string s) { _s = s; if (_s.Length > 0 && _s[0] == '﻿') _i = 1; }
            public bool End => _i >= _s.Length;
            public int Pos => _i;

            public void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

            private char Peek() { Ws(); if (_i >= _s.Length) throw new FormatException("JSON: неожиданный конец"); return _s[_i]; }

            private void Expect(char c)
            {
                if (Peek() != c) throw new FormatException("JSON: ожидался «" + c + "» на позиции " + _i);
                _i++;
            }

            public object Value()
            {
                var c = Peek();
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't') { Word("true"); return true; }
                if (c == 'f') { Word("false"); return false; }
                if (c == 'n') { Word("null"); return null; }
                return Num();
            }

            private void Word(string w)
            {
                if (string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0) throw new FormatException("JSON: ожидалось «" + w + "»");
                _i += w.Length;
            }

            private Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{');
                if (Peek() == '}') { _i++; return d; }
                while (true)
                {
                    var k = Str();
                    Expect(':');
                    d[k] = Value();
                    var c = Peek(); _i++;
                    if (c == '}') return d;
                    if (c != ',') throw new FormatException("JSON: ожидалась «,» на позиции " + (_i - 1));
                }
            }

            private List<object> Arr()
            {
                var a = new List<object>();
                Expect('[');
                if (Peek() == ']') { _i++; return a; }
                while (true)
                {
                    a.Add(Value());
                    var c = Peek(); _i++;
                    if (c == ']') return a;
                    if (c != ',') throw new FormatException("JSON: ожидалась «,» на позиции " + (_i - 1));
                }
            }

            private string Str()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new FormatException("JSON: незакрытая строка");
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    var e = _s[_i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16)); _i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
            }

            private double Num()
            {
                int start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0) _i++;
                if (_i == start) throw new FormatException("JSON: неожиданный символ на позиции " + _i);
                return double.Parse(_s.Substring(start, _i - start), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public static IDictionary<string, object> Obj(object o, string key)
        {
            var d = o as IDictionary<string, object>;
            return d != null && d.TryGetValue(key, out var v) ? v as IDictionary<string, object> : null;
        }

        public static string Str(object o, string key)
        {
            var d = o as IDictionary<string, object>;
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return "";
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        public static int Int(object o, string key)
        {
            var d = o as IDictionary<string, object>;
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return 0;
            try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; }
        }

        public static bool Bool(object o, string key)
        {
            var d = o as IDictionary<string, object>;
            return d != null && d.TryGetValue(key, out var v) && v is bool b && b;
        }

        public static IEnumerable Arr(object o, string key)
        {
            var d = o as IDictionary<string, object>;
            if (d == null || !d.TryGetValue(key, out var v)) return new object[0];
            return v as IEnumerable ?? new object[0];
        }

        // ---- integrity ----

        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static byte[] Gunzip(byte[] gz)
        {
            using (var src = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                src.CopyTo(dst);
                return dst.ToArray();
            }
        }

        // ---- root manifest ----

        public sealed class RootManifest
        {
            public string RuleDate = "";      // yyyy-MM-dd
            public string GeoliteDate = "";   // yyyyMMdd
        }

        public static async Task<RootManifest> RootManifestAsync(CancellationToken ct)
        {
            var root = ParseJson(await GetStringAsync(RootManifestUrl, 30, ct).ConfigureAwait(false));
            return new RootManifest
            {
                RuleDate = Str(Obj(root, "rule"), "date"),
                GeoliteDate = Str(Obj(Obj(root, "geo"), "geolite2"), "date"),
            };
        }

        // files["<path>"] = {size, sha256} of rule/manifest.json, as path → sha256.
        public static async Task<Dictionary<string, string>> RuleFileHashesAsync(CancellationToken ct)
        {
            var m = ParseJson(await GetStringAsync(RuleManifestUrl, 30, ct).ConfigureAwait(false));
            return FileHashes(m);
        }

        public static Dictionary<string, string> FileHashes(object manifest)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var files = Obj(manifest, "files");
            if (files == null) return map;
            foreach (var kv in files) map[kv.Key] = Str(kv.Value, "sha256").ToLowerInvariant();
            return map;
        }
    }
}
