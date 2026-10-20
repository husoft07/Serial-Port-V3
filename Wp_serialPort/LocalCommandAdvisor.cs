using System.IO;
using System.Text.Json;

namespace Wp_serialPort;

/// <summary>
/// Lightweight, offline advisor. It ranks existing command templates using
/// device-name matches and explicit operator feedback stored per Windows user.
/// </summary>
internal sealed class LocalCommandAdvisor
{
    private readonly string _storePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Serial Port V3",
        "command-learning.json");
    private readonly Dictionary<string, CommandLearning> _learning = new(StringComparer.OrdinalIgnoreCase);

    public LocalCommandAdvisor()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, CommandLearning>>(File.ReadAllText(_storePath));
            if (stored is null) return;
            foreach (var pair in stored) _learning[pair.Key] = pair.Value;
        }
        catch
        {
            // A damaged or inaccessible learning file must not prevent startup.
        }
    }

    public double Score(string device, string fileName, string command)
    {
        var score = 0d;
        var deviceKey = device.Trim();
        if (deviceKey.Length > 0)
        {
            if (fileName.Contains(deviceKey, StringComparison.OrdinalIgnoreCase)) score += 5;
            if (command.Contains(deviceKey, StringComparison.OrdinalIgnoreCase)) score += 3;
        }

        if (_learning.TryGetValue(MakeKey(deviceKey, command), out var record))
        {
            score += Math.Min(record.Sent, 10) * 0.25;
            score += Math.Min(record.Helpful, 10) * 4;
            score -= Math.Min(record.NotHelpful, 10) * 5;
        }
        return score;
    }

    public void RecordSent(string device, string command)
    {
        var record = GetRecord(device, command);
        record.Sent++;
        Save();
    }

    public void RecordFeedback(string device, string command, bool helpful)
    {
        var record = GetRecord(device, command);
        if (helpful) record.Helpful++;
        else record.NotHelpful++;
        Save();
    }

    public void Reset()
    {
        _learning.Clear();
        Save();
    }

    private CommandLearning GetRecord(string device, string command)
    {
        var key = MakeKey(device.Trim(), command);
        if (!_learning.TryGetValue(key, out var record))
            _learning[key] = record = new CommandLearning();
        return record;
    }

    private static string MakeKey(string device, string command) => $"{device}\n{command.Trim()}";

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var tempPath = _storePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_learning, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, _storePath, overwrite: true);
        }
        catch
        {
            // Suggestions continue to work in memory if persistence is unavailable.
        }
    }

    private sealed class CommandLearning
    {
        public int Sent { get; set; }
        public int Helpful { get; set; }
        public int NotHelpful { get; set; }
    }
}

internal sealed record CommandSuggestion(string Device, string Command, string FileName, int LineNumber, double Score)
{
    public string Display => $"{Command.Trim()}\n{FileName} · satır {LineNumber} · eşleşme puanı {Score:0.##}";
}
