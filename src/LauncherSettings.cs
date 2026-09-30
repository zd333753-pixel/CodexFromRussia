using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

internal sealed class LauncherSettings
{
    public string ServerIp;
    public int ServerPort;
    public string Username;
    public string KeyPath;

    public static LauncherSettings Load()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "connection.ini");
        if (!File.Exists(path)) throw new InvalidOperationException("Скопируйте connection.example.ini в connection.ini рядом с EXE и укажите IP сервера, порт, имя пользователя и путь к SSH-ключу.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
            int separator = trimmed.IndexOf('=');
            if (separator <= 0) throw new InvalidOperationException("Неверная строка в connection.ini.");
            values[trimmed.Substring(0, separator).Trim()] = trimmed.Substring(separator + 1).Trim();
        }
        var result = new LauncherSettings();
        IPAddress address;
        string ip = Required(values, "ServerIp");
        if (!IPAddress.TryParse(ip, out address) || address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || ip == "0.0.0.0" || ip == "203.0.113.10")
            throw new InvalidOperationException("Укажите действительный IPv4 вашего VPS в ServerIp.");
        result.ServerIp = address.ToString();
        if (!Int32.TryParse(Required(values, "ServerPort"), out result.ServerPort) || result.ServerPort < 1 || result.ServerPort > 65535)
            throw new InvalidOperationException("ServerPort должен быть числом от 1 до 65535.");
        result.Username = Required(values, "Username");
        if (!Regex.IsMatch(result.Username, "^[a-z_][a-z0-9_-]{0,31}$")) throw new InvalidOperationException("Неверное имя SSH-пользователя.");
        result.KeyPath = Environment.ExpandEnvironmentVariables(Required(values, "KeyPath"));
        if (result.KeyPath.IndexOfAny(new[] { '\"', '\r', '\n' }) >= 0) throw new InvalidOperationException("Неверный путь SSH-ключа.");
        result.KeyPath = Path.GetFullPath(result.KeyPath);
        if (!File.Exists(result.KeyPath)) throw new FileNotFoundException("SSH-ключ не найден: " + result.KeyPath);
        return result;
    }

    private static string Required(Dictionary<string, string> values, string key)
    {
        string value;
        if (!values.TryGetValue(key, out value) || value.Length == 0) throw new InvalidOperationException("Заполните " + key + " в connection.ini.");
        return value;
    }
}
