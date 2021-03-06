﻿using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using USBHelperLauncher.Configuration;

namespace USBHelperLauncher
{
    class DebugMessage
    {
        private readonly string log, fiddlerLog;

        public DebugMessage(string log, string fiddlerLog)
        {
            this.log = log;
            this.fiddlerLog = fiddlerLog;
        }

        public async Task<string> Build()
        {
            ComputerInfo info = new ComputerInfo();
            StringBuilder sb = new StringBuilder();
            Exception exception = await TryReachProxy();
            DateTime now = DateTime.UtcNow;
            var hosts = Program.Hosts;
            var locale = Program.Locale;
            var av = new Dictionary<string, bool>();
            sb.Append('-', 13).Append(" USBHelperLauncher Debug Information ").Append('-', 13).AppendLine();
            sb.AppendLine("Debug Time: " + now + " (UTC)");
            sb.AppendLine("Session Length: " + (now - Program.SessionStart).ToString(@"hh\:mm\:ss"));
            sb.AppendLine("Session GUID: " + Program.Session.ToString());
            sb.AppendLine("Proxy Available: " + (exception == null ? "Yes" : "No (" + exception.Message + ")"));
            sb.AppendLine("Public Key Override: " + (Program.OverridePublicKey ? "Yes" : "No"));
            sb.AppendLine("Version: " + Program.GetVersion());
            sb.AppendLine("Helper Version: " + Program.HelperVersion);
            sb.AppendLine(".NET Framework Version: " + Get45or451FromRegistry());
            sb.AppendFormat("Operating System: {0} ({1}-bit)", info.OSFullName, Environment.Is64BitOperatingSystem ? 64 : 32).AppendLine();
            sb.AppendLine("Platform: " + info.OSPlatform);
            sb.AppendLine("Used Locale: " + locale.ChosenLocale);
            sb.AppendLine("System Language: " + CultureInfo.CurrentUICulture.Name);
            sb.AppendLine("Total Memory: " + info.TotalPhysicalMemory);
            sb.AppendLine("Available Memory: " + info.AvailablePhysicalMemory);
            TryCatch(() => GetAntiVirus(ref av), e => sb.AppendLine("Antivirus Software: Error (" + e.Message + ")"));
            AppendDictionary(sb, "Antivirus Software", av.ToDictionary(x => x.Key, x => x.Value ? "Enabled" : "Disabled"));
            AppendDictionary(sb, "Hosts", hosts.GetHosts().ToDictionary(x => x, x => hosts.Get(x).ToString()));
            AppendDictionary(sb, "Endpoint Fallbacks", Settings.EndpointFallbacks);
            AppendDictionary(sb, "Key Sites", Settings.TitleKeys);
            AppendDictionary(sb, "Server Certificates", Program.Proxy.CertificateStore.Cast<X509Certificate2>()
                .ToDictionary(x => x.GetNameInfo(X509NameType.SimpleName, false), x => x.Thumbprint), format: "{0} ({1})");
            sb.Append('-', 26).Append(" Log Start ").Append('-', 26).AppendLine();
            sb.Append(log);
            sb.Append('-', 22).Append(" Fiddler Log Start ").Append('-', 22).AppendLine();
            sb.Append(fiddlerLog);
            return sb.ToString();
        }

        private StringBuilder AppendDictionary(StringBuilder sb, string header, Dictionary<string, string> dict, string format = null)
        {
            if (dict.Count() > 0)
            {
                sb.Append(header).AppendLine(":");
                dict.ToList().ForEach(x => sb.AppendFormat(format ?? "{0} -> {1}", x.Key, x.Value).AppendLine());
            }
            return sb;
        }

        public async Task<string> PublishAsync(TimeSpan? timeout = null)
        {
            using (var client = new HttpClient())
            {
                var content = new StringContent(await Build(), Encoding.UTF8, "application/x-www-form-urlencoded");
                var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromMilliseconds(-1));
                var response = await client.PostAsync("https://hastebin.com/documents", content, cancel.Token);
                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                return "https://hastebin.com/" + (string)json["key"];
            }
        }

        public async Task<Exception> TryReachProxy()
        {
            using (var client = new WebClient())
            {
                client.Proxy = Program.Proxy.GetWebProxy();
                string respString;
                try
                {
                    respString = await client.DownloadStringTaskAsync("http://www.wiiuusbhelper.com/session");
                }
                catch (WebException e)
                {
                    return e;
                }
                if (Guid.TryParse(respString, out Guid session) && Program.Session == session)
                {
                    return null;
                }
                return new InvalidOperationException("Invalid response: " + Regex.Replace(string.Concat(respString.Take(40)), @"\s+", " ") + "...");

            }
        }

        private static string CheckFor45DotVersion(int releaseKey)
        {
            if (releaseKey >= 393295)
            {
                return "4.6 or later";
            }
            if ((releaseKey >= 379893))
            {
                return "4.5.2 or later";
            }
            if ((releaseKey >= 378675))
            {
                return "4.5.1 or later";
            }
            if ((releaseKey >= 378389))
            {
                return "4.5 or later";
            }
            // This line should never execute. A non-null release key should mean
            // that 4.5 or later is installed.
            return "No 4.5 or later version detected";
        }

        private static string Get45or451FromRegistry()
        {
            using (RegistryKey ndpKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey("SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\\"))
            {
                if (ndpKey != null && ndpKey.GetValue("Release") != null)
                {
                    return CheckFor45DotVersion((int)ndpKey.GetValue("Release"));
                }
                else
                {
                    return "Version 4.5 or later is not detected.";
                }
            }
        }

        private static void GetAntiVirus(ref Dictionary<string, bool> antivirus)
        {
            var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT * FROM AntiVirusProduct");
            var collection = searcher.Get();
            foreach (ManagementObject obj in collection)
            {
                var name = obj["displayName"].ToString();
                var state = (uint)obj["productState"];
                antivirus.Add(name, (state & 0x1000) != 0);
            }
        }

        private static void TryCatch(Action tryAction, Action<Exception> catchAction)
        {
            try
            {
                tryAction();
            }
            catch (Exception e)
            {
                catchAction(e);
            }
        }
    }
}
