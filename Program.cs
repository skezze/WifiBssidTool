
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

internal class Program
{
    private const string TargetSsid = "WLAN-SR";

    private const int WatchIntervalMs = 1000;
    private const int ScanCooldownMs = 15000;
    private const int ScanWaitMs = 4000;
    private const int ReconnectCooldownMs = 10000;

    private static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "wifi-config.json");

    private static DateTime lastScanRequest = DateTime.MinValue;
    private static DateTime scanReadyAt = DateTime.MinValue;
    private static DateTime lastReconnect = DateTime.MinValue;

    private static string targetBssid = "";

    private record AccessPoint(
        string Ssid, string Bssid, int Signal, string Channel);

    private sealed class AppConfig
    {
        public string Ssid { get; set; } = TargetSsid;
        public string Bssid { get; set; } = "";
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.Title = "Wi-Fi BSSID Watcher";

        IntPtr client = IntPtr.Zero;

        try
        {
            Console.WriteLine("Wi-Fi BSSID Watcher");
            Console.WriteLine($"Сеть: {TargetSsid}");
            Console.WriteLine();

            uint negotiatedVersion;
            uint openResult = WlanOpenHandle(
                2, IntPtr.Zero, out negotiatedVersion, out client);

            if (openResult != 0)
            {
                Console.WriteLine(
                    $"Не удалось открыть Windows Wi-Fi API: {openResult}");
                return;
            }

            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    AppConfig? config =
                        JsonSerializer.Deserialize<AppConfig>(json);

                    if (config != null &&
                        config.Ssid.Equals(
                            TargetSsid, StringComparison.OrdinalIgnoreCase) &&
                        IsValidMac(config.Bssid))
                    {
                        targetBssid = NormalizeMac(config.Bssid);
                        Console.WriteLine(
                            $"Загружен BSSID из конфига: {targetBssid}");
                    }
                }
                catch
                {
                    Console.WriteLine(
                        "Не удалось прочитать конфиг. Будет выполнен первоначальный выбор.");
                }
            }

            if (string.IsNullOrWhiteSpace(targetBssid))
            {
                Console.WriteLine(
                    "Первый запуск: ищу точки сети и выбираю лучшую по сигналу.");

                while (string.IsNullOrWhiteSpace(targetBssid))
                {
                    RequestWifiScan(client);
                    Thread.Sleep(ScanWaitMs);

                    List<AccessPoint> points = ScanAccessPoints()
                        .Where(ap => ap.Ssid.Equals(
                            TargetSsid, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(ap => ap.Bssid,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.OrderByDescending(x => x.Signal).First())
                        .OrderByDescending(ap => ap.Signal)
                        .ToList();

                    PrintAccessPoints(points);

                    AccessPoint? best = points
                        .Where(ap => IsValidMac(ap.Bssid))
                        .OrderByDescending(ap => ap.Signal)
                        .FirstOrDefault();

                    if (best == null)
                    {
                        Console.WriteLine(
                            "Точки WLAN-SR пока не обнаружены. Повтор через 15 секунд.");
                        Thread.Sleep(ScanCooldownMs);
                        continue;
                    }

                    targetBssid = NormalizeMac(best.Bssid);

                    var config = new AppConfig
                    {
                        Ssid = TargetSsid,
                        Bssid = targetBssid
                    };

                    try
                    {
                        File.WriteAllText(
                            ConfigPath,
                            JsonSerializer.Serialize(config,
                                new JsonSerializerOptions { WriteIndented = true }),
                            new UTF8Encoding(false));

                        Console.WriteLine();
                        Console.WriteLine(
                            $"Выбрана точка: {targetBssid} (сигнал {best.Signal}%).");
                        Console.WriteLine($"Конфиг сохранён: {ConfigPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"Не удалось сохранить конфиг: {ex.Message}");
                        Console.WriteLine(
                            "Программа продолжит работу, но выбор не сохранён.");
                    }

                    Thread.Sleep(1000);
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Закреплённая цель: {targetBssid}");
            Console.WriteLine("Проверка подключения — каждую секунду.");
            Console.WriteLine("Сканирование выполняется только при другой точке.");
            Console.WriteLine("Для выхода нажми Ctrl+C.");
            Thread.Sleep(1500);

            while (true)
            {
                ReadCurrentConnection(
                    out string currentSsid,
                    out string currentBssid);

                bool connectedToTarget =
                    currentSsid.Equals(
                        TargetSsid, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeMac(currentBssid) == targetBssid;

                Console.Clear();
                Console.WriteLine("Wi-Fi BSSID Watcher");
                Console.WriteLine($"Время: {DateTime.Now:HH:mm:ss}");
                Console.WriteLine($"SSID: {currentSsid}");
                Console.WriteLine($"Текущий BSSID: {currentBssid}");
                Console.WriteLine($"Целевой BSSID: {targetBssid}");
                Console.WriteLine();

                if (connectedToTarget)
                {
                    Console.WriteLine("СТАТУС: OK — подключён к выбранной точке.");
                    lastScanRequest = DateTime.MinValue;
                    scanReadyAt = DateTime.MinValue;
                }
                else
                {
                    Console.WriteLine("СТАТУС: подключён не к выбранной точке.");
                    DateTime now = DateTime.Now;

                    if (lastScanRequest == DateTime.MinValue ||
                        (now - lastScanRequest).TotalMilliseconds >= ScanCooldownMs)
                    {
                        uint scanResult = RequestWifiScan(client);
                        lastScanRequest = now;
                        scanReadyAt = now.AddMilliseconds(ScanWaitMs);

                        Console.WriteLine(scanResult == 0
                            ? "Запрошено обновление списка Wi-Fi точек."
                            : $"Ошибка сканирования: {scanResult}");
                    }

                    if (scanReadyAt != DateTime.MinValue && now >= scanReadyAt)
                    {
                        List<AccessPoint> points = ScanAccessPoints()
                            .Where(ap => ap.Ssid.Equals(
                                TargetSsid, StringComparison.OrdinalIgnoreCase))
                            .GroupBy(ap => ap.Bssid,
                                StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.First())
                            .OrderByDescending(ap => ap.Signal)
                            .ToList();

                        PrintAccessPoints(points);

                        bool targetFound = points.Any(ap =>
                            NormalizeMac(ap.Bssid) == targetBssid);

                        if (targetFound)
                        {
                            if ((DateTime.Now - lastReconnect).TotalMilliseconds
                                >= ReconnectCooldownMs)
                            {
                                Console.WriteLine(
                                    "Целевая точка обнаружена. Запрашиваю подключение.");

                                uint result = ConnectToBssid(
                                    client, TargetSsid, targetBssid);

                                lastReconnect = DateTime.Now;

                                Console.WriteLine(result == 0
                                    ? "Запрос подключения передан Windows."
                                    : $"Ошибка запроса подключения: {result}");
                            }
                            else
                            {
                                Console.WriteLine(
                                    "Ожидаю перед следующей попыткой подключения.");
                            }
                        }
                        else
                        {
                            Console.WriteLine(
                                "Сохранённый BSSID пока не обнаружен.");
                        }

                        scanReadyAt = DateTime.MinValue;
                    }
                    else if (scanReadyAt != DateTime.MinValue)
                    {
                        Console.WriteLine(
                            "Ожидаю обновления списка точек доступа.");
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"Конфиг: {ConfigPath}");
                Thread.Sleep(WatchIntervalMs);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
            Console.WriteLine("Нажми любую клавишу для выхода.");
            Console.ReadKey();
        }
        finally
        {
            if (client != IntPtr.Zero)
                WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    private static void PrintAccessPoints(List<AccessPoint> points)
    {
        Console.WriteLine();
        Console.WriteLine($"Обнаруженные точки «{TargetSsid}»:");

        if (points.Count == 0)
        {
            Console.WriteLine("  Точки не обнаружены.");
            return;
        }

        foreach (AccessPoint ap in points)
        {
            string marker = NormalizeMac(ap.Bssid) == targetBssid
                ? " <-- СОХРАНЁННАЯ ЦЕЛЬ"
                : "";

            Console.WriteLine(
                $"  {ap.Bssid} | сигнал {ap.Signal}% | канал {ap.Channel}{marker}");
        }
    }

    private static uint RequestWifiScan(IntPtr client)
    {
        uint result = WlanEnumInterfaces(
            client, IntPtr.Zero, out IntPtr listPtr);

        if (result != 0)
            return result;

        try
        {
            uint count = (uint)Marshal.ReadInt32(listPtr);
            if (count == 0)
                return 1168;

            IntPtr entryPtr = IntPtr.Add(listPtr, 8);
            WLAN_INTERFACE_INFO info =
                Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(entryPtr);

            return WlanScan(
                client, ref info.InterfaceGuid,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            WlanFreeMemory(listPtr);
        }
    }

    private static List<AccessPoint> ScanAccessPoints()
    {
        var list = new List<AccessPoint>();
        string output = RunNetsh("wlan show networks mode=bssid");

        string ssid = "";
        string bssid = "";
        int signal = -1;
        string channel = "?";

        void Save()
        {
            if (!string.IsNullOrWhiteSpace(ssid) &&
                IsValidMac(bssid))
            {
                list.Add(new AccessPoint(ssid, NormalizeMac(bssid),
                    Math.Max(0, signal), channel));
            }
        }

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();

            Match ssidMatch = Regex.Match(
                line, @"^SSID\s+\d+\s*:\s*(.*)$",
                RegexOptions.IgnoreCase);

            if (ssidMatch.Success)
            {
                Save();
                ssid = ssidMatch.Groups[1].Value.Trim();
                bssid = "";
                signal = -1;
                channel = "?";
                continue;
            }

            Match bssidMatch = Regex.Match(
                line, @"^BSSID\s+\d+\s*:\s*([0-9a-f:-]+)",
                RegexOptions.IgnoreCase);

            if (bssidMatch.Success)
            {
                Save();
                bssid = bssidMatch.Groups[1].Value.Trim();
                signal = -1;
                channel = "?";
                continue;
            }

            Match signalMatch = Regex.Match(
                line,
                @"^(?:Signal|Signalstärke|Signalst[aä]rke)\s*:\s*(\d+)\s*%?",
                RegexOptions.IgnoreCase);

            if (signalMatch.Success &&
                int.TryParse(signalMatch.Groups[1].Value, out int parsedSignal))
            {
                signal = parsedSignal;
            }

            Match channelMatch = Regex.Match(
                line, @"^(?:Channel|Kanal|Canal)\s*:\s*(.*)$",
                RegexOptions.IgnoreCase);

            if (channelMatch.Success)
                channel = channelMatch.Groups[1].Value.Trim();
        }

        Save();
        return list;
    }

    private static void ReadCurrentConnection(
        out string ssid, out string bssid)
    {
        string output = RunNetsh("wlan show interfaces");
        ssid = "";
        bssid = "";

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();

            Match ssidMatch = Regex.Match(
                line, @"^SSID\s*:\s*(.*)$",
                RegexOptions.IgnoreCase);

            if (ssidMatch.Success)
                ssid = ssidMatch.Groups[1].Value.Trim();

            Match bssidMatch = Regex.Match(
                line,
                @"^(?:AP\s+BSSID|BSSID(?:\s+des\s+Zugriffspunkt(?:s|es)?)?)\s*:\s*([0-9a-f:-]+)\s*$",
                RegexOptions.IgnoreCase);

            if (bssidMatch.Success)
                bssid = NormalizeMac(bssidMatch.Groups[1].Value.Trim());
        }
    }

    private static string RunNetsh(string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return "";
        }
    }

    private static uint ConnectToBssid(
        IntPtr client, string profileName, string bssid)
    {
        uint result = WlanEnumInterfaces(
            client, IntPtr.Zero, out IntPtr listPtr);

        if (result != 0)
            return result;

        try
        {
            uint count = (uint)Marshal.ReadInt32(listPtr);
            if (count == 0)
                return 1168;

            IntPtr entryPtr = IntPtr.Add(listPtr, 8);
            WLAN_INTERFACE_INFO info =
                Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(entryPtr);

            byte[] mac = ParseMac(bssid);
            if (mac.Length != 6)
                return 87;

            IntPtr bssidList = Marshal.AllocHGlobal(18);

            try
            {
                Marshal.WriteInt32(bssidList, 0, 12);
                Marshal.WriteInt32(bssidList, 4, 1);
                Marshal.WriteInt32(bssidList, 8, 1);

                for (int i = 0; i < 6; i++)
                    Marshal.WriteByte(bssidList, 12 + i, mac[i]);

                IntPtr profilePtr =
                    Marshal.StringToHGlobalUni(profileName);

                try
                {
                    var parameters = new WLAN_CONNECTION_PARAMETERS
                    {
                        ConnectionMode = 0,
                        Profile = profilePtr,
                        Dot11Ssid = IntPtr.Zero,
                        DesiredBssidList = bssidList,
                        BssType = 1,
                        Flags = 0
                    };

                    return WlanConnect(
                        client, ref info.InterfaceGuid,
                        ref parameters, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(profilePtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(bssidList);
            }
        }
        finally
        {
            WlanFreeMemory(listPtr);
        }
    }

    private static bool IsValidMac(string value) =>
        ParseMac(value).Length == 6;

    private static string NormalizeMac(string value)
    {
        byte[] bytes = ParseMac(value);
        return bytes.Length == 6
            ? string.Join(":", bytes.Select(b => b.ToString("x2")))
            : "";
    }

    private static byte[] ParseMac(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<byte>();

        string[] parts = value.Trim().Split(
            new[] { ':', '-' },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 6)
            return Array.Empty<byte>();

        try
        {
            return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;

        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public int ConnectionMode;
        public IntPtr Profile;
        public IntPtr Dot11Ssid;
        public IntPtr DesiredBssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint clientVersion, IntPtr reserved,
        out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(
        IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(
        IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(
        IntPtr clientHandle, ref Guid interfaceGuid,
        IntPtr dot11Ssid, IntPtr ieData, IntPtr reserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanConnect(
        IntPtr clientHandle, ref Guid interfaceGuid,
        ref WLAN_CONNECTION_PARAMETERS connectionParameters,
        IntPtr reserved);
}
