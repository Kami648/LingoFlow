using System.Runtime.InteropServices;

namespace LingoFlow.Services;

public readonly record struct HotkeyBinding(uint Modifiers, uint Key)
{
    public static HotkeyBinding Default => new(7, 0x4C);
    public bool IsValid => (Modifiers & ~7u) == 0 && (Modifiers & 3) != 0 &&
        (Key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87);
    public static string KeyName(uint key) => key is >= 0x70 and <= 0x87 ? $"F{key - 0x6F}" : ((char)key).ToString();
    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        parts.Add(KeyName(Key)); return string.Join(" + ", parts);
    }
}

public sealed class GlobalHotkey : IDisposable
{
    private const int SlotA = 0x4C46, SlotB = 0x4C47;
    private readonly IntPtr window;
    private readonly Func<IntPtr, int, uint, uint, bool> register;
    private readonly Func<IntPtr, int, bool> unregister;
    private int activeId;
    public HotkeyBinding? Active { get; private set; }
    public GlobalHotkey(IntPtr window, Func<IntPtr, int, uint, uint, bool>? register = null, Func<IntPtr, int, bool>? unregister = null)
    {
        this.window = window; this.register = register ?? RegisterHotKey; this.unregister = unregister ?? UnregisterHotKey;
    }
    public bool TryApply(HotkeyBinding? binding, out string error)
    {
        error = "";
        if (binding is { IsValid: false }) { error = "请选择包含 Ctrl 或 Alt 的组合，按键支持字母、数字和 F1–F24。"; return false; }
        if (binding == Active) return true;
        if (binding == null)
        {
            if (Active != null && !unregister(window, activeId)) { error = "暂时无法释放原快捷键，请重试。"; return false; }
            Active = null; activeId = 0; return true;
        }
        int nextId = activeId == SlotA ? SlotB : SlotA;
        // Register the replacement before releasing the working binding. A conflict
        // must never leave a click-through subtitle without its previous hotkey.
        if (!register(window, nextId, binding.Value.Modifiers | 0x4000, binding.Value.Key))
        { error = $"{binding.Value} 不可用或已被其他软件占用，请选择其他组合。"; return false; }
        if (Active != null && !unregister(window, activeId))
        { unregister(window, nextId); error = "无法释放原快捷键，已保留原组合，请重试。"; return false; }
        activeId = nextId; Active = binding; return true;
    }
    public bool Matches(int id, uint modifiers, uint key) => Active is { } active && activeId == id &&
        (modifiers & 0xF) == active.Modifiers && key == active.Key;
    public void Dispose()
    {
        if (Active != null) unregister(window, activeId);
        Active = null; activeId = 0;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
