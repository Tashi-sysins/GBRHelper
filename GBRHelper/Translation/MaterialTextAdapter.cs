using System;
using System.Reflection;

namespace GBRHelper.Translation;

/// <summary>Translate before Material UI shortens a label; otherwise the shortened English no longer matches the dictionary.</summary>
public sealed class MaterialTextAdapter : IDisposable
{
    private readonly IDisposable hook;
    private readonly Func<bool> inside;
    private readonly Func<string, string?> translate;
    private bool disposed;

    public MaterialTextAdapter(Type navigation, Func<bool> inside, Func<string, string?> translate,
        Func<MethodInfo, Delegate, IDisposable> makeHook)
    {
        var method = navigation.GetMethod("Truncate", BindingFlags.Public | BindingFlags.Static,
            null, [typeof(string), typeof(float)], null);
        if (method?.ReturnType != typeof(string)) throw new NotSupportedException("RebornMaterial.Truncate の形式が未対応です");
        this.inside = inside;
        this.translate = translate;
        hook = makeHook(method, (Func<Func<string, float, string>, string, float, string>)Truncate);
    }

    private string Truncate(Func<string, float, string> original, string text, float width)
    {
        var value = text;
        try
        {
            if (!disposed && inside()) value = translate(text) ?? text;
        }
        catch { /* Translation failure must not interrupt the original draw. */ }
        return original(value, width); // exactly once, including when the original throws
    }

    public void Dispose()
    {
        disposed = true;
        hook.Dispose();
    }
}
