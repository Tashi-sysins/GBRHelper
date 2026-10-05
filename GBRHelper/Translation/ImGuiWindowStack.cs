using Dalamud.Bindings.ImGui;

namespace GBRHelper.Translation;

/// <summary>
/// いまの ImGui の文脈の、Begin されている窓の積み重ね（ImGuiContext.CurrentWindowStack）を読む（WindowScope に渡す）。
/// ImGui を描くスレッドから、描画の最中に使う。文脈が無ければ段数 0。
/// </summary>
public readonly unsafe struct ImGuiWindowStack : WindowScope.IStack
{
    private readonly ImGuiWindowStackData* data;
    private readonly int count;

    private ImGuiWindowStack(ImGuiWindowStackData* data, int count)
    {
        this.data = data;
        this.count = count;
    }

    /// <summary>いまの文脈の積み重ね。</summary>
    public static ImGuiWindowStack Current()
    {
        var ctx = ImGui.GetCurrentContext().Handle;
        return ctx == null
            ? default
            : new ImGuiWindowStack(ctx->CurrentWindowStack.Data, ctx->CurrentWindowStack.Size);
    }

    public int Count => this.data == null ? 0 : this.count;

    public uint IdAt(int index)
    {
        var w = this.data[index].Window;
        return w == null ? 0 : w->ID;
    }
}
