namespace Prumo.Core;

public enum EventKind { Restrained, Restored, RuleApplied, RuleFailed, PowerPlan, Info, Memory }

public sealed record LogEntry(DateTime Time, EventKind Kind, int Pid, string Exe, string Text);

/// <summary>Recent events in memory for the UI, plus a small rotating text file.</summary>
public sealed class ActivityLog
{
    const int MaxInMemory = 2000;
    const long MaxFileBytes = 1 << 20;

    readonly LinkedList<LogEntry> _entries = new();
    readonly object _lock = new();
    readonly string _file = Path.Combine(AppConfig.Dir, "logs", "prumo.log");

    public event Action<LogEntry>? Added;

    public void Add(EventKind kind, int pid, string exe, string text)
    {
        var e = new LogEntry(DateTime.Now, kind, pid, exe, text);
        lock (_lock)
        {
            _entries.AddLast(e);
            if (_entries.Count > MaxInMemory) _entries.RemoveFirst();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var fi = new FileInfo(_file);
                if (fi.Exists && fi.Length > MaxFileBytes) File.Move(_file, _file + ".1", true);
                File.AppendAllText(_file, $"{e.Time:yyyy-MM-dd HH:mm:ss}\t{kind}\t{pid}\t{exe}\t{text}{Environment.NewLine}");
            }
            catch { /* logging must never break the engine */ }
        }
        Added?.Invoke(e);
    }

    public List<LogEntry> Snapshot() { lock (_lock) return _entries.ToList(); }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            try { File.Delete(_file); File.Delete(_file + ".1"); } catch { }
        }
    }
}
