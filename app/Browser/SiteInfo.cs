using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nyx.Browser
{
    public sealed class CertInfo
    {
        public string IssuedTo, IssuedBy, Protocol;
        public DateTime NotAfter;
        public bool TrustProblem;
    }

    /// <summary>
    /// Reads a site's TLS certificate with a short separate connection (WebView2 does not expose the certificate of a
    /// valid page). Informational only: the connection that loads the page is the browser's own.
    /// </summary>
    public static class SiteInfo
    {
        private static readonly Dictionary<string, CertInfo> Cache = new Dictionary<string, CertInfo>(StringComparer.OrdinalIgnoreCase);

        public static async Task<CertInfo> GetAsync(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            lock (Cache) { if (Cache.TryGetValue(host, out var c)) return c; }
            try
            {
                using (var tcp = new TcpClient())
                {
                    var connect = tcp.ConnectAsync(host, 443);
                    if (await Task.WhenAny(connect, Task.Delay(5000)) != connect) return null;
                    await connect;

                    bool problem = false;
                    using (var ssl = new SslStream(tcp.GetStream(), false, (s, cert, chain, errors) => { problem = errors != SslPolicyErrors.None; return true; }))
                    {
                        var auth = ssl.AuthenticateAsClientAsync(host);
                        if (await Task.WhenAny(auth, Task.Delay(5000)) != auth) return null;
                        await auth;

                        var cert2 = new X509Certificate2(ssl.RemoteCertificate);
                        var info = new CertInfo
                        {
                            IssuedTo = Org(cert2.Subject) ?? cert2.GetNameInfo(X509NameType.SimpleName, false),
                            IssuedBy = Org(cert2.Issuer) ?? cert2.GetNameInfo(X509NameType.SimpleName, true),
                            NotAfter = cert2.NotAfter,
                            Protocol = ssl.SslProtocol.ToString().Replace("Tls13", "TLS 1.3").Replace("Tls12", "TLS 1.2"),
                            TrustProblem = problem
                        };
                        lock (Cache) Cache[host] = info;
                        return info;
                    }
                }
            }
            catch { return null; }
        }

        private static string Org(string dn)
        {
            var m = Regex.Match(dn ?? "", @"(?:^|,\s*)O=(?:""([^""]+)""|([^,]+))");
            return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim() : null;
        }
    }
}
