namespace Transcribe.Gui.Models;

public sealed record DeviceItem(int Index, string Name)
{
    public override string ToString() => Index < 0 ? Name : $"[{Index}] {Name}";
}

